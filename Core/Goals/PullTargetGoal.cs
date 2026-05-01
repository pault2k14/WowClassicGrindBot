using Core.GOAP;
using Core.Party;

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
    private readonly AssistStateStore assistStateStore;
    private readonly AssistStatusProvider assistStatusProvider;

    private readonly KeyAction? approachKey;
    private readonly Action approachAction;
    
    private readonly bool requiresNpcNameFinder;

    private long pullStart;
    private float _rangeStuckLastMinRange;
    private double _rangeStuckCheckAtMs;

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
        Navigation navigation,
        AssistStateStore assistStateStore,
        AssistStatusProvider assistStatusProvider)
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
        this.assistStateStore = assistStateStore;
        this.assistStatusProvider = assistStatusProvider;

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
        AddPrecondition(GoapKey.evadeRecovery, false);
        AddPrecondition(GoapKey.approachEscapeActive, false);

        AddEffect(GoapKey.pulled, true);
    }

    public override void OnEnter()
    {
        wait.Update();
        stuckDetector.Reset();

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
            logger.LogDebug($"[PTG] OnEnter: calling RATF for target={playerReader.TargetGuid} — " +
                $"pre-RATF state: escapeGuid={navigation.ApproachEscapeTargetGuid} " +
                $"yards={navigation.ApproachEscapeCurrentYards:0} active={navigation.IsApproachEscapeActive} " +
                $"exhausted={navigation.IsApproachEscapeExhausted} escalating={navigation.IsApproachEscapeEscalating}");
            navigation.ResetApproachEscapeForTarget(playerReader.TargetGuid);
        }
        else
        {
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
        if (requiresNpcNameFinder)
        {
            npcNameTargeting.ChangeNpcType(NpcNames.None);
        }

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

            if (blacklistGuid != 0)
                SendGoapEvent(new EvadeBlacklistEvent(blacklistGuid));

            if (!bits.AutoFollow())
            {
                // assistStatusProvider.CantFollow keeps assistshouldfollow=true so
                // FollowFocusGoal stays selectable throughout evade recovery.
                assistStatusProvider.CantFollow = true;
                input.PressAssistCantFollow();
            }
            return;
        }

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

        // Block pull on the leader if the assist is genuinely unavailable (not following
        // and not actively navigating toward us). Using the API store mirrors ATG's check.
        if (!bits.Combat() && classConfig.Mode == Mode.PartyLeader &&
            !assistStateStore.AnyAssistIsFollowing() &&
            !assistStateStore.AnyAssistNavigating())
        {
            logger.LogInformation("PullTargetGoal: Not pulling — assist is not following or navigating.");
            return;
        }

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

        if (navigation.IsApproachEscapeActive)
        {
            navigation.Update(CancellationToken.None);
            if (navigation.IsApproachEscapeActive)
            {
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
            _rangeStuckLastMinRange = playerReader.MinRange();
        }

        if (!stuckDetector.IsMoving())
        {
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
