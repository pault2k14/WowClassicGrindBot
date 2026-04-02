using Core.GOAP;

using Microsoft.Extensions.Logging;

using SharedLib.NpcFinder;

using System;

using static System.Diagnostics.Stopwatch;

namespace Core.Goals;

public sealed class PullTargetGoal : GoapGoal, IGoapEventListener
{
    public override float Cost => 7f;

    private const int AcquireTargetTimeMs = 5000;
    private const int MAX_PULL_DURATION = 15_000;

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

        AddEffect(GoapKey.pulled, true);
        this.chatReader = chatReader;
    }

    public override void OnEnter()
    {
        wait.Update();
        stuckDetector.Reset();

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
        input.PressDisableSoftInteract();
        wait.Update();
    }

    public override void OnExit()
    {
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

            // Set AssistRequestReturn=true immediately so FollowFocusGoal is
            // selectable right now, without waiting ~1.5s for the N5 chat echo.
            // The N5 press delivers real coordinates to the leader.
            chatReader.AssistRequestReturn = true;
            // Press N5 (AssistCantFollow) — sends position to leader, sets
            // AssistRequestReturn=true on both bots, selects FollowFocusGoal on assist.
            input.PressAssistCantFollow();
            return;
        }

        if (bits.Target() && !bits.Combat() 
            && (targetBlacklist.Is() || navigation.IsInBlacklistArea()))
        {
            Log("PullTargetGoal: Mob in blacklist area trying not to pull");
            // Broadcast to assist so they also ignore + clear this mob.
            if (classConfig.Mode == Mode.PartyLeader && playerReader.TargetGuid != 0)
                SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid));
            playerReader.IgnoreTarget(playerReader.TargetGuid);
            input.PressStopAttack();
            input.PressClearTarget();
            wait.Update();
            stopMoving.StopForward();
            wait.Update(playerReader.DoubleNetworkLatency);
            wait.Update();
            return;
        }

        if (!bits.Combat() && !chatReader.AssistIsFollowing && classConfig.Mode == Mode.PartyLeader)
        {
            logger.LogInformation("PullTargetGoal: Not pulling due to assist not following");
            return;
        }

        if (PullDurationMs > MAX_PULL_DURATION)
        {
            input.PressStopAttack();
            input.PressClearTarget();
            Log("Pull taking too long. Clear target and face away!");
            input.TurnRandomDir(1000);
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

        if (!bits.SoftInteract() || EligibleEnemySoftTargetExists())
        {
            input.PressApproach();
            wait.Update();
        }
        else
        {
            logger.LogInformation("Can't input.PressApproach()");
            logger.LogInformation("!bits.SoftInteract(): " + !bits.SoftInteract());
            logger.LogInformation("EligibleEnemySoftTargetExists(): " + EligibleEnemySoftTargetExists());
        }

        if (!stuckDetector.IsMoving())
            stuckDetector.Update();
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
