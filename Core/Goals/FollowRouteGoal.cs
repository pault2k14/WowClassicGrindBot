using Core.AreaBlacklist;
using Core.GOAP;
using Core.Party;

using Game;

using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.Extensions;
using SharedLib.NpcFinder;

using System;
using System.Linq;
using System.Numerics;
using System.Threading;

#pragma warning disable 162

namespace Core.Goals;

public sealed class FollowRouteGoal : GoapGoal, IGoapEventListener, IRouteProvider, IEditedRouteReceiver, IDisposable
{
    public const float DEFAULT_COST = 20f;
    public const float COST_OFFSET = 0.1f;

    private readonly float cost;
    public override float Cost => cost;
    public override bool CanRun() => pathSettings.CanRun();

    private const bool debug = false;

    private readonly ILogger<FollowRouteGoal> logger;
    private readonly ConfigurableInput input;
    private readonly Wait wait;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly ClassConfiguration classConfig;
    private readonly IMountHandler mountHandler;
    private readonly Navigation navigation;

    private readonly IBlacklist targetBlacklist;
    private readonly TargetFinder targetFinder;
    private const NpcNames NpcNameToFind = NpcNames.Enemy | NpcNames.Neutral;

    private const int MIN_TIME_TO_START_CYCLE_PROFESSION = 5000;
    private const int CYCLE_PROFESSION_PERIOD = 8000;

    private readonly ManualResetEventSlim sideActivityManualReset;
    private Thread? sideActivityThread;
    private CancellationTokenSource sideActivityCts;

    private readonly PathSettings pathSettings;
    private readonly RestHandler restHandler;
    private readonly ChatReader chatReader;
    private readonly AssistStateStore assistStateStore;
    private readonly LeaderNavigationProvider leaderNavProvider;
    private volatile bool _disposing;

    private int _pauseNavRequested;
    private volatile bool _pausedByLocalLogic;

    // API-based distance gate: leader pauses when assist is too far, stuck, or cant follow.
    private bool _pausedByAssistDistance;

    private Vector3[] mapRoute
    {
        get => pathSettings.Path;
        set => pathSettings.Path = value;
    }

    private DateTime onEnterTime;
    private DateTime _lastRefillWaypointsUtc = DateTime.MinValue;
    private Vector3 _lastRefillTopMap = default;
    private int _lastRefillWaypointCount = -1;

    private const int RefillWaypointsDuplicateCooldownMs = 750;

    // Assist-return rewind logic
    private bool _assistReturnActive;
    private Vector3 _assistReturnTargetW;
    private bool _assistWaitingForFollowing;

    public bool WaitingForAssist =>
        _assistReturnActive || _assistWaitingForFollowing;

    private bool _assistRewindActive;
    private Vector3 _assistRewindAnchorW;
    private int _assistAttempt;

    private const double ASSIST_RETURN_TIMEOUT_ACTIVE_SEC = 25.0;
    private TimeSpan _assistReturnActiveElapsed;
    private DateTime _assistReturnLastTickUtc;
    private bool _assistReturnTimerInit;

    private int _pathTraversalDirection;

    /// <summary>
    /// Set by both AssistReturn completion paths before calling Resume().
    /// Consumed (cleared) inside the ThereAndBack block of RefillWaypoints(false).
    /// Prevents the endpoint-forcing logic from reversing direction when the leader
    /// was placed at a route endpoint by AssistReturn rather than by completing a
    /// natural patrol leg — the leader should continue in the original direction,
    /// not treat the assist's CantFollow position as a ThereAndBack reversal point.
    /// </summary>
    private bool _suppressDirectionEndpointForcing;

    private int _suppressedBlacklistedGuid;
    private DateTime _suppressedBlacklistedUntilUtc;
    private DateTime _suppressTargetFinderUntilUtc = DateTime.MinValue;

    // Distance thresholds matching FollowFocusGoal constants.
    private const float LeaderPauseYards   = FollowFocusGoal.LeaderPauseYards;   // 20y
    private const float LeaderResumeYards  = FollowFocusGoal.LeaderResumeYards;  // 15y

    /// <summary>
    /// When FRG resumes with existing patrol waypoints and the assist is further than
    /// <see cref="FollowFocusGoal.FollowingMaxYards"/> (7y), the leader briefly pauses
    /// here before starting to move. This gives the assist — which our FFG dead-band fix
    /// ensures actively navigates toward the leader whenever rendezvous is unconfirmed —
    /// enough time to close to &lt;7y and confirm rendezvous before the leader pulls away.
    /// Without this pause, both bots move at the same speed and the gap never closes.
    /// </summary>
    private bool _syncPauseActive;
    private DateTime _syncPauseStartUtc;
    private const double SyncPauseTimeoutSec = 1.0;

    // Stale logging — avoid spamming every tick
    private bool _assistWasStaleLogged;

    #region IRouteProvider

    public DateTime LastActive => navigation.LastActive;
    public Vector3[] MapRoute() => mapRoute;
    public Vector3[] PathingRoute() => navigation.TotalRoute;
    public bool HasNext() => navigation.HasNext();
    public Vector3 NextMapPoint() => navigation.NextMapPoint();

    #endregion

    public FollowRouteGoal(
        float cost,
        PathSettings pathSettings,
        ILogger<FollowRouteGoal> logger,
        ConfigurableInput input, Wait wait, PlayerReader playerReader,
        AddonBits bits,
        ClassConfiguration classConfig,
        Navigation navigation,
        IMountHandler mountHandler, TargetFinder targetFinder,
        IBlacklist targetBlacklist, RestHandler restHandler,
        ChatReader chatReader,
        AssistStateStore assistStateStore,
        LeaderNavigationProvider leaderNavProvider)
    : base("Follow " + System.IO.Path.GetFileNameWithoutExtension(pathSettings.FileName))
    {
        this.cost = cost;

        this.logger = logger;
        this.input = input;
        this.wait = wait;
        this.classConfig = classConfig;
        this.playerReader = playerReader;
        this.bits = bits;
        this.pathSettings = pathSettings;
        this.mountHandler = mountHandler;
        this.targetFinder = targetFinder;
        this.targetBlacklist = targetBlacklist;
        this.chatReader = chatReader;
        this.assistStateStore = assistStateStore;
        this.leaderNavProvider = leaderNavProvider;

        if (pathSettings.Requirements.Count > 0)
        {
            Keys = [
             new KeyAction() {
                RequirementsRuntime = pathSettings.RequirementsRuntime,
                Name = "Follow " + System.IO.Path.GetFileNameWithoutExtension(pathSettings.FileName)
            }];
        }

        pathSettings.Finished = () => !navigation.HasWaypoint();

        this.navigation = navigation;
        navigation.OnPathCalculated   += Navigation_OnPathCalculated;
        navigation.OnDestinationReached += Navigation_OnDestinationReached;
        navigation.OnWayPointReached  += Navigation_OnWayPointReached;
        navigation.OnPathFailed       += Navigation_OnPathFailed;

        if (classConfig.Mode == Mode.PartyLeader)
        {
            AddPrecondition(GoapKey.assistrequestreturnorisfollowing, true);
        }

        if (classConfig.Mode == Mode.AttendedGather)
        {
            AddPrecondition(GoapKey.dangercombat, false);
            navigation.OnAnyPointReached += Navigation_OnWayPointReached;
        }
        else if (classConfig.Mode == Mode.PartyLeader)
        {
            AddPrecondition(GoapKey.partyleadercanfollowroute, true);
        }
        else
        {
            if (classConfig.Loot)
                AddPrecondition(GoapKey.incombat, false);

            AddPrecondition(GoapKey.damagedone, false);
            AddPrecondition(GoapKey.damagetaken, false);
            AddPrecondition(GoapKey.producedcorpse, false);
            AddPrecondition(GoapKey.consumecorpse, false);
        }

        sideActivityCts = new();
        sideActivityManualReset = new(false);

        if (classConfig.Mode == Mode.AttendedGather)
        {
            if (classConfig.GatherFindKeyConfig.Length > 1)
            {
                sideActivityThread = new(Thread_AttendedGather);
                sideActivityThread.Start();
            }
        }
        else
        {
            sideActivityThread = new(Thread_LookingForTarget);
            logger.LogInformation("FollowRouteGoal: Started sideActivityThread Thread_LookingForTarget");
            sideActivityThread.Start();
        }

        this.restHandler = restHandler;
    }

    public void Dispose()
    {
        if (_disposing) return;
        _disposing = true;

        try
        {
            navigation.OnPathCalculated   -= Navigation_OnPathCalculated;
            navigation.OnDestinationReached -= Navigation_OnDestinationReached;
            navigation.OnWayPointReached  -= Navigation_OnWayPointReached;
            navigation.OnPathFailed       -= Navigation_OnPathFailed;
            if (classConfig.Mode == Mode.AttendedGather)
                navigation.OnAnyPointReached -= Navigation_OnWayPointReached;

            sideActivityCts.Cancel();
            sideActivityManualReset.Set();

            if (sideActivityThread is { IsAlive: true })
            {
                if (!sideActivityThread.Join(millisecondsTimeout: 2000))
                    logger.LogWarning("FollowRouteGoal: sideActivityThread did not stop within timeout.");
            }

            sideActivityCts.Dispose();
            navigation.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FollowRouteGoal.Dispose failed");
        }
    }

    private void Abort()
    {
        if (bits.Target() && targetBlacklist.Is())
            SuppressCurrentTargetBriefly();

        if (!targetBlacklist.Is())
            navigation.StopMovement();

        navigation.PausePathing();

        // Clear the published waypoint — leader is no longer actively navigating
        // the patrol route (combat, evade, paused for assist, etc.).
        leaderNavProvider.ClearTargetWaypoint();

        _syncPauseActive = false;

        sideActivityManualReset.Reset();
        targetFinder.Reset();
        ResetRefillWaypointsGuard();
    }

    private void Resume()
    {
        // Apply per-route area blacklists
        if (pathSettings.MapBlacklistRects is { Length: > 0 })
        {
            navigation.AreaBlacklist = BlacklistConversion.BuildWorldBlacklistFromMapRects(
                pathSettings.MapBlacklistRects,
                playerReader.WorldMapArea
            );
            navigation.DetourMargin = 12f;
            navigation.MaxDetourAttemptsPerTarget = 6;
        }
        else
        {
            navigation.AreaBlacklist = null;
        }

        logger.LogInformation($"Blacklist rect count (map): {pathSettings.MapBlacklistRects.Length}");

        logger.LogInformation(
            $"[FRG] navHash={navigation.GetHashCode()} " +
            $"blacklistNull={navigation.AreaBlacklist is null} " +
            $"blacklistHash={(navigation.AreaBlacklist?.GetHashCode().ToString() ?? "null")} " +
            $"pos={playerReader.WorldPos} " +
            $"inside={(navigation.AreaBlacklist?.ContainsWorld(playerReader.WorldPos) == true)}");

        logger.LogInformation($"Player inside blacklist: {navigation.AreaBlacklist?.ContainsWorld(playerReader.WorldPos) == true}");

        while (restHandler.IsResting() && !assistStateStore.AnyAssistCantFollow())
            wait.Update(1000);

        onEnterTime = DateTime.UtcNow;
        ResetRefillWaypointsGuard();

        if (sideActivityCts.IsCancellationRequested)
            sideActivityCts = new();

        // Reset the "wantNavPaused caused us to pause" flag on Resume. Abort()
        // pauses navigation through a different code path and does not touch
        // _pausedByLocalLogic, so it can carry stale state across Abort/Resume
        // cycles. Without this reset, if wantNavPaused happens to be true on
        // the first post-Resume Update tick, the transition log
        // "[FRG] Target acquired -> stopping navigation" (line 747) does not
        // fire because the gate `wantNavPaused && !_pausedByLocalLogic` is
        // false — silencing the diagnostics for an actual navigation pause.
        // Observed in log 24 (leader 21:39:36:079 → 21:39:51:090): 15 seconds
        // of complete silence after ATG's focus-chain race re-acquired the
        // blacklisted mob just before the goal switch into FRG.
        _pausedByLocalLogic = false;

        if (_suppressTargetFinderUntilUtc == DateTime.MinValue || DateTime.UtcNow >= _suppressTargetFinderUntilUtc)
            sideActivityManualReset.Set();
        else
            logger.LogInformation($"[FRG] Resume: target finder still suppressed for {(_suppressTargetFinderUntilUtc - DateTime.UtcNow).TotalMilliseconds:F0}ms.");

        bool assistIsFollowing  = assistStateStore.AnyAssistIsFollowing();
        bool assistCantFollow   = assistStateStore.AnyAssistCantFollow();

        logger.LogInformation(
            $"[FRG] Resume: HasWaypoint={navigation.HasWaypoint()} HasNext={navigation.HasNext()} " +
            $"AssistIsFollowing={assistIsFollowing} AssistCantFollow={assistCantFollow} " +
            $"navActive={navigation.Active} wp={navigation.WaypointCount} route={navigation.RouteCount}");

        if (navigation.HasWaypoint() || navigation.HasNext())
        {
            if (_pausedByAssistDistance)
            {
                // The distance gate was active when this Resume() was triggered (e.g. by a
                // transient goal like BlacklistTargetGoal causing an Abort/Resume cycle).
                // Re-apply the pause — do NOT restart navigation just because another goal
                // briefly preempted FRG. Without this, the leader keeps moving forward every
                // ~400ms as each BlacklistTarget cycle undoes the distance gate pause.
                logger.LogInformation("[FRG] Resume - distance gate still active, re-applying pause.");
                navigation.PausePathing();
            }
            else
            {
                // In PartyLeader mode, wait for the assist to be within FollowingMaxYards
                // before resuming patrol. If we start moving immediately and the assist is
                // in the dead-band (7–14y), both bots run at the same speed and the assist
                // can never close the gap to confirm rendezvous. The FFG dead-band fix
                // guarantees the assist actively navigates during this pause — together the
                // two halves ensure rendezvous is confirmed before the leader pulls away.
                if (classConfig.Mode == Mode.PartyLeader && assistIsFollowing)
                {
                    // Publish the current top patrol waypoint BEFORE pausing. Abort()
                    // called ClearTargetWaypoint(), so HasTargetWaypoint=false during the
                    // pause unless we explicitly re-publish. FFG's leaderJustPublishedWaypoint
                    // detection (false→true transition) fires within one API poll (~250ms)
                    // and sets NavigatingToLeader → AnyAssistNavigating()=true → fast resolve.
                    PublishPatrolWaypoint();

                    // Guard: FRG.OnEnter() can fire twice in rapid succession when GOAP
                    // briefly selects another goal (e.g. CorpseConsumed) and immediately
                    // re-selects FRG (~15ms later). A second Resume() call would reset
                    // _syncPauseStartUtc, extending the pause by up to 1s and causing the
                    // assist's brief NavigatingToLeader flash (from the first arm) to fall
                    // outside the new timer window. If already armed, just re-publish the
                    // waypoint (to keep it fresh) and leave the original timer intact.
                    if (_syncPauseActive)
                    {
                        logger.LogInformation(
                            "[FRG] Resume - sync-pause already active: re-published top patrol waypoint, keeping original timer.");
                        navigation.PausePathing();
                        return;
                    }

                    logger.LogInformation(
                        "[FRG] Resume - sync-pause (existing waypoints): published top patrol waypoint, " +
                        "waiting for assist to begin navigating before resuming patrol.");
                    _syncPauseActive = true;
                    _syncPauseStartUtc = DateTime.UtcNow;
                    navigation.PausePathing();
                    return;
                }

                logger.LogInformation("[FRG] Resume - preserving existing navigation progress");
                navigation.Resume();
            }
        }
        else if (classConfig.Mode == Mode.PartyLeader && assistIsFollowing)
        {
            // Load the closest patrol waypoint and publish it BEFORE sync-pausing.
            // This gives FFG a HasTargetWaypoint false→true transition within one API
            // poll (~250ms), triggering leaderJustPublishedWaypoint → NavigatingToLeader
            // → AnyAssistNavigating()=true → sync-pause resolves fast.
            // Without the sync-pause the leader would sprint away immediately; without
            // the pre-publish the assist can't react until after the 1s timeout expires.
            logger.LogInformation(
                "[FRG] Resume - AssistIsFollowing branch (no existing waypoints): loading waypoint, " +
                "publishing, then sync-pausing until assist begins navigating.");
            ClearAssistReturnState();
            navigation.ClearAllRoutes();
            RefillWaypoints(true);   // sets waypoints AND calls PublishPatrolWaypoint()

            if (_syncPauseActive)
            {
                // Second Resume() in rapid succession — keep the original timer, just
                // hold pathing. See the equivalent guard in the HasWaypoint branch above.
                logger.LogInformation(
                    "[FRG] Resume - sync-pause already active (no-waypoint branch): keeping original timer.");
                navigation.PausePathing();
            }
            else
            {
                _syncPauseActive = true;
                _syncPauseStartUtc = DateTime.UtcNow;
                navigation.PausePathing(); // hold — don't start moving yet
            }
        }
        else if (classConfig.Mode == Mode.PartyLeader && assistCantFollow)
        {
            if (_assistReturnActive)
            {
                logger.LogInformation("[FRG] Resume: AssistCantFollow=true but AssistReturn already active — holding position.");
                navigation.PausePathing();
            }
            else
            {
                AssistState? cantFollowState = assistStateStore.GetCantFollowState();
                if (cantFollowState != null)
                {
                    Vector3 assistWaypoint = cantFollowState.MapPosNoZ;
                    logger.LogInformation($"[FRG] Resume - navigating to assist CantFollow position {assistWaypoint}");
                    GoToOneWaypoint(assistWaypoint);
                }
                else
                {
                    logger.LogWarning("[FRG] Resume: AnyAssistCantFollow=true but no valid state — holding.");
                    navigation.PausePathing();
                }
            }
        }
        else
        {
            logger.LogInformation("[FRG] Resume - else branch -> RefillWaypoints");
            RefillWaypoints(false);
        }

        logger.LogInformation(
            $"[FRG] Resume complete: navActive={navigation.Active} wp={navigation.WaypointCount} route={navigation.RouteCount}");

        if (playerReader.Class != UnitClass.Druid)
            MountIfPossible();
    }

    public void OnGoapEvent(GoapEventArgs e)
    {
        if (e is GoapStateEvent g)
        {
            switch (g.Key)
            {
                case GoapKey.assistisfollowing:
                    if (!assistStateStore.AnyAssistIsFollowing() && !assistStateStore.AnyAssistNavigating())
                    {
                        // Assist is genuinely unavailable — not following and not navigating toward us.
                        logger.LogInformation("FollowRouteGoal: OnGoapEvent - assist truly unavailable (not following, not navigating) — aborting.");
                        Abort();
                    }
                    else
                    {
                        bool wasWaiting = _assistWaitingForFollowing;
                        ClearAssistReturnState();

                        if (wasWaiting)
                        {
                            // Leader navigated to the assist's position and was paused
                            // waiting for "i'm following" confirmation. Now the assist has
                            // confirmed — clear the old return route and refill from here.
                            logger.LogInformation("[FRG] Assist confirmed following after leader navigated to them — resuming patrol.");
                            // Suppress the endpoint direction-forcing for the same reason as
                            // Navigation_OnDestinationReached Path A: the leader is resuming
                            // from the assist's CantFollow position, not from a natural
                            // ThereAndBack reversal point.
                            _suppressDirectionEndpointForcing = pathSettings.PathThereAndBack;
                            navigation.ClearAllRoutes();
                            Resume();
                        }
                        else
                        {
                            // Assist became available (initial connection or stale recovery).
                            // Do NOT call ClearAllRoutes() here — if the leader already has
                            // active waypoints from a normal patrol, wiping them causes a stall:
                            // Resume() would then call RefillWaypoints(true) (single closest
                            // waypoint), the leader walks to it, OnDestinationReached fires, and
                            // RefillWaypoints starts over — freezing the leader for several seconds
                            // whenever the assist's POST delivery briefly goes stale and recovers.
                            // Resume() already handles "HasWaypoint → preserve, no waypoints →
                            // refill" correctly without any pre-clearing.
                            logger.LogInformation(
                                $"[FRG] Assist available (following={assistStateStore.AnyAssistIsFollowing()} " +
                                $"navigating={assistStateStore.AnyAssistNavigating()}) — resuming. " +
                                $"navActive={navigation.Active} wp={navigation.WaypointCount}");
                            Resume();
                        }
                    }
                    break;

                case GoapKey.assistrequestreturn:
                    // Fires when store transitions to AnyAssistCantFollow = true.
                    if (assistStateStore.AnyAssistCantFollow())
                    {
                        AssistState? cantFollow = assistStateStore.GetCantFollowState();
                        if (cantFollow != null)
                        {
                            logger.LogInformation(
                                $"[FRG] OnGoapEvent assistrequestreturn — navigating to assist at " +
                                $"({cantFollow.MapX:0.00},{cantFollow.MapY:0.00})");

                            if (_assistReturnActive)
                            {
                                logger.LogInformation("[FRG] OnGoapEvent: AssistReturn already active — ignoring.");
                                break;
                            }

                            GoToOneWaypoint(cantFollow.MapPosNoZ);
                        }
                    }
                    break;

                case GoapKey.incombat:
                    if ((classConfig.Mode != Mode.PartyLeader && classConfig.Mode != Mode.AssistFocus)
                        && bits.Combat())
                    {
                        logger.LogInformation("FollowRouteGoal: OnGoapEvent - Entered Combat while following route, trying to exit!");
                        Abort();
                    }
                    break;

                case GoapKey.partyincombat:
                    if ((classConfig.Mode == Mode.PartyLeader || classConfig.Mode == Mode.AssistFocus)
                        && (bits.Combat() || bits.Focus_Combat()))
                    {
                        logger.LogInformation("FollowRouteGoal: OnGoapEvent - Party entered Combat, trying to exit!");
                        Abort();
                    }
                    break;
            }
        }

        if (e.GetType() == typeof(AbortEvent))
            Abort();
        else if (e.GetType() == typeof(ResumeEvent))
            Resume();
    }

    public override void OnEnter() => Resume();
    public override void OnExit() => Abort();

    public override void Update()
    {
        // ── Target finder suppression expiry (must run before early returns) ──
        // Re-enable the side thread once the evade-blacklist suppression window elapses.
        if (_suppressTargetFinderUntilUtc != DateTime.MinValue &&
            DateTime.UtcNow >= _suppressTargetFinderUntilUtc)
        {
            _suppressTargetFinderUntilUtc = DateTime.MinValue;
            logger.LogInformation("[FRG] Target finder suppression elapsed — resuming.");
            sideActivityManualReset.Set();
        }

        // ── Sync-pause: hold position until the assist starts navigating toward us ──
        // Set in Resume() when the leader has existing patrol waypoints.
        // Resolves when the assist is actively navigating (NavigatingToLeader) OR already
        // Following. The original condition — NavigatingToLeader only — was too narrow:
        // when the assist is co-located (<7y) on re-entry, FFG's leaderJustPublishedWaypoint
        // detection calls StartNavigatingToLeader, but UpdateNavigatingToLeader immediately
        // exits to Idle on the very next tick (dist < FollowingMaxYards=7y). The
        // NavigatingToLeader status lasts ~15ms — far too brief for the leader's API poll
        // (~250ms) to catch. Adding AnyAssistIsFollowing() covers the co-located case:
        // if the assist is Following AND within range, they are already in position and
        // there is no head-start problem to solve.
        if (_syncPauseActive)
        {
            double elapsed = (DateTime.UtcNow - _syncPauseStartUtc).TotalSeconds;
            bool assistNavigating = assistStateStore.AnyAssistNavigating();
            bool assistFollowing  = assistStateStore.AnyAssistIsFollowing();
            bool assistReady      = assistNavigating || assistFollowing;

            if (assistReady || elapsed >= SyncPauseTimeoutSec)
            {
                _syncPauseActive = false;
                logger.LogInformation(
                    $"[FRG] Sync-pause complete: assistNavigating={assistNavigating} " +
                    $"assistFollowing={assistFollowing} elapsed={elapsed:0.1}s " +
                    $"— resuming patrol navigation.");
                navigation.Resume();
            }
            else
            {
                wait.Update();
                return;
            }
        }

        // NOTE: The watchdog that previously re-enabled the side thread here was removed.
        // It caused a race on bot stop: Active=false fires Abort() (resets the event) on
        // the setter thread while GoapThread is still in Update() — the watchdog would
        // immediately re-enable the event, leaving Thread_LookingForTarget running after
        // the bot was stopped. Resume() is the correct and only place to re-enable the
        // side thread; Abort() is the correct place to disable it.

        // ── Consume pause requests from side thread ──
        if (Interlocked.Exchange(ref _pauseNavRequested, 0) == 1)
        {
            navigation.PausePathing();
            _pausedByLocalLogic = true;
        }

        if (bits.Target() && bits.Target_Dead())
        {
            Log("Has target but its dead.");
            input.PressClearTarget();
            wait.Update();

            if (bits.Target())
            {
                SendGoapEvent(ScreenCaptureEvent.Default);
                LogWarning($"Unable to clear target!");
            }
        }

        if (bits.Drowning())
            input.PressJump();

        if (IsSuppressedBlacklistedTarget())
        {
            Log("Suppressed recently-blacklisted target reacquired, clearing again");
            input.PressClearTarget();
            wait.Update();
            return;
        }

        // ── PartyLeader: enforce assist is following ──────────────────────
        if (classConfig.Mode == Mode.PartyLeader)
        {
            bool assistIsFollowing = assistStateStore.AnyAssistIsFollowing();
            bool assistCantFollow  = assistStateStore.AnyAssistCantFollow();
            bool assistStale       = assistStateStore.HasSeenAnyAssist && assistStateStore.AnyAssistStale();

            if (!assistIsFollowing && !assistCantFollow)
            {
                if (!assistStale)
                {
                    // Assist exists but neither Following nor CantFollow — still navigating.
                    // Normal; this is handled by the distance gate below.
                }
                else
                {
                    // Assist has gone stale — hold position.
                    if (!_assistWasStaleLogged)
                    {
                        _assistWasStaleLogged = true;
                        logger.LogWarning("[FRG] Assist state is STALE — holding position indefinitely.");
                    }
                    navigation.PausePathing();
                    wait.Update();
                    return;
                }
            }
            else
            {
                _assistWasStaleLogged = false;
            }

            // ── API-based distance / status gate ───────────────────────────
            if (classConfig.Mode == Mode.PartyLeader)
            {
                bool shouldPause = assistStateStore.ShouldLeaderPauseForAssist(
                    playerReader.WorldPos, LeaderPauseYards);

                bool shouldResume = assistStateStore.ShouldLeaderResumePatrol(
                    playerReader.WorldPos, LeaderResumeYards);

                if (shouldPause && !_pausedByAssistDistance)
                {
                    float dist = assistStateStore.GetNearestAssistDistanceYards(playerReader.WorldPos);
                    AssistState? stuckAssist = assistStateStore.GetCantFollowState()
                        ?? assistStateStore.GetAll().FirstOrDefault(
                            a => !assistStateStore.IsStale(a) && a.Status == BotStatus.Stuck);

                    logger.LogInformation(
                        $"[FRG] Pausing for assist — dist={dist:0.0}y " +
                        $"status={stuckAssist?.Status.ToString() ?? "TooFar"}");

                    // Diagnostic detail: print the inputs that produced `dist`,
                    // so we can distinguish "leader genuinely > 20 y away from
                    // assist" from "leader is reading a stale assist snapshot".
                    // Added after log 30 showed the assist reporting
                    // "Reached follow position (dist=7.0y)" only 220 ms before
                    // the leader logged "Pausing for assist — dist=20.1y" —
                    // physically impossible without one side reading bad data.
                    Vector3 leaderPos = playerReader.WorldPos;
                    logger.LogInformation(
                        $"[FRG] Pause-detail: leaderPos=<{leaderPos.X:F2},{leaderPos.Y:F2},{leaderPos.Z:F2}> " +
                        $"pauseYards={LeaderPauseYards}");
                    foreach (AssistState s in assistStateStore.GetAll())
                    {
                        float d = leaderPos.WorldDistanceXYTo(s.WorldPos);
                        logger.LogInformation(
                            $"[FRG] Pause-detail: assist id='{s.AssistId}' " +
                            $"pos=<{s.WorldX:F2},{s.WorldY:F2},{s.WorldZ:F2}> " +
                            $"status={s.Status} cantFollow={s.CantFollow} " +
                            $"dist={d:F2}y ageMs={s.AgeMs:F0} stale={assistStateStore.IsStale(s)}");
                    }

                    _pausedByAssistDistance = true;
                    // StopMovement must be called before PausePathing.
                    // PausePathing() suspends navigation and stops steering (left/right keys)
                    // but does NOT release the forward movement key — without StopMovement()
                    // the character keeps running forward at walking speed into obstacles.
                    // Abort() calls both; the distance gate must do the same.
                    navigation.StopMovement();
                    navigation.PausePathing();

                    // If assist is CantFollow, navigate to them.
                    if (assistCantFollow && !_assistReturnActive)
                    {
                        AssistState? cantFollow = assistStateStore.GetCantFollowState();
                        if (cantFollow != null)
                        {
                            logger.LogInformation(
                                $"[FRG] Assist is CantFollow — navigating to ({cantFollow.MapX:0.00},{cantFollow.MapY:0.00})");
                            GoToOneWaypoint(cantFollow.MapPosNoZ);
                        }
                    }
                }
                else if (!shouldPause && shouldResume && _pausedByAssistDistance)
                {
                    float dist = assistStateStore.GetNearestAssistDistanceYards(playerReader.WorldPos);
                    logger.LogInformation(
                        $"[FRG] Assist Following and within range (dist={dist:0.0}y) — resuming patrol.");

                    // Same diagnostic detail as the pause path. The pause and
                    // resume thresholds are different (LeaderPauseYards=20,
                    // LeaderResumeYards=15), and during a flapping window the
                    // resume side can also fire on stale data.
                    Vector3 leaderPos = playerReader.WorldPos;
                    logger.LogInformation(
                        $"[FRG] Resume-detail: leaderPos=<{leaderPos.X:F2},{leaderPos.Y:F2},{leaderPos.Z:F2}> " +
                        $"resumeYards={LeaderResumeYards}");
                    foreach (AssistState s in assistStateStore.GetAll())
                    {
                        float d = leaderPos.WorldDistanceXYTo(s.WorldPos);
                        logger.LogInformation(
                            $"[FRG] Resume-detail: assist id='{s.AssistId}' " +
                            $"pos=<{s.WorldX:F2},{s.WorldY:F2},{s.WorldZ:F2}> " +
                            $"status={s.Status} cantFollow={s.CantFollow} " +
                            $"dist={d:F2}y ageMs={s.AgeMs:F0} stale={assistStateStore.IsStale(s)}");
                    }

                    _pausedByAssistDistance = false;
                    // Normal flow below will call navigation.Resume() / RefillWaypoints.
                    navigation.ClearAllRoutes();
                    RefillWaypoints(false);
                }

                if (_pausedByAssistDistance && !_assistReturnActive)
                {
                    wait.Update();
                    return;
                }
            }
        }

        // ── Combat target gate ─────────────────────────────────────────────
        // wantNavPaused must reject targets that are in playerReader.IsIgnored,
        // not just targetBlacklist (the zone-rect blacklist). They are different
        // sets: targetBlacklist gates region-based avoidance; IsIgnored gates
        // per-mob blacklisting via evade dispatches (HandleGoapEvent's session-25
        // Fix 1, ATG/CombatGoal/PTG real-evade sites, and the agent-level diff
        // loop on the assist).
        //
        // Without the IsIgnored filter, the leader pauses navigation on a mob
        // that was just blacklisted — defeating the entire point of the 25 s
        // evade-recovery window, since the leader can't retreat and the mob
        // keeps attacking. Observed in log 26 (leader 23:42:10:687 → 23:42:34:667,
        // 24 s of stationary inside the first window, then 23:42:37:265 →
        // 23:42:46:283, 9 s of stationary inside the second window). Side-thread
        // suppression (Fix 2) was working — zero Tab presses during the windows
        // — but the leader's bits.Target() stayed pointing at guid=7968930
        // throughout (confirmed by the CombatGoal IsIgnored bail at 23:42:34:759
        // seeing the same guid from before the evade), so wantNavPaused was
        // being driven by a blacklisted target.
        //
        // After this filter:
        //   wantNavPaused = false (target is in IsIgnored)
        //   → "Target did not meet requirements" branch at line 765 fires:
        //     - PressClearTarget (re-press, in case the previous one was lost)
        //     - targetFinder.Reset
        //     - sideActivityManualReset.Set (harmless during suppression because
        //       Fix 2a's targetFinder.DisableUntil makes Search() return false)
        //   → !wantNavPaused at line 820 lets navigation.Update run
        //   → leader physically retreats along the patrol route
        bool wantNavPaused = bits.Target() && bits.Target_Hostile()
            && bits.Target_Alive() && !bits.Target_Tagged() && playerReader.WithInCombatRange()
            && !targetBlacklist.Is()
            && !playerReader.IsIgnored(playerReader.TargetGuid);

        if (wantNavPaused && !_pausedByLocalLogic)
        {
            logger.LogInformation("[FRG] Target acquired -> stopping navigation");
            navigation.PausePathing();
            _pausedByLocalLogic = true;
        }

        if (!wantNavPaused && bits.Target() && !bits.Target_Dead())
        {
            Log("Target did not meet requirements.");
            input.PressClearTarget();
            wait.Update();
            targetFinder.Reset();
            sideActivityManualReset.Set();

            if (_pausedByLocalLogic)
            {
                navigation.Resume();
                _pausedByLocalLogic = false;
            }

            Interlocked.Exchange(ref _pauseNavRequested, 0);
        }

        if (!wantNavPaused && _pausedByLocalLogic)
        {
            navigation.Resume();
            _pausedByLocalLogic = false;
            // Re-enable the target finder. This handles the case where a target
            // disappeared completely (bits.Target()=false) while _pausedByLocalLogic
            // was true — the "target did not meet requirements" block above only fires
            // when bits.Target() is true, so without this Set() the side thread would
            // stay paused indefinitely. This is the specific scenario the broad watchdog
            // was masking; fixing it here is safe because it only fires when
            // !wantNavPaused (no live target warrants a pause).
            sideActivityManualReset.Set();
        }

        // ── Assist return state machine ─────────────────────────────────────
        if (_assistReturnActive)
        {
            TickAssistReturnTimeout(wantNavPaused);

            if (!_assistReturnActive)
                return;

            if (_assistRewindActive)
            {
                float distToAnchor = playerReader.WorldPos.WorldDistanceXYTo(_assistRewindAnchorW);
                if (distToAnchor < 2.5f)
                {
                    _assistRewindActive = false;
                    AssistState? cantFollow = assistStateStore.GetCantFollowState();
                    if (cantFollow != null)
                    {
                        logger.LogInformation($"[FRG] AssistReturn rewind reached. Retrying assist target.");
                        navigation.SetSingleWaypoint(cantFollow.MapPosNoZ);
                    }
                }
            }
        }

        if (!wantNavPaused)
            navigation.Update(CancellationToken.None);

        if (bits.Combat() && classConfig.Mode != Mode.AttendedGather)
            return;

        RandomJump();
        wait.Update();
    }

    private bool IsDuplicateRecentRefill(Vector3 topMapPoint, int waypointCount)
    {
        if (_lastRefillWaypointCount != waypointCount) return false;
        if (_lastRefillTopMap == default) return false;
        float d = _lastRefillTopMap.MapDistanceXYTo(topMapPoint);
        if (d > 0.01f) return false;
        return (DateTime.UtcNow - _lastRefillWaypointsUtc).TotalMilliseconds < RefillWaypointsDuplicateCooldownMs;
    }

    private void RecordRefillWaypoints(Vector3 topMapPoint, int waypointCount)
    {
        _lastRefillTopMap = topMapPoint;
        _lastRefillWaypointCount = waypointCount;
        _lastRefillWaypointsUtc = DateTime.UtcNow;
    }

    private void ResetRefillWaypointsGuard()
    {
        _lastRefillTopMap = default;
        _lastRefillWaypointCount = -1;
        _lastRefillWaypointsUtc = DateTime.MinValue;
    }

    private void SuppressCurrentTargetBriefly()
    {
        if (!bits.Target()) return;
        _suppressedBlacklistedGuid = playerReader.TargetGuid;
        _suppressedBlacklistedUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
    }

    public void SuppressTargetFinderBriefly(int durationMs)
    {
        _suppressTargetFinderUntilUtc = DateTime.UtcNow.AddMilliseconds(durationMs);

        // Two layers of defence:
        //
        // 1. ManualResetEventSlim.Reset() — blocks the side thread on its NEXT
        //    Wait() call. Insufficient on its own: if Wait() already returned
        //    before the Reset was issued (HTTP thread runs HandleGoapEvent
        //    while the side thread is mid-iteration), the in-flight Search()
        //    continues, presses Tab, and acquires whatever's nearest.
        //
        // 2. targetFinder.DisableUntil(utc) — the next Search() call short-
        //    circuits inside its own IsTargetFinderDisabled() check
        //    (TargetFinder.cs:94) and returns false BEFORE pressing Tab. This
        //    is the layer that actually stops the in-flight case once the
        //    side thread loops back to the top of its Search(). For the
        //    extreme race where Search is already past line 94 and about to
        //    press Tab, the post-Search guard inside Thread_LookingForTarget
        //    catches and discards the result.
        sideActivityManualReset.Reset();
        targetFinder.DisableUntil(_suppressTargetFinderUntilUtc);

        logger.LogInformation($"[FRG] Target finder suppressed for {durationMs}ms after evade-blacklist broadcast.");
    }

    private bool IsSuppressedBlacklistedTarget()
    {
        return bits.Target() &&
               playerReader.TargetGuid != 0 &&
               playerReader.TargetGuid == _suppressedBlacklistedGuid &&
               DateTime.UtcNow < _suppressedBlacklistedUntilUtc;
    }

    private void Thread_LookingForTarget()
    {
        while (!sideActivityCts.IsCancellationRequested)
        {
            sideActivityManualReset.Wait();

            if (pathSettings.CanRunSideActivity() &&
                targetFinder.Search(NpcNameToFind, bits.Target_NotDead, sideActivityCts.Token))
            {
                // Belt-and-suspenders for the extreme race: SuppressTargetFinderBriefly
                // was called by HandleGoapEvent on a different thread WHILE this Search()
                // was already past TargetFinder.cs:94's IsTargetFinderDisabled() check
                // and pressed Tab. If that happened, discard the result — do not let
                // the existing branches below queue _pauseNavRequested, since that would
                // leave wantNavPaused=true on subsequent FRG.Update ticks and stall
                // navigation for the rest of the suppression window.
                //
                // Observed in log 25:
                //   23:01:23:458  HandleGoapEvent → SuppressTargetFinderBriefly
                //                  → ManualResetEventSlim.Reset() (no effect on
                //                    in-flight thread) + DisableUntil (now also added,
                //                    but thread was already past the Search check)
                //   23:01:23:731  Tab pressed (273 ms later, mid-Search)
                //   23:01:23:732  "Found target!" → _pauseNavRequested=1
                //   23:01:23:xxx  navigation paused, wantNavPaused=true 25 s
                //
                // After this guard, the same race produces:
                //   "[FRG] Side thread acquired target during suppression — discarding."
                //   ClearTarget pressed, _pauseNavRequested NOT queued, navigation
                //   continues per FRG's normal patrol logic.
                if (_suppressTargetFinderUntilUtc != DateTime.MinValue &&
                    DateTime.UtcNow < _suppressTargetFinderUntilUtc)
                {
                    logger.LogInformation(
                        "[FRG] Side thread acquired target during suppression — discarding.");
                    if (bits.Target())
                    {
                        input.PressClearTarget();
                        wait.Update();
                    }
                    targetFinder.Reset();
                    sideActivityManualReset.Reset();
                    wait.Update();
                    continue;
                }

                if (bits.Target() && bits.TargetTarget_PlayerOrPet()
                    && playerReader.IsIgnored(playerReader.TargetGuid)
                    && (bits.Combat() || bits.Focus_Combat()))
                {
                    Log("Found area blacklisted target targeting us in combat!");
                    sideActivityManualReset.Reset();
                    targetFinder.Reset();
                    Interlocked.Exchange(ref _pauseNavRequested, 1);
                }
                else if (bits.Target() && (targetBlacklist.Is() || playerReader.IsIgnored(playerReader.TargetGuid)))
                {
                    Log("Blacklisted target found, clearing target");
                    SuppressCurrentTargetBriefly();
                    input.PressClearTarget();
                    wait.Update();
                    targetFinder.Reset();
                    sideActivityManualReset.Set();
                }
                else
                {
                    Log("Found target!");
                    bool actionable =
                        bits.Target() &&
                        bits.Target_Hostile() &&
                        bits.Target_Alive() &&
                        !bits.Target_Tagged() &&
                        playerReader.WithInCombatRange() &&
                        playerReader.WithInPullRange() &&
                        !targetBlacklist.Is();

                    if (actionable)
                    {
                        sideActivityManualReset.Reset();
                        targetFinder.Reset();
                        Interlocked.Exchange(ref _pauseNavRequested, 1);
                    }
                    else
                    {
                        sideActivityManualReset.Reset();
                        targetFinder.Reset();
                        Interlocked.Exchange(ref _pauseNavRequested, 1);
                    }
                }
            }

            wait.Update();
        }

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("LookingForTarget Thread stopped!");
    }

    private void Thread_AttendedGather()
    {
        sideActivityManualReset.Wait();
        while (!sideActivityCts.IsCancellationRequested)
        {
            if ((DateTime.UtcNow - onEnterTime).TotalMilliseconds > MIN_TIME_TO_START_CYCLE_PROFESSION)
                AlternateGatherTypes();
            sideActivityCts.Token.WaitHandle.WaitOne(CYCLE_PROFESSION_PERIOD);
            sideActivityManualReset.Wait();
        }
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("AttendedGather Thread stopped!");
    }

    private void AlternateGatherTypes()
    {
        var oldestKey = classConfig.GatherFindKeyConfig.MaxBy(x => x.SinceLastClickMs);
        if (!playerReader.IsCasting() &&
            oldestKey?.SinceLastClickMs > CYCLE_PROFESSION_PERIOD)
        {
            logger.LogInformation($"[{oldestKey.Key}] {oldestKey.Name} pressed");
            input.PressRandom(oldestKey);
            oldestKey.SetClicked();
        }
    }

    private void TickAssistReturnTimeout(bool wantNavPaused)
    {
        if (!_assistReturnActive) return;
        var now = DateTime.UtcNow;

        if (!_assistReturnTimerInit)
        {
            _assistReturnTimerInit = true;
            _assistReturnLastTickUtc = now;
            _assistReturnActiveElapsed = TimeSpan.Zero;
            return;
        }

        bool countActive = !wantNavPaused && !_pausedByLocalLogic && !bits.Combat() && !bits.Focus_Combat();
        if (countActive)
            _assistReturnActiveElapsed += now - _assistReturnLastTickUtc;

        _assistReturnLastTickUtc = now;

        if (_assistReturnActiveElapsed.TotalSeconds >= ASSIST_RETURN_TIMEOUT_ACTIVE_SEC)
            AbortAssistReturn($"timeout (active={_assistReturnActiveElapsed.TotalSeconds:0.0}s)");
    }

    private void BeginAssistReturn(Vector3 assistTargetW)
    {
        _assistReturnActive = true;
        _assistReturnTargetW = assistTargetW;
        _assistRewindActive = false;
        _assistRewindAnchorW = default;
        _assistAttempt = 0;
        _assistReturnActiveElapsed = TimeSpan.Zero;
        _assistReturnTimerInit = false;
        _assistReturnLastTickUtc = DateTime.UtcNow;
        logger.LogInformation($"[FRG] AssistReturn begin -> {assistTargetW}");
    }

    private void AbortAssistReturn(string reason)
    {
        if (!_assistReturnActive) return;
        logger.LogWarning($"[FRG] AssistReturn abort: {reason}");
        ClearAssistReturnState();
        navigation.ClearAllRoutes();
    }

    private void ClearAssistReturnState()
    {
        _assistReturnActive = false;
        _assistRewindActive = false;
        _assistWaitingForFollowing = false;
        _pausedByAssistDistance = false;
        _assistAttempt = 0;
        _assistReturnTargetW = default;
        _assistRewindAnchorW = default;
        _assistReturnActiveElapsed = TimeSpan.Zero;
        _assistReturnLastTickUtc = DateTime.UtcNow;
        _assistReturnTimerInit = false;
    }

    private void Navigation_OnPathFailed(Vector3 startW, Vector3 endW)
    {
        if (!_assistReturnActive) return;
        if (endW.WorldDistanceXYTo(_assistReturnTargetW) > 3.0f) return;

        if (_assistAttempt >= 1)
        {
            AbortAssistReturn("Path failed after rewind retry");
            return;
        }

        if (!navigation.HasLastSafeAnchor)
        {
            AbortAssistReturn("No last safe anchor for rewind");
            return;
        }

        var anchor = navigation.LastSafeAnchorW;
        if (navigation.AreaBlacklist != null && navigation.AreaBlacklist.ContainsWorld(anchor))
        {
            AbortAssistReturn("Last safe anchor inside blacklist");
            return;
        }

        _assistAttempt = 1;
        _assistRewindActive = true;
        _assistRewindAnchorW = anchor;
        logger.LogWarning($"[FRG] AssistReturn path failed. Rewind to anchor={anchor}");
        navigation.SetSingleWaypoint(anchor);
    }

    private void MountIfPossible()
    {
        float totalDistance = VectorExt.TotalDistance<Vector3>(navigation.TotalRoute, VectorExt.WorldDistanceXY);
        if (classConfig.UseMount && mountHandler.CanMount() &&
            (MountHandler.ShouldMount(totalDistance) ||
            (navigation.TotalRoute.Length > 0 && mountHandler.ShouldMount(navigation.TotalRoute[^1]))))
        {
            Log("Mount up");
            mountHandler.MountUp();
            navigation.ResetStuckParameters();
        }
    }

    #region Refill rules

    private void Navigation_OnPathCalculated()
    {
        MountIfPossible();
    }

    private void Navigation_OnDestinationReached()
    {
        if (debug) LogDebug("Navigation_OnDestinationReached");

        if (classConfig.Mode == Mode.PartyLeader && (_assistReturnActive || _assistRewindActive))
        {
            logger.LogInformation(
                $"[FRG] AssistReturn destination reached. " +
                $"AssistIsFollowing={assistStateStore.AnyAssistIsFollowing()} " +
                $"AssistCantFollow={assistStateStore.AnyAssistCantFollow()}");

            ClearAssistReturnState();

            if (assistStateStore.AnyAssistIsFollowing())
            {
                logger.LogInformation("[FRG] AssistReturn reached — assist already following, resuming patrol.");
                // Suppress the endpoint direction-forcing in the next RefillWaypoints(false)
                // call. The leader was brought here by AssistReturn, not by completing a
                // natural patrol leg, so reaching a route endpoint here must NOT reverse
                // _pathTraversalDirection.
                _suppressDirectionEndpointForcing = pathSettings.PathThereAndBack;
                navigation.ClearAllRoutes();
                Resume();
                return;
            }

            _assistWaitingForFollowing = true;
            navigation.PausePathing();
            logger.LogInformation("[FRG] AssistReturn reached — pausing until assist reports Following.");
            return;
        }

        RefillWaypoints(false);
        MountIfPossible();
    }

    private void Navigation_OnWayPointReached()
    {
        // Publish the NEW top waypoint so assist bots know the leader's next target.
        // Only during normal patrol — not during AssistReturn (GoToOneWaypoint), which
        // navigates to the assist's position and should not override the patrol waypoint.
        if (classConfig.Mode == Mode.PartyLeader && !_assistReturnActive)
        {
            Vector3 nextWp = navigation.TopWaypointW;
            if (nextWp != default)
                leaderNavProvider.SetTargetWaypoint(nextWp);
            else
                leaderNavProvider.ClearTargetWaypoint(); // all waypoints exhausted — route will wrap
        }

        MountIfPossible();
    }

    public void ClearWaypoints()
    {
        logger.LogInformation("FollowRouteGoal: ClearWaypoints!");
        navigation.SetWayPoints(stackalloc Vector3[1] { playerReader.MapPos });
    }

    public void GoToOneWaypoint(Vector3 waypointToGoTo)
    {
        logger.LogInformation($"FollowRouteGoal: GoToOneWaypoint → {waypointToGoTo}");

        if (classConfig.Mode == Mode.PartyLeader && assistStateStore.AnyAssistCantFollow())
            BeginAssistReturn(waypointToGoTo);
        else
            ClearAssistReturnState();

        ResetRefillWaypointsGuard();
        navigation.SetWayPoints(stackalloc Vector3[1] { waypointToGoTo });
    }

    public void RefillWaypoints(bool onlyClosest)
    {
        Log($"{nameof(RefillWaypoints)} - findClosest:{onlyClosest} - ThereAndBack:{pathSettings.PathThereAndBack}");

        Vector3 playerMap = playerReader.MapPos;

        Span<Vector3> pathMap = stackalloc Vector3[mapRoute.Length];
        mapRoute.CopyTo(pathMap);

        if (pathMap.Length == 0)
            return;

        bool canSkipDuplicateRefill = navigation.HasWaypoint() || navigation.HasNext();

        float mapDistanceToFirst = playerMap.MapDistanceXYTo(pathMap[0]);
        float mapDistanceToLast  = playerMap.MapDistanceXYTo(pathMap[^1]);

        int closestIndex = 0;
        Vector3 mapClosestPoint = Vector3.Zero;
        float closestDistance = float.MaxValue;

        for (int i = 0; i < pathMap.Length; i++)
        {
            Vector3 p = pathMap[i];
            float d = playerMap.MapDistanceXYTo(p);
            if (d < closestDistance)
            {
                closestDistance = d;
                closestIndex = i;
                mapClosestPoint = p;
            }
        }

        if (onlyClosest)
        {
            if (debug) LogDebug($"{nameof(RefillWaypoints)}: Closest wayPoint: {mapClosestPoint}");
            if (canSkipDuplicateRefill && IsDuplicateRecentRefill(mapClosestPoint, 1))
            {
                Log($"{nameof(RefillWaypoints)} - skipped duplicate recent closest refill");
                return;
            }
            RecordRefillWaypoints(mapClosestPoint, 1);
            navigation.SetWayPoints(stackalloc Vector3[1] { mapClosestPoint });
            PublishPatrolWaypoint();
            return;
        }

        int resumeIndex = closestIndex;
        if (resumeIndex < pathMap.Length - 1)
        {
            float dHere = playerMap.MapDistanceXYTo(pathMap[resumeIndex]);
            float dNext = playerMap.MapDistanceXYTo(pathMap[resumeIndex + 1]);
            if (dHere < 1.5f || dNext <= dHere * 1.25f)
                resumeIndex++;
        }

        var wma = playerReader.WorldMapArea;
        Vector3 playerW = playerReader.WorldPos;
        Vector3 ToWorldCoord(Vector3 mapPt) => WorldMapAreaDB.ToWorld_FlipXY(mapPt, wma);

        while (resumeIndex < pathMap.Length - 1)
        {
            if (playerW.WorldDistanceXYTo(ToWorldCoord(pathMap[resumeIndex])) < Navigation.POP_DIST)
            {
                logger.LogWarning(
                    $"[FRG] RefillWaypoints: skipping already-reached resumeIndex={resumeIndex} " +
                    $"dist={playerW.WorldDistanceXYTo(ToWorldCoord(pathMap[resumeIndex])):0.00} < POP_DIST={Navigation.POP_DIST:0.00}");
                resumeIndex++;
            }
            else
                break;
        }

        if (pathSettings.PathThereAndBack)
        {
            if (_pathTraversalDirection == 0)
                _pathTraversalDirection = mapDistanceToFirst <= mapDistanceToLast ? 1 : -1;

            // Only force direction at endpoints when the leader arrived there via normal
            // patrol completion. When AssistReturn placed the leader at the assist's
            // CantFollow position (which may happen to be a route endpoint), the original
            // direction must be preserved — the leader was not actually completing a
            // ThereAndBack leg. _suppressDirectionEndpointForcing is set by both AssistReturn
            // completion paths and consumed exactly once here.
            if (_suppressDirectionEndpointForcing)
            {
                _suppressDirectionEndpointForcing = false;
                logger.LogInformation(
                    $"[FRG] RefillWaypoints: suppressing endpoint direction forcing " +
                    $"(closestIndex={closestIndex}, direction preserved as {_pathTraversalDirection}).");
            }
            else
            {
                if (closestIndex == 0)
                    _pathTraversalDirection = 1;
                else if (closestIndex == pathMap.Length - 1)
                    _pathTraversalDirection = -1;
            }

            if (_pathTraversalDirection > 0)
            {
                Span<Vector3> forwardPoints = pathMap[resumeIndex..];
                if (forwardPoints.Length == 0) return;
                if (canSkipDuplicateRefill && IsDuplicateRecentRefill(forwardPoints[0], forwardPoints.Length))
                {
                    Log($"{nameof(RefillWaypoints)} - skipped duplicate recent forward refill");
                    return;
                }
                RecordRefillWaypoints(forwardPoints[0], forwardPoints.Length);
                Log($"{nameof(RefillWaypoints)} - Set destination from forward resume point - with {forwardPoints.Length} waypoints");
                navigation.SetWayPoints(forwardPoints);
                PublishPatrolWaypoint();
            }
            else
            {
                int backwardStartIndex = closestIndex;
                while (backwardStartIndex > 0)
                {
                    if (playerW.WorldDistanceXYTo(ToWorldCoord(pathMap[backwardStartIndex])) < Navigation.POP_DIST)
                        backwardStartIndex--;
                    else
                        break;
                }

                Span<Vector3> backwardPoints = stackalloc Vector3[backwardStartIndex + 1];
                for (int i = 0; i <= backwardStartIndex; i++)
                    backwardPoints[i] = pathMap[backwardStartIndex - i];

                if (backwardPoints.Length == 0) return;
                if (canSkipDuplicateRefill && IsDuplicateRecentRefill(backwardPoints[0], backwardPoints.Length))
                {
                    Log($"{nameof(RefillWaypoints)} - skipped duplicate recent backward refill");
                    return;
                }
                RecordRefillWaypoints(backwardPoints[0], backwardPoints.Length);
                Log($"{nameof(RefillWaypoints)} - Set destination from backward resume point - with {backwardPoints.Length} waypoints");
                navigation.SetWayPoints(backwardPoints);
                PublishPatrolWaypoint();
            }
            return;
        }

        Span<Vector3> points = pathMap[resumeIndex..];
        if (points.Length == 0) return;

        if (points.Length == 1)
        {
            float distToOnly = playerW.WorldDistanceXYTo(ToWorldCoord(points[0]));
            if (distToOnly < Navigation.POP_DIST * 2f)
            {
                Log($"{nameof(RefillWaypoints)} - last point reached, wrapping route to start");
                int wrapResumeIndex = 0;
                while (wrapResumeIndex < pathMap.Length - 1 &&
                       playerW.WorldDistanceXYTo(ToWorldCoord(pathMap[wrapResumeIndex])) < Navigation.POP_DIST)
                    wrapResumeIndex++;

                Span<Vector3> wrapPoints = pathMap[wrapResumeIndex..];
                if (wrapPoints.Length == 0) wrapPoints = pathMap;
                RecordRefillWaypoints(wrapPoints[0], wrapPoints.Length);
                Log($"{nameof(RefillWaypoints)} - Set destination from wrap-around index={wrapResumeIndex} - with {wrapPoints.Length} waypoints");
                navigation.SetWayPoints(wrapPoints);
                PublishPatrolWaypoint();
                return;
            }
        }

        if (canSkipDuplicateRefill && IsDuplicateRecentRefill(points[0], points.Length))
        {
            Log($"{nameof(RefillWaypoints)} - skipped duplicate recent forward refill");
            return;
        }

        RecordRefillWaypoints(points[0], points.Length);
        Log($"{nameof(RefillWaypoints)} - Set destination from forward resume point - with {points.Length} waypoints");
        navigation.SetWayPoints(points);
        PublishPatrolWaypoint();
    }

    #endregion

    public void ReceivePath(Vector3[] oldMap, Vector3[] newMap)
    {
        if (mapRoute.SequenceEqual(oldMap))
        {
            this.mapRoute = newMap;
            _pathTraversalDirection = 0;
        }
    }

    private void RandomJump()
    {
        if (bits.Grounded() &&
            (DateTime.UtcNow - onEnterTime).TotalSeconds > 5 &&
            classConfig.Jump.SinceLastClickMs > Random.Shared.Next(10_000, 25_000))
        {
            Log("Random jump");
            input.PressJump();
        }
    }

    private void LogDebug(string text) => logger.LogDebug(text);
    private void LogWarning(string text) => logger.LogWarning(text);
    private void Log(string text) => logger.LogInformation(text);

    /// <summary>
    /// Publishes the leader's current top patrol waypoint to
    /// <see cref="LeaderNavigationProvider"/> so assist bots can navigate to
    /// the same destination (waypoint-sharing mode).
    /// Only called during normal patrol refill — GoToOneWaypoint (AssistReturn)
    /// must NOT publish, as that navigates to the assist's CantFollow position.
    /// </summary>
    private void PublishPatrolWaypoint()
    {
        if (classConfig.Mode != Mode.PartyLeader)
            return;

        Vector3 wp = navigation.TopWaypointW;
        if (wp != default)
        {
            leaderNavProvider.SetTargetWaypoint(wp);
            logger.LogDebug($"[FRG] Published patrol waypoint -> {wp}");
        }
        else
        {
            leaderNavProvider.ClearTargetWaypoint();
        }
    }
}
