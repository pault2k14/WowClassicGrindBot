using Core.AreaBlacklist;
using Core.Goals;
using Core.Party;
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

    // Party API services
    private readonly AssistStateStore assistStateStore;
    private readonly LeaderConnectionStatus leaderConnection;

    private DateTime _evadeRecoveryUntilUtc = DateTime.MinValue;
    private const double EvadeRecoveryDurationSec = 25.0;
    private const double GhostCombatEscapeDurationSec = 15.0;

    private const double PostCombatResetSec = 10.0;
    private DateTime _postCombatResetUtc = DateTime.MinValue;

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
                    goal.OnGoapEvent(new AbortEvent());

                input.Reset();
                stopMoving.Stop();

                if (classConfig.Mode is Mode.AttendedGrind or Mode.Grind or Mode.PartyLeader)
                    sessionHandler.Stop("Stopped", false);

                screen.Enabled = false;
            }
            else
            {
                addonReader.SessionReset();
                SessionStat.Reset();

                if (CurrentGoal is IGoapEventListener listener)
                    listener.OnGoapEvent(new ResumeEvent());

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
        Navigation navigation,
        AssistStateStore assistStateStore,
        LeaderConnectionStatus leaderConnection)
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
        this.assistStateStore = assistStateStore;
        this.leaderConnection = leaderConnection;

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
        bool previousAssistCantFollow  = false;
        bool previousInCombat = false;
        bool previousPartyInCombat = false;
        bool previousEvadeRecovery = false;

        manualReset.Wait();

        while (!cts.IsCancellationRequested)
        {
            // ── Leader side: read assist state from API store ──────────────
            if (classConfig.Mode == Mode.PartyLeader)
            {
                bool currentAssistIsFollowing = assistStateStore.AnyAssistIsFollowing();
                if (currentAssistIsFollowing != previousAssistIsFollowing)
                {
                    if (currentAssistIsFollowing)
                    {
                        AssistIsFollowing();
                        previousAssistIsFollowing = true;

                        if (_evadeLeaderWaiting && DateTime.UtcNow >= _evadeRecoveryUntilUtc)
                        {
                            _evadeLeaderWaiting = false;
                            logger.LogInformation("[GoapAgent] Assist Following after evade recovery — clearing evadeLeaderWaiting.");
                        }
                        else if (_evadeLeaderWaiting)
                        {
                            logger.LogInformation("[GoapAgent] Assist Following during evade window — keeping evadeLeaderWaiting.");
                        }
                    }
                    else
                    {
                        AssistIsNotFollowing();
                        previousAssistIsFollowing = false;
                    }
                }

                bool currentAssistCantFollow = assistStateStore.AnyAssistCantFollow();
                if (currentAssistCantFollow != previousAssistCantFollow)
                {
                    if (currentAssistCantFollow)
                    {
                        AssistRequestReturn();
                        previousAssistCantFollow = true;

                        logger.LogInformation(
                            $"[GoapAgent] Assist CantFollow — navigating to assist position.");
                        foreach (var goal in AvailableGoals.OfType<FollowRouteGoal>())
                        {
                            AssistState? cantFollow = assistStateStore.GetCantFollowState();
                            if (cantFollow != null)
                                goal.GoToOneWaypoint(cantFollow.MapPosNoZ);
                        }
                    }
                    else
                    {
                        AssistNotRequestReturn();
                        previousAssistCantFollow = false;
                    }
                }
            }

            // ── Evade recovery world state ────────────────────────────────
            bool evadeRecoveryActive = DateTime.UtcNow < _evadeRecoveryUntilUtc;
            if (evadeRecoveryActive != previousEvadeRecovery)
            {
                previousEvadeRecovery = evadeRecoveryActive;
                BroadcastGoapEvent(GoapKey.evadeRecovery, evadeRecoveryActive);
                if (!evadeRecoveryActive)
                {
                    logger.LogInformation("[GoapAgent] Evade recovery elapsed — resuming normal combat.");
                    if (_evadeLeaderWaiting)
                    {
                        _evadeLeaderWaiting = false;
                        logger.LogInformation("[GoapAgent] Clearing evadeLeaderWaiting — recovery elapsed.");
                    }
                }
            }

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
                }
                else
                {
                    SendPartyNotInCombat();
                    previousPartyInCombat = false;
                }
            }

            // Post-combat loot/gather reset
            if ((State.LootableCorpseCount > 0 || State.GatherableCorpseCount > 0) &&
                !PartyInCombat() &&
                _postCombatResetUtc == DateTime.MinValue)
            {
                _postCombatResetUtc = DateTime.UtcNow.AddSeconds(PostCombatResetSec);
                logger.LogInformation(
                    $"[GoapAgent] Post-combat reset timer started ({PostCombatResetSec}s) — " +
                    $"LootableCorpseCount={State.LootableCorpseCount} " +
                    $"GatherableCorpseCount={State.GatherableCorpseCount}.");
            }

            if (_postCombatResetUtc != DateTime.MinValue &&
                DateTime.UtcNow >= _postCombatResetUtc &&
                !PartyInCombat())
            {
                _postCombatResetUtc = DateTime.MinValue;
                if (State.LootableCorpseCount > 0 || State.GatherableCorpseCount > 0)
                {
                    logger.LogWarning(
                        $"[GoapAgent] Post-combat reset timer expired — clearing stale " +
                        $"LootableCorpseCount={State.LootableCorpseCount} " +
                        $"GatherableCorpseCount={State.GatherableCorpseCount}.");
                    State.LootableCorpseCount = 0;
                    State.GatherableCorpseCount = 0;
                }
            }

            if (_postCombatResetUtc != DateTime.MinValue && PartyInCombat())
            {
                logger.LogInformation("[GoapAgent] Post-combat reset timer cancelled — combat resumed.");
                _postCombatResetUtc = DateTime.MinValue;
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
        bool dmgDone  = combatLog.DamageDoneCount() > 0;
        bool hasTarget = b.Target();
        bool playerCombat = b.Combat();

        bool assistIsFollowing = classConfig.Mode == Mode.PartyLeader
            ? assistStateStore.AnyAssistIsFollowing()
            : chatReader.AssistIsFollowing;
        bool assistCantFollow = classConfig.Mode == Mode.PartyLeader
            ? assistStateStore.AnyAssistCantFollow()
            : chatReader.AssistRequestReturn;

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
        logger.LogInformation("GoapKey.assistisfollowing: " + assistIsFollowing);
        logger.LogInformation("GoapKey.assistrequestreturn: " + assistCantFollow);
        logger.LogInformation("GoapKey.assistrequestreturnorisfollowing: " +
            (assistIsFollowing || assistCantFollow || _evadeLeaderWaiting));
        logger.LogInformation("GoapKey.assistshouldfollow: " + (
            leaderConnection.HasValidLeaderState &&
            (chatReader.AssistRequestReturn ||
             (!chatReader.ForcedFollow && !(playerCombat && dmgTaken) && !dmgDone && !dmgTaken))));
        logger.LogInformation("GoapKey.drinking: " + restHandler.IsDrinking());
        logger.LogInformation("GoapKey.eating: " + restHandler.IsEating());
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
             (assistCantFollow || AvailableGoals.OfType<FollowRouteGoal>().Any(g => g.WaitingForAssist))));
        logger.LogInformation("GoapKey.evadeRecovery: " + (DateTime.UtcNow < _evadeRecoveryUntilUtc));
        logger.LogInformation("GoapKey.partyleadercanfollowroute: " + CanPartyLeaderFollowRoute());
        logger.LogInformation("GoapKey.approachEscapeActive: " + navigation.IsApproachEscapeActive);

        if (classConfig.Mode == Mode.AssistFocus)
        {
            logger.LogInformation("[GoapAgent] LeaderConnectionStatus.HasValidLeaderState: " +
                leaderConnection.HasValidLeaderState);
            logger.LogInformation("[GoapAgent] LeaderConnectionStatus.LastLeaderState.AgeMs: " +
                (leaderConnection.LastLeaderState?.AgeMs.ToString("0") ?? "null"));
        }
    }

    private GoapGoal? NextGoal()
    {
        UpdateWorldState();

        if (Plan.Count == 0)
            Plan = GoapPlanner.Plan(AvailableGoals, WorldState, GoapPlanner.EmptyGoalState);

        return Plan.Count > 0 ? Plan.Pop() : null;
    }

    private void UpdateWorldState()
    {
        AddonBits b = bits;

        bool dmgTaken  = combatLog.DamageTakenCount() > 0;
        bool dmgDone   = combatLog.DamageDoneCount() > 0;
        bool hasTarget = b.Target();
        bool playerCombat = b.Combat();

        WorldState.ClearAll();

        WorldState[GoapKey.hastarget]         = hasTarget;
        WorldState[GoapKey.dangercombat]       = playerCombat && dmgTaken;
        WorldState[GoapKey.damagetaken]        = dmgTaken;
        WorldState[GoapKey.damagedone]         = dmgDone;
        WorldState[GoapKey.damagetakenordone]  = dmgTaken || dmgDone;
        WorldState[GoapKey.targetisalive]      = hasTarget && !b.Target_Dead();

        WorldState[GoapKey.targettargetsus] =
            (hasTarget && playerReader.TargetHealthPercent() < 30) ||
            playerReader.TargetTarget is UnitsTarget.Me or UnitsTarget.Pet or UnitsTarget.PartyOrPet;

        WorldState[GoapKey.incombat]       = playerCombat;
        WorldState[GoapKey.focuscombat]    = bits.FocusTarget_Combat();
        WorldState[GoapKey.pethastarget]   = playerReader.PetTarget() && !b.PetTarget_Dead();
        WorldState[GoapKey.ismounted]      = mountHandler.IsMounted();
        WorldState[GoapKey.withinpullrange] = playerReader.WithInPullRange();
        WorldState[GoapKey.incombatrange]  = playerReader.WithInCombatRange();
        WorldState[GoapKey.pulled]         = bits.Combat() && bits.Target_Combat() && combatLog.ToPullCount() > 0;
        WorldState[GoapKey.isdead]         = b.Dead();
        WorldState[GoapKey.shouldloot]     = State.LootableCorpseCount > 0;
        WorldState[GoapKey.shouldgather]   = State.GatherableCorpseCount > 0;
        WorldState[GoapKey.producedcorpse] = classConfig.Loot && State.LastCombatKillCount > 0;
        WorldState[GoapKey.consumecorpse]  = State.ShouldConsumeCorpse;
        WorldState[GoapKey.isswimming]     = b.Swimming();
        WorldState[GoapKey.itemsbroken]    = b.Items_Broken();
        WorldState[GoapKey.gathering]      = State.Gathering;

        WorldState[GoapKey.targethostile] =
            b.Target_Hostile() || (bits.Target() && combatLog.ToPull.Contains(playerReader.TargetGuid));

        WorldState[GoapKey.hasfocus]              = b.Focus();
        WorldState[GoapKey.focushastarget]         = b.FocusTarget();
        WorldState[GoapKey.consumablecorpsenearby] = State.ConsumableCorpseCount > 0;
        WorldState[GoapKey.forcedfollow]           = chatReader.ForcedFollow;

        // ── Leader side: read from API store ──────────────────────────────
        bool assistIsFollowing = classConfig.Mode == Mode.PartyLeader
            ? assistStateStore.AnyAssistIsFollowing()
            : chatReader.AssistIsFollowing;

        bool assistCantFollow = classConfig.Mode == Mode.PartyLeader
            ? assistStateStore.AnyAssistCantFollow()
            : chatReader.AssistRequestReturn;

        WorldState[GoapKey.assistisfollowing]  = assistIsFollowing;
        WorldState[GoapKey.assistrequestreturn] = assistCantFollow;

        WorldState[GoapKey.assistrequestreturnorisfollowing] =
            assistIsFollowing || assistCantFollow || _evadeLeaderWaiting;

        // ── Assist side: gate on leader API reachability ──────────────────
        // FollowFocusGoal requires assistshouldfollow=true.
        // In API mode the assist must have a valid fresh leader state before
        // it can enter FFG. leaderConnection.HasValidLeaderState is false until
        // the first successful GET and becomes false again if the leader goes stale.
        WorldState[GoapKey.assistshouldfollow] =
            leaderConnection.HasValidLeaderState &&
            (chatReader.AssistRequestReturn || // local override (evade scenario)
             (!chatReader.ForcedFollow && !(playerCombat && dmgTaken) && !dmgDone && !dmgTaken));

        WorldState[GoapKey.partymembercombat]  = PartyMemberInCombat();
        WorldState[GoapKey.partyleadercombat]  = PartyLeaderInCombat();
        WorldState[GoapKey.partyincombat]      = PartyInCombat();
        WorldState[GoapKey.drinking]           = restHandler.IsDrinking();
        WorldState[GoapKey.eating]             = restHandler.IsEating();
        WorldState[GoapKey.inblacklistarea]    = navigation.IsInBlacklistArea();
        WorldState[GoapKey.focusconnected]     = bits.Focus_Connected();
        WorldState[GoapKey.party1connected]    = bits.Party1_Connected();
        WorldState[GoapKey.party2connected]    = bits.Party2_Connected();
        WorldState[GoapKey.party3connected]    = bits.Party3_Connected();
        WorldState[GoapKey.party4connected]    = bits.Party4_Connected();

        WorldState[GoapKey.leaderWaitingForAssist] =
            classConfig.Mode == Mode.PartyLeader &&
            (assistCantFollow ||
             AvailableGoals.OfType<FollowRouteGoal>().Any(g => g.WaitingForAssist));

        WorldState[GoapKey.evadeRecovery]        = DateTime.UtcNow < _evadeRecoveryUntilUtc;
        WorldState[GoapKey.partyleadercanfollowroute] = CanPartyLeaderFollowRoute();
        WorldState[GoapKey.approachEscapeActive] = navigation.IsApproachEscapeActive;
    }

    public bool PartyInCombat() => bits.Combat() || bits.Focus_Combat();

    public bool PartyMemberInCombat()
    {
        AddonBits b = bits;
        bool dmgTaken  = combatLog.DamageTakenCount() > 0;
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

        return ((playerCombat
                  && hasTarget
                  && (hasTarget && !b.Target_Dead())
                  && (b.Target_Hostile() || (bits.Target() && combatLog.ToPull.Contains(playerReader.TargetGuid)))
                  && playerReader.WithInCombatRange())
                 || (b.FocusTarget() && bits.FocusTarget_Combat())
                 || (playerCombat || bits.Focus_Combat() && dmgTaken));
    }

    public bool CanPartyLeaderFollowRoute()
    {
        if (DateTime.UtcNow < _evadeRecoveryUntilUtc)
            return true;

        bool dmgTaken = combatLog.DamageTakenCount() > 0;
        bool dmgDone  = combatLog.DamageDoneCount() > 0;

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
                    // Use chat flag or store flag depending on mode
                    bool cantFollow = classConfig.Mode == Mode.PartyLeader
                        ? assistStateStore.AnyAssistCantFollow()
                        : chatReader.AssistRequestReturn;
                    if (!cantFollow)
                        State.ShouldConsumeCorpse = g.Value;
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
            if (evade.TargetGuid != 0)
            {
                logger.LogInformation(
                    $"[GoapAgent] Evade blacklist event guid={evade.TargetGuid} — " +
                    $"starting {EvadeRecoveryDurationSec}s recovery window.");

                _evadeRecoveryUntilUtc = DateTime.UtcNow.AddSeconds(EvadeRecoveryDurationSec);

                if (classConfig.Mode == Mode.PartyLeader)
                {
                    _evadeLeaderWaiting = true;
                    stopMoving.Stop();
                    input.StopForward(true);
                    input.PressLeaderBlacklistTarget();

                    foreach (var goal in AvailableGoals.OfType<FollowRouteGoal>())
                        goal.SuppressTargetFinderBriefly((int)(EvadeRecoveryDurationSec * 1000));
                }
            }
            else
            {
                logger.LogInformation(
                    $"[GoapAgent] Ghost combat escape (guid=0) — starting {GhostCombatEscapeDurationSec}s recovery window.");

                _evadeRecoveryUntilUtc = DateTime.UtcNow.AddSeconds(GhostCombatEscapeDurationSec);

                if (classConfig.Mode == Mode.PartyLeader)
                {
                    _evadeLeaderWaiting = true;
                    stopMoving.Stop();
                    input.StopForward(true);
                    input.PressLeaderBlacklistTarget();

                    foreach (var goal in AvailableGoals.OfType<FollowRouteGoal>())
                        goal.SuppressTargetFinderBriefly((int)(GhostCombatEscapeDurationSec * 1000));
                }
            }
        }
    }

    private void OnKillCredit()
    {
        bool assistCantFollow = classConfig.Mode == Mode.PartyLeader
            ? assistStateStore.AnyAssistCantFollow()
            : chatReader.AssistRequestReturn;

        if (Active && !assistCantFollow)
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
            goal.OnGoapEvent(new GoapStateEvent(goapKey, value));
    }

    private void RemoveClosestPoiByType(string type)
    {
        if (routeInfo.PoiList.Count == 0) return;

        int index = -1;
        float minDistance = float.MaxValue;
        Vector3 playerMap = playerReader.MapPos;
        for (int i = 0; i < routeInfo.PoiList.Count; i++)
        {
            RouteInfoPoi poi = routeInfo.PoiList[i];
            if (poi.Name != type) continue;

            float mapMin = playerMap.MapDistanceXYTo(poi.MapLoc);
            if (mapMin < minDistance)
            {
                minDistance = mapMin;
                index = i;
            }
        }

        if (index > -1)
            routeInfo.PoiList.RemoveAt(index);
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
