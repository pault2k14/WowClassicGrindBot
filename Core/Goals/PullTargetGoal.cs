using Core.GOAP;

using Microsoft.Extensions.Logging;

using SharedLib.NpcFinder;

using System;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.Goals;

public sealed class PullTargetGoal : GoapGoal, IGoapEventListener
{
    public override float Cost => 7f;

    private const int AcquireTargetTimeMs = 5000;
    private const int MAX_PULL_DURATION = 15_000;
    // How long without range reduction before firing TryUnstuck, even if stuckDetector
    // thinks we're moving. Matches ATG's RangeStuckIntervalMs. Catches the "shuffling
    // sideways against terrain" case: the bot IS moving but not toward the mob.
    private const double RangeStuckIntervalMs = 3000;

    private readonly ILogger<PullTargetGoal> logger;
    private readonly ConfigurableInput input;
    private readonly ClassConfiguration classConfig;
    private readonly Wait wait;
    private readonly CombatLog combatLog;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly StopMoving stopMoving;
    private readonly StuckDetector stuckDetector;
    private readonly NpcNameTargeting npcNameTargeting;
    private readonly CastingHandler castingHandler;
    private readonly IMountHandler mountHandler;
    private readonly CombatTracker combatTracker;
    private readonly IBlacklist targetBlacklist;
    private readonly ChatReader chatReader;
    private readonly Navigation navigation;

    private readonly KeyAction? approachKey;
    private readonly Action approachAction;
    
    private readonly bool requiresNpcNameFinder;

    private long pullStart;
    private float _rangeStuckLastMinRange;
    private double _rangeStuckCheckAtMs;

    // Set by OnGoapEvent when GoapAgent broadcasts evadeRecovery=true.
    // Checked at the top of Update() to force an immediate exit.
    private bool _evadeRecoveryActive;

    private double PullDurationMs => GetElapsedTime(pullStart).TotalMilliseconds;

    public PullTargetGoal(ILogger<PullTargetGoal> logger, ConfigurableInput input,
        Wait wait, CombatLog combatlog, PlayerReader playerReader,
        AddonBits bits,
        IBlacklist targetBlacklist,
        StopMoving stopMoving, CastingHandler castingHandler,
        IMountHandler mountHandler, NpcNameTargeting npcNameTargeting,
        StuckDetector stuckDetector, CombatTracker combatTracker,
        ClassConfiguration classConfig, ChatReader chatReader,
        Navigation navigation)
        : base(nameof(PullTargetGoal))
    {
        this.logger = logger;
        this.input = input;
        this.wait = wait;
        this.combatLog = combatlog;
        this.playerReader = playerReader;
        this.bits = bits;
        this.stopMoving = stopMoving;
        this.castingHandler = castingHandler;
        this.mountHandler = mountHandler;
        this.npcNameTargeting = npcNameTargeting;
        this.stuckDetector = stuckDetector;
        this.combatTracker = combatTracker;
        this.targetBlacklist = targetBlacklist;
        this.classConfig = classConfig;
        this.chatReader = chatReader;
        this.navigation = navigation;

        Keys = classConfig.Pull.Sequence;

        approachAction = DefaultApproach;

        for (int i = 0; i < Keys.Length; i++)
        {
            KeyAction keyAction = Keys[i];

            if (keyAction.Name.Equals(input.Approach.Name, StringComparison.OrdinalIgnoreCase))
            {
                approachAction = ConditionalApproach;
                approachKey = keyAction;
            }

            if (keyAction.Requirements.Contains(RequirementFactory.AddVisible))
            {
                requiresNpcNameFinder = true;
            }
        }

        if (classConfig.Mode != Mode.AssistFocus)
        {
            AddPrecondition(GoapKey.targettargetsus, false);
        }

        AddPrecondition(GoapKey.forcedfollow, false);
        AddPrecondition(GoapKey.hastarget, true);
        AddPrecondition(GoapKey.targetisalive, true);
        AddPrecondition(GoapKey.targethostile, true);
        AddPrecondition(GoapKey.withinpullrange, true);
        AddPrecondition(GoapKey.inblacklistarea, false);
        AddPrecondition(GoapKey.assistrequestreturn, false);
        // Lock out pull during evade recovery so the leader doesn't immediately
        // pull a new mob while both bots are still navigating away.
        AddPrecondition(GoapKey.evadeRecovery, false);
        // Prevents PTG from being selected while ATG owns an active pather escape.
        // Without this the planner thrashes between ATG and PTG every tick during
        // escape navigation since both goals have their other preconditions met.
        // ATG has no equivalent precondition so it retains escape ownership cleanly.
        AddPrecondition(GoapKey.approachEscapeActive, false);

        AddEffect(GoapKey.pulled, true);
        this.chatReader = chatReader;
    }

    public override void OnEnter()
    {
        wait.Update();
        stuckDetector.Reset();

        // BUG 5 FIX: mirror ATG's wrong-mob escape guard.
        // Evidence: Run 4 line 721 — PTG entered with "escape ACTIVE" for guid=7299149
        // while target=7305883. No guard existed to stop the stale escape, so PTG drove
        // the wrong-mob navigation and the bot engaged a different mob entirely.
        // ATG has this guard (ApproachTargetGoal.cs lines 145-151); PTG did not.
        if (navigation.IsApproachEscapeActive &&
            playerReader.TargetGuid != 0 &&
            navigation.ApproachEscapeTargetGuid != 0 &&
            navigation.ApproachEscapeTargetGuid != playerReader.TargetGuid)
        {
            logger.LogWarning($"[PTG] OnEnter: stopping wrong-mob escape " +
                $"(escapeGuid={navigation.ApproachEscapeTargetGuid} ≠ target={playerReader.TargetGuid}) — calling Stop().");
            navigation.Stop();
        }

        if (!navigation.IsApproachEscapeActive)
        {
            // [LOG-14a] Pre-RATF state snapshot — mirrors ATG [LOG-11].
            logger.LogDebug($"[PTG] OnEnter: calling RATF for target={playerReader.TargetGuid} — " +
                $"pre-RATF state: escapeGuid={navigation.ApproachEscapeTargetGuid} " +
                $"yards={navigation.ApproachEscapeCurrentYards:0} active={navigation.IsApproachEscapeActive} " +
                $"exhausted={navigation.IsApproachEscapeExhausted} escalating={navigation.IsApproachEscapeEscalating}");
            navigation.ResetApproachEscapeForTarget(playerReader.TargetGuid);
        }
        else
        {
            // [LOG-14b] BUG D diagnostic: escape is active so RATF is skipped.
            // If the guid/startUtc here are stale (from a prior escape), this log exposes it.
            logger.LogInformation($"[PTG] OnEnter: escape ACTIVE — skipping RATF. " +
                $"yards={navigation.ApproachEscapeCurrentYards:0} " +
                $"startUtc={navigation.ApproachEscapeStartUtc:HH:mm:ss.fff} " +
                $"lastAttemptAge={(DateTime.UtcNow - navigation.ApproachEscapeLastAttemptUtc).TotalMilliseconds:0}ms " +
                $"guid={navigation.ApproachEscapeTargetGuid} target={playerReader.TargetGuid} " +
                $"exhausted={navigation.IsApproachEscapeExhausted} escalating={navigation.IsApproachEscapeEscalating}");
        }
        _rangeStuckLastMinRange = float.MaxValue;

        if (mountHandler.IsMounted())
        {
            mountHandler.Dismount();
        }

        if (Keys.Length != 0 && !input.StopAttack.OnCooldown())
        {
            Log("Stop auto interact!");
            input.PressStopAttack();
            wait.Update();
            // Temporarily disable due to navigation supression updates
            stopMoving.StopForward();
            wait.Update(playerReader.DoubleNetworkLatency);
            wait.Update();
        }

        if (requiresNpcNameFinder)
        {
            npcNameTargeting.ChangeNpcType(NpcNames.Enemy);
        }

        pullStart = GetTimestamp();
        _rangeStuckCheckAtMs = PullDurationMs + RangeStuckIntervalMs;
        input.PressDisableSoftInteract();
        wait.Update();
    }

    public override void OnExit()
    {
        // RATF intentionally omitted — see ATG.OnExit comment.

        if (requiresNpcNameFinder)
        {
            npcNameTargeting.ChangeNpcType(NpcNames.None);
        }

        // Added this to stop moving when combat starts
        // should we be checking for combat?
        if(bits.Target() && bits.Target_Alive() && bits.Combat())
        {
            input.StopForward(false);
        }
        
    }

    public void OnGoapEvent(GoapEventArgs e)
    {
        if (e.GetType() == typeof(ResumeEvent))
        {
            pullStart = GetTimestamp();
            _rangeStuckLastMinRange = float.MaxValue;
            _rangeStuckCheckAtMs = PullDurationMs + RangeStuckIntervalMs;
        }
        else if (e is GoapStateEvent s && s.Key == GoapKey.evadeRecovery)
        {
            _evadeRecoveryActive = s.Value;
        }
    }

    public override void Update()
    {
        wait.Update();

        if (bits.Drowning())
        {
            input.PressJump();
        }

        if (chatReader.ForcedFollow)
        {
            AddEffect(GoapKey.forcedfollow, true);
            return;
        }

        // If an evading mob was detected, abort the pull immediately so the
        // planner re-evaluates. The evadeRecovery precondition prevents re-selection.
        if (_evadeRecoveryActive)
        {
            logger.LogInformation("[PullTargetGoal] Evade recovery active — aborting pull.");
            input.PressStopAttack();
            wait.Update();
            input.PressClearTarget();
            wait.Update();
            stopMoving.Stop();
            return;
        }

        // Assist-side evade handling while mid-pull.
        if (classConfig.Mode == Mode.AssistFocus && chatReader.LeaderBlacklistTarget)
        {
            int blacklistGuid = chatReader.LeaderBlacklistTargetId;
            chatReader.LeaderBlacklistTarget = false;
            chatReader.LeaderBlacklistTargetId = 0;

            if (blacklistGuid != 0)
            {
                logger.LogInformation($"[PullTargetGoal] Leader blacklisted guid={blacklistGuid} while pulling — ignoring and exiting.");
                playerReader.IgnoreTarget(blacklistGuid);
            }

            input.PressStopAttack();
            wait.Update();
            input.PressClearTarget();
            wait.Update();
            stopMoving.Stop();

            // Fire EvadeBlacklistEvent so the assist's own GoapAgent starts its
            // evadeRecovery timer, blocking Combat/Approach/Pull on the assist side.
            if (blacklistGuid != 0)
                SendGoapEvent(new EvadeBlacklistEvent(blacklistGuid));

            if (!bits.AutoFollow())
            {
                // Set AssistRequestReturn=true immediately so FollowFocusGoal is
                // selectable right now, without waiting ~1.5s for the N5 chat echo.
                // The N5 press delivers real coordinates to the leader.
                chatReader.AssistRequestReturn = true;
                // Press N5 (AssistCantFollow) — sends position to leader, sets
                // AssistRequestReturn=true on both bots, selects FollowFocusGoal on assist.
                input.PressAssistCantFollow();
            }
            return;
        }

        // Skip the blacklist bail-out while an escape is in progress — the mob being
        // in the blacklist area is exactly WHY the escape was triggered. Bailing here
        // would abort the escape route before the character has navigated clear of the
        // obstacle. Once the escape completes (or all levels exhaust), this check fires
        // and PTG bails. IsApproachEscapeExhausted triggers immediately when all 3 levels
        // fail so the mob gets blacklisted without needing the player inside the rect.
        if (!navigation.IsApproachEscapeActive &&
            bits.Target() && !bits.Combat() &&
            (navigation.IsApproachEscapeExhausted ||
             (!navigation.IsApproachEscapeEscalating &&
              (targetBlacklist.Is() || navigation.IsInBlacklistArea()))))
        {
            string ptgBailReason = navigation.IsApproachEscapeExhausted
                ? "all escape levels exhausted (10y/20y/30y failed)"
                : targetBlacklist.Is() ? "target in blacklist" : "player inside blacklist area";
            logger.LogWarning($"[PTG] Bail-out: blacklisting target guid={playerReader.TargetGuid} — reason: {ptgBailReason}.");
            // Broadcast to assist so they also ignore + clear this mob.
            if (classConfig.Mode == Mode.PartyLeader && playerReader.TargetGuid != 0)
                SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid));
            playerReader.IgnoreTarget(playerReader.TargetGuid);
            input.PressStopAttack();
            input.PressClearTarget();
            wait.Update();
            stopMoving.StopForward();
            // Clear any in-progress escape navigation so FRG doesn't inherit a
            // stale escape waypoint pointing away from the patrol route.
            navigation.Stop();
            navigation.ResetApproachEscape();
            navigation.ClearStuckRects();
            wait.Update(playerReader.DoubleNetworkLatency);
            wait.Update();
            return;
        }

        if (!bits.Combat() && !chatReader.AssistIsFollowing && classConfig.Mode == Mode.PartyLeader)
        {
            logger.LogInformation("PullTargetGoal: Not pulling due to assist not following");
            return;
        }

        // Skip pull-duration timeout entirely while an escape is running or
        // escalating — the navigation owns movement, pressing StopAttack or
        // clearing the target mid-escape causes a tight spam loop and breaks
        // the escape sequence.
        if (PullDurationMs > MAX_PULL_DURATION &&
            !navigation.IsApproachEscapeActive &&
            !navigation.IsApproachEscapeEscalating)
        {
            input.PressStopAttack();
            input.PressClearTarget();
            navigation.TryUnstuck();
            Log("Pull taking too long. Clear target and attempting unstuck.");
            return;
        }

        if (classConfig.AutoPetAttack &&
            bits.Pet() &&
            (!playerReader.PetTarget() ||
            playerReader.TargetGuid != playerReader.PetTargetGuid) &&
            !input.PetAttack.OnCooldown())
        {
            input.PressStopAttack();
            input.PressPetAttack();
        }

        bool castAny = false;
        bool spellInQueue = false;

        ReadOnlySpan<KeyAction> keys = Keys;
        for (int i = 0; i < keys.Length; i++)
        {
            KeyAction keyAction = keys[i];

            if (classConfig.RaidIconsToSkipInCombat.IndexOf(playerReader
                .TargetRaidIcon()) != -1 && !keyAction.CrowdControl)
            {
                // None of our pull sequence had crowd control.
                if (i + 1 == keys.Length)
                {
                    return;
                }
                continue;
            }

            if (keyAction.Name.Equals(input.Approach.Name,
                StringComparison.OrdinalIgnoreCase))
                continue;

            if (!keyAction.CanRun())
                continue;

            spellInQueue = castingHandler.SpellInQueue();
            if (spellInQueue)
            {
                break;
            }

            bool interrupt() => keyAction.CanBeInterrupted() || PullPrevention();

            if (castAny = castingHandler.Cast(keyAction, interrupt))
            {
                castAny = !keyAction.BaseAction;
            }
            else if (PullPrevention() &&
                (playerReader.IsCasting() || bits.Any_AutoAttack()))
            {
                Log("Preventing pulling possible tagged target!");
                input.PressStopAttack();
                input.PressClearTarget();
                wait.Update();
                return;
            }
        }

        if (bits.Target() && combatLog.EvadeMobs.Contains(playerReader.TargetGuid))
        {
            Log("Evading mob");

            // Broadcast to assist so they also ignore + clear this evading mob.
            // This is the primary evade detection point — EvadeMobs is explicitly
            // populated by CombatLog when the server sends the UNIT_EVADED event.
            if (classConfig.Mode == Mode.PartyLeader && playerReader.TargetGuid != 0)
                SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid));
            playerReader.IgnoreTarget(playerReader.TargetGuid);
            input.PressStopAttack();
            input.PressClearTarget();
            wait.Update();
            return;
        }
        else if (bits.Target())
        {
            combatLog.ToPull.Add(playerReader.TargetGuid);
        }

        if (castAny || spellInQueue || playerReader.IsCasting() || (bits.AutoShot() && !playerReader.IsInMeleeRange()))
            return;

        approachAction();
    }

    private void DefaultApproach()
    {
        if (input.Approach.OnCooldown())
            return;

        // If a pather-based escape is in progress, drive Navigation rather than
        // pressing Approach. TryUnstuck() checks for completion and clears the
        // escape when the route finishes so normal approach can resume.
        if (navigation.IsApproachEscapeActive)
        {
            navigation.Update(CancellationToken.None);
            // Re-check after Update — StopAndResetAtDestination may have just
            // completed the escape. Only call TryUnstuck if still active (to
            // check the timeout), otherwise let normal approach resume next tick.
            if (navigation.IsApproachEscapeActive)
            {
                // [LOG-15] Periodic heartbeat while escape is active — emits at Debug level.
                // Provides escapeSec and displaced so we can see mid-escape progress without
                // waiting for completion or timeout. Throttled to ~1s to avoid log spam.
                if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                {
                    double escSec = (DateTime.UtcNow - navigation.ApproachEscapeStartUtc).TotalSeconds;
                    logger.LogDebug($"[PTG] DefaultApproach: escape heartbeat — " +
                        $"yards={navigation.ApproachEscapeCurrentYards:0} escapeSec={escSec:0.0}s " +
                        $"range={playerReader.MinRange():0.0}y guid={navigation.ApproachEscapeTargetGuid}");
                }
                navigation.TryUnstuck();
            }
            else
            {
                // Escape just completed — reset range tracker so the 3s window
                // starts fresh. Pre-escape range data is stale (player moved during
                // navigation) and would immediately trigger a false stuck detection.
                logger.LogInformation(
                    $"[PTG] Escape complete — resetting range-stuck tracker. " +
                    $"range={playerReader.MinRange():0.0}y exhausted={navigation.IsApproachEscapeExhausted} escalating={navigation.IsApproachEscapeEscalating}");
                _rangeStuckLastMinRange = playerReader.MinRange();
                _rangeStuckCheckAtMs = PullDurationMs + RangeStuckIntervalMs;
            }
            return;
        }

        if (!bits.SoftInteract() || EligibleEnemySoftTargetExists())
        {
            navigation.RecordApproachPosition(playerReader.WorldPos);
            input.PressApproach();
            wait.Update();
        }
        else
        {
            logger.LogInformation("Can't input.PressApproach()");
            logger.LogInformation("!bits.SoftInteract(): " + !bits.SoftInteract());
            logger.LogInformation("EligibleEnemySoftTargetExists(): " + EligibleEnemySoftTargetExists());
        }

        // Range-progress stuck detection: even if stuckDetector.IsMoving() is true
        // (bot is physically moving — sliding sideways against terrain), if MinRange
        // hasn't decreased after RangeStuckIntervalMs the character is not making
        // progress toward the mob. Fire TryUnstuck to route around the obstacle.
        if (PullDurationMs >= _rangeStuckCheckAtMs && !navigation.IsApproachEscapeActive)
        {
            float currentRange = playerReader.MinRange();
            if (currentRange >= _rangeStuckLastMinRange)
            {
                logger.LogInformation(
                    $"[PTG] No range progress after {RangeStuckIntervalMs:0}ms " +
                    $"({_rangeStuckLastMinRange:0.0} -> {currentRange:0.0}y) — attempting pather escape. " +
                    $"escalating={navigation.IsApproachEscapeEscalating} exhausted={navigation.IsApproachEscapeExhausted}");
                _rangeStuckCheckAtMs = PullDurationMs + RangeStuckIntervalMs;
                navigation.TryUnstuck();
            }
            else
            {
                _rangeStuckLastMinRange = currentRange;
                _rangeStuckCheckAtMs = PullDurationMs + RangeStuckIntervalMs;
            }
        }
        else if (_rangeStuckLastMinRange == float.MaxValue)
        {
            // First approach tick — seed the initial range snapshot.
            _rangeStuckLastMinRange = playerReader.MinRange();
        }

        if (!stuckDetector.IsMoving())
        {
            // [LOG-16] Richer context on stuckDetector path — confirms whether the call is
            // expected (escalating=true, yards>0) or a spurious fire after escape completion.
            // lastAttemptAge confirms the 200ms gate state.
            logger.LogInformation(
                $"[PTG] Not moving — calling TryUnstuck(). " +
                $"escalating={navigation.IsApproachEscapeEscalating} exhausted={navigation.IsApproachEscapeExhausted} " +
                $"yards={navigation.ApproachEscapeCurrentYards:0} range={playerReader.MinRange():0.0}y " +
                $"lastAttemptAge={(DateTime.UtcNow - navigation.ApproachEscapeLastAttemptUtc).TotalMilliseconds:0}ms " +
                $"stuckRects={navigation.StuckRectCount}");
            navigation.TryUnstuck();
        }
    }

    private void ConditionalApproach()
    {
        if (approachKey == null ||
            (!approachKey.CanRun() && !approachKey.OnCooldown()))
        {
            logger.LogInformation("ConditionalApproach: stopMoving.Stop()");
            logger.LogInformation("approachKey: " + approachKey);
            logger.LogInformation("!approachKey.CanRun(): " + !approachKey.CanRun());
            logger.LogInformation("!approachKey.OnCooldown(): " + !approachKey.OnCooldown());

            stopMoving.Stop();
            return;
        }

        DefaultApproach();
    }

    private bool PullPrevention()
    {
        return !targetBlacklist.Is() ||
            playerReader.TargetTarget is
            UnitsTarget.None or
            UnitsTarget.Me or
            UnitsTarget.Pet or
            UnitsTarget.PartyOrPet;
    }

    private bool EligibleEnemySoftTargetExists() =>
        bits.SoftInteract() &&
        bits.SoftInteract_Hostile() &&
        !bits.SoftInteract_Dead() &&
        !bits.SoftInteract_Tagged() &&
        playerReader.SoftInteract_Type == GuidType.Creature;

    private void Log(string text)
    {
        logger.LogInformation(text);
    }
}
