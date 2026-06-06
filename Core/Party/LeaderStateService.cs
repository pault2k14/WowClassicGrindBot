using System;
using System.Numerics;

using Microsoft.Extensions.Logging;

namespace Core.Party;

/// <summary>
/// Leader-side singleton. Builds a <see cref="LeaderState"/> snapshot on demand
/// from the live <see cref="PlayerReader"/> and <see cref="AddonBits"/> values.
/// <para>
/// No background update loop is required: <see cref="PlayerReader"/> is always
/// current because <see cref="AddonReader.Update"/> runs in the addon thread at
/// ~250 Hz. The controller calls <see cref="GetCurrentState"/> on each HTTP GET,
/// so the snapshot is always at most one addon-frame old.
/// </para>
/// </summary>
public sealed class LeaderStateService
{
    private readonly ILogger<LeaderStateService> logger;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly IBotController botController;
    private readonly LeaderNavigationProvider leaderNavProvider;

    // Cache the last valid world position. When the addon briefly returns (0,0,0)
    // (e.g. during loading screens or bot deactivation), we return the cached position
    // rather than sending the assist a bogus ~4400y distance (sqrt(700²+4350²) ≈ 4406y).
    private Vector3 _lastValidWorldPos;

    // ── Fix CZ (log-122 evidence: 15:55:37, 15:59:07, 15:59:24, 16:00:35) ──
    //
    // The published HasApproachStart is smoothed by Fix CV (TTL grace bridges
    // brief ATG.OnExit → ATG.OnEnter gaps), but the X/Y coords are pulled raw
    // from leaderNavProvider. Empirically, leaderNavProvider's coords go to
    // (0,0) any time ATG.OnExit calls ClearApproachStart — i.e. ALL the
    // transients CV was designed to smooth.
    //
    // Worse: Fix BT (ATG re-entry within 10s grace) decided NOT to re-publish
    // the anchor on re-entry. The BT log "reusing stable anchor <x,y>" reports
    // BT's INTERNAL cache; the leaderNavProvider remains zero from the prior
    // OnExit. Verified all 21 BT re-entries in log-122: zero publish events
    // followed them. So during CV's TTL window, the assist can see:
    //
    //   HasApproachStart=true (smoothed by CV)
    //   ApproachStartWorldX/Y=0/0 (zeroed by ATG.OnExit, not re-published)
    //
    // The assist's FFG then enters Anchor mode with target=<0,0,0>, walks
    // briefly toward the origin (4366y away), and snaps back when the leader
    // eventually re-publishes. The traced motion is a narrow oval/ellipse —
    // exactly the user's complaint #2 in log-122.
    //
    // Observed instances in log-122 (4 of 4 are this pattern):
    //   15:55:37:014  PositionChase → Anchor (target=<0,0,0>, drift=0.0y)
    //   15:59:07:477  PositionChase → Anchor (target=<0,0,0>, drift=0.0y)
    //   15:59:24:674  PositionChase → Anchor (target=<0,0,0>, drift=4493.2y)
    //   16:00:35:917  PositionChase → Anchor (target=<0,0,0>, drift=4366.7y)
    //
    // At 16:00:35:917, leader log shows the BT re-entry 508ms earlier
    // (16:00:35:409) "reusing stable anchor <-411.94775, -4348.463>" — exactly
    // the anchor the assist SHOULD have seen but didn't, because BT chose not
    // to re-publish.
    //
    // CZ fix: cache the last non-zero ApproachStartWorld value in this service.
    // When raw is (0,0) but publishedHasApproachStart is true (i.e. CV is
    // smoothing), serve the cache. This makes the published HasApproachStart
    // and the published X/Y mutually consistent — what CV's design comment
    // already claimed ("the right answer for the bridged transients") but
    // that the actual implementation didn't deliver.
    private float _lastValidApproachStartX;
    private float _lastValidApproachStartY;

    // One-shot log throttle for Fix CZ — flips false on next raw-valid sample.
    private bool _czCacheServeLogged;

    // ── Fix CV (log-119 evidence: 03:55:46-52 issue #2, 04:01:30-04:02:08 issue #1) ──
    //
    // Background: HasApproachStart's raw bit lifecycle is bound entirely to the
    // leader's ApproachTargetGoal — ATG.OnEnter SETs (or Fix BT re-publishes on
    // re-entry), ATG.OnExit CLEARs unconditionally. PTG and Combat do not touch
    // the bit (verified from PullTargetGoal.cs and CombatGoal.cs source). This
    // means the published bit goes false during ANY ATG.OnExit, including:
    //
    //   • ATG → PTG transitions (PTG is part of the same engagement; PTG cycles
    //     up to 3.298s observed in log-102)
    //   • ATG → Combat transitions (Focus_Combat propagation gap on assist
    //     ~100-500ms before PartyInCombat() takes over)
    //   • ATG → NO PLAN cycles (planner failed momentarily; observed re-entry
    //     within 295-531ms when loop-back is the eventual outcome)
    //   • Brief ATG → FRG → ATG flicker (observed 328ms in log-119)
    //
    // Each false-then-true cycle propagates to the assist via polling and flips
    // partyEngaging (GoapAgent's ATG.AssistFocus precondition) and FFG's anchor-
    // mode selection. In log-119's 38-second stuck window, this caused 11
    // assist ATG↔FFG plan flips and 4-point oscillation between cluster of
    // <-378,-4188>, <-378,-4183>, <-374,-4184>, <-371,-4187> (user's
    // "running back and forth" complaint).
    //
    // Fix CV semantics: the API-published HasApproachStart reflects whether the
    // leader is "in/near an approach phase" rather than "currently in ATG
    // specifically." Computed as the disjunction of three signals:
    //
    //   1. Raw bit (leaderNavProvider.HasApproachStart) — leader's ATG is active
    //   2. Engaging goal (currentGoalName ∈ {ATG, PTG, Combat}) — leader is
    //      in a goal that conceptually IS the approach/engagement phase, even
    //      though the raw bit was cleared by ATG.OnExit on the way in
    //   3. Within TTL — recently saw signals 1 or 2; bridges brief gaps
    //
    // Why two TTL values:
    //   • TransientTtlMs (2000ms): bridges NO PLAN cycles. Log-119 measured
    //     loop-back NO PLAN durations up to 531ms; longer NO PLANs (1.8s-17s)
    //     resolved to FRG (real abandonment). 2000ms covers loop-back with
    //     1.5s headroom while still expiring during long abandonment NO PLANs.
    //   • DefinitiveAbandonmentTtlMs (500ms): used when the leader is in an
    //     explicit non-engaging goal (FRG, Loot, Rest, etc.). Bridges brief
    //     FRG flickers between ATG cycles (328ms observed worst case) but
    //     exits quickly on genuine "leader gave up and returned to patrol"
    //     so the assist can transition to following within ~500ms.
    //
    // Goal name match: trim and compare against the same canonical forms
    // DetermineStatus() uses below (e.g., "Approach Target" not the class
    // name "ApproachTargetGoal" — see Fix CN/CO/CP notes there).
    //
    // Consumers that benefit (verified by audit of every HasApproachStart read):
    //   • GoapAgent.UpdateWorldState — partyEngaging stays true through PTG/
    //     Combat handoff/NO PLAN cycles → no ATG↔FFG plan flicker
    //   • FollowFocusGoal.GetNavigationTarget — Anchor mode stays selected
    //     through transients → no PositionChase turn-aside
    //   • FollowFocusGoal Idle suppression (Fix CJ/CK variants) — assist
    //     stays alert when close to leader, ready to react
    //   • FollowFocusGoal.AH+AJ+AN — focus-chain acquisition continues
    //     attempting (idempotent at WoW key level)
    //   • FollowFocusGoal leaderStartedApproaching edge — fires once per real
    //     approach event instead of repeatedly during ATG-flicker cycles
    //     (improvement, not just compatibility)
    //
    // Side effect: FollowFocusGoal_BM.cs:4206-4220 (Fix BU's anchor-mode
    // stickiness) gates on `!leader.HasApproachStart`. With CV, the published
    // value stays true during the very window BU was designed to handle, so
    // BU's condition never fires. BU becomes dead code but is left intact for
    // now — separate cleanup decision.
    private const double TransientTtlMs = 2000.0;
    private const double DefinitiveAbandonmentTtlMs = 500.0;
    private DateTime _lastEngagingActivityUtc = DateTime.MinValue;
    private bool _ttlGraceLogged;
    private readonly Core.Goals.Navigation navigation;

    public LeaderStateService(
        ILogger<LeaderStateService> logger,
        PlayerReader playerReader,
        AddonBits bits,
        IBotController botController,
        LeaderNavigationProvider leaderNavProvider,
        Core.Goals.Navigation navigation)
    {
        this.logger = logger;
        this.playerReader = playerReader;
        this.bits = bits;
        this.botController = botController;
        this.leaderNavProvider = leaderNavProvider;
        this.navigation = navigation;
    }

    public LeaderState GetCurrentState()
    {
        Vector3 world = playerReader.WorldPos;

        // Validate — (0,0,0) means the addon has not yet provided real data
        // or has momentarily lost its feed (loading screen, logout transition).
        if (world.X == 0 && world.Y == 0)
        {
            world = _lastValidWorldPos; // fall back to last known good position
        }
        else
        {
            _lastValidWorldPos = world;
        }

        Vector3 map = playerReader.MapPos;

        // ── Fix CV — compute published HasApproachStart with engaging-goal
        // awareness and dual TTL. See constants block (~line 30) for full
        // rationale, evidence, and consumer impact. Summary: published value
        // is true if any of (raw bit, leader in engaging goal, within TTL).
        string? currentGoalName = botController.GoapAgent?.CurrentGoal?.Name?.Trim();
        bool currentGoalIsEngaging =
            currentGoalName is "Approach Target" or "Pull Target" or "Combat";
        bool definitiveAbandonment =
            currentGoalName is not null && !currentGoalIsEngaging;

        bool rawHasApproachStart = leaderNavProvider.HasApproachStart;

        if (rawHasApproachStart || currentGoalIsEngaging)
        {
            _lastEngagingActivityUtc = DateTime.UtcNow;
        }

        double effectiveTtlMs = definitiveAbandonment
            ? DefinitiveAbandonmentTtlMs
            : TransientTtlMs;
        double sinceLastMs = _lastEngagingActivityUtc == DateTime.MinValue
            ? double.MaxValue
            : (DateTime.UtcNow - _lastEngagingActivityUtc).TotalMilliseconds;
        bool withinTtl = sinceLastMs < effectiveTtlMs;

        bool publishedHasApproachStart =
            rawHasApproachStart || currentGoalIsEngaging || withinTtl;

        // Fix CV: one-shot log per grace activation. Fires the first poll cycle
        // where the TTL is actually doing work — raw=false, leader not in an
        // engaging goal, but published=true via TTL grace. The _ttlGraceLogged
        // flag throttles to once per activation; it resets when an engaging
        // signal (raw bit or engaging goal) returns. Makes CV's effectiveness
        // measurable on the leader's log: count CV fires = count of plan
        // flickers / mode flickers suppressed on the assist side.
        bool graceIsDoingWork =
            publishedHasApproachStart && !rawHasApproachStart && !currentGoalIsEngaging;
        if (graceIsDoingWork)
        {
            if (!_ttlGraceLogged)
            {
                string gapKind = definitiveAbandonment
                    ? $"definitive abandonment (goal=\"{currentGoalName}\")"
                    : "transient (NO PLAN)";
                logger.LogInformation(
                    "[LeaderStateService] [FIX-FIRE] CV: publishing HasApproachStart=TRUE " +
                    "via TTL grace ({0:0}ms since last engaging activity; window={1:0}ms; " +
                    "gap={2}). Smoothing prevents assist plan/mode flicker during the gap.",
                    sinceLastMs, effectiveTtlMs, gapKind);
                _ttlGraceLogged = true;
            }
        }
        else if (rawHasApproachStart || currentGoalIsEngaging)
        {
            // Engaging signal returned — reset the throttle so the next grace
            // activation logs again.
            _ttlGraceLogged = false;
        }

        // ── Fix CZ: cache last valid anchor; serve cache when raw is zero but
        //   publishedHasApproachStart is true (CV is smoothing). See ~line 30
        //   field declarations for full evidence and rationale.
        float rawApproachX = leaderNavProvider.ApproachStartWorldX;
        float rawApproachY = leaderNavProvider.ApproachStartWorldY;

        if (rawApproachX != 0f || rawApproachY != 0f)
        {
            _lastValidApproachStartX = rawApproachX;
            _lastValidApproachStartY = rawApproachY;
            _czCacheServeLogged = false;
        }

        bool czCacheServe =
            publishedHasApproachStart &&
            rawApproachX == 0f && rawApproachY == 0f &&
            (_lastValidApproachStartX != 0f || _lastValidApproachStartY != 0f);

        float publishedApproachX = czCacheServe ? _lastValidApproachStartX : rawApproachX;
        float publishedApproachY = czCacheServe ? _lastValidApproachStartY : rawApproachY;

        if (czCacheServe && !_czCacheServeLogged)
        {
            logger.LogInformation(
                "[LeaderStateService] [FIX-FIRE] CZ: serving cached " +
                "ApproachStart=<{0:0.0}, {1:0.0}> because raw=<0,0> while " +
                "publishedHasApproachStart=true (CV smoothing window). Prevents " +
                "the assist from navigating to anchor=<0,0,0> and tracing an " +
                "oval/ellipse path during the bridged transient.",
                _lastValidApproachStartX, _lastValidApproachStartY);
            _czCacheServeLogged = true;
        }

        return new LeaderState
        {
            WorldX = world.X,
            WorldY = world.Y,
            WorldZ = world.Z,
            MapX = map.X,
            MapY = map.Y,
            UIMapId = playerReader.UIMapId.Value,
            Status = DetermineStatus(),
            HealthPercent = playerReader.HealthPercent(),
            InCombat = bits.Combat(),
            TargetGuid = playerReader.TargetGuid,
            Timestamp = DateTime.UtcNow,

            // Waypoint sharing — only meaningful when leader is Patrolling.
            HasTargetWaypoint = leaderNavProvider.HasTargetWaypoint,
            TargetWaypointWorldX = leaderNavProvider.TargetWaypointWorldX,
            TargetWaypointWorldY = leaderNavProvider.TargetWaypointWorldY,

            // Approach-start anchor — only meaningful when leader is Approaching.
            // Fix CV: HasApproachStart is the smoothed value (see ~line 30 for design).
            // Fix CZ: ApproachStartWorldX/Y are also smoothed via last-valid cache.
            // Together they keep HasApproachStart=true ⇒ anchor coords are valid.
            // Before CZ, the X/Y were raw and could be (0,0) while HasApproachStart
            // was held true by CV's TTL — the assist would navigate to <0,0,0>
            // creating the oval/ellipse path in user's log-122 complaint #2.
            HasApproachStart = publishedHasApproachStart,
            ApproachStartWorldX = publishedApproachX,
            ApproachStartWorldY = publishedApproachY,

            // Mob blacklist — cumulative for the session.
            BlacklistedMobGuids = leaderNavProvider.BlacklistedMobGuidsSnapshot,
            NoEngageMobGuids = leaderNavProvider.NoEngageMobGuidsSnapshot,

            // Fix AV (Route A): dynamic stuck-rect propagation. The
            // assist applies each entry via Navigation.AddPropagatedStuckRect
            // every poll cycle — duplicates are deduped on overlap and
            // refresh a TTL so the rects stay alive on the assist as long
            // as the leader keeps republishing them. Empty array when the
            // leader has no current dynamic rects (between plan transitions).
            StuckRects = leaderNavProvider.StuckRectsSnapshot,

            // ── Fix GA (run-167) — Backtrack-mode coordination publish ──
            // Symmetric to PartyStatePublisher (assist→leader). Assist's
            // FFG Fix GB consumer reads these to enter coordinated backtrack
            // and avoid pursuing leader's oscillating target.
            IsBacktracking = navigation.IsBacktrackingActive,
            BacktrackAggressorGuid = navigation.BacktrackPublishAggressorGuid,
            BacktrackAggressorInRect = navigation.BacktrackPublishAggressorInRect,
            InsideBlacklistArea = navigation.IsInBlacklistArea(),
            BacktrackCurrentWaypointIdx = navigation.BacktrackPublishWaypointIdx,
        };
    }

    private BotStatus DetermineStatus()
    {
        if (bits.Dead())
            return BotStatus.Dead;

        if (bits.Combat())
            return BotStatus.Combat;

        if (!botController.IsBotActive)
            return BotStatus.Waiting;

        // Fix T (log-63 19:47:02 → 19:48:21): when FRG is actively paused waiting
        // for the assist (too far / Stuck / CantFollow), the bot is functionally
        // stationary even though its current goal is still FollowRouteGoal by
        // name. The previous goal-name → BotStatus mapping below reported
        // Patrolling in this state, which kept the assist's FFG in waypoint-
        // sharing mode targeting the leader's last-published waypoint. If that
        // waypoint had become unreachable (e.g., the leader just inserted a
        // detour around a blacklist rect — see Fix S), the assist's pathfinder
        // failed on every retry and the bots stayed separated until something
        // external (here, the bot stopping at 19:48:21:068) flipped status off
        // Patrolling. Reporting Waiting during pause-for-assist short-circuits
        // FFG's waypoint-sharing branch (which gates on
        // `leader.Status == Patrolling`) and routes the assist to the leader's
        // actual body via position-chase, which is the correct target while
        // the leader is stationary.
        if (leaderNavProvider.IsPausedForAssist)
            return BotStatus.Waiting;

        // ── Fix CN (log-114 evidence) — goal-name string mismatch ──
        //
        // The original switch keys used CLASS NAMES (e.g. "ApproachTargetGoal",
        // "LootGoal", "FollowRouteGoal"). But the actual GoapGoal.Name VALUE,
        // produced by the base ctor `: base(nameof(XxxGoal))` plus the base
        // class's display-formatting (strip "Goal" suffix + CamelCase-split),
        // is the HUMAN-READABLE form: "Approach Target", "Loot", etc. The
        // class-name keys NEVER matched goalName, so the switch always fell
        // through to the default `_ => BotStatus.Patrolling`.
        //
        // Evidence (log-114 LeaderStatePoller events received by assist):
        //   23:15:34:367  Leader status changed: Patrolling
        //   23:15:44:385  Leader status changed: Combat
        //   23:15:48:802  Leader status changed: Patrolling
        //   23:16:00:920  Leader status changed: Combat
        //   23:16:05:842  Leader status changed: Patrolling
        //   ...
        // Status only ever toggled between Patrolling ↔ Combat. NEVER reached
        // Approaching, Looting, Skinning, Resting, or Evading. The leader was
        // demonstrably in ATG/PTG repeatedly (per its own goal-plan log lines
        // "New Plan= Approach Target", "New Plan= Pull Target"), and was
        // looting between combats — none of which surfaced to the assist.
        //
        // Consequence: the assist's FFG.GetNavigationTarget reads
        // leader.Status to decide between RouteWalk (when Patrolling) and
        // PositionChase/Anchor (other statuses). With status frozen at
        // Patrolling during the leader's ATG, the assist took the RouteWalk
        // branch and pushed route-waypoint targets — including waypoints
        // NORTH of the leader's actual position. Concrete log-114 example
        // at 23:15:56:164: assist had walked SE to <-717.94, -4179.03>
        // (2.4y from leader anchor <-718.63, -4181.18>). CK suppressed an
        // OnDestinationReached Idle and called GetNavigationTarget(leader),
        // which — because leader.Status was incorrectly Patrolling — returned
        // route waypoint <-710.94, -4167.58>. The assist then walked back
        // NORTH ~10y to chase the route waypoint, ending at <-711.42, -4170.02>
        // by 23:15:58:786. User-visible: "backwards movement and travel by the
        // assist after combat" / "during approach".
        //
        // Plan-log inspection confirms the actual Goal.Name values:
        //   "Approach Target", "Pull Target", "Combat", "Loot",
        //   "Consume Corpse", "Corpse Consumed", "Follow Focus",
        //   "Follow <route-filename>" (FRG, variable suffix).
        // Skinning/Drink/Eat/Rest/Evade goals follow the same base-class
        // formatting pattern (strip "Goal" suffix; single-word names produce
        // "Skinning", "Drink", "Eat", "Rest", "Evade").
        //
        // Fix: change the switch keys to the actual Name values. FRG's
        // variable filename suffix is handled by the default Patrolling
        // arm — which is the desired status for FRG anyway, so no special
        // case is required. FollowFocusGoal also produces "Follow Focus"
        // but FFG is mode-gated to AssistFocus and never runs on the leader,
        // so no collision with the FRG-style default Patrolling arm.
        // ── Fix CO (log-115 evidence) — Goal.Name has a leading space ──
        //
        // Fix CN updated the switch keys from class names ("ApproachTargetGoal")
        // to what we believed were the actual Name values ("Approach Target").
        // But the LeaderStatePoller log in log-115 still showed status broadcasts
        // toggling only between Patrolling and Combat — never Approaching, never
        // Looting. CN didn't take effect.
        //
        // Hex-dump of the assist plan log lines revealed that Goal.Name has a
        // LEADING SPACE for every goal:
        //   "New Plan= {name}" template produces "New Plan=  Approach Target"
        //   with TWO spaces between '=' and 'Approach'. Since the template
        //   contributes only one space, the value of `name` must be the string
        //   " Approach Target" (leading space included). Same for every other
        //   goal: " Combat", " Loot", " Follow Focus", and even the FRG name
        //   " Follow 01-04_ Durotar_ Valley of Trials" — including FRG which
        //   passes an explicit string ("Follow ..." without leading space) to
        //   the base ctor, confirming the leading space is added inside the
        //   base GoapGoal class regardless of how Name is derived.
        //
        // So the CN switch keys "Loot", "Approach Target" etc. (without leading
        // space) never matched the actual goalName values, and the switch fell
        // through to the default Patrolling arm — exactly the symptom the user
        // continued to report in log-115.
        //
        // Concrete evidence at 00:07:04-11 in log-115:
        //   00:07:04:571 LEADER plan: Approach Target  (Name = " Approach Target")
        //   00:07:05:943 ASSIST BE push: 11-waypoint route span heading NORTH
        //                from bot position <-716.04, -4189.87>. BE makes this
        //                push because FFG.GetNavigationTarget(leader) takes the
        //                RouteWalk branch when leader.Status == Patrolling —
        //                which it was, incorrectly, because the CN switch missed.
        //   00:07:07:698 Bot moved NORTH to <-716.27, -4180.93> (~9y N).
        //                Mode flips PositionChase → Anchor at <-717.06, -4190.48>
        //                (SOUTH of bot). Bot must walk SOUTH ~9.6y back.
        //   00:07:11:735 FFG.OnExit: assist at 35.3y from leader (lost worse).
        //
        // The polled status broadcasts during this whole sequence — 6.5 seconds
        // covering leader's ATG → PTG → ATG → NO PLAN → PTG → Combat cycle —
        // showed only Patrolling, then Combat at 00:07:11:620. No Approaching
        // status was ever broadcast, confirming the switch never matched.
        //
        // Fix: trim the goalName before the switch. The base GoapGoal class's
        // leading-space convention is now neutralized; the switch keys remain
        // the readable forms (no leading space hard-coded into source). If the
        // base class is ever fixed to omit the leading space, this code keeps
        // working unchanged.
        //
        // ── Update (Fix CP) ──
        // The base GoapGoal constructor was inspected after CO was deployed
        // (file `GoapGoal.cs`) and Fix CP was applied to strip the leading
        // space at the source. So `Name` no longer has a leading space and
        // the `.Trim()` below is now redundant. It is intentionally retained
        // as defensive belt-and-suspenders: if CP is ever reverted by
        // accident, the switch keeps working here. Cost is one Trim() call
        // per 250 ms poll cycle, negligible.
        string? goalName = botController.GoapAgent?.CurrentGoal?.Name?.Trim();
        return goalName switch
        {
            "Loot"               => BotStatus.Looting,
            "Skinning"           => BotStatus.Skinning,
            "Drink"
                or "Eat"
                or "Rest"        => BotStatus.Resting,
            "Evade"              => BotStatus.Evading,
            "Approach Target"
                or "Pull Target" => BotStatus.Approaching,
            _                    => BotStatus.Patrolling
        };
    }
}
