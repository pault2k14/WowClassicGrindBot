using Core.GOAP;
using Core.Party;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SharedLib;
using SharedLib.Extensions;

using System;
using System.Numerics;
using System.Threading;

namespace Core.Goals;

public sealed class FollowFocusGoal : GoapGoal, IGoapEventListener
{
    public override float Cost => 19f;

    // -----------------------------------------------------------------------
    /// <summary>How many yards short of the leader the assist targets when navigating.
    /// Prevents the assist from running through/past the leader since WoW has no
    /// player-vs-player collision. The bot stops this many yards behind the leader.
    /// Must stay small enough to settle well within <see cref="FollowingMaxYards"/>
    /// but not so small the assist oscillates back and forth through the leader position.
    /// 3y is the practical floor given WoW Classic's character radius.</summary>
    private const float FollowStopShortYards = 3f;

    // Distance thresholds (world yards, direction-agnostic XY distance)
    // -----------------------------------------------------------------------

    /// <summary>Assist posts <see cref="BotStatus.Following"/> when closer than this.
    /// Must comfortably exceed <see cref="FollowStopShortYards"/> (3y) so navigation
    /// can complete and the Idle transition fires cleanly.</summary>
    public const float FollowingMaxYards = 7f;

    /// <summary>Assist transitions to NavigatingToLeader when farther than this.
    /// Set below <see cref="LeaderPauseYards"/> (20y) so the assist corrects course
    /// before the leader's distance gate fires — the leader almost never needs to pause.
    /// Dead-band entry: dist > NavigatingMinYards (14y) triggers navigation from Idle.
    /// Dead-band exit:  dist &lt; NavigatingExitYards (10y) → report Following, keep navigating.
    /// The 4y hysteresis band (10-14y) prevents the oscillation that occurred when exit=entry=14y
    /// (state flipped every 100-500ms, causing 68+ pather restarts per run and lateral drift).
    /// NavigatingExitYards no longer transitions to Idle — it only updates the Following status
    /// so the leader knows the assist is close enough to resume patrol. Navigation stays active
    /// and the assist runs continuously alongside the leader.</summary>
    private const float NavigatingMinYards = 14f;

    /// <summary>Distance at which the assist reports Following status while staying in
    /// NavigatingToLeader. Must be less than <see cref="NavigatingMinYards"/> (14y) to
    /// provide hysteresis, and greater than <see cref="FollowingMaxYards"/> (7y) so the
    /// leader can resume patrol before the assist reaches the minimum following distance.</summary>
    private const float NavigatingExitYards = 10f;

    /// <summary>Leader must be this close before the assist exits CantFollow.
    /// MUST exceed <c>Navigation.POP_DIST</c> (3.6y) — the leader's navigation considers
    /// a waypoint reached at POP_DIST and parks there. If LeaderArrivedYards were below
    /// that threshold the leader would stop at ~3.5y and the assist would never see
    /// dist &lt;= LeaderArrivedYards, causing a permanent deadlock where neither bot moves.
    /// 6y gives comfortable clearance above POP_DIST while still being well inside the
    /// dead-band zone (FollowingMaxYards = 7y).</summary>
    public const float LeaderArrivedYards = 6f;

    /// <summary>Minimum leader movement before the navigation waypoint is refreshed.</summary>
    private const float WaypointUpdateThresholdYards = 3f;

    /// <summary>Leader must pause when assist exceeds this distance.</summary>
    public const float LeaderPauseYards = 20f;

    /// <summary>Leader may resume when assist is within this distance (hysteresis).</summary>
    public const float LeaderResumeYards = 15f;

    // -----------------------------------------------------------------------
    // Timeouts
    // -----------------------------------------------------------------------
    private const double NavigationActiveTimeoutSec = 30.0;
    private const double CantFollowTimeoutSec = 120.0;

    /// <summary>Minimum position change that resets the navigation active timer.
    /// While the assist moves at least this far the timer does not accumulate —
    /// it only counts time spent genuinely stuck with no forward progress.
    /// Prevents the 30s wall from firing while the assist is actively covering
    /// ground but the pather is slow (e.g. elevated terrain, long path).</summary>
    private const float NavigationProgressResetYards = 3f;

    // -----------------------------------------------------------------------
    // Stuck detection
    // -----------------------------------------------------------------------
    private const double StuckCheckIntervalSec = 3.0;
    private const float StuckMinMovementWorld = 1.0f;
    private const double StuckEscapeCooldownSec = 10.0;

    /// <summary>
    /// Minimum world-units of movement within <see cref="ActiveStuckThresholdSec"/>
    /// to consider the assist as still making progress while in
    /// <see cref="NavState.NavigatingToLeader"/>. The bot at running speed covers
    /// 7y/s, so 1y over 2.5 seconds is a strict "barely moving" threshold —
    /// reached only when the bot is genuinely caught on terrain.
    /// </summary>
    private const float ActiveStuckMinMovementWorld = 1.0f;

    /// <summary>
    /// Minimum cumulative displacement from the anchor captured AT the moment
    /// of Stuck before clearing the Stuck status. ASYMMETRIC with the trigger
    /// threshold (1y) — recovery must demonstrate real escape from the
    /// obstacle, not just slow incremental drift. Without this, a bot
    /// grinding sideways along a wall at 0.5y/s clears Stuck every ~2 s
    /// (drift exceeds 1y) without ever escaping; the leader sees a flicker
    /// of Stuck and immediately reverts before any recovery action can
    /// take effect. Observed in assist log 01:50:54–01:51:09: four
    /// Stuck→Resume cycles in 10 s while the bot was visibly pinned against
    /// terrain making zero chase progress (sinceBest=140 s).
    /// </summary>
    private const float ActiveStuckResumeMinMovementWorld = 3.0f;

    /// <summary>
    /// Time window over which active-stuck detection requires
    /// <see cref="ActiveStuckMinMovementWorld"/> of movement. Chosen at 2.5s so
    /// the assist reports <see cref="BotStatus.Stuck"/> *before* Navigation.cs's
    /// chase watchdog (4s) attempts unstuck — giving the leader a chance to
    /// pause via <see cref="AssistStateStore.ShouldLeaderPauseForAssist"/>
    /// while the unstuck attempt happens.
    /// </summary>
    private const double ActiveStuckThresholdSec = 2.5;

    /// <summary>
    /// Threshold for the chase-watchdog escalation to CantFollow. When
    /// <see cref="GoalsComponent.Navigation.ChaseSinceBestSec"/> exceeds this
    /// value while the assist is in NavigatingToLeader, FFG concludes that
    /// the assist is wedged on geometry that can't be navigated around (the
    /// chase watchdog's own unstuck attempts at 4 s and route-refill at 6 s
    /// have failed to make progress) and escalates to CantFollow so the
    /// leader navigates back to retrieve the assist. Distinct from
    /// raw-displacement timers (TickNavActiveTimeout, TickActiveStuckDetection)
    /// which can be reset by sideways drift; sinceBest measures progress
    /// toward target and only resets on a genuine new closest-distance.
    /// </summary>
    private const double ChaseWatchdogCantFollowSec = 15.0;

    /// <summary>
    /// SetSingleWaypoint loop guard thresholds. Detects unreachable navigation
    /// targets (most commonly a target inside the leader's blacklist) by
    /// counting consecutive SetSingleWaypoint calls within
    /// <see cref="StationarySetTimeWindowMs"/> of each other where the bot
    /// moved less than <see cref="StationarySetMovementThresholdYards"/>
    /// between sets. After <see cref="MaxConsecutiveStationarySetsBeforeCantFollow"/>
    /// such cycles, escalates immediately to CantFollow instead of waiting for
    /// the 30 s <see cref="TickNavActiveTimeout"/>.
    ///
    /// log-36 02:52:51:236 → 02:53:21:263 baseline: leader inside blacklist,
    /// every position-chase target also blacklisted, ~2000 wasted Update ticks
    /// over 30 s before escalation. With this guard: ~10 ticks, ~150 ms.
    ///
    /// Threshold rationale:
    ///   10 sets — high enough that single legitimate retries (e.g., one
    ///     re-issue after a failed path) don't trip; low enough to escalate
    ///     promptly compared to TickNavActiveTimeout's 30 s wall.
    ///   100 ms window — observed loop runs at ~15 ms per cycle; legitimate
    ///     path-result roundtrips take ≥250 ms, so a sub-100 ms gap between
    ///     consecutive sets is unmistakably the loop.
    ///   1.0 y movement — below 1 y means the bot truly hasn't progressed;
    ///     legitimate navigation moves much more per cycle.
    /// </summary>
    private const int MaxConsecutiveStationarySetsBeforeCantFollow = 10;
    private const int StationarySetTimeWindowMs = 100;
    private const float StationarySetMovementThresholdYards = 1.0f;

    // -----------------------------------------------------------------------
    // Dependencies
    // -----------------------------------------------------------------------
    private readonly ConfigurableInput input;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly Wait wait;
    private readonly ILogger<FollowFocusGoal> logger;
    private readonly RestHandler restHandler;
    private readonly CastingHandler castingHandler;
    private readonly IMountHandler mountHandler;
    private readonly ChatReader chatReader;
    private readonly Navigation navigation;
    private readonly AssistStatusProvider assistStatusProvider;
    private readonly LeaderConnectionStatus leaderConnection;
    private readonly LeaderNavigationProvider leaderNavProvider;

    // -----------------------------------------------------------------------
    // Nav state machine
    // -----------------------------------------------------------------------
    private enum NavState { Idle, NavigatingToLeader, CantFollow }
    private NavState _navState = NavState.Idle;
    private DateTime _navStateEnteredUtc;

    // -----------------------------------------------------------------------
    // Fix 32 — CantFollow active-escape state machine
    // -----------------------------------------------------------------------
    // Log-52 17:38:41 → end-of-log: assist entered CantFollow at
    // <952.14, 293.63> (0.26 y west of std rect edge X=952.4, inside the
    // 6 y inflated zone where path-finding to the leader fails). The old
    // CantFollow was a pure hold-state — wait for the leader to arrive
    // within LeaderArrivedYards (6 y) or for the 120 s CantFollowTimeoutSec
    // to fire. Both fail in the log-52 geometry: the leader can't get
    // within 6 y because the segment leader→assist crosses BL interior,
    // and 120 s of holding doesn't change the assist's position so the
    // post-timeout retry hits the same loop guard immediately.
    //
    // Active escape: when entering CantFollow, the assist runs an
    // escape sequence to physically move out of the inflated-BL sliver:
    //   1. Projection10 — set a single waypoint 10 y from current
    //      position, directed AWAY from the nearest rect's center.
    //      Navigation drives the assist toward it. 3 s budget per phase.
    //   2. Projection20, Projection30 — same, larger radii. Used if the
    //      previous projection's target was itself blacklisted or if the
    //      assist arrived but is still in the inflated-BL zone.
    //   3. LastSafeAnchor — navigate to navigation.LastSafeAnchorW (the
    //      most recent confirmed-outside-BL position), if it's
    //      currently outside any rect.
    //   4. PhysicalUnstuck — invoke navigation.TryPhysicalUnstuck()
    //      (jump + reverse + jump + jump + reverse, ~1.4 s blocking).
    //      Re-enters Projection10 once after this.
    //   5. Exhausted — held position. Wait for the existing 120 s
    //      CantFollowTimeoutSec retry, or for the leader to arrive
    //      within 6 y. Per user direction: "if they are unable to
    //      reunite, pausing indefinitely is fine."
    //
    // Exit on every tick: if a path from current position to the leader
    // no longer crosses any BL rect, exit CantFollow and start a normal
    // NavigatingToLeader. This is the natural reunite condition — the
    // assist may also exit via the existing leader-within-6 y check or
    // the 120 s timeout, both unchanged.
    private enum CantFollowEscapePhase
    {
        NotStarted,
        Projection10,
        Projection20,
        Projection30,
        LastSafeAnchor,
        PhysicalUnstuck,
        Exhausted
    }
    private CantFollowEscapePhase _escapePhase = CantFollowEscapePhase.NotStarted;
    private DateTime _escapePhaseStartUtc;
    private Vector3 _escapePhaseStartPos;
    private Vector3 _escapeTargetW;
    private bool _escapeUnstuckUsed;

    // -----------------------------------------------------------------------
    // Fix BB (log-83 20:17:59:688 → 20:18:05:005) — path-rejection-cause flag
    //
    // Tracks whether the most recent path-failure event was caused by Fix
    // AC+AE+AI+AL rejection (i.e., the pather returned a path but FFG's
    // inspection refused it as a "complex U-turn into navmesh dead zone").
    // This flag is consumed by ComputeAwayFromRectWaypoint's Path-2 (Fix
    // AB-2) fallback to differentiate two distinct CantFollow root causes:
    //
    //   • Stuck-displacement: bot is physically wedged in terrain near an
    //     unreachable leader. Fix AB-2's original "back away from leader
    //     along the natural arrival reverse" assumption is correct here.
    //   • Path-rejection: bot is at a navmesh-valid position but the pather
    //     keeps returning U-turn paths. The bot is NOT wedged. Backing
    //     away from leader makes the routing problem worse — the bot ends
    //     up further from any clear navmesh, increasing pathLen and the
    //     likelihood of further rejections. In this failure mode the
    //     escape direction should be TOWARD leader (a direct walk that
    //     either reaches leader via physical terrain or stumbles into an
    //     obstacle the chase watchdog catches).
    //
    // Set true at the line where snap.Reject = true (path-rejection branch
    // of Navigation_OnPathResultInspected). Set false in the success branch
    // of the same handler (path accepted) so a transient rejection followed
    // by a successful path doesn't carry the flag forward. Also cleared in
    // OnEnter so a fresh FFG cycle starts with a clean slate.
    //
    // The flag is NOT cleared by ResetEscapeState — it must persist from
    // path-inspection time through to Projection10's call to
    // ComputeAwayFromRectWaypoint (~hundreds of ms later, after the rewind/
    // retry cascade in Navigation_OnPathFailed). ResetEscapeState fires
    // BEFORE Projection10 fires (at EnterCantFollow), so clearing there
    // would lose the signal.
    // -----------------------------------------------------------------------
    private bool _lastPathFailureWasRejection;
    // Per-phase budget. 3 s is enough for navigation to either reach a
    // 10-30 y target on flat terrain or determine that it's stuck and
    // we should escalate. LastSafeAnchor gets a larger budget because
    // it can be at a more-or-less arbitrary distance.
    private const double EscapeProjectionPhaseSec = 3.0;
    private const double EscapeAnchorPhaseSec = 8.0;
    // Considered "arrived" when within this distance of the escape target.
    // Generous because the target is just a direction-hint waypoint, not
    // a meaningful endpoint — once the assist moves close, we re-plan.
    private const float EscapeArrivalYards = 3.5f;
    // Per phase, minimum displacement to count as "made progress". Below
    // this, even if the phase timer hasn't elapsed, we treat the phase
    // as stuck and may consider escalating earlier.
    private const float EscapeMinProgressYards = 1.5f;

    /// <summary>
    /// Fix AO (log-78 02:41:21:328 → end-of-log, leader paused at AssistReturn
    /// destination while assist's escape projection ran east unbounded for 49+
    /// seconds, displacing 177y from leader): position recorded at the moment
    /// of CantFollow entry. Used by the cumulative-displacement exit in
    /// <c>UpdateCantFollow</c> — when the assist has moved further than
    /// <c>MaxEscapeDisplacementYards</c> from this point, the escape has
    /// fulfilled its purpose (relocate the bot to a navigable region) and
    /// the bot exits CantFollow to re-attempt NavigatingToLeader from the
    /// new position. See the comment above the displacement-cap exit in
    /// <c>UpdateCantFollow</c> for the full evidence trail.
    /// </summary>
    private Vector3 _cantFollowEnteredPos;

    /// <summary>
    /// Fix AO: cumulative-displacement cap from the CantFollow entry position.
    /// When exceeded, the bot exits CantFollow back to NavigatingToLeader.
    /// <para>
    /// 20 y covers ~2-3 Projection10 cycles (each yielding ~6-8 y net
    /// displacement). After that much escape, the bot is materially clear
    /// of the original navmesh dead-zone that caused the path rejection
    /// and a fresh navigation attempt to the leader's CURRENT position
    /// is likely to succeed via a different geometric route.
    /// </para>
    /// <para>
    /// If the new path still fails (bot is in an unusually large dead-zone),
    /// CantFollow re-fires from the new position with <c>_cantFollowEnteredPos</c>
    /// reset to the new spot. The escape continues from there, bounded fresh
    /// to 20 y per cycle. This is self-limiting and prevents the
    /// unbounded one-direction escape observed in log-78.
    /// </para>
    /// <para>
    /// Smaller values risk premature exits while the bot is still partially
    /// in the dead-zone — repeated CantFollow flapping. Larger values give
    /// the escape more room but extend the worst-case "wrong direction"
    /// travel. 20 y is a middle-ground per the log-78 evidence; tune via
    /// the warning log line below if future evidence shows mis-calibration.
    /// </para>
    /// </summary>
    private const float MaxEscapeDisplacementYards = 20.0f;

    // Fix 33 (log-53: 1000 "Position-chase: standard target ... within 6.0y
    // of assist's blacklist; projected toward assist to ..." log lines in
    // ~100 s, ~10 Hz steady state and ~60 Hz during CantFollow flap):
    // dedup the position-chase projection warning. Log only when the
    // projected candidate moves more than this threshold from the last
    // logged candidate. Reset on plan transitions (OnEnter) and
    // CantFollow entry/exit so a fresh CantFollow cycle logs the first
    // projection. `default` (Vector3.Zero) means "no projection logged
    // yet in this cycle".
    private Vector3 _lastWarnedProjectionCandidateW;
    private const float ProjectionLogChangeYards = 3.0f;

    // -----------------------------------------------------------------------
    // Leader position tracking
    // -----------------------------------------------------------------------
    /// <summary>Last leader world position we navigated toward (for waypoint update threshold).</summary>
    private Vector3 _lastNavigatedToLeaderWorldPos;

    // -----------------------------------------------------------------------
    // Waypoint-sharing mode
    // -----------------------------------------------------------------------
    /// <summary>
    /// True once the assist has confirmed Following (≤ FollowingMaxYards) while
    /// the leader is Patrolling. Gates the switch from position-chasing to
    /// waypoint-sharing mode — ensures both bots start each patrol leg from
    /// roughly the same position so their pather paths converge.
    /// Cleared when the leader leaves Patrolling (combat, looting, etc.)
    /// or on FFG.OnEnter().
    /// </summary>
    private bool _rendezvousConfirmed;

    /// <summary>
    /// Tracks the leader's status from the previous UpdateIdle tick so we can detect
    /// the non-Patrolling → Patrolling transition that signals FRG has just resumed.
    /// Initialised to <c>null</c> in <see cref="OnEnter"/> so the very first tick
    /// always evaluates the transition correctly.
    /// </summary>
    private BotStatus? _lastLeaderStatus;

    /// <summary>
    /// Tracks whether the leader had a published patrol waypoint on the previous
    /// UpdateIdle tick. A false→true transition (<c>leaderJustPublishedWaypoint</c>)
    /// means the leader just armed its patrol (FRG sync-pause resolved + RefillWaypoints
    /// published the first waypoint). We immediately break the dead-band and start
    /// navigating so both bots begin moving within one API poll cycle (~250ms) of
    /// each other instead of the assist waiting for the 14y dead-band exit.
    /// Initialised to <c>false</c> in <see cref="OnEnter"/> so the very first tick
    /// where the leader has a waypoint always fires the detection.
    /// </summary>
    private bool _lastLeaderHadTargetWaypoint;

    /// <summary>
    /// Tracks whether the leader had a published approach-start anchor on the
    /// previous UpdateIdle tick. A false→true transition fires immediately when
    /// the leader enters ATG/PTG and breaks the dead-band so the assist navigates
    /// to the anchor before the leader has moved far from its starting position.
    /// Initialised to <c>false</c> in <see cref="OnEnter"/>.
    /// </summary>
    private bool _lastLeaderHadApproachStart;

    /// <summary>
    /// Session latch: set true when the approach-start anchor is first detected to
    /// be co-located (within Navigation.POP_DIST of the assist) during the current
    /// approach phase. Once latched, <see cref="GetNavigationTarget"/> commits to
    /// position-chasing for the rest of the phase rather than re-evaluating each
    /// tick. Without this latch, the assist oscillated around the POP_DIST (3.6y)
    /// boundary: anchorDist=3.5y → return chase target (east of bot, near leader)
    /// → bot moves east → anchorDist=3.7y → return anchor (now west of bot) →
    /// SetSingleWaypoint fires (drift ≈ 9y > WaypointUpdateThresholdYards) → bot
    /// turns 180°. Visible in log 17 as alternating ~948ms RightArrow/LeftArrow
    /// presses (~85° turns) at assist 02:37:37–02:37:38. Reset to false when the
    /// approach phase ends (HasApproachStart=false observed) and on FFG.OnEnter.
    /// </summary>
    private bool _approachAnchorColocated;

    /// <summary>
    /// Fix AH (log-73 16:43:37 → 16:44:15): one-shot latch — true once
    /// FollowFocusGoal has pressed the focus chain
    /// (<c>PressTargetFocus</c> + <c>PressTargetOfTarget</c>) during the
    /// current leader-approach phase to hand the assist's hard target over
    /// to the leader's currently-targeted mob. Set to <c>true</c> by the
    /// top-of-<c>Update</c> acquisition block (above the state-machine
    /// switch). Reset to <c>false</c> on every <c>FFG.OnEnter</c> and
    /// alongside <c>_approachAnchorColocated</c> when the leader leaves
    /// the approach phase (<c>HasApproachStart=false</c> observed in
    /// <c>GetNavigationTarget</c>).
    /// <para>
    /// Purpose: satisfies ATG's <c>hastarget=true</c> precondition during
    /// the leader's pre-combat approach window so the planner can select
    /// ATG (cost 8) over FFG (cost 19) on the next tick. Without this,
    /// the planner is stuck in FFG → PositionChase, whose pather cannot
    /// route through terrain the leader's Interact key auto-walks across.
    /// Once ATG owns the goal slot, its own AssistFocus Update branch
    /// re-presses the focus chain every tick, so a single acquisition
    /// here is sufficient.
    /// </para>
    /// <para>
    /// Why one-shot rather than every-tick: focus-chain re-acquisition is
    /// idempotent at the WoW level (pressing TargetFocus + TargetOfTarget
    /// repeatedly just re-selects the same units), but each press incurs
    /// a <c>wait.Update()</c> delay. ATG's Update is the right place for
    /// continuous re-acquisition; FFG only needs to bootstrap.
    /// </para>
    /// </summary>
    private bool _approachTargetAcquired;

    /// <summary>
    /// Fix AJ (log-75 23:53:44:232 → 23:54:00 window): rate-limit timestamp
    /// for Fix AH's focus-chain retry. Holds the UTC time of the most recent
    /// PressTargetFocus + PressTargetOfTarget attempt during the current
    /// approach phase. Used to throttle retries to no more than once per
    /// <c>ApproachTargetAcquireRetryMs</c> while
    /// <c>_approachTargetAcquired</c> is still false.
    /// <para>
    /// Reset to <see cref="DateTime.MinValue"/> on every <c>FFG.OnEnter</c>
    /// and alongside <c>_approachAnchorColocated</c> when the leader leaves
    /// the approach phase. See Fix AJ in the focus-chain block at the top
    /// of <c>Update</c> for the full evidence trail.
    /// </para>
    /// </summary>
    private DateTime _approachTargetLastAttemptUtc = DateTime.MinValue;

    /// <summary>
    /// Fix AJ: minimum wall-clock interval between Fix AH focus-chain
    /// retries. 250 ms gives the WoW client comfortable headroom over typical
    /// classic round-trip latency (30-100 ms) so that on the retry tick,
    /// <c>PressTargetFocus</c>'s effect from the previous attempt has settled
    /// and <c>PressTargetOfTarget</c> can cascade correctly. Higher values
    /// would extend handoff latency without benefit; lower values risk
    /// re-triggering the same race the first attempt failed on.
    /// </summary>
    private const double ApproachTargetAcquireRetryMs = 250.0;

    /// <summary>
    /// Fix AM (log-77 01:47:03:506 → end-of-log, assist at &lt;-315.53, -4320.31&gt;
    /// during leader's second approach to mob; Fix AJ confirmed hostile target
    /// (guid=535132) but the planner never transitioned to ATG and the assist
    /// continued in FFG/PositionChase for the remainder of the log):
    ///
    /// UTC timestamp of the moment Fix AJ successfully latched. Used by the
    /// state-machine code below to detect "latched but ATG never took over"
    /// — i.e., when FFG.Update is still running &gt;1 s after the latch despite
    /// the planner having had ample opportunity to re-evaluate. The exact
    /// stale-latch warning is in the state-machine prelude.
    ///
    /// Reset to <see cref="DateTime.MinValue"/> on every <c>FFG.OnEnter</c>
    /// and at the same place the other approach-phase latches reset (when
    /// <c>HasApproachStart</c> goes false). Diagnostic only — no functional
    /// gating depends on this field.
    /// </summary>
    private DateTime _approachTargetLatchedUtc = DateTime.MinValue;

    /// <summary>
    /// Fix AM: rate-limit the stale-latch warning to once per
    /// <c>StaleLatchWarningCooldownMs</c> so the diagnostic doesn't flood
    /// every FFG.Update tick once tripped.
    /// </summary>
    private DateTime _staleLatchLastWarnUtc = DateTime.MinValue;

    /// <summary>
    /// Fix AM: how long after a successful Fix AJ latch we expect ATG to
    /// have taken over. If FFG.Update is still running past this threshold
    /// with <c>_approachTargetAcquired==true</c> and <c>HasApproachStart==true</c>,
    /// emit a warning. 1000ms is comfortably longer than the
    /// <c>Thread.Sleep(1)</c> GoapAgent loop + worst-case wait.Update() and
    /// covers any number of planner cycles.
    /// </summary>
    private const double StaleLatchWarnAfterMs = 1000.0;

    /// <summary>
    /// Fix AM: how often the stale-latch warning may re-fire while the
    /// condition persists. Keeps the log readable but lets the operator
    /// see the cadence of the failure.
    /// </summary>
    private const double StaleLatchWarningCooldownMs = 2000.0;

    /// <summary>
    /// Fix AN (log-74, log-77 first-approach window): position of the most
    /// recently observed approach-start anchor on the assist side, plus the
    /// time it was last seen. Used to extend the Fix AH+AJ activation
    /// envelope: even if the leader has cleared <c>HasApproachStart</c>
    /// (because its short ATG window finished), the assist may still be
    /// converging on the anchor location. If the assist arrives at the
    /// last-seen anchor within <c>AnchorLocalTtlMs</c> of the leader
    /// publishing it, Fix AH+AJ should still fire — the focus-chain
    /// target acquisition is just as useful one tick after the anchor
    /// expired as it was during the anchor's life.
    ///
    /// Reset on <c>FFG.OnEnter</c>. Captured whenever the assist observes
    /// <c>HasApproachStart=true</c>.
    /// </summary>
    private Vector3 _lastSeenApproachAnchorW;
    private DateTime _lastSeenApproachAnchorUtc = DateTime.MinValue;

    /// <summary>
    /// Fix AN: how long after the leader clears the approach anchor the
    /// assist still treats it as locally active for Fix AH+AJ purposes.
    /// 3000ms covers the worst-case "leader's ATG was only 2s long, assist
    /// needs another ~700ms to converge to the anchor location plus a poll
    /// or two of slack." Larger values risk stale acquisition during long
    /// patrol-then-engage cycles; smaller values reproduce the log-74 and
    /// log-77 first-approach failure mode where the anchor expired before
    /// the assist could converge.
    /// </summary>
    private const double AnchorLocalTtlMs = 3000.0;

    /// <summary>
    /// Discriminates the three possible navigation target sources returned by
    /// <see cref="GetNavigationTarget"/>: the fixed approach-start anchor, the
    /// shared patrol waypoint, or the leader's live body (position-chasing).
    /// Used purely for diagnostic logging on mode transitions — the per-tick
    /// SetSingleWaypoint call in <see cref="UpdateNavigatingToLeader"/> was
    /// previously silent, masking the anchor↔chase flip-flop bug. With mode
    /// tracking, only mode CHANGES log; in-mode drift updates remain silent
    /// to avoid spam during steady-state chasing.
    /// </summary>
    private enum NavTargetMode { Anchor, PositionChase, WaypointSharing, RouteWalk }
    private NavTargetMode _currentNavTargetMode = NavTargetMode.PositionChase;
    private NavTargetMode _lastLoggedNavTargetMode = NavTargetMode.PositionChase;

    /// <summary>Last shared waypoint world position the assist navigated toward.
    /// Used to detect when the leader advances to a new waypoint so navigation
    /// can be refreshed without spamming SetSingleWaypoint every tick.</summary>
    private Vector3 _lastSharedWaypointW;

    // ── Route-walking migration: Turn 2 (Turn 1 → Turn 2 commit) ──
    //
    // _assistRouteIndex tracks the assist's current target waypoint index
    // in navigation.LoadedRoute. The assist walks LoadedRoute[index] →
    // LoadedRoute[index+1] etc, capped at (leader's index − 1) for the
    // trailing-by-one steady state described in the design doc.
    //
    // Sentinel −1 means "unsynced" — the next RouteWalk entry will run
    // FindNearestSafeRouteIndex to pick a starting point near the assist
    // that's at or before the leader's current index. Reset to −1 on
    // FFG OnEnter, FFG OnExit, and on any tick where the active mode is
    // NOT RouteWalk, so that re-entry into RouteWalk after a combat /
    // loot / rest detour always re-syncs from the assist's current
    // position rather than continuing from a stale index.
    //
    // Reset-on-non-RouteWalk has a small cost (recompute nearest index
    // on every RouteWalk entry) but avoids subtle drift bugs where the
    // assist's body moved during PositionChase and the stored index no
    // longer reflects the assist's actual proximity to route waypoints.
    // FindNearestSafeRouteIndex is O(LoadedRoute.Length) ≈ 150 ops —
    // negligible per tick.
    private int _assistRouteIndex = -1;

    // ── Route-walking migration: Turn 3 (Turn 2 → Turn 3 commit) ──
    //
    // _pendingAnchor holds the approach-start anchor when the assist is
    // route-walking and the leader publishes an ApproachStart before the
    // assist has reached its current route waypoint. The assist continues
    // route-walking to the current waypoint, then transitions Route-walk
    // → Anchor mode using the stored anchor coords.
    //
    // Sentinel default(Vector3) = <0,0,0> means "no pending anchor."
    // Legitimate anchor positions in Azeroth are at large negative
    // coordinates, so this sentinel doesn't collide with real values.
    //
    // Lifecycle:
    //   - Set / updated each tick during ApproachStart-while-route-walking.
    //   - Consumed (cleared + transition fired) when the assist reaches
    //     its current route waypoint within Navigation.POP_DIST.
    //   - Cleared without transition when HasApproachStart goes false
    //     before arrival (mob died early, approach canceled, etc.).
    //   - Cleared on FFG OnEnter and OnExit.
    //
    // The motivation: log-85's corridor incident at 01:56:25-30 happened
    // because after combat, the assist's pather had to find a path from
    // an off-route start position back to the leader's body — exactly
    // the failure mode body-chase fixes (AC+AE+AI+AL, AB-2, BB, AO) were
    // designed to catch. With combat-handoff, the assist arrives at
    // combat via the route's curated final-leg geometry, and re-engages
    // the route from a known route waypoint after combat. Both legs
    // become route-adjacent rather than off-route.
    private Vector3 _pendingAnchor = default;

    // ── Route-walking migration: Turn 3.5 (Turn 3 → Turn 3.5 commit) ──
    //
    // _cachedLeaderRouteIdx + _cachedLeaderRouteIdxUtc cache the most
    // recent route index we successfully matched to the leader's
    // published TargetWaypoint. Used as a fallback in
    // TryFindLeaderRouteIndex when the leader's HasTargetWaypoint is
    // false (i.e., the leader has cleared its patrol target — observed
    // during the approach phase when ATG takes over from FRG, and
    // briefly post-combat before FRG re-publishes).
    //
    // Why this is necessary: log-86 analysis showed that Turn 3's
    // CheckShouldDefer fired ZERO times across 57 approach events
    // because the leader's HasTargetWaypoint went false at the exact
    // moment HasApproachStart went true — a structural mutual
    // exclusion in the leader's state machine. Without a cache,
    // TryFindLeaderRouteIndex always fails during approach, defer
    // never engages, and Turn 3 is dead code.
    //
    // Cache update policy:
    //   - Refreshed (value + timestamp) on every successful match via
    //     leader.HasTargetWaypoint + 1.0y tolerance.
    //   - NOT updated when leader.HasTargetWaypoint=true but no match
    //     within tolerance (rescue insertion, transient non-route
    //     waypoint). Preserves the last KNOWN route index.
    //
    // Cache use policy:
    //   - Used as fallback when (a) leader.HasTargetWaypoint=false, OR
    //     (b) HasTargetWaypoint=true but no waypoint matched.
    //   - TTL of CachedLeaderRouteIdxMaxAgeSec; treated as stale beyond
    //     that to avoid using ancient indices after a long combat
    //     break, rescue, etc.
    //
    // Lifecycle:
    //   - Sentinel -1 = "no cached value yet."
    //   - Reset to -1 on FFG OnEnter and OnExit (fresh session).
    private int _cachedLeaderRouteIdx = -1;
    private DateTime _cachedLeaderRouteIdxUtc = DateTime.MinValue;
    private const double CachedLeaderRouteIdxMaxAgeSec = 30.0;

    // ── Route-walking migration: Turn 3.5b (Turn 3.5 → 3.5b commit) ──
    //
    // _cacheFallbackInUse tracks whether the most recent call to
    // TryFindLeaderRouteIndex returned via the cache fallback path
    // (rather than the primary HasTargetWaypoint-match path). Used
    // only for transition logging — flips true/false trigger an Info
    // log, intermediate ticks are silent.
    //
    // Why we need this: log-87 revealed that during the leader's
    // rescue-in-progress windows (104 AssistRequestReturn events!),
    // the leader's TargetWaypointWorldX/Y is published as the
    // rescue-insertion coords, NOT a route waypoint. Primary match
    // fails. Cache fallback fires. Without a transition log, this
    // is invisible — the only symptom is "_assistRouteIndex stuck at
    // the same index for many seconds" which is hard to distinguish
    // from "leader paused at next waypoint" (the legitimate case).
    //
    // The transition log makes the diagnostic visible in the future:
    // log-88 should show, near each rescue event, a "Cache fallback
    // engaged" log followed (when rescue ends) by "Cache primary
    // resumed."
    private bool _cacheFallbackInUse = false;

    // -----------------------------------------------------------------------
    // Navigation active-time timeout
    // -----------------------------------------------------------------------
    private TimeSpan _navActiveElapsed;
    private DateTime _navLastTickUtc;
    private bool _navTimerInit;
    private int _navAttempt;
    private bool _navRewindActive;
    private Vector3 _navRewindAnchorW;
    /// <summary>Last recorded position used to detect forward progress during navigation.
    /// Reset each time the assist moves <see cref="NavigationProgressResetYards"/>, which
    /// resets <see cref="_navActiveElapsed"/> so the timeout only accumulates when truly stuck.</summary>
    private Vector3 _navProgressCheckPosW;

    // -----------------------------------------------------------------------
    // Stuck detection fields
    // -----------------------------------------------------------------------
    private Vector3 _stuckCheckPosW;
    private DateTime _stuckCheckLastUtc = DateTime.MinValue;
    private DateTime _stuckEscapeLastUtc = DateTime.MinValue;

    // -----------------------------------------------------------------------
    // Option B refactor — moved from Navigation.cs:
    //   Fix AB-1 stale-empty-path counter state. Used by
    //   Navigation_OnStaleEmptyPathMatchesActive to count consecutive
    //   stale empty-path results that match the active in-flight request.
    //   The counter is also reset in Navigation_OnPathResultInspected on
    //   accepted paths (any successful, non-rejected path implies the
    //   pather is functional again for the current (start, end)), and on
    //   OnEnter to avoid carrying state across follow sessions.
    // -----------------------------------------------------------------------
    private int _staleEmptySameCount;
    private Vector3 _staleEmptyLastStartW;
    private Vector3 _staleEmptyLastEndW;
    private const int StaleEmptyResultsBeforeOnPathFailed = 3;

    // -----------------------------------------------------------------------
    // Fix AX (Route B Part 1) — direct-route attempts per NavigatingToLeader
    // cycle.
    //
    // When _activeStuckReported is true and a stale-empty matches the active
    // request, Navigation_OnStaleEmptyPathMatchesActive (Fix AU branch)
    // requests Navigation to push a direct one-hop route to the unreachable
    // target instead of escalating to CantFollow immediately. This counter
    // bounds the number of times we'll attempt direct-route within a single
    // NavigatingToLeader cycle — after the limit is hit, the Fix AU branch
    // falls through to EnterCantFollow as the original (pre-Fix-AX) behavior
    // would have done.
    //
    // Rationale for MAX=1: a single attempt either succeeds (bot walks, becomes
    // un-stuck, _activeStuckReported clears, counter resets on next nav cycle)
    // or fails (bot is physically wedged in geometry, direct-route push didn't
    // help). The watchdog cascade (chase progress, no-progress) will fire
    // OnPathFailed within ~15s if the bot doesn't move on the direct route,
    // routing through Navigation_OnPathFailed for rewind/CantFollow. Allowing
    // a second attempt would only repeat the same push at the same position
    // with the same outcome — better to give up and let CantFollow's escape
    // sequence (Projection10/20/30) try a different recovery.
    //
    // Worst-case delay vs pre-Fix-AX behavior: ~10s (the typical interval
    // between stale-empty arrivals in log-81 — PPather queue cycle time).
    // First stale-empty triggers direct-route; if direct-route doesn't help,
    // next stale-empty (~10s later) sees attempts >= max and CantFollow fires.
    // Net effect:
    //   - Success case: ~3s recovery vs ~30s CantFollow + escape (huge win).
    //   - Failure case: ~10s CantFollow vs ~3s (small regression, bounded).
    // -----------------------------------------------------------------------
    private int _directRouteAttemptsInCycle;
    private const int MaxDirectRouteAttemptsPerCycle = 1;

    // -----------------------------------------------------------------------
    // Active-navigation stuck reporting
    //
    // Detects a stationary assist while in NavigatingToLeader and reports
    // BotStatus.Stuck via the API so the leader's FRG.ShouldLeaderPauseForAssist
    // can pause patrol (which already gates on Status==Stuck per
    // AssistStateStore.ShouldLeaderPauseForAssist line 161). Recovery flips
    // back to BotStatus.NavigatingToLeader so the leader naturally resumes.
    //
    // Without this reporting, the leader sees a stuck assist as
    // status=NavigatingToLeader && dist<20y and continues patrolling — the
    // exact symptom in log 21 where the assist was caught on terrain for
    // ~12 seconds while the leader engaged the next mob. See log 21,
    // assist 00:11:03–00:11:15.
    // -----------------------------------------------------------------------
    private DateTime _activeStuckSinceUtc = DateTime.MinValue;
    private Vector3 _activeStuckCheckPosW;
    private bool _activeStuckReported;

    /// <summary>
    /// World-position anchor captured at the moment Stuck was reported.
    /// Used by TickActiveStuckDetection's asymmetric resume check — the
    /// bot must displace at least <see cref="ActiveStuckResumeMinMovementWorld"/>
    /// from this anchor before the Stuck flag clears.
    /// </summary>
    private Vector3 _activeStuckAnchorW;

    // -----------------------------------------------------------------------
    // CantFollow: hold position, wait for leader within LeaderArrivedYards
    // -----------------------------------------------------------------------
    private DateTime _cantFollowEnteredUtc;

    /// <summary>
    /// Fix AK (revised): UTC timestamp of the most recent <see cref="OnExit"/>
    /// call. Used in <see cref="OnEnter"/> when <c>_navState == CantFollow</c>
    /// to measure how long FFG was preempted by another goal. Brief
    /// preemptions (Heal, Buff, Loot, ConsumeCorpse — typically &lt;2.5s)
    /// indicate the underlying CantFollow condition is essentially
    /// unchanged and we should resume CantFollow as before (Fix 31/32
    /// behavior). Long preemptions (Combat — typically &gt;3s) indicate
    /// significant time has passed during which world state may have
    /// changed in ways we can't detect locally (mob respawns/despawns,
    /// blacklist changes, leader's queued actions, etc.), so we reset
    /// to Idle for a fresh evaluation. Initialised to
    /// <see cref="DateTime.MinValue"/> so the very first
    /// <see cref="OnEnter"/> (before any <see cref="OnExit"/>) treats the
    /// preemption duration as effectively zero and preserves CantFollow
    /// if somehow that's the entry state.
    /// </summary>
    private DateTime _lastOnExitUtc = DateTime.MinValue;

    /// <summary>
    /// Fix AK: preemption-duration threshold above which FFG.OnEnter treats
    /// the previous CantFollow state as stale and resets to Idle for
    /// fresh evaluation. Below this threshold, the original Fix 31/32
    /// behavior (resume CantFollow, reset escape phase only) is preserved.
    /// <para>
    /// 2500 ms is calibrated to discriminate Combat-class preemptions
    /// (CombatGoal typically holds the plan for &gt;3s — the shortest
    /// realistic combat duration seen in logs is ~3s for a one-shot)
    /// from brief-cast preemptions (Heal/Buff cast times 1-2s in WoW
    /// Classic, plus ~250ms goal-swap overhead). The threshold is
    /// asymmetric on purpose: false negatives (very brief Combat fights
    /// treated as brief preemptions) cost only the same wrong-direction
    /// projection that Fix AK was designed to prevent; false positives
    /// (long Heal casts treated as significant preemptions) cost only
    /// 2 wasted path-find attempts (~100ms) before re-escalating to
    /// CantFollow. The fix is robust to misclassification in both
    /// directions.
    /// </para>
    /// </summary>
    private const double SignificantPreemptionMs = 2500.0;

    // -----------------------------------------------------------------------
    // SetSingleWaypoint loop guard (Fix 5, log-36)
    // -----------------------------------------------------------------------
    private int _consecutiveStationarySetWaypointCount;
    private DateTime _lastSetWaypointUtc = DateTime.MinValue;
    private Vector3 _lastSetWaypointPlayerPos;

    // -----------------------------------------------------------------------
    // Turn 4a / Fix BE — route-span navigation tracking
    // -----------------------------------------------------------------------
    // Records the last route-span push so duplicate refreshes can be
    // suppressed. Mirrors FRG's _lastRefill* fields at FollowRouteGoal.cs:
    // 74-76 + RefillWaypointsDuplicateCooldownMs at line 77.
    //
    // A push is "duplicate" if all of:
    //   - first route waypoint matches last (within 0.01y — route waypoints
    //     are stable Vector3 values, so 0.01y is essentially equality)
    //   - length matches last push
    //   - elapsed since last push < RouteSpanSuppressionMs
    //   - AND navigation.HasWaypoint() || navigation.HasNext() (content-
    //     aware safety: only suppress when nav has waypoints loaded)
    // See IsDuplicateRecentRouteSpan / RecordRouteSpanPush helpers.
    private Vector3 _lastRouteSpanFirstWp;
    private int _lastRouteSpanLength;
    private DateTime _lastRouteSpanUtc = DateTime.MinValue;
    private const int RouteSpanSuppressionMs = 750;

    // Threshold for "target is near the leader." Above this, the target is
    // likely a rewind anchor, recovery projection, or other off-leader point
    // — single-waypoint behavior is preserved for those cases. Below this,
    // the target is treated as a follow-leader target and route-span navigation
    // applies. Tuned to capture body-chase (typically <10y from leader),
    // refresh/retry targets, and approach anchors (the leader's body when
    // approach started, which may be 5-15y from the current leader position
    // by the time the assist arrives).
    private const float RouteSpanLeaderProximityYards = 25.0f;

    public FollowFocusGoal(
        ConfigurableInput input,
        PlayerReader playerReader,
        AddonBits bits,
        Wait wait,
        ClassConfiguration classConfig,
        ILogger<FollowFocusGoal> logger,
        RestHandler restHandler,
        ChatReader chatReader,
        Navigation navigation,
        AssistStatusProvider assistStatusProvider,
        LeaderConnectionStatus leaderConnection,
        LeaderNavigationProvider leaderNavProvider,
        IOptions<PartyApiConfig> configOptions,
        IMountHandler mountHandler,
        CastingHandler castingHandler)
        : base(nameof(FollowFocusGoal))
    {
        this.input = input;
        this.playerReader = playerReader;
        this.bits = bits;
        this.wait = wait;
        this.logger = logger;
        this.restHandler = restHandler;
        this.chatReader = chatReader;
        this.navigation = navigation;
        this.assistStatusProvider = assistStatusProvider;
        this.leaderConnection = leaderConnection;
        this.leaderNavProvider = leaderNavProvider;
        this.castingHandler = castingHandler;
        this.mountHandler = mountHandler;
        this.Keys = classConfig.FollowFocusActions.Sequence;

        if (classConfig.UnitToFollow == "focus")
            AddPrecondition(GoapKey.hasfocus, true);

        AddPrecondition(GoapKey.assistshouldfollow, true);
        AddPrecondition(GoapKey.shouldloot, false);
        AddPrecondition(GoapKey.shouldgather, false);
        AddPrecondition(GoapKey.consumecorpse, false);

        navigation.OnDestinationReached += Navigation_OnDestinationReached;
        navigation.OnWayPointReached    += Navigation_OnWayPointReached;
        navigation.OnPathFailed         += Navigation_OnPathFailed;

        // Option B refactor: subscribe to Navigation's policy-decision
        // events to host the moved Fix AB-1 / Fix AC+AE / Fix AD policies.
        // FRG does not subscribe to these events so the leader sees no
        // behavior change. See the corresponding Navigation_On* handlers
        // below for the moved evidence trails and rationale.
        navigation.OnPathResultInspected               += Navigation_OnPathResultInspected;
        navigation.OnStaleEmptyPathMatchesActive       += Navigation_OnStaleEmptyPathMatchesActive;
        navigation.OnRepeatedNoPathDirectRouteDecision += Navigation_OnRepeatedNoPathDirectRouteDecision;
    }

    private void Cleanup()
    {
        navigation.OnDestinationReached -= Navigation_OnDestinationReached;
        navigation.OnWayPointReached    -= Navigation_OnWayPointReached;
        navigation.OnPathFailed         -= Navigation_OnPathFailed;

        navigation.OnPathResultInspected               -= Navigation_OnPathResultInspected;
        navigation.OnStaleEmptyPathMatchesActive       -= Navigation_OnStaleEmptyPathMatchesActive;
        navigation.OnRepeatedNoPathDirectRouteDecision -= Navigation_OnRepeatedNoPathDirectRouteDecision;
    }

    // -----------------------------------------------------------------------
    // IGoapEventListener
    // -----------------------------------------------------------------------

    public void OnGoapEvent(GoapEventArgs e)
    {
        // FFG previously cached GoapKey.evadeRecovery state for two purposes:
        //   1. A hold-position gate in Update() that suppressed all
        //      navigation during the evade window.
        //   2. Suppression of stuck-detection inside the navigation timeout,
        //      active-stuck, and idle-stuck reporters.
        // Both are removed in session 27. Fix 4 (FRG.wantNavPaused filtering
        // IsIgnored) lets the leader actually retreat during the evade
        // window, so the original "leader is sitting next to the mob" worry
        // that motivated the hold gate no longer applies — FFG should track
        // the retreating leader normally. And the stuck-detection
        // suppression was protecting the gate's own forced stationary
        // window; with the gate gone, real stalls during evade should be
        // surfaced (BotStatus.Stuck → leader pauses → recovery), not
        // hidden. Nothing in FFG needs to react to the evadeRecovery state
        // any more.
    }

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    public override void OnEnter()
    {
        while (restHandler.IsResting())
            wait.Update(1000);

        // ── Route-walking migration: Turn 1 (log-84 baseline → Turn 1 commit) ──
        //
        // Load the route file into navigation.LoadedRoute at every FFG OnEnter.
        // Turn 1 is purely additive — the data sits in LoadedRoute but no code
        // reads from it yet. Verifies the assist has the same route data the
        // leader has and that Navigation can extract it from its own
        // injected pathSettings (Navigation has it; FFG can't get it via DI
        // directly on the assist, which is why LoadRoute() is parameterless
        // and Navigation owns the read).
        //
        // Re-loading on every OnEnter is wasteful but cheap (array clone of
        // ~100 waypoints); Turn 2 may optimize to load-once semantics if
        // needed.
        //
        // Acceptance signals for Turn 1:
        //   [NAV] [ROUTE-LOAD] log line appears (or warning if route empty)
        //   [FFG] [FIX-CONFIG] line includes RouteWaypointCount=N
        //   N matches the leader's published waypoint count
        //   All other fix-firing rates unchanged from log-84 baseline
        navigation.LoadRoute();

        // ── Route-walking migration: Turn 2 ──
        // Reset waypoint index so the first RouteWalk-engaged tick will
        // resync from the assist's current position. See _assistRouteIndex
        // declaration for rationale on reset semantics.
        _assistRouteIndex = -1;

        // ── Route-walking migration: Turn 3 ──
        // Clear pending anchor in case prior FFG session ended mid-approach
        // without firing the transition (e.g., bot died or operator
        // intervened). Stale pending would otherwise trigger an unwanted
        // transition next time RouteWalk advances to a waypoint.
        _pendingAnchor = default;

        // ── Route-walking migration: Turn 3.5 ──
        // Clear cached leader route index — the next session may have
        // a different starting state, and stale cache could mislead
        // the very first defer attempt.
        _cachedLeaderRouteIdx = -1;
        _cachedLeaderRouteIdxUtc = DateTime.MinValue;

        // ── Route-walking migration: Turn 3.5b ──
        // Reset the cache-fallback-in-use flag so the first transition
        // (primary → cache or cache → primary) in this session logs.
        _cacheFallbackInUse = false;

        // ── Architecture migration observability ──
        //
        // Emit a one-line configuration header at every FFG OnEnter so log
        // analysis can correlate fix-firing rates against the active
        // architecture mode and threshold set. As the route-walking
        // migration progresses, additional config flags will appear here
        // and old ones (anchor-drift behavior, rendezvous-confirmation
        // gating) will flip. The header serves as a per-session ground
        // truth: "what configuration was running when this log was
        // captured?"
        //
        // Pair this header with [FIX-FIRE] tags throughout the codebase
        // for the migration's primary observability primitive:
        //   grep -c "FIX-FIRE\] AC"   → rate of complex-rejection firings
        //   grep -c "FIX-FIRE\] AB-2" → rate of away-from-leader fallbacks
        //   grep -c "FIX-FIRE\] BB"   → rate of toward-leader flips
        //   grep -oE "FIX-FIRE\] [A-Z+0-9-]+" log.txt | sort | uniq -c
        //                              → histogram of all firings
        //
        // Body-chase-specific fixes (AC+AE+AI+AL, AB-2, BB, AO escape
        // ladder, AY direct-route push) are expected to silence as
        // route-walking matures; if they continue firing in route-
        // walking sessions, that's a signal — either the migration
        // missed a case, or the fix is catching a class of failure
        // wider than its original evidence suggested.
        logger.LogInformation(
            $"[FFG] [FIX-CONFIG] OnEnter: ArchitectureMode=RouteWalking (Turn 4a) " +
            $"— route-walk is now the patrol default when LoadedRoute is " +
            $"populated AND the leader's published TargetWaypoint maps to a " +
            $"route index OR a recent cached index is available. Anchor mode " +
            $"used for approach phase, BUT now deferred via _pendingAnchor " +
            $"when route-walking with target not yet reached (combat handoff: " +
            $"assist completes its route leg, then transitions Route-walk → " +
            $"Anchor at the route waypoint). PositionChase used for non-patrol " +
            $"leader states (combat/loot/rest) AND as fallback when route " +
            $"lookup fails. WaypointSharing is now dead code reachable only " +
            $"when LoadedRoute is empty. Turn 3.5: leader route index cache " +
            $"(30s TTL) unblocks CheckShouldDefer when HasTargetWaypoint goes " +
            $"false during approach. Fix BB threshold raised 15y → 25y. " +
            $"Turn 3.5b: parked-RouteWalk co-located targets skip position-" +
            $"chase fallback (eliminated SetWaypoint loop guard escalations). " +
            $"Turn 3.5c (Fix BD): AB-2 projection basis prefers nearest " +
            $"LoadedRoute waypoint over away-from-leader (route waypoints are " +
            $"pre-validated terrain). Turn 3.5d (Fix BC): Fix AA's rear-node " +
            $"drop gated by bot's facing — only drops snap-back artifacts " +
            $"when facing is forward-aligned with path direction; preserves " +
            $"legitimate turnaround arcs when bot is about to reverse direction. " +
            $"Turn 4a (Fix BE): SetWaypointLoopGuarded now attempts a route-span " +
            $"push (SetWayPoints with multi-waypoint span) before falling back " +
            $"to SetSingleWaypoint. When target is near the leader (<25y), " +
            $"a span [route[resumeIndex..leaderIdx], target] is pushed, causing " +
            $"AvgDistance to reflect the real route stride (~10y instead of 3y " +
            $"default), which keeps Navigation.cs:3548's usePather=false for " +
            $"typical route legs. Span construction mirrors FRG.RefillWaypoints: " +
            $"projection-aware advancement (incByDistance + incByProgress + POP " +
            $"loop) computes resumeIndex correctly when bot is between route " +
            $"waypoints; duplicate-suppression uses content-aware safety " +
            $"(navigation.HasWaypoint() || HasNext()) plus FRG-style exact " +
            $"match within RouteSpanSuppressionMs={RouteSpanSuppressionMs}ms. " +
            $"Key thresholds: " +
            $"FollowingMaxYards={NavigatingMinYards:0.0}y, " +
            $"NavigatingExitYards={NavigatingExitYards:0.0}y, " +
            $"WaypointUpdateThresholdYards={WaypointUpdateThresholdYards:0.0}y, " +
            $"MaxEscapeDisplacementYards={MaxEscapeDisplacementYards:0.0}y, " +
            $"ChaseWatchdogCantFollowSec={ChaseWatchdogCantFollowSec:0.0}s, " +
            $"AnchorLocalTtlMs={AnchorLocalTtlMs:0}ms, " +
            $"RouteSpanLeaderProximityYards={RouteSpanLeaderProximityYards:0.0}y. " +
            $"Active fix list: AA, AB-1, AB-2, AC+AE+AI+AL, AD, AG, AH+AJ+AN, " +
            $"AJ, AL, AM, AO, AQ, AT, AU, AV+AW, AX, AY, AZ, BA, BB, BC, BD, BE. " +
            $"RouteWaypointCount={navigation.LoadedRoute.Length}, " +
            $"navHash={navigation.GetHashCode()}.");

        _stuckCheckLastUtc = DateTime.MinValue;
        _activeStuckSinceUtc = DateTime.MinValue;
        _activeStuckReported = false;
        navigation.ResetApproachEscape();
        ResetSetWaypointLoopGuardState();

        // Option B refactor: reset Fix AB-1 stale-empty counter so a
        // previous follow session's count doesn't carry over.
        _staleEmptySameCount = 0;
        _staleEmptyLastStartW = default;
        _staleEmptyLastEndW = default;

        // Fix AX: reset direct-route attempt counter so a previous follow
        // session's exhausted attempts don't immediately escalate to
        // CantFollow on the new session's first stuck event.
        _directRouteAttemptsInCycle = 0;

        // Fix BB: reset the path-rejection-cause flag so a stale flag from
        // a previous FFG session doesn't influence the first CantFollow
        // escape direction in the new session. The flag tracks within-
        // session pather behavior; new session = fresh slate.
        _lastPathFailureWasRejection = false;

        // Always reset rendezvous on goal entry — the assist must re-confirm
        // proximity to the leader before waypoint-sharing mode activates.
        _rendezvousConfirmed = false;
        _lastSharedWaypointW = default;
        _lastLeaderStatus = null; // force Patrolling-transition check on first UpdateIdle tick
        _lastLeaderHadTargetWaypoint = false; // force waypoint-published detection on first UpdateIdle tick
        _lastLeaderHadApproachStart = false;  // force approach-start detection on first UpdateIdle tick
        _approachAnchorColocated = false;     // reset co-located latch on each FFG entry
        _approachTargetAcquired = false;       // Fix AH: reset focus-chain one-shot latch on each FFG entry
        _approachTargetLastAttemptUtc = DateTime.MinValue;  // Fix AJ: reset focus-chain retry timestamp
        _approachTargetLatchedUtc = DateTime.MinValue;  // Fix AM: reset stale-latch diagnostic timestamp
        _staleLatchLastWarnUtc = DateTime.MinValue;  // Fix AM: reset stale-latch warn cooldown
        _lastSeenApproachAnchorW = default;  // Fix AN: reset local anchor TTL position
        _lastSeenApproachAnchorUtc = DateTime.MinValue;  // Fix AN: reset local anchor TTL timestamp
        _lastLoggedNavTargetMode = NavTargetMode.PositionChase; // first transition will log

        if (input.IsKeyDown(input.ForwardKey))
            input.StopForward(true);

        if (_navState == NavState.NavigatingToLeader)
        {
            logger.LogInformation("[FFG] OnEnter: was NavigatingToLeader — stopping navigation.");
            navigation.Stop();
            ResetNavState();
            _navState = NavState.Idle;
        }
        else if (_navState == NavState.CantFollow)
        {
            // Fix AK (revised, log-76 evidence + oscillation-safety review):
            //
            // Original log-76 scenario (00:34:35:005 → 00:34:40:383):
            //   - Assist entered CantFollow at <-375.78, -4134.94> due to a
            //     genuine Fix AC+AE+AI U-turn rejection (angle=166.7°) when
            //     pathing 10y SE through rocks.
            //   - Combat preempted FFG at 00:34:35:362, ran for 5s, exited
            //     at 00:34:40:217. FFG.OnEnter at 00:34:40:383.
            //   - Pre-fix behavior: "resuming CantFollow state" branch fires
            //     Projection10 with basis=away-from-leader. Leader had moved
            //     south during Combat (final pos <-381.44, -4152.90>), so
            //     away-from-leader projected NORTH. Assist walked north
            //     for 15+ seconds through 16 Projection10 iterations.
            //
            // Naive Fix AK would always reset to Idle on FFG.OnEnter from
            // CantFollow. But that creates a regression for the oscillation
            // case raised in review: a genuinely-stuck assist (terrain
            // obstacle persists) being briefly preempted by Heal, Buff,
            // Loot, or any range-keyed goal would, on each preemption,
            // pay 2 wasted path-find attempts (~100ms) AND briefly
            // re-enter NavigatingToLeader (movement keys may pulse →
            // visible jitter). Repeated rapidly, this is bad UX AND
            // accumulating overhead.
            //
            // The discriminator: preemption duration. Position-based checks
            // looked appealing but log-76 disproved them — the assist moved
            // 1.25y during Combat and the leader moved 0.27y. Both were
            // essentially stationary (the assist a ranged caster fighting
            // in place). The ONLY signal that distinguishes Combat-class
            // preemption from Heal-class preemption is wall-clock duration.
            //
            // Calibration:
            //   - Combat: ~3-30s typical (log-76 was 5s; CombatTracker
            //     "Left Combat after 3.00sec" line shows the shortest
            //     realistic case).
            //   - Heal/Buff: 1-2s cast time + 250ms goal-swap = 1.25-2.25s.
            //   - Loot: 1-3s depending on items.
            //   - ConsumeCorpse: 2-4s depending on level.
            //   - SignificantPreemptionMs = 2500ms threshold.
            //
            // Behavior split:
            //   - Brief preemption (≤2500ms): resume CantFollow with
            //     ResetEscapeState() (original Fix 31/32 behavior). No
            //     regression; oscillation-safe.
            //   - Long preemption (>2500ms): reset to Idle, ResetEscapeState,
            //     ResetNavState. UpdateIdle re-evaluates from current world
            //     state. If still unreachable, FFG re-escalates to CantFollow
            //     after 2 path attempts (~100ms wasted in the worst case).
            //
            // Misclassification cost:
            //   - Very brief Combat (<2500ms) classified as brief: assist
            //     resumes CantFollow with possibly-stale projection direction.
            //     But the leader likely hasn't moved much in such a short
            //     fight, so the projection direction is still roughly correct.
            //   - Long Heal cast (>2500ms, e.g. resurrect-channel) classified
            //     as long: assist pays 2 wasted path attempts before re-
            //     escalating. Bounded, recoverable, no behavior loop.
            //
            // Both misclassification modes are strictly less bad than the
            // log-76 wrong-direction walk.
            navigation.Stop();

            double preemptionMs = _lastOnExitUtc != DateTime.MinValue
                ? (DateTime.UtcNow - _lastOnExitUtc).TotalMilliseconds
                : 0.0;

            // Capture position context for both branches' troubleshooting logs.
            // Computed once outside the branch so a future maintainer can't
            // accidentally diverge the two messages.
            LeaderState? leaderForLog = leaderConnection.LastLeaderState;
            string posCtx;
            if (leaderForLog != null)
            {
                float distToLeader = playerReader.WorldPos.WorldDistanceXYTo(leaderForLog.WorldPos);
                posCtx = $"assist={playerReader.WorldPos}, leader={leaderForLog.WorldPos}, dist={distToLeader:0.0}y";
            }
            else
            {
                posCtx = $"assist={playerReader.WorldPos}, leader=<unknown — no fresh broadcast>";
            }

            if (preemptionMs > SignificantPreemptionMs)
            {
                logger.LogInformation(
                    $"[FFG] OnEnter: CantFollow CLEARED by Fix AK gate. " +
                    $"Preemption={preemptionMs:0}ms (> SignificantPreemptionMs={SignificantPreemptionMs:0}ms), " +
                    $"treating as Combat-class — world state may have changed in non-positional " +
                    $"ways during the preemption. {posCtx}. " +
                    $"Resetting _navState to Idle for fresh evaluation; if the leader is still " +
                    $"unreachable, FFG will re-escalate to CantFollow via the standard " +
                    $"path-failure → _navAttempt escalation flow.");
                ResetEscapeState();
                ResetNavState();
                _navState = NavState.Idle;
            }
            else
            {
                // Original Fix 31/32 behavior — brief preemption, geometry
                // and conditions essentially unchanged. Resume CantFollow,
                // reset escape phase only (so projection picks fresh
                // direction from current position).
                logger.LogInformation(
                    $"[FFG] OnEnter: CantFollow PRESERVED by Fix AK gate. " +
                    $"Preemption={preemptionMs:0}ms (≤ SignificantPreemptionMs={SignificantPreemptionMs:0}ms), " +
                    $"treating as brief (Heal/Buff/Loot-class). {posCtx}. " +
                    $"Resuming CantFollow with escape-phase reset (Fix 31/32 behavior preserved).");
                ResetEscapeState();
            }
        }
    }

    public override void OnExit()
    {
        navigation.ResetApproachEscape();
        ResetSetWaypointLoopGuardState();
        input.StepBackwards();
        wait.Update();

        // ── Route-walking migration: Turn 2 ──
        // Reset waypoint index on FFG exit so the next OnEnter starts
        // cleanly. The next session may re-engage RouteWalk with the
        // assist at a different position; force re-sync.
        _assistRouteIndex = -1;

        // ── Route-walking migration: Turn 3 ──
        // Clear pending anchor on exit. The next FFG session may face
        // a different approach context (or none); stale pending would
        // confuse it.
        _pendingAnchor = default;

        // ── Route-walking migration: Turn 3.5 ──
        // Clear cached leader route index on exit. Symmetric with
        // OnEnter; ensures no leak across FFG sessions.
        _cachedLeaderRouteIdx = -1;
        _cachedLeaderRouteIdxUtc = DateTime.MinValue;

        // ── Route-walking migration: Turn 3.5b ──
        _cacheFallbackInUse = false;

        if (_navState == NavState.NavigatingToLeader)
        {
            logger.LogInformation("[FFG] OnExit: stopping navigation.");
            navigation.Stop();

            // Fix K-1 (log-57 09:16:29:147 → 09:16:36:165 assist held the
            // forward key for 7 s during NO PLAN, traveling ~54 y SW past
            // the leader's intended position because FFG.OnExit halted
            // navigation logic but never released the held Forward key):
            //
            // navigation.Stop() (Navigation.cs:1431-1453) clears nav state,
            // routes, and stuckDetector ownership, but does NOT call
            // input.StopForward — that's the dedicated StopMovement()
            // method at Navigation.cs:1455-1458, which Stop() doesn't
            // invoke. The explicit prior-art comment at
            // FollowRouteGoal.cs:997-1002 documents this same gotcha:
            // "PausePathing/Stop suspend navigation and stop steering
            // (left/right keys) but do NOT release the forward movement
            // key — without StopMovement() the character keeps running
            // forward at walking speed into obstacles."
            //
            // The existing input.StepBackwards() at line 553 above presses
            // Backward for 100 ms then releases. In WoW's input model
            // pressing Backward briefly cancels forward motion, but once
            // Backward is released the Forward key state is still down and
            // the bot resumes running. That's why log-57 shows zero
            // movement-key log lines on the assist between 28:339 and
            // 36:613 yet the bot moved 54 y — Forward was held
            // continuously the whole time.
            //
            // OnEnter at line 514-515 already defensively releases Forward
            // on entry. Mirror that here so OnExit is symmetric and the
            // assist actually halts when navigation ends due to a plan
            // change (Combat preemption, NO PLAN, etc.).
            if (input.IsKeyDown(input.ForwardKey))
                input.StopForward(true);
        }

        if (_navState != NavState.CantFollow)
        {
            // Fix 27 (log-50 14:27:14:932 → 14:28:02:655, leader stood still
            // for 46 seconds while assist fought BL mob via Fix 26 self-
            // defense override):
            //
            // The previous code unconditionally set Status=Waiting on every
            // non-CantFollow exit. When the assist transitions FFG → Combat
            // (or Loot, Consume Corpse, etc.) while still right next to the
            // leader — log-50 measured 0.9 y at OnExit — that overwrote a
            // perfectly valid "I'm with you" state with a misleading
            // "I'm not following" state. Sequence in log-50:
            //   14:27:14:792 FFG.OnEnter → UpdateIdle posts Following (0.9y)
            //   14:27:14:807 FFG.OnExit  → OLD code overwrites to Waiting
            //   14:27:14:932 NO PLAN, then Combat plan at 14:27:17:730
            //   14:27:14 → 14:28:01 (47 s) — CombatGoal/Loot/Consume don't
            //                                 touch Status, so it stays
            //                                 Waiting the whole time
            //   Leader-side: assistrequestreturnorisfollowing=False
            //                → FRG precondition fails → NO PLAN for 46 s,
            //                until the assist's next FFG.OnEnter at
            //                14:28:01:428 re-posts Following.
            //
            // Worse, the 500 ms publisher interval (PartyApiConfig
            // .AssistPostIntervalMs) meant the brief Following posted at
            // 14:27:14:792 was overwritten by Waiting in the publisher's
            // staging slot before the next publish tick fired — so the
            // leader never saw the Following at all, the wasWaiting
            // branch at line ~502 never executed, and the leader's
            // _assistWaitingForFollowing flag (set at 14:27:02:213 when
            // the AssistReturn destination was reached) stayed set.
            //
            // Fix: only set Waiting when actually far from the leader.
            // If we're still inside FollowingMaxYards at exit time, we
            // are positionally still "following" — only the active goal
            // has changed. Keeping Following lets the leader's FRG
            // precondition pass (assistisfollowing=True → assist-
            // requestreturnorisfollowing=True) so it can patrol /
            // pause-for-distance normally instead of locking up.
            //
            // The original comment's worry about NavigatingToLeader
            // leaking into other plans doesn't apply here: FFG.UpdateIdle
            // line ~681 only posts Following when dist < FollowingMaxYards,
            // which is exactly the same condition we re-check here. So
            // we never publish Following when the assist is actually
            // out of position.
            //
            // Edge case — assist exits FFG mid-navigation at > 7 y from
            // leader (e.g. NavigatingToLeader interrupted by Combat at
            // 10 y): old behavior preserved, Status=Waiting, because
            // the leader genuinely cannot assume we're in position.
            LeaderState? leader = leaderConnection.LastLeaderState;
            float distToLeader = (leader != null)
                ? playerReader.WorldPos.WorldDistanceXYTo(leader.WorldPos)
                : float.MaxValue;

            if (distToLeader < FollowingMaxYards)
            {
                if (assistStatusProvider.CurrentStatus != BotStatus.Following)
                {
                    logger.LogInformation(
                        $"[FFG] OnExit: assist within {distToLeader:0.0}y of leader " +
                        $"(< FollowingMaxYards={FollowingMaxYards}y) — setting Status=Following " +
                        $"(was {assistStatusProvider.CurrentStatus}). " +
                        $"Leader's FRG precondition stays satisfied through whatever non-FFG goal runs next.");
                    assistStatusProvider.CurrentStatus = BotStatus.Following;
                }
            }
            else
            {
                if (assistStatusProvider.CurrentStatus != BotStatus.Waiting)
                {
                    logger.LogInformation(
                        $"[FFG] OnExit: assist at {distToLeader:0.0}y from leader " +
                        $"(≥ FollowingMaxYards={FollowingMaxYards}y) — setting Status=Waiting.");
                    assistStatusProvider.CurrentStatus = BotStatus.Waiting;
                }
            }
            _navState = NavState.Idle;
        }
        // CantFollow persists across plan cycles so the assist holds position.

        // Fix AK: capture exit timestamp so the next OnEnter can measure
        // preemption duration and discriminate brief preemptions (Heal/Buff,
        // resume CantFollow) from long ones (Combat, reset to Idle for
        // fresh evaluation).
        _lastOnExitUtc = DateTime.UtcNow;
    }

    public bool ChangeToTarget(KeyAction keyAction) 
    {
        bool validChangeToTarget = false;

        if (!string.IsNullOrEmpty(keyAction.ChangeTargetTo) && keyAction.CanRun())
        {
            wait.Update();

            switch (keyAction.ChangeTargetTo)
            {
                case "focus":
                case "party1":
                    validChangeToTarget = true;
                    input.PressTargetFocus();
                    break;
                case "party2":
                    validChangeToTarget = true;
                    input.PressTargetFocusPartyMemberTwo();
                    break;
                case "party3":
                    validChangeToTarget = true;
                    input.PressTargetFocusPartyMemberThree();
                    break;
                case "party4":
                    validChangeToTarget = true;
                    input.PressTargetFocusPartyMemberFour();
                    break;
                default:
                    logger.LogWarning("keyAction.ChangeTargetTo not a valid target: " + keyAction.ChangeTargetTo);
                    break;
            }

            wait.Update();
        }

        return validChangeToTarget;
    }

    // -----------------------------------------------------------------------
    // Main update
    // -----------------------------------------------------------------------

    public override void Update()
    {
        if (bits.Drowning())
            input.PressJump();

        // Note: API-based mob blacklist diff lives in GoapAgent.GoapThread
        // (not here) so the signal interrupts the assist's combat regardless
        // of which goal is active. Combat (cost 4) preempts FFG (cost 19),
        // so an FFG-only diff would only fire after combat ended naturally —
        // observed in log 22 (assist 18:03:34–18:03:46): the assist saw the
        // GUID 3 seconds AFTER kill credit because FFG.OnEnter fired only
        // post-loot. The agent-level diff is the correct location.

        if (chatReader.ForcedFollow)
        {
            AddEffect(GoapKey.forcedfollow, true);
            return;
        }

        if (restHandler.IsResting())
        {
            logger.LogInformation("[FFG] Waiting while resting.");
            while (restHandler.IsResting())
                wait.Update(1000);
        }


        for (int i = 0; i < Keys.Length; i++)
        {
            KeyAction keyAction = Keys[i];
            bool validChangeToTarget = ChangeToTarget(keyAction);

            if (castingHandler.SpellInQueue() && !keyAction.BaseAction)
                continue;

            if (keyAction.BeforeCastDismount && mountHandler.IsMounted())
                mountHandler.Dismount();

            if (chatReader.ForcedFollow && !keyAction.UseWithForcedFollow)
                continue;

            if (castingHandler.CastIfReady(keyAction,
                keyAction.Interrupts.Count > 0
                ? keyAction.CanBeInterrupted
                : bits.Target_Alive))
                break;

            if (validChangeToTarget)
            {
                input.PressLastTarget();
                wait.Update();
            }
        }


        // ── Evade-recovery hold (REMOVED in session 27) ────────────────────
        // Session 23 added a hold-position gate here that returned early during
        // _evadeRecoveryActive, on the premise that "the leader is still next
        // to the blacklisted mob when the event fires (and stays there until
        // the leader-side PressClearTarget releases the wantNavPaused gate in
        // FRG.cs:741); chasing the leader's body via PositionChase would
        // route the assist into the danger zone."
        //
        // That premise was true at the time because Defect D (FRG.wantNavPaused
        // didn't filter IsIgnored) prevented the leader from moving during
        // evade. Fix 4 in session 26 closed Defect D — wantNavPaused now
        // filters playerReader.IsIgnored, so the leader actually retreats.
        //
        // Log 27 confirmed the new failure mode created by leaving this gate
        // in place after Fix 4:
        //   00:50:05:631  evade fires
        //   00:50:05:829  leader's FRG starts navigating (Fix 4 working) —
        //                  RightArrow movement keys begin
        //   00:50:08:608  leader has covered ~18y, still moving
        //   00:50:09:602  leader hits LeaderPauseYards=20y → "[FRG] Pausing
        //                  for assist — dist=20.0y status=TooFar"
        //   00:50:05:771–30:610  assist FFG total silence (this gate held it
        //                  in Idle for the full 24.84s window)
        //   00:50:30:646  leader's evade window elapses
        //   00:50:31:011  Tab in CombatGoal.FindPossibleThreats finds the
        //                  same blacklisted mob still adjacent (because the
        //                  leader couldn't continue retreating)
        //   00:50:31:057  re-fired evade → cycle restarts
        //
        // Removing the gate lets FFG's normal state machine run during the
        // evade-recovery window. The assist stays Idle while inside
        // FollowingMaxYards (7y), transitions to NavigatingToLeader when the
        // retreating leader exceeds NavigatingMinYards (14y), and tracks the
        // leader's retreat naturally. The original "into the danger zone"
        // concern is moot because the leader's body is no longer in the
        // danger zone — it's actively retreating away from it.
        //
        // Other defenses remain in place to ensure the assist does not
        // engage the blacklisted mob during the window:
        //   - CombatGoal's AddPrecondition(GoapKey.evadeRecovery, false)
        //     keeps Combat unselectable for the entire 25 s window.
        //   - TFT.CanRun rejects on _evadeRecoveryActive AND, independently,
        //     when playerReader.FocusTargetGuid is in IsIgnored (Fix 3/5a).
        //   - CombatGoal.Update's session-24 IsIgnored short-circuit
        //     (CombatGoal.cs:198) catches any race window where Combat
        //     somehow runs an Update tick.
        //   - TFT.Update's IsIgnored guard before the F press (Fix 5b).
        //
        // Stuck detection now runs unconditionally during the evade window
        // (see TickNavActiveTimeout / TickActiveStuckDetection /
        // TickIdleStuckDetection). The previous code suppressed it during
        // evade because the hold gate above forced the assist stationary
        // and we didn't want false-positive Stuck reports for that forced
        // pause. With the gate gone, the assist navigates during evade and
        // any real stall is a real problem — surfacing it (BotStatus.Stuck
        // → leader pauses → recovery / pather-based escape) is exactly the
        // right behaviour during a flee. Only bits.Combat() remains as an
        // exemption because cast-loop stationarity is genuinely not a stall.

        // ── Fix AJ (log-75 23:53:44 onwards): retry until hostile target acquired ──
        //
        // Fix AH as originally shipped latched the one-shot
        // _approachTargetAcquired UNCONDITIONALLY after pressing the focus
        // chain. log-75 evidence showed this latches even when the chain
        // fails:
        //
        //   23:53:44:232  Fix AH log line ("acquiring leader's target...")
        //   23:53:44:294  PageUp (PressTargetFocus) pressed
        //   23:53:44:355  F     (PressTargetOfTarget) pressed (60ms after)
        //   23:53:44:355  _approachTargetAcquired = true latched
        //   23:53:44:355  FFG: Reached follow position (dist=3.6y) — Idle.
        //   [next 2.68 s: no plan transition; ATG never selected]
        //   23:53:47:052  FFG: Leader out of range (14.7y) — resuming nav
        //   ...PositionChase through trees...
        //   23:53:52:467  Fix AC+AE+AI rejection #1 (angle=151°, legit U-turn)
        //   23:53:53:037  Fix AC+AE+AI rejection #2 (angle=162°, legit U-turn)
        //   23:53:53:054  Fix AB-2 projection 10y AWAY from leader (NW direction
        //                 leader at <-483.57, -4314.54>; bot escapes SE)
        //
        // Root cause: WoW Classic round-trip latency is typically 30-100ms.
        // 60ms between PageUp and F is at the lower edge of that window.
        // When F is processed by the client BEFORE PageUp's effect lands
        // (no current target yet), F is a no-op. Then PageUp eventually
        // arrives: assist's target = focus = leader (friendly). The
        // unconditional latch then prevented any retry.
        //
        // Consequences in ATG's preconditions:
        //   - targethostile=false  (the leader is friendly)        ← FAILS
        //   - incombatrange=true   (assist is 3.6y from leader,
        //                           ≤ 5y melee combat range)        ← FAILS
        // Either failure alone deselects ATG. Planner stays on FFG, falls
        // into PositionChase, hits the legitimate U-turn rejections, and
        // escalates to CantFollow's away-from-leader projection.
        //
        // Fix AJ: only LATCH _approachTargetAcquired when the chain actually
        // produced a hostile target. Retry the chain on subsequent FFG.Update
        // ticks at a 250ms rate-limit (covers max typical round-trip with
        // headroom). The retry case is robust because the FAILED first
        // attempt left the assist's target = leader (focus); on the retry,
        // PageUp targets the same focus (no-op visible to F), and F now
        // sees a stable current target and correctly cascades to
        // leader.target = mob (hostile). Convergence in 1-2 retries.
        //
        // The retry stops naturally when either:
        //   (a) bits.Target() && bits.Target_Hostile() → latch closes the loop
        //   (b) HasApproachStart goes false (anchor cleared) → block-gate fails
        //   (c) _approachAnchorColocated resets → block-gate fails
        //
        // Why hostile-only as the success criterion:
        //   targethostile=true is the precondition ATG actually needs. We
        //   check bits.Target() too so the latch can't accidentally close
        //   on a stale-from-prior-combat bit (defense-in-depth).
        // ── Fix AH (log-73): focus-chain target acquisition at approach anchor ──
        //
        // Hand off to ApproachTargetGoal at the moment the assist is co-located
        // with the leader's approach-start anchor. ATG's AssistFocus precondition
        // (GoapKey.partyEngaging, formerly partyincombat) is already satisfied
        // by leader.HasApproachStart via the corresponding partyEngaging key in
        // GoapAgent.UpdateWorldState. The remaining blocker is the common
        // hastarget=true precondition — the assist has no target at this point
        // because the leader's mob is too far/obscured for autotargeting to
        // grab it.
        //
        // Solution: press the same focus chain that ATG.Update's AssistFocus
        // branch presses every tick (PressTargetFocus + PressTargetOfTarget).
        // On the next planner tick, the world state includes hastarget=true
        // and ATG (cost 8) outranks FFG (cost 19) — control transfers to ATG,
        // which then re-presses the focus chain every Update and adds
        // PressApproach for the interact-key auto-walk.
        //
        // Gating conditions (all must hold):
        //   - leader != null              we have a fresh broadcast
        //   - leader.HasApproachStart     the leader is in its ATG phase
        //   - _approachAnchorColocated    the assist is at the anchor; the
        //                                 latch is set by GetNavigationTarget
        //                                 when the bot is within POP_DIST of
        //                                 the anchor.
        //   - !_approachTargetAcquired    not yet succeeded — see Fix AJ
        //                                 above for the conditional latch.
        //   - elapsed ≥ ApproachTargetAcquireRetryMs since last attempt
        //                                 (Fix AJ rate-limit).
        //
        // Placement above the state machine: at the approach anchor the assist
        // toggles rapidly between Idle and NavigatingToLeader (log-73 shows
        // <15 ms toggling). A check inside any single state handler would
        // miss most ticks. Above-the-switch placement is state-agnostic.
        //
        // ── Fix AN (log-74 22:52, log-77 first-approach 01:46:45→01:46:48):
        // Local anchor TTL fallback.
        //
        // log-74 22:52: leader's ATG window was 2.55s. At the moment the
        //   anchor cleared, the assist was 4.28y from it — just past
        //   POP_DIST (3.6y). The co-location latch never fired; Fix AH
        //   never ran. Assist fell back to PositionChase through trees,
        //   hit Fix AC+AE+AI rejection, escalated to CantFollow.
        // log-77 first-approach 01:46:45→01:46:48: leader's ATG window
        //   was only 2.14s — even shorter. The assist never converged
        //   to the anchor. Same failure mode as log-74.
        //
        // Pattern: leader's ATG completes faster than the assist can
        // traverse to the anchor. The anchor was a perfectly good place
        // to fire the focus-chain handoff, but the leader's side cleared
        // it on ATG.OnExit, and the assist abandoned the latch a tick
        // later when HasApproachStart=false was observed.
        //
        // Fix AN: remember the most recently observed anchor position
        // for up to AnchorLocalTtlMs after HasApproachStart goes false.
        // If, within that window, the assist actually arrives at the
        // anchor location (within POP_DIST), fire the same Fix AH+AJ
        // focus-chain anyway. The target acquisition itself is just as
        // useful one tick after the anchor expired: the leader still
        // has the target selected (it's in PTG/Combat now), and the
        // assist can latch onto it via the focus chain.
        //
        // Why an assist-side TTL rather than leader-side extended anchor:
        // The leader-side fix (keep the anchor set across PTG/Combat)
        // requires touching multiple files (ATG, PTG, CombatGoal, plus
        // all of ATG's bail-out paths) and creates new coordination
        // contracts. The assist-side TTL is single-file, has a bounded
        // blast radius (3s window), and addresses the same failure mode.
        // If the simpler fix proves insufficient, the leader-side fix
        // remains an option to layer on top.
        //
        // ── Fix AM (log-77 01:47:03:506 → end-of-log):
        // Diagnostic logging for "Fix AJ latched but ATG never took over."
        //
        // log-77 evidence: at 01:47:03:506 Fix AJ confirmed hostile target
        // acquired (guid=535132). FFG.Update continued running for another
        // 3+ seconds in PositionChase mode, with the planner never selecting
        // ATG. One of ATG's preconditions must be failing in
        // GoapAgent.UpdateWorldState, but without a snapshot of the relevant
        // GoapKey values at the latch moment we can't tell which.
        //
        // Fix AM emits two diagnostics:
        //   1. At the moment Fix AJ latches, dump every ATG-gating value
        //      that FFG can observe (bits.Target_*, playerReader.With...,
        //      navigation.IsInBlacklistArea, leaderNavProvider.HasApproachStart,
        //      chatReader.ForcedFollow). This gives a single line of evidence
        //      at the moment the world state should be ATG-favorable.
        //   2. If FFG.Update is still running >StaleLatchWarnAfterMs after
        //      the latch with HasApproachStart still true, emit a warning
        //      indicating ATG did NOT take over. This makes the bug visible
        //      in the log even when nothing else looks wrong.
        //
        // Together these turn "ATG silently not picked" into a single
        // greppable warning line plus a diagnostic snapshot.
        {
            LeaderState? approachLeader = leaderConnection.LastLeaderState;
            bool leaderAnchorActive = approachLeader != null && approachLeader.HasApproachStart;

            // Fix AN: capture the anchor position whenever HasApproachStart=true
            // so we can use it within AnchorLocalTtlMs after it expires.
            if (leaderAnchorActive)
            {
                _lastSeenApproachAnchorW = new Vector3(
                    approachLeader!.ApproachStartWorldX,
                    approachLeader.ApproachStartWorldY,
                    0f);
                _lastSeenApproachAnchorUtc = DateTime.UtcNow;
            }

            // Determine whether we should try Fix AH+AJ this tick. Two paths:
            //   (a) original Fix AH path: HasApproachStart=true AND the
            //       _approachAnchorColocated latch (set by GetNavigationTarget)
            //       AND not yet acquired.
            //   (b) Fix AN local-TTL path: HasApproachStart=false, but
            //       _lastSeenApproachAnchorUtc is fresh (within AnchorLocalTtlMs),
            //       and the bot is currently at the last-seen anchor position
            //       (within POP_DIST). The bot might have arrived just after
            //       the leader cleared HasApproachStart.
            bool isAtAnchorLive = leaderAnchorActive && _approachAnchorColocated;
            bool isAtAnchorViaTtl = false;
            if (!leaderAnchorActive
                && approachLeader != null
                && _lastSeenApproachAnchorUtc != DateTime.MinValue)
            {
                double anchorAgeMs = (DateTime.UtcNow - _lastSeenApproachAnchorUtc).TotalMilliseconds;
                if (anchorAgeMs < AnchorLocalTtlMs)
                {
                    float distToLastAnchor = playerReader.WorldPos.WorldDistanceXYTo(_lastSeenApproachAnchorW);
                    if (distToLastAnchor < Navigation.POP_DIST)
                    {
                        isAtAnchorViaTtl = true;
                    }
                }
            }

            if (approachLeader != null
                && (isAtAnchorLive || isAtAnchorViaTtl)
                && !_approachTargetAcquired)
            {
                double msSinceLast =
                    (DateTime.UtcNow - _approachTargetLastAttemptUtc).TotalMilliseconds;
                bool rateLimitOk =
                    _approachTargetLastAttemptUtc == DateTime.MinValue ||
                    msSinceLast >= ApproachTargetAcquireRetryMs;

                if (rateLimitOk)
                {
                    bool isRetry = _approachTargetLastAttemptUtc != DateTime.MinValue;
                    _approachTargetLastAttemptUtc = DateTime.UtcNow;

                    string gatePath = isAtAnchorLive
                        ? "live anchor (HasApproachStart=true, _approachAnchorColocated=true)"
                        : "Fix AN local-TTL (anchor expired but assist arrived within " +
                          $"{AnchorLocalTtlMs:0}ms at last-seen position)";

                    logger.LogInformation(
                        $"[FFG] [FIX-FIRE] AH+AJ+AN: at approach anchor via {gatePath}" +
                        $"{(isRetry ? $" — retry msSinceLast={msSinceLast:0}" : "")} — " +
                        $"acquiring leader's target via PressTargetFocus + PressTargetOfTarget. " +
                        $"Will latch only if bits.Target_Hostile() confirms a hostile acquisition; " +
                        $"otherwise retry after {ApproachTargetAcquireRetryMs:0}ms.");

                    input.PressTargetFocus();
                    wait.Update();
                    input.PressTargetOfTarget();
                    wait.Update();

                    // Fix AJ: verify the chain produced a HOSTILE target before
                    // latching. See the comment block above for the full
                    // race-condition analysis and evidence trail from log-75.
                    if (bits.Target() && bits.Target_Hostile())
                    {
                        _approachTargetAcquired = true;
                        _approachTargetLatchedUtc = DateTime.UtcNow;  // Fix AM: record for stale-latch warning

                        logger.LogInformation(
                            $"[FFG] [FIX-FIRE] AJ: focus-chain confirmed hostile target acquired " +
                            $"(guid={playerReader.TargetGuid}) — latching one-shot. " +
                            $"ATG (cost 8) should win the next plan over FFG (cost 19) " +
                            $"and take over the interact-key approach.");

                        // Fix AM: dump every ATG-gating value FFG can observe.
                        // This snapshots what FFG sees at the moment the world
                        // state should be ATG-favorable. If ATG isn't picked
                        // on the next planner tick, one of these is the reason.
                        bool diagHasTarget       = bits.Target();
                        bool diagTargetDead      = bits.Target_Dead();
                        bool diagTargetIsAlive   = diagHasTarget && !diagTargetDead;
                        bool diagTargetHostile   = bits.Target_Hostile();
                        bool diagInCombatRange   = playerReader.WithInCombatRange();
                        bool diagInBlacklistArea = navigation.IsInBlacklistArea();
                        bool diagForcedFollow    = chatReader.ForcedFollow;
                        bool diagHasApproachStart = approachLeader.HasApproachStart;
                        bool diagBitsCombat      = bits.Combat();
                        logger.LogInformation(
                            $"[FFG] [FIX-FIRE] AM: ATG-precondition snapshot at latch moment — " +
                            $"hastarget={diagHasTarget}, targetisalive={diagTargetIsAlive} " +
                            $"(dead={diagTargetDead}), targethostile={diagTargetHostile}, " +
                            $"incombatrange={diagInCombatRange}, " +
                            $"inblacklistarea={diagInBlacklistArea}, " +
                            $"forcedfollow={diagForcedFollow}, " +
                            $"HasApproachStart={diagHasApproachStart} (contributes to partyEngaging), " +
                            $"bits.Combat={diagBitsCombat} (also contributes to partyEngaging via PartyInCombat). " +
                            $"ATG.AssistFocus requires: partyEngaging=true, forcedfollow=false, " +
                            $"hastarget=true, targetisalive=true, targethostile=true, " +
                            $"incombatrange=false, inblacklistarea=false, evadeRecovery=false. " +
                            $"If ATG is not selected on the next planner tick, the failing precondition " +
                            $"is the one above whose value is wrong-side of its expectation.");
                    }
                    else
                    {
                        logger.LogWarning(
                            $"[FFG] [FIX-FIRE] AJ: focus-chain attempt did not produce a hostile " +
                            $"target (bits.Target={bits.Target()}, bits.Target_Hostile=" +
                            $"{bits.Target_Hostile()}, currentTargetGuid={playerReader.TargetGuid}). " +
                            $"Will retry in {ApproachTargetAcquireRetryMs:0}ms. " +
                            $"Probable cause: WoW client hadn't processed PressTargetFocus " +
                            $"yet when PressTargetOfTarget fired — retry should converge " +
                            $"because the assist's target is now stable for the next chain.");
                    }
                }
            }

            // Fix AM: stale-latch warning. If we successfully latched but
            // FFG.Update is STILL running well past StaleLatchWarnAfterMs,
            // the planner has chosen FFG (not ATG) on multiple iterations.
            // Emit a warning explaining the situation; rate-limited via
            // _staleLatchLastWarnUtc.
            if (_approachTargetAcquired
                && _approachTargetLatchedUtc != DateTime.MinValue
                && leaderAnchorActive)
            {
                double msSinceLatch =
                    (DateTime.UtcNow - _approachTargetLatchedUtc).TotalMilliseconds;
                if (msSinceLatch > StaleLatchWarnAfterMs)
                {
                    double msSinceLastWarn =
                        _staleLatchLastWarnUtc == DateTime.MinValue
                            ? double.MaxValue
                            : (DateTime.UtcNow - _staleLatchLastWarnUtc).TotalMilliseconds;

                    if (msSinceLastWarn >= StaleLatchWarningCooldownMs)
                    {
                        _staleLatchLastWarnUtc = DateTime.UtcNow;

                        // Re-read the gating state for the warning so the operator
                        // sees the CURRENT values (which may have shifted since
                        // the latch moment).
                        bool curHasTarget       = bits.Target();
                        bool curTargetDead      = bits.Target_Dead();
                        bool curTargetHostile   = bits.Target_Hostile();
                        bool curInCombatRange   = playerReader.WithInCombatRange();
                        bool curInBlacklistArea = navigation.IsInBlacklistArea();
                        bool curForcedFollow    = chatReader.ForcedFollow;
                        bool curBitsCombat      = bits.Combat();
                        logger.LogWarning(
                            $"[FFG] [FIX-FIRE] AM: STALE LATCH WARNING — Fix AJ latched " +
                            $"{msSinceLatch:0}ms ago (> {StaleLatchWarnAfterMs:0}ms threshold) " +
                            $"but FFG.Update is still running. ATG was NOT selected by the " +
                            $"planner. Current ATG-precondition state — " +
                            $"hastarget={curHasTarget}, targetisalive={curHasTarget && !curTargetDead} " +
                            $"(dead={curTargetDead}), targethostile={curTargetHostile}, " +
                            $"incombatrange={curInCombatRange}, " +
                            $"inblacklistarea={curInBlacklistArea}, " +
                            $"forcedfollow={curForcedFollow}, " +
                            $"HasApproachStart=true (still), bits.Combat={curBitsCombat}, " +
                            $"currentTargetGuid={playerReader.TargetGuid}. " +
                            $"One of the above values is blocking ATG (cost 8) from winning " +
                            $"the plan over FFG (cost 19). Next warning in " +
                            $"{StaleLatchWarningCooldownMs:0}ms if condition persists.");
                    }
                }
            }
        }

        // ── State machine ──────────────────────────────────────────────────
        switch (_navState)
        {
            case NavState.Idle:              UpdateIdle();              break;
            case NavState.NavigatingToLeader: UpdateNavigatingToLeader(); break;
            case NavState.CantFollow:        UpdateCantFollow();        break;
        }
    }

    // -----------------------------------------------------------------------
    // State: Idle — within FollowingMaxYards, posting Following
    // -----------------------------------------------------------------------
    private void UpdateIdle()
    {
        LeaderState? leader = leaderConnection.LastLeaderState;

        if (leader == null || leaderConnection.LocalAgeMs > leaderConnection.StaleThresholdMs)
        {
            // No fresh data — stay put, don't post Following.
            assistStatusProvider.CurrentStatus = BotStatus.Waiting;
            wait.Update();
            return;
        }

        float dist = playerReader.WorldPos.WorldDistanceXYTo(leader.WorldPos);

        // Beyond dead-band upper threshold → start navigating.
        if (dist > NavigatingMinYards)
        {
            logger.LogInformation(
                $"[FFG] Leader out of range ({dist:0.0}y > {NavigatingMinYards}y) — starting navigation.");
            StartNavigatingToLeader(leader);
            return;
        }

        if (dist < FollowingMaxYards)
        {
            // Clearly within range — post Following.
            if (assistStatusProvider.CurrentStatus != BotStatus.Following)
            {
                logger.LogInformation(
                    $"[FFG] Within {dist:0.0}y of leader (threshold={FollowingMaxYards}y) — posting Following.");
                assistStatusProvider.CurrentStatus = BotStatus.Following;
                assistStatusProvider.CantFollow = false;
            }

            // Confirm rendezvous when both bots are co-located during patrol.
            // This gates the switch to waypoint-sharing mode — the assist must be
            // within FollowingMaxYards (7y) of the leader before it starts navigating
            // to shared waypoints, ensuring both paths start from roughly the same point.
            if (!_rendezvousConfirmed && leader.Status == BotStatus.Patrolling)
            {
                _rendezvousConfirmed = true;
                _lastSharedWaypointW = default; // force waypoint refresh on first use
                logger.LogInformation("[FFG] Rendezvous confirmed — waypoint-sharing mode active.");
            }

            // Keep the transition tracker up to date so that crossing from < 7y into
            // the dead-band doesn't produce a false leaderJustResumedPatrol trigger.
            _lastLeaderStatus = leader.Status;

            // Track the waypoint transition for the 7-14y block below.
            // Do NOT call StartNavigatingToLeader here even if leaderJustPublishedWaypoint
            // is true: the assist is already within FollowingMaxYards (7y) of the leader,
            // so NavigatingToLeader would immediately exit back to Idle on the very next
            // GOAP tick (dist < 7y → "Reached follow position"). That 15ms flash produces
            // no movement and no benefit — the FRG sync-pause already resolves via
            // AnyAssistIsFollowing()=true which covers both Following and NavigatingToLeader.
            bool leaderJustPublishedWaypoint = leader.HasTargetWaypoint && !_lastLeaderHadTargetWaypoint
                && !leader.HasApproachStart;
            _lastLeaderHadTargetWaypoint = leader.HasTargetWaypoint;
            // (leaderJustPublishedWaypoint is used in the 7-14y block below)

            // Break the dead-band when the leader just entered ATG/PTG.
            // The approach-start anchor is the leader's world position at ATG entry —
            // navigating there immediately ensures the assist reaches the leader's
            // starting point before the leader has pressed interact far toward the mob.
            bool leaderStartedApproaching = leader.HasApproachStart && !_lastLeaderHadApproachStart;
            _lastLeaderHadApproachStart = leader.HasApproachStart;
            if (leaderStartedApproaching)
            {
                logger.LogInformation(
                    $"[FFG] Leader started approaching mob while co-located (dist={dist:0.0}y) — " +
                    "breaking dead-band: navigating to approach-start anchor.");
                StartNavigatingToLeader(leader);
                return;
            }
        }
        else
        {
            // When leader leaves Patrolling (combat, looting, resting), clear the
            // rendezvous so we revert to position-chasing until next co-location.
            if (_rendezvousConfirmed && leader.Status != BotStatus.Patrolling)
            {
                _rendezvousConfirmed = false;
                _lastSharedWaypointW = default;
                logger.LogInformation($"[FFG] Leader status {leader.Status} — clearing rendezvous, reverting to position-chase.");
            }

            // ── Patrolling-transition detection ─────────────────────────────────
            // When the leader's FRG resumes after combat/loot, its status flips from
            // non-Patrolling → Patrolling. If we are in the dead-band (7–14y) AND
            // the rendezvous is already confirmed, the normal dead-band logic would
            // silently wait up to 2 seconds before the leader exceeds NavigatingMinYards
            // (14y / 7y/s = 2s), giving the leader a 14y head start. Detecting this
            // transition here breaks the silent wait and starts navigation immediately,
            // which in combination with the FRG sync-pause (see FollowRouteGoal) results
            // in both bots starting to move together within one API poll cycle (~250ms).
            bool leaderJustResumedPatrol =
                leader.Status == BotStatus.Patrolling &&
                _lastLeaderStatus.HasValue &&              // not the initialisation sentinel (null)
                _lastLeaderStatus != BotStatus.Patrolling; // genuine non→Patrol transition

            // leaderJustPublishedWaypoint fires when HasTargetWaypoint transitions false→true.
            // This is the reliable signal that FRG's sync-pause resolved and RefillWaypoints
            // (or PublishPatrolWaypoint) ran. Works even when the leader was already
            // Patrolling and the status doesn't change — which is the common post-combat case.
            // Suppressed when HasApproachStart=true: the anchor detection below takes
            // priority, and the patrol waypoint is irrelevant once ATG has started.
            // Without this suppression, both transitions fire in sequence (~800ms apart),
            // producing a wasted NavigatingToLeader→Idle cycle from the waypoint detection
            // before the anchor detection correctly navigates to the right target.
            bool leaderJustPublishedWaypoint = leader.HasTargetWaypoint && !_lastLeaderHadTargetWaypoint
                && !leader.HasApproachStart;

            // leaderStartedApproaching fires when HasApproachStart transitions false→true,
            // meaning the leader just entered ATG/PTG. Break the dead-band immediately so
            // the assist is at the anchor before the leader has pressed interact far toward the mob.
            bool leaderStartedApproaching = leader.HasApproachStart && !_lastLeaderHadApproachStart;

            BotStatus? previousLeaderStatus = _lastLeaderStatus; // capture before updating; may be null on first tick
            _lastLeaderStatus = leader.Status; // update for next tick
            _lastLeaderHadTargetWaypoint = leader.HasTargetWaypoint;
            _lastLeaderHadApproachStart = leader.HasApproachStart;

            if (leaderJustResumedPatrol ||
                (leaderJustPublishedWaypoint && leader.Status == BotStatus.Patrolling) ||
                leaderStartedApproaching)
            {
                logger.LogInformation(
                    $"[FFG] Leader resumed patrol/started approaching " +
                    $"(was {previousLeaderStatus?.ToString() ?? "Initial"}, waypointPublished={leaderJustPublishedWaypoint}, approachStarted={leaderStartedApproaching}) — " +
                    $"starting navigation immediately to match (dist={dist:0.0}y).");
                StartNavigatingToLeader(leader);
                return;
            }

            // Dead-band: NavigatingExitYards ≤ dist ≤ NavigatingMinYards.
            // Only stay silent here if we are ALREADY Following AND rendezvous is confirmed.
            // If rendezvous is not confirmed, we must navigate to close the gap to < 7y —
            // otherwise the leader will patrol away during the silent wait, making rendezvous
            // impossible for the entire next patrol leg (causing position-chasing instead of
            // waypoint-sharing, which then leads to the assist falling further and further behind).
            //
            // This commonly occurs when FFG re-enters after combat/loot with the assist in the
            // dead-band and status still set to Following from the previous session. Without this
            // fix, UpdateIdle silently waits 3-4s while the leader runs to 14y+, then the assist
            // starts chasing but can never close to < 7y before the next combat stop.
            if (assistStatusProvider.CurrentStatus == BotStatus.Following && _rendezvousConfirmed)
            {
                // Already Following with confirmed rendezvous — minor distance fluctuation, stay put.
            }
            else
            {
                // Navigate to close the gap: either status is not yet Following, or rendezvous
                // has not been confirmed since the last OnEnter. Both cases require reaching
                // < FollowingMaxYards (7y) before the leader pulls too far ahead.
                string reason = !_rendezvousConfirmed
                    ? "closing gap to confirm rendezvous"
                    : "not yet Following";

                // Fix O (log-60 12:49:36:227-12:49:36:659 oscillation, ~30ms-per-cycle
                // Idle ↔ NavigatingToLeader flip escalating to CantFollow in ~150ms via
                // SetWaypointLoopGuarded):
                //
                // When the leader is at the BL rect boundary (within the 6y inflated
                // zone), ComputeFollowTargetWorldPos's projection (line 2054-2089)
                // caps the candidate ~6y from the rect's strict edge. In log-60 the
                // leader was at <950.43, 271.91> — 2y west of MinX=952.39, well
                // inside the 6y inflated zone — and the projection produced targets
                // like <946.23, 272.26>, only 3.4y from the assist at <942.40, 272.17>.
                //
                // Navigation's wpAlreadyReached threshold (3.55y, just under
                // POP_DIST=3.60y) pops such a waypoint within ~93ms of being set,
                // firing OnWayPointReached → co-located branch (line 2790-2838) →
                // _rendezvousConfirmed=true → NavState=Idle. The very next UpdateIdle
                // tick clears rendezvous (leader.Status != Patrolling — line 925) and
                // falls into this dead-band navigate branch, which issues the SAME
                // SetWaypoint to the same boundary target, popped again, etc.
                //
                // The oscillation cannot proceed because the projection target is
                // geometrically as close as the BL constraint allows. There is nothing
                // to navigate toward. SetWaypointLoopGuarded eventually escalates to
                // CantFollow (10 stationary sets, 100ms window), which triggers
                // PhysicalUnstuck (no-op — assist isn't stuck), Fix I-1 leader-AssistReturn
                // (leader navigates to the assist's "stuck" position where it's already
                // standing), and a multi-second recovery cycle.
                //
                // Fix: detect the projection-boundary case here, before calling
                // StartNavigatingToLeader. If the would-be target is within POP_DIST
                // of the assist, accept the position as a stable boundary — post
                // Following (parity with OnWayPointReached's co-located branch at
                // line 2831 which also sets Following) and remain Idle without
                // setting a waypoint. When the leader later moves further from BL
                // (or out of the inflated zone entirely), wouldBeDist becomes
                // > POP_DIST and this guard releases — StartNavigatingToLeader fires
                // normally.
                //
                // Crucially, we do NOT set _rendezvousConfirmed=true here. Setting
                // it would cause UpdateIdle's "clearing rendezvous" branch (line 925)
                // to fire on the next tick — same source of oscillation we are
                // trying to break, just via a different path. With rendezvous staying
                // false, the next UpdateIdle re-enters this same dead-band branch,
                // hits the guard again, stays silent (status already Following so the
                // log is gated).
                Vector3 wouldBeTarget = GetNavigationTarget(leader);
                float wouldBeDist = playerReader.WorldPos.WorldDistanceXYTo(wouldBeTarget);
                if (wouldBeDist < Navigation.POP_DIST)
                {
                    if (assistStatusProvider.CurrentStatus != BotStatus.Following)
                    {
                        logger.LogInformation(
                            $"[FFG] Dead-band ({dist:0.0}y): would-be target {wouldBeTarget} " +
                            $"co-located ({wouldBeDist:0.0}y < {Navigation.POP_DIST}y) — " +
                            $"projection at BL boundary, posting Following and holding Idle " +
                            $"to avoid SetWaypoint/pop oscillation. " +
                            $"Reason was: {reason}.");
                        assistStatusProvider.CurrentStatus = BotStatus.Following;
                        assistStatusProvider.CantFollow = false;
                    }
                    return;
                }

                logger.LogInformation(
                    $"[FFG] Dead-band ({dist:0.0}y) — {reason}.");
                StartNavigatingToLeader(leader);
                return;
            }
        }

        wait.Update();
    }

    // -----------------------------------------------------------------------
    // State: NavigatingToLeader — pather routing to leader's live position
    // -----------------------------------------------------------------------
    private void UpdateNavigatingToLeader()
    {
        // ── Active-time timeout ─────────────────────────────────────────────
        TickNavActiveTimeout();
        if (_navState != NavState.NavigatingToLeader)
            return;

        // ── Chase-progress watchdog escalation ──────────────────────────────
        // When Navigation's chase watchdog reports it's been more than
        // ChaseWatchdogCantFollowSec (15 s) since the assist last achieved a
        // new closest-distance to the chase target, escalate to CantFollow.
        // sinceBest is progress-toward-target, not raw displacement — it
        // doesn't reset on geometry-grinding drift the way TickNavActiveTimeout
        // can. By the time it reaches 15 s, the watchdog's own recovery
        // paths (unstuck attempt at 4 s, route refill at 6 s) have failed
        // to make progress, so the obstacle is unrecoverable and the leader
        // should retrieve the assist.
        TickChaseWatchdog();
        if (_navState != NavState.NavigatingToLeader)
            return;

        LeaderState? leader = leaderConnection.LastLeaderState;

        if (leader == null || leaderConnection.LocalAgeMs > leaderConnection.StaleThresholdMs)
        {
            logger.LogWarning(
                $"[FFG] NavigatingToLeader: leader state stale/missing " +
                $"(localAge={leaderConnection.LocalAgeMs:0}ms) — holding current waypoint.");
            assistStatusProvider.CurrentStatus = BotStatus.NavigatingToLeader;
            navigation.Update(CancellationToken.None);
            wait.Update();
            return;
        }

        float dist = playerReader.WorldPos.WorldDistanceXYTo(leader.WorldPos);

        // Within FollowingMaxYards → transition to Idle, post Following.
        if (dist < FollowingMaxYards && !navigation.IsApproachEscapeActive)
        {
            logger.LogInformation(
                $"[FFG] Reached follow position (dist={dist:0.0}y < {FollowingMaxYards}y) — entering Idle.");
            navigation.Stop();
            input.StopForward(true); // explicitly stop — prevents momentum carry-through
            ResetNavState();
            EnterState(NavState.Idle);
            assistStatusProvider.CurrentStatus = BotStatus.Following;
            assistStatusProvider.CantFollow = false;
            return;
        }

        // Within NavigatingExitYards (10y) — report Following so the leader knows
        // the assist is close enough and can resume patrol if it was paused.
        // DO NOT stop navigation or transition to Idle.
        //
        // Stopping here causes a stop-start leapfrog:
        //   1. Assist stops at 10y, sets Following.
        //   2. Leader (paused by distance gate) resumes — now running at ~7y/s.
        //   3. Assist is stationary: gap grows 10y → 14y in ~0.57s.
        //   4. Assist re-engages NavigatingToLeader at 14y, but leader is still running.
        //   5. Assist path through the mesh is ≥ leader's direct route → gap keeps growing.
        //   6. Gap hits 20y → leader pauses again → cycle repeats every 2–4 seconds.
        //
        // By keeping navigation active, both bots run at the same speed. The gap
        // holds at ~10y, shouldPause stays false (10y < LeaderPauseYards=20y), and
        // the leader never needs to pause. The natural exit is via FollowingMaxYards
        // (7y) when the leader actually stops, giving 13y of clean runway before the
        // pause threshold.
        if (dist < NavigatingExitYards && !navigation.IsApproachEscapeActive)
        {
            if (assistStatusProvider.CurrentStatus != BotStatus.Following)
            {
                logger.LogInformation(
                    $"[FFG] Within follow range ({dist:0.0}y < {NavigatingExitYards}y) — reporting Following, keeping navigation active.");
                assistStatusProvider.CurrentStatus = BotStatus.Following;
                // NOTE: Do NOT clear assistStatusProvider.CantFollow here.
                // This branch fires while the assist is still actively navigating
                // (see comment block above — we explicitly keep navigation active
                // between 7 y and 10 y to avoid the stop-start leapfrog). Clearing
                // the CantFollow override at this point removes the only thing
                // keeping FFG selectable during evade recovery while dmgDone is
                // still latched from a recent CombatGoal cast — the planner then
                // returns NO PLAN (CombatGoal is blocked by evadeRecovery=false,
                // TFT by _evadeRecoveryActive, FFG by assistshouldfollow=false),
                // GoapAgent never calls FFG.OnExit, and the in-flight movement
                // keys (W/Right/Left from navigation.Update) stay pressed for the
                // remainder of the recovery window. Observed in log 28:
                //   01:23:07:469  this branch fired, CantFollow cleared
                //   01:23:07:484  NO PLAN with dmgDone=True, evadeRecovery=True
                //   01:23:07:484–30:716  total log silence, assist runs forward
                //   01:23:30:844  Combat selected, FFG.OnExit finally fires
                // CantFollow is correctly cleared in two other places:
                //   - line 535 (UpdateIdle, dist < FollowingMaxYards=7y)
                //   - line 707 (UpdateNavigatingToLeader, dist < FollowingMaxYards
                //              and entering Idle)
                // Both fire only when the assist has truly settled — those are
                // the right moments to drop the override.
            }
            // Fall through — navigation continues; no stop, no Idle transition.
        }

        // Active-navigation stuck reporting. May override the Following/NavigatingToLeader
        // status set above to BotStatus.Stuck if the assist has stopped making progress.
        // The leader's ShouldLeaderPauseForAssist already gates on Status==Stuck (line 161
        // of AssistStateStore), so reporting here is sufficient — no additional API change
        // required. See log 21 (assist 00:11:03–00:11:15) for the symptom this fixes.
        TickActiveStuckDetection();

        // Update navigation target.
        // Waypoint-sharing mode: when the rendezvous has been confirmed and the leader
        // is patrolling with a published waypoint, navigate to the SAME waypoint rather
        // than chasing the leader's live position. This eliminates the systematic gap
        // growth caused by the assist's pather finding a slightly-longer path to a
        // moving target — both bots navigate to the same endpoint so their paths converge.
        //
        // Position-chasing mode (fallback): when leader is in combat, looting, resting,
        // or rendezvous has not yet been confirmed, chase the leader's body directly so
        // the assist automatically follows any unplanned detour.
        Vector3 currentNavigationTarget = GetNavigationTarget(leader);
        float navTargetDrift = currentNavigationTarget.WorldDistanceXYTo(_lastNavigatedToLeaderWorldPos);

        // Mode-change diagnostic — fires whenever GetNavigationTarget switches between
        // Anchor / PositionChase / WaypointSharing. Independent of the drift gate
        // below: a non-Update caller (StartNavigatingToLeader, dead-band refresh,
        // rewind path) may have already SetSingleWaypoint on the new target before
        // we get here, leaving drift ≈ 0 on this tick. Without separating these
        // concerns, the mode change would be silently lost. Within-mode drift
        // updates do NOT log (chase-mode target moves with leader every tick); only
        // mode transitions log, so spam is bounded by the rate of approach/patrol
        // phase changes (~1 per few seconds in practice).
        if (_currentNavTargetMode != _lastLoggedNavTargetMode)
        {
            logger.LogInformation(
                $"[FFG] Nav target mode change: {_lastLoggedNavTargetMode} → {_currentNavTargetMode} " +
                $"(target={currentNavigationTarget}, drift={navTargetDrift:0.0}y).");
            _lastLoggedNavTargetMode = _currentNavTargetMode;
        }

        if (navTargetDrift > WaypointUpdateThresholdYards)
        {
            // Path-preservation suppression: when the new navigation target is roughly
            // aligned with the bot's current direction of travel AND further away than
            // the bot's current waypoint, the existing route is still a valid prefix of
            // the route to the new target. Re-running the pather only to lengthen the
            // route by ~12y at the end is wasteful and visibly disruptive:
            // SetSingleWaypoint discards the existing route, the bot momentarily idles
            // waiting for the new path, and the new path's routeTop is often in a
            // slightly different direction than the previous, producing a visible body
            // rotation as the bot turns toward the new heading.
            //
            // Originally added in session 19 for WaypointSharing mode to absorb the
            // leader's per-pop waypoint advances during patrol. Broadened in session 20
            // to cover Anchor and PositionChase modes after observing the same path-
            // discard storm during ATG/PTG cycles:
            //
            //   Log 20 (assist 23:28:07–23:28:14): leader cycled ATG#1 → PTG#1 → ATG#2
            //   → PTG#2 → Combat in 7 seconds. Each Has* toggle flipped the assist's
            //   nav mode, and each flip with drift > 3y discarded the existing path.
            //   Combined with intra-PositionChase drift updates as the leader's body
            //   moved 3-4y per tick, the assist accumulated 5 path rebuilds in 2
            //   seconds. Each rebuild started from the bot's current (already-SW)
            //   position, producing yet another route that began SW. The bot wandered
            //   SW for ~6s before stalling and another ~8s correcting NE before
            //   reaching the leader, where 2 seconds of direct travel would have
            //   sufficed. The user described this as "ran off in opposite direction
            //   before correcting".
            //
            // The geometric principle (existing route is a valid prefix when the new
            // target is in the same general direction, only further) holds regardless
            // of which mode produced the new target. Direction changes (cos < 0.7,
            // e.g. leader pivots, route wraps, or genuine rendezvous) still fail the
            // angle test and refresh as before.
            //
            // Trade-off in PositionChase steady-state: with suppression the bot
            // follows leader's position-from-a-few-seconds-ago instead of leader's
            // live position. When the bot reaches the old position via
            // OnDestinationReached, GetNavigationTarget refreshes to the latest
            // chase point. Net: the bot trails by roughly the distance the leader
            // moves during one nav phase rather than constantly re-pathing. Better
            // than rebuild storms; reactive enough for normal patrol following.
            bool suppressRefresh = false;
            if (navigation.HasWaypoint())
            {
                // ── Fix AZ (log-82 19:06:54:442 → 19:06:55:229) ──
                //
                // ROUTEESCAPE OWNERSHIP OF THE WAYPOINT
                //
                // When Navigation's RouteEscape state machine is mid-execution
                // (IsRouteEscapeActive=true), the current waypoint is owned by
                // RouteEscape — it's the tactical escape target (10y at 0°, 20y
                // at +120°, or 30y at −120° from the bot's facing direction at
                // attempt-start). RouteEscape sets the waypoint via
                // SetSingleWaypoint(escapeW) when an attempt fires (Navigation
                // TryRouteUnstuck line ~2720) and expects to retain it for the
                // duration of the attempt (up to RouteEscapeNoMovementSec=3.0s
                // or per-yard timeout).
                //
                // The alignment check below was designed for normal patrol /
                // leader-pursuit transitions where the new target is in roughly
                // the same direction as the old waypoint (just further along).
                // Escape targets are INTENTIONALLY in a different direction —
                // they aim away from the obstacle that caused the stuck, which
                // is by definition not aligned with the leader. So the cos > 0.7
                // test ALWAYS fails for escape targets, suppressRefresh stays
                // false, and FFG overwrites the escape route on its next drift-
                // gate tick (~250-700ms cadence).
                //
                // Evidence (log-82, mob N post-combat):
                //   19:06:54:442  Navigation: RouteEscape attempt 30y →
                //                 <-757.52, -4265.19>. offset=-120°, target NW.
                //                 SetSingleWaypoint(escapeW). _routeEscapeActive
                //                 = true.
                //   19:06:54:443  SetWayPoints(count=1) — wpTop=<-757.52, -4265.19>.
                //   19:06:54:473  REFILL fires with escape target.
                //   19:06:54:503  Path 130 computed: pathLen=33 (long curve around
                //                 the hill SE of the bot). routeCount=5 after
                //                 SimplifyRouteToWaypoint.
                //   19:06:54:504  ROUTESET reqId=130. Bot starts walking along curve
                //                 — Right arrow pressed 664ms.
                //   19:06:55:229  SetWayPoints(count=1) AGAIN — wpTop=<-735.20,
                //                 -4288.27>, the 7y leader-pursuit target. Curve
                //                 escape OVERWRITTEN after 787ms.
                //   19:06:55:245  Path 131 computed (pathLen=15, due east toward
                //                 leader). Bot walks east — Left arrow 858ms.
                //   19:06:58:878  Stuck AGAIN at <-740.87, -4290.86> (same spot
                //                 as the original stuck, 1y away from start of
                //                 the 30y curve).
                //
                // User observation: "as it cleared the hill going in a curve, the
                // assist moved right back to where it was." Verified: the 30y
                // curve had pathLen=33 (winding around the obstacle), bot got
                // 787ms of movement along it before FFG yanked the wp back to
                // leader-pursuit, then walked straight back into the same
                // stuck terrain.
                //
                // Fix: when navigation.IsRouteEscapeActive=true, suppress the
                // refresh unconditionally. The escape route runs to completion
                // (target reached → IsRouteEscapeActive=false naturally) or to
                // failure (RouteEscape's own no-progress/timeout logic in
                // TryRouteUnstuck escalates 10y→20y→30y, or
                // "all pather attempts exhausted" → ResetRouteEscape, also
                // clearing IsRouteEscapeActive). Either way, FFG resumes normal
                // refresh cadence as soon as RouteEscape relinquishes ownership.
                //
                // Why this is additive (doesn't break existing logic):
                //   - The alignment-based check only fires for leader-pursuit /
                //     anchor / waypoint-sharing scenarios where the bot is
                //     actively navigating toward the leader. Those scenarios
                //     don't intersect with RouteEscape (mutually exclusive: bot
                //     is either escaping OR pursuing).
                //   - During RouteEscape, the leader-pursuit "currentNavigation-
                //     Target" still gets computed each tick (line ~1910), but
                //     suppression prevents it from being applied. _lastNavigated-
                //     ToLeaderWorldPos still gets updated below so the drift
                //     baseline stays fresh; when escape ends, the next tick
                //     uses the current leader position naturally without
                //     re-triggering the drift gate from a stale baseline.
                //
                // Inter-attempt gap: _routeEscapeActive briefly flips to false
                // between attempts (after one attempt's no-progress/timeout but
                // before the next attempt fires at +200ms cooldown). During that
                // ~1-2 second window FFG may overwrite the stale wp with leader-
                // pursuit. Acceptable because the next escape attempt overwrites
                // it back, and the brief leader-pursuit doesn't have time to
                // route the bot far before the escape resumes. Could be tightened
                // with a separate "IsRouteEscapeInProgress" (includes inter-
                // attempt) flag if logs show it matters.
                if (navigation.IsRouteEscapeActive)
                {
                    suppressRefresh = true;
                    logger.LogDebug(
                        $"[FFG] [FIX-FIRE] AZ: Path-preservation suppression (RouteEscape active): " +
                        $"existing wp at {navigation.TopWaypointW} is owned by " +
                        $"Navigation's RouteEscape. Suppressing FFG nav refresh " +
                        $"until escape completes (target reached or all attempts " +
                        $"exhausted).");
                }
                else
                {
                    Vector3 existingWp = navigation.TopWaypointW;
                    Vector3 botPos = playerReader.WorldPos;

                    float ax = existingWp.X - botPos.X;
                    float ay = existingWp.Y - botPos.Y;
                    float nx = currentNavigationTarget.X - botPos.X;
                    float ny = currentNavigationTarget.Y - botPos.Y;

                    float aLen = MathF.Sqrt(ax * ax + ay * ay);
                    float nLen = MathF.Sqrt(nx * nx + ny * ny);

                    // aLen > 0.001 guards against the degenerate case where the bot has
                    // already arrived at the existing waypoint (zero-length vector → cos
                    // undefined). nLen >= aLen ensures the new target really is "further
                    // along" — if the new target is closer to the bot than the existing
                    // waypoint, the existing route overshoots and should be refreshed
                    // (e.g., leader pivoted and now is between bot and old wpTop).
                    if (aLen > 0.001f && nLen > 0.001f && nLen >= aLen)
                    {
                        float cosAngle = (ax * nx + ay * ny) / (aLen * nLen);
                        if (cosAngle > 0.7f)
                        {
                            suppressRefresh = true;
                            logger.LogDebug(
                                $"[FFG] Path-preservation suppression ({_currentNavTargetMode}): " +
                                $"existing wp at {existingWp} ({aLen:0.0}y) still aligned with " +
                                $"new target {currentNavigationTarget} ({nLen:0.0}y), cos={cosAngle:0.00}. " +
                                "Keeping existing route.");
                        }
                    }
                }
            }

            // Always update _lastNavigatedToLeaderWorldPos — even on suppression — so the
            // next drift comparison uses the latest target as the baseline. If we left
            // it stale, the drift gate would re-fire on every tick (the new target is
            // still > 3y from the historical _lastNavigatedToLeaderWorldPos).
            _lastNavigatedToLeaderWorldPos = currentNavigationTarget;

            if (!suppressRefresh)
            {
                SetWaypointLoopGuarded(currentNavigationTarget); // world coords — SetWayPoints detects non-map range
            }
        }

        // Record position for TryUnstuck direction.
        // NOTE: RecordApproachPosition is intentionally NOT called here.
        // That call is for mob-approach scenarios (ATG/PTG) where TryUnstuck()
        // needs a direction vector toward the mob. Calling it during leader
        // navigation records the direction of travel toward the leader, so when
        // TryUnstuck() fires it projects a 10y escape *further away from the
        // leader*, overshooting by 47+ yards and then adding a stuck rect that
        // causes blacklist detours for the rest of the run.
        // Navigation.Update() already handles route stuck recovery internally
        // via the chase watchdog and TryRouteUnstuck().
        navigation.Update(CancellationToken.None);

        // A navigation event (OnDestinationReached / OnWayPointReached) may have
        // fired during Update() and transitioned the state to Idle. Return
        // immediately to avoid overwriting the status set by the callback and to
        // avoid running stale NavigatingToLeader logic on an already-Idle state.
        if (_navState != NavState.NavigatingToLeader)
            return;

        // ── Co-located terrain fallback ─────────────────────────────────────
        if (!navigation.HasWaypoint() && !navigation.HasNext() && !navigation.IsApproachEscapeActive
            && dist > NavigatingMinYards)
        {
            // ── Turn 3.5b fix (log-87 evidence) ──
            // If we're in RouteWalk mode and navigation has popped our route
            // waypoint (because IsAtFinalWaypoint at 3.35y reach fires when
            // bot arrives), the fallback below would call GetNavigationTarget,
            // get the same route waypoint back, see it's co-located, fall back
            // to body-chase, and SetWaypoint. On the next tick the same thing
            // happens. After 10 sets in 100ms the SetWaypoint loop guard
            // escalates to CantFollow → AB-2 projects away from leader.
            //
            // Sister of the OnDestinationReached fix (~line 5000). Same
            // failure mode, same resolution: when route-walking, the popped
            // route waypoint means "I'm parked at my trailing-by-one cap."
            // Skip the fallback entirely. The next leader pop refreshes the
            // cache, allowedAdvance advances, and the next time
            // GetNavigationTarget runs in the main FFG loop the new target
            // will be > POP_DIST away (drift gate fires SetWaypoint).
            //
            // Silent: this branch can fire many times per second while
            // parked. Logging every tick would spam. The mode flag
            // _currentNavTargetMode = RouteWalk, set by the most recent
            // GetNavigationTarget call, is the diagnostic record.
            if (_currentNavTargetMode == NavTargetMode.RouteWalk)
            {
                return;
            }

            logger.LogWarning(
                "[FFG] No active waypoint but still far from leader — refreshing waypoint.");
            Vector3 fallbackTarget = GetNavigationTarget(leader);

            // Same co-located guard as Navigation_OnDestinationReached: if the target is
            // within POP_DIST, setting it would produce an immediate pop with no movement.
            float fallbackDist = playerReader.WorldPos.WorldDistanceXYTo(fallbackTarget);
            if (fallbackDist < Navigation.POP_DIST)
            {
                logger.LogWarning(
                    $"[FFG] Fallback target co-located ({fallbackDist:0.0}y < {Navigation.POP_DIST}y) — " +
                    "stale shared waypoint; reverting to position-chasing.");
                _rendezvousConfirmed = false;
                _lastSharedWaypointW = default;
                fallbackTarget = ComputeFollowTargetWorldPos(leader);
            }

            _lastNavigatedToLeaderWorldPos = fallbackTarget;
            // log-36: if SetWaypointLoopGuarded escalates to CantFollow (10
            // consecutive stationary sets within 100ms, e.g. unreachable
            // blacklisted target), the line ~1023 status assignment below
            // would otherwise overwrite the CantFollow status back to
            // NavigatingToLeader. Bail out early on escalation.
            if (!SetWaypointLoopGuarded(fallbackTarget))
                return;
        }

        // Guard against overwriting Following — set by the NavigatingExitYards block
        // above (when dist < 10y) or by nav callbacks before the _navState guard above.
        // Without this guard, the assignment runs every tick and resets Following →
        // NavigatingToLeader, causing the NavigatingExitYards block to re-log and
        // re-set Following on every single tick (log spam + HTTP noise).
        if (assistStatusProvider.CurrentStatus != BotStatus.Following)
        {
            assistStatusProvider.CurrentStatus = BotStatus.NavigatingToLeader;
        }

        wait.Update();
    }

    // -----------------------------------------------------------------------
    // State: CantFollow — actively try to escape via 10y/20y/30y projection
    // away from rect center, then LastSafeAnchor, then physical unstuck.
    // See the CantFollowEscapePhase enum block (~line 195) for the full
    // design rationale.
    // -----------------------------------------------------------------------
    private void UpdateCantFollow()
    {
        assistStatusProvider.CurrentStatus = BotStatus.CantFollow;

        // Fix 31 (log-52 17:38:41 → end-of-log): the previous code called
        // navigation.Stop() unconditionally on every UpdateCantFollow tick.
        // Stop() always emits a NAV-DIAG LogInformation line, so once the
        // assist entered CantFollow the log filled with ~60 Stop() lines
        // per second. Navigation is stopped on entry to CantFollow (see
        // EnterCantFollow ~line 1739) and the OnEnter CantFollow re-entry
        // branch (~line 445) defensively Stops if another goal had it
        // active. Per-tick Stop() removed.
        //
        // Fix 32 — CantFollow is no longer pure hold; the escape state
        // machine actively drives navigation toward 10/20/30 y projection
        // targets away from the rect center, falls back to
        // LastSafeAnchor, then a physical jump+reverse unstuck. The
        // existing leader-arrived-within-6 y exit (below) and the 120 s
        // CantFollowTimeoutSec retry are both preserved; the escape just
        // adds a more active recovery path during the wait period.

        LeaderState? leader = leaderConnection.LastLeaderState;
        bool leaderFresh = leader != null && leaderConnection.LocalAgeMs <= leaderConnection.StaleThresholdMs;

        double cantFollowSec = (DateTime.UtcNow - _cantFollowEnteredUtc).TotalSeconds;

        // Existing exit: leader arrived within 6 y.
        if (leaderFresh)
        {
            float dist = playerReader.WorldPos.WorldDistanceXYTo(leader!.WorldPos);

            if (dist <= LeaderArrivedYards)
            {
                logger.LogInformation(
                    $"[FFG] CantFollow: leader arrived ({dist:0.0}y) — resuming navigation.");
                assistStatusProvider.CantFollow = false;
                ResetEscapeState();
                ResetNavState();
                StartNavigatingToLeader(leader);
                return;
            }
        }

        // Fix 32 — new exit: segment from assist's current position to
        // the leader no longer crosses any BL rect. This is the natural
        // "reunite" condition — the path-finding obstruction that put us
        // in CantFollow is gone, so resume normal NavigatingToLeader
        // even though we're still > 6 y from the leader.
        //
        // Fix 33 (log-53 21:22:43:657 → 21:22:47+: assist flapped
        // CantFollow ↔ NavigatingToLeader every ~300 ms for 4+ seconds at
        // <945.5, 295> while leader was at <948.4, 289.4> in combat with
        // a BL mob; 26 SetWaypoint loop-guard escalations, 188 immediate
        // re-exits): the original Fix 32 condition used TryGetBlockingRect
        // (STRICT rect crossing only) for segment-clear, but the
        // position-chase code at line 1903 uses TryGetContainingRectInflated
        // with a 6 y safety margin. When the leader is near a rect edge,
        // the segment from assist to leader can be clear of strict
        // crossing while the standard follow target (3 y short of the
        // leader along the leader→assist line) lands inside the 6 y
        // inflated zone — position-chase then projects the target toward
        // the assist, lands ~1 y from the assist, SetWayPoint loop guard
        // fires after 10 stationary sets, CantFollow re-fires the next
        // tick. Cycle every ~300 ms.
        //
        // Fix: require BOTH (a) segment to leader doesn't cross a strict
        // rect AND (b) the standard follow target wouldn't itself trigger
        // the position-chase projection. Use the same 6 y inflated check
        // (ProjectionSafetyMarginYards) that position-chase uses, so we
        // know NavigatingToLeader can run for at least one tick without
        // immediately re-escalating to CantFollow.
        if (leaderFresh && navigation.AreaBlacklist != null)
        {
            Vector3 assistPos = playerReader.WorldPos;
            Vector3 leaderPos = leader!.WorldPos;
            bool segmentClear = !navigation.AreaBlacklist.TryGetBlockingRect(
                assistPos, leaderPos, out _);

            bool followTargetClear = true;
            if (segmentClear)
            {
                // Compute the natural follow target the same way
                // ComputeFollowTargetWorldPos does (line 1873-1893):
                // leader's position shifted FollowStopShortYards toward
                // the assist. If this point falls within 6 y of any
                // inflated rect, position-chase will project it on the
                // very next tick and re-trigger CantFollow.
                float dx = assistPos.X - leaderPos.X;
                float dy = assistPos.Y - leaderPos.Y;
                float lenXY = MathF.Sqrt(dx * dx + dy * dy);
                Vector3 followTarget;
                if (lenXY < 0.001f)
                {
                    followTarget = new Vector3(leaderPos.X, leaderPos.Y, 0f);
                }
                else
                {
                    float invLen = 1f / lenXY;
                    followTarget = new Vector3(
                        leaderPos.X + dx * invLen * FollowStopShortYards,
                        leaderPos.Y + dy * invLen * FollowStopShortYards,
                        0f);
                }

                const float ProjectionSafetyMarginYards = 6.0f;
                followTargetClear = !navigation.AreaBlacklist.TryGetContainingRectInflated(
                    followTarget, ProjectionSafetyMarginYards, out _);
            }

            if (segmentClear && followTargetClear)
            {
                float dist = assistPos.WorldDistanceXYTo(leaderPos);
                logger.LogInformation(
                    $"[FFG] CantFollow: segment to leader is now clear ({dist:0.0}y, " +
                    $"no BL rect blocks, follow target outside inflated zones) — resuming navigation.");
                assistStatusProvider.CantFollow = false;
                ResetEscapeState();
                ResetNavState();
                StartNavigatingToLeader(leader);
                return;
            }
        }

        // ── Fix AO (log-78 02:41:21:328 → end-of-log): cumulative-displacement exit
        //
        // Evidence: assist failed to path 36 y NE to leader's waypoint top
        // (Fix AC+AE+AI+AL rejected a 107-raw-node, 11-simplified path with
        // 172.6° rear angle — genuine navmesh wraparound). Entered CantFollow
        // at <-707.89, -4281.00> at 02:41:21:328. Leader detected CantFollow
        // rising edge, broadcast AssistRequestReturn, navigated to assist's
        // last position (<-710.68, -4280.07>), arrived at 02:41:23:812, then
        // PAUSED waiting for assist to report Following.
        //
        // Meanwhile, the assist's CantFollow escape ran Fix AB-2 "directional
        // fallback projection 10y, basis=away-from-leader." Each Projection10
        // cycle recomputed direction using the leader's CURRENT (stationary)
        // position. Leader stayed west; projection stayed east. Across 23
        // cycles in 49 s, assist moved <-707.89, -4281.00> → <-531.31, -4313.57>
        // — 177 y east of leader. The log ended with the escape still running;
        // the only exit that would have fired was the 120 s CantFollowTimeoutSec.
        //
        // Why the existing exits didn't fire:
        //   - Leader-arrived (6 y): leader paused, assist running away —
        //     gap monotonically widened. Never triggers.
        //   - Segment-clear-of-BL: gated by `navigation.AreaBlacklist != null`;
        //     on the assist side AreaBlacklist is null (no blacklist data
        //     loaded), so the entire block is skipped regardless of position.
        //   - 120 s timeout: would eventually fire but lets the assist
        //     wander 200+ y before retrying.
        //
        // Root cause: the Fix AB-2 design rationale (line ~2585-2587) assumes
        // "The leader, via AssistRequestReturn, is navigating TOWARD the
        // assist, so even slight backward displacement doesn't hurt the
        // rendezvous — the leader catches up." When the leader's AssistReturn
        // completes and the leader PAUSES, this assumption breaks: the leader
        // has already caught up, but the assist's escape keeps recomputing
        // "away from leader" and recedes further.
        //
        // Fix: cap cumulative displacement from the CantFollow entry position
        // at MaxEscapeDisplacementYards (20 y). When exceeded, exit CantFollow
        // → NavigatingToLeader. From the new position, navigation re-attempts
        // pathing to the leader's CURRENT position (whatever it is now). One
        // of three things happens:
        //   (a) Path succeeds (most common when bot has escaped the original
        //       dead-zone; in log-78's case, a path WEST to the paused
        //       leader is geometrically different from the rejected NE path
        //       and likely navigable). Assist returns to leader. ✓
        //   (b) Path fails identically. CantFollow re-fires with
        //       _cantFollowEnteredPos updated to the new spot. Escape starts
        //       fresh, bounded again. Self-limiting.
        //   (c) Bot has actually walked PAST the rendezvous (rare). Leader
        //       moves toward bot when assist reports Following or via
        //       subsequent AssistReturn cycles.
        //
        // This is additive — it does not change leader-arrived, segment-clear,
        // or 120 s timeout behavior. The only new outcome is "give up on
        // unbounded escape after 20 y and let normal navigation retry."
        if (leaderFresh && _cantFollowEnteredPos != default)
        {
            float displacementFromEntry =
                playerReader.WorldPos.WorldDistanceXYTo(_cantFollowEnteredPos);
            if (displacementFromEntry > MaxEscapeDisplacementYards)
            {
                float distToLeader =
                    playerReader.WorldPos.WorldDistanceXYTo(leader!.WorldPos);
                logger.LogWarning(
                    $"[FFG] [FIX-FIRE] AO: cumulative escape displacement {displacementFromEntry:0.0}y " +
                    $"exceeded cap ({MaxEscapeDisplacementYards}y) from entry at {_cantFollowEnteredPos}. " +
                    $"Exiting CantFollow to re-attempt navigation to leader (currently {distToLeader:0.0}y " +
                    $"at {leader.WorldPos}). If path still fails, CantFollow will re-fire from current position.");
                assistStatusProvider.CantFollow = false;
                ResetEscapeState();
                ResetNavState();
                StartNavigatingToLeader(leader);
                return;
            }
        }

        // Existing 120 s timeout — retry navigation in case the leader moved.
        if (cantFollowSec >= CantFollowTimeoutSec)
        {
            logger.LogWarning(
                $"[FFG] CantFollow timed out after {cantFollowSec:0.0}s — " +
                "retrying navigation in case leader moved.");
            ResetEscapeState();
            if (leader != null)
                StartNavigatingToLeader(leader);
            else
                EnterState(NavState.Idle);
            return;
        }

        // Fix 32 — drive the active-escape state machine.
        DriveCantFollowEscape();

        // Fix AF (log-72 16:09:15:518 → 16:09:35:198+, assist held in
        // CantFollow at <-699.85, -4170.15> for 20+ seconds during a leader
        // fight; user described "it didn't appear the assist was actually
        // stuck, plenty of free movement space on almost all sides"):
        //
        // Evidence from log-72:
        //   16:09:15:518  SetWayPoints(count=1) for Projection10 target
        //                 <-705.69, -4178.27>. Navigation.active flipped True.
        //   16:09:15:519 → 16:09:18:526   ZERO NAV-DBG, NAV-EMPTY, NAV-REFILL,
        //                                 NAV-DBG ENQUEUE PATH, or
        //                                 PathCalculatedCallback entries.
        //                                 Navigation was simply never ticked.
        //   16:09:18:526  Projection10 elapsed 3.0s with 0.0y displacement.
        //   (same pattern repeated for Projection20, Projection30, then
        //   after PhysicalUnstuck moved the bot 2.4y via direct keyboard
        //   input, a fresh Projection10 from the new position again
        //   produced 0.0y over the next 3s window.)
        //
        // Root cause: navigation.Update() is called from FFG only inside
        // UpdateNavigatingToLeader (line ~1142 and ~1349). When _navState
        // is CantFollow, UpdateCantFollow drives the escape state machine
        // via StartProjectionPhase → navigation.SetSingleWaypoint(target),
        // which clears the route and flips active=True — but Navigation is
        // never given a tick to process that change. No path request is
        // enqueued, no MOVE event fires, no input key is pressed. The bot
        // sits at the projection's start position for the entire 3 s phase
        // budget, "0.0y displacement" is observed, and the state machine
        // escalates to the next phase, where the cycle repeats.
        //
        // Why this only surfaced after Fix AE: before Fix AE, the spurious
        // CantFollow entries caused by Fix AC false-positives were short
        // (assist re-rescued by leader within ~5-10s), so the broken
        // projection escape rarely got a chance to matter. Fix AE
        // eliminated the false-positive entries; the CantFollow entries
        // that remain are legitimate, the leader is typically engaged in
        // combat and not returning quickly, and the projection escape
        // actually needs to do its job. It can't.
        //
        // Fix: tick Navigation on every UpdateCantFollow call, after the
        // escape state machine has had a chance to install a new waypoint.
        //   - During Projection10/20/30 and LastSafeAnchor phases, active
        //     is True (set by SetSingleWaypoint via SetWayPoints). Update
        //     processes the route, refills via path request, drives the
        //     bot toward the route top via input keys.
        //   - During PhysicalUnstuck and Exhausted phases, both
        //     StartPhysicalUnstuckPhase (~line 1775) and the Exhausted
        //     branch in StartEscapePhase (~line 1666) call
        //     navigation.Stop(), which sets active=False. Navigation.Update
        //     has `if (!active) return;` at its top (~line 1137), so this
        //     call is a safe no-op during those phases.
        //   - The leader-arrived exit and the BL-segment-clear exit at the
        //     top of UpdateCantFollow both `return` before reaching here,
        //     so this call doesn't fire on transition-out ticks.
        //   - All Navigation event handlers in FFG that could fire from
        //     this Update (Navigation_OnPathFailed,
        //     Navigation_OnDestinationReached, Navigation_OnWayPointReached,
        //     and the Option B policy handlers Navigation_OnPathResultInspected,
        //     Navigation_OnStaleEmptyPathMatchesActive,
        //     Navigation_OnRepeatedNoPathDirectRouteDecision) guard on
        //     `_navState != NavState.NavigatingToLeader` and no-op during
        //     CantFollow. This is correct: a "rear-curving" projection
        //     path means the pather found a route back toward the leader,
        //     which is *useful* during an escape — Fix AC+AE must not
        //     reject it.
        navigation.Update(CancellationToken.None);

        wait.Update();
    }

    private void DriveCantFollowEscape()
    {
        switch (_escapePhase)
        {
            case CantFollowEscapePhase.NotStarted:
                StartEscapePhase(CantFollowEscapePhase.Projection10);
                break;

            case CantFollowEscapePhase.Projection10:
            case CantFollowEscapePhase.Projection20:
            case CantFollowEscapePhase.Projection30:
            case CantFollowEscapePhase.LastSafeAnchor:
                DriveActiveEscapePhase();
                break;

            case CantFollowEscapePhase.PhysicalUnstuck:
                // PhysicalUnstuck executes inline in StartEscapePhase and
                // immediately transitions onward — we should not normally
                // observe this state outside that call. Defensive: if we
                // do see it here, advance to the post-unstuck retry.
                _escapeUnstuckUsed = true;
                StartEscapePhase(CantFollowEscapePhase.Projection10);
                break;

            case CantFollowEscapePhase.Exhausted:
                // Hold position — nothing left to try. The leader-arrived
                // and 120 s timeout exits above remain the only paths out.
                break;
        }
    }

    private void DriveActiveEscapePhase()
    {
        Vector3 currentPos = playerReader.WorldPos;
        float displacement = currentPos.WorldDistanceXYTo(_escapePhaseStartPos);
        double phaseSec = (DateTime.UtcNow - _escapePhaseStartUtc).TotalSeconds;

        bool isProjectionPhase =
            _escapePhase == CantFollowEscapePhase.Projection10 ||
            _escapePhase == CantFollowEscapePhase.Projection20 ||
            _escapePhase == CantFollowEscapePhase.Projection30;

        double budgetSec = isProjectionPhase ? EscapeProjectionPhaseSec : EscapeAnchorPhaseSec;

        // Arrived check — within EscapeArrivalYards of the target, the
        // movement is essentially complete. Re-plan: pick a fresh
        // projection from the new position. (Don't escalate phase — if
        // 10 y got us somewhere, try another 10 y from here before
        // jumping to 20 y.)
        if (_escapeTargetW != default &&
            currentPos.WorldDistanceXYTo(_escapeTargetW) <= EscapeArrivalYards)
        {
            logger.LogInformation(
                $"[FFG] CantFollow escape: arrived at {_escapePhase} target {_escapeTargetW} " +
                $"(displacement {displacement:0.0}y). Re-planning from new position.");
            StartEscapePhase(CantFollowEscapePhase.Projection10);
            return;
        }

        // Phase timeout — escalate. If we made meaningful progress
        // (>= EscapeMinProgressYards) during this phase, restart from
        // Projection10 at the new position rather than escalating —
        // we're moving, just didn't reach the precise target.
        if (phaseSec >= budgetSec)
        {
            if (displacement >= EscapeMinProgressYards && isProjectionPhase)
            {
                logger.LogInformation(
                    $"[FFG] CantFollow escape: {_escapePhase} budget elapsed " +
                    $"({phaseSec:0.0}s) but made {displacement:0.0}y progress — " +
                    $"re-planning from new position.");
                StartEscapePhase(CantFollowEscapePhase.Projection10);
                return;
            }

            logger.LogWarning(
                $"[FFG] CantFollow escape: {_escapePhase} budget elapsed " +
                $"({phaseSec:0.0}s) with only {displacement:0.0}y displacement — escalating.");
            EscalateEscapePhase();
        }
    }

    private void StartEscapePhase(CantFollowEscapePhase phase)
    {
        _escapePhase = phase;
        _escapePhaseStartUtc = DateTime.UtcNow;
        _escapePhaseStartPos = playerReader.WorldPos;
        _escapeTargetW = default;

        switch (phase)
        {
            case CantFollowEscapePhase.Projection10:
                StartProjectionPhase(10f);
                break;

            case CantFollowEscapePhase.Projection20:
                StartProjectionPhase(20f);
                break;

            case CantFollowEscapePhase.Projection30:
                StartProjectionPhase(30f);
                break;

            case CantFollowEscapePhase.LastSafeAnchor:
                StartLastSafeAnchorPhase();
                break;

            case CantFollowEscapePhase.PhysicalUnstuck:
                StartPhysicalUnstuckPhase();
                break;

            case CantFollowEscapePhase.Exhausted:
                logger.LogWarning(
                    "[FFG] CantFollow escape: all phases exhausted — holding position " +
                    "until leader arrives or 120 s timeout fires.");
                navigation.Stop();
                break;
        }
    }

    private void StartProjectionPhase(float yards)
    {
        Vector3 target = ComputeAwayFromRectWaypoint(yards);
        if (target == default)
        {
            // We're not near any rect — the CantFollow shouldn't be
            // BL-driven. Fall through to LastSafeAnchor as the next
            // reasonable thing to try.
            logger.LogInformation(
                $"[FFG] CantFollow escape: Projection{yards:0} — not inside any inflated " +
                "rect; CantFollow may not be BL-driven. Falling through to LastSafeAnchor.");
            StartEscapePhase(CantFollowEscapePhase.LastSafeAnchor);
            return;
        }

        // Don't bother navigating to a target that's itself inside a
        // standard rect (we'd just relocate the problem).
        if (navigation.AreaBlacklist != null && navigation.AreaBlacklist.ContainsWorld(target))
        {
            logger.LogInformation(
                $"[FFG] CantFollow escape: Projection{yards:0} target {target} is " +
                "itself inside a rect — escalating.");
            EscalateEscapePhase();
            return;
        }

        _escapeTargetW = target;
        logger.LogInformation(
            $"[FFG] CantFollow escape: Projection{yards:0} → navigating to {target} " +
            $"(from {playerReader.WorldPos}, away from rect center).");
        navigation.SetSingleWaypoint(target);
    }

    private void StartLastSafeAnchorPhase()
    {
        if (!navigation.HasLastSafeAnchor)
        {
            logger.LogInformation(
                "[FFG] CantFollow escape: LastSafeAnchor — no anchor recorded. Escalating.");
            EscalateEscapePhase();
            return;
        }

        Vector3 anchor = navigation.LastSafeAnchorW;
        if (navigation.AreaBlacklist != null && navigation.AreaBlacklist.ContainsWorld(anchor))
        {
            logger.LogInformation(
                $"[FFG] CantFollow escape: LastSafeAnchor {anchor} is itself inside a " +
                "rect — escalating.");
            EscalateEscapePhase();
            return;
        }

        // Fix 33 (log-53: 188 lines of "arrived at LastSafeAnchor target
        // <X> (displacement 0.0y)" followed by "Projection10 — not inside
        // any inflated rect" → "LastSafeAnchor → navigating to <same X>",
        // cycling every ~15 ms): when CantFollow is not actually
        // BL-driven from the assist's side (assist outside all inflated
        // rects but follow target landed inside one because the LEADER is
        // near a rect), LastSafeAnchor was typically set to the assist's
        // last waypoint, which during steady-state position-chase equals
        // the assist's current position. Navigating to a co-located
        // anchor produces zero displacement, the DriveActiveEscapePhase
        // arrival check (within EscapeArrivalYards = 3.5 y) fires
        // immediately, restarts Projection10, which falls through to
        // LastSafeAnchor, repeating every tick. The assist is not stuck
        // — there is just nowhere to escape TO because the assist is
        // already in a safe position; the leader is the side that needs
        // to move.
        //
        // Fix: if the anchor is within EscapeArrivalYards of the assist's
        // current position, skip the navigate-to-anchor step (which would
        // arrive instantly and trigger the cycle). Try PhysicalUnstuck if
        // we haven't already, otherwise go to Exhausted to hold position
        // until the leader-arrived (6 y) or 120 s timeout exit fires.
        Vector3 currentPos = playerReader.WorldPos;
        float anchorDist = currentPos.WorldDistanceXYTo(anchor);
        if (anchorDist <= EscapeArrivalYards)
        {
            logger.LogInformation(
                $"[FFG] CantFollow escape: LastSafeAnchor {anchor} is co-located with " +
                $"current position ({anchorDist:0.0}y ≤ {EscapeArrivalYards:0.0}y) — " +
                "no movement would result. " +
                (_escapeUnstuckUsed
                    ? "PhysicalUnstuck already used, holding position (Exhausted)."
                    : "Trying PhysicalUnstuck."));
            if (_escapeUnstuckUsed)
                StartEscapePhase(CantFollowEscapePhase.Exhausted);
            else
                StartEscapePhase(CantFollowEscapePhase.PhysicalUnstuck);
            return;
        }

        _escapeTargetW = anchor;
        logger.LogInformation(
            $"[FFG] CantFollow escape: LastSafeAnchor → navigating to {anchor} " +
            $"({anchorDist:0.0}y away).");
        navigation.SetSingleWaypoint(anchor);
    }

    private void StartPhysicalUnstuckPhase()
    {
        logger.LogWarning(
            "[FFG] CantFollow escape: PhysicalUnstuck — jump + reverse + jump + reverse.");
        navigation.Stop();
        navigation.TryPhysicalUnstuck();
        _escapeUnstuckUsed = true;
        // Transition immediately back to Projection10 to retry from the
        // post-unstuck position. Don't recurse via StartEscapePhase here
        // — the next UpdateCantFollow tick will pick up Projection10.
        _escapePhase = CantFollowEscapePhase.Projection10;
        _escapePhaseStartUtc = DateTime.UtcNow;
        _escapePhaseStartPos = playerReader.WorldPos;
        _escapeTargetW = default;
    }

    private void EscalateEscapePhase()
    {
        switch (_escapePhase)
        {
            case CantFollowEscapePhase.Projection10:
                StartEscapePhase(CantFollowEscapePhase.Projection20);
                break;
            case CantFollowEscapePhase.Projection20:
                StartEscapePhase(CantFollowEscapePhase.Projection30);
                break;
            case CantFollowEscapePhase.Projection30:
                StartEscapePhase(CantFollowEscapePhase.LastSafeAnchor);
                break;
            case CantFollowEscapePhase.LastSafeAnchor:
                if (_escapeUnstuckUsed)
                    StartEscapePhase(CantFollowEscapePhase.Exhausted);
                else
                    StartEscapePhase(CantFollowEscapePhase.PhysicalUnstuck);
                break;
            default:
                StartEscapePhase(CantFollowEscapePhase.Exhausted);
                break;
        }
    }

    private void ResetEscapeState()
    {
        _escapePhase = CantFollowEscapePhase.NotStarted;
        _escapePhaseStartUtc = default;
        _escapePhaseStartPos = default;
        _escapeTargetW = default;
        _escapeUnstuckUsed = false;

        // Fix 33: reset the position-chase projection log dedup so the
        // next projection in a fresh state cycle (after plan re-entry,
        // CantFollow exit via segment-clear, leader-arrived exit, or
        // 120 s timeout) logs once. Same-target repeats during steady
        // operation are suppressed by the >3 y change threshold.
        _lastWarnedProjectionCandidateW = default;
    }

    /// <summary>
    /// Computes an escape waypoint <paramref name="yards"/> away from the
    /// nearest blacklist rect's center, projected through the assist's
    /// current position.
    ///
    /// <para>When no rect contains the assist (Fix AB-2), falls back to a
    /// direction-aware projection AWAY from the leader's body, with
    /// ±120° rotation per escalation level — so the Projection10/20/30
    /// escape phases sweep a ~240° arc and have a real chance of
    /// physically displacing the bot even in non-blacklisted zones
    /// (e.g., Durotar corridor stucks where PPather genuinely cannot
    /// navigate the terrain).</para>
    /// </summary>
    private Vector3 ComputeAwayFromRectWaypoint(float yards)
    {
        Vector3 pos = playerReader.WorldPos;

        // ── Path 1: rect-aware projection (original behaviour) ────────────
        if (navigation.AreaBlacklist != null)
        {
            // Inflate the containment check by the same margin the rest of
            // Navigation uses for detour candidates (DetourMargin * 0.5).
            // This catches the log-52 sliver case where the assist is just
            // outside the strict rect but inside the 6 y safety zone — the
            // pathing problem is the same, and the escape direction should
            // be the same.
            float searchInflation = navigation.DetourMargin * 0.5f;
            if (navigation.AreaBlacklist.TryGetContainingRectInflated(pos, searchInflation, out var rect))
            {
                float centerX = (rect.MinX + rect.MaxX) * 0.5f;
                float centerY = (rect.MinY + rect.MaxY) * 0.5f;

                float dx = pos.X - centerX;
                float dy = pos.Y - centerY;
                float magSq = dx * dx + dy * dy;
                float mag = MathF.Sqrt(magSq);

                if (mag < 0.001f)
                {
                    // Degenerate: assist is exactly at the rect center. Pick an
                    // arbitrary positive-X direction — any escape direction will
                    // do, and the next projection cycle will recompute from a
                    // non-degenerate position.
                    dx = 1f;
                    dy = 0f;
                    mag = 1f;
                }

                float invMag = 1f / mag;
                return new Vector3(
                    pos.X + dx * invMag * yards,
                    pos.Y + dy * invMag * yards,
                    pos.Z);
            }
        }

        // ── Path 2: Fix AB-2 directional fallback ─────────────────────────
        //
        // Fix AB-2 (log-68 11:03:43→11:04:25, assist stuck in Durotar
        // corridor between hill and tree; no blacklist exists for the
        // route so the original return-default fell through to
        // LastSafeAnchor):
        //
        // When no rect contains the assist (within the inflated search
        // zone), the original code returned default and StartProjectionPhase
        // logged "Projection{yards} — not inside any inflated rect;
        // CantFollow may not be BL-driven. Falling through to LastSafeAnchor."
        //
        // In log-68, LastSafeAnchor was 0.1y from the assist's current
        // position (set during a recent successful navigation step before
        // the stuck began). The co-located check in StartLastSafeAnchorPhase
        // (line ~1714) then jumped to PhysicalUnstuck — fired ONCE — and
        // afterwards the second Projection10 attempt repeated the same
        // fall-through pattern, this time landing on an anchor 5.8y away
        // (now reachable in principle) but PPather still failed to route
        // there. After 8s of no progress all phases were exhausted, and
        // the bot held position for another ~16s waiting for the leader.
        //
        // The mechanism missing was directional waypoint-based escape
        // when no blacklist exists. With BL absent, the entire 10/20/30
        // projection escalation collapsed into a single PhysicalUnstuck
        // round.
        //
        // Fix: provide a fallback direction so the Projection phases can
        // still emit meaningful escape waypoints. Direction strategy —
        // AWAY from the leader's body (the unreachable target):
        //   - The leader is "where we want to go but can't reach".
        //   - Reversing the leader→assist vector points along the bot's
        //     natural back-step direction; whatever path the assist took
        //     to arrive at the stuck position is probably traversable in
        //     reverse.
        //   - The leader, via AssistRequestReturn, is navigating TOWARD
        //     the assist, so even slight backward displacement doesn't
        //     hurt the rendezvous — the leader catches up.
        //
        // Rotation by escalation level (mirrors Fix W in Navigation.cs):
        //   10y: 0°    — straight back along the leader→assist axis
        //   20y: +120° — 60° forward-and-side from straight back
        //   30y: -120° — 60° forward-and-side, opposite side
        // This sweeps ~240° around the bot so three attempts cover
        // most clear directions. If "straight back" is also obstructed
        // (corner stuck, etc.), one of the rotated attempts is more
        // likely to find clearance.
        //
        // Edge cases:
        //   - Leader missing/stale: use reversed player facing
        //     (-cos(facing), -sin(facing)). This is the "back away from
        //     whatever you were heading toward" heuristic.
        //   - Co-located with leader (degenerate, shouldn't reach
        //     CantFollow but defensive): same reversed-facing fallback.
        //
        // The projection target may itself be unreachable by PPather
        // (e.g., 30y backward lands in another impassable area). In
        // that case the escape phase's 3-second budget elapses with no
        // progress and EscalateEscapePhase fires normally. Behaviour is
        // strictly improved over the pre-fix collapse: we get up to 3
        // directional attempts where previously we got 0.
        // ── Turn 3.5c / Fix BD (log-88 evidence) ──
        // PREFER ROUTE WAYPOINT AS PROJECTION BASIS
        //
        // The original AB-2 basis was "away-from-leader" with the rationale
        // that the bot's arrival path is the safest reverse direction. That
        // assumption only holds when the arrival path itself was navmesh-
        // valid. In log-88's hill incident, the bot's arrival was a chain
        // of body-chase + previous AB-2 escapes through terrain whose
        // navmesh-collision agreement was poor. AB-2 then projected 10y
        // further away-from-leader, walking the bot deeper into the same
        // terrain class. Result: bot drifted UP a hill, ended completely
        // stuck at the hilltop where the WoW client's character collision
        // blocked all lateral movement; leader rescue was required.
        //
        // The LoadedRoute, by contrast, contains pre-validated terrain
        // — the route author walked it during creation, every waypoint
        // is by construction physically traversable, every leg between
        // adjacent waypoints is by construction walkable. Projecting
        // toward the nearest route waypoint heads the bot toward
        // known-good ground regardless of what terrain class the bot
        // is currently standing on.
        //
        // The ±120° rotations on subsequent escalation phases (20y, 30y)
        // still apply, but rotate around the toward-route axis rather
        // than the away-from-leader axis. If the direct route approach
        // is blocked by an obstacle, the ±120° sweep still gives three
        // angles to try.
        //
        // Fallback: if LoadedRoute is empty (no patrol route loaded) OR
        // the nearest route waypoint is co-located with the bot (within
        // 1.0y), keep the existing away-from-leader / reversed-facing
        // logic.
        //
        // Interaction with Fix BB: BB flips the direction TOWARD the
        // leader when (a) we were using the leader-direction basis AND
        // (b) CantFollow was caused by path-rejection AND (c) leader is
        // ≤ 25y. With Fix BD, the leader-direction branch fires only
        // when route is unavailable. So BB still applies its flip there.
        // When route IS available, BD's toward-route direction is
        // already pointed at known-good ground; BB's flip is unnecessary
        // (and is skipped because usingLeaderDirection=false).
        //
        // Naming note: this fix retains the AB-2 log message (the
        // projection geometry itself is unchanged — same 10/20/30y
        // phases, same ±120° rotation), but adds a new basis label
        // "toward-route" to the existing log line. The fix-fire
        // identifier BD logs separately when the route direction is
        // chosen, so we can track its rate of engagement.
        float fdx = 0f;
        float fdy = 0f;
        bool usingLeaderDirection = false;
        bool usingRouteDirection = false;

        // Hoisted so Fix BB (below, ~line 3442) can reference it. When BD's
        // route direction is in use, usingLeaderDirection stays false and BB
        // won't fire — the leader variable is unused in that path.
        LeaderState? leader = leaderConnection.LastLeaderState;

        Vector3[] loadedRoute = navigation.LoadedRoute;
        if (loadedRoute.Length > 0)
        {
            float bestDist = float.MaxValue;
            int bestIdx = -1;
            for (int i = 0; i < loadedRoute.Length; i++)
            {
                float d = pos.WorldDistanceXYTo(loadedRoute[i]);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = i;
                }
            }

            // Only use the route direction if the nearest waypoint is more
            // than 1y away. Co-located waypoint would produce a near-zero
            // direction vector that degenerates the projection.
            if (bestIdx >= 0 && bestDist >= 1.0f)
            {
                Vector3 wp = loadedRoute[bestIdx];
                float dxw = wp.X - pos.X;
                float dyw = wp.Y - pos.Y;
                float lenSqW = dxw * dxw + dyw * dyw;
                if (lenSqW > 0.001f)
                {
                    float lenW = MathF.Sqrt(lenSqW);
                    fdx = dxw / lenW;
                    fdy = dyw / lenW;
                    usingRouteDirection = true;
                    logger.LogInformation(
                        $"[FFG] [FIX-FIRE] BD: AB-2 projection basis = toward " +
                        $"nearest LoadedRoute waypoint idx={bestIdx} at {wp} " +
                        $"({bestDist:0.0}y away). Route waypoints are pre-validated " +
                        $"terrain — heading toward route guarantees the projection " +
                        $"target sits on traversable ground, even if the bot's " +
                        $"current position is on terrain where navmesh and " +
                        $"character collision disagree. Original away-from-leader " +
                        $"basis suppressed for this CantFollow cycle.");
                }
            }
        }

        // Fallback: route unavailable or co-located. Use existing logic.
        if (!usingRouteDirection)
        {
            bool leaderFresh = leader != null &&
                               leaderConnection.LocalAgeMs <= leaderConnection.StaleThresholdMs;

            if (leaderFresh)
            {
                fdx = pos.X - leader!.WorldPos.X;
                fdy = pos.Y - leader.WorldPos.Y;
                float lenSq = fdx * fdx + fdy * fdy;
                if (lenSq < 0.001f)
                {
                    float facing = playerReader.Direction;
                    fdx = -MathF.Cos(facing);
                    fdy = -MathF.Sin(facing);
                    usingLeaderDirection = false;
                }
                else
                {
                    float len = MathF.Sqrt(lenSq);
                    fdx /= len;
                    fdy /= len;
                    usingLeaderDirection = true;
                }
            }
            else
            {
                float facing = playerReader.Direction;
                fdx = -MathF.Cos(facing);
                fdy = -MathF.Sin(facing);
                usingLeaderDirection = false;
            }
        }

        // ── Fix BB (log-83 20:17:59:688 → 20:18:05:005) ──
        //
        // CLOSE-LEADER PATH-REJECTION OVERRIDE: FLIP DIRECTION TOWARD LEADER
        //
        // Original Fix AB-2 (this function, Path-2) projects AWAY from the
        // leader's body on the assumption that the leader is "where we want
        // to go but can't reach" — i.e., the bot is physically stuck in
        // terrain near an unreachable leader, and the bot's arrival path
        // is the safest reverse direction. That assumption is correct for
        // displacement-based stuck scenarios.
        //
        // It is WRONG for the path-rejection failure mode: Fix AC+AE+AI+AL
        // refuses paths whose first major segment is a U-turn (angle > 150°,
        // simplified ≥ 8 nodes). When this rejection fires twice in a row
        // and escalates to CantFollow, the bot is at a navmesh-VALID position
        // (it just walked there); the rejection is a routing quirk where
        // the pather returns a long winding detour for a short straight-line
        // distance. The bot is not wedged — backing away from leader simply
        // moves it further from any clean navmesh route, deepening the
        // routing problem.
        //
        // Evidence (log-83, mob 1 approach):
        //   20:17:57:807  Assist starts navigating to approach-start anchor
        //                 <-510.83, -4448.26>. Bot at <-523.32, -4442.74>.
        //   20:17:57:992  Path 195 computed cleanly: pathLen=12. Bot walks SE
        //                 through Durotar corridor between hill and tree.
        //   20:17:59:487  Bot arrives at anchor area (Fix AN tolerance match).
        //                 Bot ends Y=-4446.69 — 1.6y NORTH of corridor
        //                 centerline (Y≈-4448) due to navmesh-snap drift.
        //   20:17:59:626  FFG refreshes wp to new leader-pursuit target
        //                 <-503.99, -4448.80> (8.5y SE of bot).
        //   20:17:59:688  Path 196 computed: pathLen=145 for 9y straight-line
        //                 distance. Simplified=16, routeTop=<-516, -4444.8>
        //                 at 163° behind forward dir. The bot's 1.6y-off-
        //                 axis position forces the pather to wind through
        //                 the tight tree/hill navmesh, producing a long
        //                 detour whose first major segment goes NW first.
        //                 Fix AC+AE+AI+AL rejects (matches log-69 profile).
        //   20:17:59:689  Rewind to LastSafeAnchor (<-513.01, -4446.62> —
        //                 0.4y away, useless: bot already there).
        //   20:17:59:703  Rewind reached; retry leader target.
        //   20:18:00:260  Path 197: same 145-node path. Rejected again.
        //   20:18:00:261  Path failed after retry → CantFollow.
        //   20:18:00:277  ╔════════════════════════════════════════════╗
        //                 ║ Fix AB-2 directional fallback projection   ║
        //                 ║ 10y AWAY from leader → <-522.25, -4444.74> ║
        //                 ║ (10y WEST of bot, AWAY from leader EAST).  ║
        //                 ║                                            ║
        //                 ║ Bot follows this Projection10 target,      ║
        //                 ║ then Projection10 fires again on arrival   ║
        //                 ║ at 20:18:02:903 (another 10y WEST), then   ║
        //                 ║ again at 20:18:03:877 (third 10y WEST).    ║
        //                 ║ Total westward travel: 20y before Fix AO   ║
        //                 ║ caps cumulative displacement.              ║
        //                 ╚════════════════════════════════════════════╝
        //   20:18:05:005  Fix AO cap (20y) — exit CantFollow at <-532.58,
        //                 -4451.25>. Leader has moved on, now 64.4y east.
        //                 Net result: assist walked 20y in the WRONG
        //                 direction, leader had to rescue.
        //
        // User-visible behavior matches the log: "the assist followed half
        // way through the corridor, then stopped, turned around and ran in
        // the opposite direction." The "turned around" event is the
        // SetSingleWaypoint(Projection10 target = 10y west) at 20:18:00:277
        // — exactly when the bot's wp flipped from <-503.99, -4448.80>
        // (leader, east) to <-522.25, -4444.74> (10y west).
        //
        // Fix: when CantFollow is entered due to recent path-rejection
        // (_lastPathFailureWasRejection=true, set at the snap.Reject=true
        // line in Navigation_OnPathResultInspected) AND the leader is
        // close (≤ FixBBLeaderProximityYards), AND we have a fresh leader
        // position (usingLeaderDirection=true), NEGATE the direction
        // vector. Projection10's 0° rotation then points TOWARD leader
        // (straight at, where the bot was already heading); Projection20's
        // +120° and Projection30's -120° still apply on top, so the sweep
        // is over the FORWARD half-plane instead of the rear.
        //
        // For log-83 geometry: leader 9y SE, fdx initial ≈ (-0.973, +0.230)
        // (10y NW). After flip: fdx ≈ (+0.973, -0.230) (10y SE). Projection10
        // → <-503.04, -4448.74>: 0.95y short of the leader, in the corridor.
        // Even if the navmesh blocks the route, the bot's CURRENT heading
        // doesn't flip (the wp is in the same direction the bot was
        // already going). The "turned around" symptom is fully eliminated.
        //
        // Why 15y threshold:
        //   - FollowingMaxYards = 14y is the dead-band's outer edge —
        //     "within follow range" is roughly bounded by this.
        //   - 15y gives 1y of margin for jitter; covers the log-83 case
        //     (leader was 8.5y) and similar close-leader scenarios.
        //   - Beyond 15y, the bot may legitimately be in a different
        //     "stuck-far-from-leader" failure mode where the original AB-2
        //     reverse-arrival heuristic is correct. Don't override there.
        //
        // Why not just disable the flip when bot is wedged: the wedged
        // case sets _lastPathFailureWasRejection=false (rejection didn't
        // fire — bot was stuck due to displacement). The flag-based gate
        // already distinguishes these failure modes correctly without
        // needing additional condition checks.
        //
        // Recovery if the flipped direction also fails: the bot walks
        // toward the leader's last known position; if it hits real
        // terrain (the hill or tree the pather was routing around), the
        // chase/no-progress watchdog fires within ~3s and Projection10
        // escalates to Projection20 (which adds +120° from the flipped
        // basis, still in the forward half-plane). Eventually the
        // CantFollow phase exhausts and LastSafeAnchor / PhysicalUnstuck
        // / Exhausted fire as today. Worst-case: 9-12s of forward-
        // attempt cycling before the bot is back to today's recovery
        // path — and during those 9-12s the bot stays NEAR the leader,
        // not 20y away. AssistRequestReturn rescue (which fires from
        // the leader side at status=CantFollow) can resolve from the
        // close position much faster than the original 20y-away spot.
        // ── Turn 3.5 update (log-86 evidence) ──
        // Raised FixBBLeaderProximityYards from 15.0f to 25.0f. The 15y
        // value was too aggressive for corridor scenarios where the
        // assist trails further behind the leader during catch-up:
        //
        // Log-86 trace (03:05:02-15): the assist was at <-521.60,
        // -4429.37> with the leader 20.9y away ("segment to leader is
        // now clear (20.9y, no BL rect blocks)"). Fix AB-2 fired with
        // basis=away-from-leader, projecting the bot WEST while the
        // leader was EAST. Cumulative AB-2 firings (23 in the session)
        // walked the bot ~13y west of the corridor entrance, then
        // rotated through ±120° projections, ending 18y NORTH of the
        // corridor at <-521.60,-4429.37>. Stuck for 50s.
        //
        // BB was supposed to override this by flipping the projection
        // TOWARD the leader, but BB only fired when leader was ≤ 15y.
        // At 20.9y, BB stayed silent and AB-2's wrong-direction
        // projection ran unimpeded.
        //
        // 25y covers all observed corridor-stuck scenarios (log-83,
        // log-84, log-85, log-86 — leader was ≤ 25y in every case).
        // Trade-off: BB may now flip in scenarios where the leader is
        // farther away and the away-from-leader direction WAS correct
        // (e.g., escaping a wide blacklist rect). But BB still
        // requires _lastPathFailureWasRejection=true, which is
        // specifically the corridor-style failure — not the
        // blacklist-rect escape. The two conditions together remain
        // a tight signature for the corridor failure mode.
        const float FixBBLeaderProximityYards = 25.0f;
        if (usingLeaderDirection && _lastPathFailureWasRejection)
        {
            float distLeader = pos.WorldDistanceXYTo(leader!.WorldPos);
            if (distLeader <= FixBBLeaderProximityYards)
            {
                logger.LogWarning(
                    $"[FFG] [FIX-FIRE] BB: CantFollow caused by path-rejection " +
                    $"(_lastPathFailureWasRejection=true) AND leader is close " +
                    $"(dist={distLeader:0.0}y ≤ {FixBBLeaderProximityYards:0.0}y). " +
                    $"Flipping Fix AB-2 direction TOWARD leader instead of " +
                    $"away. Original (away) basis would have sent the bot " +
                    $"further from any clean navmesh; toward-leader keeps the " +
                    $"bot pointed in the direction it was already trying to go. " +
                    $"Projection10 will fire at 0° (straight toward leader); " +
                    $"Projection20/30 sweep ±120° over the forward half-plane. " +
                    $"pos={pos} leader={leader!.WorldPos}.");
                fdx = -fdx;
                fdy = -fdy;
            }
        }

        // ±120° rotation. yards is always 10/20/30 (the three Projection
        // phases pass these constants explicitly). Use >25/>15 thresholds
        // so the test is robust to future constant adjustments without
        // breaking at the exact boundaries.
        const float Rotate120 = 2f * MathF.PI / 3f;
        float angleOffset = 0f;
        if (yards > 25f)
            angleOffset = -Rotate120;
        else if (yards > 15f)
            angleOffset = Rotate120;

        if (MathF.Abs(angleOffset) > 0.001f)
        {
            float cos = MathF.Cos(angleOffset);
            float sin = MathF.Sin(angleOffset);
            float rdx = fdx * cos - fdy * sin;
            float rdy = fdx * sin + fdy * cos;
            fdx = rdx;
            fdy = rdy;
        }

        Vector3 fallbackTarget = new Vector3(
            pos.X + fdx * yards,
            pos.Y + fdy * yards,
            0f);

        logger.LogInformation(
            $"[FFG] [FIX-FIRE] AB-2: no BL rect contains assist — directional fallback projection " +
            $"{yards:0}y (offset={angleOffset * 180f / MathF.PI:+0.0;-0.0;0}°, " +
            $"basis={(usingRouteDirection ? "toward-route" : usingLeaderDirection ? "away-from-leader" : "reversed-facing")}) → " +
            $"{fallbackTarget} (pos={pos}).");

        return fallbackTarget;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    // ── Route-walking migration: Turn 2 helpers ──
    //
    // TryFindLeaderRouteIndex maps the leader's published TargetWaypointW
    // (a world-space position) back to an index in navigation.LoadedRoute.
    // The leader publishes the world coordinates of its current patrol
    // target; the assist needs to know "which route file index is that?"
    // to compute its own allowed-advance.
    //
    // Match tolerance (1.0y) handles minor float precision differences
    // between the leader's stored route waypoints and the assist's
    // map-to-world-converted equivalents. If the published waypoint
    // doesn't match any LoadedRoute entry within tolerance, returns
    // false — the leader is on a non-route waypoint (rescue, manual
    // detour, or transient state). Caller falls back to PositionChase.
    //
    // O(LoadedRoute.Length) linear scan. With ~150 waypoints per route
    // and FFG ticking every ~250ms, this is negligible (sub-µs).
    //
    // ── Turn 3.5 update ──
    // Now caches successful matches in _cachedLeaderRouteIdx +
    // _cachedLeaderRouteIdxUtc. Falls back to the cached value when:
    //   (a) leader.HasTargetWaypoint=false (observed when ATG takes
    //       over from FRG — leader's patrol waypoint cleared while
    //       HasApproachStart=true), OR
    //   (b) HasTargetWaypoint=true but no match within 1.0y tolerance
    //       (rescue insertion, blacklist detour, etc.).
    //
    // Cache TTL is CachedLeaderRouteIdxMaxAgeSec (30s); older values
    // are treated as stale and rejected.
    //
    // Log-86 root cause: this function previously returned false on
    // HasTargetWaypoint=false, which made CheckShouldDefer return
    // false during every approach event (HasApproachStart and
    // HasTargetWaypoint are mutually exclusive in the leader's
    // state machine — see log-86 evidence "Leader resumed patrol/
    // started approaching ... waypointPublished=False,
    // approachStarted=True"). With the cache fallback, the previous
    // route index is available when ApproachStart fires, so
    // CheckShouldDefer can engage.
    private bool TryFindLeaderRouteIndex(LeaderState leader, out int leaderIdx)
    {
        leaderIdx = -1;
        Vector3[] route = navigation.LoadedRoute;
        if (route.Length == 0) return false;

        // Primary path: leader has a published target waypoint we can match.
        if (leader.HasTargetWaypoint)
        {
            Vector3 leaderWp = new(leader.TargetWaypointWorldX, leader.TargetWaypointWorldY, 0f);
            const float matchToleranceYards = 1.0f;

            float bestDist = float.MaxValue;
            int bestIdx = -1;
            for (int i = 0; i < route.Length; i++)
            {
                float d = route[i].WorldDistanceXYTo(leaderWp);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = i;
                }
            }

            if (bestDist <= matchToleranceYards)
            {
                leaderIdx = bestIdx;
                // Update cache. Log only on transition (new index value),
                // not on every tick where the leader is still at the same
                // waypoint — avoids per-tick spam.
                if (_cachedLeaderRouteIdx != leaderIdx)
                {
                    int prevIdx = _cachedLeaderRouteIdx;
                    _cachedLeaderRouteIdx = leaderIdx;
                    _cachedLeaderRouteIdxUtc = DateTime.UtcNow;
                    if (prevIdx < 0)
                    {
                        logger.LogInformation(
                            $"[FFG] [ROUTE-WALK] Cache initialized: leaderIdx={leaderIdx} " +
                            $"at {route[leaderIdx]}.");
                    }
                    // Quiet on update — happens often during patrol.
                }
                else
                {
                    // Same index — just refresh timestamp.
                    _cachedLeaderRouteIdxUtc = DateTime.UtcNow;
                }
                // ── Turn 3.5b: cache-fallback transition log ──
                // We're returning via the primary path. If we were previously
                // on cache fallback, log the transition back. This pairs with
                // the "Cache fallback engaged" log below.
                if (_cacheFallbackInUse)
                {
                    logger.LogInformation(
                        $"[FFG] [ROUTE-WALK] Cache primary resumed: leader's published " +
                        $"TargetWaypoint matches route[{leaderIdx}] within tolerance.");
                    _cacheFallbackInUse = false;
                }
                return true;
            }
            // HasTargetWaypoint=true but no match within tolerance.
            // Could be a rescue insertion or non-route waypoint. Fall
            // through to cache fallback below — preserve last known
            // route index rather than failing outright.
        }

        // Fallback path: cached value, if still fresh.
        if (_cachedLeaderRouteIdx >= 0 && _cachedLeaderRouteIdx < route.Length)
        {
            double ageSec = (DateTime.UtcNow - _cachedLeaderRouteIdxUtc).TotalSeconds;
            if (ageSec <= CachedLeaderRouteIdxMaxAgeSec)
            {
                leaderIdx = _cachedLeaderRouteIdx;
                // ── Turn 3.5b: cache-fallback transition log ──
                // Log only on entry (transition from primary). Subsequent
                // ticks where we keep using the cache are silent.
                if (!_cacheFallbackInUse)
                {
                    string reason = leader.HasTargetWaypoint
                        ? "HasTargetWaypoint=true but no route match within 1.0y tolerance " +
                          "(likely rescue insertion or non-route waypoint)"
                        : "HasTargetWaypoint=false (likely ApproachStart-mutex or " +
                          "leader transient state)";
                    logger.LogInformation(
                        $"[FFG] [ROUTE-WALK] Cache fallback engaged: using leaderIdx=" +
                        $"{leaderIdx} (cached age={ageSec:0.0}s). Reason: {reason}. " +
                        $"Will revert to primary when leader publishes a route-matching " +
                        $"waypoint or cache exceeds {CachedLeaderRouteIdxMaxAgeSec:0}s TTL.");
                    _cacheFallbackInUse = true;
                }
                return true;
            }
            // Stale — let it expire silently. Caller treats as "no leader idx."
            if (_cacheFallbackInUse)
            {
                // Was on fallback, now expired. Log the expiry so it doesn't
                // look like the leaderIdx silently vanished.
                logger.LogInformation(
                    $"[FFG] [ROUTE-WALK] Cache fallback expired: " +
                    $"age={ageSec:0.0}s > {CachedLeaderRouteIdxMaxAgeSec:0}s TTL. " +
                    $"TryFindLeaderRouteIndex now returns false; caller will fall " +
                    $"back to PositionChase.");
                _cacheFallbackInUse = false;
            }
        }

        return false;
    }

    // FindNearestSafeRouteIndex picks an initial index for the assist to
    // start route-walking from. Constraint: must be ≤ maxIdx (typically
    // leader's index − 1, so the assist trails). Within that cap, picks
    // the route waypoint closest to the assist's current position.
    //
    // Used for initial sync at RouteWalk entry. The assist may have been
    // doing PositionChase / Anchor mode immediately prior, so its
    // physical position is somewhere not-particularly-aligned with any
    // route waypoint. Picking the nearest one keeps the next leg short.
    //
    // If maxIdx is negative or LoadedRoute is empty, returns −1
    // (caller should fall through to PositionChase).
    private int FindNearestSafeRouteIndex(Vector3 assistPos, int maxIdx)
    {
        Vector3[] route = navigation.LoadedRoute;
        if (route.Length == 0) return -1;
        int upper = Math.Min(maxIdx, route.Length - 1);
        if (upper < 0) return -1;

        float bestDist = float.MaxValue;
        int bestIdx = 0;
        for (int i = 0; i <= upper; i++)
        {
            float d = route[i].WorldDistanceXYTo(assistPos);
            if (d < bestDist)
            {
                bestDist = d;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    // ── Route-walking migration: Turn 3 ──
    //
    // CheckShouldDefer is a pure read-only predicate: should the
    // HasApproachStart-true tick continue route-walking (storing the
    // anchor as pending) rather than entering Anchor mode directly?
    //
    // Returns true and sets anchorCoords when ALL of:
    //   - leader.HasApproachStart is true (else there's no anchor to defer)
    //   - LoadedRoute is populated
    //   - leader.Status == Patrolling (the leader's published target
    //     waypoint is current; non-Patrolling means HasTargetWaypoint
    //     may be stale or absent and TryFindLeaderRouteIndex would fail)
    //   - TryFindLeaderRouteIndex succeeds
    //   - allowedAdvance ≥ 0 (leader past route start)
    //   - the assist has NOT yet reached its current route target
    //     (distToCurrent > Navigation.POP_DIST). If the assist is
    //     already at the target waypoint, deferring would add no
    //     benefit — Anchor mode transitions immediately via the
    //     direct branch.
    //
    // Pure read-only: does NOT modify _assistRouteIndex or _pendingAnchor.
    // Caller (the HasApproachStart block) handles state mutation based
    // on the result.
    //
    // O(LoadedRoute.Length) due to TryFindLeaderRouteIndex + optionally
    // FindNearestSafeRouteIndex. Called once per FFG tick during approach;
    // negligible cost.
    private bool CheckShouldDefer(LeaderState leader, out Vector3 anchorCoords)
    {
        anchorCoords = default;
        if (!leader.HasApproachStart) return false;
        if (navigation.LoadedRoute.Length == 0) return false;
        if (leader.Status != BotStatus.Patrolling) return false;

        if (!TryFindLeaderRouteIndex(leader, out int leaderIdx)) return false;
        int allowedAdvance = leaderIdx - 1;
        if (allowedAdvance < 0) return false;

        // Determine assist's effective current target index. If unsynced,
        // probe what the initial-sync logic WOULD pick — that's the
        // waypoint we'd be heading to once route-walking engages.
        int idx = _assistRouteIndex >= 0
            ? _assistRouteIndex
            : FindNearestSafeRouteIndex(playerReader.WorldPos, allowedAdvance);
        if (idx < 0) return false;
        if (idx > allowedAdvance) idx = allowedAdvance;

        float distToTarget = playerReader.WorldPos.WorldDistanceXYTo(navigation.LoadedRoute[idx]);
        if (distToTarget <= Navigation.POP_DIST)
        {
            // Already at (or within POP_DIST of) the current target —
            // no point deferring. The Anchor branch transitions now.
            return false;
        }

        anchorCoords = new Vector3(leader.ApproachStartWorldX, leader.ApproachStartWorldY, 0f);
        return true;
    }

    private void StartNavigatingToLeader(LeaderState leader)
    {
        Vector3 target = GetNavigationTarget(leader);
        _lastNavigatedToLeaderWorldPos = target;
        _navAttempt = 0;
        _navRewindActive = false;
        _navRewindAnchorW = default;
        _navTimerInit = false;
        _navActiveElapsed = TimeSpan.Zero;
        _navProgressCheckPosW = playerReader.WorldPos;

        // log-36: if the loop guard escalates to CantFollow (target inside the
        // leader's blacklist on every retry), the EnterState(NavigatingToLeader)
        // and CurrentStatus assignment below would overwrite the CantFollow
        // state set by EnterCantFollow. Bail out on escalation.
        if (!SetWaypointLoopGuarded(target))
            return;
        EnterState(NavState.NavigatingToLeader);
        assistStatusProvider.CurrentStatus = BotStatus.NavigatingToLeader;
    }

    /// <summary>
    /// Selects the navigation target in world-space coordinates.
    ///
    /// <para><b>Anchor mode</b> (Priority 0):<br/>
    /// When the leader is approaching a combat target, returns the published
    /// approach-start anchor. Both bots converge on the same geographic point
    /// before the final interact-key approach.</para>
    ///
    /// <para><b>Route-walk mode</b> (Priority 1, Turn 2):<br/>
    /// When the leader is patrolling AND <c>navigation.LoadedRoute</c> is
    /// populated AND the leader's published target waypoint maps to a route
    /// index, the assist navigates to its own next route waypoint, trailing
    /// the leader by one waypoint index. Each leg is short and curated;
    /// pather problems associated with body-chasing through tight terrain
    /// disappear. See the route-walking design doc for full semantics.</para>
    ///
    /// <para><b>Waypoint-sharing mode</b> (Priority 2, fallback for empty LoadedRoute):<br/>
    /// Legacy behavior: returns the leader's current patrol waypoint
    /// directly. Only reachable when LoadedRoute is empty (assist class
    /// config has no PathFilename) or when RouteWalk's index lookup
    /// fails for an unrelated reason. Marked dormant under route-walking.</para>
    ///
    /// <para><b>Position-chasing mode</b> (Priority 3, fallback):<br/>
    /// Returns the world-space position from <see cref="ComputeFollowTargetWorldPos"/>
    /// — the leader's body offset by <see cref="FollowStopShortYards"/> toward
    /// the assist. Used when the leader is in combat, looting, resting,
    /// evading, or when route-walking explicitly bails (leader on non-route
    /// waypoint such as a rescue insertion).</para>
    /// </summary>
    private Vector3 GetNavigationTarget(LeaderState leader)
    {
        // Priority 0: Leader is approaching a mob — navigate to the fixed approach-start anchor.
        // The anchor is the leader's world position at the moment ATG/PTG began, published via
        // LeaderNavigationProvider. Using a fixed target eliminates the moving-target problem:
        // chasing the leader's live body during approach means the pather continuously recomputes
        // a route to a retreating point, arriving in different terrain. Both bots navigating to
        // the same anchor start the final interact-key approach from the same geographic location.
        if (leader.HasApproachStart)
        {
            // ── Route-walking migration: Turn 3 — combat handoff ──
            //
            // Before entering Anchor mode directly, check whether we
            // should defer the anchor and continue route-walking to the
            // current route waypoint first. CheckShouldDefer returns
            // true when we're in steady-state route-walking and have a
            // route-leg-distance still to cover before reaching the
            // target waypoint. In that case, store the anchor as
            // pending and fall through to the RouteWalk block below,
            // which will continue advancing toward the route target.
            //
            // The transition to Anchor mode happens in the RouteWalk
            // block when the assist arrives at the current route
            // target (distToCurrent ≤ POP_DIST) AND _pendingAnchor is
            // set — at that point _pendingAnchor is consumed and the
            // route waypoint transitions to the Anchor target.
            //
            // Why defer at all? Log-85's corridor incident showed that
            // body-chase (and direct-anchor with off-route start) can
            // fail in tight terrain because the pather is given a
            // start position that the route designer didn't curate.
            // Route-walking keeps the assist on or near a curated
            // waypoint, so when Anchor mode finally engages the
            // assist's pather start is geometrically aligned with
            // the same leg the leader walked into combat.
            //
            // CheckShouldDefer returns false when the assist is
            // already at the current target waypoint — no benefit to
            // deferring, transition to Anchor immediately via the
            // direct branch below.
            if (CheckShouldDefer(leader, out Vector3 deferAnchor))
            {
                // Store / update pending anchor (the published anchor
                // may shift if the leader picks a slightly different
                // approach position on subsequent ticks; rare but
                // possible).
                if (_pendingAnchor != deferAnchor)
                {
                    Vector3 prev = _pendingAnchor;
                    _pendingAnchor = deferAnchor;
                    if (prev == default)
                    {
                        logger.LogInformation(
                            $"[FFG] [COMBAT-HANDOFF] Approach start while route-walking — " +
                            $"deferring anchor={deferAnchor}. Continue to current route target " +
                            $"idx={_assistRouteIndex} (or initial-sync if -1) before Anchor mode.");
                    }
                    else
                    {
                        float shift = prev.WorldDistanceXYTo(deferAnchor);
                        if (shift > 1.0f)
                        {
                            logger.LogInformation(
                                $"[FFG] [COMBAT-HANDOFF] Pending anchor updated " +
                                $"({prev} → {deferAnchor}, shift={shift:0.0}y). Still deferring.");
                        }
                    }
                }
                // Fall through to RouteWalk block below — do not return
                // here. Skip the rest of the Anchor branch's setup;
                // RouteWalk handles target selection.
            }
            else
            {
                if (_rendezvousConfirmed)
                {
                    _rendezvousConfirmed = false;
                    _lastSharedWaypointW = default;
                    logger.LogInformation("[FFG] Leader approaching mob — clearing rendezvous, locking to approach-start anchor.");
                }

            // Latch short-circuit: once the anchor was determined to be co-located in
            // this approach phase, commit to position-chasing for the rest of the phase.
            // The latch is reset in the else-branch below when HasApproachStart goes
            // false (ATG.OnExit fires ClearApproachStart on the leader; the assist
            // observes this via the next API poll).
            //
            // Without this latch the assist oscillates around the POP_DIST (3.6y)
            // boundary because GetNavigationTarget is called every GOAP tick:
            //   tick t₀ — anchorDist=3.5y → return chase target (east of bot, near leader)
            //   tick t₁ — bot moved east → anchorDist=3.7y → return anchor (now west of bot)
            //             SetSingleWaypoint fires (drift ≈ 9y > WaypointUpdateThresholdYards=3y)
            //             bot turns ~180° to face anchor
            //   tick t₂ — bot crosses 3.6y boundary again → flip back, another 180° turn
            // This was visible in log 17 as alternating ~948ms RightArrow / 949ms
            // LeftArrow presses (each ~85° rotation) at assist 02:37:37–02:37:38.
            if (_approachAnchorColocated)
            {
                _currentNavTargetMode = NavTargetMode.PositionChase;
                return ComputeFollowTargetWorldPos(leader);
            }

            Vector3 anchor = new(leader.ApproachStartWorldX, leader.ApproachStartWorldY, 0f);

            // Co-location guard: the approach-start anchor is the leader's world position
            // at ATG entry. If the assist had been navigating toward the same location as
            // the FRG patrol waypoint (common — the leader was just there), it may have
            // arrived within Navigation.POP_DIST of the anchor. Setting a waypoint at a
            // co-located point causes the navigation system's "already reached" check to
            // fire immediately, popping the waypoint without movement and triggering
            // OnDestinationReached on the very next navigation.Update() call. When that
            // happens, fall back to position-chasing toward the leader's live body —
            // guaranteed to be > POP_DIST away — and LATCH the decision via
            // _approachAnchorColocated so subsequent ticks don't re-evaluate.
            float anchorDist = playerReader.WorldPos.WorldDistanceXYTo(anchor);
            if (anchorDist < Navigation.POP_DIST)
            {
                _approachAnchorColocated = true;
                logger.LogWarning(
                    $"[FFG] Approach-start anchor co-located ({anchorDist:0.0}y < {Navigation.POP_DIST}y) — " +
                    "anchor would be immediately popped; reverting to position-chasing for the remainder of this approach phase.");
                _currentNavTargetMode = NavTargetMode.PositionChase;
                return ComputeFollowTargetWorldPos(leader);
            }

            // Anchor is not co-located — return world-space anchor directly.
            // SetSingleWaypoint's IsMapPoint check will not match (values are large
            // negative for Azeroth) and uses it as-is.
            _currentNavTargetMode = NavTargetMode.Anchor;
            return anchor;
            }
            // ── Route-walking migration: Turn 3 ──
            // Reached here only when CheckShouldDefer returned true.
            // Pending anchor was stored above. Fall through to
            // RouteWalk block below (skip the !HasApproachStart else
            // branch's latch resets — we're still in approach phase,
            // latches should NOT be reset yet).
        }
        else
        {
            // ── Route-walking migration: Turn 3 ──
            // Approach phase ended (HasApproachStart=false). If we had
            // a pending anchor that was never consumed (assist didn't
            // arrive at its route target before the approach ended —
            // could be mob died early, operator aborted, leader picked
            // a different mob, etc.), clear it so the next RouteWalk
            // tick doesn't accidentally fire a transition from a stale
            // anchor. The "consume via arrival" path in the RouteWalk
            // block already clears pending on a successful transition;
            // this is the only place where pending is cleared without
            // an arrival transition.
            if (_pendingAnchor != default)
            {
                logger.LogInformation(
                    $"[FFG] [COMBAT-HANDOFF] Approach ended without arrival at route waypoint — " +
                    $"clearing pending anchor (was {_pendingAnchor}). " +
                    $"Resume normal route-walking on next tick.");
                _pendingAnchor = default;
            }

            // Approach phase ended — reset the latch so the next approach episode
            // (ATG re-entry, possibly on a new mob with a different anchor position)
            // is evaluated fresh. Safe across rapid re-entry: ATG.OnExit always fires
            // ClearApproachStart before PTG/ATG re-entry sets a new anchor, so the
            // assist observes HasApproachStart=false at least once between phases
            // (poll interval ~250ms).
            _approachAnchorColocated = false;

            // Fix AN: gate the latch resets on the local-TTL state. The old
            // unconditional reset would clear _approachTargetAcquired and
            // _approachTargetLastAttemptUtc every tick HasApproachStart=false,
            // which was correct before Fix AN. With Fix AN, the focus-chain
            // block may fire WHILE HasApproachStart=false (via the local-TTL
            // path), latching _approachTargetAcquired=true. Resetting it on
            // the very same FFG.Update() tick — GetNavigationTarget is called
            // from the state-machine code below — would un-latch it, and the
            // next tick Fix AN would re-fire (rate-limit timestamp was also
            // just reset). Result: focus-chain spam every ~30ms.
            //
            // Solution: only clear the latches once the TTL has fully expired.
            // Within the TTL window, the latches persist; outside it, they
            // reset cleanly. This preserves the pre-Fix-AN behavior for the
            // common case (no local TTL ever active) and correctly handles
            // the Fix AN case (latches persist while the TTL is fresh).
            double anchorAgeMs = _lastSeenApproachAnchorUtc == DateTime.MinValue
                ? double.MaxValue
                : (DateTime.UtcNow - _lastSeenApproachAnchorUtc).TotalMilliseconds;
            bool localTtlExpired = anchorAgeMs >= AnchorLocalTtlMs;

            if (localTtlExpired)
            {
                // Fix AH: also drop the one-shot focus-chain latch so the next approach
                // phase re-acquires the leader's (possibly different) target. Same
                // re-entry safety as above — the HasApproachStart=false observation
                // between phases clears both latches together.
                _approachTargetAcquired = false;
                // Fix AJ: also clear the retry timestamp so the next approach
                // episode is rate-limited fresh from t=0.
                _approachTargetLastAttemptUtc = DateTime.MinValue;
                // Fix AM: clear the latched timestamp + stale-warn cooldown so
                // diagnostic accounting starts fresh on the next approach.
                _approachTargetLatchedUtc = DateTime.MinValue;
                _staleLatchLastWarnUtc = DateTime.MinValue;
            }
            // NOTE Fix AN: we deliberately do NOT reset _lastSeenApproachAnchor*
            // here. The whole purpose of that field is to remember the anchor
            // AFTER HasApproachStart goes false, so the Fix AN local-TTL logic
            // can still fire if the assist arrives at the (now-expired) anchor
            // within AnchorLocalTtlMs. It naturally ages out via the timestamp
            // comparison in the Fix AH+AJ gating block.
        }

        // ── Route-walking migration: Turn 2 ──
        //
        // Priority 1: Route-walking during patrol. The assist navigates its
        // own next route waypoint (trailing the leader by one index in
        // LoadedRoute) rather than chasing the leader's body. Eliminates
        // the moving-target failure mode that caused log-69 / log-83
        // corridor incidents.
        //
        // Engagement conditions (all must hold):
        //   - leader is Patrolling (not Combat/Loot/Rest/Waiting)
        //   - navigation.LoadedRoute is populated (Turn 1 LoadRoute()
        //     succeeded — assist class config has a PathFilename)
        //   - leader's published TargetWaypointW matches a LoadedRoute
        //     entry within 1.0y tolerance (the leader is on a known
        //     route waypoint, not a rescue/manual detour insertion)
        //   - the resulting leaderIdx − 1 is ≥ 0 (leader has advanced
        //     past the route's start — otherwise there's nothing for
        //     the assist to trail to)
        //
        // When RouteWalk doesn't engage but the leader is patrolling
        // with a populated LoadedRoute, fall through to PositionChase
        // (NOT WaypointSharing) — see code below. WaypointSharing
        // remains as a safety net only for the empty-LoadedRoute case.
        //
        // State management:
        //   - _assistRouteIndex starts at −1 (unsynced) after OnEnter
        //     and after any non-RouteWalk mode tick. First RouteWalk
        //     engagement calls FindNearestSafeRouteIndex to pick the
        //     closest waypoint at-or-before allowedAdvance.
        //   - On each RouteWalk tick, if the assist is within
        //     Navigation.POP_DIST of its current target waypoint AND
        //     the index can still advance (< allowedAdvance), advance.
        //     Repeats within a tick if multiple waypoints are within
        //     POP_DIST (close-packed corner waypoints).
        //   - If the leader retreated (rare; manual override, rescue
        //     completion at a behind-position), clamp _assistRouteIndex
        //     to the new allowedAdvance to prevent the assist from
        //     overshooting the leader.
        if (leader.Status == BotStatus.Patrolling &&
            navigation.LoadedRoute.Length > 0)
        {
            if (TryFindLeaderRouteIndex(leader, out int leaderIdx))
            {
                int allowedAdvance = leaderIdx - 1;
                if (allowedAdvance >= 0)
                {
                    Vector3[] route = navigation.LoadedRoute;

                    // Initial sync (or re-sync after non-RouteWalk tick).
                    if (_assistRouteIndex < 0)
                    {
                        int syncIdx = FindNearestSafeRouteIndex(playerReader.WorldPos, allowedAdvance);
                        _assistRouteIndex = syncIdx;
                        logger.LogInformation(
                            $"[FFG] [ROUTE-WALK] Initial sync: assist at {playerReader.WorldPos}, " +
                            $"nearest safe index={syncIdx} of {route.Length} " +
                            $"(leaderIdx={leaderIdx}, allowedAdvance={allowedAdvance}), " +
                            $"target wp={route[syncIdx]}, " +
                            $"distToTarget={playerReader.WorldPos.WorldDistanceXYTo(route[syncIdx]):0.0}y.");
                    }

                    // Clamp if leader retreated.
                    if (_assistRouteIndex > allowedAdvance)
                    {
                        logger.LogWarning(
                            $"[FFG] [ROUTE-WALK] Clamping _assistRouteIndex from " +
                            $"{_assistRouteIndex} to {allowedAdvance} " +
                            $"(leader retreated? leaderIdx={leaderIdx}).");
                        _assistRouteIndex = allowedAdvance;
                    }

                    // Advance if arrived. Loop in case multiple close-packed
                    // waypoints are within POP_DIST simultaneously.
                    while (_assistRouteIndex < allowedAdvance)
                    {
                        float distToCurrent = playerReader.WorldPos.WorldDistanceXYTo(route[_assistRouteIndex]);
                        if (distToCurrent > Navigation.POP_DIST)
                            break;
                        int prevIdx = _assistRouteIndex;
                        _assistRouteIndex++;
                        logger.LogInformation(
                            $"[FFG] [ROUTE-WALK] Advanced index {prevIdx} → {_assistRouteIndex} " +
                            $"(reached wp={route[prevIdx]}, dist={distToCurrent:0.0}y, " +
                            $"new target={route[_assistRouteIndex]}, " +
                            $"leaderIdx={leaderIdx}, gap={leaderIdx - _assistRouteIndex}).");
                    }

                    // ── Route-walking migration: Turn 3 — combat handoff transition ──
                    //
                    // After the advance loop, _assistRouteIndex is the
                    // largest index we can currently occupy (either
                    // allowedAdvance, or the highest index we've
                    // reached within POP_DIST). If a pending anchor is
                    // set AND we're within POP_DIST of the current
                    // target waypoint, this tick is the transition:
                    // consume the pending anchor, leave RouteWalk mode,
                    // and return the anchor target (with inline
                    // co-location guard mirroring the direct Anchor
                    // branch's behavior).
                    //
                    // The "within POP_DIST" condition handles both
                    // cases:
                    //   (a) we just advanced to allowedAdvance and
                    //       we're standing on it (loop break: dist
                    //       was <= POP_DIST for the popped waypoint,
                    //       but if the new target is also close
                    //       enough, this check sees it as well —
                    //       conservative re-check on the new target).
                    //   (b) we hit the cap (idx == allowedAdvance)
                    //       and the advance loop ran to completion
                    //       without further pops — we're waiting at
                    //       allowedAdvance. If we're within POP_DIST,
                    //       we're parked at the waypoint and ready
                    //       for the transition.
                    if (_pendingAnchor != default)
                    {
                        float distToTarget = playerReader.WorldPos.WorldDistanceXYTo(route[_assistRouteIndex]);
                        if (distToTarget <= Navigation.POP_DIST)
                        {
                            Vector3 anchorTarget = _pendingAnchor;
                            _pendingAnchor = default;
                            int handoffIdx = _assistRouteIndex;
                            _assistRouteIndex = -1; // leaving RouteWalk mode

                            // Inline co-location guard — mirrors the direct
                            // Anchor branch. If the route waypoint is
                            // already within POP_DIST of the anchor (would
                            // cause SetSingleWaypoint to immediately re-pop
                            // and oscillate), latch _approachAnchorColocated
                            // and switch to PositionChase for the remainder
                            // of this approach phase.
                            float anchorDist = playerReader.WorldPos.WorldDistanceXYTo(anchorTarget);
                            if (anchorDist < Navigation.POP_DIST)
                            {
                                _approachAnchorColocated = true;
                                logger.LogWarning(
                                    $"[FFG] [COMBAT-HANDOFF] Route waypoint idx={handoffIdx} " +
                                    $"is co-located with anchor ({anchorDist:0.0}y < {Navigation.POP_DIST}y) — " +
                                    $"latching co-located and switching to PositionChase. " +
                                    $"Subsequent ticks will use direct Anchor branch's PositionChase path.");
                                _currentNavTargetMode = NavTargetMode.PositionChase;
                                return ComputeFollowTargetWorldPos(leader);
                            }

                            logger.LogInformation(
                                $"[FFG] [COMBAT-HANDOFF] Reached route waypoint idx={handoffIdx} " +
                                $"at {route[handoffIdx]}, distToWp={distToTarget:0.0}y; " +
                                $"transitioning Route-walk → Anchor. " +
                                $"anchor={anchorTarget}, distToAnchor={anchorDist:0.0}y.");
                            _currentNavTargetMode = NavTargetMode.Anchor;
                            return anchorTarget;
                        }
                        // Pending anchor set but not yet arrived — keep
                        // route-walking. The pending anchor stays in
                        // place; this tick returns the route target
                        // below.
                    }

                    _currentNavTargetMode = NavTargetMode.RouteWalk;
                    return route[_assistRouteIndex];
                }
                // allowedAdvance < 0: leader at start of route. Fall through
                // to PositionChase below.
            }
            // Leader's published waypoint doesn't map to any route entry
            // (rescue insertion, manual detour). Fall through to PositionChase.

            // Reset index so the next RouteWalk-eligible tick re-syncs from
            // the assist's then-current position.
            if (_assistRouteIndex >= 0)
            {
                _assistRouteIndex = -1;
            }

            _currentNavTargetMode = NavTargetMode.PositionChase;
            return ComputeFollowTargetWorldPos(leader);
        }

        // Reset index on any non-Patrolling status (combat/loot/rest).
        // Next RouteWalk engagement will re-sync.
        if (_assistRouteIndex >= 0)
        {
            _assistRouteIndex = -1;
        }

        // Priority 2: Waypoint-sharing during patrol (LEGACY fallback, only
        // reachable when LoadedRoute is empty — assist class config has no
        // PathFilename). Dormant under route-walking but preserved as a
        // safety net.
        if (_rendezvousConfirmed &&
            leader.Status == BotStatus.Patrolling &&
            leader.HasTargetWaypoint)
        {
            // Fix Y (log-66 23:05:39 → 23:06:42): distance gate.
            //
            // WaypointSharing's semantic is "the bots are close enough to
            // converge on the same destination together." Once
            // _rendezvousConfirmed is set, there has been no automatic exit
            // when the bots drift apart — only the blacklist guard below,
            // and the outer condition for non-Patrolling status. If the
            // assist falls behind, it continued targeting the leader's
            // *next* waypoint rather than the leader's body, even when the
            // bots are 20+ yards apart.
            //
            // The failure mode (log-66): the leader pops a corridor-routing
            // waypoint with up to ~3.5y of slack in wpPopThreshold, then
            // immediately broadcasts the *next* waypoint (which may be past
            // the corridor or in a different direction). If the assist is
            // upstream of that corridor — i.e., still on the wrong side of
            // an obstacle the popped waypoint was placed to thread — the
            // assist's path request to the new waypoint goes from its
            // upstream position to the far waypoint, and the pathfinder
            // returns a detour around the obstacle.
            //
            // Observed in log-66: leader popped <1975.68,-2173.66> at
            // 23:05:39:711 while still 3.3y north of it; at 23:05:40:699
            // the new wp <1975.53,-2187.30> was broadcast; the assist at
            // <1981.85,-2150.15> was 21.7y from the leader and ~5y east of
            // the corridor; the path to the new wp curved around a tree;
            // the bot got stuck; RouteEscape (with Fix W rotation) + Fix X
            // recovery took ~25s.
            //
            // Threshold matches NavigatingMinYards (14y) — FFG's own
            // definition of "the bots are no longer together enough" used
            // for the dead-band entry that triggers navigation from Idle.
            // Above this distance, ComputeFollowTargetWorldPos (which
            // returns the leader's body with FollowStopShortYards offset)
            // is the right target — it anchors the path-find at the
            // leader's actual progressed position rather than the leader's
            // forward broadcast. The path is shorter, the assist isn't
            // pulled past obstacles it hasn't yet crossed, and once it
            // closes back inside NavigatingMinYards the broadcast resumes
            // automatically.
            //
            // _rendezvousConfirmed is NOT cleared — matches the blacklist-
            // guard pattern just below: the bots WERE together, they
            // drifted, they're catching back up. When dist returns to
            // <= NavigatingMinYards on a later tick, the entire outer
            // condition is satisfied again and WaypointSharing resumes
            // without needing a fresh full rendezvous co-location.
            //
            // No hysteresis on the threshold: oscillation around 14y would
            // require the bots to be moving in lockstep at exactly that
            // separation, which doesn't match observed leader/assist
            // dynamics. If logs later show mode-flip thrash here, lower
            // the re-entry threshold to NavigatingExitYards (10y) the same
            // way FFG already hystereses Following↔NavigatingToLeader.
            float distToLeader = playerReader.WorldPos.WorldDistanceXYTo(leader.WorldPos);
            if (distToLeader > NavigatingMinYards)
            {
                if (_currentNavTargetMode != NavTargetMode.PositionChase)
                {
                    logger.LogInformation(
                        $"[FFG] Waypoint-sharing: assist {distToLeader:0.0}y from leader " +
                        $"(> NavigatingMinYards={NavigatingMinYards:0}y) — falling back to " +
                        "position-chase to avoid pathing past the leader through obstacles " +
                        "the assist hasn't yet crossed " +
                        "(rendezvous remains confirmed; resumes sharing when dist returns within range).");
                }

                _currentNavTargetMode = NavTargetMode.PositionChase;
                return ComputeFollowTargetWorldPos(leader);
            }

            Vector3 sharedWp = new(leader.TargetWaypointWorldX, leader.TargetWaypointWorldY, 0f);

            // Defensive blacklist guard (log-35 14:08:16:226 → 14:08:16:894+):
            // The leader can briefly publish a transient blacklisted waypoint via
            // OnWayPointReached before its own SkipBlacklistedWaypoints filters
            // it out. The leader-side fix in FRG.Navigation_OnWayPointReached and
            // PublishPatrolWaypoint now uses TopPublishableWaypointW to avoid this,
            // but timing windows or future leader/assist blacklist divergence can
            // still produce a bad shared waypoint. Without this guard, the assist
            // sets the bad waypoint, Navigation.SkipBlacklistedWaypoints pops it,
            // OnDestinationReached fires (silent — NavDbg gated), this method is
            // called again from the dist-fallback at FFG line 957, returns the
            // same bad waypoint, SetSingleWaypoint is called again — infinite
            // loop every ~15ms, observed for 35+ seconds in log-35.
            //
            // Fall back to position-chase but DON'T clear _rendezvousConfirmed —
            // when the leader publishes a clean waypoint on the next pop,
            // waypoint-sharing resumes automatically without requiring a fresh
            // rendezvous co-location.
            if (navigation.AreaBlacklist != null &&
                navigation.AreaBlacklist.ContainsWorld(sharedWp))
            {
                if (sharedWp.WorldDistanceXYTo(_lastSharedWaypointW) > WaypointUpdateThresholdYards)
                {
                    _lastSharedWaypointW = sharedWp; // remember to dedup log spam
                    logger.LogWarning(
                        $"[FFG] Waypoint-sharing: leader's target waypoint {sharedWp} " +
                        "is in assist's blacklist — falling back to position-chase " +
                        "(rendezvous remains confirmed; will resume sharing on next clean publish).");
                }

                _currentNavTargetMode = NavTargetMode.PositionChase;
                return ComputeFollowTargetWorldPos(leader);
            }

            // Log only when the shared waypoint changes meaningfully — avoids per-tick spam.
            if (sharedWp.WorldDistanceXYTo(_lastSharedWaypointW) > WaypointUpdateThresholdYards)
            {
                _lastSharedWaypointW = sharedWp;
                logger.LogInformation(
                    $"[FFG] Waypoint-sharing: navigating to leader waypoint {sharedWp}");
            }

            _currentNavTargetMode = NavTargetMode.WaypointSharing;
            return sharedWp;
        }

        // Fallback — position-chasing: navigate to the leader's body with stop-short offset.
        // Uses map-space coords which SetSingleWaypoint's IsMapPoint() check handles correctly.
        if (_rendezvousConfirmed)
        {
            // Rendezvous was confirmed but leader is not Patrolling — clear it so we
            // don't try waypoint mode again until the next co-location during patrol.
            _rendezvousConfirmed = false;
            _lastSharedWaypointW = default;
        }

        _currentNavTargetMode = NavTargetMode.PositionChase;
        return ComputeFollowTargetWorldPos(leader);
    }

    /// <summary>
    /// Returns a world-space position that is <see cref="FollowStopShortYards"/>
    /// behind the leader (toward the assist), preventing the assist from running
    /// through and past the leader. WoW has no player-vs-player collision, so
    /// without this offset the assist would overshoot the leader's position at
    /// running speed.
    /// <para>
    /// Operates entirely in world coordinates (yards). The previous map-space
    /// implementation produced cross-coordinate-system drift values when the
    /// caller compared its result against world-space targets returned by the
    /// anchor and waypoint-sharing branches of <see cref="GetNavigationTarget"/>:
    /// <c>WorldDistanceXYTo</c> between a map coord (e.g. <c>&lt;46, 60&gt;</c>) and
    /// a world coord (e.g. <c>&lt;-317, -4417&gt;</c>) is ~4500y, which silently
    /// disabled the per-tick drift gate in <see cref="UpdateNavigatingToLeader"/>
    /// and forced a path recomputation on every mode change. Visible in log 18 as
    /// a 50° body rotation when a brief (388ms) leader ATG re-entry triggered an
    /// Anchor↔PositionChase flip with a real target difference of only 1.85y.
    /// World-space throughout makes the drift gate work as designed and removes
    /// the cross-zone scale factor that the map-space version needed.
    /// </para>
    /// </summary>
    private Vector3 ComputeFollowTargetWorldPos(LeaderState leader)
    {
        // Direction from leader toward assist, in world coordinates (yards).
        Vector3 leaderW = leader.WorldPos;
        Vector3 assistW = playerReader.WorldPos;

        float dx = assistW.X - leaderW.X;
        float dy = assistW.Y - leaderW.Y;
        float lenXY = MathF.Sqrt(dx * dx + dy * dy);

        if (lenXY < 0.001f)
            return new Vector3(leaderW.X, leaderW.Y, 0f); // co-located — navigate to exact position

        float invLen = 1f / lenXY;

        // Fix Z (log-67 00:37:11:228 → 00:37:24:551, assist detour 25y east
        // of corridor over ~13 s): cap the path target distance from the
        // assist when the bots are far apart.
        //
        // The failure mode (log-67): leader walked north through the
        // two-tree corridor from Y=-2174 to Y=-2149. Once Fix Y switched
        // the assist to PositionChase (because dist > NavigatingMinYards),
        // the assist's path target became the leader's body offset —
        // <1976.92,-2152.16>, NORTH of the trees. The straight-line
        // distance from the assist at <1974.43,-2162.89> to that target
        // is 11y, but the trees create a constrained passage between
        // them; the pathfinder returned a 14-node curve that started
        // SW (routeTop=<1974.00,-2164.80> — south of the assist!) and
        // wrapped around to the north. Bot followed the curve, was
        // interrupted mid-path by an Adhoc plan (Inner Fire cast), and
        // on FFG re-entry was at <1978.84,-2168.65> — 4.4 y east of its
        // original position. The next path from this drifted start
        // produced a 15-node curve, and the bot ended up at
        // <2003.49,-2161.80> — 25 y east of the corridor.
        //
        // Compare path #1 in the same log: assist at <1978.21,-2174.27>,
        // target <1976.82,-2161.59> (IN the trees' Y zone, not past
        // them). 4-node straight path. No detour.
        //
        // The difference is purely the target's position relative to the
        // obstacles. When the target sits PAST the trees (Y < -2155),
        // the pathfinder plans a route through or around them, and any
        // route the simplifier returns is a curve the bot has to follow.
        // When the target sits IN OR BEFORE the trees zone, the path is
        // short and straight.
        //
        // Fix Z's solution: when lenXY > NavigatingMinYards, cap the
        // target's distance from the assist at FarTargetMaxYards (7y)
        // along the assist→leader line. For the log-67 scenario:
        // instead of <1976.92,-2152.16> (11y from assist, past trees),
        // the new target is <1976.02,-2156.06> — 7 y from assist,
        // INSIDE the corridor at Y=-2156 (between trees at roughly
        // Y=-2160 south and Y=-2150 north). The pathfinder is asked
        // for a short path to a target inside the corridor, gets a
        // short straight route, the bot walks 7 y north without
        // crossing the constrained passage, and on the next tick the
        // distance drops below NavigatingMinYards and the standard
        // close-target branch resumes.
        //
        // Geometry note: dx, dy is (assist - leader), so (dx, dy)/lenXY
        // is the unit vector pointing FROM leader TOWARD assist. To go
        // FROM assist TOWARD leader, subtract that vector from assistW.
        //
        // 7y matches FollowingMaxYards — the "naturally close" distance
        // already used elsewhere in FFG. When the bot reaches the Fix Z
        // target, dist to leader is ~ (lenXY - 7), which for lenXY
        // slightly above 14y puts the bot right in the Following band.
        //
        // No hysteresis on the threshold: small oscillation around 14 y
        // would just mean a 4-y target jump per crossing, which path-
        // preservation suppression absorbs in most cases. If logs show
        // path-discard thrash here, add a re-entry threshold (e.g.,
        // 10y = NavigatingExitYards).
        //
        // Intersection with the blacklist projection-safety loop below:
        // both Fix Z and Fix 19 modify `target`. Fix Z runs first, then
        // the BL loop. If Fix Z's target falls in/near a BL rect, the
        // BL loop will search for a non-BL alternative starting at
        // distFromLeader=5y and stepping toward the assist. Candidates
        // closer to the leader than Fix Z's distance constraint may be
        // chosen if they're non-BL — i.e., the BL correctness guarantee
        // takes precedence over Fix Z's performance optimization in
        // that rare intersection.
        const float FarTargetMaxYards = 7f;
        Vector3 target;
        if (lenXY > NavigatingMinYards)
        {
            // Far branch: target sits FarTargetMaxYards from assist along
            // the assist→leader line. Subtract because (dx, dy)/lenXY
            // points leader→assist.
            target = new Vector3(
                assistW.X - dx * invLen * FarTargetMaxYards,
                assistW.Y - dy * invLen * FarTargetMaxYards,
                0f);
        }
        else
        {
            // Standard (near) branch: leader's world position shifted
            // FollowStopShortYards toward the assist. Z is zeroed to match
            // the format of every other path returned by GetNavigationTarget
            // (anchor, shared waypoint) — Navigation only uses XY.
            target = new Vector3(
                leaderW.X + dx * invLen * FollowStopShortYards,
                leaderW.Y + dy * invLen * FollowStopShortYards,
                0f);
        }

        // log-36 02:52:51:236 → 02:53:21:263: when the leader is inside a blacklist
        // (e.g., killed/looted a mob inside a blacklisted area), the standard target
        // (leader_pos + FollowStopShortYards * dir_to_assist) sits inside the same
        // rect. Navigation.SkipBlacklistedWaypoints pops it on the next Update tick,
        // OnDestinationReached fires, GetNavigationTarget is re-called and returns
        // the same blacklisted point — infinite SetSingleWaypoint→pop loop at
        // ~15ms intervals for 30 seconds until TickNavActiveTimeout escalates.
        //
        // Fix: project further along the leader→assist line in 2y steps until a
        // non-blacklisted point is found. The assist navigates to the closest safe
        // point near the leader (semantically: "get as close as possible without
        // entering forbidden terrain"). If no safe point exists between the leader
        // and the assist's current position, return the assist's position so the
        // SetWaypointLoopGuarded helper detects the no-movement loop and escalates
        // to CantFollow within ~150ms instead of 30s.
        // Fix 19 (log-44 00:40:40 → 00:41:14: assist ping-ponging in/out of
        // blacklist rect at X≈952-955, 6 full oscillations over ~34 s):
        // the original Fix 4 outer trigger check below used strict
        // ContainsWorld(target). When the standard target landed even 1 y
        // outside the rect's strict boundary (e.g. <947.12, 285.62> with
        // rect MinX≈952.4, target X=947.12 is 5.3 y outside), Fix 4 didn't
        // fire — the function returned the standard target as-is. FFG then
        // set this as the wp, the pather built a 7-point route to reach it,
        // and bot movement execution (auto-run + clockwise turn corrections)
        // overshot the wp by ~5-15 y of forward inertia, depositing the bot
        // inside the rect at X≈952.6 within ~3 s. Navigation's escape-first
        // then fired, drove bot west to X≈937, FFG recomputed the same
        // (still-outside) target, pather rebuilt route, bot overshot again.
        //
        // Fix: change the outer trigger from ContainsWorld (strict) to
        // TryGetContainingRectInflated (margin=ProjectionSafetyMarginYards
        // = 6 y). Now ANY target within 6 y of a rect — even technically
        // outside it — triggers the projection loop. The inflated check
        // inside the loop (also 6 y) returns candidates that are ≥6 y from
        // the strict rect, so the wp itself is at least 6 y clear. Bot's
        // overshoot during route execution is bounded by POP_DIST (3.6 y)
        // plus a small amount of inertia, well under the 6 y margin.
        //
        // 6 y margin chosen for consistency with Fix 12's ExitMargin and
        // Fix 18's projection-loop check. Both inner and outer checks use
        // the same margin so the loop always finds a strictly-better
        // candidate than the standard target if one exists at all.
        //
        // If the entire leader→assist line is within 6 y of a rect (loop
        // finds no safe candidate), control falls through to Fix 12's
        // assist self-escape logic below — same failure-mode as before.
        const float ProjectionSafetyMarginYards = 6.0f;
        if (navigation.AreaBlacklist != null &&
            navigation.AreaBlacklist.TryGetContainingRectInflated(
                target, ProjectionSafetyMarginYards, out _))
        {
            const float STEP_YARDS = 2.0f;
            for (float distFromLeader = FollowStopShortYards + STEP_YARDS;
                 distFromLeader < lenXY;
                 distFromLeader += STEP_YARDS)
            {
                Vector3 candidate = new Vector3(
                    leaderW.X + dx * invLen * distFromLeader,
                    leaderW.Y + dy * invLen * distFromLeader,
                    0f);

                if (!navigation.AreaBlacklist.TryGetContainingRectInflated(
                        candidate, ProjectionSafetyMarginYards, out _))
                {
                    // Fix 33 (log-53: 1000 of these warnings in ~100 s
                    // during CantFollow flap and stationary position-chase):
                    // only log when the projected candidate moves more
                    // than ProjectionLogChangeYards from the last logged
                    // candidate. Same-target repeats are noise.
                    if (_lastWarnedProjectionCandidateW == default ||
                        candidate.WorldDistanceXYTo(_lastWarnedProjectionCandidateW) > ProjectionLogChangeYards)
                    {
                        logger.LogWarning(
                            $"[FFG] Position-chase: standard target {target} is within " +
                            $"{ProjectionSafetyMarginYards:0.0}y of assist's blacklist; " +
                            $"projected toward assist to {candidate} ({distFromLeader:0.0}y from leader, " +
                            $"{lenXY - distFromLeader:0.0}y from assist).");
                        _lastWarnedProjectionCandidateW = candidate;
                    }
                    return candidate;
                }
            }

            // Fix 12 (log-42 17:23:23:280 → 17:24:02:681: assist stuck for 39 s
            // inside leader's blacklist rect (952.39,277.05)-(999.27,327.20)
            // after combat ended; assist at <957.40, 299.97> is 5 y east of the
            // rect's west edge; leader just outside at <951.76, 286.30>; entire
            // leader→assist line crosses the rect. Old fallback returned assist's
            // own position → SetWaypoint loop guard → CantFollow → leader fires
            // AssistReturn → Fix 9 projects rescue target to leader-side edge
            // <951.81, 288.23> → leader stops there, still 15.5 y from assist —
            // LeaderArrivedYards=6 y check in UpdateCantFollow never fires →
            // leader cycles AssistReturn timeouts every 25 s indefinitely; assist
            // never moves. Geometric deadlock: rect is 47 y × 50 y, far wider
            // than 2×LeaderArrivedYards=12 y, so Fix 9's projection geometrically
            // cannot place the leader within 6 y of an assist deep inside the
            // rect, and Navigation's own Escape-first never runs because
            // UpdateCantFollow holds active=false (navigation.Stop every tick).
            //
            // Fix: when the entire leader→assist line is blacklisted AND the
            // assist's own position is inside a blacklist rect, escape the rect
            // first via its closest edge. Return the exit point as the
            // navigation target; the assist moves OUT of the blacklist by ~6-7 y
            // (above the 1 y SetWaypoint loop-guard threshold so the loop guard
            // is naturally reset by real movement), then the next FFG.OnDestinationReached
            // re-enters ComputeFollowTargetWorldPos with the assist outside the
            // rect — standard leader→assist offset target now works since the
            // line no longer crosses the rect from the assist's side.
            //
            // Exit margin = 6 y. POP_DIST is 3.6 y, so a bot popping the wp at
            // 3.6 y short still ends up 2.4 y clear of the rect at worst. Margin
            // also matches DetourMargin/2=6 — the inflation used elsewhere in
            // Navigation when computing safety buffers around blacklist rects.
            //
            // Score = distFromAssist + 0.5*distFromLeader. The 0.5 weight gives
            // a mild lean toward exits on the leader's side without overriding
            // the cheaper close-edge choice when the leader is far.
            //
            // If all 4 axis-aligned exits land in another rect (overlapping
            // blacklist composition), no candidate found → fall through to the
            // old "return assist position" path. Strictly better than current,
            // never worse.
            if (navigation.AreaBlacklist.TryGetContainingRect(assistW, out var containingRect))
            {
                const float ExitMargin = 6.0f;

                ReadOnlySpan<Vector3> exitCandidates = stackalloc Vector3[]
                {
                    new Vector3(containingRect.MinX - ExitMargin, assistW.Y, 0f), // west exit
                    new Vector3(containingRect.MaxX + ExitMargin, assistW.Y, 0f), // east exit
                    new Vector3(assistW.X, containingRect.MinY - ExitMargin, 0f), // south exit
                    new Vector3(assistW.X, containingRect.MaxY + ExitMargin, 0f), // north exit
                };

                Vector3 bestExit = default;
                float bestScore = float.MaxValue;
                bool foundExit = false;

                for (int i = 0; i < exitCandidates.Length; i++)
                {
                    Vector3 c = exitCandidates[i];

                    // Skip candidates that land inside another blacklist rect
                    // (overlapping-rect composition); the bot would just be
                    // stuck in a new rect.
                    if (navigation.AreaBlacklist.ContainsWorld(c))
                        continue;

                    float distFromAssist = c.WorldDistanceXYTo(assistW);
                    float distFromLeader = c.WorldDistanceXYTo(leaderW);
                    float score = distFromAssist + 0.5f * distFromLeader;

                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestExit = c;
                        foundExit = true;
                    }
                }

                if (foundExit)
                {
                    logger.LogWarning(
                        $"[FFG] Position-chase: leader→assist line entirely blacklisted AND assist " +
                        $"inside rect — escaping rect first via exit point {bestExit} " +
                        $"(rect=({containingRect.MinX:0.0},{containingRect.MinY:0.0})-" +
                        $"({containingRect.MaxX:0.0},{containingRect.MaxY:0.0}), assist={assistW}, " +
                        $"exitDist={bestExit.WorldDistanceXYTo(assistW):0.0}y, " +
                        $"leaderDist={bestExit.WorldDistanceXYTo(leaderW):0.0}y).");
                    return bestExit;
                }
            }

            // Fix 24 (log-47 09:25:19:460 → 09:25:46:451, assist sat in
            // CantFollow for 27 s waiting for leader to walk over): the
            // existing fallback below unconditionally returns the assist's
            // own position → SetWaypoint loop guard → CantFollow within
            // ~185 ms. That's only correct when the geometry actually blocks
            // navigation. In log-47, leader <952.5, 272.6> and assist
            // <967.6, 271.5> were both ~5 y south of rect (952.4, 277.05)-
            // (999.3, 327.2). Standard target = <955.5, 272.4>:
            //   - Strictly outside rect (Y = 272.4 < MinY = 277.05).
            //   - Assist→target line stays in Y ∈ [271.5, 272.4], doesn't
            //     cross rect (rect's Y range is [277.05, 327.2]).
            //   - But target is within 6 y inflated, AND every projection
            //     candidate along the leader→assist line lies in the same
            //     parallel band 5 y south of MinY, also within 6 y. The
            //     loop exhausts with no candidate → Fix 12's rect-escape
            //     doesn't apply (assist is not inside any rect) → here.
            // The line is just NEAR the rect, not crossing it. Standard
            // target is reachable. Overshoot during chase is the only
            // residual risk and Navigation's escape-first handles that.
            //
            // Distinguish "near a rect" from "obstructed by a rect" using
            // strict-rect checks before escalating. Two ways the standard
            // target can genuinely be unreachable:
            //   (a) target lands strictly inside a rect — bot would arrive
            //       in forbidden terrain
            //   (b) assist→target segment strictly crosses a rect — route
            //       passes through forbidden terrain en route
            // If neither holds, the inflated-but-strictly-outside scenario
            // applies: return the standard target. CantFollow only fires
            // when the line is genuinely blocked.
            bool targetInsideStrictRect = navigation.AreaBlacklist.ContainsWorld(target);
            bool segmentCrossesStrictRect =
                navigation.AreaBlacklist.TryGetBlockingRect(assistW, target, out _);

            if (!targetInsideStrictRect && !segmentCrossesStrictRect)
            {
                logger.LogWarning(
                    $"[FFG] Position-chase: no candidate ≥{ProjectionSafetyMarginYards:0.0}y " +
                    $"from rect along leader→assist line, AND assist not inside any rect, " +
                    $"BUT standard target {target} is strictly outside all rects and " +
                    $"assist→target segment doesn't cross any rect — returning standard " +
                    $"target (Fix 24). Line is just near rect, not obstructed by it. " +
                    $"(leader={leaderW}, assist={assistW}).");
                return target;
            }

            logger.LogWarning(
                $"[FFG] Position-chase: leader→assist segment is entirely blacklisted " +
                $"(leader={leaderW}, assist={assistW}, targetInsideStrictRect={targetInsideStrictRect}, " +
                $"segmentCrossesStrictRect={segmentCrossesStrictRect}). Returning assist position; " +
                $"SetWaypoint loop guard will escalate to CantFollow.");
            return new Vector3(assistW.X, assistW.Y, 0f);
        }

        return target;
    }

    /// <summary>
    /// Wraps <see cref="Navigation.SetSingleWaypoint"/> with a loop guard that
    /// detects "set wp → immediately popped, no movement" cycles caused by
    /// unreachable navigation targets. The most common trigger is a target
    /// inside the leader's blacklist that <see cref="Navigation.SkipBlacklistedWaypoints"/>
    /// pops on every Update tick.
    ///
    /// <para>Counts consecutive sets within
    /// <see cref="StationarySetTimeWindowMs"/> of each other where the bot
    /// moved less than <see cref="StationarySetMovementThresholdYards"/>. After
    /// <see cref="MaxConsecutiveStationarySetsBeforeCantFollow"/> such cycles,
    /// escalates to <see cref="EnterCantFollow"/> and returns false. Returns
    /// true on a successful set; the time/movement gate naturally resets the
    /// counter when navigation is making progress (legitimate sets are
    /// >100 ms apart and move >1 y between them).</para>
    ///
    /// <para>Without this guard, the existing <see cref="TickNavActiveTimeout"/>
    /// (30 s) is the only escalation path; log-36 02:52:51:236 → 02:53:21:263
    /// shows ~2000 wasted Update iterations during that wait. With it, the
    /// loop is detected after ~150 ms and CantFollow fires immediately.</para>
    /// </summary>
    // -----------------------------------------------------------------------
    // Turn 4a / Fix BE — route-span navigation
    // -----------------------------------------------------------------------
    //
    // EVIDENCE (log-89, 10-minute run):
    //   Leader made 9 pathfinder requests; assist made 319 — a 35x asymmetry.
    //   Caused by the assist using SetSingleWaypoint (which internally calls
    //   SetWayPoints with a 1-element span, producing AvgDistance=
    //   OutDoorMinDistance=3y) while the leader uses SetWayPoints with a
    //   multi-waypoint span (AvgDistance ≈ real route stride ≈ 10y). The
    //   Navigation.cs:3548 formula
    //     `bool usePather = distance > MaxDistance || distance > AvgDistance * 2`
    //   makes usePather=true for any single-waypoint target >6y away, and
    //   usePather=false for typical multi-waypoint route legs of ~10y.
    //
    //   The assist's pather dependency caused fragility to pathfinder latency
    //   spikes (3 of 322 queries in log-89 were >200ms; one was 2543ms). The
    //   corridor stop at <-516,-4449> for 7+ seconds was a single pathfinder
    //   hang that froze the assist because it had no active waypoint to walk
    //   while waiting. The leader traversed the same corridor at the same
    //   time without incident.
    //
    // SOLUTION (mirrors FollowRouteGoal.cs:1924-2135 — FRG.RefillWaypoints):
    //   When SetWaypointLoopGuarded is called with a target near the leader
    //   (within RouteSpanLeaderProximityYards), build a span of route
    //   waypoints from the assist's projected next-route-index to the
    //   leader's route index, optionally with the target appended as the
    //   trailing waypoint. Push via SetWayPoints. The route portion direct-
    //   walks (no pather); the trailing leg may or may not invoke the pather
    //   depending on its distance from the last route waypoint, but it's at
    //   most ONE pather call per span — not one per refresh.
    //
    //   When the target is NOT near the leader (rewind anchor, recovery
    //   projection, etc.), fall through to original SetSingleWaypoint
    //   behavior. These are typically one-shot calls where a single pather
    //   query is acceptable.
    //
    // PROJECTION-AWARE ADVANCEMENT (mirrors FRG:1971-2030):
    //   FindNearestSafeRouteIndex picks by simple Euclidean distance. When
    //   the bot is between route[N] and route[N+1] but slightly closer to
    //   route[N], that returns N. Building a span starting at route[N]
    //   forces the bot to walk BACKWARDS to a waypoint it already passed.
    //   Log-91 evidence: bot oscillated between route[23] and route[24] for
    //   40 seconds because each push reset navigation's stack to start at
    //   route[23] (Euclidean-nearest but already projection-passed).
    //
    //   The advancement applies at most one increment via:
    //     incByDistance = dHere < 1.5y OR dNext <= dHere * 1.25  (bot is
    //       at, or essentially at, current waypoint)
    //     incByProgress = (bot - A) · (B - A) > 0  (bot's foot-of-
    //       perpendicular onto segment A→B is past A toward B)
    //   ...then a POP loop for any further waypoints already within
    //   POP_DIST (bounded by leaderIdx so we never advance past the leader).
    //
    // FALLBACK (caller uses SetSingleWaypoint) when:
    //   - LoadedRoute is empty
    //   - leader state unavailable
    //   - target is too far from leader (>RouteSpanLeaderProximityYards)
    //   - TryFindLeaderRouteIndex fails (leader not on route, no cache)
    //   - FindNearestSafeRouteIndex fails
    //   - after advancement, resumeIndex >= leaderIdx (no forward span)
    //
    // DUPLICATE SUPPRESSION (mirrors FRG:1273-1279 + line 1936 gate):
    //   Two-part check, suppress only if BOTH:
    //     1. canSkipDuplicateRefill = navigation.HasWaypoint() || HasNext()
    //        — only suppress when nav actually has waypoints loaded. If the
    //        stack was cleared (e.g., by EnterCantFollow's Stop()), a fresh
    //        push is required regardless. Content-aware safety, not a
    //        time-based heuristic.
    //     2. IsDuplicateRecentRouteSpan: same firstWp (route waypoints are
    //        stable Vector3 values — 0.01y tolerance) AND same total length
    //        AND within RouteSpanSuppressionMs (750ms, matching FRG's
    //        RefillWaypointsDuplicateCooldownMs).
    //
    //   With projection-aware advancement above, the same span doesn't get
    //   re-computed across many ticks — when the bot advances, resumeIndex
    //   changes, firstWp changes, suppression breaks naturally.
    //
    // LOOP-GUARD INTERACTION:
    //   The SetWaypoint loop guard (caller — SetWaypointLoopGuarded) tracks
    //   consecutive calls within 100ms where the bot moved <1y. On a
    //   suppressed push, we don't want the loop guard to count it as a
    //   "stationary set" — the bot IS walking the existing span. Caller
    //   handles this by only updating _lastSetWaypointUtc/PlayerPos when
    //   outcome starts with "pushed" (actual SetWayPoints invocation).
    private bool TryPushRouteSpanForTarget(Vector3 target, out string outcome)
    {
        outcome = "?";

        Vector3[] route = navigation.LoadedRoute;
        if (route.Length == 0)
        {
            outcome = "no-route";
            return false;
        }

        LeaderState? leader = leaderConnection.LastLeaderState;
        if (leader == null)
        {
            outcome = "no-leader-state";
            return false;
        }

        // Proximity gate: only route-span when the target is "near the leader."
        // For rewind anchors and other off-leader targets, fall through to
        // single-waypoint behavior (one pather call, acceptable cost).
        float targetToLeader = target.WorldDistanceXYTo(leader.WorldPos);
        if (targetToLeader > RouteSpanLeaderProximityYards)
        {
            outcome = $"target-far-from-leader ({targetToLeader:0.0}y > {RouteSpanLeaderProximityYards:0}y)";
            return false;
        }

        if (!TryFindLeaderRouteIndex(leader, out int leaderIdx))
        {
            outcome = "no-leader-route-idx";
            return false;
        }

        Vector3 playerPos = playerReader.WorldPos;
        int closestIndex = FindNearestSafeRouteIndex(playerPos, route.Length - 1);
        if (closestIndex < 0)
        {
            outcome = "no-assist-route-idx";
            return false;
        }

        // Projection-aware advancement (mirrors FRG:1971-2030).
        // See header comment "PROJECTION-AWARE ADVANCEMENT" for rationale.
        int resumeIndex = closestIndex;
        if (resumeIndex < route.Length - 1)
        {
            Vector3 a = route[resumeIndex];
            Vector3 b = route[resumeIndex + 1];

            float dHere = playerPos.WorldDistanceXYTo(a);
            float dNext = playerPos.WorldDistanceXYTo(b);

            bool incByDistance = dHere < 1.5f || dNext <= dHere * 1.25f;

            bool incByProgress = false;
            float abx = b.X - a.X;
            float aby = b.Y - a.Y;
            float abLenSq = abx * abx + aby * aby;
            if (abLenSq > 0.001f)
            {
                float apx = playerPos.X - a.X;
                float apy = playerPos.Y - a.Y;
                float t = (apx * abx + apy * aby) / abLenSq;
                if (t > 0.0f) incByProgress = true;
            }

            if (incByDistance || incByProgress)
                resumeIndex++;
        }

        // POP loop for already-reached waypoints, bounded by leaderIdx.
        while (resumeIndex < leaderIdx)
        {
            if (playerPos.WorldDistanceXYTo(route[resumeIndex]) < Navigation.POP_DIST)
                resumeIndex++;
            else
                break;
        }

        if (resumeIndex >= leaderIdx)
        {
            // At or past leader's route position. Forward span would be empty
            // or single-element; let caller use SetSingleWaypoint for the
            // tight body-chase to wherever the leader actually is.
            outcome = $"resumeIndex-at-or-past-leader (closestIndex={closestIndex}, resumeIndex={resumeIndex}, leaderIdx={leaderIdx})";
            return false;
        }

        // Determine if the target is essentially at route[leaderIdx] — if so,
        // no trailing element needed; the route waypoints already arrive there.
        bool targetIsAtLeaderWp = target.WorldDistanceXYTo(route[leaderIdx]) < Navigation.POP_DIST;
        int routeCount = leaderIdx - resumeIndex + 1;
        int totalLen = targetIsAtLeaderWp ? routeCount : routeCount + 1;
        Vector3 firstWp = route[resumeIndex];

        // Duplicate suppression (mirrors FRG:1273-1279 + line 1936 gate).
        // See header comment "DUPLICATE SUPPRESSION" for rationale.
        bool canSkipDuplicateRefill = navigation.HasWaypoint() || navigation.HasNext();
        if (canSkipDuplicateRefill && IsDuplicateRecentRouteSpan(firstWp, totalLen))
        {
            outcome = $"duplicate-suppressed (firstWp={firstWp}, len={totalLen})";
            logger.LogInformation(
                $"[FFG] [FIX-FIRE] BE: route-span duplicate suppressed " +
                $"(navigation has waypoints loaded, firstWp={firstWp}, len={totalLen}). " +
                $"Existing span is being walked.");
            return true;
        }

        // Build and push the span.
        Span<Vector3> span = stackalloc Vector3[totalLen];
        for (int i = 0; i < routeCount; i++)
            span[i] = route[resumeIndex + i];
        if (!targetIsAtLeaderWp)
            span[totalLen - 1] = target;

        navigation.SetWayPoints(span);
        RecordRouteSpanPush(firstWp, totalLen);

        bool advancedByProjection = resumeIndex != closestIndex;
        logger.LogInformation(
            $"[FFG] [FIX-FIRE] BE: route-span push — {totalLen} waypoints " +
            $"[route[{resumeIndex}..{leaderIdx}]={routeCount} pre-validated waypoints" +
            $"{(targetIsAtLeaderWp ? ", no trailing target (at leader WP)" : $" + trailing target {target}")}]" +
            $"{(advancedByProjection ? $" — advanced from closestIndex={closestIndex} via projection (bot past closest waypoint)" : "")}.");

        outcome = targetIsAtLeaderWp ? "pushed-pure-route" : "pushed-with-trailing";
        return true;
    }

    // Duplicate-suppression helper (mirrors FRG.IsDuplicateRecentRefill at
    // FollowRouteGoal.cs:1273-1279). Returns true if the proposed span has
    // the same first waypoint and same length as the previous push, within
    // the suppression window. 0.01y tolerance on firstWp is essentially an
    // equality check — route waypoints are stable Vector3 values across
    // calls when resumeIndex hasn't advanced.
    private bool IsDuplicateRecentRouteSpan(Vector3 firstWp, int count)
    {
        if (_lastRouteSpanLength != count) return false;
        if (_lastRouteSpanFirstWp == default) return false;
        float d = firstWp.WorldDistanceXYTo(_lastRouteSpanFirstWp);
        if (d > 0.01f) return false;
        return (DateTime.UtcNow - _lastRouteSpanUtc).TotalMilliseconds < RouteSpanSuppressionMs;
    }

    // Records the span we just pushed for use by IsDuplicateRecentRouteSpan
    // on subsequent calls. Mirrors FRG.RecordRefillWaypoints at
    // FollowRouteGoal.cs:1282-1287.
    private void RecordRouteSpanPush(Vector3 firstWp, int count)
    {
        _lastRouteSpanFirstWp = firstWp;
        _lastRouteSpanLength = count;
        _lastRouteSpanUtc = DateTime.UtcNow;
    }

    private bool SetWaypointLoopGuarded(Vector3 target)
    {
        DateTime now = DateTime.UtcNow;
        Vector3 botPos = playerReader.WorldPos;

        if (_lastSetWaypointUtc != DateTime.MinValue)
        {
            double elapsedMs = (now - _lastSetWaypointUtc).TotalMilliseconds;
            float moved = botPos.WorldDistanceXYTo(_lastSetWaypointPlayerPos);

            if (elapsedMs < StationarySetTimeWindowMs && moved < StationarySetMovementThresholdYards)
            {
                _consecutiveStationarySetWaypointCount++;
            }
            else
            {
                _consecutiveStationarySetWaypointCount = 0;
            }

            if (_consecutiveStationarySetWaypointCount >= MaxConsecutiveStationarySetsBeforeCantFollow)
            {
                logger.LogWarning(
                    $"[FFG] SetWaypoint loop guard: {_consecutiveStationarySetWaypointCount} consecutive " +
                    $"sets within {StationarySetTimeWindowMs} ms of each other, moved " +
                    $"<{StationarySetMovementThresholdYards} y per cycle — target {target} is unreachable. " +
                    "Escalating to CantFollow without 30 s TickNavActiveTimeout wait.");
                ResetSetWaypointLoopGuardState();
                EnterCantFollow();
                return false;
            }
        }

        // Turn 4a / Fix BE: try route-span navigation first. When the target
        // is near the leader and a route is loaded, push a multi-waypoint
        // route span instead of a single body-chase target — bypasses the
        // pather for most legs (see Navigation.cs:3548's usePather formula).
        // Falls through to original SetSingleWaypoint behavior on any of
        // the fallback conditions documented in TryPushRouteSpanForTarget.
        if (TryPushRouteSpanForTarget(target, out string spanOutcome))
        {
            // Span path handled (pushed or duplicate-suppressed).
            // On actual push, update loop-guard state with current bot pos
            // so the next call's "moved since last set" check is accurate.
            // On duplicate-suppression, do NOT update — the loop guard
            // should compare against the last ACTUAL push, capturing real
            // bot displacement and correctly resetting the counter on
            // genuine progress.
            if (spanOutcome.StartsWith("pushed"))
            {
                _lastSetWaypointUtc = now;
                _lastSetWaypointPlayerPos = botPos;
            }
            return true;
        }

        // Fallback: original SetSingleWaypoint path for targets that don't
        // qualify for route-span (no route loaded, target far from leader,
        // assist past leader on route, etc.). spanOutcome carries the
        // reason — useful for log analysis when route-span coverage is
        // unexpectedly low.
        logger.LogDebug(
            $"[FFG] Route-span not applicable ({spanOutcome}); falling back to " +
            $"SetSingleWaypoint(target={target}). This still forces usePather=true.");

        navigation.SetSingleWaypoint(target);
        _lastSetWaypointUtc = now;
        _lastSetWaypointPlayerPos = botPos;
        return true;
    }

    private void ResetSetWaypointLoopGuardState()
    {
        _consecutiveStationarySetWaypointCount = 0;
        _lastSetWaypointUtc = DateTime.MinValue;
        _lastSetWaypointPlayerPos = default;
    }

    private void EnterCantFollow()
    {
        logger.LogWarning(
            $"[FFG] Navigation exhausted — entering CantFollow. " +
            "Assist will attempt active escape (10/20/30y projection away from " +
            "rect center, LastSafeAnchor, physical unstuck) and also exit when " +
            $"the leader arrives within {LeaderArrivedYards}y or the segment " +
            "to the leader clears any blacklist.");
        navigation.Stop();
        assistStatusProvider.CantFollow = true;
        _cantFollowEnteredUtc = DateTime.UtcNow;
        // Fix AO (log-78): record entry position for the cumulative-displacement
        // exit in UpdateCantFollow. See the field comment for rationale.
        _cantFollowEnteredPos = playerReader.WorldPos;
        assistStatusProvider.CurrentStatus = BotStatus.CantFollow;
        // Fix 32 — reset the escape state machine so it starts fresh from
        // NotStarted on the first UpdateCantFollow tick. Without this,
        // a previous CantFollow cycle's state (e.g., Exhausted) could
        // persist into a new cycle and the assist would never even try
        // its first projection.
        ResetEscapeState();
        EnterState(NavState.CantFollow);
    }

    private void EnterState(NavState newState)
    {
        logger.LogInformation($"[FFG] NavState: {_navState} → {newState}");
        _navState = newState;
        _navStateEnteredUtc = DateTime.UtcNow;

        if (newState == NavState.NavigatingToLeader)
            _stuckCheckLastUtc = DateTime.MinValue;
    }

    private void ResetNavState()
    {
        _navAttempt = 0;
        _navRewindActive = false;
        _navRewindAnchorW = default;
        _navTimerInit = false;
        _navActiveElapsed = TimeSpan.Zero;
        _navProgressCheckPosW = default;
        _lastNavigatedToLeaderWorldPos = default;
        _activeStuckSinceUtc = DateTime.MinValue;
        _activeStuckReported = false;

        // Fix AX: direct-route attempts are scoped per NavigatingToLeader
        // cycle. Reset here so each new cycle gets a fresh attempt budget
        // — without this, a long-running follow session could exhaust the
        // counter and then never recover even if the failure mode is
        // genuinely intermittent (different (start, end) on each stuck).
        _directRouteAttemptsInCycle = 0;
    }

    // -----------------------------------------------------------------------
    // Active-time navigation timeout
    // -----------------------------------------------------------------------
    private void TickNavActiveTimeout()
    {
        var now = DateTime.UtcNow;

        if (!_navTimerInit)
        {
            _navTimerInit = true;
            _navLastTickUtc = now;
            _navActiveElapsed = TimeSpan.Zero;
            _navProgressCheckPosW = playerReader.WorldPos;
            return;
        }

        // Accumulate elapsed only outside combat. Combat legitimately suspends
        // forward movement (cast loops, etc.) and is handled by CombatGoal —
        // FFG isn't even the active goal then in most cases. Evade-recovery
        // is NOT exempted: during a flee the assist must keep moving, and a
        // genuine stall during the window is exactly the case the timeout
        // should surface (escalation to CantFollow → leader navigates back).
        bool countActive = !bits.Combat();
        if (countActive)
            _navActiveElapsed += now - _navLastTickUtc;

        _navLastTickUtc = now;

        // Reset the timeout whenever the assist makes meaningful forward progress.
        // Without this, the 30s wall fires even when the assist is actively navigating —
        // e.g. the pather gets slow on elevated terrain near the final waypoint and the
        // character stops briefly while waiting for the route result, burning the remaining
        // timer budget even though 170 yards of progress was made in the preceding 28 seconds.
        // By resetting on NavigationProgressResetYards of movement, the timer only accumulates
        // during genuine stalls with zero position change.
        Vector3 currentPos = playerReader.WorldPos;
        float moved = currentPos.WorldDistanceXYTo(_navProgressCheckPosW);
        if (moved >= NavigationProgressResetYards)
        {
            _navProgressCheckPosW = currentPos;
            _navActiveElapsed = TimeSpan.Zero;
        }

        if (_navActiveElapsed.TotalSeconds >= NavigationActiveTimeoutSec)
        {
            logger.LogWarning(
                $"[FFG] Navigation stuck timeout ({_navActiveElapsed.TotalSeconds:0.0}s without progress) — escalating to CantFollow.");
            EnterCantFollow();
        }
    }

    // -----------------------------------------------------------------------
    // Chase-progress watchdog escalation
    //
    // Reads Navigation.ChaseSinceBestSec — the time since the chase watchdog
    // last recorded a new closest-distance to the chase target. Distinct
    // from raw-displacement timers in this class:
    //
    //   - TickNavActiveTimeout: resets on NavigationProgressResetYards (3 y)
    //     of any movement. A bot grinding sideways along terrain can
    //     accumulate 3 y of drift in a few seconds and reset the timer
    //     forever, never escalating.
    //
    //   - TickActiveStuckDetection: triggers/resumes on raw displacement
    //     within a sliding window. Even with the asymmetric resume threshold
    //     (3 y from anchor), 3 y of drift along a wall eventually clears
    //     the Stuck flag — same flapping risk over a longer cycle.
    //
    // ChaseSinceBestSec is the only metric that's resilient to drift
    // because it only resets when the bot achieves a NEW closest distance
    // to the chase target. A bot wedged against geometry making zero
    // progress toward target accumulates sinceBest indefinitely regardless
    // of how much sideways drift occurs.
    //
    // When sinceBest exceeds ChaseWatchdogCantFollowSec (15 s), the chase
    // watchdog's own internal recovery paths (unstuck attempt at 4 s,
    // route refill at 6 s — both in Navigation.cs) have already had two
    // chances to make progress and failed. The obstacle is unrecoverable
    // by automated means; escalate to CantFollow so the leader navigates
    // back to retrieve the assist.
    //
    // Skipped when chase isn't being tracked (ChaseSinceBestSec returns 0).
    // -----------------------------------------------------------------------
    private void TickChaseWatchdog()
    {
        double sinceBest = navigation.ChaseSinceBestSec;
        if (sinceBest < ChaseWatchdogCantFollowSec)
            return;

        logger.LogWarning(
            $"[NAV-DIAG] Chase watchdog fire: stuckDetector.OwnerId={navigation.StuckDetectorOwnerId} " +
            $"Enabled={navigation.StuckDetectorEnabled} sinceBest={sinceBest:0.0}s");
        logger.LogWarning(
            $"[FFG] Chase watchdog: {sinceBest:0.0}s without closing on chase target — " +
            $"escalating to CantFollow. Leader will navigate back to retrieve assist.");
        EnterCantFollow();
    }

    // -----------------------------------------------------------------------
    // Active-navigation stuck reporting
    //
    // Detects when the assist is in NavigatingToLeader but failing to make
    // forward progress, and reports BotStatus.Stuck via the API so the leader's
    // FRG.ShouldLeaderPauseForAssist gates on it (line 161 of AssistStateStore).
    //
    // Without this signal, the leader sees the assist as
    // status=NavigatingToLeader && dist<20y and continues patrolling onto the
    // next mob, leaving the stuck assist behind. Observed in log 21
    // (assist 00:11:03–00:11:15): the assist was caught on terrain at
    // <-404.23, -4062.5> for ~12s while the leader engaged the next mob.
    // Navigation.cs's chase watchdog logged "No chase progress while stationary"
    // every 1.5s during the stuck period but did not surface the condition to
    // the leader API.
    //
    // Triggers on movement < ActiveStuckMinMovementWorld (1y) over
    // ActiveStuckThresholdSec (2.5s). Recovery flips status back to
    // NavigatingToLeader so the leader resumes naturally. Skipped during
    // combat (CombatGoal handles its own positioning, the assist legitimately
    // stops to cast). NOT skipped during evade-recovery: with the session-27
    // hold-gate removal the assist navigates during the window, so a stall
    // there is a real stall — and surfacing it (leader pauses, navigates
    // back to the stuck assist) is the right behaviour for a crucial flee.
    // -----------------------------------------------------------------------
    private void TickActiveStuckDetection()
    {
        if (bits.Combat())
        {
            // Combat naturally suspends progress; clear tracking so we
            // don't immediately flag stuck on resumption.
            _activeStuckSinceUtc = DateTime.MinValue;
            _activeStuckReported = false;
            return;
        }

        Vector3 currentPos = playerReader.WorldPos;
        var now = DateTime.UtcNow;

        if (_activeStuckSinceUtc == DateTime.MinValue)
        {
            // First tick of this navigation phase — anchor the position and start the clock.
            _activeStuckSinceUtc = now;
            _activeStuckCheckPosW = currentPos;
            return;
        }

        // ── Asymmetric resume path ──────────────────────────────────────────
        // While Stuck is reported, the resume check uses a SEPARATE anchor
        // (captured at the moment of Stuck) and a HIGHER threshold (3y vs
        // the 1y trigger). This prevents slow incremental drift along an
        // obstacle from clearing Stuck without real escape — the symptom
        // the user observed in log 01:50:54-01:51:09 where the bot
        // flapped Stuck → NavigatingToLeader four times in 10 s while still
        // pinned against a wall (sinceBest=140 s on the chase watchdog).
        if (_activeStuckReported)
        {
            float displaced = currentPos.WorldDistanceXYTo(_activeStuckAnchorW);
            if (displaced >= ActiveStuckResumeMinMovementWorld)
            {
                _activeStuckReported = false;
                _activeStuckCheckPosW = currentPos;
                _activeStuckSinceUtc = now;
                // Restore NavigatingToLeader so the leader resumes patrol. Note
                // that a concurrent Following assignment in UpdateNavigatingToLeader
                // (when dist<10y) sets Following AFTER us when the next tick runs,
                // so this restoration is safe — the higher-level status logic
                // re-asserts itself naturally on the next pass.
                assistStatusProvider.CurrentStatus = BotStatus.NavigatingToLeader;
                logger.LogInformation(
                    $"[FFG] Movement resumed during navigation (displaced {displaced:0.00}y " +
                    $"from stuck anchor) — reverting status from Stuck to NavigatingToLeader.");
            }
            // Else: still wedged. Keep Stuck status. Don't advance the
            // trigger anchor; the trigger window is irrelevant while
            // already reported. The chase watchdog (TickChaseWatchdog,
            // 15 s on Navigation.ChaseSinceBestSec) is the escalation
            // path when Stuck persists too long.
            return;
        }

        // ── Trigger path ────────────────────────────────────────────────────
        float moved = currentPos.WorldDistanceXYTo(_activeStuckCheckPosW);
        if (moved >= ActiveStuckMinMovementWorld)
        {
            // Made progress — reset the trigger window.
            _activeStuckCheckPosW = currentPos;
            _activeStuckSinceUtc = now;
            return;
        }

        // Movement is below threshold. Has the stationary window elapsed?
        double elapsed = (now - _activeStuckSinceUtc).TotalSeconds;
        if (elapsed >= ActiveStuckThresholdSec)
        {
            _activeStuckReported = true;
            _activeStuckAnchorW = currentPos;  // resume anchor pinned here
            assistStatusProvider.CurrentStatus = BotStatus.Stuck;
            logger.LogWarning(
                $"[FFG] Stuck while navigating — moved only {moved:0.00}y in {elapsed:0.0}s " +
                $"at {currentPos}. Reporting Stuck so leader pauses; " +
                $"resume requires {ActiveStuckResumeMinMovementWorld:0.0}y of displacement " +
                $"or {ChaseWatchdogCantFollowSec:0.0}s of no chase progress (escalates to CantFollow).");
        }
    }

    // -----------------------------------------------------------------------
    // Idle stuck detection
    // -----------------------------------------------------------------------
    private void TickIdleStuckDetection()
    {
        if (bits.Combat())
        {
            _stuckCheckLastUtc = DateTime.MinValue;
            return;
        }

        var now = DateTime.UtcNow;
        Vector3 currentW = playerReader.WorldPos;

        if (_stuckCheckLastUtc == DateTime.MinValue)
        {
            _stuckCheckPosW = currentW;
            _stuckCheckLastUtc = now;
            return;
        }

        double elapsed = (now - _stuckCheckLastUtc).TotalSeconds;
        if (elapsed < StuckCheckIntervalSec)
            return;

        float moved = currentW.WorldDistanceXYTo(_stuckCheckPosW);
        _stuckCheckPosW = currentW;
        _stuckCheckLastUtc = now;

        if (moved >= StuckMinMovementWorld)
            return;

        if ((now - _stuckEscapeLastUtc).TotalSeconds < StuckEscapeCooldownSec)
            return;

        _stuckEscapeLastUtc = now;
        logger.LogWarning(
            $"[FFG] Stuck detected — moved only {moved:0.00}y in {elapsed:0.0}s. " +
            "Attempting pather-based escape.");

        assistStatusProvider.CurrentStatus = BotStatus.Stuck;

        if (!navigation.TryUnstuck())
            InjectPhysStuckEscape();
    }

    // -----------------------------------------------------------------------
    // PhysStuck injection
    // -----------------------------------------------------------------------
    private void InjectPhysStuckEscape()
    {
        if (!navigation.IsApproachEscapePhysicallyStuck)
            return;

        navigation.IsApproachEscapePhysicallyStuck = false;
        logger.LogWarning("[FFG] Physically trapped — injecting jump + reverse.");
        input.StopForward(false);
        input.PressJump();
        Thread.Sleep(400);
        input.StartBackward(false);
        input.PressJump();
        Thread.Sleep(600);
        input.PressJump();
        Thread.Sleep(400);
        input.StopBackward(false);
    }

    // -----------------------------------------------------------------------
    // Navigation events
    // -----------------------------------------------------------------------

    private void Navigation_OnDestinationReached()
    {
        if (_navState != NavState.NavigatingToLeader)
            return;

        if (_navRewindActive)
        {
            _navRewindActive = false;
            LeaderState? leader = leaderConnection.LastLeaderState;
            if (leader != null)
            {
                logger.LogInformation("[FFG] Rewind reached — retrying leader target.");
                Vector3 rewindTarget = GetNavigationTarget(leader);
                _lastNavigatedToLeaderWorldPos = rewindTarget;
                SetWaypointLoopGuarded(rewindTarget);
            }
            return;
        }

        LeaderState? currentLeader = leaderConnection.LastLeaderState;
        if (currentLeader == null) return;

        float dist = playerReader.WorldPos.WorldDistanceXYTo(currentLeader.WorldPos);

        logger.LogInformation(
            $"[FFG] Destination reached. dist={dist:0.0}y to leader.");

        if (dist < FollowingMaxYards)
        {
            navigation.Stop();
            input.StopForward(true);
            ResetNavState();
            EnterState(NavState.Idle);
            assistStatusProvider.CurrentStatus = BotStatus.Following;
            assistStatusProvider.CantFollow = false;
            return;
        }

        // Still in dead-band (FollowingMaxYards < dist < NavigatingMinYards).
        //
        // Previously this branch transitioned immediately to Idle and set Following.
        // That caused rapid oscillation every 250ms:
        //   1. OnDestinationReached → Idle, Following=True
        //   2. UpdateIdle: Following=True but _rendezvousConfirmed=False
        //      → dead-band fires → StartNavigatingToLeader
        //   3. ComputeFollowTargetWorldPos returns a target within POP_DIST of the
        //      assist (originally via map↔world conversion precision loss when the
        //      function returned map coords; now possible only when leader and
        //      assist are genuinely co-located near each other)
        //   4. Waypoint immediately popped → OnDestinationReached again → back to 1
        //
        // Fix: stay in NavigatingToLeader and refresh the waypoint to the leader's
        // current live position. If the refresh target is also within POP_DIST (leader
        // genuinely co-located), only then accept Idle — that case is already handled
        // by the dist < FollowingMaxYards branch above, so reaching here means the
        // leader has drifted and we should keep chasing.
        if (dist < NavigatingMinYards)
        {
            Vector3 refreshTarget = GetNavigationTarget(currentLeader);
            float refreshDist = playerReader.WorldPos.WorldDistanceXYTo(refreshTarget);

            if (refreshDist > Navigation.POP_DIST)
            {
                // Leader has moved since the waypoint was set — chase the new position
                // without cycling through Idle/dead-band.
                logger.LogInformation(
                    $"[FFG] Destination reached in dead-band (leader={dist:0.0}y) — refreshing waypoint (target={refreshDist:0.0}y away), staying NavigatingToLeader.");
                _lastNavigatedToLeaderWorldPos = refreshTarget;
                SetWaypointLoopGuarded(refreshTarget);
                // Do NOT transition to Idle — remain in NavigatingToLeader.
                return;
            }

            // Refresh target is also co-located (refreshDist ≤ POP_DIST) — cannot
            // navigate any closer with the current map resolution. Accept Idle so the
            // assist does not spin indefinitely trying to reach an unreachable position.
            //
            // Fix 10 (parity with OnWayPointReached's co-located branch): set
            // _rendezvousConfirmed=true so UpdateIdle's line 723 dead-band check
            // takes the stay-put branch on the next tick instead of re-triggering
            // StartNavigatingToLeader. In log-40 the cycle ran through
            // OnWayPointReached (TryConsumeReachedWaypoint fires both events,
            // OnWayPointReached first, transitioning _navState→Idle so this
            // OnDestinationReached returns early at line 1733). Setting it here
            // too handles the SkipBlacklistedWaypoints path (Navigation line 786)
            // where wayPoints drops to 0 without going through
            // TryConsumeReachedWaypoint — OnDestinationReached fires alone.
            logger.LogInformation(
                $"[FFG] Destination reached in dead-band (leader={dist:0.0}y, target co-located {refreshDist:0.0}y) — entering Idle.");
            _rendezvousConfirmed = true;
            navigation.Stop();
            input.StopForward(true);
            ResetNavState();
            EnterState(NavState.Idle);
            assistStatusProvider.CurrentStatus = BotStatus.Following;
            assistStatusProvider.CantFollow = false;
            return;
        }

        // The leader has moved beyond NavigatingMinYards since we set the waypoint.
        // This is NOT a navigation failure — the pather successfully reached the target.
        // The leader is simply patrolling. Keep chasing; let TickNavActiveTimeout (30s)
        // be the sole CantFollow escalation path for a genuinely unreachable leader.
        logger.LogWarning(
            $"[FFG] Arrived but leader moved on ({dist:0.0}y) — refreshing waypoint to current position.");
        Vector3 retryTarget = GetNavigationTarget(currentLeader);

        // Guard: waypoint-sharing can return the patrol waypoint we just arrived at if the
        // leader hasn't published a new one yet. Navigation.RefillRouteToNextWaypoint calls
        // IsAtFinalWaypoint (reach ≈ 3.35y) — a co-located target is immediately popped
        // without producing movement. With destinationReachedLatched=true the
        // CompleteDestinationReached inside that pop path is a no-op, so navigation exits
        // with HasWaypoint()=false, triggering the fallback loop below every tick indefinitely.
        // Fix: if the target is within POP_DIST the shared waypoint is stale — drop
        // waypoint-sharing and navigate to the leader's live position instead.
        float retryDist = playerReader.WorldPos.WorldDistanceXYTo(retryTarget);
        if (retryDist < Navigation.POP_DIST)
        {
            // ── Turn 3.5b fix (log-87 evidence) ──
            // If we're in RouteWalk mode, the co-located target is EXPECTED —
            // we're parked at our allowed_advance route waypoint, waiting for
            // the leader to advance (cache update on next leader pop will
            // unblock the advance loop). The legacy fallback below (revert to
            // body-chase via ComputeFollowTargetWorldPos) is wrong here:
            //
            // Log-87 trace at 03:53:01:294-372:
            //   - Bot reaches LoadedRoute[10] at <-710.94,-4167.58>.
            //   - dist to leader = 15.5y (> NavigatingMinYards 14y).
            //   - OnDestinationReached retry path fires.
            //   - GetNavigationTarget returns route[10] AGAIN (cache=11,
            //     _assistRouteIndex=10, can't advance because at cap).
            //   - retryDist = 3.3y < POP_DIST 3.6y → "co-located" check.
            //   - Falls back to ComputeFollowTargetWorldPos at <-715.39,
            //     -4176.62> (Fix Z far branch, 7y from bot).
            //   - SetWaypoint(body-chase target).
            //   - Main FFG drift tick (15ms later): GetNavigationTarget
            //     returns route[10] (drift 10y from body-chase target) →
            //     SetWaypoint(route[10]). Navigation pops immediately
            //     (IsAtFinalWaypoint within 3.35y reach).
            //   - "No active waypoint but still far" fallback fires →
            //     same co-located dance → SetWaypoint(body-chase) again.
            //   - 10 SetWaypoint calls in 78ms → SetWaypoint loop guard
            //     escalates to CantFollow → AB-2 projection 10y away from
            //     leader (wrong direction).
            //
            // Across log-87 the pattern fired 26 times, every one followed
            // by AB-2 (79 firings total). AB-2 walked the bot 6-20y away
            // from the leader. AO cumulative cap (20y) eventually exited
            // CantFollow, and the cycle repeated. This is the "back and
            // forth movement after combat" the user reported.
            //
            // The fix: when route-walking, the co-located target means
            // "I'm at my trailing-by-one cap and waiting." Don't refresh
            // navigation; don't fall back to body-chase. Just return.
            // The next leader pop updates the cache, allowedAdvance moves
            // forward, and the advance loop in GetNavigationTarget will
            // pick the new target naturally.
            //
            // Sit-in-place is correct only when route-walking. The legacy
            // WaypointSharing fallback (the else branch below) remains for
            // that code path.
            if (_currentNavTargetMode == NavTargetMode.RouteWalk)
            {
                logger.LogInformation(
                    $"[FFG] [ROUTE-WALK] Parked at route waypoint idx={_assistRouteIndex} " +
                    $"(retryDist={retryDist:0.0}y, leader {dist:0.0}y away). " +
                    $"Trailing-by-one cap reached; waiting for leader to advance. " +
                    $"Skipping waypoint refresh — no SetWaypoint, no loop-guard escalation.");
                return;
            }

            logger.LogWarning(
                $"[FFG] Retry target co-located ({retryDist:0.0}y < {Navigation.POP_DIST}y) — " +
                "stale shared waypoint; reverting to position-chasing.");
            _rendezvousConfirmed = false;
            _lastSharedWaypointW = default;
            retryTarget = ComputeFollowTargetWorldPos(currentLeader);
        }

        _lastNavigatedToLeaderWorldPos = retryTarget;
        SetWaypointLoopGuarded(retryTarget);
    }

    private void Navigation_OnWayPointReached()
    {
        if (_navState != NavState.NavigatingToLeader)
            return;

        LeaderState? leader = leaderConnection.LastLeaderState;
        if (leader == null) return;

        float dist = playerReader.WorldPos.WorldDistanceXYTo(leader.WorldPos);

        if (dist >= NavigatingMinYards)
            return; // still far out — keep navigating, no action needed here

        if (dist < FollowingMaxYards)
        {
            // Truly arrived within following range.
            navigation.Stop();
            ResetNavState();
            EnterState(NavState.Idle);
            assistStatusProvider.CurrentStatus = BotStatus.Following;
            assistStatusProvider.CantFollow = false;
            return;
        }

        // Dead-band zone (FollowingMaxYards < dist < NavigatingMinYards).
        // An intermediate waypoint was reached but the leader is still ahead.
        // Refresh the waypoint instead of stopping — avoids the same Idle/dead-band
        // oscillation described in Navigation_OnDestinationReached.
        Vector3 refreshTarget = GetNavigationTarget(leader);
        float refreshDist = playerReader.WorldPos.WorldDistanceXYTo(refreshTarget);

        if (refreshDist > Navigation.POP_DIST)
        {
            _lastNavigatedToLeaderWorldPos = refreshTarget;
            SetWaypointLoopGuarded(refreshTarget);
            // Stay in NavigatingToLeader.
        }
        else
        {
            // Target co-located — can't get closer; stop.
            //
            // Fix 10 (log-40, three 120s CantFollow timeout cycles at <952.07,297.85>
            // & later at <949.92,294.16>): when ComputeFollowTargetWorldPos returns
            // a target within POP_DIST of the bot — either because the entire
            // leader→assist segment is blacklisted (returns assist's own position)
            // OR because Fix 4 projected to a candidate near the assist that the
            // bot is already adjacent to — we accept Idle here, but
            // _rendezvousConfirmed stays false (line 615-617 only sets it when
            // dist < FollowingMaxYards=7y, and we're in the dead-band 7-14y).
            //
            // On the very next UpdateIdle tick, line 723 check fails:
            //   `Following && _rendezvousConfirmed` → status=Following but
            //   _rendezvousConfirmed=false → falls to else → "Dead-band — closing
            //   gap to confirm rendezvous" → StartNavigatingToLeader → same target
            //   returned → wp popped → OnWayPointReached → here again.
            //
            // The cycle runs at ~30ms per iteration. After 10 stationary
            // SetSingleWaypoint calls in ~300ms, Fix 5's SetWaypointLoopGuarded
            // escalates to CantFollow, which then triggers the leader's
            // ShouldLeaderPauseForAssist (any CantFollow → pause regardless of
            // distance), AssistRequestReturn, and the Fix 9 phantom-AssistReturn
            // loop. log-40 observed three 120s CantFollow cycles (12:57:41:809,
            // 12:59:42:132, 13:01:42:472) with the leader and assist completely
            // stuck for >4 minutes.
            //
            // Semantic justification: geometric impossibility of getting closer
            // (line entirely blacklisted, or projection lands near assist) IS
            // the rendezvous outcome for this geometry. The bot accepts dead-band
            // distance as "close enough"; setting _rendezvousConfirmed=true lets
            // UpdateIdle take the line 723 stay-put branch.
            //
            // Existing safety net at line 1252: GetNavigationTarget clears
            // _rendezvousConfirmed when leader.Status != Patrolling, so this
            // doesn't accidentally persist into combat/loot phases.
            // leaderJustResumedPatrol / leaderJustPublishedWaypoint checks
            // (lines 700 / 673) fire StartNavigatingToLeader on real leader
            // transitions regardless of _rendezvousConfirmed, so the assist
            // re-engages when needed.
            _rendezvousConfirmed = true;

            navigation.Stop();
            ResetNavState();
            EnterState(NavState.Idle);
            assistStatusProvider.CurrentStatus = BotStatus.Following;
            assistStatusProvider.CantFollow = false;
        }
    }

    private void Navigation_OnPathFailed(Vector3 startW, Vector3 endW)
    {
        if (_navState != NavState.NavigatingToLeader)
            return;

        if (_navAttempt >= 1)
        {
            logger.LogWarning("[FFG] Path to leader failed after retry — escalating to CantFollow.");
            EnterCantFollow();
            return;
        }

        if (!navigation.HasLastSafeAnchor)
        {
            logger.LogWarning("[FFG] Path failed — no safe anchor. Escalating to CantFollow.");
            EnterCantFollow();
            return;
        }

        _navAttempt = 1;
        _navRewindActive = true;
        _navRewindAnchorW = navigation.LastSafeAnchorW;

        logger.LogWarning(
            $"[FFG] Path failed. Rewinding to anchor={_navRewindAnchorW}.");
        SetWaypointLoopGuarded(_navRewindAnchorW);
    }

    // -----------------------------------------------------------------------
    // Option B refactor — moved from Navigation.cs:
    //
    // The three handlers below implement path-validation policy that used
    // to live inline inside Navigation.PathCalculatedCallback /
    // Navigation.HandleRepeatedNoPath as Fix AB-1 / Fix AC+AE / Fix AD.
    // Navigation now fires snapshot events at the corresponding decision
    // points and acts on the subscriber's decision flag — see
    // PathInspectionSnapshot / StaleEmptyPathSnapshot /
    // RepeatedNoPathDecisionSnapshot in Navigation.cs.
    //
    // The leader's FollowRouteGoal does not subscribe to these events, so
    // these policies apply only to the assist's follow flow. That matches
    // the assist-only nature of the original log evidence (log-68 corridor
    // stuck, log-69 U-turn into dead zone, log-70b walk-into-terrain,
    // log-71 false-positive AC rejections).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Option B refactor — implements the policy that used to live as Fix AC
    /// and Fix AE inline inside Navigation.PathCalculatedCallback.
    ///
    /// Fix AC original evidence (log-69 12:00:19:399 reqId=23, assist at
    /// &lt;-508.93, -4447.33&gt; chasing target &lt;-501.98, -4448.21&gt; 7y east
    /// through a Durotar corridor between a hill and a tree):
    /// PPather returned pathLen=146 — a heavily-curved wrap-around route
    /// to detour around a tree obstacle. Fix AA correctly dropped 2
    /// individual rear-pointing path nodes within 3y of start, but
    /// PathSimplify reduced 144 remaining nodes to 17, and the simplified
    /// routeTop landed at &lt;-512.4, -4444.8&gt; — dist=4.3y from start,
    /// dot=-26.3 against the forward direction. That node is REAR-POINTING
    /// but just outside Fix AA's 3y bound, so Fix AA didn't catch it. The
    /// bot then turned ~180° and ran NW toward the rear routeTop. Each
    /// subsequent path request from the new (westward-drifted) position
    /// generated another rear-curving wrap. Within 4 seconds the bot was
    /// pulled from &lt;-508.93&gt; west to &lt;-517.45&gt; and then south to
    /// &lt;-4450.98&gt; — a navmesh dead zone where PPather cannot find paths
    /// in ANY direction.
    ///
    /// Original Fix AC: after Fix AA filtering AND PathSimplify, examine
    /// the FINAL simplified routeTop. If it's &gt; 2y from start AND its
    /// projection on the forward direction is &lt;= 0 (rear half-plane),
    /// reject the entire path. The 2y minimum left micro-rear-noise alone;
    /// no upper bound because even a 10y rear routeTop is still bad.
    ///
    /// Fix AE evidence (log-71 13:10:20:141 + 13:10:20:712 + 13:10:31:246
    /// + 13:10:31:814, assist at &lt;-511.73, -4373.90&gt; chasing target
    /// &lt;-505.90, -4377.77&gt; 7y SE, then at &lt;-509.92, -4369.01&gt; chasing
    /// target &lt;-512.46, -4362.49&gt; 7y NNW; user described "tree in front
    /// of them and clear paths on both sides and behind"):
    ///
    /// Fix AC fired 4 times in this episode, rejecting paths whose
    /// simplified routeTops were only marginally rear-leaning:
    ///   #1 (20:141): dist=3.13y dot=-1.19 cos=-0.054 angle= 93.1°
    ///   #2 (20:712): dist=3.21y dot=-1.68 cos=-0.075 angle= 94.3°
    ///   #3 (31:246): dist=4.50y dot=-7.56 cos=-0.240 angle=103.9°
    ///   #4 (31:814): dist=4.50y dot=-7.56 cos=-0.240 angle=103.9°
    /// These are SIDEWAYS detours around an obstacle, not the U-turns
    /// Fix AC was designed for. The genuine log-69 cases were at
    /// cos=-0.875 (151°) and cos=-0.955 (163°). Fix AC's original
    /// `dot &lt;= 0` threshold (90° half-plane) wrongly rejected the
    /// sideways detours, forcing 2 spurious CantFollow entries in
    /// log-71 (assist immobile for 5s + 13s).
    ///
    /// Fix AE tightens the threshold to require angle &gt; 135° (cos &lt; -0.707).
    /// Math identity (avoids sqrt in hot path):
    ///   cos(angle) &lt; -0.707
    ///     ⇔ dot/(|fwd|·|top|) &lt; -0.707
    ///     ⇔ dot² &gt; 0.5·|fwd|²·|top|²   (combined with the dot &lt; 0 guard)
    ///
    /// Recovery path: on Reject, Navigation clears the route, sets a 500ms
    /// cooldown, and fires OnPathFailed → Navigation_OnPathFailed below:
    ///   1st: _navAttempt=0 → rewind to LastSafeAnchor.
    ///   2nd: _navAttempt=1 → EnterCantFollow.
    /// </summary>
    private void Navigation_OnPathResultInspected(PathInspectionSnapshot snap)
    {
        // Only operate while actively chasing the leader. If FFG isn't the
        // logical owner of the current path (e.g., Idle), don't impose
        // assist-follow policy on it.
        if (_navState != NavState.NavigatingToLeader)
            return;

        if (snap.HasSimplifiedRouteTop && snap.ForwardLenSq > 0.0001f)
        {
            float toTopX = snap.SimplifiedRouteTop.X - snap.StartW.X;
            float toTopY = snap.SimplifiedRouteTop.Y - snap.StartW.Y;
            float toTopDistSq = toTopX * toTopX + toTopY * toTopY;
            const float REAR_TOP_REJECT_MIN_YARDS = 2.0f;
            // Fix AE: cos² threshold for angle > 135°. 0.707² = 0.5.
            // To revisit the threshold, this is the one constant to tune
            // (0.75 → 150°, 0.25 → 120°).
            //
            // Fix AI (log-74 22:52:09:983 + 22:52:10:540, assist at
            // <-256.48, -4206.46> chasing target <-255.35, -4213.68> 7.3y SSE;
            // user described "trees obscuring the way", then bot "turned
            // around and walked in the opposite direction"):
            //
            // Fix AE's 0.5 threshold (135°) rejected a legitimate tree-detour
            // path at angle=135.1° (cos=-0.708, dot=-18.28, dist=3.53y).
            // pathLen was 14 (modest detour, not a wraparound); the simplified
            // route had 3 nodes — first NE to swing around trees, then south
            // to the goal — exactly the pather output expected when obstacles
            // sit directly in the goal direction.
            //
            // Two rejections fired back-to-back at exactly 135.1° (a borderline
            // case where the pather's navmesh snapping deterministically lands
            // the simplified routeTop just past the cos²=0.5 threshold), FFG
            // escalated to CantFollow at 22:52:10:541, and Fix AB-2's
            // directional projection (basis=away-from-leader) sent the bot
            // 10y NORTH while the leader was 10y SOUTH at <-254.88, -4216.65>
            // (per leader log at 22:52:10:514). That sent the assist directly
            // away from the leader during active combat — the visible symptom.
            //
            // Fix AI raises the threshold to 0.75 (cos² > 0.75 ⇔ cos < -0.866
            // ⇔ angle > 150°). Effect on known cases:
            //   log-69 #1 (genuine U-turn into navmesh dead zone): cos=-0.875,
            //     151° → STILL rejected (cos²=0.766 > 0.75).
            //   log-69 #2 (genuine U-turn into navmesh dead zone): cos=-0.955,
            //     163° → STILL rejected (cos²=0.912 > 0.75).
            //   log-71 #1-#4 (sideways detours): 93-104° → unchanged from
            //     Fix AE (cos² in 0.003-0.058, well below either threshold).
            //   log-74 (tree-detour false positive): cos=-0.708, 135.1° →
            //     ACCEPTED (cos²=0.501 < 0.75).
            //
            // Risk profile: the new threshold gives ~15° of margin between
            // accepted detours and rejected U-turns (135° vs 150°). A future
            // genuine U-turn at 140-149° would slip past Fix AI, but the
            // recovery path is unchanged — Navigation's OnPathFailed still
            // fires, _navAttempt increments, and a 2nd failure escalates to
            // CantFollow. So a missed rejection costs at most one extra
            // failed path attempt before falling through to the same recovery.
            const float REAR_REJECT_COS_THRESHOLD_SQ = 0.75f;

            // Fix AL (log-76 00:34:34:449 + 00:34:35:004, assist at
            // <-375.78, -4134.94> chasing target <-379.89, -4143.99> 9.94y
            // SSE through some rocks; user observed assist participated in
            // combat after barely clearing the rocks, then "took off running
            // in the opposite direction" post-combat):
            //
            // Fix AI's 150° threshold rejected what was actually a legitimate
            // short rock-detour at angle=166.8° (cos=-0.973, dot=-40.79,
            // dist=4.22y). pathLen=16 raw, simplified to 3 nodes — a short,
            // simple detour requiring ONE bend, not a navmesh-dead-zone
            // wraparound. The cos²=0.947 result is FAR above 0.75; raising
            // the angle threshold further would chase the symptom forever.
            //
            // What actually distinguishes genuine dead-zone U-turns from
            // legitimate short detours is PATH COMPLEXITY, not angle alone:
            //
            //   Case           | angle  | pathLen | simplified | Verdict
            //   ---------------|--------|---------|------------|---------
            //   log-69 (real)  | 151°   | 146     | 17         | DEAD ZONE
            //   log-69 (real)  | 163°   | (high)  | (high)     | DEAD ZONE
            //   log-74 (false) | 135.1° | 14      | 3          | DETOUR
            //   log-76 (false) | 166.8° | 16      | 3          | DETOUR
            //
            // The angle distribution does NOT separate the classes; the
            // simplified-route-count distribution does. log-69's 17-node
            // simplified route reflects a heavy wraparound through navmesh-
            // difficult terrain. log-74 and log-76's 3-node simplified routes
            // reflect single-bend detours around isolated obstacles (trees,
            // rocks).
            //
            // Fix AL: require BOTH high angle AND high simplified route
            // count. A path that meets only the angle criterion (short
            // detour around a small obstacle) is accepted with a warning
            // log line — useful for monitoring future evidence of the
            // false-positive class without losing the data point.
            //
            // The threshold of 8 simplified nodes gives ~5 nodes of margin
            // between known detours (3) and known wraparounds (17). The
            // threshold could be tuned tighter (e.g., 6) without losing
            // detection of log-69, but 8 leaves room for legitimate
            // mid-complexity detours we haven't seen yet.
            //
            // Behavior on known cases:
            //   log-69 (simplified=17, angle 151°-163°) → STILL REJECTED ✓
            //     (both conditions met — genuine dead zone)
            //   log-71 (simplified varies, angle 93°-104°) → unchanged
            //     from Fix AE (angle below threshold)
            //   log-74 (simplified=3, angle 135.1°) → was already accepted
            //     by Fix AI; still accepted here
            //   log-76 (simplified=3, angle 166.8°) → NOW ACCEPTED — bot
            //     follows the detour around the rocks, ~1s of swing-wide
            //     instead of 15s+ of CantFollow walking the wrong direction
            //
            // Why simplified route count rather than raw pathLen: pathLen
            // varies with the pather's per-segment node spacing (which can
            // shift across navmesh versions and area-density). simplified
            // count is measured AFTER Fix AA pruning AND PathSimplify, so
            // it captures the path's actual kink-count — a more direct
            // measure of "is this a complex wraparound" than raw pathLen.
            //
            // Recovery for any missed-rejection case is unchanged: if the
            // accepted path turns out to land the bot in trouble (the bot
            // walks NE 4y then runs into a navmesh edge), Navigation's
            // stuck detection and OnPathFailed escalation still fire.
            const int COMPLEX_WRAPAROUND_MIN_ROUTECOUNT = 8;

            if (toTopDistSq > REAR_TOP_REJECT_MIN_YARDS * REAR_TOP_REJECT_MIN_YARDS)
            {
                float dot = snap.ForwardX * toTopX + snap.ForwardY * toTopY;
                bool angleExceedsThreshold =
                    dot < 0f &&
                    dot * dot > REAR_REJECT_COS_THRESHOLD_SQ * snap.ForwardLenSq * toTopDistSq;
                bool isComplexWraparound =
                    snap.SimplifiedRouteCount >= COMPLEX_WRAPAROUND_MIN_ROUTECOUNT;

                if (angleExceedsThreshold && isComplexWraparound)
                {
                    // Compute readable cos/angle for the log message. Only
                    // executed on the rare rejection path, so the sqrt is fine.
                    float topDist = MathF.Sqrt(toTopDistSq);
                    float fwdLen = MathF.Sqrt(snap.ForwardLenSq);
                    float cosA = dot / (fwdLen * topDist);
                    if (cosA < -1f) cosA = -1f;
                    else if (cosA > 1f) cosA = 1f;
                    float angleDeg = MathF.Acos(cosA) * (180f / MathF.PI);

                    logger.LogError(
                        $"[FFG] [FIX-FIRE] AC+AE+AI+AL: rejecting strongly-rear-curving COMPLEX path " +
                        $"(angle > 150° AND simplified route count ≥ {COMPLEX_WRAPAROUND_MIN_ROUTECOUNT}). " +
                        $"Simplified routeTop {snap.SimplifiedRouteTop} is {topDist:0.00}y " +
                        $"from start with dot={dot:0.00} cos={cosA:0.000} " +
                        $"angle={angleDeg:0.0}° against forward direction. " +
                        $"Bot would U-turn into navmesh dead zone. " +
                        $"start={snap.StartW} end={snap.EndW} " +
                        $"pathLen={snap.PathLength} routeCount-after-simplify={snap.SimplifiedRouteCount}. " +
                        $"Signalling Reject — Navigation will clear route and fire OnPathFailed.");
                    snap.Reject = true;
                    snap.RejectCooldownMs = 500;
                    // Fix BB (log-83): mark that the most recent path failure
                    // was a rejection (not a stuck-displacement or no-path).
                    // ComputeAwayFromRectWaypoint's Path-2 fallback consults
                    // this flag at Projection10 time (~hundreds of ms later
                    // via the rewind/retry → EnterCantFollow → StartEscapePhase
                    // chain) to flip the escape direction from AWAY-from-leader
                    // to TOWARD-leader when the leader is close. See the field
                    // doc comment near _lastPathFailureWasRejection for full
                    // rationale and evidence.
                    _lastPathFailureWasRejection = true;
                    return;
                }
                else if (angleExceedsThreshold)
                {
                    // Fix AL: angle alone met the threshold, but the path is
                    // a short simple detour (low simplified count), not a
                    // dead-zone wraparound. Accept it. Log at Warning level
                    // so future evidence of this false-positive class is
                    // visible without polluting normal logs.
                    float topDist = MathF.Sqrt(toTopDistSq);
                    float fwdLen = MathF.Sqrt(snap.ForwardLenSq);
                    float cosA = dot / (fwdLen * topDist);
                    if (cosA < -1f) cosA = -1f;
                    else if (cosA > 1f) cosA = 1f;
                    float angleDeg = MathF.Acos(cosA) * (180f / MathF.PI);

                    logger.LogWarning(
                        $"[FFG] [FIX-FIRE] AL: angle would have triggered rejection " +
                        $"(angle={angleDeg:0.0}°, cos={cosA:0.000}) but path is a " +
                        $"short simple detour (simplified routeCount={snap.SimplifiedRouteCount} " +
                        $"< {COMPLEX_WRAPAROUND_MIN_ROUTECOUNT}); accepting. " +
                        $"start={snap.StartW} end={snap.EndW} routeTop={snap.SimplifiedRouteTop} " +
                        $"dist={topDist:0.00}y pathLen={snap.PathLength}. " +
                        $"If the bot subsequently struggles to navigate this path, " +
                        $"OnPathFailed/_navAttempt escalation will trigger CantFollow normally.");
                    // Fall through — path is accepted (no Reject set).
                }
            }
        }

        // Path is accepted — pather is functional for the current (start,
        // end). Reset Fix AB-1's stale-empty counter so a future stale
        // failure for a different (start, end) starts the count fresh.
        // (This mirrors the original behavior that lived in Navigation's
        // success branch.)
        _staleEmptySameCount = 0;
        _staleEmptyLastStartW = default;
        _staleEmptyLastEndW = default;

        // Fix BB: a successful path acceptance means the pather is no
        // longer stuck on the U-turn problem that drove the most recent
        // rejection (if any). Clear the rejection-cause flag so a later
        // CantFollow (e.g., from a future stuck-displacement scenario)
        // doesn't spuriously inherit the toward-leader override.
        _lastPathFailureWasRejection = false;
    }

    /// <summary>
    /// Option B refactor — implements the policy that used to live as Fix AB-1
    /// inline inside Navigation.PathCalculatedCallback's stale-result branch.
    ///
    /// Original evidence (log-68 11:03:43:081 → 11:04:12:949 — assist stuck
    /// for 30s in Durotar corridor at &lt;-517.30, -4448.72&gt; with target
    /// &lt;-510.42, -4449.06&gt; only 6.9y east; PPather's navmesh ends on a
    /// hillside, every search returned "closest spot &lt;-508.80, -4441.2,
    /// Z=67.11&gt;" with "Closest spot is too far from target. 9.2&gt;5",
    /// taking ~10s per attempt to fail):
    ///
    /// When the pather takes longer than the 3-second waitingForPathResult
    /// watchdog to return a failure, the result arrives AFTER a new request
    /// for the same (start, end) has been enqueued and made the previous
    /// one stale. The PathCalculatedCallback stale-result branch used to
    /// drop the result without informing HandleRepeatedNoPath or
    /// OnPathFailed subscribers — so the same-(start, end) failure
    /// pattern that HandleRepeatedNoPath would have detected after 3 hits
    /// NEVER accumulated. Each failure was silently stale-dropped, leaving
    /// FFG to time out at the 30s TickNavActiveTimeout wall.
    ///
    /// Log-68 evidence: 10 successive ENQUEUE events with identical
    /// start=&lt;-517.30, -4448.72&gt; end=&lt;-510.42, -4449.06&gt; dist=6.89y
    /// over the 30-second window, original reqId=328 callback arriving at
    /// 11:03:53:416 reporting "Ignoring stale path result … activeReqId=331"
    /// — the failure information was discarded.
    ///
    /// Fix: when a stale empty-path result arrives whose (start, end)
    /// matches the currently in-flight request's (start, end) within 1y
    /// (Navigation pre-computes the match and only fires the event when
    /// true), count it toward a dedicated stale-no-path counter. After
    /// StaleEmptyResultsBeforeOnPathFailed (3) consecutive matches, signal
    /// Escalate so Navigation fires OnPathFailed and sets a 2s cooldown.
    /// The 2s cooldown is long enough that the next FFG tick (after
    /// OnPathFailed fires) doesn't immediately re-trigger a path request
    /// inside Navigation_OnPathFailed's rewind branch before that branch
    /// sets the rewind anchor and waits for it to resolve.
    ///
    /// Intentionally NOT routed through HandleRepeatedNoPath because that
    /// function's count==2 branch builds a DIRECT route to the unreachable
    /// target (gated to dist &lt;= 5y by the moved Fix AD policy in
    /// Navigation_OnRepeatedNoPathDirectRouteDecision below). For the
    /// log-68 corridor that would still walk the bot straight into the
    /// hill geometry at dist=6.89y; the stale-counter path here uses ONLY
    /// the OnPathFailed escalation, which is safe regardless of why the
    /// pather couldn't find a route.
    /// </summary>
    private void Navigation_OnStaleEmptyPathMatchesActive(StaleEmptyPathSnapshot snap)
    {
        if (_navState != NavState.NavigatingToLeader)
            return;

        // Reset the counter if the active (start, end) has rolled to a
        // different one since the last stale match we counted. (Navigation
        // doesn't fire the event for results that don't match the active
        // request, so we don't need to handle the !matches case here.)
        bool sameAsLastStale =
            snap.ResultStartW.WorldDistanceXYTo(_staleEmptyLastStartW) < 1f &&
            snap.ResultEndW.WorldDistanceXYTo(_staleEmptyLastEndW) < 1f;
        if (!sameAsLastStale)
        {
            _staleEmptyLastStartW = snap.ResultStartW;
            _staleEmptyLastEndW = snap.ResultEndW;
            _staleEmptySameCount = 0;
        }
        _staleEmptySameCount++;

        logger.LogWarning(
            $"[FFG] [FIX-FIRE] AB-1: stale no-path #{_staleEmptySameCount}/" +
            $"{StaleEmptyResultsBeforeOnPathFailed} for active (start, end). " +
            $"start={snap.ResultStartW} end={snap.ResultEndW}");

        // ── Fix AU (log-81 16:00:19:837 → 16:00:47:157) ──
        //
        // COMPOUND STUCK + STALE-EMPTY ESCALATION
        //
        // When TickActiveStuckDetection has already set _activeStuckReported
        // = true (bot moved < ActiveStuckMinMovementWorld in
        // ActiveStuckThresholdSec — confirmed real-world stationary) AND
        // Fix AB-1 has just observed a stale empty-path matching the
        // currently-active in-flight request's (start, end) within 1y
        // (pather-side evidence that the bot's current position is not
        // routable to its current target), the bot will not recover by
        // waiting. Escalate directly to CantFollow without the 3-count
        // gate or the 30 s TickNavActiveTimeout wall.
        //
        // Evidence (log-81 mob 2 post-combat sequence):
        //   16:00:14:939  leader Consume Corpse (combat ends).
        //   16:00:15:054  assist plan: Combat → Follow Focus.
        //   16:00:15:085  Path 95 enqueued from <-750.13, -4275.64>
        //                 to <-756.81, -4277.68>. Returns pathLen=53
        //                 (long curved route). Bot navigates north.
        //   16:00:16:104  Path 96 from <-751.48, -4271.68>.
        //                 Returns pathLen=7. Fix AA drops 1 rear node.
        //   16:00:17:305  Path 97 enqueued from <-755.22, -4272.88>
        //                 to <-761.46, -4275.77> (6.88 y SW). PPather
        //                 will throw NullReferenceException on this
        //                 request (and every subsequent request from
        //                 the bot's eventual stuck position).
        //   16:00:19:837  FFG: "Stuck while navigating — moved only
        //                 0.00 y in 2.5 s at <-755.4346, -4272.968, 0>"
        //                 — TickActiveStuckDetection fires;
        //                 _activeStuckReported = true; status = Stuck.
        //   16:00:20:317  First Path-wait timeout (3 s elapsed since
        //                 Path 97 enqueue with no callback). Re-enqueue
        //                 reqId 98 with identical (start, end).
        //   16:00:23:327, 16:00:26:335, 16:00:29:346, 16:00:32:361,
        //                 16:00:35:370, 16:00:38:381, 16:00:41:391,
        //                 16:00:44:399  Same 3 s re-enqueue cycle
        //                 repeats. Bot has not moved (still
        //                 <-755.43, -4272.97>); _activeStuckReported
        //                 stays true (no 3 y resume movement, not in
        //                 combat, NavState unchanged, no ResetNavState).
        //   16:00:27:306  PPatherService logs
        //                 "Object reference not set to an instance of
        //                 an object." PathFinderThread "processes" the
        //                 oldest queued request (reqId 97 from 10 s
        //                 earlier) and emits a stale empty result.
        //   16:00:27:323  Navigation: "Ignoring stale path result
        //                 reqId=97 waitingReqId=100 activeReqId=100"
        //                 — but Fix AB-1's pre-check matches the
        //                 stale empty's (start, end) against the
        //                 active (start, end) within 1 y and fires
        //                 OnStaleEmptyPathMatchesActive.
        //   16:00:27:324  Fix AB-1: "stale no-path #1/3 for active
        //                 (start, end). start=<-755.22, -4272.88>
        //                 end=<-761.46, -4275.77>".
        //   16:00:37:317  Fix AB-1: "stale no-path #2/3" — second
        //                 stale empty for the same (start, end), 10 s
        //                 later (one full PPather queue cycle).
        //   16:00:47:157  TickNavActiveTimeout fires (30.0 s without
        //                 NavigationProgressResetYards of movement);
        //                 escalates to CantFollow. Fix AB-1 would
        //                 have hit #3/3 at approximately the same
        //                 instant — the 3-count gate provided no
        //                 benefit over the timeout.
        //
        // Without Fix AU, the bot sat at <-755.43, -4272.97> for
        // 27.3 seconds after FFG reported Stuck, doing nothing
        // productive — every 3 s re-enqueue produced the same
        // un-routable (start, end), every 10 s PPather call threw
        // NRE, and the chase watchdog (TickChaseWatchdog, 15 s on
        // Navigation.ChaseSinceBestSec) was silent because chase
        // tracking only updates when a route is active, and the
        // bot had no route the entire time.
        //
        // With Fix AU, escalation fires at 16:00:27:324 — the
        // FIRST Fix AB-1 hit observed while _activeStuckReported
        // is true. CantFollow starts ~19.8 s earlier; the leader's
        // AssistRequestReturn rising edge fires ~20 s earlier;
        // rendezvous shifts ~20 s earlier. From a user-visible
        // standpoint, the "long stuck" window collapses from
        // 32 s (Stuck flag → escape exit → still walking back)
        // down to ~12 s.
        //
        // Why count == 1 is safe (no false positives):
        //   - _activeStuckReported requires 2.5 s of < 1 y movement
        //     before it sets true. That is real-world position
        //     evidence — not a heuristic, the bot has actually
        //     been stationary.
        //   - Fix AB-1 firing requires the stale empty-path
        //     result's (start, end) to match the CURRENTLY ACTIVE
        //     in-flight request's (start, end) within 1 y.
        //     Navigation pre-computes the match and only raises
        //     the event when true. That is pather-side evidence
        //     the bot's current position cannot be routed to its
        //     current target.
        //   - Both signals are independently strong. Together,
        //     they prove the bot will not recover by waiting:
        //     waiting requires either the bot moving (it isn't)
        //     or the pather succeeding for the same (start, end)
        //     it just failed on (it won't, the bot is stationary
        //     so the failing input is unchanged).
        //   - The existing 3-count gate (StaleEmptyResults-
        //     BeforeOnPathFailed = 3) is itself a stationarity
        //     proxy — requiring "same (start, end) within 1 y
        //     for 3 consecutive failures" implies the bot hasn't
        //     moved much. With _activeStuckReported already
        //     proving stationarity directly, count = 1 carries
        //     the same evidentiary weight as count = 3 without
        //     the Stuck flag.
        //
        // Why this is additive (doesn't break existing logic):
        //   - When the bot is moving (Stuck flag not set) and
        //     the pather keeps failing for near-stationary
        //     positions, the existing 3-count path still fires:
        //     snap.Escalate → Navigation.OnPathFailed →
        //     Navigation_OnPathFailed rewinds to LastSafeAnchor
        //     on first failure, EnterCantFollow on second. This
        //     branch is unchanged.
        //   - When the bot is Stuck and Fix AB-1 fires, Fix AU
        //     short-circuits to CantFollow. The OnPathFailed
        //     rewind path would not help here anyway —
        //     LastSafeAnchor is typically <10 y from the stuck
        //     position, and PPather's NRE is local to the area
        //     (in log-81, paths from <-755.43, -4272.97> failed
        //     for every target; paths from <-740.10, -4259.93>
        //     after escape succeeded in 15 ms). Walking the bot
        //     a few yards backward then forward again would
        //     just re-enter the same NRE region.
        //
        // Counter reset matches the existing 3-count escalation
        // path — both clear _staleEmptySameCount + last-start/end
        // so the next NavigatingToLeader cycle (after CantFollow
        // exit via Fix AO / leader-arrived / segment-clear /
        // 120 s timeout) starts fresh.
        //
        // Note: OnEnter (FFG) and Update transitions to
        // NavigatingToLeader do not currently reset the Fix AB-1
        // counter explicitly; OnEnter clears it via lines
        // 745-749, ResetNavState does not. Since Fix AU resets
        // here on fire and the existing 3-count branch resets
        // on fire, the only way the counter persists across a
        // NavigatingToLeader cycle is if neither branch fires
        // during the cycle — which means there were < 3 stale
        // empties and Stuck was never reported, i.e. the bot
        // was navigating successfully. The counter being stale
        // in that case is harmless because the same-as-last
        // check at the top of this function will reset it as
        // soon as a different (start, end) appears.
        if (_activeStuckReported)
        {
            // Fix AX (Route B Part 1): try direct-route recovery before
            // escalating to CantFollow. The first attempt per cycle pushes
            // a direct one-hop route via Navigation.BuildDirectRouteFallback;
            // if the bot can physically walk, this typically recovers within
            // a few seconds. If it can't (subsequent stale-empties arrive
            // with bot still stationary), the counter exhausts and we fall
            // through to the original Fix AU CantFollow path.
            _directRouteAttemptsInCycle++;

            if (_directRouteAttemptsInCycle <= MaxDirectRouteAttemptsPerCycle)
            {
                logger.LogWarning(
                    $"[FFG] [FIX-FIRE] AX: assist stationary (_activeStuckReported=true) " +
                    $"AND stale-empty matches active. Requesting direct-route " +
                    $"recovery (attempt #{_directRouteAttemptsInCycle}/" +
                    $"{MaxDirectRouteAttemptsPerCycle}). Setting " +
                    $"snap.TriggerDirectRoute=true — Navigation will push a " +
                    $"one-hop route to the unreachable target via " +
                    $"BuildDirectRouteFallback. If the bot doesn't move, " +
                    $"chase/no-progress watchdogs will fire OnPathFailed within " +
                    $"~15s. start={snap.ResultStartW} end={snap.ResultEndW}");
                _staleEmptySameCount = 0;
                _staleEmptyLastStartW = default;
                _staleEmptyLastEndW = default;
                snap.TriggerDirectRoute = true;
                return;
            }

            // Counter exhausted — fall back to the original Fix AU behavior
            // (CantFollow at first stale-empty when stuck). Direct-route did
            // not recover the bot; the position is unrecoverable by walking.
            logger.LogError(
                $"[FFG] [FIX-FIRE] AU: (after Fix AX exhausted) assist already in " +
                $"active Stuck state AND pather has returned a stale empty-path " +
                $"matching the active (start, end) within 1y. Direct-route " +
                $"recovery was attempted {_directRouteAttemptsInCycle - 1} " +
                $"time(s) earlier this cycle (max=" +
                $"{MaxDirectRouteAttemptsPerCycle}) and did not recover. " +
                $"Escalating directly to CantFollow at stale-empty count " +
                $"#{_staleEmptySameCount} — skipping the 3-count gate " +
                $"(StaleEmptyResultsBeforeOnPathFailed={StaleEmptyResultsBeforeOnPathFailed}) " +
                $"AND the {NavigationActiveTimeoutSec:0}s TickNavActiveTimeout " +
                $"wall. start={snap.ResultStartW} end={snap.ResultEndW}");
            _staleEmptySameCount = 0;
            _staleEmptyLastStartW = default;
            _staleEmptyLastEndW = default;
            EnterCantFollow();
            return;
        }

        if (_staleEmptySameCount >= StaleEmptyResultsBeforeOnPathFailed)
        {
            logger.LogError(
                $"[FFG] [FIX-FIRE] AB-1: pather failed {_staleEmptySameCount} consecutive " +
                $"times for the same (start, end) — signalling Escalate. " +
                $"Navigation will fire OnPathFailed. start={snap.ResultStartW} end={snap.ResultEndW}");
            _staleEmptySameCount = 0;
            _staleEmptyLastStartW = default;
            _staleEmptyLastEndW = default;
            snap.Escalate = true;
            snap.EscalateCooldownMs = 2000;
        }
    }

    /// <summary>
    /// Option B refactor — implements the policy that used to live as Fix AD
    /// inline inside Navigation.HandleRepeatedNoPath's count==2 branch.
    ///
    /// Original evidence (log-70b 12:32:40:629→12:33:12:706, assist stuck
    /// for 32 seconds in a narrow corridor between a hill and a wall; user
    /// description: "ran directly into the hill, then did unstuck that got
    /// it part way into the corridor while running against the side of the
    /// hill, before it gave up"):
    ///
    /// The original count==2 branch unconditionally pushed a direct route
    /// to the unreachable target whenever dist &lt;= 30y. That permitted the
    /// dangerous walk-into-terrain case: when PPather has just failed
    /// twice for the same (start, end) at non-trivial distance (8.16y in
    /// log-70b), it's signalling that the geometry doesn't admit a path.
    /// Walking the bot blindly toward the unreachable point doesn't fix
    /// the unreachability — it rams the bot into whatever obstacle PPather
    /// was failing to route around. In log-70b reqId=113 returned pathLen=0
    /// at 12:32:40:629 with dist=8.16y, the direct-route push fired at
    /// 12:32:40:630, and the bot walked ~5y SE from &lt;-468.88, -4398.20&gt;
    /// to &lt;-465.23, -4401.78&gt; — past the corridor's safe zone and into
    /// the navmesh dead area. From there every subsequent path request
    /// stale-timed-out; 30s later TickNavActiveTimeout fired CantFollow.
    ///
    /// Fix: bound the direct-route push to dist &lt;= DirectRouteMaxSafeYards
    /// (5y). For dist &gt; 5y, signal Veto so Navigation skips the push,
    /// resets its no-path counter, applies a 1s cooldown, and fires
    /// OnPathFailed.
    ///
    /// Why 5y:
    ///   - Just under FollowingMaxYards (7y) — below this, the bot is
    ///     essentially adjacent to its target and a brief direct walk is
    ///     unlikely to do harm even on a navmesh hiccup.
    ///   - Short enough that worst-case walk-into-terrain is bounded.
    ///   - 5y &gt; POP_DIST (3.6y) so the pushed wp doesn't immediately pop
    ///     before any movement — the push can actually achieve progress
    ///     when it's safe to use.
    ///
    /// For log-70b's case (dist=8.16y &gt; 5y) the Veto fires OnPathFailed
    /// at the equivalent moment. FFG's Navigation_OnPathFailed:
    /// _navAttempt=0 → rewind to LastSafeAnchor (set during earlier
    /// successful route progress in the navigable corridor). If that also
    /// fails: _navAttempt=1 → EnterCantFollow at ~12:32:41 vs 12:33:12
    /// actual. Saved ~31s, AND the CantFollow occurs from a navigable
    /// position where Projection10/20/30 escape (Fix AB-2) has a real
    /// chance of succeeding instead of running 0.0y displacement against
    /// the dead zone.
    /// </summary>
    private void Navigation_OnRepeatedNoPathDirectRouteDecision(RepeatedNoPathDecisionSnapshot snap)
    {
        if (_navState != NavState.NavigatingToLeader)
            return;

        const float DirectRouteMaxSafeYards = 5.0f;
        if (snap.DistYards > DirectRouteMaxSafeYards)
        {
            // Fix AY (Route B Part 2): the original Fix AD veto exists to
            // prevent the dangerous "walk-into-terrain" case (log-70b: bot
            // walked from a navigable corridor into a navmesh dead zone
            // when the pather failed twice at 8.16y). The veto is correct
            // when the bot is still actively navigating — there's no reason
            // to abandon careful pathing if the bot can keep trying.
            //
            // BUT when the bot is _activeStuckReported (TickActiveStuck-
            // Detection has confirmed < 1y movement in 2.5s), the bot's
            // current position is unrecoverable by waiting for the pather
            // to succeed: the pather will keep failing for the same (start,
            // end) until the bot moves, and the bot won't move without a
            // route. In that case the trade-off inverts: walking blindly
            // toward the target risks walking into terrain (bounded by the
            // chase/no-progress watchdog cascade, ~15s), but doing nothing
            // guarantees a 30s TickNavActiveTimeout → CantFollow cycle from
            // a dead position where the escape sequence has fewer options.
            //
            // log-81 evidence (mob 2 post-combat): bot at <-755.43, -4272.97>,
            // target <-761.46, -4275.77>, dist 6.65y. PPather threw NRE on
            // every request. Bot was _activeStuckReported. The 6.65y > 5y
            // case would have been vetoed by Fix AD — walking forward 6.65y
            // would have exited the NRE-prone area (paths from <-740.10,
            // -4259.93> after the eventual escape succeeded in 15ms),
            // delivering ~20s of recovery savings vs the actual 32s stuck.
            //
            // Watchdog cascade as the safety net: if walking toward the
            // target rams the bot into terrain (log-70b case), the chase
            // and no-progress watchdogs fire OnPathFailed within ~15s,
            // routing through Navigation_OnPathFailed → rewind to
            // LastSafeAnchor or CantFollow. The original Fix AD path
            // (veto → 1s cooldown → OnPathFailed → rewind/CantFollow)
            // takes ~1-2s longer in the bot-moving case, but in the bot-
            // stationary case the watchdog is the SAME safety mechanism
            // that would catch a bad direct-route push.
            //
            // Why the bypass is safe (no regression on log-70b):
            //   - log-70b had the bot in a NAVIGABLE corridor walking
            //     normally; the navmesh briefly hiccupped and direct-route
            //     would have pushed the bot off the corridor into the dead
            //     zone. In that scenario, _activeStuckReported would be
            //     FALSE because the bot had been moving fine — the Fix AY
            //     bypass doesn't fire and the Fix AD veto still triggers.
            //   - The bypass only activates when the bot has ALREADY been
            //     stationary for 2.5s (the TickActiveStuckDetection
            //     threshold), meaning we're in the "nothing else has
            //     worked" regime where extra risk is justified by greater
            //     potential upside.
            if (_activeStuckReported)
            {
                logger.LogWarning(
                    $"[FFG] [FIX-FIRE] AY: bypassing Fix AD veto on direct-route push " +
                    $"because bot is stationary (_activeStuckReported=true). " +
                    $"count={snap.SameNoPathCount} dist={snap.DistYards:0.0}y " +
                    $"> {DirectRouteMaxSafeYards:0.0}y. Walking from a dead " +
                    $"position is preferable to waiting; chase/no-progress " +
                    $"watchdogs will catch a bad direct-route push within ~15s. " +
                    $"start={snap.StartW} end={snap.EndW}");
                return; // no veto, allow Navigation's direct-route push
            }

            logger.LogWarning(
                $"[FFG] [FIX-FIRE] AD: signalling Veto on direct-route push " +
                $"(count={snap.SameNoPathCount} dist={snap.DistYards:0.0}y > " +
                $"{DirectRouteMaxSafeYards:0.0}y). Pather has failed twice for " +
                $"this (start, end) at a non-trivial distance; a direct walk " +
                $"would ram the assist into terrain. Navigation will fire " +
                $"OnPathFailed. start={snap.StartW} end={snap.EndW}");
            snap.Veto = true;
            snap.VetoCooldownMs = 1000;
        }
    }
}
