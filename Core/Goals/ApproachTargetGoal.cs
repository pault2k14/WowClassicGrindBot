using Core.GOAP;

using Microsoft.Extensions.Logging;

using System;

using static System.Diagnostics.Stopwatch;

#pragma warning disable 162

namespace Core.Goals;

public sealed partial class ApproachTargetGoal : GoapGoal, IGoapEventListener
{
    private const bool debug = true;
    private const double STUCK_INTERVAL_MS = 400; // cant be lower than Approach.Cooldown
    private const double MAX_APPROACH_DURATION_MS = 15_000; // max time to chase to pull
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
    
    private long approachStart;

    private double nextStuckCheckTime;

    private int initialTargetGuid;
    private float initialMinRange;

    // Set by OnGoapEvent when GoapAgent broadcasts evadeRecovery=true.
    // Checked at the top of Update() to force an immediate exit.
    private bool _evadeRecoveryActive;

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
        Navigation navigation)
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

        if(classConfig.Mode == Mode.PartyLeader)
        {
            AddPrecondition(GoapKey.assistisfollowing, true);

            // This might allow assist focus to approach mob when
            // they have requested a return.
            AddPrecondition(GoapKey.assistrequestreturn, false);
        }

        // This might stop assistfocus from switching to
        // approach target goal whenever party leader
        // changes target.
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
        // Lock out approach during evade recovery so neither bot re-engages
        // while navigating away from the evading mob.
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
        SetNextStuckTimeCheck();

        input.PressDisableSoftInteract();
        wait.Update();
    }

    public override void OnExit()
    {
        // Temporarily disable due to navigation supression updates
        input.StopForward(false);
    }

    public override void Update()
    {
        wait.Update();

        if (bits.Drowning())
        {
            input.PressJump();
        }

        // If an evading mob was detected, exit immediately so the planner
        // re-evaluates. The evadeRecovery precondition prevents re-selection.
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

        // Assist-side evade handling while mid-approach.
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

            // Fire EvadeBlacklistEvent so the assist's own GoapAgent starts its
            // evadeRecovery timer, blocking Combat/Approach/Pull on the assist side.
            if (blacklistGuid != 0)
                SendGoapEvent(new EvadeBlacklistEvent(blacklistGuid));

            // Press N5 (AssistCantFollow) — sends position to leader, sets
            // AssistRequestReturn=true on both bots, selects FollowFocusGoal on assist.
            // We don't need to do this, follow focus goal will do it
            //input.PressAssistCantFollow();
            return;
        }

        if (navigation.IsInBlacklistArea())
        {
            logger.LogInformation("In BlacklistArea - Adding target to AreaBlacklistMobs list.");
            // Broadcast to assist so they also ignore + clear this evading mob.
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

        bool targetInBlacklist = targetBlacklist.Is();

        if (chatReader.ForcedFollow)
        {
            AddEffect(GoapKey.forcedfollow, true);
            return;
        }

        if(!bits.Combat() && bits.Target() && (targetInBlacklist || navigation.IsInBlacklistArea()))
        {
            logger.LogInformation("In BlacklistArea - Adding target to AreaBlacklistMobs list.");

            if (navigation.IsInBlacklistArea())
            {
                // Broadcast to assist so they also ignore + clear this evading mob.
                if (classConfig.Mode == Mode.PartyLeader && playerReader.TargetGuid != 0)
                    SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid));
                playerReader.IgnoreTarget(playerReader.TargetGuid);
            }

            input.PressStopAttack();
            input.PressClearTarget();
            wait.Update();
            stopMoving.StopForward();
            wait.Update(playerReader.DoubleNetworkLatency);
            wait.Update();
            return;
        }

        if(!chatReader.AssistIsFollowing && classConfig.Mode == Mode.PartyLeader)
        {
            logger.LogInformation("ApproachTargetGoal: Not approaching due to assist target not following");
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
            logger.LogInformation("!input.Approach.OnCooldown(): " + !input.Approach.OnCooldown());
            logger.LogInformation("!bits.SoftInteract(): " + !bits.SoftInteract());
            logger.LogInformation("HasValidSoftInteract(): " + HasValidSoftInteract());

            if (classConfig.Mode == Mode.AssistFocus && !targetInBlacklist)
            {
                // HasMoonIcon 5
                // HasSquareIcon 6
                // HasCrossIcon 7
                bool foundCrowdControlAction = false;
                string? raidIconRequirement = null;
                int targetRaidIcon = classConfig.RaidIconsToSkipInCombat
                    .IndexOf(playerReader.TargetRaidIcon());

                if(targetRaidIcon == 5)
                {
                    raidIconRequirement = "HasMoonIcon";
                }
                else if(targetRaidIcon == 6)
                {
                    raidIconRequirement = "HasSquareIcon";
                }
                else if (targetRaidIcon == 7)
                {
                    raidIconRequirement = "HasCrossIcon";
                }

                if(raidIconRequirement != null)
                {
                    Keys = classConfig.Combat.Sequence;
                    ReadOnlySpan<KeyAction> span = Keys;
                    for (int i = 0; i < span.Length; i++)
                    {
                        KeyAction keyAction = span[i];

                        if (keyAction.CrowdControl
                            && keyAction.Requirements.Contains(raidIconRequirement))
                        {
                            foundCrowdControlAction = true;
                            break;
                        }
                    }
                }

                // The target had a raid icon, and at least one of our
                // combat actions had a matching raid icon requirement
                // so we should approach
                if (foundCrowdControlAction || classConfig.AssistApproach)
                {
                    input.PressApproach();
                    wait.Update();
                }
                // Either the target did not have a raid icon or none
                // of our combat actions had a matching raid icon requirement
                else
                {
                    input.PressTargetFocus();
                    input.PressTargetOfTarget();
                    wait.Update();
                    input.PressApproach();
                    wait.Update();
                }
            }
            else
            {
                if (!bits.Combat() && (targetInBlacklist || navigation.IsInBlacklistArea()))
                {
                    logger.LogWarning($"Losing the target due blacklist!");
                    if (navigation.IsInBlacklistArea())
                    {
                        // Broadcast to assist so they also ignore + clear this evading mob.
                        if (classConfig.Mode == Mode.PartyLeader && playerReader.TargetGuid != 0)
                            SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid));
                        playerReader.IgnoreTarget(playerReader.TargetGuid);
                    }

                    input.PressStopAttack();
                    input.PressClearTarget();
                    wait.Update();
                    stopMoving.StopForward();
                    wait.Update(playerReader.DoubleNetworkLatency);
                    wait.Update();
                    return;
                }
                
                input.PressApproach();
                wait.Update();
            }

        }

        if (!bits.Combat() && !targetInBlacklist)
        {
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
                // TODO: not sure why this is here!
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

                Log($"Seems stuck! Clear Target.");

                input.PressClearTarget();
                input.TurnRandomDir(250 + Random.Shared.Next(250));
                wait.Update();

                return;
            }
        }

        if (ApproachDurationMs > MAX_APPROACH_DURATION_MS)
        {
            logger.LogWarning("Too long time. Clear Target. Turn away.");

            input.PressClearTarget();
            input.TurnRandomDir(250 + Random.Shared.Next(250));
            wait.Update();

            return;
        }

        if (playerReader.TargetGuid == initialTargetGuid &&
            !playerReader.IsInMeleeRange())
        {
            int initialTargetMinRange = playerReader.MinRange();
            if (!input.TargetNearestTarget.OnCooldown())
            {
                input.PressNearestTarget();
                wait.Update();
            }

            if (bits.Target() && playerReader.TargetGuid != initialTargetGuid)
            {
                if (targetBlacklist.Is() || navigation.IsInBlacklistArea())
                {
                    logger.LogWarning($"Losing the target due blacklist!");

                    if (navigation.IsInBlacklistArea())
                    {
                        // Broadcast to assist so they also ignore + clear this evading mob.
                        if (classConfig.Mode == Mode.PartyLeader && playerReader.TargetGuid != 0)
                            SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid));
                        playerReader.IgnoreTarget(playerReader.TargetGuid);
                    }

                    input.PressStopAttack();
                    input.PressClearTarget();
                    wait.Update();
                    stopMoving.StopForward();
                    wait.Update(playerReader.DoubleNetworkLatency);
                    wait.Update();
                    return;
                }

                if (playerReader.MinRange() < initialTargetMinRange)
                {
                    logger.LogWarning($"Found a closer target! {playerReader.MinRange()} < {initialTargetMinRange}");

                    initialMinRange = playerReader.MinRange();
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

        if (ApproachDurationMs > MIN_TIME_TILL_IDLE && initialMinRange < playerReader.MinRange())
        {
            Log($"Going away from the target! {initialMinRange} < {playerReader.MinRange()}");

            input.PressClearTarget();
            wait.Update();
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

    private void Log(string text)
    {
        logger.LogDebug(text);
    }


    #region Logging

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Warning,
        Message = "Clear current target as not in combat!")]
    static partial void LogPreventExtraPull(ILogger logger);

    #endregion
}
