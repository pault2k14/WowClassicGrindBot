using Core.GOAP;
using Core.Party;

using Microsoft.Extensions.Logging;

using System;
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

    private long approachStart;
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
        AssistStatusProvider assistStatusProvider)
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

        if(classConfig.Mode == Mode.PartyLeader)
        {
            AddPrecondition(GoapKey.assistisfollowing, true);
            AddPrecondition(GoapKey.assistrequestreturn, false);
        }

        if (classConfig.Mode == Mode.AssistFocus)
        {
            AddPrecondition(GoapKey.incombat, true);
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
    }

    public override void OnExit()
    {
        input.StopForward(false);
    }

    public override void Update()
    {
        wait.Update();

        if (bits.Drowning())
            input.PressJump();

        if (_evadeRecoveryActive)
        {
            logger.LogInformation("[ApproachTargetGoal] Evade recovery active — aborting approach.");
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
                assistStatusProvider.CantFollow = true;
                input.PressAssistCantFollow();
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
