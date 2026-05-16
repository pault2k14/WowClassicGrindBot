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
    // Session blacklist tracking (used by both PartyLeader and AssistFocus)
    //
    // Original purpose (assist-side, pre-Fix 15): tracked GUIDs already
    // observed in the leader's BlacklistedMobGuids API field. When a NEW
    // GUID arrived, the agent-level diff in GoapThread dispatched
    // EvadeBlacklistEvent so the assist's CombatGoal exited via its
    // evadeRecovery precondition.
    //
    // Critical placement (agent, not FFG): Combat is cost 4 and FFG is cost
    // 19 — Combat preempts FFG. A leader-published blacklist GUID dispatched
    // during the assist's combat would otherwise not be seen by the assist
    // until combat ended naturally, by which point the mob is already dead.
    // Observed in log 22 (assist 18:03:34 through 18:03:46): the assist
    // cast Smite 4 times against the blacklisted GUID before FFG.OnEnter
    // ran the original FFG-level diff — 3 seconds AFTER kill credit.
    //
    // Fix 15 extension: also populated by the EvadeBlacklistEvent handler
    // (line ~1040) regardless of mode. This means the leader's own evade
    // dispatches (from TargetFinder / ATG / PTG) now also record the GUID
    // here. The per-tick refresh block in GoapThread iterates this set and
    // keeps every guid's IsIgnored TTL alive as long as the bot is inside
    // any blacklist rect inflated by BlacklistMemoryBufferYards.
    //
    // Fix 16 (log-43 22:35:50 → 22:41:23 stuck cycle): this set is
    // append-only per session. Cleanup of expired guids was removed because
    // it broke the API observer's dedup invariant — see the explanatory
    // comment in GoapThread at the refresh block. Memory cost of unbounded
    // session growth is negligible (tens to hundreds of unique guids per
    // session = a few KB at most).
    // -----------------------------------------------------------------------
    private readonly System.Collections.Generic.HashSet<int> _knownBlacklistedGuids = new();

    // Fix 15: inflation radius applied to each blacklist rect when deciding
    // whether to refresh _knownBlacklistedGuids. 35 y covers typical caster
    // cast range in classic WoW (most spells 30 y, some 35 y) and gives a
    // small safety margin beyond DetourMargin=12 y (the geometric buffer
    // Navigation uses for path detours). The result: as long as the bot is
    // anywhere within caster reach of a rect, every guid we've ever
    // blacklisted during this session stays on IsIgnored — the planner
    // cannot decide "the 30 s wall-clock TTL has elapsed, this mob is now
    // fair game" while we're still standing in cast range of the rect.
    private const float BlacklistMemoryBufferYards = 35f;

    // Fix 15: tracks whether the bot was inside the BlacklistMemoryBuffer-
    // inflated zone on the previous tick, so we can log entry / exit
    // transitions once rather than every tick.
    private bool _previouslyInBlacklistMemoryZone;

    // Fix 13: tracks the GUID currently being overridden by the IsIgnored
    // self-defense exception in UpdateWorldState. Used purely to debounce the
    // override log so it fires once per (guid, attack-session) transition
    // rather than every GOAP tick. Reset to 0 when no override is active.
    private int _selfDefenseOverrideGuid;

    // Fix L (log-58 10:12:22:148 onwards): latch for the party-assist
    // override, mirroring _selfDefenseOverrideGuid above. When the focus
    // (= leader in AssistFocus mode) is engaging an IsIgnored target,
    // we flip focusTargetIgnored=false at the planner level so the assist's
    // CombatGoal precondition allPartyTargetsIsIgnored=false is met. The
    // latch holds the focus-target GUID currently overridden — used purely
    // to gate the log message so it fires once per activation rather than
    // every tick. Behavior is the same on every tick the override conditions
    // hold; the latch only suppresses log spam (matching the existing Fix 17
    // pattern at line 1014-1023 above).
    private int _partyAssistOverrideGuid;

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
        bool previousAssistStatusCantFollow = false;
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

                        // Log whether the union-diff transition is flag-only or
                        // status-backed at the time it fires. The actual
                        // GoToOneWaypoint call is now driven by the separate
                        // status-CantFollow diff below (Fix I-1, log-55), so
                        // this branch is broadcast-only — it always fires
                        // AssistRequestReturn (the request-to-return GoapEvent
                        // that goals like ConsumeCorpse/Loot listen for) and
                        // never directly issues a GoToOneWaypoint. The
                        // GoToOneWaypoint trigger was moved out because the
                        // flag-only path (leader's own mob-blacklist diff at
                        // GoapAgent.cs:352) latches previousAssistCantFollow=true
                        // before the assist's status formally flips to
                        // BotStatus.CantFollow — leaving the union diff with
                        // no transition to detect when status actually arrives.
                        // Log-55 evidence: flag set at 00:12:47:326, status
                        // flip at 00:12:46:830 (visible to leader at ~00:12:47:710);
                        // union diff fired once on the flag transition, never
                        // again on the status transition; GoToOneWaypoint never
                        // executed; leader stranded.
                        AssistState? cantFollowForLog = assistStateStore.GetCantFollowState();
                        if (cantFollowForLog != null)
                        {
                            logger.LogInformation(
                                $"[GoapAgent] AnyAssistCantFollow rising edge — status=CantFollow " +
                                $"available ({cantFollowForLog.MapX:0.00},{cantFollowForLog.MapY:0.00}). " +
                                $"AssistRequestReturn broadcast. GoToOneWaypoint will be issued by " +
                                $"the status-diff handler below.");
                        }
                        else
                        {
                            logger.LogInformation(
                                "[GoapAgent] AnyAssistCantFollow rising edge — flag only " +
                                "(status not BotStatus.CantFollow yet). AssistRequestReturn " +
                                "broadcast. GoToOneWaypoint deferred until status transitions.");
                        }
                    }
                    else
                    {
                        AssistNotRequestReturn();
                        previousAssistCantFollow = false;
                    }
                }

                // Fix I-1 (log-55 00:12:47:710 leader, assist 00:12:46:830:
                // GoToOneWaypoint never fired despite assist's status formally
                // becoming CantFollow): status-specific diff. The union diff
                // above latches previousAssistCantFollow=true on the FLAG-only
                // path (leader's own mob-blacklist diff at line ~440 below
                // sets only the flag, not the status). When the assist's
                // actual status later transitions to BotStatus.CantFollow,
                // the union diff sees no change (already true) and never
                // fires the GoToOneWaypoint that Fix 33 (A+B+C) was supposed
                // to handle. By tracking GetCantFollowState() != null
                // separately, we fire GoToOneWaypoint on every status-
                // CantFollow rising edge regardless of flag state.
                // Idempotent: if status transitions before flag (no prior
                // mob-blacklist event), the union diff and the status diff
                // both detect the rising edge — the status diff fires
                // GoToOneWaypoint exactly once on the rising edge of its
                // own tracker.
                AssistState? statusCantFollowState = assistStateStore.GetCantFollowState();
                bool currentAssistStatusCantFollow = statusCantFollowState != null;
                if (currentAssistStatusCantFollow != previousAssistStatusCantFollow)
                {
                    if (currentAssistStatusCantFollow)
                    {
                        logger.LogInformation(
                            $"[GoapAgent] Fix I-1: Assist status -> BotStatus.CantFollow — " +
                            $"navigating leader to assist position " +
                            $"({statusCantFollowState!.MapX:0.00},{statusCantFollowState.MapY:0.00}).");
                        foreach (var goal in AvailableGoals.OfType<FollowRouteGoal>())
                            goal.GoToOneWaypoint(statusCantFollowState.MapPosNoZ);
                    }
                    previousAssistStatusCantFollow = currentAssistStatusCantFollow;
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
            //   1. IgnoreTarget(guid) — adds to PlayerReader.BlacklistAreaMobs
            //      (Core, not the addon) with the 30 s TTL declared on
            //      PlayerReader.BLACKLIST_IGNORE_SECONDS. While the entry is
            //      live, IsIgnored(guid) returns true and the IsIgnored
            //      short-circuits in CombatGoal/ATG/PTG.Update bail the goal
            //      cleanly. After ~30 s the entry self-expires; by that point
            //      the leader should have retreated far enough that the mob
            //      is no longer relevant (FRG.wantNavPaused's IsIgnored term
            //      from session 26 ensures retreat actually happens).
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

            // Fix J (log-56 01:32:08:357 leader auto-blacklisted guid=279717
            // via base-library "AreaBlacklistMob on attack!" path, but
            // _evadeRecoveryUntilUtc was never set — Fix 17 self-defense
            // override fired at 01:32:19:719 and the leader engaged the
            // blacklisted mob; assist's Fix 17 fired at 01:32:28:856 and
            // assist killed the blacklisted mob at 01:32:38:253; entire
            // sequence had evadeRecovery=false on both bots): mirror the
            // assist's diff loop above (line 441-460) on the leader side.
            //
            // The leader's blacklist additions come from multiple paths:
            //   (1) Goal-side dispatch — ApproachTargetGoal.cs (lines 270,
            //       303, 334, 494, 647) and CombatGoal.cs (lines 441, 470,
            //       524, 1016) call SendGoapEvent(new EvadeBlacklistEvent(guid))
            //       BEFORE playerReader.IgnoreTarget — these properly start
            //       the recovery window.
            //   (2) Base-library auto-blacklist — Core.BlacklistTarget
            //       class's "AreaBlacklistMob on attack!" trigger calls
            //       playerReader.IgnoreTarget directly when the bot is
            //       attacked by a mob in an area-blacklist rect. NO
            //       EvadeBlacklistEvent is dispatched. The Adhoc "Blacklist
            //       Target" plan that follows is a key-press goal (F10 macro
            //       to broadcast via chat), not an event dispatcher.
            //
            // Without (2) being covered: _evadeRecoveryUntilUtc stays at
            // MinValue, GoapKey.evadeRecovery stays false, Fix 17's
            // !EvadeRecoveryActive gate never trips, and the bot re-engages
            // the blacklisted mob the moment it attacks again. The
            // leaderNavProvider.AddBlacklistedMobGuid call inside
            // HandleGoapEvent's leader branch (line ~1215) also never
            // executes, so the assist's API-diff loop (line 444) never sees
            // the guid — both bots end up engaging.
            //
            // Detection: a guid is in playerReader.IsIgnored AND not in
            // _knownBlacklistedGuids. Path (1) adds the guid to
            // _knownBlacklistedGuids inside HandleGoapEvent (line ~1205)
            // BEFORE this diff sees it next tick, so path (1) never triggers
            // a duplicate dispatch. Path (2) bypasses _knownBlacklistedGuids
            // entirely — this loop catches that and dispatches the missing
            // event. Checks target and focusTarget; auto-blacklist most
            // commonly hits the current target, but focusTarget is also
            // possible (e.g., the leader's current focus changes to an
            // area-blacklisted mob).
            if (classConfig.Mode == Mode.PartyLeader)
            {
                int leaderTargetGuid = playerReader.TargetGuid;
                int leaderFocusTargetGuid = playerReader.FocusTargetGuid;

                if (leaderTargetGuid != 0
                    && playerReader.IsIgnored(leaderTargetGuid)
                    && !_knownBlacklistedGuids.Contains(leaderTargetGuid))
                {
                    logger.LogInformation(
                        $"[GoapAgent] Fix J: leader-side auto-blacklist detected — " +
                        $"target guid={leaderTargetGuid} is in IsIgnored but absent " +
                        $"from _knownBlacklistedGuids (no prior EvadeBlacklistEvent). " +
                        $"Dispatching event to start 25s recovery window and " +
                        $"API-broadcast to assist.");
                    HandleGoapEvent(new EvadeBlacklistEvent(leaderTargetGuid));
                }

                if (leaderFocusTargetGuid != 0
                    && leaderFocusTargetGuid != leaderTargetGuid
                    && playerReader.IsIgnored(leaderFocusTargetGuid)
                    && !_knownBlacklistedGuids.Contains(leaderFocusTargetGuid))
                {
                    logger.LogInformation(
                        $"[GoapAgent] Fix J: leader-side auto-blacklist detected — " +
                        $"focusTarget guid={leaderFocusTargetGuid} is in IsIgnored but " +
                        $"absent from _knownBlacklistedGuids. Dispatching " +
                        $"EvadeBlacklistEvent.");
                    HandleGoapEvent(new EvadeBlacklistEvent(leaderFocusTargetGuid));
                }
            }

            // ── Fix 15 + Fix 16: per-tick blacklist memory refresh ───────────
            //
            // Original Fix 15 motivation (caster cycle, session 49): caster mob
            // in a blacklist rect → patrol detour with DetourMargin=12 y brings
            // the bot inside the caster's ~30 y cast range → caster keeps
            // hitting the bot during evade → 30 s IsIgnored TTL on wall-clock
            // decays → patrol routes us back near the rect → next aggro starts
            // a fresh evade cycle, repeat.
            //
            // Fix: as long as the bot is inside any blacklist rect inflated by
            // BlacklistMemoryBufferYards (35 y, covering most caster cast
            // ranges), refresh the IsIgnored TTL on every guid we've
            // blacklisted this session. The TTL only starts decaying for real
            // once the patrol has carried the bot geographically clear of
            // every inflated zone.
            //
            // Runs every tick regardless of mode, current goal, evade window
            // state, or anything else. The refresh action is purely
            // geographic — "as long as we're near the rect, remember
            // everything we've already learned to avoid."
            //
            // Fix 16 (log-43 22:35:50 onward — repeated 30 s re-detection
            // cycles of guid=182020): the original Fix 15 also cleaned up
            // _knownBlacklistedGuids when a guid's IsIgnored TTL had decayed
            // (bot moved geographically clear). This was incorrect — the same
            // set is used by the API observer above (line ~410) to dedup
            // "have we ever seen this guid from the leader's broadcast." When
            // the cleanup removed a guid, the next API observer pass saw
            // _knownBlacklistedGuids.Add(guid) return true (because we just
            // removed it) and treated the guid as freshly arrived, re-firing
            // EvadeBlacklistEvent AND setting assistStatusProvider.CantFollow
            // (line ~413) — which propagates as assistrequestreturn=true,
            // blocking LootGoal, ConsumeCorpseGoal, and FollowFocusGoal (all
            // require assistrequestreturn=false). Observed: assist stuck in
            // NO PLAN for ~1.5 minutes at log end after a kill at 22:39:52,
            // because LeaderNavigationProvider has no per-guid Remove and
            // the leader broadcasts the GUID forever. Every 30 s the cycle
            // repeats: cleanup → API re-detect → CantFollow → blocked goals.
            //
            // Resolution: drop the cleanup pass entirely. The set is naturally
            // bounded by unique mobs encountered per session (low: tens to
            // hundreds, not thousands), so unbounded session growth is a
            // non-issue. Refresh continues to iterate the set each tick while
            // in zone — at typical set sizes this is microseconds. The
            // pre-Fix-15 invariant ("API observer Add returns true only on
            // first observation per session") is restored.
            bool currentlyInBlacklistMemoryZone =
                navigation.AreaBlacklist != null &&
                navigation.AreaBlacklist.TryGetContainingRectInflated(
                    playerReader.WorldPos, BlacklistMemoryBufferYards, out _);

            if (currentlyInBlacklistMemoryZone != _previouslyInBlacklistMemoryZone)
            {
                _previouslyInBlacklistMemoryZone = currentlyInBlacklistMemoryZone;
                logger.LogInformation(
                    $"[GoapAgent] Bot {(currentlyInBlacklistMemoryZone ? "entered" : "exited")} " +
                    $"blacklist-memory zone (inflate={BlacklistMemoryBufferYards}y, " +
                    $"trackedGuids={_knownBlacklistedGuids.Count}, " +
                    $"pos={playerReader.WorldPos}).");
            }

            if (currentlyInBlacklistMemoryZone && _knownBlacklistedGuids.Count > 0)
            {
                foreach (int guid in _knownBlacklistedGuids)
                    playerReader.IgnoreTarget(guid);
            }

            // ── Evade recovery world state ────────────────────────────────
            bool evadeRecoveryActive = DateTime.UtcNow < _evadeRecoveryUntilUtc;
            if (evadeRecoveryActive != previousEvadeRecovery)
            {
                previousEvadeRecovery = evadeRecoveryActive;
                BroadcastGoapEvent(GoapKey.evadeRecovery, evadeRecoveryActive);

                // Mirror to AssistStatusProvider so PartyStatePublisher can
                // suppress its Following → Combat override during the window.
                // See AssistStatusProvider.EvadeRecoveryActive for the full
                // rationale; without this mirror the publisher would mask the
                // assist's correct Following claim with Combat for the entire
                // 25 s, the leader's diff loop would observe assist not-Following
                // / not-Navigating, and FRG.OnGoapEvent would Abort. The
                // assignment is unconditional on mode because AssistFocus is the
                // only mode where the publisher runs (gated in BuildSnapshot),
                // so this is a no-op for PartyLeader-side ticks.
                assistStatusProvider.EvadeRecoveryActive = evadeRecoveryActive;

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
                // NO PLAN transition. Exit the previously-running goal so any
                // in-flight input state (movement keys held by navigation.Update,
                // approach state, soft-interact toggles, ghost-combat tracking,
                // etc.) is released cleanly — every goal's OnExit is designed
                // to do this. Without the explicit OnExit, the previous goal's
                // last frame of pressed keys (e.g. FFG's W/Right/Left from
                // navigation.Update) stays pressed for the entire NO PLAN
                // window because no goal's Update is called and no transition-
                // to-different-goal triggers OnExit at line 469. Observed in
                // log 28: 01:23:07:484 NO PLAN → 23 s of autorun → 01:23:30:844
                // Combat selected, only then did FFG.OnExit fire. CurrentGoal=
                // null keeps the next-iteration path consistent with the
                // existing invariant — when NextGoal eventually returns a
                // non-null goal, line 466's `newGoal != CurrentGoal` test
                // becomes true and the new goal goes through OnEnter normally.
                if (CurrentGoal != null)
                {
                    CurrentGoal.OnExit();
                    CurrentGoal = null;
                }
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
        logger.LogInformation("GoapKey.partyEngaging: " +
            (PartyInCombat() ||
             (classConfig.Mode == Mode.AssistFocus && leaderNavProvider.HasApproachStart)));
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
        bool targetIgnoredLog = IsTargetIgnoredOrAbsent(hasTarget, playerReader.TargetGuid);
        bool focusTargetIgnoredLog = IsTargetIgnoredOrAbsent(b.FocusTarget(), playerReader.FocusTargetGuid);
        logger.LogInformation("GoapKey.targetIsIgnored: " + targetIgnoredLog);
        logger.LogInformation("GoapKey.focusTargetIsIgnored: " + focusTargetIgnoredLog);
        logger.LogInformation("GoapKey.allPartyTargetsIsIgnored: " + (targetIgnoredLog && focusTargetIgnoredLog));

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

        // ── Target / focus-target ignore state ────────────────────────────
        // "Ignored" means: not present, OR present but in PlayerReader.IsIgnored
        // (the per-bot blacklist with 30 s TTL set by every evade dispatch).
        // CombatGoal in PartyLeader / AssistFocus modes uses these to decide
        // whether ANY target slot in the party offers something fightable —
        // see GoapKey.allPartyTargetsIsIgnored for the full rationale.
        bool targetIgnored = IsTargetIgnoredOrAbsent(hasTarget, playerReader.TargetGuid);

        // Fix 13 (log-42 17:22:57:134 → 17:23:01:852: 4.7 s NO PLAN gap during
        // active combat): the IsIgnored TTL is 30 s (PlayerReader.BLACKLIST_IGNORE_SECONDS,
        // per the comment block at line ~344 of this file) but EvadeRecoveryDurationSec
        // is 25 s. The 5 s overlap is a dead zone: the evade window has ended
        // (planner sees evadeRecovery=false) but the blacklisted mob's GUID is
        // still on IsIgnored → CombatGoal's allPartyTargetsIsIgnored=false
        // precondition blocks the plan. If the bot is still being hit during
        // that 5 s, it stands still taking damage (assist GoapKey dump at
        // 17:22:57:135 in log-42: hastarget=True, damagetaken=True,
        // targettargetsus=True, incombat=True, evadeRecovery=False, but
        // New Plan = NO PLAN for the full 4.7 s until IsIgnored self-expires
        // at the 30 s mark).
        //
        // Original design assumption (line ~347 comment): "After ~30 s the
        // entry self-expires; by that point the leader should have retreated
        // far enough that the mob is no longer relevant." This holds when the
        // bot actually moves away during the 25 s evade window. In AssistFocus
        // mode the assist follows the leader and may stay near the mob; the
        // leader may also hold position if it's also in evade. The mob is
        // still relevant when IsIgnored expires.
        //
        // Self-defense override design (per user, session 49 follow-up): the
        // overriding principle is that blacklisted areas are geographic
        // regions to avoid — fighting inside one defeats the purpose of the
        // blacklist. Combat must only be re-enabled when the bot has actually
        // escaped. The user articulated this as two combat-allowed conditions:
        //   1. Evade window is over AND mob is still in combat with us AND
        //      we have moved outside the blacklist area.
        //   2. We have moved outside the blacklist area AND we are fighting
        //      a non-blacklisted mob.
        // Case (2) is the normal flow — targetIgnored is already false for a
        // non-blacklisted target and CombatGoal runs through standard
        // preconditions; no override needed. Case (1) is the only scenario
        // this override addresses, and ALL of its conjuncts must hold:
        //
        // Required conjuncts (and the log evidence each guards against):
        //   - targetIgnored                              the slot would otherwise block Combat
        //   - hasTarget && playerCombat && dmgTaken      really in active combat (not a stale
        //                                                rolling-window damage echo)
        //   - TargetTarget is Me or Pet                  the mob is locked onto US, not the
        //                                                leader (PartyOrPet excluded — the
        //                                                leader's own GoapAgent runs this
        //                                                same code path for its own slot)
        //   - !evadeRecoveryActive                       evade window has already ended;
        //                                                during evade the bot's job is to
        //                                                retreat, not to engage
        //   - !navigation.IsInBlacklistArea()            bot is geographically outside all
        //                                                blacklist rects — the core principle.
        //                                                If bot is inside a rect, escape
        //                                                takes priority (Fix 12 in FFG, or
        //                                                Navigation's blacklist-aware patrol
        //                                                in FRG).
        //
        // What this override does NOT do: it does not modify the IsIgnored
        // map itself, the evade window, focusTargetIgnored, or add any
        // retreat behavior. It only flips one frame's WorldState value when
        // the bot is geographically safe AND being actively attacked AND the
        // evade window has elapsed — exactly the scenario in log-42 where
        // the assist sat outside the blacklist at <942.65, 296.71> taking
        // hits for 4.7 s after the evade window expired.
        // Fix 22 (log-46 02:46:17:399 → 02:46:19:453, ~2 s combat then NO PLAN):
        // latch the override across BL entry. The bot was attacked outside the
        // rect, override fired correctly (insideBlacklistArea=false), Combat
        // plan ran, bot pressed Approach toward the mob (which was inside the
        // rect). Two seconds later the bot's chase had carried it into the rect
        // (WorldState dump at 02:46:19:454: `inblacklistarea: True`). The
        // `!botInsideBlacklistArea` term flipped False → override dropped →
        // `allPartyTargetsIsIgnored` reverted to True → Combat precondition
        // failed → NO PLAN. The 2 s window was insufficient for the bot to
        // reach spell range; only Approach keys fired, no offensive spells.
        //
        // The latch: once the override is active for a given target GUID
        // (initial activation while bot was geographically safe), subsequent
        // ticks remain in override even if the chase carries the bot into the
        // rect. The override still drops naturally on: target gone
        // (hasTarget=false), target switched, combat ended (playerCombat=false),
        // damage stopped (dmgTaken=false), the mob retargeted off us
        // (TargetTarget no longer Me/Pet), or evade-recovery window opened.
        //
        // Fix 26 (log-49 12:09:35:583 leader NO PLAN / 12:09:47:245 assist
        // NO PLAN): drop the !botInsideBlacklistArea restriction entirely
        // for INITIAL activations too. The user's design intent is that
        // self-defense fires regardless of BL position — when a mob is
        // actively attacking us (TargetTarget=Me/Pet) and we've taken
        // damage, fighting back takes priority over the "escape rect first"
        // heuristic that motivated the original BL guard.
        //
        // Evidence from log-49 leader at 12:09:35:583 (full self-defense
        // setup blocked solely by inblacklistarea=True):
        //   hastarget=True, targethostile=True, targetisalive=True,
        //   targettargetsus=True, incombat=True, damagetaken=True,
        //   targetIsIgnored=True, evadeRecovery=False, inblacklistarea=True
        // Same shape at assist 12:09:47:245. Without the override, both
        // ran allPartyTargetsIsIgnored=True → Combat-precondition fails →
        // NO PLAN → the bots stood still while ignored mobs killed them.
        //
        // The latch (overrideAlreadyLatched) is preserved for the log
        // message and for the new-activation-vs-continuation branch at
        // line ~876 (only log a fresh activation once per guid), but it
        // no longer guards entry. The remaining conjuncts (target actively
        // hits us, we're in combat, we've taken damage, evade window
        // closed) are sufficient to identify genuine self-defense and
        // exclude "engaging an ignored mob we happen to be next to" —
        // a non-attacking nearby mob will not have TargetTarget=Me/Pet.
        bool evadeRecoveryActive = DateTime.UtcNow < _evadeRecoveryUntilUtc;
        bool botInsideBlacklistArea = navigation.IsInBlacklistArea();
        bool overrideAlreadyLatched =
            _selfDefenseOverrideGuid != 0 &&
            _selfDefenseOverrideGuid == playerReader.TargetGuid;
        bool selfDefenseOverride =
            targetIgnored &&
            hasTarget && playerCombat && dmgTaken &&
            playerReader.TargetTarget is UnitsTarget.Me or UnitsTarget.Pet &&
            !evadeRecoveryActive;
        if (selfDefenseOverride)
        {
            targetIgnored = false;
            if (_selfDefenseOverrideGuid != playerReader.TargetGuid)
            {
                _selfDefenseOverrideGuid = playerReader.TargetGuid;
                logger.LogInformation(
                    $"[GoapAgent] IsIgnored self-defense override: target guid={playerReader.TargetGuid} " +
                    $"is on IsIgnored map but actively attacking us (TargetTarget={playerReader.TargetTarget}, " +
                    $"playerCombat=true, dmgTaken=true, evadeRecovery=false, " +
                    $"insideBlacklistArea={botInsideBlacklistArea}, latched={overrideAlreadyLatched}) — " +
                    $"treating as not-ignored so Combat plan can engage.");
            }
        }
        else if (_selfDefenseOverrideGuid != 0)
        {
            _selfDefenseOverrideGuid = 0;
        }

        bool focusTargetIgnored = IsTargetIgnoredOrAbsent(b.FocusTarget(), playerReader.FocusTargetGuid);

        // Fix L (log-58 10:12:22 onwards: leader engaging blacklisted 311297
        // via Fix 17 self-defense, but assist's CombatGoal blocked by
        // allPartyTargetsIsIgnored=true; assist stood near the rect edge for
        // 14 s while leader fought alone — user observed "shouldn't the
        // assist also be able to attack the blacklisted mob").
        //
        // Expanded in log-60 from AssistFocus-only to BOTH party modes
        // (log-60 12:49:43:546 onwards: leader's recovery elapsed, assist's
        // Fix 17 was engaging 320802 since 12:49:41:139, but leader's
        // CombatGoal was blocked by allPartyTargetsIsIgnored=true; leader
        // navigated SW to assist's CantFollow position via Fix I-1 AssistReturn
        // but never planned Combat — "the leader approached the mob but didn't
        // participate in combat", assist killed 320802 alone). The logic is
        // fundamentally symmetric: when partner is engaging an IsIgnored mob
        // via Fix 17 self-defense, this bot should join. Both directions
        // (leader-helps-assist and assist-helps-leader) use the same focus-
        // chain detection.
        //
        // Detection (isPartyMode): the focus's (= partner's) target is
        // IsIgnored AND in combat. The only path that puts an IsIgnored
        // target into combat is Fix 17 self-defense — so this reliably
        // signals "partner has committed to engaging a blacklisted mob,
        // retreat has failed". In Standalone Grind mode, bits.FocusTarget()
        // returns false (no focus set), so the condition short-circuits
        // and Fix L doesn't fire — safe.
        //
        // Effect: flip focusTargetIgnored=false locally. This makes
        //   allPartyTargetsIsIgnored = targetIgnored && focusTargetIgnored
        //                            = (anything) && false = false
        // CombatGoal's AssistFocus/PartyLeader precondition
        // (CombatGoal.cs:106/119) is then satisfied, the planner selects
        // Combat, and CombatGoal's Case 2 (line ~417) swaps the bot's
        // target to the partner's via TargetFocus + TargetOfTarget. From
        // there, the CombatGoal-side Fix L mirror keeps the bot engaged
        // through Case 3 by also flipping currentTargetIsIgnored after
        // the swap.
        //
        // !evadeRecoveryActive: during the 25 s recovery window, this
        // override does NOT fire. The design intent during recovery
        // ("retreat") is preserved. Only after recovery elapses — when
        // the partner has already committed to engagement via Fix 17 —
        // does this bot join.
        //
        // Latched via _partyAssistOverrideGuid for log-once-per-activation
        // (matching Fix 17's _selfDefenseOverrideGuid pattern at line ~1014).
        // The flip itself fires every tick the conditions hold; only the
        // log message is gated.
        bool isPartyModeForFixL = classConfig.Mode == Mode.PartyLeader
                                || classConfig.Mode == Mode.AssistFocus;
        if (isPartyModeForFixL &&
            focusTargetIgnored &&
            b.FocusTarget() &&
            b.FocusTarget_Combat() &&
            playerReader.FocusTargetGuid != 0 &&
            !evadeRecoveryActive)
        {
            focusTargetIgnored = false;
            if (_partyAssistOverrideGuid != playerReader.FocusTargetGuid)
            {
                _partyAssistOverrideGuid = playerReader.FocusTargetGuid;
                logger.LogInformation(
                    $"[GoapAgent] Fix L party-assist override: focus target " +
                    $"guid={playerReader.FocusTargetGuid} is on IsIgnored map but " +
                    $"the partner ({(classConfig.Mode == Mode.PartyLeader ? "assist" : "leader")}) " +
                    $"is in combat with it (partner-side Fix 17 active). " +
                    $"Flipping focusTargetIgnored=false so this bot's CombatGoal " +
                    $"precondition allPartyTargetsIsIgnored=false is met. " +
                    $"(Mode={classConfig.Mode})");
            }
        }
        else if (_partyAssistOverrideGuid != 0 && isPartyModeForFixL)
        {
            logger.LogInformation(
                $"[GoapAgent] Fix L party-assist override cleared (was " +
                $"guid={_partyAssistOverrideGuid}). Conditions no longer hold: " +
                $"focusTargetIgnored={focusTargetIgnored}, " +
                $"focusHasTarget={b.FocusTarget()}, " +
                $"focusTargetCombat={b.FocusTarget_Combat()}, " +
                $"evadeRecovery={evadeRecoveryActive}. " +
                $"(Mode={classConfig.Mode})");
            _partyAssistOverrideGuid = 0;
        }

        WorldState[GoapKey.targetIsIgnored]          = targetIgnored;
        WorldState[GoapKey.focusTargetIsIgnored]     = focusTargetIgnored;
        WorldState[GoapKey.allPartyTargetsIsIgnored] = targetIgnored && focusTargetIgnored;

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
        // Four branches keep FFG selectable:
        //   1. evadeRecoveryActive — the authoritative override during the
        //      25 s blacklist-evade window. Throughout this window CombatGoal
        //      is precondition-blocked (evadeRecovery=false fails) and TFT
        //      is gated by its own _evadeRecoveryActive flag, so FFG is the
        //      ONLY goal that can navigate the assist. Latched combat
        //      residue (dmgDone/dmgTaken from a Smite cast just before the
        //      evade dispatch) must NOT block FFG here, otherwise the
        //      planner returns NO PLAN, GoapAgent's NO-PLAN-OnExit fires
        //      (session 28 fix), FFG.OnExit sets BotStatus.Waiting, the
        //      leader's diff loop sees assist→Waiting, broadcasts
        //      AssistIsNotFollowing(), and FRG.OnGoapEvent aborts patrol —
        //      observed in log 29 at assist 02:18:15:578 → leader 02:18:15:897.
        //   2. assistStatusProvider.CantFollow — genuine "the assist truly
        //      cannot get back to the leader" (navigation exhausted, path
        //      failed). Set by the goals listed in AssistStatusProvider.cs.
        //      No longer overloaded with evade-recovery duty, so its lifecycle
        //      (cleared in FFG line 535/707 when the assist settles) is no
        //      longer fragile around the evade window.
        //   3. Normal path — no forced-follow chat command and no recent
        //      damage. This is the steady-state "assist should follow leader
        //      because nothing else is going on."
        //   4. Fix K-2 (log-57 09:16:29:147 assist NO PLAN: hastarget=False,
        //      focusTargetIgnored=True, allPartyTargetsIsIgnored=True,
        //      damagetaken=True, damagedone=False, evadeRecovery=False —
        //      all of branches 1/2/3 failed, FFG was un-selectable, plan
        //      resolved to NO PLAN, assist stood idle 7 s while the leader
        //      headed off to engage the blacklisted mob via Fix 17): if
        //      both the assist's own target and the focus's target are
        //      IsIgnored/absent (= the existing allPartyTargetsIsIgnored
        //      condition computed at line 1033), then the party has
        //      nothing fightable and CombatGoal's
        //      allPartyTargetsIsIgnored precondition is already blocking
        //      it from firing — FFG is the correct fallback. Branch 3's
        //      "no recent damage" guard was a proxy for "not actively
        //      fighting"; the proxy breaks when fresh damage lingers but
        //      no target exists (e.g., heal cast triggers Combat-flag,
        //      then no follow-up target acquired). Falling back to FFG
        //      lets the assist navigate to the leader's new position
        //      rather than freeze in place.
        // Note: evadeRecoveryActive is declared earlier in this method (Fix 13
        // block, line ~846) and is still in scope here. Reusing it rather
        // than redeclaring avoids CS0128. targetIgnored (line 895) and
        // focusTargetIgnored (line 1030) are also already in scope.
        WorldState[GoapKey.assistshouldfollow] =
            leaderConnection.HasValidLeaderState &&
            !chatReader.ForcedFollow &&
            (evadeRecoveryActive ||
             assistStatusProvider.CantFollow ||
             (!(playerCombat && dmgTaken) && !dmgDone && !dmgTaken) ||
             (targetIgnored && focusTargetIgnored));

        WorldState[GoapKey.partymembercombat]  = PartyMemberInCombat();
        WorldState[GoapKey.partyleadercombat]  = PartyLeaderInCombat();
        WorldState[GoapKey.partyincombat]      = PartyInCombat();

        // Fix AH (log-73 16:43:37 → 16:44:15:225): partyEngaging is the
        // disjunction of partyincombat and "the leader has published an
        // approach-start anchor" (HasApproachStart). It is consumed only by
        // ApproachTargetGoal's AssistFocus precondition (was: partyincombat=true;
        // now: partyEngaging=true), so it makes ATG selectable on the assist
        // during the leader's pre-combat approach window. partyincombat itself
        // is preserved verbatim above — FRG.OnGoapEvent and other consumers
        // still receive the "actual combat is happening" semantics they rely on.
        //
        // Mode gate (AssistFocus only): HasApproachStart is set by the leader's
        // ATG.OnEnter and broadcast via LeaderNavigationProvider. The leader's
        // own GoapAgent has no business reading the leader's own flag back —
        // ATG is the goal that publishes it. For PartyLeader and Grind modes,
        // partyEngaging collapses to partyincombat (the historical semantics)
        // because the leader-approaching disjunct is mode-gated.
        //
        // Why a key and not a CanRun gate: GOAP preconditions are pure boolean
        // tests against WorldState. Moving this into ATG.CanRun would mean ATG
        // is never selected for planning at all (the planner short-circuits on
        // unsatisfied preconditions before reaching the goal's body). A new
        // key, computed once per UpdateWorldState, is the idiomatic match.
        WorldState[GoapKey.partyEngaging] =
            PartyInCombat() ||
            (classConfig.Mode == Mode.AssistFocus && leaderNavProvider.HasApproachStart);

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

        WorldState[GoapKey.evadeRecovery]        = evadeRecoveryActive;
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

    /// <summary>
    /// Helper for the targetIsIgnored / focusTargetIsIgnored world-state keys.
    /// Returns true when the slot offers nothing fightable: either the slot is
    /// empty (<paramref name="present"/> == false), or the GUID in it is on the
    /// per-bot ignore list (recently blacklisted, 30 s TTL). An empty slot
    /// counts as "ignored" because the planner shouldn't select Combat just
    /// because a slot exists — it should select Combat because the slot points
    /// at something to fight.
    /// </summary>
    private bool IsTargetIgnoredOrAbsent(bool present, int guid)
    {
        return !present || playerReader.IsIgnored(guid);
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

                // Add the GUID to PlayerReader.BlacklistAreaMobs (the IsIgnored
                // map — Core, not the addon) with the standard 30 s TTL.
                // Real-evade call sites (CombatGoal.cs:209, ApproachTargetGoal.cs:265 / 456)
                // call this themselves before/around their SendGoapEvent dispatch —
                // for those paths this is idempotent (IgnoreTarget overwrites the
                // entry's expiry if already present). The test endpoint
                // (PartyController.PostDebugEvade → RaiseDebugEvent → HandleGoapEvent)
                // had no such call, leaving the leader's IsIgnored set empty for
                // the synthetic GUID. Result, log 25:
                //   23:01:48:472  evade window elapsed
                //   23:01:48:473  New Plan = Combat (CombatGoal re-eligible)
                //   23:01:48:535+ leader resumes Heroic Strike on guid=7963945
                //                 because the session-24 IsIgnored short-circuit
                //                 in CombatGoal.Update (line 198) saw IsIgnored=false
                //   23:01:54:894  kill credit on the supposedly-blacklisted mob
                // Centralizing the call here makes every dispatch path symmetric.
                playerReader.IgnoreTarget(evade.TargetGuid);

                // Fix 15: record this guid in the session blacklist so the
                // per-tick refresh in GoapThread keeps its IsIgnored TTL
                // alive while we remain inside any blacklist rect inflated
                // by BlacklistMemoryBufferYards. Idempotent (HashSet.Add).
                // For the assist, this is already added by the API observer
                // before HandleGoapEvent is called (line ~376), so the Add
                // here returns false and is a no-op. For the leader, this
                // is the only path that records the guid.
                _knownBlacklistedGuids.Add(evade.TargetGuid);

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
