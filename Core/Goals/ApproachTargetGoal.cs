using Core.GOAP;
using Core.Party;

using Microsoft.Extensions.Logging;

using System;
using System.Numerics;  // Fix BN: Vector3 for anchor-position field
using System.Threading;

using static System.Diagnostics.Stopwatch;

#pragma warning disable 162

namespace Core.Goals;

public sealed partial class ApproachTargetGoal : GoapGoal, IGoapEventListener
{
    private const bool debug = true;
    private const double STUCK_INTERVAL_MS = 400;
    private const double MAX_APPROACH_DURATION_MS = 15_000;
    private const double MIN_TIME_TILL_IDLE = 2000;

    public override float Cost => 8f;

    private readonly ILogger<ApproachTargetGoal> logger;
    private readonly ConfigurableInput input;
    private readonly Wait wait;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly StopMoving stopMoving;
    private readonly CombatTracker combatTracker;
    private readonly IMountHandler mountHandler;
    private readonly IBlacklist targetBlacklist;
    private readonly CombatLog combatLog;
    private readonly ClassConfiguration classConfig;
    private readonly ChatReader chatReader;
    private readonly Navigation navigation;
    private readonly AssistStateStore assistStateStore;
    private readonly AssistStatusProvider assistStatusProvider;
    private readonly LeaderNavigationProvider leaderNavProvider;

    private long approachStart;

    /// <summary>
    /// Holds the leader's first Update() tick after publishing the approach-start anchor,
    /// giving the assist one or two API poll cycles (~250ms each) to detect the anchor,
    /// call StartNavigatingToLeader, and appear as NavigatingToLeader in AssistStateStore
    /// before the leader begins pressing the interact key.
    /// Without this, the leader immediately presses interact and moves away from the anchor
    /// position before the assist has had a chance to start navigating there.
    /// </summary>
    private bool _anchorSyncPauseActive;
    private DateTime _anchorSyncPauseStartUtc;
    private Vector3 _anchorPos;  // Fix BN: saved at OnEnter for distance check during pause
    private double _anchorSyncPauseTimeoutSec;  // Fix BS: computed at OnEnter based on initial assist distance

    // Fix BN (log-97 evidence) + Fix BS (log-98 evidence: 3 of 9 BN events timed out at the
    // flat 3.0s ceiling with the assist still 19-41y from anchor, missing combats):
    // distance-aware sync-pause completion AND distance-scaled timeout. The original Fix BN
    // proximity gate is preserved; Fix BS replaces the constant timeout ceiling with a value
    // computed at OnEnter based on how far the assist is — when the assist is close, a brief
    // wait is sufficient; when far, a longer wait is needed to actually give the assist time
    // to arrive. Cap at FarTimeoutSec to prevent indefinite leader freezing when the assist
    // is unreachable.
    // See HANDOFF Fix BN/BS for the full log evidence trail.
    //
    // ── Fix CI (log-112 20:36:55 evidence) — tighten BN/BS proximity gate ──
    //
    // User complaint: "On the last combat I noticed that the assist wasn't anywhere
    // near the leader when the leader was engaging the mob. We need to be able to
    // get closer to the approach before the leader engages the mob."
    //
    // Evidence (log-112): on the LAST 3 ATG events, BN/BS completed with the assist
    // 10.2y, 10.3y, and 10.7y from the anchor — at the EDGE of the 12y threshold.
    // The leader proceeded immediately with the interact approach (BN/BS check
    // passed in ~30ms because distance ≤ threshold). During the leader's 5-second
    // run to the mob (~22y), the assist's mode-transition lag (~1.2s from
    // HasApproachStart polling) meant the assist spent ~1 second in the wrong mode
    // (PositionChase chasing the leader's body) before transitioning to Anchor —
    // by which time HasApproachStart was about to clear (ATG → Pull Target at
    // 20:36:57:411). Net effect: assist permanently lagged 8-10y behind leader
    // throughout the engagement. At combat start (20:37:00:746), the assist was
    // 9.8y from the leader — at the EDGE of healing/spell range.
    //
    // Old behavior (AnchorSyncReadyYards=12y, NearTimeoutSec=1.5s):
    //   distToAnchor=10.3y ≤ 12y → BN/BS passes IMMEDIATELY (~30ms)
    //   → Leader proceeds with assist still in PositionChase mode at 10.3y
    //   → 1.2s mode-transition lag wasted while leader sprints to mob
    //   → Final gap at engagement: 9.8y
    //
    // New behavior (AnchorSyncReadyYards=7y, NearTimeoutSec=2.5s):
    //   distToAnchor=10.3y > 7y → BN/BS WAITS
    //   → ~1.2s later assist mode-transitions to Anchor, starts navigating
    //   → After ~2.0s total, assist is within 7y of anchor → BN/BS passes
    //   → Leader proceeds with assist at ~5-7y (already in Anchor mode)
    //   → Final gap at engagement: ~5-7y
    //
    // Why 7y? Matches FFG's "Reached follow position" threshold — the assist's
    // natural idle distance from the leader. Below this, the assist is essentially
    // adjacent. Above this, the assist is still in Following but visibly behind.
    //
    // Why bump NearTimeoutSec to 2.5s? The assist needs time to:
    //   1. Detect HasApproachStart=true (250ms-1s polling lag)
    //   2. Mode-transition Idle/RouteWalk → Anchor (~1 FFG tick)
    //   3. Navigate from current pos to within 7y of anchor (~0.7-1.1s at run speed)
    // Total ~1.5-2.0s. 2.5s gives 25% headroom for terrain/path quirks.
    //
    // Trade-off: ~0.5-1.5s extra wait per ATG (when assist starts at 8-12y from
    // anchor). Worth it for the user's stated goal: assist closer at engagement.
    // For cases where assist is ALREADY close (≤7y, common — see log-112 data:
    // 2.5y, 2.6y, 4.7y, 5.3y, 6.2y), BN/BS still passes immediately.
    //
    // Mid/Far timeouts unchanged: those cover 12-30y and 30+y starts (rare,
    // typically only during long catch-ups), and the existing 4s/6s already
    // allows ample closing time. Tightening their proximity gate also benefits
    // those cases (assist must reach 7y, not just 12y, before leader proceeds).
    private const float AnchorSyncReadyYards = 7.0f;    // Fix CI: was 12.0f
    private const double NearTimeoutSec = 2.5;   // Fix CI: was 1.5s. Applies when distAtArm ≤ AnchorSyncReadyYards (≤7y) at OnEnter
    private const double MidTimeoutSec  = 4.0;   // Fix BS: 7-30y at OnEnter (was 12-30y; bucket boundary follows AnchorSyncReadyYards via Fix CI)
    private const double FarTimeoutSec  = 6.0;   // Fix BS: 30y+ at OnEnter (cap)
    private const float MidDistanceCutoffYards = 30.0f;  // Fix BS: boundary near→mid

    // Fix BT (log-99 evidence: leader had 23 ATG.OnEnter events vs 9 BN/BS completions;
    // target 872063 alone had 9 OnEnter events in 30s, anchor moving 30y between consecutive
    // publishes from <-722,-4147> to <-691,-4149>): stable anchor across same-target
    // re-entries. The ATG re-enters whenever the leader's pather can't reach the mob and
    // escalates — each re-entry republishes a fresh anchor at the leader's current position,
    // causing the assist to chase a moving target. By keeping the original anchor for the
    // duration of one engagement attempt with the same target (within the grace period),
    // the assist has a stable target to navigate to. Cooldown after target change or grace
    // expiry — fresh OnEnter publishes a new anchor and arms a new sync-pause.
    private int _stableTargetGuid;
    private DateTime _stableAnchorUtc = DateTime.MinValue;
    private Vector3 _stableAnchorPos;
    private const double StableAnchorGraceSec = 10.0;  // Fix BT: same-target re-entries within this window reuse the original anchor
    private double nextStuckCheckTime;
    private int initialTargetGuid;
    private float initialMinRange;

    private bool _evadeRecoveryActive;

    private const double RangeStuckIntervalMs = 3000;
    private float _rangeStuckLastMinRange;
    private double _rangeStuckCheckAtMs;

    private double ApproachDurationMs => GetElapsedTime(approachStart).TotalMilliseconds;

    public ApproachTargetGoal(ILogger<ApproachTargetGoal> logger,
        ConfigurableInput input, Wait wait,
        PlayerReader playerReader, AddonBits addonBits,
        StopMoving stopMoving, CombatTracker combatTracker,
        IBlacklist blacklist,
        IMountHandler mountHandler,
        CombatLog combatLog,
        ClassConfiguration classConfig,
        ChatReader chatReader,
        Navigation navigation,
        AssistStateStore assistStateStore,
        AssistStatusProvider assistStatusProvider,
        LeaderNavigationProvider leaderNavProvider)
        : base(nameof(ApproachTargetGoal))
    {
        this.logger = logger;
        this.input = input;
        this.wait = wait;
        this.playerReader = playerReader;
        this.bits = addonBits;
        this.stopMoving = stopMoving;
        this.combatTracker = combatTracker;
        this.mountHandler = mountHandler;
        this.targetBlacklist = blacklist;
        this.combatLog = combatLog;
        this.classConfig = classConfig;
        this.chatReader = chatReader;
        this.navigation = navigation;
        this.assistStateStore = assistStateStore;
        this.assistStatusProvider = assistStatusProvider;
        this.leaderNavProvider = leaderNavProvider;

        if (classConfig.Mode == Mode.PartyLeader)
        {
            AddPrecondition(GoapKey.assistisfollowing, true);
            AddPrecondition(GoapKey.assistrequestreturn, false);
        }

        if (classConfig.Mode == Mode.AssistFocus)
        {
            // Fix AH (log-73 16:43:37 → 16:44:15:225): was partyincombat=true; now
            // partyEngaging=true. partyEngaging is computed in GoapAgent.UpdateWorldState
            // as PartyInCombat() || (Mode == AssistFocus && leaderNavProvider.HasApproachStart),
            // so this broadens the precondition to also be satisfied during the leader's
            // pre-combat approach window.
            //
            // Why: in log-73 the leader entered ATG at 16:43:37:493 and published an
            // approach-start anchor at <-414.54, -4120.96>. The leader then walked into
            // rocks via the Interact key auto-walk (which uses the game's built-in
            // movement system, not PPather), pressing Approach 9 times across 4.2 s
            // (16:43:37:835 → 16:43:42:000) before combat finally fired at 16:43:46:079
            // — an 8.5 s pre-combat traversal. The assist's ATG was un-selectable that
            // entire window (partyincombat=false), so FFG remained active and fell into
            // PositionChase via the _approachAnchorColocated latch (anchor 2.6 y away
            // <  3.6 y POP_DIST). FFG's pather then tried to route to position-chase
            // targets on the rocky terrain (PPather returned Z=51.86 elevation results
            // it couldn't actually walk to). 8 path requests in 12 s, all rejected.
            // 16:44:15:225 TickNavActiveTimeout escalated to CantFollow. The assist
            // never moved past <-389.84, -4137.61>, leaving the leader 23 y away alone
            // at the kill site.
            //
            // What partyEngaging unblocks: once FollowFocusGoal's Fix AH Part 1
            // (focus-chain target acquisition at the approach anchor) satisfies
            // hastarget=true, the planner sees the precondition tuple
            // {partyEngaging=true, hastarget=true, targetisalive=true, targethostile=true,
            //  incombatrange=false, inblacklistarea=false, evadeRecovery=false,
            //  forcedfollow=false} and selects ATG (cost 8) over FFG (cost 19). ATG's
            // AssistFocus Update branch then runs the same key sequence the leader's
            // ATG uses (PressTargetFocus → PressTargetOfTarget → PressApproach), and
            // the assist auto-walks through the rocks via the in-game Interact mechanic
            // — the same one the leader used to cross that terrain.
            //
            // Once partyincombat does fire (combat starts), partyEngaging stays true
            // (combat is one of its two disjuncts) and ATG continues unchanged. When
            // the leader exits ATG, HasApproachStart goes false; if combat is still
            // active partyEngaging stays true, otherwise it drops and ATG becomes
            // un-selectable (FFG resumes). This matches the prior steady-state
            // semantics exactly — the change only affects the previously-uncovered
            // pre-combat window.
            //
            // partyincombat itself is NOT redefined (only ATG's precondition migrates).
            // FRG.OnGoapEvent's partyincombat handler still fires only on true combat,
            // so the leader's patrol-abort behavior is unchanged.
            AddPrecondition(GoapKey.partyEngaging, true);
        }

        AddPrecondition(GoapKey.forcedfollow, false);
        AddPrecondition(GoapKey.hastarget, true);
        AddPrecondition(GoapKey.targetisalive, true);
        AddPrecondition(GoapKey.targethostile, true);
        AddPrecondition(GoapKey.incombatrange, false);
        AddPrecondition(GoapKey.inblacklistarea, false);
        AddPrecondition(GoapKey.evadeRecovery, false);

        AddEffect(GoapKey.incombatrange, true);
    }

    public override bool CanRun()
    {
        // ── Fix FU (run-166) — yield to Fix FN/FT backtrack ──
        //
        // When the leader is inside a BL rect and the FollowRouteGoal
        // backtrack state machine is driving us back along prior route
        // waypoints (IsBacktrackingActive=true), ATG must not preempt.
        // WoW Classic auto-retargets the leader to the in-rect mob each
        // time the target slot empties — every PressClearTarget creates
        // a brief target=0 window, the BL caster's next swing re-fills
        // the target with the same in-rect guid, satisfying ATG's
        // preconditions (hasTarget, targethostile, etc.) on the very
        // next planner tick. Without this gate, ATG would fire, run its
        // E5 in-rect ClearTarget, exit, and trigger FRG.OnExit → Abort.
        // Even with Fix FU's state preservation across Abort, suppressing
        // the wasted plan flicker at the planner level is cleaner.
        //
        // Mirrors BlacklistTargetGoal.CanRun line 52. The IsBacktrackingActive
        // flag is true during Navigating + Evaluating phases (set by FRG
        // backtrack state machine, preserved across FRG.Abort by Fix FU) and
        // false during EngageWindow (where Combat plan is supposed to win via
        // BacktrackEngageGuid → Fix 17 btEngageGuidMatch). So ATG is suppressed
        // only when we're actively retreating, not when we're re-engaging at
        // a backtrack waypoint.
        if (navigation.IsBacktrackingActive)
            return false;

        return true;
    }

    public void OnGoapEvent(GoapEventArgs e)
    {
        if (e.GetType() == typeof(ResumeEvent))
        {
            approachStart = GetTimestamp();
        }
        else if (e is GoapStateEvent s && s.Key == GoapKey.evadeRecovery)
        {
            _evadeRecoveryActive = s.Value;
        }
    }

    public override void OnEnter()
    {
        // Target-tracking diagnostic: always log the live game target guid + the
        // escape's locked guid at goal entry, so mob-target changes are traceable
        // across the whole run (grep TARGET-GUID). Unconditional and Info-level
        // (the older RATF log is Debug and skipped while an escape is active).
        logger.LogInformation($"[ATG] OnEnter TARGET-GUID={playerReader.TargetGuid} escapeGuid={navigation.ApproachEscapeTargetGuid}");

        // E5: approach-entry gate (re-evaluation + fresh-pull). We only reach OnEnter
        // for a fightable (non-ignored) target — the finder skips no-engage mobs. If
        // that target is inside a blacklist rect, don't approach: blacklist it in-rect
        // so we retreat instead. This is the natural re-evaluation hook — a mob whose
        // no-engage verdict expired is re-judged here on the next approach (still
        // in-rect -> re-blacklist; moved out -> fall through and approach). The
        // IsIgnored abort at the top of Update performs the actual exit next tick.
        if (playerReader.TargetGuid != 0 && navigation.IsTargetLikelyInBlacklistRect())
        {
            logger.LogInformation(
                $"[ATG] E5 approach gate: target guid={playerReader.TargetGuid} is " +
                $"inside a blacklist rect — clearing without blacklisting (we never " +
                $"engaged this mob).");

            // ── Edit 2 (replaces former E5 blacklist behavior) ──
            //
            // Per operator principle: only mobs we have actively engaged go onto
            // the IsIgnored map. ATG.OnEnter is pre-engagement — we just acquired
            // the target via Tab (or focus chain, or SoftInteract) but have not
            // attacked. So: clear the target, don't mutate IsIgnored, don't
            // SendGoapEvent. The bot's plan re-evaluates and either picks
            // another mob (finder runs again) or resumes patrol.
            //
            // Edit 1 (FollowRouteGoal.cs side-thread filter) catches the same
            // case at the finder layer before ATG.OnEnter ever runs. This E5
            // gate stays as the safety net for paths that bypass the FRG
            // finder: focus chain (assist following leader's target), SoftInteract
            // (game auto-targets on proximity), plan-transition flicker.
            input.PressClearTarget();
            wait.Update();

            // ── Fix FC (run-154 20:55:16:793 false StuckRect in open terrain) ──
            //
            // Reset approach timers BEFORE returning so the next ATG.Update
            // tick doesn't false-stuck against a stale approachStart from a
            // previous ATG run. Even with Edit 2's PressClearTarget(),
            // ATG.Update can run once or twice before the planner switches
            // goals; without resetting these timers, NonCombatApproach would
            // see ApproachDurationMs against an approachStart from minutes
            // ago and fire "Seems stuck! Attempting pather escape." in open
            // terrain. The original normal-path setup at lines ~289-290 below
            // is the only other place these get reset, and we skip past it on
            // this early return.
            approachStart = GetTimestamp();
            nextStuckCheckTime = MIN_TIME_TILL_IDLE;

            // ── Fix FF Q1-C (audit completion of Fix FC) ──
            // Fix FC's reset is INCOMPLETE: it resets approachStart and
            // nextStuckCheckTime (the "Seems stuck" timer family) but
            // leaves _rangeStuckCheckAtMs and _rangeStuckLastMinRange in
            // their pre-E5 state. The range-progress check at line ~929
            // reads those fields; on subsequent ticks after E5 they retain
            // values from the previous ATG run. Even with Q1-B's
            // currentRange<=0 sentinel in place, a non-zero-but-stale
            // _rangeStuckLastMinRange from minutes ago can produce a
            // currentRange >= stale comparison that fires TryUnstuck
            // spuriously. Reset both range-tracker fields here so the
            // E5 early return matches what the normal-path setup at
            // lines 320-321 below produces (initialMinRange/approachStart
            // both reset). Symmetric with the OnEnter-time defaults at
            // lines 365-366 (RangeStuckIntervalMs / float.MaxValue).
            _rangeStuckCheckAtMs = RangeStuckIntervalMs;
            _rangeStuckLastMinRange = float.MaxValue;

            return;
        }

        initialTargetGuid = initialTargetGuid == playerReader.TargetGuid
            ? -1
            : playerReader.TargetGuid;

        initialMinRange = playerReader.MinRange();

        approachStart = GetTimestamp();
        nextStuckCheckTime = MIN_TIME_TILL_IDLE;

        if (navigation.IsApproachEscapeActive &&
            playerReader.TargetGuid != 0 &&
            navigation.ApproachEscapeTargetGuid != 0 &&
            navigation.ApproachEscapeTargetGuid != playerReader.TargetGuid)
        {
            navigation.Stop();
        }
        if (!navigation.IsApproachEscapeActive)
        {
            logger.LogDebug($"[ATG] OnEnter: calling RATF for target={playerReader.TargetGuid} — " +
                $"pre-RATF state: escapeGuid={navigation.ApproachEscapeTargetGuid} " +
                $"yards={navigation.ApproachEscapeCurrentYards:0} active={navigation.IsApproachEscapeActive} " +
                $"exhausted={navigation.IsApproachEscapeExhausted} escalating={navigation.IsApproachEscapeEscalating}");
            navigation.ResetApproachEscapeForTarget(playerReader.TargetGuid);
        }
        else
        {
            logger.LogInformation($"[ATG] OnEnter: escape ACTIVE — skipping RATF. " +
                $"yards={navigation.ApproachEscapeCurrentYards:0} " +
                $"startUtc={navigation.ApproachEscapeStartUtc:HH:mm:ss.fff} " +
                $"lastAttemptAge={(DateTime.UtcNow - navigation.ApproachEscapeLastAttemptUtc).TotalMilliseconds:0}ms " +
                $"guid={navigation.ApproachEscapeTargetGuid} target={playerReader.TargetGuid} " +
                $"exhausted={navigation.IsApproachEscapeExhausted} escalating={navigation.IsApproachEscapeEscalating}");
        }
        _rangeStuckLastMinRange = float.MaxValue;
        _rangeStuckCheckAtMs = RangeStuckIntervalMs;

        input.PressDisableSoftInteract();
        wait.Update();

        // Publish the approach-start anchor so the assist knows the leader's
        // geographic starting point for this approach. The assist navigates to
        // this fixed position rather than chasing the leader's moving body,
        // ensuring both bots begin their final interact-key close on the mob
        // from the same location and through the same terrain.
        if (classConfig.Mode == Mode.PartyLeader)
        {
            // Fix BT (log-99): determine whether this OnEnter is a re-entry for the SAME
            // target within the grace period. If so, reuse the original anchor instead of
            // republishing at the leader's (now-different) current position. This stops
            // the "moving anchor" problem observed in log-99 — target 872063 had 9 OnEnter
            // events in 30s with the published anchor sliding ~30y from <-722,-4147> to
            // <-691,-4149> while the assist tried to chase the moving target.
            int currentTarget = playerReader.TargetGuid;
            bool sameTargetWithinGrace =
                currentTarget != 0 &&
                currentTarget == _stableTargetGuid &&
                (DateTime.UtcNow - _stableAnchorUtc).TotalSeconds < StableAnchorGraceSec;

            if (sameTargetWithinGrace)
            {
                // Reuse the existing anchor. Re-publish to refresh leaderNavProvider's
                // HasApproachStart bit (which the previous OnExit cleared); the assist sees
                // the same anchor coordinates so its in-flight nav is preserved.
                _anchorPos = _stableAnchorPos;
                leaderNavProvider.SetApproachStart(_anchorPos);
                logger.LogInformation(
                    $"[ATG] [FIX-FIRE] BT: Re-entry for same target={currentTarget} within " +
                    $"{StableAnchorGraceSec:0.0}s grace — reusing stable anchor {_anchorPos}.");
            }
            else
            {
                // Fix BN: save the anchor position for the distance-aware sync-pause check.
                _anchorPos = playerReader.WorldPos;
                leaderNavProvider.SetApproachStart(_anchorPos);
                logger.LogInformation($"[ATG] Published approach-start anchor: {_anchorPos}");

                // Fix BT: update stable-anchor tracking for future re-entries.
                _stableTargetGuid = currentTarget;
                _stableAnchorPos = _anchorPos;
            }
            _stableAnchorUtc = DateTime.UtcNow;

            // Fix BS: compute the timeout from initial assist distance. Close assist → short
            // wait (assist already in position, don't dawdle). Far assist → longer wait
            // (give assist time to actually close the gap). The cap (FarTimeoutSec) prevents
            // indefinite freezing when assist is truly unreachable.
            // Compute distance against the CURRENT anchor (which may be the stable one or
            // a fresh one) — either way it reflects the assist's distance to the target.
            float distAtArm = assistStateStore.GetNearestAssistDistanceYards(_anchorPos);
            if (distAtArm <= AnchorSyncReadyYards)
                _anchorSyncPauseTimeoutSec = NearTimeoutSec;
            else if (distAtArm <= MidDistanceCutoffYards)
                _anchorSyncPauseTimeoutSec = MidTimeoutSec;
            else
                _anchorSyncPauseTimeoutSec = FarTimeoutSec;

            // Arm sync-pause: hold interact presses until the assist is actually near the
            // anchor (Fix BN: AnchorSyncReadyYards proximity) or the (scaled) timeout expires.
            // Re-arm fresh on every OnEnter (including BT re-entries): each entry is a
            // chance to give the assist more time to close before the leader starts
            // pressing interact. This is a feature, not a bug — BT only stabilizes the
            // anchor VALUE; it keeps the leader's pause behavior responsive.
            _anchorSyncPauseActive = true;
            _anchorSyncPauseStartUtc = DateTime.UtcNow;
        }
    }

    public override void OnExit()
    {
        input.StopForward(false);

        // Clear the approach-start anchor — the assist should revert to normal
        // patrol-follow behaviour once the leader leaves ATG.
        if (classConfig.Mode == Mode.PartyLeader)
        {
            leaderNavProvider.ClearApproachStart();
        }
    }

    public override void Update()
    {
        wait.Update();

        if (bits.Drowning())
            input.PressJump();

        // Abort if either:
        //  (a) the GoapKey.evadeRecovery broadcast has fired and OnGoapEvent set
        //      _evadeRecoveryActive=true (the original gate), OR
        //  (b) the current target is already in playerReader.IsIgnored — which is
        //      what HandleGoapEvent (real evade or test endpoint) effectively
        //      asserts when it dispatches EvadeBlacklistEvent: the leader's
        //      ApproachTargetGoal/PullTargetGoal/CombatGoal sites all call
        //      playerReader.IgnoreTarget(guid) on the same update tick that
        //      dispatches the event, and the test endpoint's PartyController
        //      does the same. The agent-level diff on the assist also calls
        //      IgnoreTarget before HandleGoapEvent.
        //
        // Without (b), ATG's update tick that is already in flight when
        // HandleGoapEvent sets _evadeRecoveryUntilUtc on a different thread
        // proceeds through the PressTargetFocus → PressTargetOfTarget →
        // PressApproach chain at lines ~439–444, which re-acquires the
        // blacklisted mob via the focus chain — undoing the ClearTarget that
        // HandleGoapEvent just pressed. Observed in log 24 (leader
        // 21:39:35:376 Insert pressed by HandleGoapEvent → 21:39:35:422 PageUp
        // → 21:39:35:501 F → re-target on blacklisted mob 7962675 → leader
        // stuck via wantNavPaused for 15s).
        bool currentTargetIsIgnored =
            bits.Target() && playerReader.TargetGuid != 0 &&
            playerReader.IsIgnored(playerReader.TargetGuid);

        if (_evadeRecoveryActive || currentTargetIsIgnored)
        {
            string reason = _evadeRecoveryActive
                ? "evade recovery active"
                : $"current target guid={playerReader.TargetGuid} is in IsIgnored (broadcast not yet seen)";
            logger.LogInformation($"[ApproachTargetGoal] Aborting approach — {reason}.");
            input.StopForward(false);
            input.PressStopAttack();
            wait.Update();
            input.PressClearTarget();
            wait.Update();
            return;
        }

        // ── Fix CF Q2a (run-157 01:53:51:573 → 01:53:56:418 assist runs into rect) ──
        //
        // Mid-approach re-check of IsTargetLikelyInBlacklistRect. The OnEnter
        // E5 check at line 272 fires only once — at goal entry — and uses
        // whatever bot facing + range happens to hold at that exact instant.
        // That single-point estimate has a known false-negative mode at long
        // range with wide brackets (see Navigation.cs:5847-5894 Fix EQ rationale).
        //
        // Run-147 added the multi-sample fallback inside Navigation's
        // IsTargetLikelyInBlacklistRect; run-157 surfaces the next failure
        // mode: even when multi-sample fires, ATG only listens at OnEnter.
        // 167 ms after ATG.OnEnter the multi-sample HIT detected the rect for
        // mob 2092377 (estimated probe=<24.21, -1614.06> inside the static
        // rect [-116,-1672 .. 21,-1545] inflate 6.0). ATG had no listener,
        // kept approaching, the assist ran into the rect, the mob attacked,
        // and Fix 17 self-defense kicked in — the leader followed via Fix L,
        // the Blacklist library blocked the leader's attacks, and the
        // leader-stuck-while-assist-fights symptom emerged.
        //
        // This re-check fires every Update tick. Once the bot has had time to
        // rotate toward the target (typically within 100-200 ms of OnEnter),
        // multi-sample becomes reliable and any false negative from OnEnter
        // is corrected here. Semantics match OnEnter E5: clear without
        // blacklisting (we haven't engaged the mob — only approached). The
        // operator principle "only mobs we have actively engaged go onto the
        // IsIgnored map" is preserved.
        //
        // Gated on:
        //   - playerReader.TargetGuid != 0 (we have a target to evaluate)
        //   - !navigation.IsApproachEscapeActive (don't interfere with an
        //     active escape flow; escape state machine has its own exit)
        //
        // Placement: after the IsIgnored guard above (so IsIgnored already
        // got its abort path) and before the player-position bail-outs at
        // 479+ / 514+ (those handle different conditions — player in rect
        // vs target in rect — and don't subsume this).
        if (playerReader.TargetGuid != 0 &&
            !navigation.IsApproachEscapeActive &&
            navigation.IsTargetLikelyInBlacklistRect())
        {
            logger.LogInformation(
                $"[ATG] Mid-approach rect re-check: target guid={playerReader.TargetGuid} " +
                $"detected inside a blacklist rect — clearing without blacklisting " +
                $"(we never engaged this mob).");
            input.StopForward(false);
            input.PressStopAttack();
            wait.Update();
            input.PressClearTarget();
            wait.Update();
            stopMoving.StopForward();
            navigation.Stop();
            navigation.ResetApproachEscape();

            // ── Fix FF Q1-D (Q2a / Fix FC reset symmetry) ──
            // Fix FC's OnEnter E5 path resets approachStart, nextStuckCheckTime,
            // _rangeStuckCheckAtMs, _rangeStuckLastMinRange (the last two via
            // Q1-C above). Q2a is the same semantic path — abort the approach
            // because the target is in a blacklist rect — but it runs in
            // Update rather than OnEnter, so without a matching reset the
            // approach-timer family stays loaded with values from the
            // current ATG.OnEnter (typically only 100-200 ms in, so
            // ApproachDurationMs is small and the line ~926 / line ~960
            // checks usually don't fire YET on the same tick). However:
            //   * If the planner re-enters ATG for a sibling target before
            //     the OnEnter reset path runs again (re-entry via the
            //     ApproachTarget plan rather than a fresh OnEnter), these
            //     stale baselines persist.
            //   * Range-progress baseline staleness is the exact failure
            //     mode Fix FC was originally added to prevent on the E5
            //     side; Q2a inherits the same hazard.
            // Mirror Fix FC's reset (now Fix FC + Q1-C combined) for
            // strict symmetry. Cheap; no downside.
            approachStart = GetTimestamp();
            nextStuckCheckTime = MIN_TIME_TILL_IDLE;
            _rangeStuckCheckAtMs = RangeStuckIntervalMs;
            _rangeStuckLastMinRange = float.MaxValue;

            wait.Update(playerReader.DoubleNetworkLatency);
            wait.Update();
            return;
        }

        if (!navigation.IsApproachEscapeActive &&
            (navigation.IsApproachEscapeExhausted ||
             (!navigation.IsApproachEscapeEscalating && navigation.IsInBlacklistArea())))
        {
            string bail1Reason = navigation.IsApproachEscapeExhausted
                ? $"all escape levels exhausted (10y/20y/30y failed)"
                : $"player inside blacklist area";
            // Edit 3: pre-engagement bail-out (escape-exhausted or wandered
            // into rect mid-approach) — clear target without blacklisting.
            // We never landed an attack on this mob; per operator principle,
            // do not mutate IsIgnored, do not SendGoapEvent. Plan re-evaluates
            // and the bot patrols normally; the in-rect detection in Edit 1
            // (FRG finder geometric check) will keep the finder from re-
            // acquiring the same mob from the rect on its next cycle.
            logger.LogWarning($"[ATG] Bail-out: clearing target guid={playerReader.TargetGuid} — reason: {bail1Reason}.");
            input.PressStopAttack();
            input.PressClearTarget();
            wait.Update();
            stopMoving.StopForward();
            navigation.Stop();
            navigation.ResetApproachEscape();
            navigation.ClearStuckRects();
            wait.Update(playerReader.DoubleNetworkLatency);
            wait.Update();
            return;
        }

        bool targetInBlacklist = targetBlacklist.Is();

        if (chatReader.ForcedFollow)
        {
            AddEffect(GoapKey.forcedfollow, true);
            return;
        }

        if(!navigation.IsApproachEscapeActive && !navigation.IsApproachEscapeEscalating && !bits.Combat() && bits.Target() && (targetInBlacklist || navigation.IsInBlacklistArea()))
        {
            logger.LogWarning($"[ATG] Bail-out: clearing target guid={playerReader.TargetGuid} — " +
                $"reason: {(targetInBlacklist ? "target in blacklist" : "player inside blacklist area")} " +
                $"[exhausted={navigation.IsApproachEscapeExhausted} escalating={navigation.IsApproachEscapeEscalating} yards={navigation.ApproachEscapeCurrentYards:0}].");

            // Edit 3: pre-engagement bail-out — clear target without
            // blacklisting (we never engaged this mob).

            input.PressStopAttack();
            input.PressClearTarget();
            wait.Update();
            stopMoving.StopForward();
            navigation.Stop();
            navigation.ResetApproachEscape();
            navigation.ClearStuckRects();
            wait.Update(playerReader.DoubleNetworkLatency);
            wait.Update();
            return;
        }

        if (classConfig.Mode == Mode.PartyLeader && _anchorSyncPauseActive)
        {
            double elapsed = (DateTime.UtcNow - _anchorSyncPauseStartUtc).TotalSeconds;

            // Fix BN: require BOTH a nav-state signal AND proximity. The old condition
            // (assistNavigating alone) exited immediately because the assist is in
            // NavigatingToLeader almost continuously during catch-up — proximity was
            // ignored. Log-97 evidence: 9 of 11 ATG sync-pauses completed in 1-19 ms
            // with the assist up to 55 y from the anchor. New condition: distance
            // <= AnchorSyncReadyYards counts as ready (handles both navigating-toward
            // and co-located cases); otherwise wait for the (raised) timeout.
            //
            // Fix BS: timeout is now distance-scaled (computed at OnEnter, stored in
            // _anchorSyncPauseTimeoutSec). Log-98 evidence: the flat 3.0s timeout was
            // insufficient — 3 of 9 events timed out with assist still 19-41y away.
            float distToAnchor = assistStateStore.GetNearestAssistDistanceYards(_anchorPos);
            bool assistNearby = distToAnchor <= AnchorSyncReadyYards;
            bool assistNavigating = assistStateStore.AnyAssistNavigating();
            bool assistFollowing = assistStateStore.AnyAssistIsFollowing();
            bool assistReady = assistNearby && (assistNavigating || assistFollowing);

            if (assistReady || elapsed >= _anchorSyncPauseTimeoutSec)
            {
                _anchorSyncPauseActive = false;
                // ── Fix DK (log-127 22:40:30) ── The sync-pause is an
                // INTENTIONAL hold (the leader stands at its approach anchor
                // waiting for the assist), but ApproachDurationMs accrues during
                // it, so nextStuckCheckTime is already "due" the instant the
                // pause ends. The very next NonCombatApproach then samples
                // !bits.Moving() (line ~814) ~1ms after the leader presses
                // Approach — before it has begun moving — and falsely logs
                // "Seems stuck! Attempting pather escape," triggering an escape
                // the leader never needed (it was never wedged on anything).
                // Re-arm the not-moving check so the leader gets a fresh
                // STUCK_INTERVAL_MS (400ms) to begin the interact-walk / react
                // to a too-far UI error before the instantaneous check can fire.
                // The 3s range-progress check below remains the real backstop
                // for a genuine stall, so this only defers a true stuck by ≤400ms.
                SetNextStuckTimeCheck();
                logger.LogInformation(
                    $"[ATG] [FIX-FIRE] BN/BS: Anchor sync-pause complete: distToAnchor={distToAnchor:0.0}y " +
                    $"(threshold={AnchorSyncReadyYards}y), assistNavigating={assistNavigating} " +
                    $"assistFollowing={assistFollowing} elapsed={elapsed:0.2}s " +
                    $"(timeout={_anchorSyncPauseTimeoutSec:0.0}s) — beginning interact approach.");
            }
            else
            {
                // Hold — don't press interact yet. Give the assist time to detect the
                // anchor via the API poll and begin navigating toward it so both bots
                // start the final interact-key close from the same geographic position.
                wait.Update();
                return;
            }
        }

        if (classConfig.Mode == Mode.PartyLeader &&
            !assistStateStore.AnyAssistIsFollowing() &&
            !assistStateStore.AnyAssistNavigating())
        {
            logger.LogInformation("ApproachTargetGoal: Not approaching — assist is not following or navigating.");
            return;
        }

        if (bits.Combat() && !bits.Target_Combat() &&
            !combatLog.ToPull.Contains(playerReader.TargetGuid))
        {
            stopMoving.Stop();
            LogPreventExtraPull(logger);
            input.PressClearTarget();
            wait.Update();
            combatTracker.AcquiredTarget(5000);
            return;
        }

        if (!input.Approach.OnCooldown() && (!bits.SoftInteract() || HasValidSoftInteract()))
        {
            if (navigation.IsApproachEscapeActive)
            {
                navigation.Update(CancellationToken.None);
                if (navigation.IsApproachEscapeActive)
                {
                    if (!navigation.TryUnstuck())
                    {
                        logger.LogInformation(
                            $"[ATG] TryUnstuck returned false — " +
                            $"guid={navigation.ApproachEscapeTargetGuid} yards={navigation.ApproachEscapeCurrentYards:0} " +
                            $"exhausted={navigation.IsApproachEscapeExhausted} escalating={navigation.IsApproachEscapeEscalating} " +
                            $"physStuck={navigation.IsApproachEscapePhysicallyStuck} " +
                            $"startUtc={navigation.ApproachEscapeStartUtc:HH:mm:ss.fff}");
                        if (navigation.IsApproachEscapePhysicallyStuck)
                        {
                            navigation.IsApproachEscapePhysicallyStuck = false;
                            logger.LogWarning("[ATG] Physically trapped in terrain — injecting jump + reverse to escape.");
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

                        approachStart = GetTimestamp();
                        initialMinRange = float.MaxValue;
                        _rangeStuckLastMinRange = playerReader.MinRange();
                        _rangeStuckCheckAtMs = ApproachDurationMs + RangeStuckIntervalMs;
                    }
                }
                else
                {
                    logger.LogInformation(
                        $"[ATG] Escape complete — resetting approach timers. " +
                        $"range={playerReader.MinRange():0.0}y exhausted={navigation.IsApproachEscapeExhausted} escalating={navigation.IsApproachEscapeEscalating}");
                    approachStart = GetTimestamp();
                    initialMinRange = float.MaxValue;
                    _rangeStuckLastMinRange = playerReader.MinRange();
                    _rangeStuckCheckAtMs = ApproachDurationMs + RangeStuckIntervalMs;
                }
                return;
            }

            logger.LogInformation("!input.Approach.OnCooldown(): " + !input.Approach.OnCooldown());
            logger.LogInformation("!bits.SoftInteract(): " + !bits.SoftInteract());
            logger.LogInformation("HasValidSoftInteract(): " + HasValidSoftInteract());

            if (classConfig.Mode == Mode.AssistFocus && !targetInBlacklist)
            {
                bool foundCrowdControlAction = false;
                string? raidIconRequirement = null;
                int targetRaidIcon = classConfig.RaidIconsToSkipInCombat
                    .IndexOf(playerReader.TargetRaidIcon());

                if(targetRaidIcon == 5) raidIconRequirement = "HasMoonIcon";
                else if(targetRaidIcon == 6) raidIconRequirement = "HasSquareIcon";
                else if (targetRaidIcon == 7) raidIconRequirement = "HasCrossIcon";

                if(raidIconRequirement != null)
                {
                    Keys = classConfig.Combat.Sequence;
                    ReadOnlySpan<KeyAction> span = Keys;
                    for (int i = 0; i < span.Length; i++)
                    {
                        KeyAction keyAction = span[i];
                        if (keyAction.CrowdControl && keyAction.Requirements.Contains(raidIconRequirement))
                        {
                            foundCrowdControlAction = true;
                            break;
                        }
                    }
                }

                if (foundCrowdControlAction || classConfig.AssistApproach)
                {
                    navigation.RecordApproachPosition(playerReader.WorldPos);
                    input.PressApproach();
                    wait.Update();
                }
                else
                {
                    // ── Fix CT (log-118 evidence: 138 PageUp + 138 F presses) ──
                    // The default-approach else branch unconditionally pressed
                    // PressTargetFocus + PressTargetOfTarget every ATG.Update
                    // iteration. After AH+AJ+AN has acquired the leader's
                    // hostile target (one PressTargetFocus + PressTargetOfTarget
                    // pair from FFG), subsequent ATG iterations should not need
                    // to re-acquire — the assist's target IS the leader's
                    // target and is hostile. Re-pressing the chain on every
                    // iteration is wasteful keystroke spam and produces the
                    // user-observed "increase in Target Focus / Target Focus
                    // Target presses during the time the leader acquires a
                    // target and before they pull it."
                    //
                    // Log-118 evidence:
                    //   - 138 [PageUp] (TargetFocus) + 138 [F] (TargetOfTarget) presses
                    //     in an 11-minute log.
                    //   - 56 of those came from FFG's AH+AJ+AN one-shot
                    //     (55 confirmed-hostile latches + 1 retry warning).
                    //   - The remaining 82 came from this ATG else branch
                    //     firing ~3–4 iterations per ATG.OnEnter (31 entries),
                    //     each iteration re-pressing the chain.
                    //
                    // Worked example (02:33:01 burst, 5 PageUps in 1.5s):
                    //   02:33:01:029 FFG AH+AJ+AN fires (1st PageUp+F)
                    //   02:33:01:152 AJ latches hostile (guid=971418)
                    //   02:33:01:299 Plan → ATG; ATG.Update begins iterating
                    //   02:33:01:444 ATG iter #1 → PageUp+F (target already hostile)
                    //   02:33:01:999 ATG iter #2 → PageUp+F (target STILL hostile)
                    //   02:33:02:555 ATG iter #3 → PageUp+F (target STILL hostile)
                    //   → 4 redundant PageUp+F pairs after the first acquisition.
                    //
                    // Skip the chain when the bot is verified to already be on
                    // the leader's current target. "Hostile and present" is not
                    // a sufficient gate — the bot's target may have drifted via
                    // the Tab key press at the end of each iteration (Tab fires
                    // TargetNearestTarget; if a closer hostile enters range it
                    // can switch the bot off the leader's target). We need a
                    // POSITIVE confirmation that the bot is on the leader's
                    // current target before skipping the resync.
                    //
                    // The check uses three facts the assist can read locally:
                    //   - bits.Target() && bits.Target_Hostile(): bot has a
                    //     currently-hostile target.
                    //   - bits.FocusTarget(): the focus (= leader) currently
                    //     has a target. If FocusTarget is false, the leader's
                    //     target was lost/cleared and we should resync.
                    //   - playerReader.TargetGuid == playerReader.FocusTargetGuid:
                    //     the bot's target guid matches the focus's target
                    //     guid — i.e., the bot IS on the leader's target.
                    //
                    // If any of these fail, the focus chain fires to resync.
                    // This is the same chain semantic as the original code,
                    // just gated against a positive verification.
                    //
                    // Also handles the leader-retarget edge case: if the
                    // leader switches target mid-ATG (e.g., the original mob
                    // dies, leader pulls a new mob), FocusTargetGuid changes
                    // immediately; the bot's stale TargetGuid no longer
                    // matches, the gate fails, and the chain runs to re-sync
                    // to the new target.
                    //
                    // PressApproach is always pressed — that's the
                    // interact-key drive separate from targeting.
                    bool botOnFocusTarget =
                        bits.Target()
                        && bits.Target_Hostile()
                        && bits.FocusTarget()
                        && playerReader.TargetGuid == playerReader.FocusTargetGuid
                        && playerReader.TargetGuid != 0;
                    if (!botOnFocusTarget)
                    {
                        input.PressTargetFocus();
                        input.PressTargetOfTarget();
                        wait.Update();
                    }
                    navigation.RecordApproachPosition(playerReader.WorldPos);
                    input.PressApproach();
                    wait.Update();
                }
            }
            else
            {
                if (!navigation.IsApproachEscapeActive && !navigation.IsApproachEscapeEscalating && !bits.Combat() && (targetInBlacklist || navigation.IsInBlacklistArea()))
                {
                    logger.LogWarning($"Losing the target due blacklist!");
                    // Edit 3: pre-engagement bail-out — clear target without
                    // blacklisting (we never engaged). Note the !bits.Combat()
                    // guard above: we are explicitly not engaged here.

                    input.PressStopAttack();
                    input.PressClearTarget();
                    wait.Update();
                    stopMoving.StopForward();
                    navigation.Stop();
                    navigation.ResetApproachEscape();
                    wait.Update(playerReader.DoubleNetworkLatency);
                    wait.Update();
                    return;
                }

                navigation.RecordApproachPosition(playerReader.WorldPos);
                input.PressApproach();
                wait.Update();
            }
        }

        if (!bits.Combat() && !targetInBlacklist)
        {
            if (navigation.IsApproachEscapeActive)
            {
                navigation.Update(CancellationToken.None);
                if (navigation.IsApproachEscapeActive)
                {
                    if (!navigation.TryUnstuck())
                    {
                        approachStart = GetTimestamp();
                        initialMinRange = float.MaxValue;
                        _rangeStuckLastMinRange = playerReader.MinRange();
                        _rangeStuckCheckAtMs = ApproachDurationMs + RangeStuckIntervalMs;
                    }
                }
                else
                {
                    approachStart = GetTimestamp();
                    initialMinRange = float.MaxValue;
                    _rangeStuckLastMinRange = playerReader.MinRange();
                    _rangeStuckCheckAtMs = ApproachDurationMs + RangeStuckIntervalMs;
                }
                return;
            }

            NonCombatApproach();
            RandomJump();
        }
    }

    private void NonCombatApproach()
    {
        if (ApproachDurationMs >= nextStuckCheckTime)
        {
            SetNextStuckTimeCheck();

            if (!bits.Moving())
            {
                if (playerReader.LastUIError is
                    UI_ERROR.ERR_AUTOFOLLOW_TOO_FAR or UI_ERROR.ERR_BADATTACKPOS)
                {
                    playerReader.LastUIError = UI_ERROR.NONE;
                    Log($"Target is too far({playerReader.MinRange()} yard) for interact, start moving forward!");
                    input.StartForward(false);
                    return;
                }
                else if (playerReader.LastUIError == UI_ERROR.ERR_ATTACK_PACIFIED)
                {
                    playerReader.LastUIError = UI_ERROR.NONE;
                    if (mountHandler.IsMounted())
                    {
                        mountHandler.Dismount();
                        wait.While(bits.Falling);
                        input.PressInteract();
                        wait.Update();
                        SetNextStuckTimeCheck();
                        return;
                    }
                }

                Log($"Seems stuck! Attempting pather escape.");
                navigation.TryUnstuck();
                wait.Update();
                return;
            }
        }

        if (ApproachDurationMs >= _rangeStuckCheckAtMs && !navigation.IsApproachEscapeActive)
        {
            float currentRange = playerReader.MinRange();
            // ── Fix FF Q1-B (audit followup, mirrors Fix DS in PTG lines 497, 532) ──
            // MinRange()==0 is the sentinel for "target not in any detectable
            // range bracket" (out of every range check, target dead, or
            // LOS-blocked), NOT a real 0y distance. Without this guard the
            // comparison reads currentRange=0 and _rangeStuckLastMinRange=0
            // (or float.MaxValue freshly demoted to 0 via line 1013-1015), then
            // 0 >= 0 yields TRUE and TryUnstuck fires spuriously. The
            // run-131 13:42:03 PTG side of this bug is what motivated Fix
            // DS; the equivalent ATG range-progress check (line 991 below)
            // carried the same defect uncovered by the Q1 audit. Treat 0
            // the same way Fix DS treats it: defer to the next interval
            // rather than fire-and-add-rect.
            //
            // Pre-Fix-FE this was catastrophic (clobber → unreachable
            // projection → deadlock). Post-Fix-FE the bot wastes ~30 s
            // per spurious fire and the resulting rects accumulate.
            if (currentRange <= 0f)
            {
                _rangeStuckCheckAtMs = ApproachDurationMs + RangeStuckIntervalMs;
            }
            else if (currentRange >= _rangeStuckLastMinRange)
            {
                Log($"No range progress after {RangeStuckIntervalMs}ms ({_rangeStuckLastMinRange:0.0} -> {currentRange:0.0}y) — attempting pather escape. " +
                    $"escalating={navigation.IsApproachEscapeEscalating} exhausted={navigation.IsApproachEscapeExhausted}");
                _rangeStuckCheckAtMs = ApproachDurationMs + RangeStuckIntervalMs;
                bool stillActive = navigation.TryUnstuck();
                if (!stillActive)
                {
                    approachStart = GetTimestamp();
                    initialMinRange = float.MaxValue;
                    _rangeStuckLastMinRange = playerReader.MinRange();
                    _rangeStuckCheckAtMs = ApproachDurationMs + RangeStuckIntervalMs;
                }
                wait.Update();
                return;
            }
            else
            {
                _rangeStuckLastMinRange = currentRange;
            }
            _rangeStuckCheckAtMs = ApproachDurationMs + RangeStuckIntervalMs;
        }
        else if (_rangeStuckLastMinRange == float.MaxValue)
        {
            _rangeStuckLastMinRange = playerReader.MinRange();
        }

        if (initialMinRange == float.MaxValue)
            initialMinRange = playerReader.MinRange();

        if (ApproachDurationMs > MAX_APPROACH_DURATION_MS)
        {
            logger.LogWarning("Too long time. Attempting pather escape.");
            if (!navigation.IsApproachEscapeActive)
            {
                input.PressClearTarget();
                navigation.TryUnstuck();
            }
            wait.Update();
            return;
        }

        if (playerReader.TargetGuid == initialTargetGuid &&
            !playerReader.IsInMeleeRange() &&
            !navigation.IsApproachEscapeActive &&
            !navigation.IsApproachEscapeEscalating)
        {
            int initialTargetMinRange = playerReader.MinRange();
            if (!input.TargetNearestTarget.OnCooldown())
            {
                input.PressNearestTarget();
                wait.Update();
            }

            if (bits.Target() && playerReader.TargetGuid != initialTargetGuid)
            {
                if (!navigation.IsApproachEscapeActive && !navigation.IsApproachEscapeEscalating && (targetBlacklist.Is() || navigation.IsInBlacklistArea()))
                {
                    logger.LogWarning($"Losing the target due blacklist!");
                    // Edit 3: pre-engagement bail-out (target swapped mid-
                    // approach to a blacklisted one OR bot wandered into a
                    // rect) — clear target without blacklisting.

                    input.PressStopAttack();
                    input.PressClearTarget();
                    wait.Update();
                    stopMoving.StopForward();
                    navigation.Stop();
                    navigation.ResetApproachEscape();
                    wait.Update(playerReader.DoubleNetworkLatency);
                    wait.Update();
                    return;
                }

                if (playerReader.MinRange() < initialTargetMinRange)
                {
                    logger.LogWarning($"Found a closer target! {playerReader.MinRange()} < {initialTargetMinRange}");
                    initialMinRange = playerReader.MinRange();
                    _rangeStuckLastMinRange = playerReader.MinRange();
                    _rangeStuckCheckAtMs = ApproachDurationMs + RangeStuckIntervalMs;
                }
                else
                {
                    initialTargetGuid = -1;
                    logger.LogWarning("Stick to initial target!");
                    input.PressLastTarget();
                    wait.Update();
                }
            }
        }

        const float GoingAwayBuffer = 6f;
        if (ApproachDurationMs > MIN_TIME_TILL_IDLE &&
            initialMinRange != float.MaxValue &&
            playerReader.MinRange() > initialMinRange + GoingAwayBuffer)
        {
            Log($"Going away from the target! {initialMinRange} < {playerReader.MinRange()}");
            if (!navigation.IsApproachEscapeActive)
            {
                input.PressClearTarget();
                wait.Update();
            }
        }
    }

    private void SetNextStuckTimeCheck()
    {
        nextStuckCheckTime = ApproachDurationMs + STUCK_INTERVAL_MS;
    }

    private void RandomJump()
    {
        if (ApproachDurationMs > MIN_TIME_TILL_IDLE &&
            input.Jump.SinceLastClickMs > Random.Shared.Next(5000, 25_000))
        {
            input.PressJump();
            wait.Update();
        }
    }

    private bool HasValidSoftInteract()
    {
        return
            bits.SoftInteract() &&
            !bits.SoftInteract_Dead() &&
            !bits.SoftInteract_Tagged() &&
            playerReader.SoftInteract_Type == GuidType.Creature;
    }

    private void Log(string text) => logger.LogDebug(text);

    #region Logging

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Warning,
        Message = "Clear current target as not in combat!")]
    static partial void LogPreventExtraPull(ILogger logger);

    #endregion
}
