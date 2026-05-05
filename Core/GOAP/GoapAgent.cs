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
    private readonly LeaderNavigationProvider leaderNavProvider;
    private readonly AssistStatusProvider assistStatusProvider;

    private DateTime _evadeRecoveryUntilUtc = DateTime.MinValue;
    private const double EvadeRecoveryDurationSec = 25.0;
    private const double GhostCombatEscapeDurationSec = 15.0;

    private const double PostCombatResetSec = 10.0;
    private DateTime _postCombatResetUtc = DateTime.MinValue;

    private bool _evadeLeaderWaiting;

    // -----------------------------------------------------------------------
    // Assist-side blacklist tracking
    //
    // Tracks GUIDs already observed in the leader's BlacklistedMobGuids API
    // field. When a NEW GUID arrives, the agent-level diff in GoapThread
    // dispatches EvadeBlacklistEvent so the assist's CombatGoal exits via
    // its evadeRecovery precondition (CombatGoal.cs:191).
    //
    // Critical: this MUST live on the agent (not on FFG), because Combat is
    // cost 4 and FFG is cost 19 — Combat preempts FFG. A leader-published
    // blacklist GUID dispatched during the assist's combat would otherwise
    // not be seen by the assist until combat ended naturally, by which
    // point the mob is already dead. Observed in log 22 (assist 18:03:34
    // through 18:03:46): the assist cast Smite 4 times against the
    // blacklisted GUID before FFG.OnEnter ran the original FFG-level diff
    // — 3 seconds AFTER kill credit.
    // -----------------------------------------------------------------------
    private readonly System.Collections.Generic.HashSet<int> _knownBlacklistedGuids = new();

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
        LeaderConnectionStatus leaderConnection,
        AssistStatusProvider assistStatusProvider,
        LeaderNavigationProvider leaderNavProvider)
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
        this.assistStatusProvider = assistStatusProvider;
        this.leaderNavProvider = leaderNavProvider;

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
                // Track "assist present" = Following OR NavigatingToLeader.
                // Previously tracking only AnyAssistIsFollowing() caused AssistIsNotFollowing()
                // to fire on every Following→NavigatingToLeader transition, which triggered
                // FRG.Abort() → navigation paused → stop/start loop every ~1 second.
                // The leader should only stop when the assist is TRULY unavailable
                // (stale, CantFollow), not just momentarily catching up.
                bool currentAssistPresent =
                    assistStateStore.AnyAssistIsFollowing() || assistStateStore.AnyAssistNavigating();

                if (currentAssistPresent != previousAssistIsFollowing)
                {
                    if (currentAssistPresent)
                    {
                        AssistIsFollowing();
                        previousAssistIsFollowing = true;

                        if (_evadeLeaderWaiting && DateTime.UtcNow >= _evadeRecoveryUntilUtc)
                        {
                            _evadeLeaderWaiting = false;
                            logger.LogInformation("[GoapAgent] Assist available after evade recovery — clearing evadeLeaderWaiting.");
                        }
                        else if (_evadeLeaderWaiting)
                        {
                            logger.LogInformation("[GoapAgent] Assist available during evade window — keeping evadeLeaderWaiting.");
                        }
                    }
                    else
                    {
                        // Assist is genuinely unavailable (stale or no fresh status) — abort.
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

            // ── Assist side: API-based mob blacklist ─────────────────────
            // Diff BlacklistedMobGuids from the leader API at the agent level so
            // the signal interrupts the assist's combat regardless of which goal
            // is currently active. Without this, the equivalent diff in
            // FollowFocusGoal.Update only runs when FFG is the active goal —
            // but Combat (cost 4) preempts FFG (cost 19), so a leader evade
            // dispatched during the assist's combat would not reach the assist
            // until combat ends naturally. By that point the mob is dead and
            // the signal is moot. See log 22 (assist 18:03:34 through
            // 18:03:46): assist cast Smite 4 times against guid=7945779 after
            // the leader had blacklisted it; the assist saw the GUID only when
            // FFG.OnEnter fired post-loot, 3 seconds after kill credit.
            //
            // Each new GUID:
            //   1. IgnoreTarget(guid) — addon-side ignore so targeting won't
            //      re-acquire it
            //   2. HandleGoapEvent(EvadeBlacklistEvent) — same dispatcher real
            //      evades and the test endpoint use; sets _evadeRecoveryUntilUtc
            //   3. The block below (line 312) will then observe the new
            //      _evadeRecoveryUntilUtc value on this same iteration and
            //      broadcast GoapKey.evadeRecovery=true, which CombatGoal
            //      consumes via OnGoapEvent (CombatGoal.cs:126) and reacts to
            //      on its next Update tick (CombatGoal.cs:191).
            //   4. assistStatusProvider.CantFollow=true keeps FFG selectable
            //      after CombatGoal exits — matches the original FFG diff loop
            //      behavior (line 433 of FollowFocusGoal.cs).
            if (classConfig.Mode == Mode.AssistFocus)
            {
                LeaderState? leaderState = leaderConnection.LastLeaderState;
                if (leaderState != null && leaderState.BlacklistedMobGuids is { Length: > 0 })
                {
                    foreach (int guid in leaderState.BlacklistedMobGuids)
                    {
                        if (guid != 0 && _knownBlacklistedGuids.Add(guid))
                        {
                            logger.LogInformation(
                                $"[GoapAgent] New blacklisted mob guid={guid} from API — " +
                                "ignoring target and dispatching EvadeBlacklistEvent.");

                            playerReader.IgnoreTarget(guid);
                            HandleGoapEvent(new EvadeBlacklistEvent(guid));
                            assistStatusProvider.CantFollow = true;
                        }
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

        bool assistIsFollowing = assistStateStore.AnyAssistIsFollowing();
        bool assistCantFollow = classConfig.Mode == Mode.PartyLeader
            ? assistStateStore.AnyAssistCantFollow()
            : assistStatusProvider.CantFollow;

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
        bool assistNavigatingLog = classConfig.Mode == Mode.PartyLeader
            && assistStateStore.AnyAssistNavigating();
        logger.LogInformation("GoapKey.assistrequestreturnorisfollowing: " +
            (assistIsFollowing || assistCantFollow || assistNavigatingLog || _evadeLeaderWaiting));
        logger.LogInformation("GoapKey.assistshouldfollow: " + (
            leaderConnection.HasValidLeaderState &&
            (assistStatusProvider.CantFollow ||
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
        // AssistFocus / Grind modes don't post to the store; AnyAssistIsFollowing() returns
        // false for them, which is correct — those modes have no "assist is following" concept.
        bool assistIsFollowing = assistStateStore.AnyAssistIsFollowing();

        // PartyLeader reads from the API store. AssistFocus reads from AssistStatusProvider —
        // the assist's own self-reported flag that replaces chatReader.AssistRequestReturn.
        bool assistCantFollow = classConfig.Mode == Mode.PartyLeader
            ? assistStateStore.AnyAssistCantFollow()
            : assistStatusProvider.CantFollow;

        WorldState[GoapKey.assistisfollowing]  = assistIsFollowing;
        WorldState[GoapKey.assistrequestreturn] = assistCantFollow;

        // assistrequestreturnorisfollowing gates FollowRouteGoal on the leader.
        // Must be true while the assist is Following, CantFollow, OR NavigatingToLeader.
        // Without NavigatingToLeader: the leader enters NO PLAN the moment the assist
        // transitions from Following to NavigatingToLeader (both AnyAssistIsFollowing
        // and AnyAssistCantFollow are false), causing the stop/start patrol loop.
        // The distance gate in FollowRouteGoal.Update() already pauses the leader if
        // the assist falls beyond LeaderPauseYards, so keeping this flag true during
        // NavigatingToLeader is safe — the leader just continues patrolling at a normal
        // pace while the assist catches up.
        bool assistNavigating = classConfig.Mode == Mode.PartyLeader
            && assistStateStore.AnyAssistNavigating();

        WorldState[GoapKey.assistrequestreturnorisfollowing] =
            assistIsFollowing || assistCantFollow || assistNavigating || _evadeLeaderWaiting;

        // assistshouldfollow gates FollowFocusGoal on the assist.
        // assistStatusProvider.CantFollow acts as the override that keeps FFG selectable
        // during evade recovery and genuine CantFollow — even when combat conditions
        // (dmgTaken/dmgDone) would otherwise set this false and leave the assist with NO PLAN.
        WorldState[GoapKey.assistshouldfollow] =
            leaderConnection.HasValidLeaderState &&
            (assistStatusProvider.CantFollow || // override: keep FFG selectable during evade/CantFollow
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
                    // Use assist provider flag or store flag depending on mode
                    bool cantFollow = classConfig.Mode == Mode.PartyLeader
                        ? assistStateStore.AnyAssistCantFollow()
                        : assistStatusProvider.CantFollow;
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

                // Press StopAttack and ClearTarget unconditionally for both modes.
                //
                // CombatGoal.Update has an _evadeRecoveryActive branch
                // (CombatGoal.cs:191) that calls PressStopAttack + PressClearTarget,
                // but that branch is UNREACHABLE in practice: CombatGoal has
                // AddPrecondition(GoapKey.evadeRecovery, false). The moment
                // _evadeRecoveryUntilUtc is set above and the next GoapThread
                // iteration broadcasts GoapKey.evadeRecovery=true, the planner
                // deselects CombatGoal and calls OnExit. OnExit does not press
                // StopAttack (only stopMoving.Stop and PressEnableSoftInteract;
                // see CombatGoal.cs:168). Auto-attack remains toggled on.
                //
                // Symptom on warriors and other auto-attack classes: leader
                // continues swinging at the blacklisted mob until kill credit
                // or de-aggro. Observed in log 23:
                //   18:36:56:918  evade fires
                //   18:36:57:010  New Plan = Follow (CombatGoal exited via planner,
                //                  not via the _evadeRecoveryActive branch)
                //   (no StopAttack press anywhere after evade)
                //   18:37:17:403  Kill credit — mob died from continued attacks
                //                  22.5 seconds later.
                //
                // Pressing StopAttack here ensures the toggle is released the
                // instant the evade event is dispatched, regardless of which
                // goal is currently active or which goal will be selected next.
                // Idempotent: pressing while already stopped is a no-op.
                input.PressStopAttack();
                input.PressClearTarget();

                if (classConfig.Mode == Mode.PartyLeader)
                {
                    _evadeLeaderWaiting = true;
                    stopMoving.Stop();
                    input.StopForward(true);

                    // Publish the blacklisted GUID via API so the assist can call
                    // playerReader.IgnoreTarget without relying on the chat message.
                    leaderNavProvider.AddBlacklistedMobGuid(evade.TargetGuid);

                    foreach (var goal in AvailableGoals.OfType<FollowRouteGoal>())
                        goal.SuppressTargetFinderBriefly((int)(EvadeRecoveryDurationSec * 1000));
                }
            }
            else
            {
                logger.LogInformation(
                    $"[GoapAgent] Ghost combat escape (guid=0) — starting {GhostCombatEscapeDurationSec}s recovery window.");

                _evadeRecoveryUntilUtc = DateTime.UtcNow.AddSeconds(GhostCombatEscapeDurationSec);

                // Same fix as the real-evade branch above: PressStopAttack and
                // PressClearTarget unconditionally because CombatGoal's
                // _evadeRecoveryActive Update branch is unreachable due to the
                // precondition gate. See the long comment above for details.
                input.PressStopAttack();
                input.PressClearTarget();

                if (classConfig.Mode == Mode.PartyLeader)
                {
                    _evadeLeaderWaiting = true;
                    stopMoving.Stop();
                    input.StopForward(true);
                    // guid=0 means ghost combat — no specific mob to blacklist via API,
                    // the assist handles evade recovery via the status signal alone.

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
            : assistStatusProvider.CantFollow;

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

    /// <summary>
    /// Test-only entry point that injects a synthetic <see cref="GoapEventArgs"/>
    /// into the same dispatcher (<see cref="HandleGoapEvent"/>) that real goal-raised
    /// events flow through. Exists to verify the leader→assist blacklist + evade-recovery
    /// pipeline without needing to find a real evading mob in WoW Classic.
    /// <para>
    /// Bypasses the normal "goal raises event via SendGoapEvent" path and calls the
    /// dispatcher directly. This is observationally identical: every goal's
    /// <c>GoapEvent</c> is wired to <see cref="HandleGoapEvent"/> at agent construction
    /// (see the <c>a.GoapEvent += HandleGoapEvent</c> subscription at line 183), and
    /// no goal's <see cref="IGoapEventListener.OnGoapEvent"/> branches on
    /// <see cref="EvadeBlacklistEvent"/> — only <see cref="HandleGoapEvent"/> does.
    /// So there is no observable difference between routing through a goal and
    /// calling the dispatcher directly.
    /// </para>
    /// <para>
    /// Refuses non-PartyLeader modes: invoking on an assist would arm the assist's
    /// own evade-recovery window via the <see cref="EvadeBlacklistEvent"/> branch,
    /// which is harmless but not useful for the leader-side test it exists to support.
    /// The assist's evade-recovery is already exercised end-to-end as a side effect
    /// of injecting on the leader (the GUID propagates via the API to the assist's
    /// FFG blacklist diff loop, which itself raises <see cref="EvadeBlacklistEvent"/>
    /// locally).
    /// </para>
    /// <para>
    /// Threading: called from an HTTP request thread, not the GOAP thread. Same call
    /// pattern as the <see cref="Active"/> property setter (which calls
    /// <c>stopMoving.Stop()</c> and <c>input.Reset()</c> from the UI thread); the
    /// underlying handlers were already designed to tolerate this.
    /// </para>
    /// </summary>
    /// <returns>
    /// True if the event was dispatched. False if refused — currently only when
    /// the bot is not running in <see cref="Mode.PartyLeader"/>.
    /// </returns>
    public bool RaiseDebugEvent(GoapEventArgs e)
    {
        if (classConfig.Mode != Mode.PartyLeader)
        {
            logger.LogWarning(
                $"[GoapAgent] RaiseDebugEvent refused: mode is {classConfig.Mode}, " +
                "expected PartyLeader. The leader-side blacklist + evade-recovery " +
                "pipeline cannot be exercised on an assist.");
            return false;
        }

        logger.LogInformation(
            $"[GoapAgent] RaiseDebugEvent dispatching synthetic {e.GetType().Name} " +
            "via HandleGoapEvent — TEST-INDUCED, not from a real evade trigger.");

        HandleGoapEvent(e);
        return true;
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
