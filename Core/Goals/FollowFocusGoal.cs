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

    /// <summary>
    /// Fix DN (log-129 evidence): sanity cap on leader distance for the
    /// "hold while in combat range during a pre-combat engage" guard. While the
    /// assist is within its own combat range of the target and the leader is
    /// approaching (HasApproachStart) but combat has not started, the assist holds
    /// position instead of pather-chasing the leader — UNLESS the leader is farther
    /// than this, in which case a genuinely departing leader is still followed so
    /// the assist is not stranded. Generous (beyond a caster's combat range) so it
    /// only releases the hold when the leader has clearly left the engagement.
    /// </summary>
    private const float HoldInCombatRangeMaxLeaderYards = 30f;

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

    /// <summary>
    /// Leader must pause when assist exceeds this distance.
    ///
    /// <para>Fix BF (log-91, log-92): raised from 20y to 25y to accommodate
    /// the new pause-at-waypoint semantics (the leader's pause check fires at
    /// waypoint arrival rather than every tick). With Option D semantics,
    /// distance can overshoot the threshold by up to one route stride
    /// (~10y) between waypoint events, so a 25y threshold yields a
    /// worst-case actual pause distance of ~35y — comfortably within the
    /// 30-40y healer range floor.</para>
    ///
    /// <para>Hysteresis with LeaderResumeYards=15y is now 10y (= ~one stride),
    /// preventing the flapping window the previous 5y hysteresis allowed
    /// (per the comment on the resume side at FRG line 1092-1094).</para>
    /// </summary>
    public const float LeaderPauseYards = 25f;

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
        DirectedFallback,
        Exhausted
    }
    private CantFollowEscapePhase _escapePhase = CantFollowEscapePhase.NotStarted;
    private DateTime _escapePhaseStartUtc;
    private Vector3 _escapePhaseStartPos;
    private Vector3 _escapeTargetW;
    private bool _escapeUnstuckUsed;
    // 1b: DirectedFallback sweep step (1->10y/0deg, 2->20y/+120deg, 3->30y/-120deg, >3->Exhausted/hold).
    private int _directedFallbackStep;

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
    // Fix BH-2 (log-94 01:26:18:001 → 01:26:46:807, 28.8-second 1630-event
    // CPU loop). Minimum distance to the nearest route waypoint required
    // before "toward-route" basis (Fix BD) is selected for the CantFollow
    // escape projection. Must be > EscapeArrivalYards or the BG-2 clamp
    // (effectiveYards = routeBasisDist) produces a projection target
    // INSIDE the arrival radius — arrival check fires on the very next
    // tick before the bot can move, the phase restarts, the same
    // projection re-fires, and the phase budget timer is constantly
    // reset (never reaching the 3-second escalation threshold).
    //
    // The 1.0y headroom above EscapeArrivalYards guarantees at least
    // ~1y of useful displacement room before "arrived" can fire,
    // giving the phase budget a chance to time-out-and-escalate when
    // the bot is physically wedged. Below this threshold, the basis
    // falls through to away-from-leader / reversed-facing — different
    // geometry, no clamp interaction, no instant-arrival loop.
    private const float MinRouteBasisDistance = EscapeArrivalYards + 1.0f;


    // Fix BR (log-98 22:06:14 → 22:06:56, assist drifted idx 11→9→8→4 over successive resyncs):
    // distance threshold below which the assist abandons route-walk and heads directly to
    // the approach-start anchor (Fix BP behavior). When the assist is FURTHER than this,
    // abandoning route just drags the assist off-route since it can't reach the anchor
    // before combat ends anyway — defer to RouteWalk in that case (original Turn 3 behavior).
    // 25y reflects realistic closing budget: ~5y/s × 5s typical approach window.
    private const float AnchorReachableYards = 25.0f;

    // Fix BQ (log-98) + Fix BU (log-99): anchor-mode hysteresis. The leader's
    // HasApproachStart can flicker for ~1.5-3.4s windows because the leader's ATG
    // re-enters during pathing escalation (log-99: 23 ATG.OnEnter events vs 9 BN/BS
    // completions; same-target re-entries every 1-3s during target 872063's 30s
    // engagement attempt). Each flicker is an OnExit→ClearApproachStart→OnEnter→
    // SetApproachStart cycle, so the assist sees HasApproachStart toggle.
    //
    // Original BQ measured time since Anchor-mode ENTRY (1500ms window). Log-99 showed
    // flips at 1.5s, 1.5s, 1.5s, 1.8s, 2.3s, 2.5s, 2.7s, 2.7s, 2.9s, 3.2s, 3.4s — most
    // slipping past the 1500ms ceiling because the timer ticked from entry, not from
    // "last seen true". A 2500ms entry-based ceiling would catch some but miss many.
    //
    // Fix BU: measure time since the last tick that saw HasApproachStart=true. Each
    // true tick refreshes the timer; the sticky window only counts elapsed time AFTER
    // HasApproachStart has been continuously false. This naturally handles the
    // flicker pattern: while HasApproachStart toggles true/false rapidly, the timer
    // keeps resetting on the true ticks, so the sticky window never expires. The
    // window only "starts" when the leader truly stops approaching. With the leader's
    // ATG re-entry gaps observed at ≤3.5s, a 2500ms window catches the typical case;
    // longer gaps mean the leader has genuinely moved on (status changes also bypass
    // the hysteresis via the Patrolling gate).
    //
    // ── Fix BY (log-102 PTG cycle measurement) ──
    // Bumped from 2500ms to 4500ms after observing PTG cycle durations across log-102:
    //   median ATG→PTG→ATG cycle: 2.4s
    //   max (excluding 13s outlier): 3.298s at 57:29.413 → 57:32.711
    //   plus typical poll lag of ~250ms on either side
    // The 2500ms window narrowly missed the 3298ms case: at 57:32.097 BW's sticky
    // expired (sinceLastTrueMs≈2.7s), the latch reset, and the bot flipped
    // PositionChase → RouteWalk with drift=18.3y — a large visible turn-back during
    // what was actually a single continuous approach phase.
    // Also fixes the 56:16.391 turn-back (DIAG-BW: sinceLastTrueMs=2607ms, mode=Anchor,
    // 7.9y drift) — that case was 107ms past the old 2500ms ceiling.
    // 4500ms covers all observed normal PTG cycles with margin. The 13.1s outlier
    // (56:49.692 → 57:02.806) is left uncovered — that case represents a leader
    // pathing failure, not a normal approach phase; protecting it would mean the
    // assist stays parked at a stale anchor for 13+ seconds.
    // Affects both BU (mode=Anchor protection) and BW (latch+PositionChase
    // protection). 4.5s of Anchor-mode persistence on a truly-ended approach is a
    // tolerable cost (assist still heading roughly toward leader area) for
    // eliminating the mid-approach turn-backs.
    private const int AnchorModeStickyMs = 4500;
    private DateTime _anchorModeEnteredUtc = DateTime.MinValue;     // retained for log/diagnostic clarity
    private DateTime _lastHasApproachStartUtc = DateTime.MinValue;  // Fix BU: refreshed each tick HasApproachStart=true
    private Vector3 _lastAnchorTarget;  // anchor coords cached for hysteresis return value

    // ── Fix CJ (log-112 20:36:55 evidence) — Idle-entry guard after FFG.OnEnter ──
    //
    // User complaint: "the assist was sitting in place while the leader was
    // approaching the mob and didn't move until they actually went into combat"
    //
    // Evidence (log-112, last combat at 20:37:00:746):
    //   20:36:53:554  FFG.OnEnter (post-combat, leader 22.8y away → NavigatingToLeader)
    //   20:36:55:826  Leader ATG.OnEnter (target 952768)
    //   20:36:55:887  Leader publishes approach-start anchor
    //   20:36:55:990  *** Assist: "Reached follow position (dist=6.9y) — entering Idle" ***
    //                  (only 164ms after leader's ATG.OnEnter; the leader has JUST
    //                  started the interact approach but the assist sees stale leader
    //                  position from the previous poll cycle, ~250-500ms older)
    //   20:36:55:990 → 20:36:57:055  *** 1.07s of assist Idle ***
    //                  Leader is moving east toward mob via interact-key (~4y/s).
    //                  Assist sees HasApproachStart=true once polling propagates.
    //   20:36:57:055  "Leader resumed patrol/started approaching ...
    //                  starting navigation immediately to match (dist=10.5y)"
    //                  Gap has grown from 6.9y → 10.5y during the idle period.
    //   20:37:00:746  Combat begins. Assist 9.8y from leader, never fully catches up.
    //
    // Root cause: the assist's polling latency for leader.HasApproachStart is
    // ~250ms-1s. In Combat-3 (log-112), the assist crossed the Idle threshold
    // (dist < 7y) in the same poll cycle the leader was publishing the ATG
    // anchor. The assist's view of the leader's state was one poll old —
    // HasApproachStart still false — so the Idle entry check passed. The leader
    // then accelerated toward the mob while the assist sat still for ~1s
    // waiting for the next poll cycle to deliver HasApproachStart=true.
    //
    // The existing leaderStartedApproaching detection in UpdateIdle (line ~2062)
    // DOES catch the transition once polling propagates, but by then 0.5-1.5s
    // of idle has already elapsed and the gap has grown by 3-5y. The user sees
    // this visually as the assist "sitting in place" while the leader moves.
    //
    // Fix CJ: don't enter Idle in the first IdleGuardAfterEnterMs (3000ms)
    // after FFG.OnEnter. Post-combat FFG.OnEnter is followed within ~1-3s by
    // the leader's next ATG (the usual pull cadence). During this 3s guard,
    // the assist stays NavigatingToLeader regardless of distance — the bot
    // tracks the leader's body via PositionChase mode and is poised to react
    // (mode-switch to Anchor) the moment HasApproachStart=true is observed
    // via GetNavigationTarget, with no Idle exit overhead.
    //
    // After 3s elapsed (no ATG started yet), the bot is allowed to Idle
    // normally. If the leader's ATG happens later, the existing
    // leaderStartedApproaching detection in UpdateIdle handles it.
    //
    // Why 3s? The post-combat → ATG transition typically takes 1-3s:
    //   - Loot/ConsumeCorpse (~1-2s)
    //   - Brief Follow Route patrol (~0.5-1s)
    //   - ATG.OnEnter (next mob found)
    // 3s covers the typical case. Longer windows (e.g. 5s) start to feel like
    // "assist isn't relaxing properly" when the leader is genuinely paused.
    private const int IdleGuardAfterEnterMs = 3000;  // Fix CJ
    private DateTime _ffgEnterTimeUtc = DateTime.MinValue;  // Fix CJ: set in OnEnter, used to gate Idle entry
    private DateTime _lastFixCJSuppressLogUtc = DateTime.MinValue;  // Fix CJ: throttle suppression diagnostic to ≤2 Hz

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
    /// Fix DM (log-128 00:16:33 evidence) — PERSISTENT "anchor reached" latch.
    /// <para>
    /// <see cref="_approachAnchorColocated"/> commits the assist to
    /// position-chasing once it reaches the approach anchor, but it is reset on
    /// every <c>FFG.OnEnter</c>. When the plan flickers ATG→FFG mid-approach
    /// (log-128: <c>incombatrange</c> flips true the instant the interact-walk
    /// brings the mob into combat range, before combat actually starts, which
    /// fails ATG's <c>incombatrange=false</c> precondition and drops the planner
    /// to FFG), that OnEnter reset wipes the latch. FFG then re-locks to the
    /// approach anchor the assist has ALREADY reached and walked PAST while
    /// approaching the mob, turning it ~180° backward to the anchor (the
    /// user-observed "full 360° turn in place before the first Smite").
    /// </para>
    /// <para>
    /// This latch persists across FFG.OnEnter (it is NOT reset there). Once the
    /// assist has been to the anchor for the current pull, the assist never
    /// turns back to it — it keeps position-chasing forward toward the
    /// leader/target so it continues closing the gap (valuable for anchors far
    /// from the mob and for melee classes that must reach melee range). Scoped
    /// to the anchor position via <see cref="_anchorReachedPos"/>: a NEW pull
    /// (anchor more than <see cref="AnchorReachedResetYards"/> away) re-arms
    /// normal Anchor-mode rendezvous. Reset on a new anchor and when the
    /// approach phase ends (<c>HasApproachStart=false</c> observed).
    /// </para>
    /// </summary>
    private bool _anchorReached;
    private Vector3 _anchorReachedPos;


    /// <summary>
    /// Fix DM: a published anchor farther than this from <see cref="_anchorReachedPos"/>
    /// is treated as a NEW pull, re-arming Anchor-mode rendezvous. The same pull's
    /// anchor is republished at identical coordinates (leader Fix BT reuses a stable
    /// anchor within its 10s grace), so same-anchor drift is ~0y; a fresh pull's
    /// anchor is the leader's new stop, almost always well beyond this.
    /// </summary>
    private const float AnchorReachedResetYards = 6.0f;

    /// <summary>
    /// Fix DP (log-129 01:06:30→34): reactive route-walk-first fallback for a failing
    /// direct-Anchor pather. When the direct Anchor approach stops making progress toward
    /// the anchor (the defective-corridor pather times out / null-refs / returns a detour),
    /// <see cref="_anchorPatherFallback"/> is armed and the approach is deferred to
    /// route-walk: the assist advances up to the leader's own route index (the anchor is the
    /// leader's position) using the clean usePather=false route-walk, then the existing
    /// combat-handoff pathers the short final hop. <see cref="_anchorFallbackUsed"/> latches
    /// once that handoff fires so the final hop is not re-deferred (loop guard). Progress is
    /// tracked via the closest distance achieved to <see cref="_dpTrackedAnchor"/>; the
    /// tracker resets when the anchor moves (new pull) or the approach ends.
    /// </summary>
    private bool _anchorPatherFallback;
    private bool _anchorFallbackUsed;
    private float _anchorApproachBestDist = float.MaxValue;
    private DateTime _anchorApproachProgressUtc = DateTime.MinValue;
    private Vector3 _dpTrackedAnchor;
    private const double AnchorProgressTimeoutMs = 2000.0;
    private const float AnchorProgressEpsilonYards = 1.0f;

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

    // Route-walking Turn 3.5: leader route index cache (30s TTL).
    //
    // Caches the route index matched to leader's published TargetWaypoint.
    // Used as fallback in TryFindLeaderRouteIndex when leader.HasTargetWaypoint
    // is false (mutually exclusive with HasApproachStart in the leader's
    // state machine — without the cache, CheckShouldDefer is dead code
    // during every approach event). Fix BI-2: PRESERVED across FFG
    // OnExit/OnEnter boundaries; TTL handles staleness, preserving the
    // cache eliminates the ~1-2s stale-PositionChase window after combat.
    // Sentinel -1 = no cached value.
    // See HANDOFF Fix BI-2 + SCOPING Turn 3.5 for full evidence.
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

    // ── Fix DA (log-123 16:49:58 corridor between hill and tree) ──
    //
    // Counter for consecutive intermediate-step retries on a positional
    // U-turn rejection. See Navigation_OnPathFailed for the full mechanism.
    // Reset to 0 wherever _navAttempt is reset (StartNavigatingToLeader,
    // ResetNavState) so each fresh navigation / cleared pocket gets a full
    // step budget.
    private int _daIntermediateStepCount;

    // Short straight-line step length toward the rejected far target.
    //
    // MUST stay ≤ AvgDistance*2 for a single pushed waypoint so Navigation's
    // usePather test (Navigation.cs:3651 `usePather = distance > MaxDistance
    // || distance > AvgDistance*2`) evaluates FALSE. For a single waypoint
    // AvgDistance = OutDoorMinDistance = 3y, so the threshold is 6y; 5y keeps
    // usePather=false with margin. That matters because usePather=false makes
    // Navigation build a DIRECT straight-line route (builtDirectRoute) rather
    // than calling the pather — and the pather is precisely what is broken in
    // this corridor (it returns ~140-node U-turn detours; see OnPathFailed).
    // A direct straight-line step is exactly how the LEADER traverses the
    // corridor (it logged builtDirectRoute at the same spot and walked
    // straight through), so DA reproduces the leader's working mechanism
    // instead of relying on the pather returning a clean short path.
    private const float DAStepYards = 5.0f;

    // Cap on consecutive intermediate steps before falling through to the
    // existing rewind/CantFollow escalation. One 5y direct step already drops
    // the remaining route leg under the route-span usePather threshold (~14y
    // for the assist's typical 2-waypoint span), after which normal RouteWalk
    // resumes directly; 3 steps (15y of guaranteed forward progress) covers
    // deeper pockets while bounding the probe if the target is a genuine dead
    // zone rather than a pather-defective but walkable corridor.
    private const int DAMaxIntermediateSteps = 3;

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

    /// <summary>
    /// Fix BH-3 (log-94 01:25:48:280 → 01:26:18:001): true on ticks where
    /// the trailing-by-one cap parked path fires (assist sits on its
    /// allowed-advance route waypoint, waiting for the leader to publish
    /// a higher-index waypoint). Read by <see cref="TickActiveStuckDetection"/>
    /// to suppress the no-movement stuck trigger — the bot is not stuck,
    /// it's correctly idle. Set in the parked branch of <see cref="OnRetrySharedWaypoint"/>
    /// (~line 5704), cleared at the start of every <see cref="UpdateNavigatingToLeader"/>
    /// invocation so a single non-parked tick re-arms the detector.
    ///
    /// Without this, the assist's no-movement triggers Stuck status
    /// (~2.5s into the park), the leader sees Status==Stuck and STAYS
    /// paused (already paused for distance), the chase-watchdog reaches
    /// 30s, the assist escalates to CantFollow, and CantFollow's
    /// projection escape thrashes (also fixed independently by BH-2).
    /// The root fix is to recognise that "parked at my legitimate cap"
    /// is not "stuck."
    /// </summary>
    private bool _routeWalkParked;

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

    // ── Fix CB (log-105 09:21:10 evidence) — Direction-aware Initial sync ──
    //
    // FindNearestSafeRouteIndex picks the Euclidean-nearest route waypoint
    // (within rendezvousCap=leaderIdx). When the route makes a U-turn or
    // loop, the Euclidean-nearest waypoint to the bot's post-combat position
    // can be on the WRONG side of the U-turn — its forward direction
    // (route[syncIdx] → route[syncIdx+1]) points AWAY from where the leader
    // currently is. The assist walks several waypoints backward (north when
    // leader is south, etc.) before the advance loop catches up to the
    // leader's actual route position.
    //
    // Log-105 evidence at 09:21:10:495:
    //   Bot at <-719.86, -4210.95> (post-combat, south-east area).
    //   Leader recently published anchor <-723.24, -4215.79> (5y SW of bot),
    //   now ~14.8y from bot doing ATG/PTG with mob 909131.
    //   FindNearestSafeRouteIndex picks route[7]=<-720.45, -4207.66> (3.3y N).
    //   Route[7] → route[8]=<-718.54, -4194.08> heads bot NORTH.
    //   Leader is SOUTH. Bot walks NORTH (opposite direction).
    //
    // The fix: after BM-3, if walking from syncIdx forward would head bot
    // AWAY from leader's actual current position (cos < threshold), scan
    // forward to find a waypoint with better direction alignment. Skip
    // straight to that index, avoiding the U-turn detour.
    //
    // CBLeaderProximityYards: only apply when leader is within this distance
    // (rendezvous scenarios — assist needs to catch up quickly). When leader
    // is far away (large patrol step or fresh OnEnter from far away), the
    // standard Euclidean-nearest behavior is correct.
    //
    // CBBackwardCosThreshold: -0.3 means an angle ~107° between picked
    // waypoint and leader directions. -0.5 (120°) was too restrictive in
    // testing — it missed slightly-angled cases. -0.3 catches clear backward
    // walks (>= 100°) without over-firing on moderately angled approaches.
    //
    // CBMaxScanDistanceYards: don't pick waypoints too far from bot — even
    // if direction aligns better, walking 50+ y is worse than fixing the
    // angle on the next advance cycle.
    //
    // CBImprovementMargin: only skip if the better waypoint's alignment is
    // meaningfully better than the picked one. Prevents oscillation between
    // marginally-different alignments.
    private const float CBLeaderProximityYards = 30.0f;
    private const float CBBackwardCosThreshold = -0.3f;
    private const float CBMaxScanDistanceYards = 35.0f;
    private const float CBImprovementMargin = 0.3f;

    // ── Fix CF (log-109 16:08:57 evidence) — Body-chase override for route detours ──
    //
    // When the leader is geographically close (within
    // CFBodyChaseProximityYards) but the next route waypoint is FURTHER
    // from the leader than the bot is, the route is taking a detour
    // (typically a U-turn or loop). Walking the route forward would take
    // longer than chasing the leader directly. Per-tick: switch from
    // RouteWalk → PositionChase to body-chase the leader directly.
    //
    // User observation (log-109 16:08:57): "the assist took a very long
    // lap around what looked to be some waypoints after the last combat."
    // Trace: bot at <-728.64, -4217.32>, leader at <-739.30, -4234.53>
    // (25.8y direct). BM-3 + CB picked route[7]=<-720.45, -4207.66> as
    // Initial sync target. CB's scan (CBMaxScanDistanceYards=35y) broke
    // at route[9] (39y from bot) without seeing route[20] at 23y from
    // bot — the route LOOPS through route[7..19] before coming back
    // close to the bot. Without CF, bot walked 13 route waypoints (~140y)
    // over ~30s while the leader stood 25.8y away. With CF: per-tick
    // body-chase override fires (25.8y < 30y AND route[7] is 32.8y from
    // leader > 25.8 + 3y margin) → bot walks 25.8y direct, ~6s.
    //
    // Per-tick override; no latching. If route-walk would actually be
    // better on a future tick (next route wp closer to leader than direct),
    // override naturally disengages.
    //
    // Why not just refine CB? CB's break-on-too-far protects against
    // picking a 50+ y target during normal patrol. Loosening that risks
    // regressions in non-loop scenarios. CF is a separate per-tick check
    // that complements CB without changing its semantics — and matches
    // the user's design suggestion (distance-based chase).
    //
    // Gate: skip CF when _pendingAnchor is set (combat handoff in flight —
    // body-chase would miss the Anchor mode transition).
    //
    // CFBodyChaseProximityYards: bot-to-leader threshold below which
    //   body-chase is worth the risk of unvalidated terrain. 30y matches
    //   CBLeaderProximityYards (consistent with CB's "close" definition).
    // CFRouteDetourMargin: next route waypoint must be at least this much
    //   FURTHER from leader than bot is, to avoid flicker when route is
    //   roughly aligned with chase direction. 3y ≈ POP_DIST.
    private const float CFBodyChaseProximityYards = 30.0f;
    private const float CFRouteDetourMargin = 3.0f;


    // ── Fix CC (log-106 11:14:16 evidence) — Geometric cache extension ──
    //
    // The leader's route index cache is updated only when leader publishes
    // a TargetWaypoint matching a route index (primary path in
    // TryFindLeaderRouteIndex). During extended ATG/PTG/Combat sequences,
    // HasTargetWaypoint stays false (ApproachStart-mutex), and the cache
    // doesn't update via primary. The leader's actual route progress can
    // still be inferred geometrically from leader.WorldPos.
    //
    // Without this fix, the assist's advanceCap (=cache.leaderIdx - 1)
    // stays at a stale value while the leader progresses further along the
    // route. The bot parks at trailing-by-one (per BH-3/BM-1's parked
    // logic) and waits indefinitely for cache to update. Meanwhile the
    // leader is many waypoints ahead.
    //
    // Log-106 evidence at 11:14:16 → 11:14:29 (13s park):
    //   Bot at route[25] = <-749.53, -4305.37> (advanceCap = 24, can't move).
    //   Leader was 35.5y away and moving further (next anchor was 31.5y).
    //   Cache stayed at 25 until 11:14:35 (21 seconds!) while leader
    //   actually progressed to route[27] at 11:14:27. The cache update
    //   missed the brief 99ms Follow window between ATGs.
    //
    // With Fix CC, when cache fallback fires, we check if leader.WorldPos
    // is closer to route[leaderIdx+1] than to route[leaderIdx]. If yes,
    // advance the cache by 1. Repeat up to CCMaxAdvancePerCall waypoints.
    // The cache catches up to the leader's actual route progress without
    // requiring TargetWaypoint publication.
    //
    // CCMaxAdvancePerCall: cap per call to prevent runaway in edge cases
    // (leader off-route near a looped section, position jitter, etc.).
    // 5 waypoints (~50y at typical route stride) covers normal combat-
    // sequence drift while still bounding worst-case overshoot.
    private const int CCMaxAdvancePerCall = 5;

    // ── Fix CG (log-110 17:30:28 evidence) — Geometric leaderIdx reacquisition on cache expiry ──
    //
    // CC (Fix CC above) extends a still-valid cache geometrically during
    // long ATG/PTG/Combat sequences where TargetWaypoint isn't published.
    // But CC only runs inside the cache-still-fresh branch. Once the cache
    // hits the 30s TTL and expires, TryFindLeaderRouteIndex returns false,
    // the caller falls through to PositionChase, and RouteWalk (along with
    // CF body-chase override) becomes unavailable for the rest of the
    // catch-up. The bot loses access to the pre-validated route waypoints
    // and relies on the pather to navigate to leader.WorldPos directly.
    //
    // Log-110 manifestation at 17:30:28:552:
    //   "Cache fallback expired: age=31.0s > 30s TTL. TryFindLeaderRouteIndex
    //   now returns false; caller will fall back to PositionChase."
    //   Bot at <-481.94, -4390.29>, leader at ~<-477, -4395>. Bot's
    //   PHYSICAL position was near route[~130] (per "closestIndex=130" in
    //   subsequent Route-span log). Route loops in this area — route[55]
    //   and route[130] are physically close. Leader had walked from
    //   route[55] (where cache was set 31s earlier) to ~route[130] during
    //   the intervening combat/loot/loop sequences.
    //   Without CG: PositionChase target = leader's body. Pather can't
    //   path through terrain between bot and leader (the "two hills"
    //   the user observed). 17:30:36:241 "Path wait timeout". Bot stuck
    //   at <-476.70, -4387.40> for 12+ seconds (moved 0.35y in 2.5s,
    //   reported as Stuck). Leader walked off to engage another mob;
    //   by 17:30:50 bot was 47.1y from leader.
    //   With CG: leader.WorldPos is ~5y from route[130]. CG fires:
    //   reacquire leaderIdx=130, refresh cache, return true. Caller's
    //   RouteWalk gate engages. Initial sync picks route waypoint near
    //   bot's position. Bot walks pre-validated route waypoints through
    //   the hills.
    //
    // Approach: when the cache is expired (about to return false), search
    // globally for the route waypoint closest to leader.WorldPos. If the
    // distance is within CGLeaderProximityYards, reacquire that index.
    //
    // Safety: tight CGLeaderProximityYards (20y) ensures we only reacquire
    // when the leader is genuinely on or near the route. If leader is
    // truly off-route (rescue scenario, mob roamed far), the geometric
    // search will return a too-far index and CG won't fire — fallthrough
    // to PositionChase remains the correct behavior.
    //
    // Direction guard: prefer indices >= the LAST cached value to avoid
    // backward jumps when the route loops past where the leader was.
    // If no forward index is within tolerance, fall back to global nearest
    // (which may be backward — accept this as a recovery from a stale
    // cache that missed a loop wrap-around).
    //
    // Why not just extend CC's TTL? The 30s TTL protects against truly
    // stale data (leader teleported, was kicked from group, etc.).
    // Extending it indefinitely risks acting on data that no longer
    // reflects the leader's actual route position. CG re-derives the
    // index from current geometry, which is always fresh.
    //
    // CGLeaderProximityYards: 20y — covers typical route stride (~10y)
    //   plus normal off-route drift during combat positioning. Beyond
    //   this distance, the leader is sufficiently off-route that route-
    //   walking would mislead the bot.
    private const float CGLeaderProximityYards = 20.0f;


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

    // Fix EK (log hygiene, run-135): the large [FIX-CONFIG] config dump below was logged in
    // full on every OnEnter (every combat handoff). Log it once per process instead.
    private static bool _fixConfigLogged;

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

        // ── Fix CJ (log-112 evidence) ──
        // Record OnEnter time for the Idle-entry guard. See _ffgEnterTimeUtc
        // declaration (~line 423) and the Idle entry check (~line 2247) for
        // the full rationale.
        _ffgEnterTimeUtc = DateTime.UtcNow;

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

        // Fix BI-2: cache is PRESERVED across OnExit/OnEnter boundaries
        // (was reset to -1 prior to BI-2). Eliminates the ~1-2s
        // stale-PositionChase walk after combat re-entry that caused
        // log-94 kill #15's 39.9y drift + 180° flip-on-cache-repopulate.
        // The TTL inside TryFindLeaderRouteIndex (CachedLeaderRouteIdxMaxAgeSec=30s)
        // bounds cross-session staleness; the cached value is a route
        // INDEX (not a physical position) which remains approximately
        // correct after typical combat displacement. First-ever OnEnter
        // unaffected (default sentinel idx=-1 + utc=MinValue).
        // See HANDOFF Fix BI-2 for full evidence.

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
        if (!_fixConfigLogged)
        {
            _fixConfigLogged = true;
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
            $"ChaseWatchdogCantFollowSec={ChaseWatchdogCantFollowSec:0.0}s, " +
            $"AnchorLocalTtlMs={AnchorLocalTtlMs:0}ms, " +
            $"RouteSpanLeaderProximityYards={RouteSpanLeaderProximityYards:0.0}y. " +
            $"Active fix list: AA, AB-1, AB-2, AC+AE+AI+AL, AD, AG, AH+AJ+AN, " +
            $"AJ, AL, AM, AO, AQ, AT, AU, AV+AW, AX, AY, AZ, BA, BB, BC, BD, BE, " +
            $"BF, BG-2, BH-2, BH-3, BI-1, BI-2, BJ, BK, BL, BM-1, BM-2, BM-3, " +
            $"BP, BQ, BR, BT, BU, BV, BW, BX, BY, BZ, CA, CB, CC, CD, CE-1, CE-2, CF, CG, CH, CI, CJ, CK, CL, CM, CN, CO, CP, CQ, CR, CS, CT, CV, CW, CX, CY, CZ, DA, DC, DD, DE, DF, DG, DH, DI, DJ, DK, DM, DN, DP, DQ, DR. " +
            $"RouteWaypointCount={navigation.LoadedRoute.Length}, " +
            $"navHash={navigation.GetHashCode()}.");
        }

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
        _anchorPatherFallback = false;         // Fix DP: reset reactive-fallback state on each FFG entry
        _anchorFallbackUsed = false;
        _anchorApproachBestDist = float.MaxValue;
        _anchorApproachProgressUtc = DateTime.MinValue;
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
            // Fix AK: on CantFollow re-entry, discriminate by preemption
            // duration. Brief (≤SignificantPreemptionMs=2500ms): resume
            // CantFollow with ResetEscapeState (original Fix 31/32 — Heal/
            // Buff/Loot class, geometry essentially unchanged). Long
            // (>2500ms, Combat class): reset to Idle for fresh evaluation;
            // if still unreachable, FFG re-escalates after ~100ms.
            //
            // Wall-clock duration is the only signal that reliably
            // discriminates the two classes (log-76 disproved position-
            // based checks — bot and leader were both essentially
            // stationary during 5s of ranged combat). Misclassification
            // in either direction is bounded and self-correcting.
            // See HANDOFF Fix AK for full evidence.
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

                // Fix CE-1 (log-108): the AK gate clears the assist's internal
                // CantFollow nav-state (resets _navState to Idle), but the
                // canonical CantFollow exit pattern (AO cumulative cap, line
                // ~2599) also clears assistStatusProvider.CantFollow=false so
                // the leader's snapshot of AnyAssistCantFollow() updates
                // promptly. Without this clear, the flag stays true until the
                // bot physically reaches follow range (via the line 1840 /
                // 2025 / 2119 / 5766 / 5827 / 6029 / 6106 paths) and the
                // leader continues to observe AssistCantFollow=true while
                // walking to a stale AssistReturn destination.
                //
                // Log-108 13:31:48 manifestation: at combat end, AK clears
                // _navState (good), but assist.CantFollow flag remains true
                // until 13:32:38:996 (~50s later, when bot reaches follow
                // range). During that window, leader's FRG.Resume PRESERVE
                // branch preserves the AssistReturn destination <-515.76>
                // (set at 13:31:42 by the union diff's GoToOneWaypoint
                // before combat preemption), and walks WEST to it while
                // assist's BM-3 picks route[47] and walks EAST. Bots
                // diverge 55y in opposite directions over ~12 seconds,
                // producing the user-visible corridor loop.
                //
                // Pairs with Fix CE-2 (FRG.OnGoapEvent case
                // assistrequestreturn falling-edge handler) — CE-1 makes
                // the flag fall promptly post-AK; CE-2 acts on the falling
                // edge by aborting the leader's in-flight AssistReturn.
                // Both required: CE-1 alone leaves the AssistReturn waypoint
                // on the leader's nav stack until physical follow range is
                // reached; CE-2 alone waits until the flag falls naturally
                // (same 50s delay).
                assistStatusProvider.CantFollow = false;
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

        // Fix BF: mirror reset to the provider so the leader sees a clean
        // unsynced state when FFG is not active. Update() would otherwise
        // not run to overwrite the stale value.
        assistStatusProvider.CurrentRouteIndex = -1;

        // ── Route-walking migration: Turn 3 ──
        // Clear pending anchor on exit. The next FFG session may face
        // a different approach context (or none); stale pending would
        // confuse it.
        _pendingAnchor = default;

        // ── Fix BI-2 (log-94 kill #15) ──
        // Cache PRESERVED across the OnExit boundary. See the matching
        // OnEnter block (~line 1043) for the full rationale. The TTL
        // check inside TryFindLeaderRouteIndex auto-rejects stale
        // cache, so cross-session leaks are bounded. Preserving here
        // (symmetric with OnEnter) means the next FFG re-entry has
        // the cache available immediately for its fallback path.

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
            // Fix 27: only set Waiting if actually > FollowingMaxYards
            // from the leader at exit time. Within range, keep Status =
            // Following so the leader's FRG precondition
            // (assistrequestreturnorisfollowing) stays satisfied through
            // brief preemptions (Combat/Loot/ConsumeCorpse). The previous
            // unconditional Waiting on every non-CantFollow exit caused a
            // 46-second leader stall in log-50 when the assist fought a
            // BL mob from 0.9y away — the brief Following posted at OnEnter
            // was overwritten by Waiting in the publisher's staging slot
            // before publication, so the leader never saw it.
            //
            // Edge case preserved: > FollowingMaxYards → still Status=Waiting
            // (genuine out-of-position, NavigatingToLeader interrupted etc).
            // See HANDOFF Fix 27 for full evidence.
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
            else if (leader != null && leader.HasApproachStart
                     && distToLeader <= LeaderPauseYards)
            {
                // Fix DT (log-132 15:03:02-10): the leader is actively engaging
                // (HasApproachStart), so the assist is exiting FFG to JOIN the pull on
                // the leader's target -- its own ATG just became selectable via
                // partyEngaging (= PartyInCombat() || (AssistFocus && HasApproachStart)).
                // It is NOT stopping out of position. Reporting Waiting here would make
                // the leader's assistisfollowing GoapKey False, failing ApproachTargetGoal's
                // PartyLeader precondition (assistisfollowing=true, ATG line 182): the
                // leader then gets NO PLAN and stops approaching, HasApproachStart drops,
                // the assist returns to FFG/Following, the leader can approach again, the
                // assist leaves again -- a leader<->assist oscillation in which the leader
                // never closes on the mob and the squishy assist engages it first
                // (operator-observed in log-132). Report NavigatingToLeader instead, which
                // AnyAssistIsFollowing() counts as available (see ~line 2272), so
                // assistisfollowing stays true and the leader can complete its approach.
                // ATG's own anchor sync-pause (BN/BS) still holds the leader at the anchor
                // until the assist is in position, so the leader does not pull without the
                // healer. Bounded by LeaderPauseYards (25y) -- the leader's own pause-for-
                // assist threshold -- so a genuinely lagging assist still reports Waiting.
                if (assistStatusProvider.CurrentStatus != BotStatus.NavigatingToLeader)
                {
                    logger.LogInformation(
                        $"[FFG] [FIX-FIRE] DT: assist at {distToLeader:0.0}y from leader " +
                        $"(beyond FollowingMaxYards={FollowingMaxYards}y) but leader is engaging " +
                        $"(HasApproachStart) within LeaderPauseYards={LeaderPauseYards}y -- reporting " +
                        $"NavigatingToLeader instead of Waiting so the leader's assistisfollowing " +
                        $"precondition stays satisfied and it keeps approaching " +
                        $"(was {assistStatusProvider.CurrentStatus}).");
                    assistStatusProvider.CurrentStatus = BotStatus.NavigatingToLeader;
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

        // Fix BF: publish current route index for the leader to consume in
        // diagnostic logs and future waypoint-precise coordination. Writing
        // once per tick at Update entry keeps the provider in sync with the
        // _assistRouteIndex field without mirroring every internal assignment.
        // The field can be -1 (unsynced) or any valid route index.
        assistStatusProvider.CurrentRouteIndex = _assistRouteIndex;

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


        // ── ATG-handoff focus-chain: Fix AH + AJ + AN + AM ──
        //
        // Fix AH (log-73): at the approach-anchor co-location moment, press
        // PressTargetFocus + PressTargetOfTarget so the next planner tick
        // sees hastarget=true and ATG (cost 8) outranks FFG (cost 19),
        // transferring control. The chain is placed ABOVE the state switch
        // because Idle↔NavigatingToLeader toggles rapidly (<15ms in log-73)
        // at the anchor — a per-state check would miss most ticks.
        //
        // Fix AJ (log-75): only LATCH _approachTargetAcquired when the
        // chain produces a hostile target (bits.Target() && Target_Hostile()).
        // Retry at ApproachTargetAcquireRetryMs (250ms) — round-trip
        // latency can cause F to fire before PageUp lands, leaving the
        // assist's target=focus=leader (friendly). The retry converges
        // in 1-2 attempts because the failed first attempt left target=focus
        // = leader, so the next F sees a stable current target.
        //
        // Fix AN (log-74/77): TTL fallback for short ATG windows. When the
        // leader's ATG completes faster than the assist can converge to
        // the anchor (~2-2.5s), remember the anchor position for
        // AnchorLocalTtlMs (3s) past HasApproachStart=false; if the assist
        // arrives in that window, fire the same chain. The leader still
        // has the target selected at this point.
        //
        // Fix AM: diagnostic only — at the AJ latch moment, snapshot every
        // ATG-gating value (bits.Target_*, IsInBlacklistArea, HasApproachStart,
        // ForcedFollow). If FFG is still running >StaleLatchWarnAfterMs
        // after the latch, emit a warning. Turns "ATG silently not picked"
        // into a greppable signature.
        //
        // See HANDOFF Fix AH/AJ/AN/AM for full evidence trail.
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
                    //
                    // Fix DF (log-124 19:27:34:442): also require the target to
                    // be ALIVE. A just-killed mob keeps its hostile flag for a
                    // moment, so `Target() && Target_Hostile()` alone latches the
                    // corpse (log-124: guid=1034619 latched with dead=True right
                    // after CombatGoal finished it). ATG cannot engage a corpse,
                    // so the planner never selects ATG, the one-shot latch never
                    // clears, and AM logs stale-latch warnings for ~8s while the
                    // assist holds in approach state instead of following/idling.
                    // The dead check is already computed below for the AM
                    // diagnostic (diagTargetDead); gate the latch on it too.
                    if (bits.Target() && bits.Target_Hostile() && !bits.Target_Dead())
                    {
                        _approachTargetAcquired = true;
                        _approachTargetLatchedUtc = DateTime.UtcNow;  // Fix AM: record for stale-latch warning

                        // Fix EH (log-134: 33/33 AM stale-latch warnings cited incombatrange=
                        // true). The AJ latch fires "at the approach anchor," which sits next to
                        // the mob, so the assist is frequently ALREADY in combat range when it
                        // latches. ATG.AssistFocus requires incombatrange=false, so in that case
                        // ATG can never be the handoff -- Combat (cost 4) takes over once party-
                        // combat fires. Frame the latch log so the in-range case is not mislabeled
                        // an ATG handoff (diagnostic only; the latch and behavior are unchanged).
                        bool latchedInCombatRange = playerReader.WithInCombatRange();
                        if (latchedInCombatRange)
                        {
                            logger.LogInformation(
                                $"[FFG] [FIX-FIRE] AJ: focus-chain confirmed hostile target acquired " +
                                $"(guid={playerReader.TargetGuid}) - latching one-shot. Assist is " +
                                $"already in combat range (incombatrange=true), so this is a " +
                                $"COMBAT-handoff latch, NOT an ATG-handoff: ATG requires " +
                                $"incombatrange=false and is intentionally ineligible here; Combat " +
                                $"(cost 4) takes over when party-combat fires. AM treats the in-range " +
                                $"wait as expected, not stale.");
                        }
                        else
                        {
                            logger.LogInformation(
                                $"[FFG] [FIX-FIRE] AJ: focus-chain confirmed hostile target acquired " +
                                $"(guid={playerReader.TargetGuid}) - latching one-shot. " +
                                $"ATG (cost 8) should win the next plan over FFG (cost 19) " +
                                $"and take over the interact-key approach.");
                        }

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

                        // ── Fix CL (log-113 14× AM stale-latch warnings, 17.4s worst case) ──
                        //
                        // Log-113 showed Fix AJ latching successfully but ATG NOT being
                        // selected for 5 of 7 pre-combat windows. The AM warning displayed
                        // all 8 of ATG.AssistFocus's listed preconditions as met, but two
                        // gating values are NOT logged: evadeRecovery (an ATG precondition
                        // directly) and partyEngaging (the GoapAgent-computed derived key
                        // that ATG actually reads). Without these values it's impossible to
                        // determine from the log which precondition the planner sees as
                        // false. The original AM warning also HARDCODED "HasApproachStart=
                        // true (still)" — a string literal, not a fresh read — so we
                        // couldn't tell whether the leader's polled-state HasApproachStart
                        // had silently flipped between latch and warn moments. Fix CL
                        // closes all three gaps:
                        //
                        //   1. Read curHasApproachStart fresh from approachLeader (the
                        //      same LeaderState reference used by leaderAnchorActive at the
                        //      top of this block, so by construction it's non-null here).
                        //   2. Read curEvadeRecoveryActive from assistStatusProvider
                        //      (mirrored from GoapAgent's GoapKey.evadeRecovery broadcast).
                        //   3. Compute curPartyEngaging the same way GoapAgent does:
                        //      bits.Combat() || bits.Focus_Combat() || (AssistFocus mode &&
                        //      HasApproachStart). Mode is implicit — FFG only registers
                        //      Fix AJ/AM in AssistFocus mode (see this method's outer gating).
                        //
                        // After Fix CL, the AM warning's "ATG.AssistFocus requires:" tuple
                        // can be checked element-by-element against the displayed CURRENT
                        // values. The first one whose value disagrees with ATG's expected
                        // is the blocker.
                        //
                        // Note: CurrentGoal is intentionally not logged. FFG.Update only
                        // runs when the planner's CurrentGoal IS FollowFocusGoal — logging
                        // "currentGoal=FollowFocusGoal" would be tautological.
                        bool curHasApproachStart =
                            approachLeader != null && approachLeader.HasApproachStart;
                        bool curEvadeRecoveryActive = assistStatusProvider.EvadeRecoveryActive;
                        bool curFocusCombat        = bits.Focus_Combat();
                        bool curPartyEngaging      =
                            curBitsCombat || curFocusCombat || curHasApproachStart;

                        // Fix EH (log-134): only the GENUINELY stale case stays a Warning.
                        // When curInCombatRange is true the latch is a COMBAT-handoff (ATG
                        // ineligible by design, awaiting party-combat) -- expected behavior, not a
                        // stale latch -- so demote it to Debug with accurate framing. This stops
                        // the per-pull in-range noise (33/33 last session) from burying a real
                        // handoff failure (ATG eligible but not selected), which still warns.
                        string amBody =
                            $"Fix AJ latched {msSinceLatch:0}ms ago " +
                            $"(> {StaleLatchWarnAfterMs:0}ms threshold) but FFG.Update is still " +
                            $"running. ATG was NOT selected by the planner. Current " +
                            $"ATG-precondition state - " +
                            $"hastarget={curHasTarget}, targetisalive={curHasTarget && !curTargetDead} " +
                            $"(dead={curTargetDead}), targethostile={curTargetHostile}, " +
                            $"incombatrange={curInCombatRange}, " +
                            $"inblacklistarea={curInBlacklistArea}, " +
                            $"forcedfollow={curForcedFollow}, " +
                            $"HasApproachStart={curHasApproachStart} (CL: fresh read), " +
                            $"bits.Combat={curBitsCombat}, bits.Focus_Combat={curFocusCombat}, " +
                            $"partyEngaging={curPartyEngaging} (CL: bits.Combat || bits.Focus_Combat || HasApproachStart), " +
                            $"evadeRecovery={curEvadeRecoveryActive} (CL: from assistStatusProvider), " +
                            $"currentTargetGuid={playerReader.TargetGuid}. " +
                            $"ATG.AssistFocus requires: partyEngaging=true, forcedfollow=false, " +
                            $"hastarget=true, targetisalive=true, targethostile=true, " +
                            $"incombatrange=false, inblacklistarea=false, evadeRecovery=false. " +
                            $"Compare element-by-element: the first precondition above whose " +
                            $"value disagrees with ATG's expected is the blocker. " +
                            $"Next note in {StaleLatchWarningCooldownMs:0}ms if condition persists.";

                        if (curInCombatRange)
                        {
                            logger.LogDebug(
                                $"[FFG] [FIX-FIRE] AM: in-range COMBAT-handoff hold (expected, NOT " +
                                $"stale) - incombatrange=true makes ATG ineligible by design; Combat " +
                                $"(cost 4) takes over on party-combat. {amBody}");
                        }
                        else
                        {
                            logger.LogWarning(
                                $"[FFG] [FIX-FIRE] AM: STALE LATCH WARNING - {amBody}");
                        }
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

    /// <summary>
    /// Fix DN (log-129 evidence): true when the assist should HOLD position rather
    /// than pather-chase the leader. Fires when the assist is already within its own
    /// combat range of the target during an active pre-combat engage (leader
    /// HasApproachStart, combat not yet started). In that window the assist is
    /// positioned to cast/strike, so chasing the (out-of-follow-range) leader the
    /// rest of the way only drives FFG's PositionChase through the defective-navmesh
    /// corridor — which returns rear-pointing/convoluted routes and a jittering follow
    /// target, producing the wide left/right re-aim swings seen right before the first
    /// cast. Holding avoids the swings without losing approach (already in range);
    /// Combat starts within ~1-3s and the Combat goal takes over facing+casting.
    /// <para>
    /// Complements Fix CJ: CJ keeps the assist chasing while it still needs to close
    /// distance (NOT in combat range); DN holds once it IS in range. The two are
    /// mutually exclusive on <see cref="PlayerReader.WithInCombatRange"/>. Bounded by
    /// <see cref="HoldInCombatRangeMaxLeaderYards"/> so a departing leader is followed.
    /// </para>
    /// </summary>
    private bool ShouldHoldInCombatRange(LeaderState leader, float distToLeader)
    {
        return leader.HasApproachStart
            && !bits.Combat()
            && playerReader.WithInCombatRange()
            && distToLeader < HoldInCombatRangeMaxLeaderYards;
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
            // Fix DN: if already in combat range of the target during a pre-combat
            // engage, hold here instead of starting a pather-chase toward the leader.
            if (ShouldHoldInCombatRange(leader, dist))
            {
                if (assistStatusProvider.CurrentStatus != BotStatus.Following)
                {
                    logger.LogInformation(
                        $"[FFG] [FIX-FIRE] DN: in combat range during engage " +
                        $"(leader {dist:0.0}y, HasApproachStart=true, bits.Combat=false) — " +
                        $"holding instead of chasing leader (avoids pre-cast pather swings).");
                    assistStatusProvider.CurrentStatus = BotStatus.Following;
                }
                wait.Update();
                return;
            }
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
                // ── Fix DR (log-130 issue #2: ping-pong → CantFollow) ──
                //
                // Extends DN's combat-range hold into the dead-band. DN's GUARD-2
                // (UpdateNavigatingToLeader, ~line 2557) stops the chase and drops
                // to Idle when the assist is already in combat range during a
                // pre-combat engage. But DN's GUARD-1 (above, ~line 2221) only
                // fires when dist > NavigatingMinYards (14y). In the dead-band
                // (NavigatingExitYards 10y < dist < 14y) neither DN guard applies,
                // so this branch fired StartNavigatingToLeader to "close the gap"
                // every tick — which DN's GUARD-2 then immediately stopped,
                // producing a rapid Idle↔NavigatingToLeader flip. Each flip issued
                // a SetWayPoints(count=1) while the bot stood still (DN keeps
                // stopping it), so 10 stationary sets accumulated within the
                // SetWaypointLoopGuarded 100ms window → "Navigation exhausted" →
                // CantFollow.
                //
                // Evidence (log-130 12:33:19:130-207, ~77ms): DN "entered combat
                // range during engage (leader 10.5y)" → Stop → Idle → "Dead-band
                // (10.6y) — closing gap to confirm rendezvous" → SetWayPoints →
                // Idle→Nav → DN fires again → ... ×10 → loop guard → CantFollow.
                // All 10 escalations in 130 carry this exact signature (3 DN-fires
                // + 4 dead-band lines in the 25 lines preceding each); log-129
                // (no DN) had 0 escalations. Fix O's co-located guard (below)
                // can't catch this because the leader is ~10y away, not within
                // POP_DIST.
                //
                // The hold is safe: ShouldHoldInCombatRange requires
                // HasApproachStart (the leader is engaging a mob nearby, NOT
                // patrolling away) and WithInCombatRange (the assist can already
                // cast from here — no need to close to <7y). When combat starts
                // (bits.Combat becomes true) the predicate releases and normal
                // gap-closing resumes; when the leader resumes patrol
                // (HasApproachStart clears) it also releases.
                if (ShouldHoldInCombatRange(leader, dist))
                {
                    if (assistStatusProvider.CurrentStatus != BotStatus.Following)
                    {
                        logger.LogInformation(
                            $"[FFG] [FIX-FIRE] DR: in combat range during engage " +
                            $"(dead-band {dist:0.0}y) — holding, NOT closing rendezvous gap " +
                            $"(extends DN's hold into the dead-band; avoids the " +
                            $"DN-vs-dead-band Idle↔Nav oscillation that trips the " +
                            $"SetWaypoint loop guard → CantFollow).");
                        assistStatusProvider.CurrentStatus = BotStatus.Following;
                    }
                    wait.Update();
                    return;
                }

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
        // Fix BM-1 (supersedes BH-3's transient flag-set, log-96
        // 16:36:20:860 → 16:36:50:754 evidence): recompute parked-at-cap
        // state every tick from observable fields. The original BH-3
        // design set the flag transiently in
        // Navigation_OnDestinationReached's RouteWalk branch (which only
        // fires ON ARRIVAL, not on every tick while still parked) and
        // cleared it here every tick. The result was that the flag was
        // true only on the single tick the parked event fired; on every
        // subsequent tick of legitimate parked-at-cap waiting, the flag
        // was false and TickActiveStuckDetection / TickNavActiveTimeout
        // fired unsuppressed.
        //
        // Concrete failure mode in log-96:
        //   16:36:20:860  parked event fires, flag=true briefly
        //   16:36:23:267  Stuck fires (2.5s later, flag cleared by this
        //                 tick's UpdateNavigatingToLeader entry)
        //   16:36:25:660  leader sees Stuck status → "assist truly
        //                 unavailable" → leader aborts patrol resume,
        //                 cap can never lift
        //   16:36:50:754  TickNavActiveTimeout fires CantFollow (30s
        //                 later, not gated by _routeWalkParked at all
        //                 in the BH-3 design — BM-2 fixes that)
        //
        // IsParkedAtRouteCap reads _currentNavTargetMode (set by
        // GetNavigationTarget on the prev tick) + _assistRouteIndex +
        // distance to route[_assistRouteIndex] — all CURRENT state — so
        // the flag now tracks "am I parked NOW" rather than "did the
        // parked path fire on the previous tick."
        _routeWalkParked = IsParkedAtRouteCap();

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

        // Fix DN (log-129 evidence): if we have entered combat range of the target
        // mid-chase during a pre-combat engage, abort the chase and hold (Idle). This
        // is what stops the PositionChase pather from swinging the bot left/right right
        // before the first cast — the assist is already in range to fight. The Idle
        // state's own DN guard then keeps it holding until combat starts (Combat goal
        // takes over) or the leader leaves combat range. See ShouldHoldInCombatRange.
        if (ShouldHoldInCombatRange(leader, dist))
        {
            logger.LogInformation(
                $"[FFG] [FIX-FIRE] DN: entered combat range during engage " +
                $"(leader {dist:0.0}y, HasApproachStart=true, bits.Combat=false) — " +
                $"aborting chase, holding (avoids pre-cast pather swings).");
            navigation.Stop();
            input.StopForward(true);
            ResetNavState();
            EnterState(NavState.Idle);
            assistStatusProvider.CurrentStatus = BotStatus.Following;
            return;
        }

        // ── Fix DP (log-129 01:06:30→34) — reactive route-walk-first fallback arming ──
        // While the assist is directly pathering to the approach anchor (Anchor mode), watch
        // its closest distance to the anchor. If that stops improving for
        // AnchorProgressTimeoutMs, the defective-corridor pather is failing — it timed out /
        // null-ref'd and idled, or returned a detour that walks the wrong way. Arm the
        // fallback: CheckShouldDefer then defers to route-walk up to the leader's own route
        // index (the clean usePather=false walk), and the existing combat-handoff pathers the
        // short final hop. Tracking resets when the anchor moves (new pull) or the approach ends.
        if (leader.HasApproachStart
            && _currentNavTargetMode == NavTargetMode.Anchor
            && !_anchorFallbackUsed)
        {
            Vector3 anchorW = new(leader.ApproachStartWorldX, leader.ApproachStartWorldY, 0f);
            float distToAnchor = playerReader.WorldPos.WorldDistanceXYTo(anchorW);
            DateTime nowUtc = DateTime.UtcNow;

            if (_anchorApproachProgressUtc == DateTime.MinValue
                || anchorW.WorldDistanceXYTo(_dpTrackedAnchor) > AnchorReachedResetYards)
            {
                // First Anchor tick this approach, or anchor moved (new pull): (re)start tracking.
                _dpTrackedAnchor = anchorW;
                _anchorApproachBestDist = distToAnchor;
                _anchorApproachProgressUtc = nowUtc;
            }
            else if (distToAnchor < _anchorApproachBestDist - AnchorProgressEpsilonYards)
            {
                _anchorApproachBestDist = distToAnchor;
                _anchorApproachProgressUtc = nowUtc;
            }
            else if (!_anchorPatherFallback
                     && (nowUtc - _anchorApproachProgressUtc).TotalMilliseconds > AnchorProgressTimeoutMs)
            {
                _anchorPatherFallback = true;
                logger.LogWarning(
                    $"[FFG] [FIX-FIRE] DP: direct-Anchor pather not progressing toward anchor " +
                    $"(dist={distToAnchor:0.0}y, best={_anchorApproachBestDist:0.0}y, " +
                    $"{(nowUtc - _anchorApproachProgressUtc).TotalMilliseconds:0}ms since last gain > " +
                    $"{AnchorProgressTimeoutMs:0}ms). Falling back to route-walk up to leaderIdx, " +
                    $"then pather the short final hop.");
            }
        }

        // ── Fix CJ (log-112 evidence) ──
        // Suppress Idle entry in two cases:
        //   (a) leader.HasApproachStart=true — the leader is already in ATG and
        //       about to sprint to the mob; the assist must stay engaged to
        //       follow without lag.
        //   (b) within IdleGuardAfterEnterMs (3000ms) of FFG.OnEnter — covers
        //       the polling-lag race where leader's HasApproachStart=true is
        //       on the way but not yet observed. The post-combat OnEnter is
        //       followed within 1-3s by the next ATG in the typical pull
        //       cadence; entering Idle in this window strands the assist for
        //       ~1s while polling delivers the ATG signal.
        // See _ffgEnterTimeUtc and IdleGuardAfterEnterMs declarations (~line 423)
        // for full evidence and design rationale.
        bool ffgIdleGuardActive = (DateTime.UtcNow - _ffgEnterTimeUtc).TotalMilliseconds < IdleGuardAfterEnterMs;
        bool suppressIdle = leader.HasApproachStart || ffgIdleGuardActive;

        // Within FollowingMaxYards → transition to Idle, post Following.
        if (dist < FollowingMaxYards && !navigation.IsApproachEscapeActive && !suppressIdle)
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

        // ── Fix CJ diagnostic ── log when we would have Idle'd but were suppressed.
        // Bounded by the natural cadence (fires only once per crossing of < 7y).
        if (dist < FollowingMaxYards && !navigation.IsApproachEscapeActive && suppressIdle)
        {
            // Throttle: don't log more than once per 500ms.
            DateTime cjNow = DateTime.UtcNow;
            if ((cjNow - _lastFixCJSuppressLogUtc).TotalMilliseconds > 500)
            {
                _lastFixCJSuppressLogUtc = cjNow;
                logger.LogInformation(
                    $"[FFG] [FIX-FIRE] CJ: Idle entry suppressed at dist={dist:0.0}y " +
                    $"(threshold=FollowingMaxYards={FollowingMaxYards:0.0}y) — " +
                    $"HasApproachStart={leader.HasApproachStart}, " +
                    $"ffgIdleGuardActive={ffgIdleGuardActive} " +
                    $"({(DateTime.UtcNow - _ffgEnterTimeUtc).TotalMilliseconds:0}ms since OnEnter < " +
                    $"{IdleGuardAfterEnterMs}ms guard). " +
                    $"Staying NavigatingToLeader to avoid the 'sit in place while leader sprints to mob' " +
                    $"pattern. PositionChase mode will track the leader's body and switch to Anchor mode " +
                    $"as soon as HasApproachStart is observed.");
            }
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
                // Fix AZ: when navigation.IsRouteEscapeActive=true, suppress
                // FFG's drift-gate waypoint refresh unconditionally. The
                // current wp is owned by RouteEscape (tactical escape target
                // 10y/20y/30y at offset angles from facing); refreshing it
                // toward the leader yanks the bot back into the obstacle
                // that caused the stuck.
                //
                // The pre-AZ alignment-based check (cos > 0.7) ALWAYS fails
                // for escape targets because they intentionally point away
                // from the leader. log-82 mob N: 30y escape curve (pathLen=33,
                // 664ms movement) overwritten after 787ms with a 7y leader-
                // pursuit target, bot walked straight back into same stuck.
                //
                // Inter-attempt gap (~1-2s between escape attempts when
                // _routeEscapeActive briefly flips false at cooldown):
                // acceptable, next attempt restores ownership before brief
                // leader-pursuit can route the bot far.
                // See HANDOFF Fix AZ for full evidence.
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
            //
            // ── Fix CA (log-104 evidence) — extend to Anchor mode ──
            // Identical failure mode also applies to Anchor mode: when the
            // bot has arrived at the anchor (via Fix AN local-TTL, or
            // because IsAtFinalWaypoint popped on arrival), navigation has
            // no waypoint and dist to leader still exceeds NavigatingMinYards
            // (the leader has moved during the approach phase). The fallback
            // below calls GetNavigationTarget → BU returns the cached anchor
            // (mode stays Anchor via hysteresis after HasApproachStart=false)
            // → anchor co-located guard fires → body-chase fallback →
            // ComputeFollowTargetWorldPos returns a target that may ALSO be
            // co-located (Far-branch projection from assist toward a nearby
            // leader, with possible BL safety adjustment). SetWaypoint loop
            // ensues, same escalation to CantFollow. Sit-in-place is correct
            // for the same reason: the anchor is the right geographic spot;
            // the cycle resolves via BU hysteresis expiry or a new ATG.
            // See line ~5640 (OnDestinationReached) for the matched fix and
            // full evidence trace.
            if (_currentNavTargetMode == NavTargetMode.RouteWalk ||
                _currentNavTargetMode == NavTargetMode.Anchor)
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

        // Fix AF: tick Navigation on every UpdateCantFollow call so the
        // escape state machine's installed waypoints actually drive the
        // bot. Without this, StartProjectionPhase calls SetSingleWaypoint
        // and flips Navigation.active=true but no path request, route
        // refill, or input key ever fires — escape phases observe 0.0y
        // displacement for their entire budget and escalate uselessly.
        //
        // Safe across all phases: PhysicalUnstuck and Exhausted call
        // navigation.Stop() (active=false), so Navigation.Update no-ops
        // via its early-return guard. Leader-arrived/BL-segment-clear
        // exits at the top of UpdateCantFollow return before reaching
        // here. All Navigation event handlers in FFG that could fire
        // from this Update guard on _navState != NavigatingToLeader
        // and no-op during CantFollow — correct, because a "rear-curving"
        // path during escape is USEFUL (it routes back toward the leader)
        // and Fix AC+AE must not reject it.
        // See HANDOFF Fix AF for full evidence.
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
            case CantFollowEscapePhase.DirectedFallback:
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
            _escapePhase == CantFollowEscapePhase.Projection30 ||
            _escapePhase == CantFollowEscapePhase.DirectedFallback;

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

            case CantFollowEscapePhase.DirectedFallback:
                StartDirectedFallbackPhase();
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

    // 1b: DirectedFallback - last-resort directed escape for a genuinely wedged
    // bot (LastSafeAnchor unreachable + PhysicalUnstuck failed). BD route-basis
    // (toward nearest pre-validated waypoint) or reversed facing, swept across
    // 3 steps (10y/0deg, 20y/+120deg, 30y/-120deg), then hold (Exhausted). The
    // away-from-leader basis and Fix BB are intentionally NOT restored - those
    // were the AB-2 regression. Reached only when genuinely wedged; the
    // safe-and-waiting (co-located) case holds without coming here.
    private void StartDirectedFallbackPhase()
    {
        _directedFallbackStep++;
        if (_directedFallbackStep > 3)
        {
            logger.LogWarning(
                "[FFG] CantFollow escape: DirectedFallback sweep exhausted (3 steps) - " +
                "holding position (Exhausted).");
            StartEscapePhase(CantFollowEscapePhase.Exhausted);
            return;
        }

        float yards = _directedFallbackStep == 1 ? 10f : _directedFallbackStep == 2 ? 20f : 30f;
        float angleOffset = _directedFallbackStep == 2 ? (2f * MathF.PI / 3f)
                          : _directedFallbackStep == 3 ? (-2f * MathF.PI / 3f)
                          : 0f;

        Vector3 target = ComputeDirectedFallbackWaypoint(yards, angleOffset);

        if (navigation.AreaBlacklist != null && navigation.AreaBlacklist.ContainsWorld(target))
        {
            logger.LogInformation(
                $"[FFG] CantFollow escape: DirectedFallback step {_directedFallbackStep} target {target} " +
                "is itself inside a rect - advancing sweep.");
            StartEscapePhase(CantFollowEscapePhase.DirectedFallback);
            return;
        }

        _escapeTargetW = target;
        logger.LogInformation(
            $"[FFG] CantFollow escape: DirectedFallback step {_directedFallbackStep} " +
            $"({yards:0}y, offset={angleOffset * 180f / MathF.PI:+0.0;-0.0;0}deg) -> navigating to {target} " +
            $"(from {playerReader.WorldPos}).");
        navigation.SetSingleWaypoint(target);
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
                // 1b: safe anchor unreachable AND PhysicalUnstuck already ran =
                // genuinely wedged (the safe-and-waiting case holds in
                // StartLastSafeAnchorPhase's co-located branch and never reaches
                // here, so hold-when-safe is preserved). Try the DirectedFallback
                // sweep before holding.
                if (_escapeUnstuckUsed)
                    StartEscapePhase(CantFollowEscapePhase.DirectedFallback);
                else
                    StartEscapePhase(CantFollowEscapePhase.PhysicalUnstuck);
                break;
            case CantFollowEscapePhase.DirectedFallback:
                // Advance the sweep; StartDirectedFallbackPhase caps at step 3 -> Exhausted.
                StartEscapePhase(CantFollowEscapePhase.DirectedFallback);
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
        _directedFallbackStep = 0;   // 1b

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
    /// current position. Rect-relative only.
    ///
    /// <para>When no rect contains the assist, returns <c>default</c> so the
    /// caller (StartProjectionPhase) falls through to LastSafeAnchor and the
    /// hold-when-safe logic. The AB-2 away-from-leader directional fallback
    /// was removed here (it drove the bot away from its destination for every
    /// non-rect CantFollow cause); the directed route/sweep escape now lives
    /// in the last-resort DirectedFallback phase
    /// (<see cref="ComputeDirectedFallbackWaypoint"/>).</para>
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

        // ── Not near any rect: no rect-relative escape exists. ────────
        // Restored to pre-AB-2 behavior — return default so the caller
        // (StartProjectionPhase) falls through to LastSafeAnchor → hold-when-safe
        // (the run-141 / log-78 drift fix). The AB-2 "away-from-leader"
        // directional fallback and the BB/BG-2/BH-2 band-aids hung off it are
        // removed. BD/W return in the last-resort DirectedFallback phase
        // (see ComputeDirectedFallbackWaypoint), behind the hold-when-safe gate.
        return default;
    }

    // 1b: directed escape waypoint for the DirectedFallback phase. BD route-basis
    // (toward the nearest pre-validated LoadedRoute waypoint) or, if no usable
    // route, reversed player facing. The AB-2 away-from-leader basis and Fix BB
    // flip are intentionally NOT restored. BG-2 overshoot clamp retained for the
    // route basis. <paramref name="angleOffset"/> is the per-step sweep rotation.
    private Vector3 ComputeDirectedFallbackWaypoint(float yards, float angleOffset)
    {
        Vector3 pos = playerReader.WorldPos;
        float fdx = 0f, fdy = 0f;
        bool usingRouteDirection = false;
        float routeBasisDist = float.MaxValue;

        Vector3[] loadedRoute = navigation.LoadedRoute;
        if (loadedRoute.Length > 0)
        {
            float bestDist = float.MaxValue;
            int bestIdx = -1;
            for (int k = 0; k < loadedRoute.Length; k++)
            {
                float d = pos.WorldDistanceXYTo(loadedRoute[k]);
                if (d < bestDist) { bestDist = d; bestIdx = k; }
            }
            if (bestIdx >= 0 && bestDist >= MinRouteBasisDistance)
            {
                Vector3 wp = loadedRoute[bestIdx];
                float dxw = wp.X - pos.X, dyw = wp.Y - pos.Y;
                float lenSqW = dxw * dxw + dyw * dyw;
                if (lenSqW > 0.001f)
                {
                    float lenW = MathF.Sqrt(lenSqW);
                    fdx = dxw / lenW; fdy = dyw / lenW;
                    usingRouteDirection = true;
                    routeBasisDist = bestDist;
                    logger.LogInformation(
                        $"[FFG] [FIX-FIRE] BD: DirectedFallback basis = toward nearest LoadedRoute " +
                        $"waypoint idx={bestIdx} at {wp} ({bestDist:0.0}y). Pre-validated terrain.");
                }
            }
        }

        if (!usingRouteDirection)
        {
            float facing = playerReader.Direction;
            fdx = -MathF.Cos(facing);
            fdy = -MathF.Sin(facing);
        }

        if (MathF.Abs(angleOffset) > 0.001f)
        {
            float cos = MathF.Cos(angleOffset), sin = MathF.Sin(angleOffset);
            float rdx = fdx * cos - fdy * sin;
            float rdy = fdx * sin + fdy * cos;
            fdx = rdx; fdy = rdy;
        }

        // Fix BG-2: clamp to the route-waypoint distance on the unrotated step so
        // the projection lands AT the waypoint instead of overshooting + flipping.
        float effectiveYards = yards;
        if (usingRouteDirection && MathF.Abs(angleOffset) < 0.001f && routeBasisDist < yards)
            effectiveYards = routeBasisDist;

        return new Vector3(pos.X + fdx * effectiveYards, pos.Y + fdy * effectiveYards, 0f);
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

                // ── Fix CC (log-106 11:14:16 evidence) ──
                // Geometric cache extension: when cache is in use and the
                // leader's actual position has progressed past route[leaderIdx],
                // advance the cache locally using leader.WorldPos. This handles
                // the case where the leader is in extended ATG/PTG/Combat
                // sequences and isn't publishing TargetWaypoint, but is still
                // geographically moving along the route.
                //
                // Criterion: leader.WorldPos is closer to route[leaderIdx+1]
                // than to route[leaderIdx]. Repeat up to CCMaxAdvancePerCall
                // times to catch up multiple stale waypoints. Bounded to
                // prevent runaway on looped routes / off-route positions.
                //
                // Only refresh _cachedLeaderRouteIdxUtc when we actually
                // advance — preserves TTL behavior for cases where leader
                // truly stops (no advance, no refresh, TTL eventually fires).
                int prevIdx = leaderIdx;
                int maxIdx = Math.Min(route.Length - 1, leaderIdx + CCMaxAdvancePerCall);
                Vector3 leaderPos = leader.WorldPos;
                while (leaderIdx < maxIdx)
                {
                    float dCur = leaderPos.WorldDistanceXYTo(route[leaderIdx]);
                    float dNext = leaderPos.WorldDistanceXYTo(route[leaderIdx + 1]);
                    if (dNext < dCur)
                        leaderIdx++;
                    else
                        break;
                }
                if (leaderIdx > prevIdx)
                {
                    logger.LogInformation(
                        $"[FFG] [FIX-FIRE] CC: Geometric cache extension " +
                        $"{prevIdx} → {leaderIdx} " +
                        $"(leader.WorldPos closer to route[{leaderIdx}] than " +
                        $"route[{prevIdx}]; +{leaderIdx - prevIdx} waypoints). " +
                        $"TargetWaypoint stale (ApproachStart-mutex or transient); " +
                        $"using leader's actual position to advance cache.");
                    _cachedLeaderRouteIdx = leaderIdx;
                    _cachedLeaderRouteIdxUtc = DateTime.UtcNow;
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

        // ── Fix CG (log-110 17:30:28 evidence) — Geometric leaderIdx reacquisition ──
        // See the CGLeaderProximityYards constant block (~line 1060) for full
        // design rationale and worked example.
        //
        // After the cache-expired branch above failed, before returning false,
        // try one more recovery: if the leader's WorldPos is geographically
        // close to a route waypoint, reacquire leaderIdx from geometry. This
        // re-enables RouteWalk (and CF body-chase override) for the next
        // FFG.Update tick, keeping the bot on pre-validated route terrain
        // instead of falling back to a pather-driven PositionChase that may
        // not be able to navigate terrain obstacles (the user-observed "two
        // hills" pinch).
        //
        // Direction-aware: prefer the closest waypoint at index >= the
        // PREVIOUSLY cached index (forward progression). If no forward
        // waypoint is within CGLeaderProximityYards, accept the globally
        // nearest — covers the case where a loop wrap-around happened
        // while the cache was expired and the leader is now physically
        // close to an earlier-index waypoint.
        if (route.Length > 0)
        {
            Vector3 leaderPos = leader.WorldPos;

            // Pass 1: forward-only search (>= last cached index).
            // Limits backward jumps that would mislead RouteWalk's
            // trailing-by-one cap into thinking the leader retreated.
            int forwardStart = _cachedLeaderRouteIdx >= 0 ? _cachedLeaderRouteIdx : 0;
            int forwardBestIdx = -1;
            float forwardBestDist = float.MaxValue;
            for (int i = forwardStart; i < route.Length; i++)
            {
                float d = leaderPos.WorldDistanceXYTo(route[i]);
                if (d < forwardBestDist)
                {
                    forwardBestDist = d;
                    forwardBestIdx = i;
                }
            }

            // Pass 2: global search if forward didn't find anything within
            // tolerance. Handles the case where the leader has moved past
            // a loop wrap-around or the cache was completely stale.
            int chosenIdx = -1;
            float chosenDist = float.MaxValue;
            string acquisitionMode = "";
            if (forwardBestIdx >= 0 && forwardBestDist <= CGLeaderProximityYards)
            {
                chosenIdx = forwardBestIdx;
                chosenDist = forwardBestDist;
                acquisitionMode = "forward";
            }
            else
            {
                int globalBestIdx = -1;
                float globalBestDist = float.MaxValue;
                for (int i = 0; i < route.Length; i++)
                {
                    float d = leaderPos.WorldDistanceXYTo(route[i]);
                    if (d < globalBestDist)
                    {
                        globalBestDist = d;
                        globalBestIdx = i;
                    }
                }
                if (globalBestIdx >= 0 && globalBestDist <= CGLeaderProximityYards)
                {
                    chosenIdx = globalBestIdx;
                    chosenDist = globalBestDist;
                    acquisitionMode = "global";
                }
            }

            if (chosenIdx >= 0)
            {
                int prevCached = _cachedLeaderRouteIdx;
                leaderIdx = chosenIdx;
                _cachedLeaderRouteIdx = chosenIdx;
                _cachedLeaderRouteIdxUtc = DateTime.UtcNow;
                logger.LogInformation(
                    $"[FFG] [FIX-FIRE] CG: Geometric leaderIdx reacquisition " +
                    $"({acquisitionMode} search). " +
                    $"Cache had expired or was absent (prevCached={prevCached}). " +
                    $"leader.WorldPos {leaderPos} is {chosenDist:0.0}y from " +
                    $"route[{chosenIdx}] (within CGLeaderProximityYards=" +
                    $"{CGLeaderProximityYards:0.0}y tolerance). " +
                    $"Reacquired leaderIdx={chosenIdx}, refreshing cache. " +
                    $"Avoids PositionChase fallback when leader is still " +
                    $"geographically on the route — keeps assist on pre-" +
                    $"validated route terrain instead of pathing through " +
                    $"unvalidated terrain obstacles.");
                return true;
            }
            // No route waypoint within tolerance — leader is genuinely off-
            // route (rescue scenario, mob roamed far, etc.). Fallthrough to
            // PositionChase is the correct behavior here.
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

    /// <summary>
    /// Fix BM-1 (log-96): returns true when assist is parked at its
    /// trailing-by-one (advance) cap or rendezvous cap, waiting for
    /// leader to advance — the "correctly idle" state that
    /// <see cref="TickActiveStuckDetection"/> and
    /// <see cref="TickNavActiveTimeout"/> must NOT flag as stuck.
    /// Replaces BH-3's transient flag, which was false on every tick
    /// except the arrival tick (parked-EVENT fires once, parked-
    /// CONDITION persists for tens of seconds).
    /// Predicate: NavTargetMode==RouteWalk AND _assistRouteIndex valid
    /// AND distance(player, route[_assistRouteIndex]) &lt; POP_DIST.
    /// See HANDOFF Fix BM-1 for full evidence.
    /// </summary>
    private bool IsParkedAtRouteCap()
    {
        if (_currentNavTargetMode != NavTargetMode.RouteWalk)
            return false;

        if (_assistRouteIndex < 0)
            return false;

        Vector3[] route = navigation.LoadedRoute;
        if (_assistRouteIndex >= route.Length)
            return false;

        float distToCurrent = playerReader.WorldPos.WorldDistanceXYTo(route[_assistRouteIndex]);
        return distToCurrent < Navigation.POP_DIST;
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
        // Fix BJ: rendezvous semantics (see RouteWalk entry block at
        // ~line 4500 for full rationale). Two caps:
        //   rendezvousCap = leaderIdx     — upper bound for clamp; the
        //                                    bot may be AT the leader's
        //                                    target waypoint during the
        //                                    post-combat rejoin phase.
        //   advanceCap    = leaderIdx - 1 — upper bound for "at the
        //                                    bot's current cap, can't
        //                                    advance further without
        //                                    the leader moving on."
        //                                    Covers both the rendezvous
        //                                    (idx == leaderIdx) and the
        //                                    steady-state trailing-by-
        //                                    one cap (idx == leaderIdx-1).
        int rendezvousCap = leaderIdx;
        int advanceCap = leaderIdx - 1;

        // Determine assist's effective current target index. If unsynced,
        // probe what the initial-sync logic WOULD pick — that's the
        // waypoint we'd be heading to once route-walking engages. Uses
        // rendezvousCap so the probe matches what the RouteWalk block
        // would pick at Initial sync.
        int idx = _assistRouteIndex >= 0
            ? _assistRouteIndex
            : FindNearestSafeRouteIndex(playerReader.WorldPos, rendezvousCap);
        if (idx < 0) return false;
        if (idx > rendezvousCap) idx = rendezvousCap;

        // Fix DP: reactive fallback armed — the direct Anchor pather failed this approach.
        // Force a defer to route-walk regardless of the at-waypoint / distance checks below,
        // so the assist route-walks toward leaderIdx (clean usePather=false) and the
        // combat-handoff then pathers the short final hop. The handoff sets
        // _anchorFallbackUsed so this does not re-fire for the final hop (loop guard).
        if (_anchorPatherFallback && !_anchorFallbackUsed)
        {
            anchorCoords = new Vector3(leader.ApproachStartWorldX, leader.ApproachStartWorldY, 0f);
            return true;
        }

        float distToTarget = playerReader.WorldPos.WorldDistanceXYTo(navigation.LoadedRoute[idx]);
        if (distToTarget <= Navigation.POP_DIST)
        {
            // ── Fix BI-1 (log-94 post-kill rotation thrash) ─────────────
            //
            // When the bot is parked at "any cap" (rendezvous cap during
            // post-combat rejoin, OR steady-state trailing-by-one cap),
            // staying in RouteWalk is correct; switching to Anchor causes
            // a visible flip-flop that the user described as "facing back
            // toward where they came from, walking for a few seconds,
            // then stopping and turning around."
            //
            // The flip-flop dynamics (kill #21 trace, 01:21:58 → 01:22:04,
            // 5 big rotations in 6 seconds): bot at route[advanceCap],
            // distToTarget ≈ 3.2y < POP_DIST (3.6y). The original
            // `return false` here selects Anchor mode (target = approach-
            // start anchor, often 10-33y in a perpendicular or opposite
            // direction). Bot rotates toward Anchor → moves slightly
            // off the route waypoint → distToTarget crosses POP_DIST →
            // CheckShouldDefer flips back → mode flips → bot turns
            // again. Repeats every 200-900ms.
            //
            // Fix BJ extends the original BI-1 check to also cover the
            // rendezvous cap (idx == leaderIdx), not just the steady-
            // state advanceCap. Both are "at the highest waypoint
            // currently allowed for this phase":
            //   - rendezvous phase (post-combat rejoin): idx ==
            //     rendezvousCap == leaderIdx
            //   - steady-state patrol: idx == advanceCap == leaderIdx-1
            // The check `idx >= advanceCap` covers both because, after
            // the clamp to rendezvousCap, idx ∈ {0..leaderIdx}, and
            // `idx >= leaderIdx - 1` matches idx == leaderIdx-1 OR idx
            // == leaderIdx.
            //
            // Evidence: 19 deferrals (CheckShouldDefer returned TRUE
            // with real anchor) across the 13.5-min log-94 run, ALL of
            // them ended via "Approach ended without arrival at route
            // waypoint" (the pending anchor was cleared without the
            // assist completing a route leg). 0 successful deferred
            // handoffs at the line-4329 success path.
            //
            // Returning TRUE without setting anchorCoords (leaves it
            // default) tells the caller to fall through to the RouteWalk
            // block without storing a pending anchor.
            if (idx >= advanceCap)
            {
                return true;
            }

            // Bot is at a non-cap waypoint (idx < advanceCap with
            // distToTarget within POP_DIST — i.e., on a transient
            // intermediate waypoint that the advance loop didn't pop
            // yet). The original "no defer → Anchor direct" path applies
            // because the bot can still advance the route on a future
            // tick; entering Anchor mode immediately is the design intent
            // here.
            return false;
        }

        // Fix BV (log-99 22:43:07.393 → 22:43:14.801, 4 Anchor↔RouteWalk flips in 7.4s
        // while the leader's ATG was continuously active for target 872146):
        // once we've committed to Anchor mode for this approach phase, don't re-evaluate
        // BR's distance-based defer. The decision was made when we first entered Anchor;
        // staying there is more important than re-checking the 25y boundary every tick.
        //
        // Mechanics of the bug Fix BV addresses: when distToAnchor hovers near the
        // AnchorReachableYards threshold (25y), small assist movements cross the threshold
        // back and forth. CheckShouldDefer alternates true/false each tick, flipping
        // _pendingAnchor in/out and toggling _currentNavTargetMode between Anchor and
        // RouteWalk. The user observed this as "the assist still seems like they are
        // turning around right around the time that the leader is pulling the mob".
        //
        // Latch semantics: once _currentNavTargetMode==Anchor, this check skips the BR
        // distance test. The latch naturally releases when Anchor mode exits (status
        // transition out of Patrolling, or BU hysteresis expires after a sustained
        // HasApproachStart=false). On the next approach phase, the latch re-evaluates
        // fresh — BR's protective behavior (don't drag assist off-route when far) still
        // applies to the INITIAL defer decision; it's only the mid-phase re-evaluation
        // that's suppressed.
        //
        // ── Fix BX (log-102 57:28.140 → 57:28.388, 248ms latch-bypass turn-back) ──
        // Symmetric latch for the PositionChase commitment path. When
        // _approachAnchorColocated is set (line 3740 area inside the `else` branch of
        // this method), GetNavigationTarget has already committed to PositionChase mode
        // for the rest of this approach phase. Allowing CheckShouldDefer to return TRUE
        // here would cause the caller (line 3684) to fall through to the RouteWalk block,
        // bypassing the latch check entirely (the latch check lives in the `else` branch
        // of `if (CheckShouldDefer)`, so a true return prevents it from running).
        //
        // Evidence: log-102 timeline showed
        //   57:28.140  Approach-start anchor co-located (3.6y<3.6y) → latch=true,
        //              mode=PositionChase. Bot at anchor, ready to engage.
        //   57:28.155  [AH+AJ+AN] focus chain fires with HasApproachStart=true,
        //              _approachAnchorColocated=true.
        //   57:28.311  [AJ] focus-chain confirmed hostile (HasApproachStart=True).
        //   57:28.388  mode → RouteWalk drift=0.0y. CheckShouldDefer BI-1 path
        //              fired: idx=25, advanceCap=25, distToTarget(assist→route[25])
        //              ≈ 1.8y ≤ POP_DIST(3.6y) → returns TRUE. Caller fell through
        //              to RouteWalk block. Bot turns toward route waypoint AWAY from
        //              leader instead of staying parked at the anchor.
        // Without Fix BX, the latch is set but cannot defend against the BI-1 path —
        // the very path that fires when the bot is at-or-past its route cap (which is
        // exactly when the latch is most likely to be set, since the bot is parked).
        //
        // Both checks express the same semantic: "we've committed to a non-RouteWalk
        // mode for this approach phase; don't let defer override that commitment."
        if (_currentNavTargetMode == NavTargetMode.Anchor || _approachAnchorColocated)
        {
            return false;
        }

        // Fix BP+BR — assist-side response to mid-leg approach signal:
        //   - Fix BP (log-97): when the leader signals approach mid-leg, abandon route-walk
        //     and head directly to the anchor. Per user design intent #2.
        //   - Fix BR (log-98 22:06:14-56 evidence): condition that on distToAnchor.
        //     When the assist is FAR from the anchor (>AnchorReachableYards), abandoning
        //     route just drags the assist further off-route (it can't reach the anchor
        //     before combat ends anyway) — observed as the progressive Initial-sync drift
        //     from idx=11→9→8→4 in log-98 (5 successive resyncs after combat got further
        //     from the leader's idx as the assist drifted south chasing partial anchors).
        //     In that case, restore the original Turn 3 behavior: defer to RouteWalk with
        //     pending anchor, so the assist makes route progress and can transition to
        //     Anchor once it reaches a route waypoint.
        Vector3 anchor = new Vector3(leader.ApproachStartWorldX, leader.ApproachStartWorldY, 0f);
        float distToAnchor = playerReader.WorldPos.WorldDistanceXYTo(anchor);
        if (distToAnchor <= AnchorReachableYards)
        {
            // Fix BP behavior: anchor is reachable, head directly. Returning false selects
            // Anchor mode in GetNavigationTarget.
            return false;
        }

        // Fix BR: anchor too far, defer to RouteWalk with pending anchor (original Turn 3).
        // See HANDOFF Fix BP+BR for full evidence.
        anchorCoords = anchor;
        return true;
    }


    private void StartNavigatingToLeader(LeaderState leader)
    {
        Vector3 target = GetNavigationTarget(leader);
        _lastNavigatedToLeaderWorldPos = target;
        _navAttempt = 0;
        _daIntermediateStepCount = 0;
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
        // Fix BQ + BU: anchor-mode hysteresis.
        //
        // Fix BQ (log-98): suppress brief HasApproachStart flickers so the assist doesn't
        // visibly turn around when the leader's ATG hiccups during approach.
        //
        // Fix BU (log-99): measure time since LAST TICK with HasApproachStart=true, not
        // since Anchor-mode entry. Log-99 showed Anchor→X→Anchor patterns where the gap
        // between flips was 1.5-3.4s — most slipped past BQ's 1500ms entry-based ceiling
        // because the timer ran from entry, not from "last seen true". With the tick-
        // refresh approach, every tick that sees HasApproachStart=true resets the timer;
        // the sticky window only counts elapsed time AFTER HasApproachStart has been
        // continuously false. During an ATG re-entry storm (HasApproachStart toggling
        // every ~1s), the timer keeps refreshing on the true ticks, so hysteresis never
        // expires until the leader truly stops approaching.
        //
        // Refresh _lastHasApproachStartUtc on every true tick — this is the heartbeat
        // that makes BU work.
        if (leader.HasApproachStart)
        {
            _lastHasApproachStartUtc = DateTime.UtcNow;
        }

        // Hysteresis check applies when:
        //   1. we are currently in Anchor mode (not yet exited)
        //   2. HasApproachStart is now false (we'd exit Anchor on this tick without hysteresis)
        //   3. we've seen HasApproachStart=true at some point in the past (anchor was real)
        //   4. leader is still Patrolling — combat/loot/rest transitions bypass hysteresis
        //   5. we have a cached anchor coordinate to return
        if (_currentNavTargetMode == NavTargetMode.Anchor &&
            !leader.HasApproachStart &&
            _lastHasApproachStartUtc != DateTime.MinValue &&
            leader.Status == BotStatus.Patrolling &&
            _lastAnchorTarget != default)
        {
            double sinceLastTrueMs = (DateTime.UtcNow - _lastHasApproachStartUtc).TotalMilliseconds;
            if (sinceLastTrueMs < AnchorModeStickyMs)
            {
                // Within sticky window AND leader still patrolling — treat as HasApproachStart
                // flicker rather than a real exit. Return the cached anchor without changing
                // mode (so the "Nav target mode change" log stays quiet for these flickers).
                return _lastAnchorTarget;
            }
        }

        // [DIAG-BW] TEMPORARY diagnostic (log-100 17.998 investigation):
        // Reaching this line with mode==Anchor && !HasApproachStart means BU's
        // hysteresis was eligible but did NOT return — i.e., one of the five
        // conditions failed OR the sticky window elapsed. Static analysis of
        // log-100 23:16:17.998 says all conditions should have held, yet mode
        // flipped Anchor → RouteWalk. This log captures the actual state at the
        // moment of bypass so the next reproduction identifies which condition
        // broke. Observation-only — no state mutation. REMOVE AFTER ROOT CAUSE
        // IDENTIFIED.
        if (_currentNavTargetMode == NavTargetMode.Anchor && !leader.HasApproachStart)
        {
            double diagSinceLastTrueMs = _lastHasApproachStartUtc == DateTime.MinValue
                ? double.MaxValue
                : (DateTime.UtcNow - _lastHasApproachStartUtc).TotalMilliseconds;
            logger.LogInformation(
                $"[FFG] [DIAG-BW] BU bypass: mode=Anchor !HasApproachStart but BU did not return. " +
                $"_lastHasApproachStartUtc={_lastHasApproachStartUtc:HH:mm:ss.fff} " +
                $"(cond_utc={_lastHasApproachStartUtc != DateTime.MinValue}), " +
                $"leader.Status={leader.Status} (cond_status={leader.Status == BotStatus.Patrolling}), " +
                $"_lastAnchorTarget={_lastAnchorTarget} (cond_anchor={_lastAnchorTarget != default}), " +
                $"sinceLastTrueMs={diagSinceLastTrueMs:0} " +
                $"(cond_time={diagSinceLastTrueMs < AnchorModeStickyMs}, sticky={AnchorModeStickyMs}ms), " +
                $"leader.ApproachStart=<{leader.ApproachStartWorldX:0.0},{leader.ApproachStartWorldY:0.0}>, " +
                $"leaderConnection.LocalAgeMs={leaderConnection.LocalAgeMs:0}.");
        }

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

                // ── Fix DM — persistent "anchor reached" guard (see field docs) ──
                // The within-session latch above is wiped on FFG.OnEnter, so a
                // plan flicker (ATG→FFG when incombatrange flips true on reaching
                // combat range) loses it and the assist re-locks to an anchor it
                // already reached and walked past while approaching the mob —
                // turning ~180° backward to it. Once reached for THIS pull, never
                // go back: keep position-chasing forward toward the leader/target
                // so the assist keeps closing on the mob. A new pull (anchor moved
                // > AnchorReachedResetYards) re-arms normal Anchor-mode rendezvous.
                if (_anchorReached)
                {
                    if (anchor.WorldDistanceXYTo(_anchorReachedPos) > AnchorReachedResetYards)
                    {
                        _anchorReached = false;  // new pull / new anchor — evaluate fresh below
                    }
                    else
                    {
                        _currentNavTargetMode = NavTargetMode.PositionChase;
                        return ComputeFollowTargetWorldPos(leader);
                    }
                }

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
                    _anchorReached = true;          // Fix DM: persists across FFG.OnEnter
                    _anchorReachedPos = anchor;     // Fix DM: scope to this pull's anchor
                    logger.LogWarning(
                    $"[FFG] Approach-start anchor co-located ({anchorDist:0.0}y < {Navigation.POP_DIST}y) — " +
                    "anchor would be immediately popped; reverting to position-chasing for the remainder of this approach phase.");
                    _currentNavTargetMode = NavTargetMode.PositionChase;
                    return ComputeFollowTargetWorldPos(leader);
                }

                // ── Fix CQ (log-116 evidence) — stale-anchor detection ──
                //
                // Symptom (user-reported): "the assist seems to be doing a lot of
                // running back and forth between two points when the leader is
                // trying to pull (and this seemed to cause the leader to pause his
                // pull) … this happened near the end of the log."
                //
                // Concrete log-116 evidence (01:12:13 → 01:13:13, ~60 seconds):
                //   - Leader entered ATG for target 964527 at 01:12:13:352.
                //     Anchor published once at 01:12:13:414 at <-752.4065, -4180.992>.
                //   - Leader's PTG hit "No range progress after 3000ms" and entered
                //     pather-escape mode. Escape never completed — leader cycled
                //     through ATG → NO PLAN → ATG → PTG repeatedly for 60s while
                //     escapeSec ticked up to 39.3s.
                //   - Fix BT's grace timer (_stableAnchorUtc = DateTime.UtcNow on
                //     every ATG.OnEnter, see ApproachTargetGoal.cs:339) refreshed
                //     the 10s grace period EVERY re-entry. Result: the same anchor
                //     at <-752.41, -4181> kept being republished for the full 60s
                //     even as the leader's actual body drifted south to <-749, -4200>
                //     and then east to <-742, -4196>. Final anchor-to-body drift:
                //     ~17-19y, sustained.
                //   - Assist mode log over the back-and-forth window shows 34 mode
                //     changes. Anchor target stayed at the stale <-752.41, -4180.992>
                //     while PositionChase targets tracked the moving leader body:
                //     <-749.74, -4197.57>, <-742.39, -4196.16>, etc. Each Anchor
                //     re-fire showed drift=16.8y, 18.3y, 18.2y vs the previous
                //     PositionChase target — a 17y target jump every cycle.
                //   - Bot positions oscillated W ↔ E by ~6y, period ~3-4s, between
                //     two clusters: west cluster (~<-751, -4196>, near anchor X)
                //     and east cluster (~<-744, -4194>, near leader's body). Matches
                //     the user's description exactly.
                //
                // Why BU/BW hysteresis didn't catch this: the sticky window is
                // 4500ms (AnchorModeStickyMs). The leader's escape cycle ran for
                // 60+ seconds with HasApproachStart toggling false→true every
                // 250-1500ms. BU/BW preserve mode across BRIEF false windows; they
                // cannot indefinitely hold a stale anchor against a 60-second
                // sustained leader-stuck condition. DIAG-BW evidence at 01:12:30:761
                // shows cond_time=False with sinceLastTrueMs=4707 — sticky window
                // exceeded — confirming BU/BW cannot mask this duration.
                //
                // Why the latch (_approachAnchorColocated) didn't help: the latch
                // was set when the bot first reached the anchor area, but every
                // expiration of the BW sticky window resets the latch (line 4475).
                // On the next HasApproachStart=true tick the anchor-co-located check
                // at line 4355 found anchorDist >> POP_DIST (bot had followed the
                // leader south during the latched window), so Anchor mode re-engaged
                // — pulling the bot back NORTH to the stale anchor.
                //
                // Geometric fix: if the bot is closer to the leader's actual body
                // than to the published anchor, the anchor is geometrically
                // irrelevant — walking toward it would walk AWAY from where the
                // leader actually is. Track the body instead.
                //
                // Self-correcting in healthy approaches: in a normal pre-combat ATG
                // the bot starts BEHIND the leader's start position, so distToAnchor
                // is small while distToBody grows as the leader moves toward the
                // mob. The condition below is FALSE in this case → Anchor mode
                // chosen correctly. Only when the bot has caught up to the leader's
                // body (stale-anchor case) does distToBody drop below distToAnchor
                // and CQ fires.
                //
                // The distToBody > POP_DIST guard skips firing when the bot is
                // essentially at the leader (≤ 3.6y) — the existing latch path
                // handles that case correctly and we don't want to interfere.
                //
                // Setting _approachAnchorColocated = true is semantically correct
                // ("the assist has logically settled with the leader's position;
                // the anchor is no longer the convergence target"). It also enables
                // BW's existing hysteresis to preserve the PositionChase decision
                // through subsequent HasApproachStart=false flickers, eliminating
                // the cycle that was reasserting Anchor mode.
                float distToBody = playerReader.WorldPos.WorldDistanceXYTo(leader.WorldPos);
                if (distToBody < anchorDist && distToBody > Navigation.POP_DIST)
                {
                    if (_currentNavTargetMode != NavTargetMode.PositionChase)
                    {
                        logger.LogInformation(
                            $"[FFG] [FIX-FIRE] CQ: bot closer to leader body ({distToBody:0.0}y) " +
                            $"than to published anchor ({anchorDist:0.0}y), anchor-to-body drift " +
                            $"{anchor.WorldDistanceXYTo(leader.WorldPos):0.0}y. Anchor appears stale " +
                            $"(leader has moved past its published anchor — escape, stuck, or fresh " +
                            $"ATG with bot already caught up). PositionChase to track actual leader.");
                    }
                    _approachAnchorColocated = true;
                    _currentNavTargetMode = NavTargetMode.PositionChase;
                    return ComputeFollowTargetWorldPos(leader);
                }

                // Anchor is not co-located — return world-space anchor directly.
                // SetSingleWaypoint's IsMapPoint check will not match (values are large
                // negative for Azeroth) and uses it as-is.

                // Fix BQ: track Anchor-mode entry time and cache the anchor for hysteresis
                // suppression of HasApproachStart flicker. Only set entry time on a NEW
                // entry (not on every tick that re-confirms Anchor mode) — otherwise the
                // sticky window resets continuously while in Anchor and the hysteresis
                // never expires.
                if (_currentNavTargetMode != NavTargetMode.Anchor)
                    _anchorModeEnteredUtc = DateTime.UtcNow;
                _lastAnchorTarget = anchor;
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
            // ── Fix BW (log-101 22:34.317→22:34.488→22:36.778 evidence) ──
            // Latch-aware hysteresis for the _approachAnchorColocated state.
            //
            // Symptom: bot reverses direction twice in ~2.3s during ATG↔PTG
            // re-entry storms. Concrete sequence from log-101:
            //   22:34.317  anchor co-located → latch=true, mode=PositionChase
            //              (bot chasing leader's body, correctly)
            //   22:34.488  HasApproachStart cache flipped false (leader did
            //              ATG→PTG at 22:34.145, ~343ms earlier). Else-branch
            //              fires, latch reset, RouteWalk block sets mode →
            //              RouteWalk with drift=17.2y. NAV BC-fix-fire on the
            //              same tick confirms: "bot is about to reverse
            //              direction" (facing·forward = -0.16 cos).
            //   22:36.778  HasApproachStart cache flipped true again (leader
            //              re-entered ATG at 22:36.558). Mode → Anchor with
            //              drift=5.7y. Second reversal: bot now turns around
            //              again to head toward the anchor.
            // User-visible: bot near leader → reverses to route waypoint AWAY
            // from leader → reverses again back toward anchor. Two visible
            // turn-backs in 2.3 seconds, with no actual change in the leader's
            // strategic intent (same mob, same target GUID, same anchor coords
            // via Fix BT's stable-anchor re-use).
            //
            // Root cause asymmetry: BU's sticky-window protects mode==Anchor
            // from premature flip after HasApproachStart=false (2.5s window
            // covers the brief PTG OnEnter gap). But mode==PositionChase
            // when set by the _approachAnchorColocated latch has no such
            // protection — the latch resets unconditionally on the very
            // first tick HasApproachStart=false, dropping the bot straight
            // through to the RouteWalk block.
            //
            // Fix: mirror BU. While the latch is set and we're within the
            // sticky window since the last HasApproachStart=true tick,
            // preserve the latch and return PositionChase chase target.
            // The leader's ATG re-entry will re-fire HasApproachStart=true
            // within typical PTG cycles (1.5-2.5s observed); on that next
            // tick the if(HasApproachStart) block's latch check at
            // line ~3714 takes over, keeping the bot in PositionChase
            // smoothly. If the approach truly ended (mob killed, leader
            // gave up), the sticky window expires after AnchorModeStickyMs
            // and the latch resets normally — original behavior.
            if (_approachAnchorColocated && _lastHasApproachStartUtc != DateTime.MinValue)
            {
                double sinceLastTrueMs = (DateTime.UtcNow - _lastHasApproachStartUtc).TotalMilliseconds;
                if (sinceLastTrueMs < AnchorModeStickyMs)
                {
                    // Within sticky window — preserve latch and PositionChase mode.
                    // Bot continues chasing leader's body across the brief
                    // HasApproachStart=false gap (typical: leader's PTG OnEnter
                    // before ATG re-entry). _pendingAnchor and AN latches are
                    // also preserved by virtue of this early return — they will
                    // be re-evaluated naturally when HasApproachStart returns
                    // true on the next tick.
                    _currentNavTargetMode = NavTargetMode.PositionChase;
                    return ComputeFollowTargetWorldPos(leader);
                }
                // Sticky window elapsed — fall through to the original
                // latch resets below. The approach genuinely ended.
            }

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
            _anchorReached = false;  // Fix DM: clear the persistent anchor-reached latch at phase end
            _anchorPatherFallback = false;        // Fix DP: clear reactive-fallback state at phase end
            _anchorFallbackUsed = false;
            _anchorApproachBestDist = float.MaxValue;
            _anchorApproachProgressUtc = DateTime.MinValue;

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

        // Route-walking (Turn 2 + Fix BF): when leader is Patrolling OR
        // Waiting (pause-for-assist) and LoadedRoute is populated,
        // assist navigates its own route waypoint trailing the leader
        // by one index. Eliminates the moving-target failure mode that
        // caused log-69 / log-83 corridor incidents. Falls through to
        // PositionChase (not WaypointSharing) when conditions don't hold.
        // State managed via _assistRouteIndex (-1 = unsynced).
        // See SCOPING Turn 2 + HANDOFF Fix BF for full design.
        if ((leader.Status == BotStatus.Patrolling || leader.Status == BotStatus.Waiting) &&
        navigation.LoadedRoute.Length > 0)
        {
            if (TryFindLeaderRouteIndex(leader, out int leaderIdx))
            {
                // Fix BJ: post-combat rendezvous semantics. Two caps,
                // applied at DIFFERENT phases:
                //
                //   rendezvousCap = leaderIdx
                //     Upper bound for Initial sync + clamp. Both bots
                //     converge on route[leaderIdx] when rejoining after
                //     combat / detour. Pre-BJ used leaderIdx-1 here,
                //     which excluded the leader's actual target — picked
                //     a route waypoint behind leader, often 30-40y away
                //     in route-progression direction. Bots converged
                //     on DIFFERENT waypoints with ~13y permanent gap
                //     (log-94 kill #15: assist 21.9y from route[49] but
                //     picked route[48] at 33.8y under old semantic).
                //
                //   advanceCap = leaderIdx - 1
                //     Upper bound for the steady-state advance loop
                //     only. Lockstep gap-of-one emerges naturally:
                //     once assist is at route[leaderIdx], advance is
                //     gated until leader hits route[leaderIdx+1].
                //
                // leaderIdx==0 edge case: rendezvousCap=0, advanceCap=-1.
                // Sync to route[0], no advance, assist waits. Correct
                // behavior (pre-BJ skipped this block entirely).
                // See HANDOFF Fix BJ for full evidence.
                int rendezvousCap = leaderIdx;
                int advanceCap = leaderIdx - 1;

                Vector3[] route = navigation.LoadedRoute;

                // Initial sync (or re-sync after non-RouteWalk tick).
                //
                // ── Fix DQ (log-130 issue #1: trailing-by-one not honored) ──
                //
                // The advance loop caps at advanceCap (leaderIdx-1) — the
                // trailing-by-one contract. The Initial sync, however, used
                // rendezvousCap (leaderIdx) as its ceiling, so a re-sync could
                // land the assist on route[leaderIdx] — the LEADER's own target
                // waypoint — instead of trailing by one. These two contracts
                // contradicted each other, and because the grind cycle resets
                // _assistRouteIndex constantly (non-Patrolling status line 5414,
                // CF body-chase override line 5382, every Anchor/PositionChase
                // excursion), the sync runs FAR more often than the advance loop
                // (log-130: 59 Initial syncs vs 1 Advanced index; log-129: 203 vs
                // 5). So the sync's ceiling dominated and the assist shadowed
                // leaderIdx in the normal case.
                //
                // Evidence (log-130 line 441, 12:33:24):
                //   "nearest safe index=9 of 146 (leaderIdx=9, rendezvousCap=9,
                //    advanceCap=8) ... distToTarget=11.9y" — synced to 9
                //   (=leaderIdx), then "Parked at idx=9 ... Trailing-by-one cap
                //   reached" (line 480) although idx=9 is AT leaderIdx, not
                //   leaderIdx-1.
                //
                // Fix: clamp the sync to advanceCap in the normal case, matching
                // the advance loop. rendezvousCap (leaderIdx) is permitted ONLY in
                // the DP reactive fallback (the documented "problem scenario"
                // where the assist must rendezvous AT the leader's waypoint). This
                // mirrors dpAdvanceLimit in the advance loop. leaderIdx==0 →
                // advanceCap=-1 → Math.Max(0,-1)=0 so the assist still syncs to
                // route[0] and waits (preserves the BJ leaderIdx==0 semantics).
                //
                // syncCap also bounds BM-3's projection advance (below) and CB's
                // forward scan, so neither can push the index past leaderIdx-1 in
                // the normal case.
                if (_assistRouteIndex < 0)
                {
                    int syncCap = (_anchorPatherFallback && !_anchorFallbackUsed)
                        ? rendezvousCap
                        : Math.Max(0, advanceCap);
                    int syncIdx = FindNearestSafeRouteIndex(playerReader.WorldPos, syncCap);

                    // ── Fix BM-3 (log-96 16:35:25:698 reversal at 175°, 16:35:32
                    //    and 16:35:45 same class — 3 of 8 log-96 reversals) ──
                    //
                    // FindNearestSafeRouteIndex picks the Euclidean-nearest
                    // waypoint at or before rendezvousCap. After combat has
                    // pulled the bot OFF the route into a position adjacent
                    // to the route, the Euclidean-nearest waypoint may be
                    // slightly BEHIND the bot's projection-position on the
                    // route — picking it forces the bot to walk backward to
                    // the chosen waypoint before the advance loop can fire
                    // and head it forward to the next one.
                    //
                    // Apply the same projection-aware advancement that
                    // TryPushRouteSpanForTarget uses for resumeIndex (FFG.cs
                    // ~line 5510, mirrors FRG.RefillWaypoints ~line 1986):
                    // if the bot's foot-of-perpendicular onto segment
                    // [route[syncIdx], route[syncIdx+1]] projects past
                    // route[syncIdx] toward route[syncIdx+1] (t > 0), the
                    // bot has already passed route[syncIdx] in projection —
                    // advance syncIdx by one.
                    //
                    // Bounded by rendezvousCap to preserve BJ's rendezvous
                    // semantics (can't advance past the leader's current
                    // target waypoint).
                    //
                    // Worked example from log-96 reversal 1:
                    //   bot at <-713.84, -4193.44>, route[8]=<-718.54, -4194.08>,
                    //   route[9]=<-714.97, -4180.76>, rendezvousCap=11.
                    //   FindNearestSafeRouteIndex returns 8 (4.74y) over 9 (12.74y).
                    //   Projection: ab=(3.57,13.32), |ab|²=190.16,
                    //   ap=(4.70,0.64), t=25.30/190.16=0.133 > 0 → advance to 9.
                    //   With BM-3, _assistRouteIndex=9. Bot walks straight
                    //   north to route[9] without the SW excursion to
                    //   route[8] first.
                    if (syncIdx >= 0 && syncIdx < syncCap) // Fix DQ: was rendezvousCap
                    {
                        Vector3 a = route[syncIdx];
                        Vector3 b = route[syncIdx + 1];
                        Vector3 botPos = playerReader.WorldPos;

                        // Distance-based: the bot is essentially at
                        // route[syncIdx] already, or the next waypoint is
                        // no farther than ~1.25× the current. Mirrors
                        // TryPushRouteSpanForTarget's incByDistance.
                        float dHere = botPos.WorldDistanceXYTo(a);
                        float dNext = botPos.WorldDistanceXYTo(b);
                        bool incByDistance = dHere < 1.5f || dNext <= dHere * 1.25f;

                        // Projection-based: bot's foot-of-perpendicular
                        // onto segment a→b is past a. Catches the post-
                        // combat off-route case where the bot is slightly
                        // past route[syncIdx] in the route direction but
                        // Euclidean-closest to it because b is much
                        // farther away.
                        bool incByProgress = false;
                        float abx = b.X - a.X;
                        float aby = b.Y - a.Y;
                        float abLenSq = abx * abx + aby * aby;
                        if (abLenSq > 0.001f)
                        {
                            float apx = botPos.X - a.X;
                            float apy = botPos.Y - a.Y;
                            float t = (apx * abx + apy * aby) / abLenSq;
                            if (t > 0.0f) incByProgress = true;
                        }

                        if (incByDistance || incByProgress)
                        {
                            int advancedIdx = syncIdx + 1;
                            logger.LogInformation(
                                $"[FFG] [FIX-FIRE] BM-3: Initial sync projection-aware " +
                                $"advancement {syncIdx} → {advancedIdx} " +
                                $"(dHere={dHere:0.0}y, dNext={dNext:0.0}y, " +
                                $"incByDistance={incByDistance}, " +
                                $"incByProgress={incByProgress}). " +
                                $"Euclidean-nearest was route[{syncIdx}] but bot " +
                                $"has projection-passed it; advancing one index " +
                                $"forward to avoid backward walk.");
                            syncIdx = advancedIdx;
                        }
                    }

                    // ── Fix CB (log-105 09:21:10 evidence) ──
                    // After BM-3's one-step projection advance, check if
                    // walking from syncIdx forward would head bot AWAY from
                    // leader's actual current position. When the route loops
                    // back near the bot's post-combat position but the leader
                    // is in a different geographic direction, walking the
                    // route forward from syncIdx takes a long detour through
                    // the U-turn before reaching the leader. The fix scans
                    // forward to find a waypoint better aligned with the
                    // leader's direction, skipping the detour.
                    //
                    // Worked example from log-105 09:21:10:
                    //   Bot at <-719.86, -4210.95>, leader.WorldPos ~14.8y away.
                    //   syncIdx=7 picked (route[7]=<-720.45, -4207.66>, 3.3y N).
                    //   toRoute = route[7]-bot = (-0.59, 3.29)  |toRoute|=3.3
                    //   Leader was SW of bot (per anchor proxy <-723.24, -4215.79>).
                    //   toLeader = (-3.38, -4.84)               |toLeader|=5.9
                    //   cos = (-0.59*-3.38 + 3.29*-4.84) / (3.3*5.9)
                    //       = (1.99 - 15.93) / 19.5 = -0.71
                    //   cos < -0.3 → fire CB scan.
                    //   route[12]=<-743.20, -4197.20>: toCandidate=(-23.34, 13.75)
                    //     dot_with_leader = -23.34*-3.38 + 13.75*-4.84
                    //                     = 78.89 - 66.55 = 12.34
                    //     cos = 12.34 / (27.09*5.9) = 0.077 (toward leader)
                    //   Skip 7 → 12 (positive cos = aligned with leader direction).
                    //
                    // Bounds: only fire when leader is close (within
                    // CBLeaderProximityYards). When leader is far, the
                    // Euclidean-nearest behavior is what we want — the bot
                    // has a long way to go either way.
                    if (syncIdx >= 0 && syncIdx < rendezvousCap)
                    {
                        Vector3 botPosCB = playerReader.WorldPos;
                        Vector3 leaderPosCB = leader.WorldPos;
                        float botToLeaderXY = botPosCB.WorldDistanceXYTo(leaderPosCB);

                        if (botToLeaderXY < CBLeaderProximityYards && botToLeaderXY > 0.001f)
                        {
                            Vector3 picked = route[syncIdx];
                            float pickedDx = picked.X - botPosCB.X;
                            float pickedDy = picked.Y - botPosCB.Y;
                            float pickedLen = MathF.Sqrt(pickedDx * pickedDx + pickedDy * pickedDy);

                            float leaderDx = leaderPosCB.X - botPosCB.X;
                            float leaderDy = leaderPosCB.Y - botPosCB.Y;

                            if (pickedLen > 0.001f)
                            {
                                float pickedCos = (pickedDx * leaderDx + pickedDy * leaderDy)
                                                   / (pickedLen * botToLeaderXY);

                                if (pickedCos < CBBackwardCosThreshold)
                                {
                                    // Picked waypoint heads away from leader.
                                    // Scan forward for a better-aligned one
                                    // within reasonable distance.
                                    int bestIdx = syncIdx;
                                    float bestCos = pickedCos;
                                    for (int i = syncIdx + 1; i <= syncCap; i++) // Fix DQ: was rendezvousCap
                                    {
                                        Vector3 candidate = route[i];
                                        float cdx = candidate.X - botPosCB.X;
                                        float cdy = candidate.Y - botPosCB.Y;
                                        float clen = MathF.Sqrt(cdx * cdx + cdy * cdy);

                                        // ── Fix CH (log-111 19:30:29 evidence) ──
                                        // Skip candidates too far from bot but
                                        // KEEP SCANNING (was `break` before
                                        // Fix CH, which exited the scan at the
                                        // first too-far waypoint).
                                        //
                                        // The original `break` protected against
                                        // picking 50+ y targets in normal patrol
                                        // (the design comment said "sticking
                                        // near syncIdx is better than picking
                                        // a 50+ y target"). But in routes with
                                        // LOOPS, the route temporarily extends
                                        // FAR from the bot before wrapping back
                                        // close to it — and the `break` exited
                                        // the scan before reaching the wrap-back
                                        // waypoints that are physically NEAR the
                                        // bot AND aligned with leader direction.
                                        //
                                        // Log-111 worked example at 19:30:29:481:
                                        //   bot=<-731.12,-4276.92>, leader~<-749,-4296>
                                        //   syncIdx=3 (route[3]=<-712.94,-4263.46>,
                                        //     22.6y from bot, cos=-0.98 against
                                        //     leader direction — heads NE while
                                        //     leader is SW)
                                        //   Scan i=4: route[4] at 29.7y, cos=-0.91
                                        //     (marginal improvement, < +0.3 margin
                                        //     vs -0.98, no bestIdx update)
                                        //   Scan i=5: route[5] at 42.5y > 35y
                                        //     → OLD BEHAVIOR: break, exit scan
                                        //                    with bestIdx=3
                                        //   Routes 22-24 (the loop wrap-back) are
                                        //   22-27y from bot AND cos=+0.09 to +0.97
                                        //   (well-aligned with leader). They were
                                        //   never seen by the scan.
                                        //
                                        //   → NEW BEHAVIOR: continue past route[5]
                                        //     and onwards. Eventually reach route[22]
                                        //     at 22.8y (within 35y), cos=+0.09; route[23]
                                        //     at 23.5y, cos=+0.77; route[24] at 27.0y,
                                        //     cos=+0.98. Each successive improvement
                                        //     update bestIdx via the margin check.
                                        //     Final bestIdx=24 (within 6y of leader).
                                        //     Bot walks 27y to route[24], leader at
                                        //     route[25] — trailing-by-one rendezvous.
                                        //
                                        //   Old behavior had the bot walking
                                        //   route[3] → 4 → 5 → ... → 22 (a 19-waypoint,
                                        //   ~200y loop) to reach a leader 28y direct
                                        //   distance away. ~47s elapsed.
                                        //
                                        // Safety: the `ccos > bestCos +
                                        // CBImprovementMargin` check at the
                                        // bottom of the loop still prevents
                                        // picking a marginal candidate. To
                                        // override bestIdx, a candidate must
                                        // improve cos by ≥0.3. Picking a 50y
                                        // target requires that target to be
                                        // significantly better-aligned than
                                        // anything closer — which is the case
                                        // ONLY in loop-back scenarios. In
                                        // normal patrol, route[N+1] has the
                                        // best alignment already (route goes
                                        // toward leader), so the margin
                                        // prevents far-target picks.
                                        //
                                        // Iteration cap: rendezvousCap bounds
                                        // the upper limit naturally
                                        // (rendezvousCap ≤ route.Length-1, and
                                        // for 146-waypoint routes the worst
                                        // case is ~146 distance computations,
                                        // negligible).
                                        if (clen > CBMaxScanDistanceYards) continue;
                                        if (clen < 0.001f) continue;

                                        float ccos = (cdx * leaderDx + cdy * leaderDy)
                                                      / (clen * botToLeaderXY);
                                        if (ccos > bestCos + CBImprovementMargin)
                                        {
                                            bestCos = ccos;
                                            bestIdx = i;
                                        }
                                    }

                                    if (bestIdx != syncIdx)
                                    {
                                        logger.LogInformation(
                                            $"[FFG] [FIX-FIRE] CB: Direction-aware Initial sync " +
                                            $"skip {syncIdx} → {bestIdx} " +
                                            $"(route[{syncIdx}] heads away from leader: " +
                                            $"cos={pickedCos:0.00}; route[{bestIdx}] " +
                                            $"cos={bestCos:0.00}). " +
                                            $"Leader {botToLeaderXY:0.0}y away. " +
                                            $"Skipping {bestIdx - syncIdx} route waypoints to " +
                                            $"avoid backward walk through route U-turn / loop.");
                                        syncIdx = bestIdx;
                                    }
                                }
                            }
                        }
                    }

                    _assistRouteIndex = syncIdx;
                    logger.LogInformation(
                        $"[FFG] [ROUTE-WALK] Initial sync: assist at {playerReader.WorldPos}, " +
                        $"nearest safe index={syncIdx} of {route.Length} " +
                        $"(leaderIdx={leaderIdx}, rendezvousCap={rendezvousCap}, " +
                        $"advanceCap={advanceCap}), " +
                        $"target wp={route[syncIdx]}, " +
                        $"distToTarget={playerReader.WorldPos.WorldDistanceXYTo(route[syncIdx]):0.0}y.");
                }

                // Clamp if leader retreated. Allow _assistRouteIndex
                // == leaderIdx (rendezvous semantics, BJ), but never
                // > leaderIdx — that would mean the assist is past
                // the leader's current target, which can only happen
                // if leaderIdx moved BACKWARD (e.g., manual override,
                // rescue inserting an earlier waypoint).
                if (_assistRouteIndex > rendezvousCap)
                {
                    logger.LogWarning(
                        $"[FFG] [ROUTE-WALK] Clamping _assistRouteIndex from " +
                        $"{_assistRouteIndex} to {rendezvousCap} " +
                        $"(leader retreated? leaderIdx={leaderIdx}).");
                    _assistRouteIndex = rendezvousCap;
                }

                // Advance if arrived. Uses advanceCap (= leaderIdx-1)
                // to enforce the steady-state trailing-by-one
                // invariant — once both bots are on-route, the assist
                // can never advance to the same index the leader is
                // currently targeting. Once the assist arrives at
                // route[leaderIdx] via the Initial sync's rendezvous
                // semantics, this loop's condition
                // `_assistRouteIndex (leaderIdx) < advanceCap
                // (leaderIdx-1)` is FALSE — no advance, assist waits.
                // The leader then moves and its TargetWaypoint
                // advances; on the next tick, new leaderIdx is the
                // old + 1, new advanceCap is old leaderIdx, and the
                // assist (still at old leaderIdx) is exactly at the
                // new cap — still no advance. The assist starts
                // advancing only when the leader's TargetWaypoint
                // reaches old leaderIdx + 2 (new advanceCap = old
                // leaderIdx + 1 > _assistRouteIndex = old leaderIdx).
                //
                // Loop in case multiple close-packed waypoints are
                // within POP_DIST simultaneously.
                // Fix DP: while the reactive fallback is armed, advance up to the leader's own
                // route index (rendezvousCap) rather than the steady-state trailing-by-one cap
                // (advanceCap). The anchor is the leader's position, so leaderIdx lands the
                // assist right next to it; the leader has stopped (it's pulling), so advancing
                // to its index can't overrun a moving leader.
                // Fix EM (run-137 deadlock): when the leader is paused-for-assist (Status=Waiting)
                // it is STATIONARY -- exactly like the DP pulling case above -- so advancing to its
                // own index (rendezvousCap) cannot overrun a moving leader, and it is REQUIRED to
                // close the gap and release the leader's pause. Without it the assist parks at the
                // trailing-by-one cap (leaderIdx-1); when that route segment is wider than the
                // leader's LeaderPauseYards (25y) the parked assist is stranded outside the release
                // radius and both bots mutually wait forever (run-137: route[20]->route[21] ~35y, the
                // assist parked 32.9y from the leader for the rest of the session). The advanced,
                // non-co-located target this produces also makes OnDestinationReached re-path past
                // its sit-in-place park (that only fires on a co-located retry target), so no
                // separate guard change is needed. Reverts to advanceCap the moment the leader
                // resumes (Status=Patrolling), so steady-state trailing-by-one is unchanged.
                bool leaderWaitingForAssist = leader.Status == BotStatus.Waiting;
                int dpAdvanceLimit = ((_anchorPatherFallback && !_anchorFallbackUsed) || leaderWaitingForAssist)
                    ? rendezvousCap
                    : advanceCap;
                // Fix EO (run-139): the loop ceiling is rendezvousCap (= leaderIdx) rather
                // than the trailing-by-one advanceCap. Non-blacklisted advances still stop at
                // dpAdvanceLimit (the explicit break below), so steady-state trailing-by-one is
                // unchanged; the higher ceiling exists ONLY so a blacklisted waypoint sitting at
                // the cap can be skipped past it.
                while (_assistRouteIndex < rendezvousCap)
                {
                    // Fix EO (run-139): a route waypoint can sit inside a static blacklist rect
                    // loaded from the route file (route[64]=<113.28,-4709.24> inside one of
                    // 05-08_Durotar_big v2.json's 3 rects). The assist can never arrive at it --
                    // Navigation.SkipBlacklistedWaypoints pops it (wpCount=0), the assist idles
                    // with no nav target, reports Stuck, and the leader pauses for it: mutual
                    // standstill. The leader survives because its multi-waypoint refill pops the
                    // bad waypoint and paths to the next; mirror that here by advancing PAST a
                    // blacklisted waypoint WITHOUT the POP_DIST arrival gate, up to rendezvousCap.
                    // Ceiling is rendezvousCap (never beyond): the leader publishes only
                    // non-blacklisted waypoints (TopPublishableWaypointW), so leaderIdx is
                    // guaranteed reachable and skipping up to it always suffices; advancing past
                    // it would push the assist ahead of the tank -- exactly what trailing-by-one
                    // prevents.
                    bool wpBlacklisted = navigation.AreaBlacklist != null &&
                                         navigation.AreaBlacklist.ContainsWorld(route[_assistRouteIndex]);
                    if (wpBlacklisted)
                    {
                        int blIdx = _assistRouteIndex;
                        _assistRouteIndex++;
                        logger.LogWarning(
                            $"[FFG] [ROUTE-WALK] [FIX-FIRE] EO: skipping blacklisted route " +
                            $"waypoint idx={blIdx} ({route[blIdx]}) → idx={_assistRouteIndex} " +
                            $"(advanceCap={advanceCap}, rendezvousCap={rendezvousCap}, " +
                            $"leaderIdx={leaderIdx}).");
                        continue;
                    }

                    // Non-blacklisted: steady-state trailing-by-one advance, gated on the cap
                    // (dpAdvanceLimit) and physical arrival (POP_DIST).
                    if (_assistRouteIndex >= dpAdvanceLimit)
                        break;
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

                        // Fix DP: if this handoff is the reactive-fallback recovery completing,
                        // latch _anchorFallbackUsed and clear the fallback so the short final
                        // Anchor pather hop from here is NOT re-deferred (loop guard).
                        if (_anchorPatherFallback)
                        {
                            _anchorPatherFallback = false;
                            _anchorFallbackUsed = true;
                            logger.LogInformation(
                                $"[FFG] [FIX-FIRE] DP: reactive fallback reached route idx={handoffIdx} " +
                                $"(near anchor) — committing to the short final Anchor pather hop, " +
                                $"no further re-defer this approach.");
                        }

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
                            _anchorReached = true;            // Fix DM: persists across FFG.OnEnter
                            _anchorReachedPos = anchorTarget; // Fix DM: scope to this pull's anchor
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

                        // Fix BQ: track Anchor-mode entry for hysteresis (combat-handoff path).
                        // See the direct-entry site for rationale.
                        if (_currentNavTargetMode != NavTargetMode.Anchor)
                            _anchorModeEnteredUtc = DateTime.UtcNow;
                        _lastAnchorTarget = anchorTarget;
                        _currentNavTargetMode = NavTargetMode.Anchor;
                        return anchorTarget;
                    }
                    // Pending anchor set but not yet arrived — keep
                    // route-walking. The pending anchor stays in
                    // place; this tick returns the route target
                    // below.
                }

                // ── Fix CF (log-109 16:08:57 evidence) ──
                // Per-tick body-chase override when the route would
                // detour through a loop while the leader is geographically
                // close. See the CFBodyChaseProximityYards constant block
                // (~line 1020) for full design rationale and worked
                // example.
                //
                // Gated on:
                //   - leader within CFBodyChaseProximityYards (close
                //     enough that body-chase is feasible — within healer
                //     range)
                //   - _assistRouteIndex valid (Initial sync has run)
                //   - _pendingAnchor not set (combat handoff in flight —
                //     body-chase would miss the Anchor mode transition)
                //   - next route waypoint at least CFRouteDetourMargin
                //     yards FURTHER from leader than the bot is (route
                //     is genuinely detouring, not just slightly off-axis)
                //
                // Reset _assistRouteIndex = -1 so the next RouteWalk-
                // eligible tick re-syncs from the assist's new (post-
                // body-chase) position. Avoids stale-index issues where
                // _assistRouteIndex still points at route[N] far behind
                // the bot's actual position after body-chase moved it
                // forward.
                if (_pendingAnchor == default)
                {
                    float botToLeaderDistCF = playerReader.WorldPos.WorldDistanceXYTo(leader.WorldPos);
                    if (botToLeaderDistCF < CFBodyChaseProximityYards
                        && _assistRouteIndex >= 0
                        && _assistRouteIndex < route.Length)
                    {
                        Vector3 nextWp = route[_assistRouteIndex];
                        float nextWpToLeader = nextWp.WorldDistanceXYTo(leader.WorldPos);
                        if (nextWpToLeader > botToLeaderDistCF + CFRouteDetourMargin)
                        {
                            if (_currentNavTargetMode != NavTargetMode.PositionChase)
                            {
                                logger.LogInformation(
                                    $"[FFG] [FIX-FIRE] CF: Body-chase override — " +
                                    $"leader {botToLeaderDistCF:0.0}y away " +
                                    $"(< CFBodyChaseProximityYards={CFBodyChaseProximityYards:0.0}y), " +
                                    $"next route wp[{_assistRouteIndex}]={nextWp} is " +
                                    $"{nextWpToLeader:0.0}y from leader " +
                                    $"(+{nextWpToLeader - botToLeaderDistCF:0.0}y further than direct " +
                                    $"chase). Route is detouring through a loop or U-turn; " +
                                    $"switching RouteWalk → PositionChase to chase leader directly. " +
                                    $"Resetting _assistRouteIndex={_assistRouteIndex} → -1 so the " +
                                    $"next RouteWalk-eligible tick re-syncs from the bot's new " +
                                    $"position.");
                            }
                            _assistRouteIndex = -1;
                            _currentNavTargetMode = NavTargetMode.PositionChase;
                            return ComputeFollowTargetWorldPos(leader);
                        }
                    }
                }

                // Fix EO (run-139) safety net: if the target is STILL blacklisted after the
                // skip loop above (degenerate -- even route[rendezvousCap] is blacklisted, which
                // shouldn't happen since the leader publishes only non-blacklisted waypoints;
                // reachable only via a stale cached leaderIdx), fall back to PositionChase so the
                // pather routes around the rect toward the leader's body instead of the assist
                // idling on a waypoint Navigation.SkipBlacklistedWaypoints will immediately pop.
                if (navigation.AreaBlacklist != null &&
                    navigation.AreaBlacklist.ContainsWorld(route[_assistRouteIndex]))
                {
                    if (_currentNavTargetMode != NavTargetMode.PositionChase)
                        logger.LogWarning(
                            $"[FFG] [ROUTE-WALK] [FIX-FIRE] EO: route target idx={_assistRouteIndex} " +
                            $"({route[_assistRouteIndex]}) still blacklisted through rendezvousCap " +
                            $"({rendezvousCap}) -- falling back to PositionChase.");
                    _currentNavTargetMode = NavTargetMode.PositionChase;
                    return ComputeFollowTargetWorldPos(leader);
                }

                _currentNavTargetMode = NavTargetMode.RouteWalk;
                return route[_assistRouteIndex];
                // (Fix BJ: the `allowedAdvance < 0` fall-through path that
                // used to live here is gone. With rendezvous semantics
                // the leaderIdx == 0 case is handled positively: bot
                // navigates to route[0] as the rendezvous waypoint.)
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
        //
        // Fix BF: also accept Status==Waiting (pause-for-assist) here for
        // symmetry with the primary RouteWalk gate above. In legacy
        // configurations without a loaded route, the leader's last published
        // waypoint is still the correct target to chase even when the leader
        // is paused.
        if (_rendezvousConfirmed &&
            (leader.Status == BotStatus.Patrolling || leader.Status == BotStatus.Waiting) &&
            leader.HasTargetWaypoint)
        {
            // Fix Y: WaypointSharing distance gate. When dist > NavigatingMinYards
            // (14y), exit to PositionChase. Pre-Y, once _rendezvousConfirmed
            // latched the assist kept targeting the leader's forward-broadcast
            // wp even when 20+y behind, dragging path-finds past obstacles
            // the assist hadn't yet crossed (log-66: leader popped corridor
            // wp with 3.3y of slack, broadcast next wp far past it, assist
            // 21.7y north got pathed around a tree, ~25s stuck recovering).
            //
            // PositionChase uses ComputeFollowTargetWorldPos (leader's body
            // with FollowStopShortYards offset) — anchors path-find at
            // leader's CURRENT progressed position. Once assist closes
            // back inside NavigatingMinYards, WaypointSharing resumes
            // automatically. _rendezvousConfirmed not cleared (matches the
            // blacklist-guard pattern just below).
            // See HANDOFF Fix Y for full evidence.
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

        // Fix Z: when lenXY > NavigatingMinYards (14y) in PositionChase,
        // cap target distance from assist at FarTargetMaxYards (7y) along
        // the assist→leader line. Pre-Z, a far target sitting PAST a
        // navmesh constraint (log-67: assist at <1974,-2163> targeting
        // leader at <1977,-2152> through a two-tree corridor) produced
        // a winding 14-node detour starting SW; bot drifted 25y east
        // before recovery (~13s). Putting the target IN/BEFORE the
        // constraint zone forces the pathfinder to return short straight
        // paths.
        //
        // 7y matches FollowingMaxYards — when bot reaches the capped
        // target, dist to leader is approx (lenXY - 7), landing in the
        // Following band naturally. No hysteresis on the 14y threshold
        // (oscillation would just produce ~4y target jumps absorbed
        // by path-preservation suppression).
        //
        // Intersection with BL projection-safety loop below: Fix Z runs
        // first, then BL loop. BL correctness takes precedence over Fix Z.
        // See HANDOFF Fix Z for full evidence.
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

        // Blacklist projection safety (Fix 18 + Fix 19 + log-36):
        //
        // Outer trigger uses TryGetContainingRectInflated with
        // ProjectionSafetyMarginYards (6y) — not strict ContainsWorld —
        // so targets within 6y of a BL rect ALSO trigger the projection
        // loop. Strict containment let route execution overshoot (3.6y
        // POP_DIST + inertia) deposit the bot inside, causing 30s+
        // BL-edge ping-pong (log-44: 6 oscillations over 34s).
        //
        // Inner loop projects in 2y steps along leader→assist line until
        // a point ≥6y from the rect is found. If the entire segment is
        // within 6y of a rect, control falls through to Fix 12's self-
        // escape logic below. 6y margin matches Fix 12 ExitMargin so
        // both checks find the same candidate.
        // See HANDOFF Fix 18/19/33 for full evidence.
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

        // ── Fix BZ (log-103 02:40:30→02:40:44 "circuit of waypoints") ──
        //
        // Anchor mode means the assist has decided to abandon the patrol route
        // and head DIRECTLY to the approach-start anchor (the leader's body
        // position at ATG entry). The whole point of Anchor mode is to short-
        // circuit the route — the route is the long way around; the anchor
        // is a beeline to where the action is.
        //
        // Route-span construction is `[route[resumeIndex..leaderIdx], target]`.
        // For PositionChase/RouteWalk, this is correct — the bot walks the
        // route to catch up with the leader. For Anchor mode, this forces the
        // bot to walk through every intermediate route waypoint BEFORE finally
        // reaching the anchor as the trailing waypoint. The bot effectively
        // takes a long detour through the patrol route, defeating the purpose
        // of Anchor mode.
        //
        // Evidence: log-103 02:40:30.490 → 02:40:44 (14-second window):
        //   • Mode flipped PC → Anchor (target=<-715.83,-4222.29> = anchor).
        //   • [FIX-FIRE] BE: route-span push — 11 waypoints
        //     [route[8..17]=10 pre-validated waypoints + trailing target].
        //   • Bot then POPped waypoints sequentially over the next ~10s:
        //       02:40:32.009  POP route[8]  → newWpTop=route[9]
        //       02:40:33.971  POP route[9]  → newWpTop=route[10]
        //       02:40:35.968  POP route[10] → newWpTop=route[11]
        //       02:40:37.822  POP route[11] → newWpTop=route[12]
        //       02:40:39.926  POP route[12] → newWpTop=route[13]
        //   • At 02:40:34.257 the leader's BN/BS sync-pause TIMED OUT because
        //     the assist was 39y from anchor (vs the 12y "ready" threshold)
        //     — the assist had walked AWAY from the anchor along the route
        //     while it should have headed directly to it.
        //   • Leader's 14.4-second stuck ATG followed because the assist
        //     wasn't in position to engage.
        //
        // Why this didn't manifest in prior runs: most PTG cycles are 1-3s,
        // so the bot doesn't have time to POP many route waypoints before
        // mode flips back. Log-103's pathologically long 14.4-second ATG
        // (leader couldn't reach mob) gave the bot enough time for 5+ POPs.
        //
        // Fix: when mode is Anchor, fall through to SetSingleWaypoint with
        // the anchor as a single target. The pather will compute a direct
        // route (one pather call — acceptable cost; the anchor is at most
        // RouteSpanLeaderProximityYards=25y away in the common case).
        //
        // PositionChase and RouteWalk still use route-span — the original
        // Fix BE motivation (avoid pather thrash when the assist is patrolling
        // along with the leader) applies to those modes, not to Anchor.
        if (_currentNavTargetMode == NavTargetMode.Anchor)
        {
            outcome = "anchor-mode-direct (Fix BZ — abandon route, go direct to anchor)";
            return false;
        }

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

        // ── Fix CS (log-118 02:36:01:061 evidence) ──
        // Honor CB's loop-aware index decision when constructing the span.
        //
        // closestIndex is Euclidean-nearest — it has no notion of loop
        // topology. When the route loops back near the bot (the route's
        // physical geometry has two arcs passing close together but at
        // very different route indices), closestIndex picks the LOW-index
        // arc by Euclidean nearness. If CB has already detected this and
        // advanced _assistRouteIndex to the HIGH-index arc (the one
        // aligned with leader's direction), closestIndex's pick contradicts
        // CB and would walk the bot through the entire loop.
        //
        // GetNavigationTarget (route-walk path) returns route[_assistRouteIndex]
        // as the navigation target. SetWaypointLoopGuarded passes this to
        // TryPushRouteSpanForTarget. Inside, this function should respect the
        // same loop-aware index that produced the target — not re-derive a
        // contradictory one via raw Euclidean nearness.
        //
        // Use Math.Max so:
        //   - assistIdx < 0 (no route-walk active): use closestIndex (no
        //     loop-aware hint available, fall back to original behavior).
        //   - assistIdx == closestIndex: no change.
        //   - assistIdx > closestIndex (CB advanced past a loop): use
        //     assistIdx, honoring CB's loop decision.
        //   - assistIdx < closestIndex (bot moved past _assistRouteIndex
        //     before the route-walking advance loop caught up): use
        //     closestIndex — the bot is genuinely past _assistRouteIndex
        //     in route order. Math.Max preserves both cases naturally.
        //
        // Worked example from log-118 02:36:01:061:
        //   Bot at <-720.40, -4280.79>, leader 22.2y SW.
        //   Route loops: route[3]=<-712.94, -4263.46> is ~22.6y from bot
        //     (NE direction). route[24]=<-753.40, -4292.22> is ~34.9y
        //     from bot (SW direction, aligned with leader).
        //   closestIndex = FindNearestSafeRouteIndex → 3 (Euclidean-nearest)
        //   _assistRouteIndex = 24 (CB advanced 3→24, cos=+0.78 vs leader)
        //   leaderIdx = 27 (leader's published patrol waypoint route[27])
        //
        //   WITHOUT CS: resumeIndex=3, span=[route[3..27]]=25 waypoints.
        //     Bot walks NE to route[3], then loops back SW through route[4..27]
        //     ~360y total. (Observed: 02:36:01 → 02:36:55, 54-second detour.)
        //
        //   WITH CS: resumeIndex=24, span=[route[24..27]]=4 waypoints.
        //     Bot walks SW directly (~31y to route[24], then 3 short steps
        //     to route[27]). Total ~40y.
        //
        // The fix is read-only with respect to _assistRouteIndex (no
        // assignment) — it only INFLUENCES closestIndex used downstream.
        // The CB advance lives in the GetNavigationTarget initial-sync
        // block and stays the source of truth for the route-walk state.
        int assistIdx = _assistRouteIndex;
        if (assistIdx > closestIndex && assistIdx < route.Length)
        {
            logger.LogInformation(
                $"[FFG] [FIX-FIRE] CS: TryPushRouteSpanForTarget honoring " +
                $"_assistRouteIndex={assistIdx} over closestIndex={closestIndex} " +
                $"(Δ={assistIdx - closestIndex} indices). " +
                $"closestIndex is Euclidean-nearest (loop-unaware); " +
                $"_assistRouteIndex reflects CB's loop-aware advance. " +
                $"Using _assistRouteIndex as the span start to honor CB's decision " +
                $"and avoid the bot walking through a route U-turn / loop.");
            closestIndex = assistIdx;
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

        // ── Fix DH (log-126 20:32:14 evidence) — direction-aware span start ──
        // CB applies a direction-aware skip on the Initial-sync path: when the
        // bot is near the leader and route[syncIdx] heads AWAY from the leader,
        // it scans forward for a better-aligned index. But the resumeIndex
        // computed above (distance/progress projection + POP) has NO such
        // check, so the span can START at a waypoint that heads away from the
        // target — the bot walks BACKWARD to it before progressing.
        //
        // log-126 20:32:14:698: BE pushed route[8..18] from closestIndex=7;
        // route[8]=<-718.5,-4194.1> was ~19y NORTH while the trailing target
        // (leader's body) was SOUTH at <-720.9,-4213.0>, so the assist turned
        // north (backward). 0.6s later CB's Initial sync skipped 7→18
        // (route[7] cos=-0.69), but BE had already pushed the backward span.
        // The flip between BE's backward span (north) and CR's forward
        // body-chase (south) was the post-combat N/S oscillation the user saw.
        //
        // CR (below) does not catch this: once resumeIndex projects to 8, the
        // trailing target is closer to route[leaderIdx]=18 than to route[8]
        // (route[8] is far north), so CR's malformed check passes and the
        // backward span is pushed anyway.
        //
        // Apply CB's skip here against `target` (what the span must lead
        // toward): if the bot is within CBLeaderProximityYards of the target
        // and route[resumeIndex] heads away (cos < CBBackwardCosThreshold),
        // scan forward [resumeIndex+1..leaderIdx] for the best-aligned index
        // (CBMaxScanDistanceYards / CBImprovementMargin gates, identical to
        // CB). If the skip reaches leaderIdx, the resumeIndex>=leaderIdx check
        // below falls through to SetSingleWaypoint (forward body-chase) — no
        // backward span. In healthy patrol route[resumeIndex] heads TOWARD the
        // target (cos > 0), so DH is inert.
        if (resumeIndex < leaderIdx)
        {
            float botToTargetDH = playerPos.WorldDistanceXYTo(target);
            if (botToTargetDH < CBLeaderProximityYards && botToTargetDH > 0.001f)
            {
                Vector3 pickedDH = route[resumeIndex];
                float pdx = pickedDH.X - playerPos.X;
                float pdy = pickedDH.Y - playerPos.Y;
                float plen = MathF.Sqrt(pdx * pdx + pdy * pdy);
                float tdx = target.X - playerPos.X;
                float tdy = target.Y - playerPos.Y;
                if (plen > 0.001f)
                {
                    float pickedCosDH = (pdx * tdx + pdy * tdy) / (plen * botToTargetDH);
                    if (pickedCosDH < CBBackwardCosThreshold)
                    {
                        int bestIdxDH = resumeIndex;
                        float bestCosDH = pickedCosDH;
                        for (int i = resumeIndex + 1; i <= leaderIdx; i++)
                        {
                            Vector3 cand = route[i];
                            float cdx = cand.X - playerPos.X;
                            float cdy = cand.Y - playerPos.Y;
                            float clen = MathF.Sqrt(cdx * cdx + cdy * cdy);
                            if (clen > CBMaxScanDistanceYards) continue;
                            if (clen < 0.001f) continue;
                            float ccos = (cdx * tdx + cdy * tdy) / (clen * botToTargetDH);
                            if (ccos > bestCosDH + CBImprovementMargin)
                            {
                                bestCosDH = ccos;
                                bestIdxDH = i;
                            }
                        }
                        if (bestIdxDH != resumeIndex)
                        {
                            logger.LogInformation(
                                $"[FFG] [FIX-FIRE] DH: direction-aware span-start skip " +
                                $"{resumeIndex} → {bestIdxDH} (route[{resumeIndex}]={pickedDH} " +
                                $"heads away from target: cos={pickedCosDH:0.00}; " +
                                $"route[{bestIdxDH}] cos={bestCosDH:0.00}). Target {target} " +
                                $"{botToTargetDH:0.0}y away. Prevents backward span-start walk.");
                            resumeIndex = bestIdxDH;
                        }
                    }
                }
            }
        }

        if (resumeIndex >= leaderIdx)
        {
            // At or past leader's route position. Forward span would be empty
            // or single-element; let caller use SetSingleWaypoint for the
            // tight body-chase to wherever the leader actually is.
            outcome = $"resumeIndex-at-or-past-leader (closestIndex={closestIndex}, resumeIndex={resumeIndex}, leaderIdx={leaderIdx})";
            return false;
        }

        // Fix BK (log-95 11:52:11:626 → 11:52:18:524 and 11:52:25:114 →
        // 11:52:30:097, two of 26 systematic occurrences in a 9-min run):
        //
        // The previous check `targetIsAtLeaderWp` only tested whether the
        // target coincides with route[leaderIdx]. When `target` was at
        // some OTHER route waypoint (typically route[resumeIndex] in
        // RouteWalk mode, since the FFG's nav target = route[_assist-
        // RouteIndex] which is ≤ leaderIdx-1 in steady state or
        // leaderIdx during rendezvous, but the projection-aware
        // advancement may have moved resumeIndex past _assistRouteIndex),
        // the check evaluated false and the trailing target was appended
        // as a duplicate of a route waypoint already in the span.
        //
        // Failure mode: nav queue loaded as
        //   [route[resumeIndex], …, route[leaderIdx], trailing=route[K]]
        // where K ∈ [resumeIndex, leaderIdx] (or even K < resumeIndex, if
        // the FFG's _assistRouteIndex hasn't yet been updated to match
        // the bot's physical position past route[K]). The bot processes
        // the queue in order: walks forward through route[resumeIndex..
        // leaderIdx], then BACKWARDS to the trailing duplicate. Log-95
        // evidence: 26/26 BE pushes had the trailing target match a
        // route waypoint; backward distances 7-14y per push; "Destination
        // reached. dist=N.Ny to leader — refreshing waypoint" fires
        // after the backward walk completes, ~2 seconds later than it
        // should have.
        //
        // The trailing target was DESIGNED for PositionChase mode, where
        // `target = leader.WorldPos` (or a follow offset) is an off-
        // route position the bot needs to reach after walking the route
        // span. In RouteWalk mode the target IS a route waypoint and the
        // trailing is always redundant.
        //
        // Fix: scan the entire route, not just route[leaderIdx]. If the
        // target matches ANY route waypoint (within POP_DIST), omit the
        // trailing — the route span itself already includes (or has
        // already passed through) the target position.
        bool targetIsRouteWaypoint = false;
        for (int i = 0; i < route.Length; i++)
        {
            if (target.WorldDistanceXYTo(route[i]) < Navigation.POP_DIST)
            {
                targetIsRouteWaypoint = true;
                break;
            }
        }
        int routeCount = leaderIdx - resumeIndex + 1;
        int totalLen = targetIsRouteWaypoint ? routeCount : routeCount + 1;
        Vector3 firstWp = route[resumeIndex];

        // ── Fix CR (log-117 evidence) — malformed route span detection ──
        //
        // Symptom (user-reported in log-117):
        //   #1 "assist moving backwards during leader approach"
        //   #2 "assist moving backwards after combat"
        //
        // Concrete log-117 evidence (with directional vectors):
        //
        // Issue #1 — Combat 2 approach window (leader ATG 01:43:35:658 → Combat
        //            01:43:44:504). Bot trajectory:
        //   01:43:35:568  bot=<-715.67, -4184.08>
        //   01:43:35:584  bot=<-715.63, -4183.97>  → POP → newWpTop=<-710.94, -4167.58>
        //   01:43:36:975  bot=<-712.37, -4174.86>
        //   01:43:37:607  bot=<-711.16, -4170.60>
        //   01:43:39:445  bot=<-710.15, -4157.81>  → POP → newWpTop=<-719.05, -4179.69>
        //                 (35:568 → 39:445: ΔY = +26.27y NORTH over 3.9s)
        //   01:43:42:396  bot=<-716.36, -4169.88>
        //                 (39:445 → 42:396: ΔY = -12.07y, ΔX = -6.21y SW reversal over 2.95s)
        //
        // Issue #2 — Post-Combat 2 window (Loot complete 01:43:50:685 → next combat
        //            01:43:59:104). Bot trajectory:
        //   01:43:51:815  bot=<-713.22, -4191.28>
        //   01:43:55:334  bot=<-713.32, -4178.30>  (51:815 → 55:334: ΔY = +12.98y N over 3.5s)
        //   01:43:56:325  bot=<-708.05, -4177.42>
        //   01:43:57:452  bot=<-709.92, -4184.26>  (56:325 → 57:452: ΔY = -6.84y SOUTH reversal over 1.1s)
        //
        // Both reversals had the same triggering mechanism. Tracing the BE pushes:
        //
        //   01:43:34:149  BE push: [route[9..11]=3 + trailing <-719.77, -4183.91>]
        //   01:43:35:569  BE push: [route[10..11]=2 + trailing <-718.75, -4182.68>]
        //   01:43:36:975  BE push: [route[10..11]=2 + trailing <-719.05, -4179.69>]
        //   01:43:42:395  BE push: [route[10..11]=2 + trailing <-717.81, -4176.73>]
        //   (... post-combat 2 ...)
        //   01:43:51:800  BE push: [route[9..11]=3 + trailing <-715.11, -4198.02>]
        //   01:43:52:694  BE push: [route[9..11]=3 + trailing <-718.32, -4199.97>]
        //   01:43:54:469  BE push: [route[10..11]=2 + trailing <-716.18, -4190.99>]
        //   01:43:58:535  BE push: [route[10..11]=2 + trailing <-714.17, -4190.01>]
        //
        // In every case, route[10]=<-710.94, -4167.58> and route[11]=<-710.20, -4154.46>
        // are NORTH of the trailing target (which tracks leader.WorldPos in
        // PositionChase mode, hovering ~<-718, -4180> to <-718, -4200>). The route span
        // walks the bot NORTH through the pre-validated waypoints, then the trailing
        // forces a U-turn SOUTH to the leader. This is the observed reversal.
        //
        // ROOT CAUSE — stale leaderIdx cache from a brief 50ms FRG.Resume window:
        //
        //   At 01:43:20:761 the leader's FRG.Resume executed
        //     leaderNavProvider.SetTargetWaypoint(<-710.19995, -4154.46, 0>)  // = route[11]
        //   The leader's plan changed to ATG at 01:43:20:771 — 10 milliseconds later.
        //   FRG.OnExit's ClearTargetWaypoint fires on the transition, but the assist's
        //   250ms-cadence poller catches the brief HasTargetWaypoint=true window.
        //   TryFindLeaderRouteIndex's primary path matches the published waypoint to
        //   route[11] within 1.0y tolerance and initializes the cache:
        //     [01:43:22:613] [ROUTE-WALK] Cache initialized: leaderIdx=11 at <-710.19995, -4154.46>.
        //   The cached value persists for CachedLeaderRouteIdxMaxAgeSec (30s).
        //
        //   FRG.Resume's RefillWaypoints publishes the NEXT patrol target — not a
        //   waypoint that reflects where the leader currently IS on the route. The
        //   leader was at <-712.94, -4148.64> at FRG.Resume but published route[11]
        //   at <-710.20, -4154.46> (a waypoint 8y ahead in the patrol direction).
        //
        //   The leader then immediately diverted south to engage combats:
        //     Combat 1 fight area, Combat 1 loot, Combat 2 ATG at <-721.48, -4181.44>
        //   leader.WorldPos at the combat 2 ATG anchor publication is 29.25y south of
        //   route[11]. The leader's geographic position is at or near route[8]/route[9]
        //   (the south end of this route segment), not route[11].
        //
        // Why Fix CC can't fix this: CC's geometric cache extension (lines 3716-3727)
        // is strictly forward — `if (dNext < dCur) leaderIdx++; else break;`. For
        // combat 2 with leader at <-721.48, -4181.44> and route[11]=<-710.20, -4154.46>
        // (29.25y away), route[12] is even further north along the route (the route
        // ascends through this segment). dNext > dCur → break → leaderIdx stays at 11.
        // CC handles forward-stale caches (leader has moved past cached index) but
        // not backward-stale caches (leader is geographically before cached index,
        // typically when FRG briefly publishes a forward waypoint then immediately
        // diverts to combat). Combat 4 in this same log shows CC working correctly
        // for forward staleness: 11 → 16 → 19, advancing as leader progressed.
        //
        // Geometric fix: the route span [route[resumeIndex], …, route[leaderIdx],
        // trailing] is only well-formed when the trailing target is geographically
        // AT or PAST route[leaderIdx]. When the trailing target is closer to
        // route[resumeIndex] than to route[leaderIdx], walking the span will take
        // the bot away from the trailing, then it must come back — the observed
        // "backwards" reversal.
        //
        // Verification of CR against this log's evidence (matches all pushes above):
        //
        //   Combat 2 first push (01:43:34:149): trailing=<-719.77, -4183.91>
        //     trailing.dist(route[9]=<-714.97,-4180.76>)  = 5.74y      ← closer to START
        //     trailing.dist(route[11]=<-710.20,-4154.46>) = 30.97y     ← farther from END
        //     5.74 < 30.97 → BLOCK. (Caller uses SetSingleWaypoint to <-719.77, -4183.91>.)
        //
        //   Combat 2 third push (01:43:36:975): trailing=<-719.05, -4179.69>
        //     trailing.dist(route[10]=<-710.94,-4167.58>) = 14.57y
        //     trailing.dist(route[11]=<-710.20,-4154.46>) = 26.74y
        //     14.57 < 26.74 → BLOCK.
        //
        //   Post-combat 2 first push (01:43:51:800): trailing=<-715.11, -4198.02>
        //     trailing.dist(route[9])  = 17.26y
        //     trailing.dist(route[11]) = 43.83y
        //     17.26 < 43.83 → BLOCK.
        //
        //   Combat 4 first push (01:44:08:221): trailing=<-735.64, -4196.81>, range
        //     route[17..19]. CC successfully advanced cache 11→16→19 because leader
        //     was forward-stale (geometrically past route[16]). leader.WorldPos was
        //     closer to route[19] than to route[16]. trailing tracks leader.WorldPos
        //     in PositionChase mode, so trailing is closest to route[19] (end of
        //     span). trailing.dist(route[17]) > trailing.dist(route[19]) → CR does
        //     NOT block, span proceeds normally. Healthy case preserved.
        //
        // The guard `!targetIsRouteWaypoint` ensures CR doesn't fire for pure route
        // spans (target equals a route waypoint, no trailing appended) — those are
        // covered by the existing Fix BK suppression of the trailing. Without
        // trailing there's no malformed-trailing concern by construction.
        //
        // Returning false → SetWaypointLoopGuarded (line 5933) calls
        // navigation.SetSingleWaypoint(target). This OVERWRITES any in-flight
        // navigation queue with a single direct waypoint to the trailing target.
        // The pather computes a route directly to the leader's body. The previously-
        // pushed malformed span (if still being walked) is replaced — important
        // because the duplicate-suppression check below would otherwise let an
        // existing malformed span continue to be walked.
        if (!targetIsRouteWaypoint)
        {
            float trailingToStart = target.WorldDistanceXYTo(route[resumeIndex]);
            float trailingToEnd = target.WorldDistanceXYTo(route[leaderIdx]);
            if (trailingToStart < trailingToEnd)
            {
                outcome = $"malformed-span (trailing target closer to route[{resumeIndex}]={trailingToStart:0.0}y than route[{leaderIdx}]={trailingToEnd:0.0}y — caller should use SetSingleWaypoint)";
                logger.LogInformation(
                    $"[FFG] [FIX-FIRE] CR: malformed route span suppressed — " +
                    $"trailing target {target} is closer to route[{resumeIndex}]={route[resumeIndex]} " +
                    $"({trailingToStart:0.0}y) than to route[{leaderIdx}]={route[leaderIdx]} " +
                    $"({trailingToEnd:0.0}y). Span would walk bot away from target. " +
                    $"Falling back to SetSingleWaypoint.");
                return false;
            }
        }

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
        if (!targetIsRouteWaypoint)
            span[totalLen - 1] = target;

        navigation.SetWayPoints(span);
        RecordRouteSpanPush(firstWp, totalLen);

        bool advancedByProjection = resumeIndex != closestIndex;
        logger.LogInformation(
            $"[FFG] [FIX-FIRE] BE: route-span push — {totalLen} waypoints " +
            $"[route[{resumeIndex}..{leaderIdx}]={routeCount} pre-validated waypoints" +
            $"{(targetIsRouteWaypoint ? ", no trailing target (target is a route waypoint, Fix BK)" : $" + trailing target {target}")}]" +
            $"{(advancedByProjection ? $" — advanced from closestIndex={closestIndex} via projection (bot past closest waypoint)" : "")}.");

        outcome = targetIsRouteWaypoint ? "pushed-pure-route" : "pushed-with-trailing";
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
        _daIntermediateStepCount = 0;
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

        // ── Fix BM-2 (log-96 16:36:20:860 → 16:36:50:754) ─────────────────
        // Parked at route cap is legitimate waiting, not a stall. The bot
        // is correctly idle until the leader advances the published cache,
        // and the 30s CantFollow escalation must NOT accumulate during
        // this window.
        //
        // BH-3 added the parked gate to TickActiveStuckDetection but
        // missed this companion timer. As a result, even with BM-1 fixing
        // _routeWalkParked's persistence, this 30s wall would still fire
        // and escalate the bot into CantFollow (where Fix BD + BG-2 then
        // produced a stable Projection10 ping-pong, observed as 5 of 8
        // log-96 reversals across the last 40 seconds).
        //
        // Reset the timestamp AND the progress anchor so that when the
        // park ends (leader advances), the timer restarts from the unpark
        // moment with a fresh position baseline. Without the anchor reset,
        // partial elapsed time could carry over and cause a premature fire
        // on the first post-unpark stall.
        if (_routeWalkParked)
        {
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

        // ── Fix BH-3 (log-94 01:25:48:280 → 01:26:18:001) ─────────────────
        // Skip stuck detection while the assist is parked at its
        // trailing-by-one route cap. The bot is correctly idle (waiting
        // for the leader to advance the published-waypoint cache), not
        // stuck. Flagging it Stuck causes the leader's pause to convert
        // from "distance" to "stuck" gating, which is far more sticky
        // (Status==Stuck holds even when dist drops; it only clears when
        // the assist's _activeStuckAnchorW displacement >= 3y) and
        // ultimately routes the assist into the 30-second CantFollow
        // escalation that produced log-94's 1630-Projection10 loop.
        //
        // Reset the trigger window too, so when the park ends the
        // detector starts fresh from the bot's new position rather
        // than firing immediately on the tick AFTER unpark.
        if (_routeWalkParked)
        {
            _activeStuckSinceUtc = DateTime.MinValue;
            // Don't clear _activeStuckReported here — if Stuck was ALREADY
            // reported (the legitimate-stuck → park transition), the
            // resume path at line ~5443 still owns the clear decision.
            // Clearing here would silently un-Stuck without any movement
            // proof, which is exactly the asymmetric-anchor bug Fix
            // (log 01:50:54-01:51:09) was added to prevent.
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

        // ── Fix DE (log-124 19:27:34-37 corridor-exit false stuck) ──
        // Don't TRIGGER a new stuck report while the assist is within follow
        // distance of the leader. Post-combat, FFG.OnEnter starts a 3s idle-
        // guard (Fix CJ's ffgIdleGuardActive) that suppresses Idle so the
        // assist stays ready for the leader's next sprint-to-mob. When the
        // leader is instead stationary (looting / fighting in place), the
        // assist holds co-located (~6y < FollowingMaxYards) and doesn't move —
        // which is correct (it's AT its follow position), but this detector
        // reads "moved <1y in 2.5s" as stuck and reports it, flipping the
        // leader's pause from distance- to stuck-gating (sticky; escalates to
        // CantFollow). Same rationale as the BH-3 parked guard above:
        // correctly stationary near the leader, not stuck.
        //
        // Evidence log-124: OnEnter 19:27:34:011 (mob died) → assist held at
        // <-492,-4450>, ~6.2y from leader → false Stuck 19:27:36:645 (paused
        // the leader) → guard expired 19:27:37:011 → assist idled "Reached
        // follow position (dist=6.2y)". It was within follow distance the
        // whole time; never actually stuck.
        //
        // Within follow distance the leader needn't pause for the assist (it
        // is already close enough), so suppressing the report here is safe and
        // preserves CJ's "stay active, don't formally Idle" intent without the
        // false pause. Only the TRIGGER path is gated; the _activeStuckReported
        // resume path above is untouched, and once the leader moves away
        // (dist ≥ FollowingMaxYards) the detector re-engages for genuine
        // stalls. The window is reset so it restarts fresh from here.
        LeaderState? stuckLeader = leaderConnection.LastLeaderState;
        if (stuckLeader != null &&
            currentPos.WorldDistanceXYTo(stuckLeader.WorldPos) < FollowingMaxYards)
        {
            _activeStuckSinceUtc = DateTime.MinValue;
            _activeStuckCheckPosW = currentPos;
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
            // ── Fix CK (log-113 21:53:24:680 evidence) ──
            // Extend Fix CJ's Idle suppression to this Navigation event handler.
            //
            // Evidence: post-combat #2 at 21:53:22:707 FFG.OnEnter, assist 0.6y
            // from leader (co-located). At 21:53:23:956 leader started approaching
            // mob, mode → Anchor (target 5.1y west). At 21:53:24:679 CJ correctly
            // suppressed the FFG.Update Idle entry path (line 2247) at dist=4.0y
            // with ffgIdleGuardActive=True (1972ms < 3000ms). Mode then transitioned
            // Anchor → PositionChase (anchor co-located 2.5y < 3.6y POP_DIST).
            // The new PositionChase target was 2.85y from bot — within POP_DIST.
            // Navigation popped it immediately, fired OnDestinationReached, and
            // THIS BRANCH entered Idle (dist=4.0y < 7y) — bypassing CJ entirely
            // because CJ only guards line 2247 in UpdateNavigatingToLeader.
            //
            // Consequence: bot sat Idle for 1.6s (21:53:24:680 → 21:53:26:270)
            // while leader sprinted NW to mob. Bot exited Idle 15y away. BE
            // pushed a 5-waypoint route span (route[7..11]) heading N along the
            // route while leader was NW off-route — bot walked N for 4s, closing
            // only 9y to leader instead of 25y of direct N→NW travel. Combat
            // started before bot caught up. User-visible: "turning and running
            // away after combat" (the brief Anchor-direction turn + Idle pause +
            // route N walk visible as direction changes).
            //
            // Fix: apply the same suppressIdle = (HasApproachStart || ffgIdleGuardActive)
            // gate Fix CJ uses. When suppressed, refresh the waypoint to leader's
            // body so the bot continues tracking via PositionChase. The bot
            // remains close to the leader during the approach phase; when the
            // leader engages combat, the bot is right there.
            bool ckGuardActive =
                (DateTime.UtcNow - _ffgEnterTimeUtc).TotalMilliseconds < IdleGuardAfterEnterMs;
            bool ckSuppressIdle = currentLeader.HasApproachStart || ckGuardActive;
            if (ckSuppressIdle)
            {
                logger.LogInformation(
                    $"[FFG] [FIX-FIRE] CK: OnDestinationReached Idle suppressed at " +
                    $"dist={dist:0.0}y (threshold=FollowingMaxYards={FollowingMaxYards:0.0}y) — " +
                    $"HasApproachStart={currentLeader.HasApproachStart}, " +
                    $"ffgIdleGuardActive={ckGuardActive} " +
                    $"({(DateTime.UtcNow - _ffgEnterTimeUtc).TotalMilliseconds:0}ms since OnEnter " +
                    $"vs {IdleGuardAfterEnterMs:0}ms guard). Refreshing waypoint to leader's " +
                    $"body to keep navigation active during approach phase.");
                Vector3 ckRefreshTarget = GetNavigationTarget(currentLeader);
                _lastNavigatedToLeaderWorldPos = ckRefreshTarget;
                SetWaypointLoopGuarded(ckRefreshTarget);
                return;
            }

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
            //
            // Fix CK extension: dead-band co-located also bypasses CJ. Apply the
            // same suppressIdle gate. When suppressed, force-refresh by pushing
            // the target one more time (refreshTarget is already known
            // co-located, but waypoint may yet pop in different way once the
            // leader's position has actually moved). Better to spin briefly here
            // than to enter Idle and miss the approach.
            bool ck2GuardActive =
                (DateTime.UtcNow - _ffgEnterTimeUtc).TotalMilliseconds < IdleGuardAfterEnterMs;
            bool ck2SuppressIdle = currentLeader.HasApproachStart || ck2GuardActive;
            if (ck2SuppressIdle)
            {
                logger.LogInformation(
                    $"[FFG] [FIX-FIRE] CK: OnDestinationReached dead-band-co-located Idle " +
                    $"suppressed (leader={dist:0.0}y, refreshDist={refreshDist:0.0}y) — " +
                    $"HasApproachStart={currentLeader.HasApproachStart}, " +
                    $"ffgIdleGuardActive={ck2GuardActive}. Re-pushing refresh target to keep " +
                    $"navigation engaged; if the target is genuinely unreachable the leader " +
                    $"will move next tick and free us.");
                _lastNavigatedToLeaderWorldPos = refreshTarget;
                SetWaypointLoopGuarded(refreshTarget);
                return;
            }

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

                // Fix BH-3 → BM-1: defense-in-depth set. The primary
                // mechanism is now BM-1's IsParkedAtRouteCap() recompute
                // at the top of UpdateNavigatingToLeader, which tracks
                // the parked CONDITION persistently. This set covers the
                // within-this-tick window between the event firing and
                // the next UpdateNavigatingToLeader entry.
                _routeWalkParked = true;
                return;
            }

            // ── Fix CA (log-104 08:41:06 "stuck in open terrain" evidence) ──
            //
            // Identical failure mode applies to Anchor mode: when the bot
            // arrives at the approach-start anchor and mode is still Anchor
            // (either inside the active approach phase, or — more commonly —
            // via BU hysteresis after HasApproachStart went false), the
            // retry-target returned by GetNavigationTarget is the anchor
            // again, which is within POP_DIST of the bot (we just arrived
            // there). The legacy fallback below reverts to body-chase via
            // ComputeFollowTargetWorldPos, which can ALSO produce a target
            // within POP_DIST when the leader is near the anchor (Fix Z's
            // FarTargetMaxYards=7y or BL projection-safety adjustments can
            // place the body-chase target within ~2.5y of the bot).
            //
            // Result: SetWaypoint cycles between anchor and body-chase
            // ~every 15ms; after 10 sets in 100ms the SetWaypoint loop
            // guard escalates to CantFollow → AB-2 projection escapes —
            // walking the bot between nearby points "in open terrain"
            // (the user's observation in log-104).
            //
            // Log-104 trace at 08:41:05:998 → 08:41:06:091:
            //   - 08:41:03:258  Mode → Anchor (target=<-731.75, -4154.32>).
            //   - 08:41:04:895  Leader transitioned PTG (HasApproachStart=false).
            //   - 08:41:05:873  Assist arrived at anchor via Fix AN local-TTL.
            //   - 08:41:05:998  OnDestinationReached: dist=14.2y, retryDist=2.6y.
            //                   Fell through past RouteWalk guard → body-chase.
            //   - 08:41:06:013 → 06:091 — 10 SetWaypoint cycles in ~92ms.
            //   - 08:41:06:091  Loop guard fires → CantFollow.
            //   - 08:41:06:108 → 06:338 → 08:602 → ... — AB-2 projection
            //     escapes carrying the bot in zig-zag pattern between
            //     nearby points (the user's reported "between same points
            //     over and over").
            //
            // The fix mirrors the RouteWalk branch above: when mode=Anchor
            // and target is co-located, sit-in-place. The anchor is the
            // correct geographic position (the leader's pre-engage spot);
            // the bot SHOULD be there. Refreshing is what creates the
            // loop. When the approach cycle resolves (HasApproachStart
            // comes back true for a new ATG, or BU sticky expires and
            // mode falls through to RouteWalk/PositionChase), the next
            // UpdateNavigatingToLeader tick will compute a fresh target
            // with non-zero drift and re-engage navigation naturally.
            if (_currentNavTargetMode == NavTargetMode.Anchor)
            {
                logger.LogInformation(
                    $"[FFG] [ANCHOR-PARKED] Parked at approach-start anchor " +
                    $"(retryDist={retryDist:0.0}y, leader {dist:0.0}y away). " +
                    $"Leader in approach phase (or recently exited and BU hysteresis active); " +
                    $"waiting at anchor for cycle to resolve. " +
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
            //
            // Fix BL (log-95 11:48:19:813 → 11:48:25:646, 5.85s with the
            // bot drifting 37.7y east at full walking speed while
            // NavState=Idle; log-95 11:51:08:320 → 11:51:15:910, 7.59s
            // with 21.5y drift): navigation.Stop() suspends the
            // nav-layer's pathing and steering logic but does NOT
            // release the forward movement key. The Fix K-1 comment at
            // line ~1398 documents the exact same gotcha for
            // FFG.OnExit, and the inline comment at FRG.cs:997-1002
            // documents it for FRG. Without input.StopForward(true)
            // here, the bot enters NavState.Idle with the forward key
            // still pressed and continues to walk in its last facing
            // direction until something else releases the key (the next
            // FFG action that calls input.StopForward, OR another nav
            // tick that issues new steering commands).
            //
            // The downstream consequence is severe: when FFG re-engages
            // (e.g., leader resumes patrol or starts approaching a
            // mob), the bot has drifted tens of yards past the route
            // waypoint where the pop fired. FFG uses _assistRouteIndex
            // = N (the index that was just popped) as the navigation
            // target = route[N]. SetSingleWaypoint(route[N]) is BEHIND
            // the bot's drifted position. The bot turns 180° and walks
            // back to route[N]. This is the user's "during approach
            // the assist actually turning around going many yards in
            // the opposite direction" complaint.
            //
            // Symptom matches Fix K-1's log-57 evidence exactly: zero
            // movement-key log lines on the assist during the drift
            // window, yet the bot moved tens of yards forward — the
            // Forward key was held continuously the whole time.
            //
            // ── Fix CK (log-113 21:53:24:680 evidence — same family) ──
            // Apply the same suppressIdle gate as CJ guards line 2247 with.
            // OnWayPointReached fires from Navigation when an intermediate
            // route waypoint is popped; if dist<FollowingMaxYards we'd Idle
            // here. During post-combat approach, the next ATG anchor is often
            // within FollowingMaxYards of the post-loot rendezvous position,
            // so the bot pops the anchor immediately and would Idle — exactly
            // what we saw at 21:53:24:680 via the parallel OnDestinationReached
            // path. Same fix applied here for symmetry.
            bool ck3GuardActive =
                (DateTime.UtcNow - _ffgEnterTimeUtc).TotalMilliseconds < IdleGuardAfterEnterMs;
            bool ck3SuppressIdle = leader.HasApproachStart || ck3GuardActive;
            if (ck3SuppressIdle)
            {
                logger.LogInformation(
                    $"[FFG] [FIX-FIRE] CK: OnWayPointReached Idle suppressed at " +
                    $"dist={dist:0.0}y (threshold=FollowingMaxYards={FollowingMaxYards:0.0}y) — " +
                    $"HasApproachStart={leader.HasApproachStart}, " +
                    $"ffgIdleGuardActive={ck3GuardActive} " +
                    $"({(DateTime.UtcNow - _ffgEnterTimeUtc).TotalMilliseconds:0}ms since OnEnter " +
                    $"vs {IdleGuardAfterEnterMs:0}ms guard). Refreshing waypoint to leader's " +
                    $"body to keep navigation active during approach phase.");
                Vector3 ck3RefreshTarget = GetNavigationTarget(leader);
                _lastNavigatedToLeaderWorldPos = ck3RefreshTarget;
                SetWaypointLoopGuarded(ck3RefreshTarget);
                return;
            }

            navigation.Stop();
            if (input.IsKeyDown(input.ForwardKey))
                input.StopForward(true);
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

            // Fix BL: see lengthy comment on the parallel
            // dist<FollowingMaxYards branch above. Same gotcha applies
            // here — navigation.Stop() does not release the forward
            // key, and without input.StopForward(true) the bot would
            // drift in its last facing direction until something else
            // releases the key. Log-95 had this path fire during the
            // grindloop's typical dead-band-co-located outcome (target
            // = route[_assistRouteIndex] is the just-popped waypoint,
            // refreshDist ≈ 0.7y < POP_DIST). Bot drifted east 37.7y
            // in 5.85s in the worst case (11:48:19:813 → 11:48:25:646)
            // before FFG re-engaged.
            //
            // ── Fix CK (log-113 21:53:24:680 evidence — same family) ──
            // Even after _rendezvousConfirmed=true above, idling during the
            // approach phase is wrong: the leader is about to (or has just)
            // started ATG and the bot needs to track the body. Apply CJ's
            // suppressIdle gate. Note: when suppressed we DO NOT roll back
            // _rendezvousConfirmed — once the geometric "as-close-as-possible"
            // determination has been made it remains valid even while we keep
            // the nav system warm. The next refresh on a moved leader will
            // push a real waypoint; until then the navigation is idempotent.
            bool ck4GuardActive =
                (DateTime.UtcNow - _ffgEnterTimeUtc).TotalMilliseconds < IdleGuardAfterEnterMs;
            bool ck4SuppressIdle = leader.HasApproachStart || ck4GuardActive;
            if (ck4SuppressIdle)
            {
                logger.LogInformation(
                    $"[FFG] [FIX-FIRE] CK: OnWayPointReached dead-band-co-located Idle " +
                    $"suppressed (leader={dist:0.0}y, refreshDist={refreshDist:0.0}y) — " +
                    $"HasApproachStart={leader.HasApproachStart}, " +
                    $"ffgIdleGuardActive={ck4GuardActive}. _rendezvousConfirmed left true; " +
                    $"navigation kept active so the next leader-position-poll change can " +
                    $"refresh the waypoint without going through Idle.");
                return;
            }

            navigation.Stop();
            if (input.IsKeyDown(input.ForwardKey))
                input.StopForward(true);
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

        // ── Fix DA (log-123 16:49:58:082 corridor between hill and tree) ──
        //
        // ROOT CAUSE (corrected): the corridor navmesh is defective for
        // PATHER queries. Any pathfind through it returns a ~140-node U-turn
        // detour — confirmed for BOTH bots: assist reqId=95/96 pathLen=140
        // (<-515.68,-4445.42> → wp[47]=<-499.19,-4448.87>, 16.85y) AND leader
        // reqId=7 pathLen=142 (<-493.04,-4449.85> → <-515.57,-4445.47>,
        // 22.95y). The straight line is physically walkable — the LEADER
        // walked it: at the SAME spot <-515.53,-4445.15> (0.34y from where the
        // assist later stuck) the leader logged builtDirectRoute wpCount=99
        // usePather=false and walked straight to wp[47].
        //
        // Why the leader sails through and the assist didn't: Navigation only
        // calls the (broken) pather when usePather=true, where (Navigation.cs:
        // 3651) `usePather = distance > MaxDistance(200) || distance >
        // AvgDistance*2`, and `AvgDistance = wpCount>1 ? max(mapDistanceXY/
        // wpCount, OutDoorMin=3) : OutDoorMin=3`.
        //   • Leader loads its whole remaining route → wpCount=99 →
        //     AvgDistance≈10y → 16.75 > 20? NO → usePather=FALSE → direct walk.
        //   • Assist trails the leader by one route index, so its route span
        //     is just [route[47..48]] = 2 waypoints → AvgDistance≈7y →
        //     16.85 > 14? YES → usePather=TRUE → pather → defective U-turn →
        //     AC+AE+AI+AL rejects → rewind/retry (same spot, same target,
        //     identical re-reject) → CantFollow → 4 escape hops over ~4s
        //     (the user's "back and forth / stuck").
        //
        // FIX: on the rejection, step DAStepYards (5y ≤ AvgDistance*2=6y for a
        // single pushed waypoint) toward the rejected target. A ≤6y single
        // waypoint keeps usePather=FALSE → builtDirectRoute → a straight-line
        // walk that NEVER touches the broken pather — reproducing the leader's
        // mechanism. One step also drops the remaining route leg under the
        // route-span usePather threshold (~14y), so normal RouteWalk resumes
        // directly on the next tick. Capped at DAMaxIntermediateSteps; on
        // exhaustion fall through to the existing rewind/CantFollow logic
        // (which still sees _lastPathFailureWasRejection=true, preserving
        // Fix BB's toward-leader escape direction).
        if (_lastPathFailureWasRejection &&
            _daIntermediateStepCount < DAMaxIntermediateSteps)
        {
            float dx = endW.X - startW.X;
            float dy = endW.Y - startW.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist > DAStepYards)
            {
                float inv = DAStepYards / dist;
                Vector3 step = new Vector3(
                    startW.X + dx * inv,
                    startW.Y + dy * inv,
                    startW.Z);

                bool stepBlocked =
                    navigation.AreaBlacklist != null &&
                    navigation.AreaBlacklist.ContainsWorld(step);

                if (!stepBlocked)
                {
                    _daIntermediateStepCount++;
                    logger.LogInformation(
                        $"[FFG] [FIX-FIRE] DA: pather-defective corridor — far target " +
                        $"{endW} ({dist:0.0}y) U-turned. Stepping {DAStepYards:0}y along the " +
                        $"heading to {step} as a single waypoint (≤6y ⇒ usePather=false ⇒ " +
                        $"builtDirectRoute straight-line walk, bypassing the broken pather — " +
                        $"the same mechanism the leader uses through this corridor). " +
                        $"Step {_daIntermediateStepCount}/{DAMaxIntermediateSteps}; one step " +
                        $"drops the remaining leg under the route-span usePather threshold so " +
                        $"normal RouteWalk resumes.");
                    navigation.SetSingleWaypoint(step);
                    return;
                }
            }
        }

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

            // Fix AL: path rejection requires BOTH high angle AND high
            // simplified route count (≥ 8). Angle alone (Fix AI's 150°)
            // doesn't distinguish dead-zone wraparounds from legitimate
            // short detours:
            //
            //   Case        | angle  | simplified | Verdict
            //   log-69 real | 151°   | 17         | DEAD ZONE — reject
            //   log-76 false| 166.8° | 3          | DETOUR    — accept
            //
            // Simplified count (measured after Fix AA pruning + PathSimplify)
            // is a direct kink-count measure, robust to per-segment node
            // spacing changes. 8 leaves ~5 node margin between known detours
            // (3) and known wraparounds (17). High-angle-only paths are
            // accepted with a warning log for future-evidence monitoring.
            //
            // Recovery on missed rejection: if the accepted path lands the
            // bot in trouble, Navigation's stuck detection and OnPathFailed
            // escalation still fire normally.
            // See HANDOFF Fix AL for full evidence.
            const int COMPLEX_WRAPAROUND_MIN_ROUTECOUNT = 8;

            if (toTopDistSq > REAR_TOP_REJECT_MIN_YARDS * REAR_TOP_REJECT_MIN_YARDS)
            {
                float dot = snap.ForwardX * toTopX + snap.ForwardY * toTopY;
                bool angleExceedsThreshold =
                    dot < 0f &&
                    dot * dot > REAR_REJECT_COS_THRESHOLD_SQ * snap.ForwardLenSq * toTopDistSq;
                bool isComplexWraparound =
                    snap.SimplifiedRouteCount >= COMPLEX_WRAPAROUND_MIN_ROUTECOUNT;

                // ── Fix DG (log-125 20:11:51 corridor body-chase U-turn at ~140°) ──
                // A genuine dead-zone wraparound can present with a simplified
                // routeTop just UNDER the 150° angle gate (Fix AI) yet still be
                // unmistakable: log-125 reqId=17/18 returned 144 RAW nodes for a
                // 7-9.6y straight-line target (16 simplified, routeTop ~140°
                // rear). angleExceedsThreshold was false (140°<150°), so the
                // path was accepted and the assist walked the U-turn west (the
                // "turning back"). DC doesn't help (single-waypoint body-chase,
                // not a route span) and DA never fired (no rejection).
                //
                // The raw pathLength is the cleanest dead-zone signature: clean
                // /detour paths run ≤~2.4 nodes/yard (log-125 reqId 16=0.8,
                // 19=1.0, 21=2.4; log-74 tree-detour=1.9), while corridor
                // wraparounds run 15-20 nodes/yard (reqId 17=15.0, 18=20.6).
                // Reject when the path leans rear (dot<0), is complex
                // (count≥8), AND its raw node count is grossly disproportionate
                // to the straight-line distance. The rear + count guards ensure
                // a legitimate long FORWARD path is never rejected; the ratio
                // (6 nodes/yard) sits well above any observed detour and well
                // below any observed wraparound. On reject this routes into the
                // same OnPathFailed → DA 5y direct-step recovery as the angle
                // path, walking the assist straight through the corridor.
                const float DeadZonePathNodesPerYard = 6.0f;
                float straightDist = MathF.Sqrt(snap.ForwardLenSq);
                bool rearLeaning = dot < 0f;
                bool grosslyLongPath =
                    straightDist > 0.5f &&
                    snap.PathLength > DeadZonePathNodesPerYard * straightDist;

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
                else if (rearLeaning && isComplexWraparound && grosslyLongPath)
                {
                    float topDist = MathF.Sqrt(toTopDistSq);
                    float fwdLen = MathF.Sqrt(snap.ForwardLenSq);
                    float cosA = dot / (fwdLen * topDist);
                    if (cosA < -1f) cosA = -1f;
                    else if (cosA > 1f) cosA = 1f;
                    float angleDeg = MathF.Acos(cosA) * (180f / MathF.PI);

                    logger.LogError(
                        $"[FFG] [FIX-FIRE] DG: rejecting dead-zone WRAPAROUND by raw path length " +
                        $"(rear-leaning angle={angleDeg:0.0}° > 90° AND simplified count " +
                        $"{snap.SimplifiedRouteCount} ≥ {COMPLEX_WRAPAROUND_MIN_ROUTECOUNT} AND " +
                        $"pathLen={snap.PathLength} > {DeadZonePathNodesPerYard:0}×{straightDist:0.0}y " +
                        $"straight-line = {snap.PathLength / straightDist:0.0} nodes/yard). " +
                        $"Simplified routeTop {snap.SimplifiedRouteTop} is {topDist:0.00}y from start. " +
                        $"This is a corridor dead-zone wraparound whose routeTop sits under Fix AI's " +
                        $"150° gate (~140°) but whose grossly-long node count is unambiguous. " +
                        $"start={snap.StartW} end={snap.EndW}. " +
                        $"Signalling Reject — Navigation will clear route and fire OnPathFailed " +
                        $"(→ Fix DA 5y direct-step recovery).");
                    snap.Reject = true;
                    snap.RejectCooldownMs = 500;
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
        //
        // ── Fix CD-1 (log-107 11:54:14 evidence) ──
        // EXCEPTION: while in CantFollow state, the bot is navigating to
        // *projection targets* (CantFollow escape points), not to the
        // ORIGINAL failure destination (leader/anchor). A successful path
        // to a projection target does NOT indicate the underlying U-turn
        // problem has resolved. Resetting the flag here would let
        // subsequent away-from-leader projections (BB-flip candidates)
        // miss the override, sending the bot *further* from the leader
        // through the wedge that triggered the original rejection.
        //
        // Log-107 trace:
        //   11:54:14.327  AC+AE+AI+AL rejects → flag = TRUE
        //   11:54:14.345  P1 (BD, toward-route 5y) — path accepted →
        //                 flag = FALSE   ← spurious reset!
        //   11:54:17.345  P2 (toward-route 20y, +120° offset)
        //   11:54:20.356  P3 (toward-route 10y, route[22] N of bot)
        //   11:54:22.393  P4 — basis=away-from-leader → BB skipped because
        //                 flag was reset on P1. Bot moved 6.7y NW.
        //   11:54:23.444  P5 — same, bot moved 6.6y NW.
        //   11:54:23.951  AO cumulative cap (20y) exits CantFollow.
        //   Net effect: 20y of movement *AWAY* from leader — the user-
        //   reported "ran opposite direction" symptom.
        //
        // With CD-1, the flag persists through CantFollow. When P4 fires
        // the away-from-leader basis, BB sees flag=true and (with CD-2's
        // threshold extension) flips direction toward leader.
        //
        // Reset normally on path acceptance whenever NOT in CantFollow —
        // primary navigation toward leader succeeded, so the U-turn
        // condition has cleared.
        if (_navState != NavState.CantFollow)
        {
            _lastPathFailureWasRejection = false;
        }
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

        // Fix AU: when _activeStuckReported=true (TickActiveStuckDetection
        // confirmed < 1y movement in 2.5s = real stationary) AND Fix AB-1
        // has observed ONE stale-empty matching the active request's
        // (start, end) within 1y (pather-side evidence the current
        // position can't route to current target), escalate to CantFollow
        // immediately — skip the 3-count gate and the 30s
        // TickNavActiveTimeout wall.
        //
        // Both signals are independently strong. Together they prove the
        // bot won't recover by waiting (movement: confirmed absent;
        // pather: failing for unchanged input). The 3-count gate is
        // itself a stationarity proxy — with _activeStuckReported
        // proving stationarity directly, count=1 carries the same
        // evidentiary weight as count=3.
        //
        // log-81 mob 2: pre-AU, bot sat at <-755.43,-4272.97> for 27.3s
        // after Stuck flag (PPather NRE-loop on bot position, 3s re-
        // enqueue cycle producing same un-routable request). AU
        // collapses to ~7.5s. AB-1's #1/3 hit at 16:00:27:324 escalates;
        // pre-AU waited for either #3/3 OR 30s TickNavActiveTimeout
        // (whichever was the same instant).
        //
        // Counter reset on fire matches the 3-count branch. Counter is
        // additive — wedge-case existing 3-count path unchanged for
        // bot-moving scenarios where Stuck flag isn't set.
        // See HANDOFF Fix AU for full evidence.
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
