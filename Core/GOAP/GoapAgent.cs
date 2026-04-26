using Core.AreaBlacklist;
using Core.Goals;
using Core.Session;

using Game;

using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.Extensions;

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing.Printing;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Transactions;

namespace Core.GOAP;

public sealed partial class GoapAgent : IDisposable
{
    private readonly ILogger logger;
    private readonly ILogger globalLogger;

    private readonly ClassConfiguration classConfig;
    private readonly AddonReader addonReader;
    private readonly PlayerReader playerReader;
    private readonly ChatReader chatReader;
    private readonly RestHandler restHandler;
    private readonly AddonBits bits;
    private readonly IWowScreen screen;
    private readonly RouteInfo routeInfo;
    private readonly ConfigurableInput input;
    private readonly IMountHandler mountHandler;
    private readonly CombatLog combatLog;

    private readonly IGrindSessionHandler sessionHandler;
    private readonly StopMoving stopMoving;

    private readonly Thread goapThread;
    private readonly CancellationTokenSource<GoapAgent> cts;
    private readonly ManualResetEventSlim manualReset;

    private readonly IScreenCapture screenCapture;
    private readonly IBagChangeTracker bagChangeTracker;
    private readonly Navigation navigation;

    // Evade recovery: blocks CombatGoal, ApproachTargetGoal and PullTargetGoal for a fixed
    // window after an evading mob is detected, giving both bots time to navigate away.
    private DateTime _evadeRecoveryUntilUtc = DateTime.MinValue;
    private const double EvadeRecoveryDurationSec = 25.0;

    // Ghost combat escape: same evadeRecovery window but triggered when both bots are
    // stuck in combat with no hostile target and no damage for 20s (bugged mob holding
    // combat flag). Uses a longer window so the bots have time to travel out of range.
    // No N2 press or _evadeLeaderWaiting — both bots move freely since there is no real mob.
    private const double GhostCombatEscapeDurationSec = 15.0;

    // Post-combat loot/gather reset timer. After combat ends, if neither shouldloot nor
    // shouldgather has been cleared by LootGoal/SkinningGoal within this window, forcibly
    // reset the counts to zero. This handles cases where the leader skins/loots a corpse
    // before the assist can, leaving the assist stuck in NO PLAN with stale flags.
    // Timer cancels if combat resumes before it expires.
    private const double PostCombatResetSec = 10.0;
    private DateTime _postCombatResetUtc = DateTime.MinValue;

    // Set when the leader fires an evade blacklist broadcast and the assist has not
    // yet confirmed they are following again. While true it overrides
    // assistrequestreturnorisfollowing=true in WorldState so FRG can run (the leader
    // can patrol away from the evading mob) without corrupting the position-bearing
    // AssistRequestReturn/AssistXPos/AssistYPos fields.
    // Cleared when ChatReader receives "i'm following" from the assist.
    private bool _evadeLeaderWaiting;

    private bool active;
    public bool Active
    {
        get => active;
        set
        {
            active = value;
            if (!active)
            {
                manualReset.Reset();

                foreach (IGoapEventListener goal in AvailableGoals.OfType<IGoapEventListener>())
                {
                    goal.OnGoapEvent(new AbortEvent());
                }

                input.Reset();
                stopMoving.Stop();

                if (classConfig.Mode is Mode.AttendedGrind or Mode.Grind or Mode.PartyLeader)
                {
                    sessionHandler.Stop("Stopped", false);
                }

                screen.Enabled = false;
            }
            else
            {
                addonReader.SessionReset();
                SessionStat.Reset();

                if (CurrentGoal is IGoapEventListener listener)
                {
                    listener.OnGoapEvent(new ResumeEvent());
                }

                manualReset.Set();

                if (classConfig.Mode is Mode.AttendedGrind or Mode.Grind or Mode.PartyLeader)
                {
                    SessionStat.Start();
                    sessionHandler.Start(classConfig.OverridePathFilename ?? classConfig.PathFilename);
                }
            }
        }
    }

    public DynamicBitVector WorldState = new((int)GoapKey.LENGTH);

    public SessionStat SessionStat { get; }

    public GoapAgentState State { get; }
    public GoapGoal[] AvailableGoals { get; }

    public Stack<GoapGoal> Plan { get; private set; }
    public GoapGoal? CurrentGoal { get; private set; }

    public GoapAgent(
        ILogger<GoapAgent> logger,
        ILogger globalLogger,
        CancellationTokenSource<GoapAgent> cts,
        RouteInfo routeInfo,
        IScreenCapture screenCapture,
        ClassConfiguration classConfiguration,
        IWowScreen screen,
        GoapAgentState state,
        AddonReader addonReader,
        PlayerReader playerReader,
        AddonBits bits,
        ConfigurableInput input,
        IMountHandler mountHandler,
        CombatLog combatLog,
        IBagChangeTracker bagChangeTracker,
        SessionStat sessionStat,
        StopMoving stopMoving,
        IGrindSessionHandler sessionHandler,
        IEnumerable<GoapGoal> availableGoals,
        ChatReader chatReader,
        RestHandler restHandler,
        Navigation navigation
        )
    {
        this.routeInfo = routeInfo;

        this.cts = cts;

        this.logger = logger;
        this.globalLogger = globalLogger;

        this.screenCapture = screenCapture;
        this.classConfig = classConfiguration;

        this.screen = screen;
        this.State = state;
        this.addonReader = addonReader;
        this.playerReader = playerReader;
        this.bits = bits;

        this.input = input;
        this.mountHandler = mountHandler;

        this.combatLog = combatLog;
        this.bagChangeTracker = bagChangeTracker;

        SessionStat = sessionStat;

        this.stopMoving = stopMoving;

        this.sessionHandler = sessionHandler;

        this.AvailableGoals = availableGoals.OrderBy(a => a.Cost).ToArray();
        this.chatReader = chatReader;
        this.restHandler = restHandler;
        this.navigation = navigation;

        combatLog.KillCredit += OnKillCredit;
        combatLog.PlayerDeath += PlayerDied;

        addonReader.SessionReset();
        sessionStat.Reset();

        this.Plan = new();

        foreach (GoapGoal a in AvailableGoals)
        {
            a.GoapEvent += HandleGoapEvent;

            foreach (IGoapEventListener b in AvailableGoals.OfType<IGoapEventListener>())
            {
                if (b != a)
                    a.GoapEvent += b.OnGoapEvent;
            }
        }

        manualReset = new(false);
        goapThread = new(GoapThread);
        goapThread.Start();
    }

    public void Dispose()
    {
        cts.Cancel();
        manualReset.Set();

        foreach (GoapGoal a in AvailableGoals)
        {
            a.GoapEvent -= HandleGoapEvent;

            foreach (IGoapEventListener b in AvailableGoals.OfType<IGoapEventListener>())
            {
                if (b != a)
                    a.GoapEvent -= b.OnGoapEvent;
            }
        }

        combatLog.KillCredit -= OnKillCredit;
        combatLog.PlayerDeath -= PlayerDied;
    }

    private void GoapThread()
    {
        bool wasEmpty = false;
        bool previousAssistIsFollowing = false;
        bool previousAssistRequestReturn = false;
        bool previousInCombat = false;
        bool previousPartyInCombat = false;
        bool previousAssistRequestedPosition = false;
        bool previousEvadeRecovery = false;

        manualReset.Wait();

        while (!cts.IsCancellationRequested)
        {
            if(previousAssistIsFollowing != chatReader.AssistIsFollowing)
            {
                if (chatReader.AssistIsFollowing)
                {
                    AssistIsFollowing();
                    previousAssistIsFollowing = true;

                    // Assist confirmed follow — evade waiting is resolved, but only
                    // once the recovery window has also elapsed. If the assist sends
                    // "i'm following" early (normal FFG cycle before N5 fires) and then
                    // immediately drops follow again, clearing too early would leave the
                    // leader with NO PLAN for the rest of the recovery window.
                    if (_evadeLeaderWaiting && DateTime.UtcNow >= _evadeRecoveryUntilUtc)
                    {
                        _evadeLeaderWaiting = false;
                        logger.LogInformation("[GoapAgent] Assist re-established follow after evade recovery — clearing evadeLeaderWaiting.");
                    }
                    else if (_evadeLeaderWaiting)
                    {
                        logger.LogInformation("[GoapAgent] Assist sent i'm following during evade window — keeping evadeLeaderWaiting until recovery elapses.");
                    }
                }
                else
                {
                    AssistIsNotFollowing();
                    previousAssistIsFollowing = false;
                }
            }

            if (previousAssistRequestReturn != chatReader.AssistRequestReturn)
            {
                if (chatReader.AssistRequestReturn)
                {
                    AssistRequestReturn();
                    previousAssistRequestReturn = true;
                }
                else
                {
                    AssistNotRequestReturn();
                    previousAssistRequestReturn = false;
                }
            }

            // Leader replies to assist's position request.
            // One-shot: fire the macro once and immediately reset — no "false" transition needed.
            if ((classConfig.Mode == Mode.PartyLeader) && !previousAssistRequestedPosition && chatReader.AssistRequestedPosition)
            {
                logger.LogInformation("[GoapAgent] Assist requested leader position — replying.");
                // Stop moving BEFORE pressing the reply macro so the position broadcast
                // reflects where the leader is actually standing, not where they were
                // heading. The assist will navigate to these coordinates, so even a few
                // yards of drift can put the destination just outside follow range.
                stopMoving.Stop();
                input.StopForward(true);
                input.PressLeaderReplyPosition();
                // Reset immediately so GoapThread doesn't fire the macro again next iteration.
                chatReader.AssistRequestedPosition = false;
                previousAssistRequestedPosition = true;
            
                // Tell FollowRouteGoal to pause patrol and wait for the assist to arrive.
                foreach (var goal in AvailableGoals.OfType<FollowRouteGoal>())
                {
                    goal.PauseForAssistNavigation();
                }
            }
            else if ((classConfig.Mode == Mode.PartyLeader) && previousAssistRequestedPosition && !chatReader.AssistRequestedPosition)
            {
                previousAssistRequestedPosition = false;
            }

            // ── Evade recovery world state ──────────────────────────────────────────
            // Drive the evade recovery timer here rather than purely in UpdateWorldState()
            // so we broadcast a GoapStateEvent at the exact moment the state changes,
            // letting goal listeners react immediately without waiting for the next Plan().
            bool evadeRecoveryActive = DateTime.UtcNow < _evadeRecoveryUntilUtc;
            if (evadeRecoveryActive != previousEvadeRecovery)
            {
                previousEvadeRecovery = evadeRecoveryActive;
                BroadcastGoapEvent(GoapKey.evadeRecovery, evadeRecoveryActive);
                if (!evadeRecoveryActive)
                {
                    logger.LogInformation("[GoapAgent] Evade recovery window elapsed — resuming normal combat.");
                    // Also clear waiting flag — the window has passed whether or not the
                    // assist confirmed follow. If they didn't, the leader will re-plan
                    // normally (FRG requires assistrequestreturnorisfollowing, which will
                    // be true if AssistIsFollowing or AssistRequestReturn is set).
                    if (_evadeLeaderWaiting)
                    {
                        _evadeLeaderWaiting = false;
                        logger.LogInformation("[GoapAgent] Clearing evadeLeaderWaiting — recovery window elapsed.");
                    }
                }
            }
            // ───────────────────────────────────────────────────────────────────────

            if ((classConfig.Mode != Mode.PartyLeader || classConfig.Mode != Mode.AssistFocus) 
                && (previousInCombat != bits.Combat()))
            {
                if (bits.Combat())
                {
                    SendInCombat();
                    previousInCombat = true;
                }
                else
                {
                    SendNotInCombat();
                    previousInCombat = false;
                }
            }

            if ((classConfig.Mode == Mode.PartyLeader || classConfig.Mode == Mode.AssistFocus) 
                && (previousPartyInCombat != PartyInCombat()))
            {
                if (PartyInCombat())
                {
                    SendPartyInCombat();
                    previousPartyInCombat = true;

                    // Combat resumed — cancel the post-combat reset timer so we don't
                    // wipe loot/gather state mid-fight.
                    if (_postCombatResetUtc != DateTime.MinValue)
                    {
                        logger.LogInformation("[GoapAgent] Post-combat reset timer cancelled — combat resumed.");
                        _postCombatResetUtc = DateTime.MinValue;
                    }
                }
                else
                {
                    SendPartyNotInCombat();
                    previousPartyInCombat = false;

                    // Combat ended — start the 10s timer. If loot/gather haven't been
                    // cleared by their respective goals by the time it fires, force-reset
                    // them. This handles the case where the leader loots/skins a corpse
                    // before the assist can, leaving the assist stuck in NO PLAN.
                    if (State.LootableCorpseCount > 0 || State.GatherableCorpseCount > 0)
                    {
                        _postCombatResetUtc = DateTime.UtcNow.AddSeconds(PostCombatResetSec);
                        logger.LogInformation($"[GoapAgent] Post-combat reset timer started ({PostCombatResetSec}s) — LootableCorpseCount={State.LootableCorpseCount} GatherableCorpseCount={State.GatherableCorpseCount}.");
                    }
                }
            }

            // Post-combat loot/gather reset: if the timer has expired and we're still
            // not in combat, force-clear the stale counts.
            if (_postCombatResetUtc != DateTime.MinValue &&
                DateTime.UtcNow >= _postCombatResetUtc &&
                !PartyInCombat())
            {
                _postCombatResetUtc = DateTime.MinValue;
                if (State.LootableCorpseCount > 0 || State.GatherableCorpseCount > 0)
                {
                    logger.LogWarning($"[GoapAgent] Post-combat reset timer expired — clearing stale LootableCorpseCount={State.LootableCorpseCount} GatherableCorpseCount={State.GatherableCorpseCount}.");
                    State.LootableCorpseCount = 0;
                    State.GatherableCorpseCount = 0;
                }
            }

            GoapGoal? newGoal = NextGoal();
            if (newGoal != null)
            {
                if (newGoal != CurrentGoal)
                {
                    wasEmpty = false;
                    CurrentGoal?.OnExit();
                    CurrentGoal = newGoal;

                    LogNewGoal(logger, newGoal.Name);
                    CurrentGoal.OnEnter();
                }

                newGoal.Update();
            }
            else if (!wasEmpty)
            {
                LogNewEmptyGoal(logger);
                LogCompleteGoapState();
                wasEmpty = true;
            }

            Thread.Sleep(1);
            manualReset.Wait();
        }

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Thread stopped!");
    }

    public void LogCompleteGoapState()
    {
        AddonBits b = bits;

        bool dmgTaken = combatLog.DamageTakenCount() > 0;
        bool dmgDone = combatLog.DamageDoneCount() > 0;
        bool hasTarget = b.Target();
        bool playerCombat = b.Combat();

        logger.LogInformation("---- Complate GoapKey State ----");
        logger.LogInformation("GoapKey.hastarget: " + hasTarget);
        logger.LogInformation("GoapKey.dangercombat: " + (playerCombat && dmgTaken));
        logger.LogInformation("GoapKey.damagetaken: " + dmgTaken);
        logger.LogInformation("GoapKey.damagedone: " + dmgDone);
        logger.LogInformation("GoapKey.damagetakenordone: " + (dmgTaken || dmgDone));
        logger.LogInformation("GoapKey.targetisalive: " + (hasTarget && !b.Target_Dead()));
        logger.LogInformation("GoapKey.targettargetsus: " + ((hasTarget && playerReader.TargetHealthPercent() < 30) ||
            playerReader.TargetTarget is UnitsTarget.Me or UnitsTarget.Pet or UnitsTarget.PartyOrPet));
        logger.LogInformation("GoapKey.incombat: " + playerCombat);
        logger.LogInformation("GoapKey.pethastarget: " + (playerReader.PetTarget() && !b.PetTarget_Dead()));
        logger.LogInformation("GoapKey.ismounted: " + mountHandler.IsMounted());
        logger.LogInformation("GoapKey.withinpullrange: " + playerReader.WithInPullRange());
        logger.LogInformation("GoapKey.incombatrange: " + playerReader.WithInCombatRange());
        logger.LogInformation("GoapKey.pulled: " + (bits.Combat() && bits.Target_Combat() && combatLog.ToPullCount() > 0));
        logger.LogInformation("GoapKey.isdead: " + b.Dead());
        logger.LogInformation("GoapKey.shouldloot: " + (State.LootableCorpseCount > 0));
        logger.LogInformation("GoapKey.shouldgather: " + (State.GatherableCorpseCount > 0));
        logger.LogInformation("GoapKey.producedcorpse: " + (State.LastCombatKillCount > 0));
        logger.LogInformation("GoapKey.consumecorpse: " + State.ShouldConsumeCorpse);
        logger.LogInformation("GoapKey.isswimming: " + b.Swimming());
        logger.LogInformation("GoapKey.itemsbroken: " + b.Items_Broken());
        logger.LogInformation("GoapKey.gathering: " + State.Gathering);
        logger.LogInformation("GoapKey.targethostile: " + (b.Target_Hostile() || (bits.Target() 
            && combatLog.ToPull.Contains(playerReader.TargetGuid))));
        logger.LogInformation("GoapKey.hasfocus: " + b.Focus());
        logger.LogInformation("GoapKey.focushastarget: " + b.FocusTarget());
        logger.LogInformation("GoapKey.focuscombat: " + bits.FocusTarget_Combat());
        logger.LogInformation("GoapKey.consumablecorpsenearby: " + (State.ConsumableCorpseCount > 0));
        logger.LogInformation("GoapKey.forcedfollow: " + chatReader.ForcedFollow);
        logger.LogInformation("GoapKey.assistisfollowing: " + chatReader.AssistIsFollowing);
        logger.LogInformation("GoapKey.assistrequestreturn: " + chatReader.AssistRequestReturn);
        logger.LogInformation("GoapKey.assistrequestreturnorisfollowing: " + (chatReader.AssistIsFollowing || chatReader.AssistRequestReturn || _evadeLeaderWaiting));
        logger.LogInformation("GoapKey.assistshouldfollow: " + (chatReader.AssistRequestReturn || (!chatReader.ForcedFollow
            && !(playerCombat && dmgTaken) && !dmgDone && !dmgTaken)));
        logger.LogInformation("GoapKey.drinking: " + restHandler.IsDrinking());
        logger.LogInformation("GoapKey.eating: " + restHandler.IsDrinking());
        logger.LogInformation("GoapKey.partymembercombat: " + PartyMemberInCombat());
        logger.LogInformation("GoapKey.partyleadercombat: " + PartyLeaderInCombat());
        logger.LogInformation("GoapKey.partyincombat: " + PartyInCombat());
        logger.LogInformation("GoapKey.inblacklistarea: " + navigation.IsInBlacklistArea());
        logger.LogInformation("GoapKey.focusconnected: " + bits.Focus_Connected());
        logger.LogInformation("GoapKey.party1connected: " + bits.Party1_Connected());
        logger.LogInformation("GoapKey.party2connected: " + bits.Party2_Connected());
        logger.LogInformation("GoapKey.party3connected: " + bits.Party3_Connected());
        logger.LogInformation("GoapKey.party4connected: " + bits.Party4_Connected());
        logger.LogInformation("GoapKey.leaderWaitingForAssist: " +
                         (classConfig.Mode == Mode.PartyLeader &&
                           (chatReader.AssistRequestReturn ||
                            AvailableGoals.OfType<FollowRouteGoal>().Any(g => g.WaitingForAssist)
                           )));
        logger.LogInformation("GoapKey.evadeRecovery: " + (DateTime.UtcNow < _evadeRecoveryUntilUtc));
        logger.LogInformation("GoapKey.partyleadercanfollowroute: " + CanPartyLeaderFollowRoute());
        logger.LogInformation("GoapKey.approachEscapeActive: " + navigation.IsApproachEscapeActive);
    }

    private GoapGoal? NextGoal()
    {
        UpdateWorldState();

        if (Plan.Count == 0)
        {
            Plan = GoapPlanner.Plan(AvailableGoals, WorldState, GoapPlanner.EmptyGoalState);
        }

        return Plan.Count > 0 ? Plan.Pop() : null;
    }

    private void UpdateWorldState()
    {
        AddonBits b = bits;

        bool dmgTaken = combatLog.DamageTakenCount() > 0;
        bool dmgDone = combatLog.DamageDoneCount() > 0;
        bool hasTarget = b.Target();
        bool playerCombat = b.Combat();

        // If WorldState is reused each tick:
        WorldState.ClearAll();

        WorldState[GoapKey.hastarget] = hasTarget;
        WorldState[GoapKey.dangercombat] = playerCombat && dmgTaken;
        WorldState[GoapKey.damagetaken] = dmgTaken;
        WorldState[GoapKey.damagedone] = dmgDone;
        WorldState[GoapKey.damagetakenordone] = dmgTaken || dmgDone;
        WorldState[GoapKey.targetisalive] = hasTarget && !b.Target_Dead();

        WorldState[GoapKey.targettargetsus] =
            (hasTarget && playerReader.TargetHealthPercent() < 30) ||
            playerReader.TargetTarget is UnitsTarget.Me or UnitsTarget.Pet or UnitsTarget.PartyOrPet;

        WorldState[GoapKey.incombat] = playerCombat;
        WorldState[GoapKey.focuscombat] = bits.FocusTarget_Combat();
        WorldState[GoapKey.pethastarget] = playerReader.PetTarget() && !b.PetTarget_Dead();
        WorldState[GoapKey.ismounted] = mountHandler.IsMounted();
        WorldState[GoapKey.withinpullrange] = playerReader.WithInPullRange();
        WorldState[GoapKey.incombatrange] = playerReader.WithInCombatRange();
        WorldState[GoapKey.pulled] = bits.Combat() && bits.Target_Combat() && combatLog.ToPullCount() > 0;
        WorldState[GoapKey.isdead] = b.Dead();
        WorldState[GoapKey.shouldloot] = State.LootableCorpseCount > 0;
        WorldState[GoapKey.shouldgather] = State.GatherableCorpseCount > 0;
        WorldState[GoapKey.producedcorpse] = classConfig.Loot && State.LastCombatKillCount > 0;
        WorldState[GoapKey.consumecorpse] = State.ShouldConsumeCorpse;
        WorldState[GoapKey.isswimming] = b.Swimming();
        WorldState[GoapKey.itemsbroken] = b.Items_Broken();
        WorldState[GoapKey.gathering] = State.Gathering;

        WorldState[GoapKey.targethostile] =
            b.Target_Hostile() || (bits.Target() && combatLog.ToPull.Contains(playerReader.TargetGuid));

        WorldState[GoapKey.hasfocus] = b.Focus();
        WorldState[GoapKey.focushastarget] = b.FocusTarget();
        WorldState[GoapKey.consumablecorpsenearby] = State.ConsumableCorpseCount > 0;

        WorldState[GoapKey.forcedfollow] = chatReader.ForcedFollow;
        WorldState[GoapKey.assistisfollowing] = chatReader.AssistIsFollowing;
        WorldState[GoapKey.assistrequestreturn] = chatReader.AssistRequestReturn;
        // _evadeLeaderWaiting is set when the leader broadcasts an evade blacklist and
        // the assist has not yet re-confirmed follow. During this window we allow
        // FollowRouteGoal to run so the leader can navigate away from the evading mob,
        // without touching AssistRequestReturn (which carries position data).
        WorldState[GoapKey.assistrequestreturnorisfollowing] =
            chatReader.AssistIsFollowing || chatReader.AssistRequestReturn || _evadeLeaderWaiting;

        WorldState[GoapKey.assistshouldfollow] =
            chatReader.AssistRequestReturn ||
            (!chatReader.ForcedFollow && !(playerCombat && dmgTaken) && !dmgDone && !dmgTaken);

        WorldState[GoapKey.partymembercombat] = PartyMemberInCombat();
        WorldState[GoapKey.partyleadercombat] = PartyLeaderInCombat();
        WorldState[GoapKey.partyincombat] = PartyInCombat();
        WorldState[GoapKey.drinking] = restHandler.IsDrinking();
        WorldState[GoapKey.eating] = restHandler.IsEating();
        WorldState[GoapKey.inblacklistarea] = navigation.IsInBlacklistArea();
        WorldState[GoapKey.focusconnected] = bits.Focus_Connected();
        WorldState[GoapKey.party1connected] = bits.Party1_Connected();
        WorldState[GoapKey.party2connected] = bits.Party2_Connected();
        WorldState[GoapKey.party3connected] = bits.Party3_Connected();
        WorldState[GoapKey.party4connected] = bits.Party4_Connected();
        WorldState[GoapKey.leaderWaitingForAssist] =
                         classConfig.Mode == Mode.PartyLeader &&
                         (chatReader.AssistRequestReturn ||
                          AvailableGoals.OfType<FollowRouteGoal>().Any(g => g.WaitingForAssist));

        // True for EvadeRecoveryDurationSec after an evading mob is detected.
        // Blocks CombatGoal and ApproachTargetGoal so both bots navigate away cleanly.
        WorldState[GoapKey.evadeRecovery] = DateTime.UtcNow < _evadeRecoveryUntilUtc;

        // Compound gate for PartyLeader FollowRouteGoal — see CanPartyLeaderFollowRoute().
        WorldState[GoapKey.partyleadercanfollowroute] = CanPartyLeaderFollowRoute();

        // True while Navigation has an active pather-based approach escape in progress.
        // PullTargetGoal requires this to be false — locking the planner onto
        // ApproachTargetGoal for the duration of the escape so ATG/PTG don't thrash.
        WorldState[GoapKey.approachEscapeActive] = navigation.IsApproachEscapeActive;
    }

    public bool PartyInCombat()
    {
        return bits.Combat() || bits.Focus_Combat();
    }

    public bool PartyMemberInCombat()
    {
        AddonBits b = bits;

        bool dmgTaken = combatLog.DamageTakenCount() > 0;
        bool hasTarget = b.Target();
        bool playerCombat = b.Combat();

        return ((playerCombat || bits.Focus_Combat()) && dmgTaken)
                || ((playerCombat || bits.Focus_Combat()) && hasTarget && (hasTarget && !b.Target_Dead())
                     && (b.Target_Hostile() || (bits.Target() && combatLog.ToPull.Contains(playerReader.TargetGuid)))
                     && playerReader.WithInCombatRange());
    }

    public bool PartyLeaderInCombat()
    {
        AddonBits b = bits;
        bool hasTarget = b.Target();
        bool playerCombat = b.Combat();
        bool dmgTaken = combatLog.DamageTakenCount() > 0;

        return ((playerCombat // AddPrecondition(GoapKey.incombat, true)
                  && hasTarget // AddPrecondition(GoapKey.hastarget, true);
                  && (hasTarget && !b.Target_Dead()) // AddPrecondition(GoapKey.targetisalive, true);
                                                     // AddPrecondition(GoapKey.targethostile, true);
                  && (b.Target_Hostile() || (bits.Target() && combatLog.ToPull.Contains(playerReader.TargetGuid)))
                  && (playerReader.WithInCombatRange()) // AddPrecondition(GoapKey.incombatrange, true)
                 )
                 || (b.FocusTarget() // AddPrecondition(GoapKey.focushastarget,true)
                    && bits.FocusTarget_Combat() // AddPrecondition(GoapKey.focuscombat, true)
                 )
                 // Added to check if just anyone in the party is in combat.
                 // Attempt to fix movement to Consume Corpse/Loot/SKin while in combat.
                 || (playerCombat || bits.Focus_Combat() && dmgTaken)
                );
    }

    /// <summary>
    /// Compound gate for GoapKey.partyleadercanfollowroute.
    /// Used as a precondition on FollowRouteGoal for PartyLeader so the leader
    /// can always select FRG during evade recovery regardless of combat/corpse state,
    /// but must complete the normal loot/consume cycle outside evade recovery.
    /// </summary>
    public bool CanPartyLeaderFollowRoute()
    {
        // During evade recovery the leader must always be able to select FRG —
        // bypass all combat/damage/corpse checks so it can navigate to the assist.
        if (DateTime.UtcNow < _evadeRecoveryUntilUtc)
            return true;

        bool dmgTaken = combatLog.DamageTakenCount() > 0;
        bool dmgDone = combatLog.DamageDoneCount() > 0;

        // Outside evade recovery: same conditions as a normal solo bot.
        // No ongoing combat indicators, no pending corpse/consume cycle.
        return (!classConfig.Loot || !bits.Combat())
            && !dmgDone
            && !dmgTaken
            && State.LastCombatKillCount == 0
            && !State.ShouldConsumeCorpse;
    }

    private void HandleGoapEvent(GoapEventArgs e)
    {
        if (e is GoapStateEvent g)
        {
            switch (g.Key)
            {
                case GoapKey.consumecorpse:
                    if(!chatReader.AssistRequestReturn)
                    {
                        State.ShouldConsumeCorpse = g.Value;
                    }
                    
                    break;
                case GoapKey.gathering:
                    State.Gathering = g.Value;
                    break;
            }
        }
        else if (e is CorpseEvent c)
        {
            routeInfo.PoiList.Add(new RouteInfoPoi(c.MapLoc, CorpseEvent.NAME, CorpseEvent.COLOR, c.Radius));
        }
        else if (e is SkinCorpseEvent s)
        {
            routeInfo.PoiList.Add(new RouteInfoPoi(s.MapLoc, SkinCorpseEvent.NAME, SkinCorpseEvent.COLOR, s.Radius));
        }
        else if (e is RemoveClosestPoi r)
        {
            RemoveClosestPoiByType(r.Name);
        }
        else if (e is ScreenCaptureEvent)
        {
            screenCapture.Request();
        }
        else if (e is EvadeBlacklistEvent evade)
        {
            // Both the leader and assist start their own evade recovery timer when this
            // event fires. On the leader it fires from CombatGoal/PullTargetGoal detection.
            // On the assist it fires from the LeaderBlacklistTarget goal handlers.
            //
            // guid=0 is a special case: ghost combat escape. The combat flag is being held
            // by a bugged mob with no hostile target and no damage. The leader presses N2
            // with guid=0 so the assist receives "blacklist target: 0", clears its combat
            // state, and follows the leader along the route. The leader continues patrol
            // via _evadeLeaderWaiting (unblocks FRG) and waits for "i'm following".
            // Unlike real evade, there is no mob to IgnoreTarget and no stop-and-wait —
            // the leader moves immediately so the assist has somewhere to follow TO.

            if (evade.TargetGuid != 0)
            {
                logger.LogInformation(
                    $"[GoapAgent] Evade blacklist event guid={evade.TargetGuid} — " +
                    $"starting {EvadeRecoveryDurationSec}s recovery window.");

                _evadeRecoveryUntilUtc = DateTime.UtcNow.AddSeconds(EvadeRecoveryDurationSec);

                if (classConfig.Mode == Mode.PartyLeader)
                {
                    _evadeLeaderWaiting = true;

                    // Stop and wait — leader must not move until the assist confirms following.
                    stopMoving.Stop();
                    input.StopForward(true);

                    // Press N2 → "blacklist target: {entryId}" in party chat.
                    input.PressLeaderBlacklistTarget();

                    foreach (var goal in AvailableGoals.OfType<FollowRouteGoal>())
                    {
                        goal.SuppressTargetFinderBriefly((int)(EvadeRecoveryDurationSec * 1000));
                    }
                }
            }
            else
            {
                // guid=0: ghost combat escape (bugged mob holding combat flag).
                // Behaviour is identical to a real evade — leader stops and waits,
                // assist clears combat state, presses N5 to send position, leader
                // navigates to assist (or assist navigates to leader via N8/N9),
                // then both continue the route once "i'm following" is received.
                // The only difference from a real evade is no IgnoreTarget call
                // and a longer 15s recovery window.
                logger.LogInformation(
                    $"[GoapAgent] Ghost combat escape (guid=0) — starting {GhostCombatEscapeDurationSec}s recovery window.");

                _evadeRecoveryUntilUtc = DateTime.UtcNow.AddSeconds(GhostCombatEscapeDurationSec);

                if (classConfig.Mode == Mode.PartyLeader)
                {
                    _evadeLeaderWaiting = true;

                    // Stop and wait — same as real evade. Leader must not move until
                    // the assist confirms following, so the assist has a stable
                    // destination to navigate toward.
                    stopMoving.Stop();
                    input.StopForward(true);

                    // Press N2 → "blacklist target: 0" in party chat.
                    // Assist sees guid=0, fires EvadeBlacklistEvent(0) locally, presses N5.
                    input.PressLeaderBlacklistTarget();

                    foreach (var goal in AvailableGoals.OfType<FollowRouteGoal>())
                    {
                        goal.SuppressTargetFinderBriefly((int)(GhostCombatEscapeDurationSec * 1000));
                    }
                }
            }
        }
    }

    private void OnKillCredit()
    {
        if (Active && !chatReader.AssistRequestReturn)
        {
            SessionStat.Kills++;

            State.LastCombatKillCount++;
            State.ConsumableCorpseCount++;
            BroadcastGoapEvent(GoapKey.producedcorpse, true);

            LogActiveKillDetected(logger, SessionStat.Kills, State.LastCombatKillCount, combatLog.DamageTakenCount());
        }
        else
        {
            LogInactiveKillDetected(logger);
        }
    }

    public void PlayerDied()
    {
        SessionStat.Deaths++;
    }

    private void BroadcastGoapEvent(GoapKey goapKey, bool value)
    {
        foreach (IGoapEventListener goal in AvailableGoals.OfType<IGoapEventListener>())
        {
            goal.OnGoapEvent(new GoapStateEvent(goapKey, value));
        }
    }

    private void RemoveClosestPoiByType(string type)
    {
        if (routeInfo.PoiList.Count == 0)
            return;

        int index = -1;
        float minDistance = float.MaxValue;
        Vector3 playerMap = playerReader.MapPos;
        for (int i = 0; i < routeInfo.PoiList.Count; i++)
        {
            RouteInfoPoi poi = routeInfo.PoiList[i];
            if (poi.Name != type)
                continue;

            float mapMin = playerMap.MapDistanceXYTo(poi.MapLoc);
            if (mapMin < minDistance)
            {
                minDistance = mapMin;
                index = i;
            }
        }

        if (index > -1)
        {
            routeInfo.PoiList.RemoveAt(index);
        }
    }

    public bool HasState(GoapKey key) => WorldState[(int)key];

    public void NodeFound()
    {
        State.Gathering = true;
        BroadcastGoapEvent(GoapKey.gathering, true);
    }

    public void AssistIsNotFollowing()
    {
        BroadcastGoapEvent(GoapKey.assistisfollowing, false);
    }

    public void AssistIsFollowing()
    {
        BroadcastGoapEvent(GoapKey.assistisfollowing, true);
    }

    public void AssistRequestReturn()
    {
        // Set state to escape ConsumeCorpseGoal/CorpseConsumedGoal/LootGoal/SkinningGoal
        State.LastCombatKillCount = 0;
        State.ShouldConsumeCorpse = false;
        State.LootableCorpseCount = 0;
        State.GatherableCorpseCount = 0;
        State.ConsumableCorpseCount = 0;
        BroadcastGoapEvent(GoapKey.assistrequestreturn, true);
    }

    public void AssistNotRequestReturn()
    {
        BroadcastGoapEvent(GoapKey.assistrequestreturn, false);
    }


    public void SendInCombat()
    {
        // Set state to escape ConsumeCorpseGoal/CorpseConsumedGoal/LootGoal/SkinningGoal
        State.LastCombatKillCount = 0;
        State.ShouldConsumeCorpse = false;
        State.LootableCorpseCount = 0;
        State.GatherableCorpseCount = 0;
        State.ConsumableCorpseCount = 0;
        BroadcastGoapEvent(GoapKey.incombat, true);
    }

    public void SendNotInCombat()
    {
        BroadcastGoapEvent(GoapKey.incombat, false);
    }

    public void SendPartyInCombat()
    {
        // Set state to escape ConsumeCorpseGoal/CorpseConsumedGoal/LootGoal/SkinningGoal
        State.LastCombatKillCount = 0;
        State.ShouldConsumeCorpse = false;
        State.LootableCorpseCount = 0;
        State.GatherableCorpseCount = 0;
        State.ConsumableCorpseCount = 0;
        BroadcastGoapEvent(GoapKey.partyincombat, true);
    }

    public void SendPartyNotInCombat()
    {
        BroadcastGoapEvent(GoapKey.partyincombat, false);
    }

    #region Logging

    [LoggerMessage(
        EventId = 0050,
        Level = LogLevel.Information,
        Message = "Kill credit detected! Session Total: {sessionTotal} | Last Combat: {lastCombatCount} | Currently fighting: {currentCombatRemain}")]
    static partial void LogActiveKillDetected(ILogger logger, int sessionTotal, int lastCombatCount, int currentCombatRemain);

    [LoggerMessage(
        EventId = 0051,
        Level = LogLevel.Information,
        Message = "Inactive, kill credit detected!")]
    static partial void LogInactiveKillDetected(ILogger logger);

    [LoggerMessage(
        EventId = 0052,
        Level = LogLevel.Information,
        Message = "New Plan= {name}")]
    static partial void LogNewGoal(ILogger logger, string name);

    [LoggerMessage(
        EventId = 0053,
        Level = LogLevel.Warning,
        Message = "New Plan= NO PLAN")]
    static partial void LogNewEmptyGoal(ILogger logger);

    #endregion
}
