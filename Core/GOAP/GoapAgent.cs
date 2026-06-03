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

    // ── Fix BS (run-156 22:45:21→22:47:18 ghost-combat deadlock) ──
    //
    // Ghost combat detection moved from CombatGoal.Update to here. The
    // detector previously lived inside CombatGoal at line ~800-851, gated on
    // CombatGoal being the selected plan. CombatGoal's PartyLeader/AssistFocus
    // preconditions require `allPartyTargetsIsIgnored=false` (line 119/127),
    // which fails in the exact scenario this detector is meant to catch: in
    // combat, both target slots empty/IsIgnored. Result before this fix: the
    // detector couldn't run during the state it was designed to detect, the
    // recovery (`EvadeBlacklistEvent(0, GhostCombat)` → `_evadeRecoveryUntilUtc`
    // → `CanPartyLeaderFollowRoute()=true` → FRG patrols) never dispatched,
    // and the planner sat in NO PLAN.
    //
    // Compounding: even when CombatGoal momentarily fired (brief focus-chain
    // swap), its OnEnter reset (`_ghostCombatActive=false` at CombatGoal.cs
    // line 207) wiped the timer every entry. The bot kept re-entering
    // CombatGoal briefly → timer reset → exit → re-enter → timer never
    // accumulated the 20 s threshold.
    //
    // The detector now runs every planner tick inside NextGoal() (via
    // CheckGhostCombat below), independent of which goal is selected.
    // State is private to GoapAgent and persists across plan transitions.
    //
    // Run-156 evidence (leader clock):
    //   22:45:12:834  CombatTracker Entered Combat.
    //   22:45:17:264  CombatGoal OnEnter (target=2083639). _ghostCombatActive
    //                 reset to false (CombatGoal.cs:207).
    //   22:45:21:418  Kill credit on 2083639 — "Currently fighting: 2" means
    //                 a second mob is still tracked but unfindable to the bot.
    //   22:45:21:418  "We were still targeting a friendly target" — 2083639
    //                 became friendly (dead) before ClearTarget.
    //   22:45:21:471  ClearTarget → Lost target → Search Possible Threats.
    //                 bits.Target_Hostile=false, bits.FocusTarget_Hostile=false
    //                 (assist had been Idle since 22:45:11:578).
    //                 Ghost combat condition was now true. With the detector
    //                 in CombatGoal, this would normally start the timer —
    //                 but CombatGoal exited shortly after (allPartyTargets
    //                 IsIgnored=true blocked re-selection).
    //   22:45:24:535  FRG Resume attempted (CombatGoal not selectable).
    //   22:45:24:596  NO PLAN. Worldstate dump: incombat=True, hastarget=False,
    //                 targetIsIgnored=True, focusTargetIsIgnored=True,
    //                 allPartyTargetsIsIgnored=True.
    //   22:45:26:311  Combat re-selected (focus chain flicker). OnEnter again
    //                 resets _ghostCombatActive=false. Timer cannot accumulate.
    //   22:45:26:383  "Current target guid=0 is ignored — swapping to focus
    //                 chain target guid=2083639" → swap fails → Lost target
    //                 → Search Possible Threats → "Waiting for target to exist
    //                 or lose combat. Possible threats 2!"
    //   22:45:29:700  Same loop.
    //   22:45:33:011  New Plan = Adhoc (Battle Shout cast). CombatGoal exits.
    //   22:45:33:135  NO PLAN. Worldstate dump same as before.
    //   22:45:33:231  LeaderStateService publishing TTL grace ("gap=transient
    //                 (NO PLAN)"). After the 2 s grace expires, the assist
    //                 sees HasApproachStart=False but no recovery fires.
    //   22:45:33 → 22:47:18  *** 1 m 45 s of pure silence. *** Only InRectVerdict
    //                 ticks (~67/s) — Navigation thread alive, GoapAgent
    //                 planner ticking but every tick returns NO PLAN. Bot
    //                 stationary at <-669.76, -1924.79>.
    //   22:47:18:780  User-stop. GoapAgent.Active=false fires the final
    //                 PausePathing.
    //
    // With this fix: the GhostCombat detector runs at every planner tick.
    // After ~20 s of (combat && noHostileTarget && no damage increase),
    // dispatches EvadeBlacklistEvent(0, GhostCombat) → HandleGoapEvent's
    // existing handler at line ~2273 sets `_evadeRecoveryUntilUtc` →
    // CanPartyLeaderFollowRoute() returns true → FRG runs → patrol continues.
    private const double GhostCombatTimeoutSec = 20.0;
    private bool _ghostCombatActive;
    private DateTime _ghostCombatSinceUtc = DateTime.MinValue;
    private int _ghostCombatDamageSnapshot;

    // ── Fix CW (log-120 evidence: 14:42:52→53, 14:43:09, 14:44:56-14:45:17) ──
    //
    // incombatrange (WorldState key, = playerReader.WithInCombatRange()) is read
    // by ATG.AssistFocus as a precondition: ATG.AssistFocus requires
    // `incombatrange=false` (i.e. "only approach if not yet in range"). When
    // the bot's distance to its target hovers around the spell/melee-range
    // threshold, server-tick wobble and small navigation steps cause
    // WithInCombatRange() to toggle every 200-500ms.
    //
    // Each toggle flips the planner's selection:
    //   incombatrange=true  → ATG fails → planner picks FFG (next selectable)
    //   incombatrange=false → ATG passes → ATG selected
    //
    // Result: sub-second ATG↔FFG plan flicker every time the bot is at the
    // edge of combat range. Visible in log-120 as the user's complaints:
    //   #1 turning during approach (entering range → flick → FFG turn-aside)
    //   #2 turning after combat (next approach phase, same mechanism)
    //   #3 running back and forth before last combat (leader stuck, bot at
    //      edge of range for 21+ seconds, 10 flip pairs observed)
    //
    // Evidence: 18 of 18 AM stale-latch warnings (100%) show
    // `incombatrange=True` as the failing precondition. HasApproachStart=true,
    // partyEngaging=true throughout (CV doing its job correctly), only the
    // incombatrange precondition is flickering.
    //
    // Fix: sticky-to-true hysteresis at the WorldState publish site. Once
    // WithInCombatRange() returns true, hold the published value true for
    // IncombatrangeGraceMs even if raw briefly returns false. Raw must stay
    // false past the grace before published can fall.
    //
    // Why sticky-to-TRUE (not -to-false): once the bot has reached combat
    // range, it should commit to either Combat (if party combat starts) or
    // FFG-navigate-to-leader-and-wait. Flipping back to "approach" makes the
    // bot press interact again, pushing it through the range threshold and
    // sustaining the oscillation.
    //
    // Why 1500ms: observed flicker cadence is 200-500ms; window must be
    // longer than that to suppress. 1500ms gives 3× margin while staying
    // short enough that a genuine "moved well out of range" (e.g. retreated
    // 10y after combat) clears within 1.5s.
    //
    // Consumer impact (audited):
    //   • ATG.AssistFocus precondition incombatrange=false: fails sooner
    //     once in range, stops flickering (intended).
    //   • CombatGoal precondition incombatrange=true: passes more readily
    //     near range (slight improvement, faster combat entry).
    //   • ATG effect incombatrange=true: unchanged (state projection only).
    //   • PartyMemberInCombat / PartyLeaderInCombat at GoapAgent.cs:1387
    //     and 1512: UNAFFECTED. They call playerReader.WithInCombatRange()
    //     directly, not via WorldState. partyincombat/partymembercombat
    //     computations are untouched.
    private const double IncombatrangeGraceMs = 1500.0;
    private DateTime _lastIncombatrangeTrueUtc = DateTime.MinValue;
    private bool _incombatrangeGraceLogged;

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

        // ── Fix AV (Route A): leader-side StuckRect propagation ──
        //
        // Subscribe to Navigation's stuck-rect lifecycle events and forward
        // each change into LeaderNavigationProvider, which the HTTP layer
        // serializes into LeaderState.StuckRects on the next poll so the
        // assist can apply them via Navigation.AddPropagatedStuckRect.
        //
        // Gated on PartyLeader mode because:
        //   - Only the leader publishes outbound state (the assist's
        //     LeaderNavigationProvider instance exists but isn't read by
        //     anyone — the HTTP server runs on the leader process).
        //   - The assist's own rect additions (FFG.TickIdleStuckDetection
        //     → TryUnstuck → AddStuckRect) should NOT propagate back; the
        //     channel is strictly leader → assist in the current 2-bot
        //     architecture.
        //
        // Unsubscribed in Dispose, matching the combatLog handler pair.
        if (classConfig.Mode == Mode.PartyLeader)
        {
            navigation.OnStuckRectAdded += OnNavigationStuckRectAdded;
            navigation.OnStuckRectsCleared += OnNavigationStuckRectsCleared;
        }

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

        // Fix AV: mirror the constructor subscription. Mode-gated for the
        // same reason — the assist instance never subscribed.
        if (classConfig.Mode == Mode.PartyLeader)
        {
            navigation.OnStuckRectAdded -= OnNavigationStuckRectAdded;
            navigation.OnStuckRectsCleared -= OnNavigationStuckRectsCleared;
        }
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
            // Fix EW (run-149 11:53:07:529 leader evidence):
            // Wrap the loop body in try/catch. GoapThread runs on a raw
            // Thread (line ~338 `goapThread = new(GoapThread); goapThread.Start();`)
            // with NO ambient exception handler — an unhandled exception inside
            // the loop body silently terminates the thread, leaving the bot
            // in a frozen state with Active still=true (no setter called),
            // manualReset still=set, AddonReader thread still ticking, but
            // UpdateWorldState / NextGoal / goal.Update() no longer firing.
            //
            // Run-149 evidence:
            //   11:53:07:374  last NAV-SANITY (Navigation.Update tick)
            //   11:53:07:529  last P3 DEGENERATE (UpdateWorldState's
            //                  IsTargetLikelyInBlacklistRect call, fires
            //                  every GoapThread iteration when target absent)
            //   11:53:07→18:534  ~11s of GoapThread silence within valid
            //                  log window. The only main-process logs are
            //                  PathFinderThread iterations (separate thread,
            //                  10s/iter due to PPatherService NRE on a
            //                  different mob path; these NREs are caught at
            //                  Navigation.cs:5483 and do NOT propagate).
            // No `[GoapAgent] LogError` of an exception exists because the
            // raw Thread swallows the crash with no diagnostics. Without
            // visibility we cannot identify the throwing call site.
            //
            // The fix is intentionally minimal: catch Exception, log via
            // ILogger so the next occurrence leaves a stack trace in the
            // operator's log, then fall through to the existing
            // Thread.Sleep(1) + manualReset.Wait() at the loop foot so the
            // thread continues iterating. This is BOTH diagnostic (next
            // crash leaves evidence) AND recovery (one bad tick does not
            // brick the bot). Per-iteration state lives in goal fields and
            // in WorldState which UpdateWorldState rewrites every tick;
            // dropping one tick is safe by construction.
            //
            // The catch is OUTSIDE Thread.Sleep + manualReset.Wait so those
            // tail operations always run — preserving the existing thread-
            // coordination invariant with the Active setter (Active=false
            // → manualReset.Reset → next Wait blocks). cts.IsCancellationRequested
            // is checked at the next while condition, so Dispose() still
            // terminates the thread normally.
            try
            {
            // ── Leader side: read assist state from API store ──────────────
            if (classConfig.Mode == Mode.PartyLeader)
            {
                // Fix ES (run-148): age out stale published stuck rects each
                // tick. The leader broadcasts its dynamic stuck rects to the
                // assist via leaderNavProvider; ClearStuckRects only fires on
                // bail/evade/geometry-trap paths, so during normal grinding a
                // local rect (DateTime.MaxValue, never auto-pruned in
                // Navigation) would otherwise be published for the rest of the
                // session and block the assist's path back to the leader. This
                // self-contained age cap drops it from the broadcast after
                // PublishedStuckRectTtlSec regardless of plan transitions.
                leaderNavProvider.PruneExpiredStuckRects();

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
                            // E4: mirror the leader's in-rect verdict so the assist's
                            // self-defense is suppressed for the same mob (IsNoEngage),
                            // closing the partner-recruit hole. NoEngageMobGuids is a
                            // subset of BlacklistedMobGuids, published in the same snapshot.
                            bool inRect = leaderState.NoEngageMobGuids is { Length: > 0 } noEngage
                                && System.Array.IndexOf(noEngage, guid) >= 0;

                            logger.LogInformation(
                                $"[GoapAgent] New blacklisted mob guid={guid} from API " +
                                $"(inRect={inRect}) — ignoring target and dispatching EvadeBlacklistEvent.");

                            playerReader.IgnoreTarget(guid, inRect);
                            HandleGoapEvent(new EvadeBlacklistEvent(guid, EvadeReason.Propagation, inRect));

                            // ── Category-B cleanup (post-run-154 audit) ──
                            //
                            // The former Fix FD `assistStatusProvider.CantFollow = true`
                            // assignment was removed here. The flag's downstream effect —
                            // keeping FFG selectable across propagation events — is now
                            // carried correctly by `assistshouldfollow`'s existing
                            // branches: branch 3 (steady state) when not in combat, and
                            // branch 4 (`targetIgnored && focusTargetIgnored`) when the
                            // propagation handler has cleared the assist's target and the
                            // leader's focus target is the same propagated mob.
                            //
                            // For scenarios where the assist is in combat with an unrelated
                            // mob when propagation arrives, CombatGoal continues to be
                            // selectable via partymembercombat / focus-chain target
                            // acquisition; FFG selectability is not the right goal anyway.
                            //
                            // CantFollow now fires only for its semantically correct
                            // cases: physical navigation exhaustion (FollowFocusGoal
                            // .cs:6621 EnterCantFollow) and the Fix 23 self-defense
                            // beacon (CombatGoal.cs:595, intended as "leader come help").
                            //
                            // The AssistRequestReturn() call below remains unconditional
                            // — that's the actual fix for the run-145 corpse-handling
                            // deadlock, by clearing State.ShouldConsumeCorpse so FFG's
                            // consumecorpse=false precondition holds. The former
                            // CantFollow=true was incidental to that fix.

                            // Fix (run-145 11:27:26:147 NO PLAN on assist): the
                            // comment above at lines 536-538 claims that setting
                            // CantFollow=true alone "keeps FFG selectable after
                            // CombatGoal exits." That is only true when
                            // consumecorpse=false. If a kill just happened and
                            // the corpse-handling chain (ConsumeCorpse → Loot →
                            // Skinning → Corpse Consumed) did NOT complete
                            // cleanly — e.g., LootGoal failed to open the loot
                            // window (run-145: "Loot Failed open: -3016ms" at
                            // 11:27:24:327) — then State.ShouldConsumeCorpse is
                            // still true when the blacklist propagation arrives.
                            // FFG is blocked by AddPrecondition(consumecorpse,
                            // false) at FollowFocusGoal.cs:1302; ConsumeCorpseGoal
                            // (library, not in our source) is gated on
                            // !assistrequestreturn in the same scenarios per the
                            // observed "OnGoapEvent - AssistRequestReturn" log
                            // line on the leader. With both blocked the planner
                            // returns NO PLAN, the assist freezes, the leader
                            // sync-pauses for it (run-145 11:27:45:051), and
                            // both bots deadlock.
                            //
                            // The leader-side analog at line 426 (the
                            // assistStateStore diff loop, when the LEADER sees
                            // an assist transition to CantFollow via API) calls
                            // AssistRequestReturn() — which clears exactly the
                            // stale corpse-handling state that traps us here
                            // (State.LastCombatKillCount, State.ShouldConsumeCorpse,
                            // LootableCorpseCount, GatherableCorpseCount,
                            // ConsumableCorpseCount; method body at line 2051).
                            // The assist-side path was missing the matching
                            // cleanup. Calling it here restores symmetry:
                            // consumecorpse flips to false on the next tick,
                            // FFG becomes selectable, the assist returns to
                            // the leader instead of standing around.
                            AssistRequestReturn();
                        }
                    }
                }

                // ── Fix AV (Route A): apply propagated stuck rects ──
                //
                // The leader publishes its current set of dynamic stuck
                // rects in LeaderState.StuckRects each poll cycle. We
                // re-apply the full list every GoapThread iteration so
                // the assist's Navigation tracks the leader's authoritative
                // set with at most one poll-interval of latency.
                //
                // Navigation.AddPropagatedStuckRect deduplicates by
                // spatial overlap and refreshes the per-rect TTL on
                // re-application — so calling every iteration is
                // safe-and-cheap (no-op for known rects, TTL bump for
                // continuing rects, new addition for newly-published
                // rects). When the leader's set goes empty (ATG/PTG/
                // CombatGoal calls ClearStuckRects on plan transition,
                // OnStuckRectsCleared fires, LeaderNavigationProvider's
                // snapshot drains), this loop body is skipped on the
                // Length==0 guard and the assist's already-applied
                // rects age out via Navigation.PruneExpiredStuckRects
                // (TTL = PropagatedStuckRectTtlSec, default 90 s).
                if (leaderState != null && leaderState.StuckRects is { Length: > 0 })
                {
                    foreach (StuckRectInfo r in leaderState.StuckRects)
                    {
                        navigation.AddPropagatedStuckRect(
                            new Vector3(r.CenterX, r.CenterY, 0f),
                            r.HalfSize);
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
                    HandleGoapEvent(new EvadeBlacklistEvent(leaderTargetGuid, EvadeReason.Propagation));
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
                    HandleGoapEvent(new EvadeBlacklistEvent(leaderFocusTargetGuid, EvadeReason.Propagation));
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
                    playerReader.IgnoreTarget(guid, playerReader.IsNoEngage(guid));
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
            }
            catch (Exception goapThreadEx)
            {
                // Fix EW (run-149) — see the wrap-open comment at the top
                // of this while loop for the full evidence trail and design
                // rationale. Log AT ERROR (visible at default Information
                // threshold) with the full exception (stack trace included
                // by ILogger.LogError) so the next silent-death repro leaves
                // diagnostics. DO NOT re-throw; falling through keeps the
                // thread alive for the next iteration. We deliberately do
                // not attempt per-iteration state cleanup (e.g. forcing
                // CurrentGoal.OnExit) because we cannot tell from a generic
                // catch where the exception originated — UpdateWorldState,
                // NextGoal, OnEnter, OnExit, or Update — and running OnExit
                // on a goal whose OnEnter never completed could itself
                // throw. The next iteration's NextGoal re-reads WorldState
                // from scratch and re-selects a goal; if the same exception
                // recurs every tick, the operator will see a flood of
                // error logs with stack traces that pinpoint the bug.
                logger.LogError(goapThreadEx,
                    "[GoapAgent] [FIX-FIRE] EW: GoapThread loop iteration " +
                    "exception caught — thread CONTINUES (raw-Thread silent " +
                    "death prevented). Mode={Mode} CurrentGoal={Goal}.",
                    classConfig.Mode,
                    CurrentGoal?.Name ?? "(null)");
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
        // Fix CW: diagnostic shows the smoothed value (matches UpdateWorldState).
        // Fix CX: only AssistFocus has the hysteresis; PartyLeader shows raw.
        logger.LogInformation("GoapKey.incombatrange: " +
            (playerReader.WithInCombatRange() ||
             (classConfig.Mode == Mode.AssistFocus &&
              _lastIncombatrangeTrueUtc != DateTime.MinValue &&
              (DateTime.UtcNow - _lastIncombatrangeTrueUtc).TotalMilliseconds < IncombatrangeGraceMs)));
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
        // Fix CM: match the corrected partyEngaging computation in UpdateWorldState
        // (see ~line 1302). Reads polled leader state on assist, not local nav provider.
        logger.LogInformation("GoapKey.partyEngaging: " +
            (PartyInCombat() ||
             (classConfig.Mode == Mode.AssistFocus &&
              leaderConnection.LastLeaderState?.HasApproachStart == true)));
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
        CheckGhostCombat();

        if (Plan.Count == 0)
            Plan = GoapPlanner.Plan(AvailableGoals, WorldState, GoapPlanner.EmptyGoalState);

        return Plan.Count > 0 ? Plan.Pop() : null;
    }

    // ── Fix BS (run-156 22:45 ghost-combat deadlock) ──
    //
    // Runs every planner tick from NextGoal() above. Mirrors the algorithm
    // that used to live in CombatGoal.cs:800-851 with one critical difference:
    // it doesn't depend on CombatGoal being the selected plan, so it works
    // in the deadlock case where allPartyTargetsIsIgnored=true blocks
    // CombatGoal selection.
    //
    // Algorithm (unchanged from the prior CombatGoal version):
    //   1. Scope: only PartyLeader / AssistFocus modes (mirrors the
    //      `classConfig.Mode == ... || ...` gate at the prior CombatGoal.cs
    //      line 800).
    //   2. Trigger condition: bits.Combat()=true AND no hostile target in
    //      either slot. The "no hostile" check matches the prior code: the
    //      bot's own target is non-hostile AND the focus's target is
    //      non-hostile. Either being non-hostile alone isn't enough — only
    //      when *both* slots are unfightable does the bot have nothing to
    //      engage.
    //   3. Damage snapshot: capture combined damage count at timer start.
    //      If counts later increase, reset — that means combat is active
    //      (mobs hitting us or pet damaging), not ghost.
    //   4. Threshold: GhostCombatTimeoutSec (20 s, same as before).
    //   5. On expiry: dispatch EvadeBlacklistEvent(0, GhostCombat). The
    //      existing HandleGoapEvent handler (this file, ~line 2273) sets
    //      _evadeRecoveryUntilUtc → CanPartyLeaderFollowRoute() returns
    //      true → FRG selectable → patrol resumes.
    //   6. Reset condition (besides expiry): hostile target re-acquired in
    //      either slot.
    //
    // The OnEnter reset that used to live in CombatGoal (line 207) is NOT
    // mirrored here — that reset was specific to the CombatGoal-scoped
    // detector (a fresh combat scenario warranted a fresh timer). With the
    // detector at GoapAgent scope, the timer correctly persists across plan
    // transitions; CombatGoal exits and re-enters no longer wipe progress.
    private void CheckGhostCombat()
    {
        if (classConfig.Mode != Mode.PartyLeader
            && classConfig.Mode != Mode.AssistFocus)
            return;

        if (!bits.Combat())
        {
            if (_ghostCombatActive)
            {
                logger.LogInformation("[GoapAgent] Ghost combat timer reset — combat ended.");
                _ghostCombatActive = false;
                _ghostCombatSinceUtc = DateTime.MinValue;
                _ghostCombatDamageSnapshot = 0;
            }
            return;
        }

        bool noHostileTarget = !bits.Target_Hostile() && !bits.FocusTarget_Hostile();
        int currentCombinedDamage = combatLog.DamageDoneCount() + combatLog.DamageTakenCount();

        if (noHostileTarget)
        {
            if (!_ghostCombatActive)
            {
                _ghostCombatActive = true;
                _ghostCombatSinceUtc = DateTime.UtcNow;
                _ghostCombatDamageSnapshot = currentCombinedDamage;
                logger.LogInformation(
                    $"[GoapAgent] Ghost combat timer started. DamageSnapshot={_ghostCombatDamageSnapshot}.");
            }
            else if (currentCombinedDamage > _ghostCombatDamageSnapshot)
            {
                logger.LogInformation(
                    $"[GoapAgent] Ghost combat timer reset — damage increased " +
                    $"({_ghostCombatDamageSnapshot} -> {currentCombinedDamage}).");
                _ghostCombatActive = false;
                _ghostCombatSinceUtc = DateTime.MinValue;
                _ghostCombatDamageSnapshot = 0;
            }
            else
            {
                double ghostSec = (DateTime.UtcNow - _ghostCombatSinceUtc).TotalSeconds;
                logger.LogDebug(
                    $"[GoapAgent] Ghost combat: {ghostSec:0.0}s / {GhostCombatTimeoutSec}s.");
                if (ghostSec >= GhostCombatTimeoutSec)
                {
                    logger.LogWarning(
                        $"[GoapAgent] Ghost combat detected — escaping via route.");
                    _ghostCombatActive = false;
                    _ghostCombatSinceUtc = DateTime.MinValue;
                    _ghostCombatDamageSnapshot = 0;
                    // Call HandleGoapEvent directly — only HandleGoapEvent branches
                    // on EvadeBlacklistEvent (no IGoapEventListener.OnGoapEvent does),
                    // so this is observationally identical to the SendGoapEvent path
                    // CombatGoal used. See the comment block at RaiseDebugEvent
                    // (~line 2058) for the full rationale.
                    HandleGoapEvent(new EvadeBlacklistEvent(0, EvadeReason.GhostCombat));
                }
            }
        }
        else
        {
            if (_ghostCombatActive)
            {
                logger.LogInformation(
                    "[GoapAgent] Ghost combat timer reset — hostile target acquired.");
                _ghostCombatActive = false;
                _ghostCombatSinceUtc = DateTime.MinValue;
                _ghostCombatDamageSnapshot = 0;
            }
        }
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

        // ── Fix CW — sticky-to-true hysteresis on incombatrange ──
        // See the IncombatrangeGraceMs constant block (~line 67) for full
        // rationale and evidence. Summary: once WithInCombatRange() returns
        // true, hold the published value true for IncombatrangeGraceMs even
        // if raw briefly returns false. Suppresses ATG↔FFG plan flicker at
        // the combat-range boundary.
        //
        // ── Fix CX (log-121 evidence: 15:20:24-26, 15:23:02-05) — mode gate ──
        // CW was originally unconditional. Log-121 revealed a regression on
        // the leader: 2 of 7 leader ATG→FRG transitions ran for 1992ms and
        // 3111ms respectively (user-visible "leader started approaching then
        // ran away"). Both correlated with CW fires within +31-46ms of the
        // FRG start, sinceLast=30-31ms. The 5 other ATG→FRG transitions in
        // the same log ran for only 47-155ms (sub-perception planner micro-
        // flickers with no CW correlation).
        //
        // Root cause: the leader's planner is *designed* to handle rapid
        // ATG↔PTG handoffs at the combat-range boundary via cost ordering
        // (PTG cost 7 < ATG cost 8). When incombatrange flicks true briefly
        // during this handoff, CW's 1500ms grace held it true, failing ATG's
        // `incombatrange=false` precondition for the entire grace window.
        // PTG also can't run if withinpullrange falls. The leader cascades
        // to FRG (patrol) — i.e., walks AWAY from the mob — for the full
        // grace window.
        //
        // The assist has no PTG path (its ATG → Combat transition depends on
        // partymembercombat which fires only when someone enters combat), so
        // the smoothing CW provides is what closes the gap. Smoothing the
        // leader's signal breaks the leader's design.
        //
        // CX: only apply hysteresis in AssistFocus mode. PartyLeader (and any
        // other mode) uses the raw WithInCombatRange() value directly.
        bool rawIncombatrange = playerReader.WithInCombatRange();
        bool publishedIncombatrange;
        if (classConfig.Mode == Mode.AssistFocus)
        {
            if (rawIncombatrange)
            {
                _lastIncombatrangeTrueUtc = DateTime.UtcNow;
            }
            bool withinIncombatrangeGrace =
                _lastIncombatrangeTrueUtc != DateTime.MinValue &&
                (DateTime.UtcNow - _lastIncombatrangeTrueUtc).TotalMilliseconds < IncombatrangeGraceMs;
            publishedIncombatrange = rawIncombatrange || withinIncombatrangeGrace;
        }
        else
        {
            // Fix CX: leader (and other modes) use raw — no hysteresis.
            publishedIncombatrange = rawIncombatrange;
        }

        // ── Fix EE-range (log 133aa/133ab evidence) ─────────────────────────
        // While an ApproachEscape is active, publish incombatrange=false so
        // ATG — the escape's designated owner — stays selectable end-to-end.
        //
        // Root cause (133aa 03:07:40 → 03:07:46; 133ab 03:09:47): after Fix ED
        // unified the Navigation instance, a close-range escape (EA dist=2.0,
        // maxR=5 → within combat range) leaves the planner with no escape
        // owner. PTG is correctly blocked (approachEscapeActive=true) and ATG
        // is blocked by its incombatrange=false precondition (ApproachTargetGoal
        // line 238), so the plan falls to FollowRouteGoal. FRG's "Target
        // acquired → stopping navigation" calls navigation.Stop() (active=false)
        // on the now-shared instance, orphaning the escape; TryUnstuck's
        // `_approachEscapeActive && !active` guard (Navigation.cs ~2680) then
        // reads it as "stopped externally" and runs a destructive
        // ResetApproachEscape that wipes the target guid → the bot gives up and
        // approaches a DIFFERENT mob (133ab guid 1225268→1195230). 133z avoided
        // this only because its escape pushed the bot past maxR (range 15y) so
        // incombatrange went false naturally and ATG kept ownership the whole
        // time. Keeping ATG eligible during the escape matches the documented
        // "ATG owns the escape" intent and removes the Follow excursion that
        // orphans it.
        //
        // Leader-safe (audited): the only readers of the incombatrange worldkey
        // are ATG (=false, here un-blocked — intended) and the SOLO-Grind
        // CombatGoal branch (=true, CombatGoal line 136). The PartyLeader
        // CombatGoal branch gates on partyleadercombat, and
        // PartyLeaderInCombat()/combat detection call WithInCombatRange()
        // DIRECTLY (not this worldkey), so combat entry is unaffected. PTG does
        // not read incombatrange. The mask is bounded strictly to the active-
        // escape window and clears the instant the escape ends (whereupon the
        // raw range value resumes governing the normal ATG→Combat handoff).
        if (navigation.IsApproachEscapeActive)
        {
            publishedIncombatrange = false;
        }

        WorldState[GoapKey.incombatrange] = publishedIncombatrange;

        // Fix CW: one-shot log per grace activation. Fires when the hysteresis
        // is actually doing work (raw=false, but published=true via grace).
        // The flag resets when raw=true returns, so each new grace activation
        // produces one log line. Only fires in AssistFocus mode (Fix CX).
        if (classConfig.Mode == Mode.AssistFocus &&
            publishedIncombatrange && !rawIncombatrange)
        {
            if (!_incombatrangeGraceLogged)
            {
                double msSinceTrue =
                    (DateTime.UtcNow - _lastIncombatrangeTrueUtc).TotalMilliseconds;
                logger.LogInformation(
                    "[GoapAgent] [FIX-FIRE] CW: publishing incombatrange=TRUE via " +
                    "hysteresis ({0:0}ms since last raw=true; grace={1:0}ms). Holds " +
                    "ATG.AssistFocus precondition unselected to prevent flicker at " +
                    "the combat-range boundary; FFG continues steady navigation.",
                    msSinceTrue, IncombatrangeGraceMs);
                _incombatrangeGraceLogged = true;
            }
        }
        else if (rawIncombatrange)
        {
            _incombatrangeGraceLogged = false;
        }
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
        // E4: keep the planner mirror in lockstep with CombatGoal:462 — don't flip
        // targetIgnored=false (i.e. don't make Combat selectable for self-defense)
        // against a mob determined to be inside a blacklist rect at blacklist time.
        // Per-GUID verdict (PlayerReader.IsNoEngage) riding the IsIgnored TTL — NOT a
        // per-tick position read, so it needs no facing and can't flap at the edge.
        // Position rule (operator-directed; supersedes per-GUID IsNoEngage gating):
        // self-defense is allowed when we are OUTSIDE every static rect, OR inside one
        // but DECLARED STUCK (escape physically wedged / exhausted) — the survival path
        // (RESTORATION_LIST_AB2.md §F, never previously implemented). Inside the rect
        // and NOT stuck → suppressed, so Combat stays blocked and FollowRoute/FFG
        // retreat (escape-first). The override's own dmgTaken + TargetTarget==Me
        // conjuncts already supply §F's "actively taking damage" discriminator, so no
        // separate damage check is needed. The run-144 "mob left the rect and attacked"
        // case is handled by the OUTSIDE-rect branch (!botInsideBlacklistArea), not the
        // stuck branch.
        bool declaredStuck = navigation.IsApproachEscapePhysicallyStuck
                          || navigation.IsApproachEscapeExhausted;
        // Section D (caster retreat) — engage decision half: outside the rect, only
        // engage an attacker we can actually REACH. A target reading as inside the rect
        // is an unreachable in-rect caster; engaging it bounces us at the rect edge
        // taking damage, so suppress engage and let retreat take over.
        //
        // CAVEAT (run-146 2026-05-27): IsTargetLikelyInBlacklistRect() inflates the rect
        // by ~6y (DetourMargin/2) so mobs hugging the INSIDE edge read in-rect — correct
        // for the E5/PTG approach gate (over-conservative is safer there). But the same
        // inflation also catches mobs hugging the OUTSIDE edge by ≤6y — which is exactly
        // the "mob walked OUT to melee us" case the position rule was supposed to enable
        // self-defense for. Earlier comment here ("adjacent melee that walked out reads
        // NOT-in-rect → still fought") was wrong. Run-146 13:16:45 assist evidence:
        // bracket=[0,5] target 2.5y away, est=<476.74,-4264.75>, real rect MaxX=473 so
        // est is 3.74y OUTSIDE the rect — but staticHit=True with inflate=6.0 because
        // inflated MaxX=479. Result: engageAllowed=false, self-defense override never
        // fired ("IsIgnored self-defense override" grep returned 0 hits across the run),
        // assist froze for 27s at <477.0359,-4266.729> with the mob in melee hitting it.
        //
        // Discriminator: a target in MELEE range (MaxRange in [1,5], the addon's short-
        // range bracket — matches the bracket=[0,5] in the run-146 log) is by definition
        // reachable without entering the rect. At range (>5y) the rect verdict still
        // applies — the original caster-retreat scenario Section D was built for.
        // declaredStuck (survival) still overrides everything.
        int meleeProbeMaxRange = playerReader.MaxRange();
        bool targetInMelee = meleeProbeMaxRange > 0 && meleeProbeMaxRange <= 5;
        bool targetInRect = !targetInMelee && navigation.IsTargetLikelyInBlacklistRect();
        bool engageAllowed = declaredStuck
                          || (!botInsideBlacklistArea && !targetInRect);
        // ── Fix EZ (run-151 evidence) — engageAllowedForJoin ──
        //
        // `engageAllowed` above conflates two distinct gates:
        //   (a) "is this bot geographically safe to engage" (botInsideBlacklistArea,
        //       declaredStuck)
        //   (b) "is the bot's OWN current target reachable"  (targetInRect via
        //       playerReader.MaxRange / TargetMapPos)
        // For SELF-DEFENSE (Fix 17/26 at line ~1432) the bot already has a target
        // and (b) is meaningful: we only engage an IsIgnored attacker if the attacker
        // is itself reachable. For Fix L (party-assist override at line ~1509) the
        // bot has NOT yet acquired a target — the CombatGoal Case 2 swap will do that
        // post-flip via PressTargetFocus + PressTargetOfTarget. With no own target,
        // (b)'s IsTargetLikelyInBlacklistRect returns degenerate-true via the P3
        // branch (Navigation.cs:5905, "maxRange=0, no estimate") and the conflated
        // engageAllowed evaluates false — blocking Fix L incorrectly.
        //
        // Run-151 evidence: leader 03:12:18:437 NO PLAN dump (~3s after the shared
        // kill on 1666423, while the assist was fighting 1666385 alone outside the
        // rect):
        //     hastarget: False              (CombatGoal cleared after kill at
        //                                    03:12:15:294 via PressInsert)
        //     focushastarget: True          ← bits CAN see assist has a target
        //     focuscombat: True             ← bits CAN see assist is in combat
        //     focusTargetIsIgnored: True    (= 1666385, blacklisted at 03:11:53:698)
        //     inblacklistarea: False        ← leader is geographically clear
        //     allPartyTargetsIsIgnored: True   ← BLOCKER
        // Every P3 line in the surrounding window confirms maxRange=0 → degenerate
        // → targetInRect=true → engageAllowed=false. Fix L's `engageAllowed` conjunct
        // failed. Result: leader sat NO PLAN from 03:12:18:437 to 03:12:34:909
        // (16.5s) while the assist fought 1666385 alone. Operator: "the leader did
        // not help the assist kill the mob that was attacking it."
        //
        // The Position rule comment at line ~1502-1508 already states the intent:
        // "a bot may JOIN the partner's fight only when it is itself allowed to
        // engage — OUTSIDE every static rect, OR inside one but DECLARED STUCK".
        // That is (a) only. engageAllowedForJoin encodes exactly this — declaredStuck
        // OR !botInsideBlacklistArea, no target-position component.
        //
        // Safety considerations:
        //   - If the leader is inside the rect and not stuck, engageAllowedForJoin
        //     is false (same as old engageAllowed in that case) → Fix L still
        //     blocked, retreat continues. The fix only changes behavior for the
        //     specific case "leader is geographically OUT of the rect with no own
        //     target".
        //   - The per-tick re-evaluation of Fix L (the override flip happens every
        //     tick conditions hold, not latched permanently) provides the natural
        //     safety: if the leader chases the focus's target back INTO the rect
        //     during combat, botInsideBlacklistArea flips true on the next tick,
        //     engageAllowedForJoin flips false, Fix L stops flipping, the planner
        //     reverts allPartyTargetsIsIgnored to true, Combat plan exits, retreat
        //     resumes. The escape-first semantics are preserved.
        //   - The focus's target reachability is decided downstream: CombatGoal's
        //     Case 2 swap acquires the partner's target into the bot's own target
        //     slot, and the next-tick self-defense override (line ~1432) evaluates
        //     the regular engageAllowed (with targetInRect now meaningful, since
        //     the bot HAS a target). If the acquired target is in the rect, the
        //     existing self-defense gate suppresses; if not, the leader engages.
        //   - Mode-irrelevant: behaves identically across PartyLeader / AssistFocus
        //     / Grind. Standalone Grind doesn't reach Fix L (gated by isPartyModeForFixL
        //     at line ~1500), so this variable is unused in Grind regardless.
        bool engageAllowedForJoin = declaredStuck || !botInsideBlacklistArea;
        bool selfDefenseOverride =
            targetIgnored &&
            hasTarget && playerCombat && dmgTaken &&
            playerReader.TargetTarget is UnitsTarget.Me or UnitsTarget.Pet &&
            !evadeRecoveryActive &&
            engageAllowed;
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
        // Position rule (operator-directed; lockstep with the self-defense gate and
        // CombatGoal): a bot may JOIN the partner's fight only when it is itself
        // allowed to engage — OUTSIDE every static rect, OR inside one but DECLARED
        // STUCK (engageAllowed, defined above). Inside the rect and not stuck, this bot
        // must retreat (FollowRoute/FFG), not join. This replaces the run-144 per-GUID
        // !IsNoEngage gate: under the position rule the partner only genuinely fights
        // when outside-or-stuck, and we only join under the same condition.
        if (isPartyModeForFixL &&
            focusTargetIgnored &&
            b.FocusTarget() &&
            b.FocusTarget_Combat() &&
            playerReader.FocusTargetGuid != 0 &&
            engageAllowedForJoin &&
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
        // ── Fix CM (log-114 evidence — CL diagnostic, 23:15:57:212 confirmation) ──
        //
        // The original Fix AH formulation reads `leaderNavProvider.HasApproachStart`
        // here, intending it to mean "the leader has published an approach anchor."
        // That is correct ON THE LEADER'S PROCESS — the leader's own ATG calls
        // leaderNavProvider.SetApproachStart() (ApproachTargetGoal.cs:223) when it
        // enters and ClearApproachStart() when it exits, so the leader's local
        // LeaderNavigationProvider._hasApproachStart accurately reflects the
        // leader's ATG state.
        //
        // BUT on the ASSIST, leaderNavProvider is a *separate instance* in a
        // *separate process*. The only thing that ever sets the assist's local
        // leaderNavProvider._hasApproachStart is the assist's *own* ATG entering
        // — which is itself gated by partyEngaging=true (this very key). So the
        // ORIGINAL READ HERE WAS SELF-REFERENTIAL on the assist: "ATG can fire
        // if the assist is currently in ATG." Because the assist never is in
        // ATG to start with, the disjunct collapsed to false, and partyEngaging
        // on the assist was effectively just PartyInCombat() — Fix AH's intended
        // pre-combat ATG window was never actually unblocked.
        //
        // Evidence — log-114 AM warning at 23:15:57:212, leader was in ATG for
        // target 962223 (HasApproachStart published true via the API):
        //   approachLeader.HasApproachStart = True    (polled, correct)
        //   bits.Combat = False, bits.Focus_Combat = False
        //   leaderNavProvider.HasApproachStart = False (local — never set on assist
        //                                                during this entire window)
        // The Fix CL diagnostic correctly computed partyEngaging=True using the
        // polled HasApproachStart, but the planner — reading WorldState[partyEngaging]
        // populated from leaderNavProvider — saw FALSE. ATG.AssistFocus precondition
        // partyEngaging=true failed, the planner picked FFG (cost 19) instead of ATG
        // (cost 8), and the assist sat in FFG for the entire leader-ATG window. When
        // the leader finally entered Combat at 23:15:59:747, the assist's
        // bits.Focus_Combat became true, PartyInCombat() became true, partyEngaging
        // became true via the FIRST disjunct — but by then CombatGoal (cost 4 <
        // ATG cost 8) preempted ATG. Result: ATG.OnEnter on assist fired 1× across
        // 23 combats in log-114. User-visible: "assist not approaching a mob until
        // after the leader already started attacking it."
        //
        // Fix: read HasApproachStart from the assist's polled leader state
        // (leaderConnection.LastLeaderState.HasApproachStart), which actually
        // reflects the leader's ATG state via the API/poller. On the leader the
        // mode-gate evaluates false (leader's own Mode != AssistFocus) so the
        // disjunct is unreachable and behavior is unchanged.
        //
        // Null-safety: leaderConnection.LastLeaderState is nullable (returns null
        // before the first successful poll). The `?.HasApproachStart == true`
        // pattern returns false on null — the same false value the disjunct
        // resolved to before the leader's first poll, so no new edge case.
        //
        // Mode gate (AssistFocus only): For PartyLeader and Grind modes,
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
            (classConfig.Mode == Mode.AssistFocus &&
             leaderConnection.LastLeaderState?.HasApproachStart == true);

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
                     && playerReader.WithInCombatRange())
                // ── Fix AP (log-79 03:55:01:459 → 03:55:16:448, first-mob window) ──
                //
                // Asymmetric counterpart to PartyLeaderInCombat()'s
                //   `(b.FocusTarget() && bits.FocusTarget_Combat())`
                // disjunct (line ~1402 below). The leader-side accepts that
                // looser predicate because the leader is meant to be the
                // proactive combatant — "leader observes any in-combat mob"
                // is a valid signal for the leader to engage. The
                // member-side (assist in AssistFocus mode) needs a tighter
                // gate: the assist should only join when the LEADER HERSELF
                // is engaged, not merely observing a fight someone else is
                // in. The third conjunct `bits.Focus_Combat()` provides that
                // gate.
                //
                // Evidence: in log-79's first mob, the leader entered combat
                // at 03:55:10:502 (BotStatus.Combat broadcast received by the
                // assist at 03:55:11:149). The assist:
                //   - playerCombat = false (no aggro on the assist)
                //   - dmgTaken = false (no damage taken during the 6s combat)
                //   - hasTarget = false (Fix AH/AJ/AN never fired — assist
                //     never reached the anchor: 11.6y at path-enqueue, ended
                //     20.5y away by anchor-clear due to pather wrap-around)
                // → both existing disjuncts evaluate false → partymembercombat
                // = false → CombatGoal's AssistFocus precondition
                // (CombatGoal.cs ctor) fails → CombatGoal not selected →
                // assist stays in FollowFocusGoal doing PositionChase →
                // wedged against terrain at <-735.65, -4281.47> for the
                // entire combat duration. RouteEscape fired at 03:55:16:690,
                // 242ms after the leader had already killed the mob at
                // 03:55:16:448. User observation: "assist runs into wall for
                // the entire combat duration."
                //
                // Why the existing predicate was insufficient: the original
                // semantics were "this bot is locally engaged" — only the
                // bot's own combat / damage / target signals counted.
                // PartyLeaderInCombat already had a focus-engagement branch
                // (matched here below) so the LEADER recognised "the assist
                // is engaging via focus chain" and could fire CombatGoal.
                // The assist had no such symmetric path: it could only enter
                // Combat by either (a) being attacked itself, or (b) having
                // its own target acquired by an external mechanism (Fix AH/
                // AJ/AN's focus chain). When (a) didn't happen and (b)
                // couldn't fire (anchor windows too short, geometry didn't
                // permit convergence), the assist was stuck.
                //
                // Why this is the right place to fix (vs an FFG-side focus
                // chain): CombatGoal already has Case 2 (CombatGoal.cs
                // line ~410) which performs PressTargetFocus +
                // PressTargetOfTarget as its first action when
                // currentTargetIsIgnored=true. The acquisition logic is
                // already present; what was missing was simply the
                // precondition that allows CombatGoal to be SELECTED in
                // the first place. Adding this branch lets the planner
                // pick CombatGoal once the leader is actually engaging a
                // fightable mob, and CombatGoal's own Case 2 swaps the
                // target. No new code path duplicates an existing one.
                //
                // Three-conjunct gate (Focus_Combat AND FocusTarget AND
                // FocusTarget_Combat) — each conjunct rejects a specific
                // failure case:
                //
                //   - Focus_Combat (leader herself is in combat): rejects
                //     scenarios where the leader is OOC but happens to
                //     have a target that's in combat with someone else.
                //     Examples:
                //       * Leader auto-targets (Tab / soft-interact) a
                //         passing mob already engaged with another player
                //         — without this conjunct the assist would enter
                //         CombatGoal, Case 2 swaps to leader's target,
                //         and assist starts attacking a mob another player
                //         is tagging (kill-steal).
                //       * Leader's stale post-kill target still has the
                //         combat flag for a tick or two after death (rare
                //         but observable). Focus_Combat drops within the
                //         tick the leader exits combat, so this conjunct
                //         filters out the residual flag.
                //       * Pet-class scenario: leader's pet engages a mob
                //         while the leader is OOC. Leader's target =
                //         pet's target = in-combat-with-pet. Without this
                //         conjunct the assist would join, which is
                //         design-dependent (some configurations may want
                //         it, but the safe default is "no").
                //
                //   - FocusTarget (leader actually has a target): trivial
                //     guard against FocusTarget_Combat reading garbage
                //     when no target is set.
                //
                //   - FocusTarget_Combat (the target is itself in combat):
                //     the substantive signal — the mob is actually engaged,
                //     not just selected. Filters out the PTG window where
                //     the leader has acquired but hasn't yet landed the
                //     pull cast (target selected, target not in combat
                //     yet). Once the pull lands, this becomes true and
                //     CombatGoal can fire.
                //
                // Conjunct ordering chosen for short-circuit efficiency:
                // Focus_Combat first because it's the most likely to be
                // false in the common case (leader patrolling/looting/
                // etc.), then FocusTarget, then FocusTarget_Combat.
                //
                // Why no IsIgnored check here: PartyMemberInCombat is
                // a generic engagement signal; the IsIgnored / blacklist
                // semantics are gated by GoapKey.allPartyTargetsIsIgnored
                // (the CombatGoal precondition just below
                // partymembercombat in the AssistFocus ctor). If the
                // leader's target is on IsIgnored, allPartyTargetsIsIgnored
                // resolves to true and CombatGoal is still blocked at
                // the planner level — even if this branch returns true.
                // The two preconditions compose correctly without
                // duplicating the IsIgnored check here.
                || (bits.Focus_Combat() && bits.FocusTarget() && bits.FocusTarget_Combat())
                // ── Fix EX (run-150 evidence) ──
                //
                // Leader's first combat 02:13:48:836 → 02:14:14:302 (leader
                // clock = 02:13:16:787 → 02:13:42:253 assist clock; 32.049s
                // clock skew confirmed by aligning leader's CombatTracker
                // Entered Combat with assist's first 'Leader status: Combat'
                // poll). Window length ~25.5s. Operator: "the assist does
                // not help the leader with the mob at all."
                //
                // Trace: throughout the entire 25.5s window the assist stayed
                // in FollowFocusGoal PositionChase mode (FFG CJ logs firing
                // every ~500ms with HasApproachStart=True, dist ~6.0-7.0y).
                // GoapAgent never selected Combat / ATG / PTG. No AH/AJ/AN/AP
                // focus-chain FIX-FIRE log fired in the entire run. The
                // assist's LeaderStatePoller saw 'Leader status: Combat' at
                // 02:13:16:787 (within 0.05s of the leader's CombatTracker
                // Entered Combat at 02:13:48:836 leader clock) — so polled
                // LastLeaderState.InCombat was reliably true the whole time.
                //
                // The three existing disjuncts above each evaluated false:
                //   - playerCombat=false, dmgTaken=false (assist was not
                //     attacked during the leader's fight; mob 1663829 was
                //     locked on the leader the whole time)
                //   - hasTarget=false (assist had no own target — confirmed
                //     by the 2nd-combat Combat plan firing with
                //     TARGET-GUID=0 at 02:13:52:184, which is the
                //     CombatGoal Case 2 swap entry point and only fires
                //     when the assist's bot-side target slot is empty)
                //   - Therefore disjunct 1 (needs dmgTaken) and disjunct 2
                //     (needs hasTarget) were both false; only disjunct 3
                //     (Fix AP) could fire. Fix AP requires
                //     bits.Focus_Combat() AND bits.FocusTarget() AND
                //     bits.FocusTarget_Combat() — and at least one of these
                //     was apparently false on the addon side for the entire
                //     25.5s window.
                //
                // Result: partymembercombat=false → CombatGoal precondition
                // (CombatGoal.cs:112) fails → CombatGoal never selected →
                // CombatGoal's Case 2 PressTargetFocus + PressTargetOfTarget
                // swap (CombatGoal.cs:737-745) never runs to acquire the
                // leader's target. The assist sat in FFG for the whole
                // first combat.
                //
                // Note that the 2nd-combat path PROVES the focus IS set on
                // the assist's WoW client: at 02:13:52:332 (assist clock,
                // during leader's 2nd combat with target 1664042) the
                // CombatGoal Case 2 PressTargetFocus (PageUp) + 
                // PressTargetOfTarget (F) at 02:13:52:410 successfully
                // acquired GUID=1664042. Yet Fix AP did not fire during
                // the 1st combat — strongly suggesting transient addon
                // unreliability of bits.Focus_Combat / bits.FocusTarget /
                // bits.FocusTarget_Combat during the first-combat window.
                //
                // Direct precedent: Fix CL at GoapAgent.cs:1701-1704
                // already does this for the partyEngaging key (the
                // ATG.AssistFocus precondition) for the same failure mode:
                //
                //     WorldState[GoapKey.partyEngaging] =
                //         PartyInCombat() ||
                //         (classConfig.Mode == Mode.AssistFocus &&
                //          leaderConnection.LastLeaderState?.HasApproachStart == true);
                //
                // partymembercombat (the CombatGoal precondition) needs
                // the symmetric polled-state fallback. Adding it here lets
                // the planner select CombatGoal once the leader's polled
                // InCombat goes true; CombatGoal's own Case 2 swap then
                // acquires the target via PressTargetFocus +
                // PressTargetOfTarget. No new acquisition logic needed —
                // we only need to UNBLOCK the existing Case 2 path.
                //
                // Why InCombat and not Status==BotStatus.Combat: InCombat
                // is the raw signal (= leader's bits.Combat()) published
                // verbatim from LeaderStateService.cs:280
                // (`InCombat = bits.Combat()`). Status is the derived enum
                // from DetermineStatus() which maps Combat first then
                // falls through to goal-name. While Status=Combat IS
                // triggered by bits.Combat() at LeaderStateService.cs:319-320,
                // InCombat is the more direct semantic match for "is the
                // leader currently in combat with a mob" — the exact
                // question PartyMemberInCombat answers for the partner.
                //
                // TargetGuid != 0 conjunct: mitigates ghost-combat (leader's
                // bits.Combat=true with no real mob targeted) — without
                // this, the assist's CombatGoal would fire on ghost combat,
                // Case 2 swap would acquire nothing (focus's target is 0),
                // and the assist would briefly press buttons against no
                // target. With this gate, the assist only joins when the
                // leader actually has a target to engage.
                //
                // Mode gate (AssistFocus only): for PartyLeader and Grind
                // modes the leader doesn't poll itself — LastLeaderState
                // is null — so this disjunct collapses to false (null-safe
                // ?.InCombat returns false on null) and behavior is unchanged.
                //
                // Why no IsIgnored check here (same rationale as Fix AP
                // above): partymembercombat is a generic engagement signal;
                // IsIgnored / blacklist semantics are gated by
                // GoapKey.allPartyTargetsIsIgnored (the CombatGoal
                // precondition just below partymembercombat). If the
                // leader's target is on IsIgnored, allPartyTargetsIsIgnored
                // resolves true and CombatGoal stays blocked even when this
                // branch returns true. The two preconditions compose
                // without duplicating the IsIgnored check here.
                || (classConfig.Mode == Mode.AssistFocus
                    && leaderConnection.LastLeaderState?.InCombat == true
                    && leaderConnection.LastLeaderState?.TargetGuid != 0)
                // ── Fix EY (run-152 standoff) — symmetric to Fix EX above ──
                //
                // Fix EX (just above) gives the ASSIST a polled fallback for
                // the LEADER's combat state when the assist's bits go stale.
                // Fix EY is the mirror: gives the LEADER a polled fallback
                // for the ASSIST's combat state when the leader's bits go
                // stale.
                //
                // Run-152 evidence: at 13:31:53:378 (assist), the assist
                // entered Section D caster-retreat in combat. By 13:32:59:832
                // the assist was at <253,-4648>, 424y from the leader at
                // <361,-4238> — far beyond the leader's WoW client
                // visibility range (~40-80y for nameplate/focus refresh).
                // The leader's bits.Focus_Combat / bits.FocusTarget /
                // bits.FocusTarget_Combat may stick at stale values once
                // out-of-range. Without a polled fallback, the leader-side
                // Fix AP gate (just above this Fix EX disjunct — three
                // conjuncts of bits.Focus_*) is at the mercy of those
                // stale reads.
                //
                // Polled disjunct: AssistState.InCombat is authoritative —
                // it's set by the assist's PartyStatePublisher from its
                // own bits.Combat() and POSTed to the leader every
                // AssistPostIntervalMs (~500ms). AssistStateStore.IsStale
                // (3000ms threshold by default) ensures we only trust fresh
                // polled state. AnyAssistInCombatWithTarget combines the
                // fresh-non-stale check with InCombat && TargetGuid != 0.
                //
                // TargetGuid != 0 conjunct (symmetric to Fix EX): mitigates
                // ghost-combat — if the assist's bits.Combat=true with no
                // mob targeted, this disjunct doesn't fire and the
                // leader's CombatGoal (which would try to swap via focus
                // chain on Case 2) doesn't press buttons against nothing.
                //
                // Mode gate (PartyLeader only): for AssistFocus the leader
                // doesn't run AssistStateStore (it's the assist who polls
                // the leader, not the other way around) — calling
                // AnyAssistInCombatWithTarget on the assist would always
                // return false. The Mode check makes the disjunct collapse
                // to false in AssistFocus / Grind modes.
                //
                // Why no IsIgnored check here (same rationale as Fix AP /
                // Fix EX above): partymembercombat is a generic engagement
                // signal; IsIgnored / blacklist semantics are composed
                // downstream via GoapKey.allPartyTargetsIsIgnored.
                || (classConfig.Mode == Mode.PartyLeader
                    && assistStateStore.AnyAssistInCombatWithTarget());
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

        // E4: allow the leader to patrol during no-engage-only combat — being damaged
        // by an in-rect mob with nothing fightable to target. FollowRoute then routes
        // away; blacklist-aware nav detours around the rect, carrying us out of range,
        // and once clear dmgTaken lapses so the normal checks below resume. Early
        // return (skips the loot/kill/corpse checks): survival > loot under a live
        // no-engage attack. A fightable target makes InNoEngageOnlyCombat() false, so
        // Combat (cost 4) still preempts patrol.
        if (InNoEngageOnlyCombat())
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
    /// E4: true when the leader is being damaged by a no-engage (in-rect) mob AND has
    /// no fightable target — the case where FollowRoute should run despite "combat" so
    /// the leader retreats instead of stalling (Combat is blocked, the finder skips the
    /// mob via Blacklist.Is, and FollowRoute is otherwise damage-gated). We iterate our
    /// own <c>_knownBlacklistedGuids</c> (a HashSet&lt;int&gt;) rather than
    /// combatLog.DamageTaken — whose element type we don't depend on — testing each via
    /// the confirmed DamageTaken.Contains. Returns false the instant a fightable target
    /// exists (so Combat preempts) or no no-engage mob is actually hitting us (so normal
    /// post-kill/loot handling via the LastCombatKillCount path is preserved).
    /// </summary>
    private bool InNoEngageOnlyCombat()
    {
        // A fightable target in the slot => fight it (Combat), don't retreat.
        if (playerReader.TargetGuid != 0 && !playerReader.IsIgnored(playerReader.TargetGuid))
            return false;

        foreach (int guid in _knownBlacklistedGuids)
        {
            if (playerReader.IsNoEngage(guid) && combatLog.DamageTaken.Contains(guid))
                return true;
        }

        return false;
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
                    $"[GoapAgent] Evade blacklist event guid={evade.TargetGuid} " +
                    $"reason={evade.Reason} — blacklisting (no recovery window; the post-evade " +
                    $"disengage was removed, self-defense stays available).");

                // Press StopAttack + ClearTarget so the auto-attack toggle is released
                // on the evaded mob. CombatGoal deselects the instant the target is
                // cleared/ignored, and its OnExit does NOT release the toggle
                // (CombatGoal.cs:168) — so warriors/auto-attack classes keep swinging
                // at the blacklisted mob until kill credit (log 23) unless we release
                // it here. Idempotent: pressing while already stopped is a no-op.
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
                playerReader.IgnoreTarget(evade.TargetGuid, evade.InRect);

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
                    stopMoving.Stop();
                    input.StopForward(true);

                    // Publish the blacklisted GUID via API so the assist can call
                    // playerReader.IgnoreTarget without relying on the chat message.
                    leaderNavProvider.AddBlacklistedMobGuid(evade.TargetGuid, evade.InRect);

                    // No recovery window, no _evadeLeaderWaiting, no finder suppression:
                    // the post-evade disengage is removed. The finder is gated by
                    // IsInBlacklistArea + AnyAssistCantFollow + its own 6s blacklist
                    // cooldown; self-defense stays available (E4-gated). See CHANGESET2_E_DESIGN.
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
                    // (Finder suppression removed — the ghost window already blocks
                    //  Combat/ATG/PTG, so engagement can't happen during it anyway.)
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

    // -----------------------------------------------------------------------
    // Fix AV (Route A) — Navigation.OnStuckRect{Added,Cleared} handlers
    //
    // Both subscribed in the constructor (PartyLeader mode only) and
    // unsubscribed in Dispose. The leader's Navigation drives these via
    // its TryUnstuck path (AddStuckRect inside ApproachEscape) and via
    // explicit ClearStuckRects calls from ATG/PTG/CombatGoal on plan
    // transitions. The forwarded state ends up in
    // LeaderNavigationProvider.StuckRectsSnapshot, which the HTTP server
    // includes in the next LeaderState response so the assist can apply
    // it via Navigation.AddPropagatedStuckRect.
    //
    // Handlers are intentionally minimal (single forwarding call) —
    // dedup, TTL refresh, and snapshot rebuild all live in
    // LeaderNavigationProvider.AddStuckRect / ClearStuckRects.
    // -----------------------------------------------------------------------

    private void OnNavigationStuckRectAdded(Vector3 center, float halfSize)
    {
        leaderNavProvider.AddStuckRect(center.X, center.Y, halfSize);
    }

    private void OnNavigationStuckRectsCleared()
    {
        leaderNavProvider.ClearStuckRects();
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
