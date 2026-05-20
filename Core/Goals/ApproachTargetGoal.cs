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
    private const float AnchorSyncReadyYards = 12.0f;
    private const double NearTimeoutSec = 1.5;   // Fix BS: assist within 12y at OnEnter
    private const double MidTimeoutSec  = 4.0;   // Fix BS: 12-30y at OnEnter
    private const double FarTimeoutSec  = 6.0;   // Fix BS: 30y+ at OnEnter (cap)
    private const float MidDistanceCutoffYards = 30.0f;  // Fix BS: boundary near→mid
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
            // Fix BN: save the anchor position for the distance-aware sync-pause check below.
            // Re-use playerReader.WorldPos here — same instant the anchor is published.
            _anchorPos = playerReader.WorldPos;
            leaderNavProvider.SetApproachStart(_anchorPos);
            logger.LogInformation($"[ATG] Published approach-start anchor: {_anchorPos}");

            // Fix BS: compute the timeout from initial assist distance. Close assist → short
            // wait (assist already in position, don't dawdle). Far assist → longer wait
            // (give assist time to actually close the gap). The cap (FarTimeoutSec) prevents
            // indefinite freezing when assist is truly unreachable.
            float distAtArm = assistStateStore.GetNearestAssistDistanceYards(_anchorPos);
            if (distAtArm <= AnchorSyncReadyYards)
                _anchorSyncPauseTimeoutSec = NearTimeoutSec;
            else if (distAtArm <= MidDistanceCutoffYards)
                _anchorSyncPauseTimeoutSec = MidTimeoutSec;
            else
                _anchorSyncPauseTimeoutSec = FarTimeoutSec;

            // Arm sync-pause: hold interact presses until the assist is actually near the
            // anchor (Fix BN: AnchorSyncReadyYards proximity) or the (scaled) timeout expires.
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

        if (classConfig.Mode == Mode.AssistFocus && chatReader.LeaderBlacklistTarget)
        {
            int blacklistGuid = chatReader.LeaderBlacklistTargetId;
            chatReader.LeaderBlacklistTarget = false;
            chatReader.LeaderBlacklistTargetId = 0;

            if (blacklistGuid != 0)
            {
                logger.LogInformation($"[ApproachTargetGoal] Leader blacklisted guid={blacklistGuid} while approaching — ignoring and exiting.");
                playerReader.IgnoreTarget(blacklistGuid);
            }

            input.PressStopAttack();
            wait.Update();
            input.PressClearTarget();
            wait.Update();
            input.StopForward(false);

            if (blacklistGuid != 0)
                SendGoapEvent(new EvadeBlacklistEvent(blacklistGuid));

            if (!bits.AutoFollow())
            {
                // assistStatusProvider.CantFollow keeps assistshouldfollow=true in GoapAgent
                // so FollowFocusGoal is immediately selectable throughout evade recovery,
                // even when dmgTaken/dmgDone flags would otherwise block it.
                //
                // Fix 28: removed the legacy input.PressAssistCantFollow()
                // call that used to follow this assignment. See the
                // explanatory block in CombatGoal.cs Fix 23 first-activation
                // branch (~ line 350) for the full rationale. Summary: the
                // chat-macro signal ("i tried following…") is dead code on
                // the leader side — GoapAgent.cs line 924-926 reads
                // exclusively from AssistStateStore.AnyAssistCantFollow()
                // (API path), and chatReader.AssistRequestReturn is no longer
                // read in Core/. The keypress wasted a NumPad5 binding,
                // generated visible in-game chat noise, and introduced a
                // ~1.7 s typing delay vs the 500 ms API publisher.
                assistStatusProvider.CantFollow = true;
            }
            return;
        }

        if (!navigation.IsApproachEscapeActive &&
            (navigation.IsApproachEscapeExhausted ||
             (!navigation.IsApproachEscapeEscalating && navigation.IsInBlacklistArea())))
        {
            string bail1Reason = navigation.IsApproachEscapeExhausted
                ? $"all escape levels exhausted (10y/20y/30y failed)"
                : $"player inside blacklist area";
            logger.LogWarning($"[ATG] Bail-out: blacklisting target guid={playerReader.TargetGuid} — reason: {bail1Reason}.");
            if (classConfig.Mode == Mode.PartyLeader && playerReader.TargetGuid != 0)
                SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid));
            playerReader.IgnoreTarget(playerReader.TargetGuid);
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
            logger.LogWarning($"[ATG] Bail-out: blacklisting target guid={playerReader.TargetGuid} — " +
                $"reason: {(targetInBlacklist ? "target in blacklist" : "player inside blacklist area")} " +
                $"[exhausted={navigation.IsApproachEscapeExhausted} escalating={navigation.IsApproachEscapeEscalating} yards={navigation.ApproachEscapeCurrentYards:0}].");

            if (navigation.IsInBlacklistArea())
            {
                if (classConfig.Mode == Mode.PartyLeader && playerReader.TargetGuid != 0)
                    SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid));
                playerReader.IgnoreTarget(playerReader.TargetGuid);
            }

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
                    input.PressTargetFocus();
                    input.PressTargetOfTarget();
                    wait.Update();
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
                    if (navigation.IsInBlacklistArea())
                    {
                        if (classConfig.Mode == Mode.PartyLeader && playerReader.TargetGuid != 0)
                            SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid));
                        playerReader.IgnoreTarget(playerReader.TargetGuid);
                    }

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
            if (currentRange >= _rangeStuckLastMinRange)
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
                    if (navigation.IsInBlacklistArea())
                    {
                        if (classConfig.Mode == Mode.PartyLeader && playerReader.TargetGuid != 0)
                            SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid));
                        playerReader.IgnoreTarget(playerReader.TargetGuid);
                    }

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
