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

    private int _suppressedBlacklistedGuid;
    private DateTime _suppressedBlacklistedUntilUtc;
    private DateTime _suppressTargetFinderUntilUtc = DateTime.MinValue;

    // Distance thresholds matching FollowFocusGoal constants.
    private const float LeaderPauseYards   = FollowFocusGoal.LeaderPauseYards;   // 20y
    private const float LeaderResumeYards  = FollowFocusGoal.LeaderResumeYards;  // 15y

    // Stale logging — avoid spamming every tick
    private bool _assistWasStaleLogged;
    private DateTime _lastAssistDistanceLogUtc = DateTime.MinValue;
    private const double AssistDistanceLogIntervalSec = 5.0;

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
        AssistStateStore assistStateStore)
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
                logger.LogInformation("[FRG] Resume - preserving existing navigation progress");
                navigation.Resume();
            }
        }
        else if (classConfig.Mode == Mode.PartyLeader && assistIsFollowing)
        {
            logger.LogInformation("[FRG] Resume - AssistIsFollowing branch -> RefillWaypoints");
            ClearAssistReturnState();
            navigation.ClearAllRoutes();
            RefillWaypoints(true);
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

                // Log distance periodically for debugging
                double secSinceDistLog = (DateTime.UtcNow - _lastAssistDistanceLogUtc).TotalSeconds;
                if (secSinceDistLog > AssistDistanceLogIntervalSec)
                {
                    _lastAssistDistanceLogUtc = DateTime.UtcNow;
                    float dist = assistStateStore.GetNearestAssistDistanceYards(playerReader.WorldPos);
                    if (dist < float.MaxValue)
                        logger.LogDebug($"[FRG] Nearest assist dist={dist:0.0}y pauseThreshold={LeaderPauseYards}y resumeThreshold={LeaderResumeYards}y");
                }

                if (shouldPause && !_pausedByAssistDistance)
                {
                    float dist = assistStateStore.GetNearestAssistDistanceYards(playerReader.WorldPos);
                    AssistState? stuckAssist = assistStateStore.GetCantFollowState()
                        ?? assistStateStore.GetAll().FirstOrDefault(
                            a => !assistStateStore.IsStale(a) && a.Status == BotStatus.Stuck);

                    logger.LogInformation(
                        $"[FRG] Pausing for assist — dist={dist:0.0}y " +
                        $"status={stuckAssist?.Status.ToString() ?? "TooFar"}");

                    _pausedByAssistDistance = true;
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
        bool wantNavPaused = bits.Target() && bits.Target_Hostile()
            && bits.Target_Alive() && !bits.Target_Tagged() && playerReader.WithInCombatRange()
            && !targetBlacklist.Is();

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
        sideActivityManualReset.Reset();
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

            if (closestIndex == 0)
                _pathTraversalDirection = 1;
            else if (closestIndex == pathMap.Length - 1)
                _pathTraversalDirection = -1;

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
}
