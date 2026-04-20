using Core.AreaBlacklist;
using Core.GOAP;

using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.Extensions;

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Collections.Concurrent;

using static System.MathF;

#pragma warning disable 162

namespace Core.Goals;

public sealed partial class Navigation : IDisposable
{
    private const bool debug = false;

    private const float DIFF_THRESHOLD = 1.5f;   // within 50% difference
    private const float UNIFORM_DIST_DIV = 2;    // within 50% difference

    private readonly string patherName;

    private readonly ILogger<Navigation> logger;
    private readonly PlayerDirection playerDirection;
    private readonly ConfigurableInput input;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly StopMoving stopMoving;
    private readonly StuckDetector stuckDetector;
    private readonly IPPather pather;
    private readonly IMountHandler mountHandler;
    private readonly PathSettings pathSettings;
    private readonly DataConfig dataConfig;

    private const float MinDistanceMount = 10;
    private float MaxDistance = 200;
    private readonly float IndoorMinDistance = 1f;
    private readonly float OutDoorMinDistance = 3f;

    private float AvgDistance;
    private float lastWorldDistance = float.MaxValue;

    private const float minAngleToTurn = PI / 35f;              // 5.14 degree
    private const float minAngleToStopBeforeTurn = PI / 2f;     // 90 degree

    private readonly Stack<Vector3> wayPoints = new();
    private readonly Stack<Vector3> routeToNextWaypoint = new();

    public Vector3[] TotalRoute { private set; get; } = Array.Empty<Vector3>();

    public DateTime LastActive { get; private set; }

    public event Action? OnPathCalculated;
    public event Action? OnWayPointReached;
    public event Action? OnDestinationReached;
    public event Action? OnAnyPointReached;
    public event Action<Vector3, Vector3>? OnPathFailed; // (startW, endW)
    public bool SimplifyRouteToWaypoint { get; set; } = true;

    private bool active;
    private bool destinationReachedLatched;
    private Vector3 playerWorldPos;

    private readonly ConcurrentQueue<PathRequest> pathRequests = new();
    private readonly ConcurrentQueue<PathResult> pathResults = new();

    private readonly CancellationToken token;
    private readonly Thread pathfinderThread;
    private readonly ManualResetEventSlim manualReset;

    private int failedAttempt;
    private Vector3 lastFailedDestination;

    public IAreaBlacklist? AreaBlacklist { get; set; }

    /// <summary>How far outside a forbidden rect detour points are placed (WORLD units).</summary>
    public float DetourMargin { get; set; } = 12f;

    /// <summary>Max detours attempted for the same target waypoint.</summary>
    public int MaxDetourAttemptsPerTarget { get; set; } = 6;

    private Vector3 lastDetourTarget;
    private int detourAttemptsForTarget;
    private volatile bool waitingForPathResult;
    private int pathRequestPending;        // 0 = none, 1 = in-flight

    // Monotonic request id so we can ignore stale results
    private long nextPathRequestId;
    private long activePathRequestId;

    // Optional: keep the last request params for debugging
    private Vector3 lastRequestStart;
    private Vector3 lastRequestEnd;

    private long lastRefillLogTick;
    private static readonly long RefillLogCooldownTicks = TimeSpan.FromMilliseconds(250).Ticks;

    private Vector3 lastEscapePoint;
    private int escapeInsertCooldownTicks;
    private DateTime waitingSinceUtc;

    private float noProgressBestDist = float.MaxValue;
    private DateTime noProgressSinceUtc = DateTime.MinValue;

    // Escape no-progress fallback to pather variables
    private bool escapeActive;
    private Vector3 escapeTargetW;
    private float escapeBestDist = float.MaxValue;
    private DateTime escapeNoProgressSinceUtc;
    private DateTime noProgressRefillCooldownUntilUtc = DateTime.MinValue;

    // Hard backoff so we don't spam Refill() every tick when route is empty
    private DateTime emptyRouteRefillCooldownUntilUtc = DateTime.MinValue;
    private const int EmptyRouteRefillMinIntervalMs = 250; // tune: 150–400

    private const int StuckOwnerId = 1;

    private enum RectSide { Left, Right, Bottom, Top }
    private Vector3 noProgressTargetW;

    // Last "known-good" anchor in WORLD coords.
    // Updated when we actually reach/progress a route point or a waypoint.
    // Used by higher-level logic (FollowRouteGoal) to rewind and retry when pather returns no-path.
    private Vector3 lastSafeAnchorW;

    public bool HasLastSafeAnchor => lastSafeAnchorW != default;
    public Vector3 LastSafeAnchorW => lastSafeAnchorW;

    /// <summary>
    /// True while a pather-based approach escape is actively in progress.
    /// Goals should check this before pressing Approach/Interact to avoid
    /// interrupting Navigation while it is routing around an obstacle.
    /// </summary>
    public bool IsApproachEscapeActive => _approachEscapeActive;

    // --- Approach-vector pather escape ---
    // Goals call RecordApproachPosition() each time they press the Approach/Interact key.
    // Navigation keeps the position only when genuine forward progress has been made from it.
    // When TryUnstuck() is called, Navigation projects waypoints along the approach vector
    // starting at 10 world units (yards) and incrementing by 1 up to 30, trying to find a
    // pather path that routes around the obstacle. Falls back to stuckDetector.Update() if
    // all 21 attempts fail.
    private const float ApproachEscapeStartYards = 10f;
    private const float ApproachEscapeEndYards = 30f;
    private const float ApproachEscapeProgressMinW = 0.75f; // min movement to keep a recorded position
    private Vector3 _approachRecordedW;       // last approach position where progress was confirmed
    private Vector3 _approachPrevRecordedW;   // position before that, for direction vector
    private bool _approachEscapeActive;
    private float _approachEscapeCurrentYards;
    private DateTime _approachEscapeLastAttemptUtc = DateTime.MinValue;

    // Backoff to prevent request spam on repeated blacklist rejections
    private DateTime blacklistRejectCooldownUntilUtc = DateTime.MinValue;
    private DateTime noPathCooldownUntilUtc = DateTime.MinValue;
    private Vector3 lastRejectStartW;
    private Vector3 lastRejectEndW;
    private int sameRejectCount;
    private const int MaxSameRejectBeforeFallback = 4;

    private long _navDebugNextTick;
    private const int NavDebugEveryMs = 250;

    private long waitingRequestId;

    // Movement can stop at STOP_DIST, but we only advance (pop) at POP_DIST.
    // IMPORTANT: POP_DIST MUST BE >= STOP_DIST to avoid deadlocks.
    private const float STOP_DIST = 3.0f;              // when we stop applying movement input
    public const float POP_DIST = 3.6f;              // when we pop route node / waypoint (>= STOP_DIST)
    private const float STOP_DIST_SQ = STOP_DIST * STOP_DIST;
    private const float POP_DIST_SQ = POP_DIST * POP_DIST;

    // Chase-progress watchdog with movement hysteresis
    private Vector3 _chaseProgTarget;
    private float _chaseProgBestDist = float.MaxValue;
    private DateTime _chaseProgSinceUtc = DateTime.MinValue;

    private Vector3 _chaseLastPos;
    private DateTime _chaseLastMovedUtc = DateTime.MinValue;
    private DateTime _chaseUnstuckCooldownUntilUtc = DateTime.MinValue;

    // True while we're actively following an escape route that does NOT target the waypoint stack.
    private bool escapeRouteInProgress;
    private Vector3 escapeRouteEndW;

    private Vector3 lastNoPathStartW;
    private Vector3 lastNoPathEndW;
    private int sameNoPathCount;
    private const int MaxSameNoPathBeforeFallback = 3;

    public Navigation(ILogger<Navigation> logger,
        CancellationTokenSource<GoapAgent> cts,
        PlayerDirection playerDirection,
        ConfigurableInput input,
        PlayerReader playerReader, AddonBits bits,
        StopMoving stopMoving,
        StuckDetector stuckDetector, IPPather pather, IMountHandler mountHandler,
        ClassConfiguration classConfiguration,
        PathSettings pathSettings, DataConfig dataConfig)
    {
        this.logger = logger;
        this.playerDirection = playerDirection;
        this.input = input;
        this.playerReader = playerReader;
        this.bits = bits;
        this.stopMoving = stopMoving;
        this.stuckDetector = stuckDetector;
        this.pather = pather;
        this.mountHandler = mountHandler;
        this.pathSettings = pathSettings;
        this.dataConfig = dataConfig;

        patherName = pather.GetType().Name;

        AvgDistance = OutDoorMinDistance;
        token = cts.Token;
        manualReset = new(false);
        pathfinderThread = new(PathFinderThread);
        pathfinderThread.Start();

        switch (classConfiguration.Mode)
        {
            case Mode.AttendedGather:
                MaxDistance = OutDoorMinDistance;
                SimplifyRouteToWaypoint = false;
                break;
        }

        // Fix #10: removed duplicate assignments of pathSettings and dataConfig that were here

        // Apply per-route area blacklists (map rects -> world rects)
        if (pathSettings.MapBlacklistRects is { Length: > 0 })
        {
            logger.LogInformation("[NAV]: pathSettings.MapBlacklistRects.Length: " + pathSettings.MapBlacklistRects.Length);

            AreaBlacklist = BlacklistConversion.BuildWorldBlacklistFromMapRects(
                pathSettings.MapBlacklistRects,
                playerReader.WorldMapArea
            );

            DetourMargin = 12f;
            MaxDetourAttemptsPerTarget = 6;
        }
        else
        {
            logger.LogInformation("[NAV]: pathSettings.MapBlacklistRects.Length: " + pathSettings.MapBlacklistRects.Length);
            AreaBlacklist = null;
        }
    }

    private void SyncRouteStateToTop(bool resetIfEmpty = true)
    {
        UpdateTotalRoute();

        if (routeToNextWaypoint.Count > 0)
        {
            Vector3 top = Nav2D(routeToNextWaypoint.Peek());

            stuckDetector.SetTargetLocation(StuckOwnerId, top);
            ResetNoProgressWatchdog(top);
            ResetChaseProgressWatchdog(top);

            // If we now have something to chase, allow immediate processing.
            emptyRouteRefillCooldownUntilUtc = DateTime.MinValue;
            return;
        }

        if (resetIfEmpty)
        {
            stuckDetector.Reset(StuckOwnerId);
            ResetNoProgressWatchdog();
            ResetChaseProgressWatchdog();

            emptyRouteRefillCooldownUntilUtc = DateTime.MinValue;
        }
    }

    private bool TryConsumeReachedWaypoint(in Vector3 playerPos)
    {
        if (wayPoints.Count == 0)
            return false;

        Vector3 wpTop = Nav2D(wayPoints.Peek());
        float wpReach = ReachedDistance(OutDoorMinDistance) + 0.35f;

        if (playerPos.WorldDistanceXYTo(wpTop) > wpReach)
            return false;

        Vector3 completed = Nav2D(wayPoints.Pop());
        SetLastSafeAnchor(playerPos);

        logger.LogWarning(
            $"[NAV] POP WAYPOINT (XY reached) completed={completed} " +
            $"newWpTop={(wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()).ToString() : "<none>")}");

        routeToNextWaypoint.Clear();
        SyncRouteStateToTop();

        OnWayPointReached?.Invoke();

        if (wayPoints.Count == 0)
        {
            CompleteDestinationReached();
        }

        return true;
    }

    private bool TryRefillRouteNow(CancellationToken token, DateTime now)
    {
        if (waitingForPathResult)
            return false;

        if (now < emptyRouteRefillCooldownUntilUtc)
            return false;

        if (now < noProgressRefillCooldownUntilUtc)
        {
            emptyRouteRefillCooldownUntilUtc = now.AddMilliseconds(EmptyRouteRefillMinIntervalMs);
            return false;
        }

        emptyRouteRefillCooldownUntilUtc = now.AddMilliseconds(EmptyRouteRefillMinIntervalMs);

        logger.LogWarning(
            $"[NAV-EMPTY] route=0 wp={wayPoints.Count} " +
            $"waiting={waitingForPathResult} pending={Volatile.Read(ref pathRequestPending)} " +
            $"reqQ={pathRequests.Count} resQ={pathResults.Count} " +
            $"noPathCdMs={(noPathCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"blCdMs={(blacklistRejectCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"npCdMs={(noProgressRefillCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"pos={Nav2D(playerReader.WorldPos)} " +
            $"wpTop={(wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()).ToString() : "<none>")}");

        RefillRouteToNextWaypoint(token);
        return routeToNextWaypoint.Count > 0;
    }

    private void CompleteDestinationReached(bool fireWaypointReached = false)
    {
        if (destinationReachedLatched)
            return;

        destinationReachedLatched = true;

        escapeRouteInProgress = false;
        escapeActive = false;

        StopAndResetAtDestination();

        if (fireWaypointReached)
            OnWayPointReached?.Invoke();

        OnDestinationReached?.Invoke();
    }

    private void StopAndResetAtDestination()
    {
        stopMoving.Stop();
        input.StopForward(true);

        // If this was the completion of a pather-based approach escape, clear
        // the escape state so goals resume normal approach on the next tick.
        if (_approachEscapeActive)
        {
            logger.LogInformation("[NAV] ApproachEscape: escape navigation complete — resuming normal approach.");
            _approachEscapeActive = false;
        }

        ResetStuckParameters();
        ResetNoProgressWatchdog();
        ResetChaseProgressWatchdog();
    }

    private void ClearDestinationLatch()
    {
        destinationReachedLatched = false;
    }

    private bool IsAtFinalWaypoint(in Vector3 playerPos, out Vector3 finalWp)
    {
        finalWp = default;

        if (wayPoints.Count != 1)
            return false;

        finalWp = Nav2D(wayPoints.Peek());

        float reach = ReachedDistance(OutDoorMinDistance) + 0.35f;
        return playerPos.WorldDistanceXYTo(finalWp) <= reach;
    }

    private void CollapseTinyRouteSteps(in Vector3 playerPos, float minStepDist = 1.25f)
    {
        while (routeToNextWaypoint.Count > 0)
        {
            Vector3 top = Nav2D(routeToNextWaypoint.Peek());

            // Drop nodes that are too close to the player
            if (playerPos.WorldDistanceXYTo(top) <= minStepDist)
            {
                routeToNextWaypoint.Pop();
                continue;
            }

            // Drop nodes that are too close to the next node behind them
            if (routeToNextWaypoint.Count >= 2)
            {
                var arr = routeToNextWaypoint.ToArray(); // top-first
                Vector3 a = Nav2D(arr[0]);
                Vector3 b = Nav2D(arr[1]);

                if (a.WorldDistanceXYTo(b) <= minStepDist)
                {
                    routeToNextWaypoint.Pop();
                    continue;
                }
            }

            break;
        }
    }

    private static Vector3 Nav2D(Vector3 w) => new Vector3(w.X, w.Y, 0f);

    private static float DistSq2D(in Vector3 a, in Vector3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    // Helper: should we apply movement input toward target?
    private static bool ShouldMoveToward(in Vector3 playerPos, in Vector3 targetPos)
    {
        float dSq = DistSq2D(playerPos, targetPos);
        return dSq > STOP_DIST_SQ;
    }

    private bool NavDebugDue()
    {
        long now = Environment.TickCount64;
        if (now < _navDebugNextTick) return false;
        _navDebugNextTick = now + NavDebugEveryMs;
        return true;
    }

    private void RefillExit(string reason)
    {
        logger.LogWarning(
            $"[NAV-REFILL-EXIT] {reason} " +
            $"active={active} escapeActive={escapeActive} insideBL={(AreaBlacklist?.ContainsWorld(Nav2D(playerReader.WorldPos)) == true)} " +
            $"wp={wayPoints.Count} route={routeToNextWaypoint.Count} " +
            $"waiting={waitingForPathResult} pending={Volatile.Read(ref pathRequestPending)} reqQ={pathRequests.Count} resQ={pathResults.Count} " +
            $"noPathCdMs={(noPathCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"blCdMs={(blacklistRejectCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"npCdMs={(noProgressRefillCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"emptyCdMs={(emptyRouteRefillCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"pos={Nav2D(playerReader.WorldPos)}");
    }

    private void RefillEnd(string phase, long startTick)
    {
        long now = Environment.TickCount64;
        logger.LogWarning(
            $"[NAV-REFILL-END] phase={phase} elapsedMs={(now - startTick)} " +
            $"uiMapId={(playerReader.UIMapId.Value > 0 ? playerReader.UIMapId.Value.ToString() : "<null>")} " +
            $"worldMapArea={playerReader.WorldMapArea} " +
            $"active={active} escapeActive={escapeActive} insideBL={(AreaBlacklist?.ContainsWorld(Nav2D(playerReader.WorldPos)) == true)} " +
            $"wp={wayPoints.Count} route={routeToNextWaypoint.Count} " +
            $"waiting={waitingForPathResult} pending={Volatile.Read(ref pathRequestPending)} reqQ={pathRequests.Count} resQ={pathResults.Count} " +
            $"noPathCdMs={(noPathCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"blCdMs={(blacklistRejectCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"npCdMs={(noProgressRefillCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"pos={Nav2D(playerReader.WorldPos)} wpTop={(wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()).ToString() : "<none>")}");
    }

    private void NavDbg(string msg)
    {
        if (NavDebugDue())
        {
            logger.LogWarning("[NAV-DBG] " + msg);
        }
    }

    private bool RouteTargetsWaypointTop(float eps = 1.5f)
    {
        if (routeToNextWaypoint.Count == 0 || wayPoints.Count == 0)
            return true;

        Vector3 wpTop = wayPoints.Peek();

        // Stack.ToArray() returns top-first. Bottom (final route destination) is last.
        var arr = routeToNextWaypoint.ToArray();
        Vector3 routeEnd = arr[arr.Length - 1];

        return routeEnd.WorldDistanceXYTo(wpTop) <= eps;
    }

    private static float MinDistToAny(Vector3 p, IEnumerable<Vector3> pts)
    {
        float best = float.MaxValue;
        foreach (var v in pts)
        {
            float d = p.WorldDistanceXYTo(v);
            if (d < best) best = d;
        }
        return best == float.MaxValue ? -1 : best;
    }

    public void Dispose()
    {
        stuckDetector.Release(StuckOwnerId);
        manualReset.Set();
    }

    public void Update()
    {
        Update(token);
    }

    public void Update(CancellationToken token)
    {
        if (NavDebugDue())
        {
            logger.LogInformation($"[NAV-SANITY] Update entered navHash={GetHashCode()} tokenCancelled={token.IsCancellationRequested}");
        }

        if (!active)
            return;

        NavDbg($"STATE enter active={active} " +
               $"wp={wayPoints.Count} route={routeToNextWaypoint.Count} " +
               $"waiting={waitingForPathResult} pending={Volatile.Read(ref pathRequestPending)} " +
               $"waitingReq={Volatile.Read(ref waitingRequestId)} activeReq={Volatile.Read(ref activePathRequestId)} " +
               $"noPathCd={(noPathCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0}ms " +
               $"blCd={(blacklistRejectCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0}ms " +
               $"npCd={(noProgressRefillCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0}ms");

        // Fix #8: Navigation only rebuilds blacklist from pathSettings when FRG has not set a custom one.
        // FRG sets AreaBlacklist directly via the property in Resume(), which takes precedence.
        // We only auto-build here if AreaBlacklist is null AND pathSettings has rects (covers the case
        // where Navigation is used without FRG, e.g. tests or other goals).
        if (AreaBlacklist == null && pathSettings.MapBlacklistRects is { Length: > 0 })
        {
            AreaBlacklist = BlacklistConversion.BuildWorldBlacklistFromMapRects(
                pathSettings.MapBlacklistRects,
                playerReader.WorldMapArea
            );

            DetourMargin = 12f;
            MaxDetourAttemptsPerTarget = 6;
        }
        // NOTE: We intentionally do NOT null AreaBlacklist when rects are empty here,
        // because FRG may have set it to null itself already in Resume(). Nulling it here
        // would be redundant and could race with a FRG Resume() that just set a custom instance.

        if (escapeInsertCooldownTicks > 0)
            escapeInsertCooldownTicks--;

        if (wayPoints.Count == 0 && routeToNextWaypoint.Count == 0)
        {
            logger.LogInformation(
                $"[NAV-SANITY] EXIT noWork inside={AreaBlacklist?.ContainsWorld(Nav2D(playerReader.WorldPos)) == true}");

            if (!destinationReachedLatched)
            {
                destinationReachedLatched = true;
                StopAndResetAtDestination();
                OnDestinationReached?.Invoke();
                NavDbg("RETURN noWork (wp=0 route=0) -> OnDestinationReached");
            }

            if (wayPoints.Count == 0 && routeToNextWaypoint.Count == 0)
                return;

            // NOTE: Do NOT call ClearDestinationLatch() here.
            // At this point SetWayPoints was just called by OnDestinationReached's callback,
            // but those waypoints may be immediately popped by wpAlreadyReached_pop before
            // any real route is built. Clearing the latch here would allow noWork to fire
            // OnDestinationReached again on the very next tick, creating a tight loop.
            // The latch is cleared further below, only after TryRefillRouteNow has
            // successfully produced a non-empty route that actually moves the player.
        }

        SkipBlacklistedWaypoints();

        if (wayPoints.Count == 0 && routeToNextWaypoint.Count == 0)
        {
            if (!destinationReachedLatched)
            {
                destinationReachedLatched = true;

                StopAndResetAtDestination();

                OnDestinationReached?.Invoke();
                NavDbg("RETURN afterSkipBlacklistedWaypoints noWork (wp=0 route=0)");
            }

            return;
        }

        while (pathResults.TryDequeue(out PathResult result))
        {
            result.Callback(result);
        }

        if (waitingForPathResult && (DateTime.UtcNow - waitingSinceUtc).TotalSeconds > 3)
        {
            logger.LogWarning("[NAV] Path wait timeout; resetting pending/waiting gates.");
            waitingForPathResult = false;
            Interlocked.Exchange(ref pathRequestPending, 0);
            Volatile.Write(ref waitingRequestId, 0);
        }

        int pending = Volatile.Read(ref pathRequestPending);

        if (pending == 1 &&
            !waitingForPathResult &&
            pathRequests.Count == 0 &&
            pathResults.Count == 0)
        {
            logger.LogWarning("[NAV] pathRequestPending=1 but no queued/in-flight work. Clearing pending gate.");
            Interlocked.Exchange(ref pathRequestPending, 0);
        }

        if (waitingForPathResult && pending == 0 && pathRequests.Count == 0)
        {
            logger.LogWarning("[NAV] waitingForPathResult was TRUE but no request is pending/queued. Resetting.");
            waitingForPathResult = false;
        }

        if (token.IsCancellationRequested)
            return;

        Vector3 playerPos = Nav2D(playerReader.WorldPos);
        playerWorldPos = playerPos;
        DateTime now = DateTime.UtcNow;

        // Clear stale routes only when this is a normal route, not an escape route.
        if (routeToNextWaypoint.Count > 0 &&
            wayPoints.Count > 0 &&
            !escapeActive &&
            !escapeRouteInProgress &&
            AreaBlacklist?.ContainsWorld(playerPos) != true &&
            !waitingForPathResult &&
            Volatile.Read(ref pathRequestPending) == 0 &&
            !RouteTargetsWaypointTop())
        {
            var arr = routeToNextWaypoint.ToArray();
            var routeEnd = arr[arr.Length - 1];

            logger.LogWarning(
                $"[NAV] Stale route detected; clearing. " +
                $"routeTop={routeToNextWaypoint.Peek()} routeEnd={routeEnd} wpTop={wayPoints.Peek()}");

            routeToNextWaypoint.Clear();
            SyncRouteStateToTop();
        }

        // If route is empty, first see if we already reached current waypoint.
        if (routeToNextWaypoint.Count == 0)
        {
            if (IsAtFinalWaypoint(playerPos, out _))
            {
                SetLastSafeAnchor(playerPos);
                wayPoints.Pop();
                routeToNextWaypoint.Clear();
                SyncRouteStateToTop();

                CompleteDestinationReached(fireWaypointReached: true);
                return;
            }

            if (TryConsumeReachedWaypoint(playerPos))
            {
                if (wayPoints.Count == 0)
                    return;
            }

            if (routeToNextWaypoint.Count == 0)
            {
                TryRefillRouteNow(token, now);

                if (routeToNextWaypoint.Count == 0)
                    return;

                // Refill produced a real route — safe to clear the latch now.
                // This is the earliest point where we know nav has actual work ahead.
                ClearDestinationLatch();
            }
        }

        // If we are inside a blacklist and the current next-step is blacklisted, clear and try refill next tick.
        if (AreaBlacklist != null &&
            AreaBlacklist.TryGetContainingRect(playerPos, out _) &&
            routeToNextWaypoint.Count > 0 &&
            IsBlacklistedPoint(routeToNextWaypoint.Peek()))
        {
            routeToNextWaypoint.Clear();
            SyncRouteStateToTop();
            return;
        }

        // Consume route nodes that are already effectively reached.
        bool poppedAny = false;
        while (routeToNextWaypoint.Count > 0)
        {
            CollapseTinyRouteSteps(playerPos);

            if (routeToNextWaypoint.Count == 0)
                break;

            if (!TryPopRouteTopIfReached(playerPos))
                break;

            poppedAny = true;
            SetLastSafeAnchor(playerPos);

            if (escapeRouteInProgress && routeToNextWaypoint.Count == 0)
            {
                escapeRouteInProgress = false;
                escapeActive = false;
                escapeBestDist = float.MaxValue;
                escapeNoProgressSinceUtc = DateTime.UtcNow;
                noProgressRefillCooldownUntilUtc = DateTime.MinValue;
            }

            SyncRouteStateToTop(routeToNextWaypoint.Count > 0 || wayPoints.Count > 0);
        }

        // Route may have emptied after pop; consume waypoint and/or refill immediately in same Update.
        if (routeToNextWaypoint.Count == 0)
        {
            if (IsAtFinalWaypoint(playerPos, out _))
            {
                SetLastSafeAnchor(playerPos);
                wayPoints.Pop();
                routeToNextWaypoint.Clear();
                SyncRouteStateToTop();

                CompleteDestinationReached(fireWaypointReached: true);
                return;
            }

            if (TryConsumeReachedWaypoint(playerPos))
            {
                if (wayPoints.Count == 0)
                    return;
            }

            if (routeToNextWaypoint.Count == 0)
            {
                TryRefillRouteNow(token, now);

                if (routeToNextWaypoint.Count == 0)
                    return;

                // Refill produced a real route — safe to clear the latch now.
                ClearDestinationLatch();
            }
        }

        // IMPORTANT: only compute target after all possible route mutations above.
        Vector3 targetW = Nav2D(routeToNextWaypoint.Peek());

        if (Math.Abs(routeToNextWaypoint.Peek().Z) > 0.001f)
            logger.LogWarning($"[NAV] Z LEAK routeTop={routeToNextWaypoint.Peek()}");

        NavDbg($"MOVE target(routeTop)={targetW} player={playerReader.WorldPos} d={playerReader.WorldPos.WorldDistanceXYTo(targetW):0.00}");

        if (NavDebugDue())
        {
            Vector3 wpTop = wayPoints.Count > 0 ? wayPoints.Peek() : default;
            float distToWp = wayPoints.Count > 0 ? playerPos.WorldDistanceXYTo(wpTop) : -1;
            float distToRoute = playerPos.WorldDistanceXYTo(targetW);
            float dAnyWp = MinDistToAny(playerPos, wayPoints);

            logger.LogWarning(
                $"[NAV-DBG] dAnyWp={dAnyWp:0.00} player={playerPos} routeTop={targetW} dRoute={distToRoute:0.00} " +
                $"wpTop={wpTop} dWp={distToWp:0.00} routeCount={routeToNextWaypoint.Count} wpCount={wayPoints.Count}");
        }

        // Escape fallback watchdog
        if (escapeActive && routeToNextWaypoint.Count > 0)
        {
            if (AreaBlacklist?.ContainsWorld(playerPos) != true)
            {
                float edgeBuffer = DetourMargin + 6f;

                if (!escapeRouteInProgress && !IsNearBlacklistEdge(playerPos, edgeBuffer))
                {
                    escapeActive = false;
                }
            }
            else
            {
                float d = playerPos.WorldDistanceXYTo(targetW);

                if (d + 0.05f < escapeBestDist)
                {
                    escapeBestDist = d;
                    escapeNoProgressSinceUtc = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - escapeNoProgressSinceUtc).TotalSeconds > 1.0 &&
                         !escapeRouteInProgress &&
                         !waitingForPathResult &&
                         Volatile.Read(ref pathRequestPending) == 0)
                {
                    logger.LogWarning($"[BL] Escape direct blocked; pathing to escape target {escapeTargetW}");

                    stopMoving.Stop();

                    EnqueuePathRequest(
                        playerReader.UIMapId.Value,
                        playerPos,
                        Nav2D(escapeTargetW),
                        d
                    );

                    escapeRouteInProgress = true;
                    escapeRouteEndW = Nav2D(escapeTargetW);
                    return;
                }
            }
        }

        // Stop only at true final destination, not intermediate nodes.
        if (!ShouldMoveToward(playerPos, targetW))
        {
            if (IsAtFinalWaypoint(playerPos, out _))
            {
                SetLastSafeAnchor(playerPos);
                wayPoints.Pop();
                routeToNextWaypoint.Clear();
                SyncRouteStateToTop();

                CompleteDestinationReached(fireWaypointReached: true);
                return;
            }

            stopMoving.Stop();
            input.StopForward(true);
            return;
        }

        // Movement this tick
        LastActive = DateTime.UtcNow;
        input.StartForward(true);

        Vector3 playerM = WorldMapAreaDB.ToMap_FlipXY(playerPos, playerReader.WorldMapArea);
        Vector3 targetM = WorldMapAreaDB.ToMap_FlipXY(targetW, playerReader.WorldMapArea);
        float heading = DirectionCalculator.CalculateMapHeading(playerM, targetM);

        stuckDetector.SetTargetLocation(StuckOwnerId, targetW);

        // Chase watchdog
        float dChase = playerPos.WorldDistanceXYTo(targetW);

        if (_chaseProgSinceUtc == DateTime.MinValue || _chaseProgTarget.WorldDistanceXYTo(targetW) > 0.05f)
        {
            _chaseProgTarget = targetW;
            _chaseProgBestDist = dChase;
            _chaseProgSinceUtc = now;

            _chaseLastPos = playerPos;
            _chaseLastMovedUtc = now;
        }
        else
        {
            float moved = playerPos.WorldDistanceXYTo(_chaseLastPos);
            if (moved > 0.75f)
            {
                _chaseLastPos = playerPos;
                _chaseLastMovedUtc = now;
            }

            if (dChase + 0.10f < _chaseProgBestDist)
            {
                _chaseProgBestDist = dChase;
                _chaseProgSinceUtc = now;
            }

            double sinceBestSec = (now - _chaseProgSinceUtc).TotalSeconds;
            double sinceMoveSec = (now - _chaseLastMovedUtc).TotalSeconds;

            if (sinceBestSec > 4.0 &&
                sinceMoveSec > 1.5 &&
                !waitingForPathResult &&
                Volatile.Read(ref pathRequestPending) == 0)
            {
                logger.LogWarning(
                    $"[NAV] No chase progress while stationary. " +
                    $"player={playerPos} chase={targetW} dChase={dChase:0.00} best={_chaseProgBestDist:0.00} " +
                    $"sinceBest={sinceBestSec:0.00}s sinceMove={sinceMoveSec:0.00}s");

                if (now >= _chaseUnstuckCooldownUntilUtc)
                {
                    stopMoving.Stop();
                    stuckDetector.SetTargetLocation(StuckOwnerId, targetW);
                    stuckDetector.Update(StuckOwnerId, token);

                    _chaseUnstuckCooldownUntilUtc = now.AddMilliseconds(1400);
                    _chaseLastPos = playerPos;
                    _chaseLastMovedUtc = now;
                    return;
                }

                if (sinceBestSec > 6.0 && now >= noProgressRefillCooldownUntilUtc)
                {
                    noProgressRefillCooldownUntilUtc = now.AddMilliseconds(1500);

                    stopMoving.Stop();
                    routeToNextWaypoint.Clear();
                    SyncRouteStateToTop();
                    return;
                }
            }
        }

        if (stuckDetector.IsGettingCloser(StuckOwnerId))
        {
            AdjustHeading(heading, token);
        }
        else
        {
            if (stuckDetector.ActionDurationMs > 10_000)
            {
                if (mountHandler.IsMounted())
                    mountHandler.Dismount();

                LogClearRouteToWaypointStuck(logger, stuckDetector.ActionDurationMs);
                routeToNextWaypoint.Clear();
                SyncRouteStateToTop();
                return;
            }

            if (HasBeenActiveRecently())
            {
                stuckDetector.Update(StuckOwnerId, token);
            }
        }

        lastWorldDistance = playerPos.WorldDistanceXYTo(targetW);
    }

    public void Resume()
    {
        active = true;
        ClearDestinationLatch();
        SetLastSafeAnchor(playerReader.WorldPos);
        stuckDetector.Acquire(StuckOwnerId);
        ResetStuckParameters();
        ResetChaseProgressWatchdog();

        if (pather.GetType() != typeof(RemotePathingAPIV3) && routeToNextWaypoint.Count > 0)
        {
            V1_AttemptToKeepRouteToWaypoint();
        }

        int removed = 0;
        while (AdjustNextWaypointPointToClosest() && removed < 5) { removed++; };
        if (removed > 0)
        {
            UpdateTotalRoute();

            if (debug)
                LogDebug($"Resume: removed {removed} waypoint!");
        }
    }

    private void SetLastSafeAnchor(Vector3 w)
    {
        if (w == default)
            return;

        if (lastSafeAnchorW.WorldDistanceXYTo(w) < 0.05f)
            return;

        lastSafeAnchorW = w;
    }

    public void ClearAllRoutes()
    {
        routeToNextWaypoint.Clear();
        ClearDestinationLatch();
        escapeRouteInProgress = false;
        escapeActive = false;
        SyncRouteStateToTop();
    }

    public void SetSingleWaypoint(Vector3 worldOrMapPoint)
    {
        Span<Vector3> tmp = stackalloc Vector3[1] { worldOrMapPoint };
        SetWayPoints(tmp);
    }

    public void PausePathing()
    {
        active = false;

        waitingForPathResult = false;
        Interlocked.Exchange(ref pathRequestPending, 0);
        Volatile.Write(ref waitingRequestId, 0);

        stuckDetector.Release(StuckOwnerId);
    }

    public void Stop()
    {
        active = false;
        ClearDestinationLatch();
        stuckDetector.Release(StuckOwnerId);

        Volatile.Write(ref activePathRequestId, Interlocked.Increment(ref nextPathRequestId));
        waitingForPathResult = false;
        Interlocked.Exchange(ref pathRequestPending, 0);
        Volatile.Write(ref waitingRequestId, 0);

        routeToNextWaypoint.Clear();
        escapeRouteInProgress = false;
        escapeActive = false;

        ResetStuckParameters();
        ResetNoProgressWatchdog();
        ResetChaseProgressWatchdog();
        UpdateTotalRoute();
    }

    public void StopMovement()
    {
        input.StopForward(true);
    }

    public bool HasWaypoint()
    {
        return wayPoints.Count != 0;
    }

    public bool HasNext()
    {
        return routeToNextWaypoint.Count != 0;
    }

    // Diagnostic properties for logging - not for control flow
    public bool Active => active;
    public int WaypointCount => wayPoints.Count;
    public int RouteCount => routeToNextWaypoint.Count;

    public Vector3 NextMapPoint()
    {
        return WorldMapAreaDB.ToMap_FlipXY(Nav2D(routeToNextWaypoint.Peek()), playerReader.WorldMapArea);
    }

    public void SetWayPoints(Span<Vector3> points)
    {
        active = true;
        // NOTE: Do NOT call ClearDestinationLatch() here.
        //
        // Clearing the latch on every SetWayPoints call causes an infinite loop when
        // the player is at or past the last waypoint on the route:
        //   noWork (latch clear) -> OnDestinationReached -> RefillWaypoints
        //   -> SetWayPoints -> ClearDestinationLatch -> wpAlreadyReached_pop
        //   -> wp=0, route=0 -> noWork (latch clear again) -> repeat forever.
        //
        // The latch is cleared by Resume(), Stop(), and ClearAllRoutes() which are the
        // genuine "start fresh" entry points. SetWayPoints is feeding new work to an
        // already-active session and must not reset the latch.
        // The latch will be cleared by RefillRouteToNextWaypoint once it successfully
        // builds a real route to a point that is actually ahead of the player.
        SetLastSafeAnchor(playerReader.WorldPos);
        wayPoints.Clear();
        routeToNextWaypoint.Clear();

        float mapDistanceXY = 0;
        WorldMapArea wma = playerReader.WorldMapArea;
        for (int i = points.Length - 1; i >= 0; i--)
        {
            Vector3 point = points[i];
            if (IsMapPoint(point))
            {
                point = WorldMapAreaDB.ToWorld_FlipXY(point, wma);
            }

            if (i != points.Length - 1)
            {
                Vector3 prev = wayPoints.Peek();
                mapDistanceXY += point.WorldDistanceXYTo(prev);
            }

            wayPoints.Push(Nav2D(point));
        }

        AvgDistance = wayPoints.Count > 1 ? Max(mapDistanceXY / wayPoints.Count, OutDoorMinDistance) : OutDoorMinDistance;

        escapeRouteInProgress = false;
        escapeActive = false;
        SyncRouteStateToTop();

        static bool IsMapPoint(Vector3 p)
        {
            return
                p.X is >= 0 and <= 100 &&
                p.Y is >= 0 and <= 100;
        }
    }

    public void ResetStuckParameters()
    {
        stuckDetector.Reset(StuckOwnerId);
    }

    /// <summary>
    /// Called by goals each time they press the Approach/Interact key.
    /// Navigation keeps the position only when genuine forward progress has been
    /// made since the last recorded position — this gives a reliable approach
    /// direction vector for pather-based stuck escape.
    /// </summary>
    public void RecordApproachPosition(Vector3 worldPos)
    {
        if (_approachRecordedW == default)
        {
            _approachRecordedW = worldPos;
            return;
        }

        float moved = worldPos.WorldDistanceXYTo(_approachRecordedW);
        if (moved >= ApproachEscapeProgressMinW)
        {
            _approachPrevRecordedW = _approachRecordedW;
            _approachRecordedW = worldPos;
        }
    }

    /// <summary>
    /// Called by goals when their own stuck detection fires (e.g. !stuckDetector.IsMoving()).
    /// Attempts to find a pather path by projecting a waypoint along the approach vector,
    /// starting at 10 yards and incrementing by 1 up to 30 yards (21 attempts).
    /// Each call advances one step — returns true while an escape attempt is in progress
    /// so the goal knows to skip its own unstuck handling.
    /// Falls back to stuckDetector.Update() once all attempts are exhausted.
    /// </summary>
    public bool TryUnstuck(CancellationToken token = default)
    {
        // If escape is already active, Navigation is executing the route.
        // StopAndResetAtDestination() will clear _approachEscapeActive when the
        // route completes. If active is false, navigation was stopped externally.
        if (_approachEscapeActive)
        {
            if (!active)
            {
                // Navigation stopped externally (e.g. Stop() called) — clear escape.
                logger.LogInformation("[NAV] ApproachEscape: navigation stopped externally — clearing escape.");
                ResetApproachEscape();
                return false;
            }
            return true;
        }

        var now = DateTime.UtcNow;

        // Throttle to one new attempt per 200ms so the pather has time to respond.
        if ((now - _approachEscapeLastAttemptUtc).TotalMilliseconds < 200)
            return false;

        _approachEscapeLastAttemptUtc = now;

        _approachEscapeCurrentYards += 1f;

        if (_approachEscapeCurrentYards > ApproachEscapeEndYards)
        {
            // All pather attempts exhausted — fall back to stuckDetector.Update().
            logger.LogWarning(
                "[NAV] ApproachEscape: all pather attempts exhausted — falling back to random unstuck.");
            ResetApproachEscape();
            stuckDetector.SetTargetLocation(StuckOwnerId, _approachRecordedW == default
                ? Nav2D(playerReader.WorldPos)
                : _approachRecordedW);
            stuckDetector.Update(StuckOwnerId, token);
            return false;
        }

        Vector3 projection = ProjectApproachEscapeTarget(_approachEscapeCurrentYards);
        logger.LogInformation(
            $"[NAV] ApproachEscape: attempt {_approachEscapeCurrentYards:0}y -> {projection}");

        // SetSingleWaypoint sets active=true and enqueues the pather request.
        // On the next TryUnstuck() call active will still be true, keeping the goal blocked
        // until Navigation actually finishes following the escape route.
        SetSingleWaypoint(projection);
        _approachEscapeActive = true;
        return true;
    }

    /// <summary>
    /// Resets approach escape state. Call when the goal exits or target changes.
    /// </summary>
    public void ResetApproachEscape()
    {
        _approachEscapeActive = false;
        // Pre-incremented in TryUnstuck before use, so reset to Start-1
        // so the first attempt fires at exactly ApproachEscapeStartYards.
        _approachEscapeCurrentYards = ApproachEscapeStartYards - 1f;
        _approachRecordedW = default;
        _approachPrevRecordedW = default;
        _approachEscapeLastAttemptUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Projects a world-space waypoint along the approach vector by the given number
    /// of world units (yards) beyond the current player position.
    /// Uses _approachPrevRecordedW → _approachRecordedW as the direction vector.
    /// Falls back to current facing direction if the approach vector is degenerate.
    /// </summary>
    private Vector3 ProjectApproachEscapeTarget(float yards)
    {
        Vector3 currentW = Nav2D(playerReader.WorldPos);

        float dx, dy;

        if (_approachPrevRecordedW != default && _approachRecordedW != default)
        {
            // Two recorded approach positions — use their direction vector.
            dx = _approachRecordedW.X - _approachPrevRecordedW.X;
            dy = _approachRecordedW.Y - _approachPrevRecordedW.Y;
        }
        else if (_approachRecordedW != default)
        {
            // Only one recorded position — use current pos as the direction endpoint.
            dx = currentW.X - _approachRecordedW.X;
            dy = currentW.Y - _approachRecordedW.Y;
        }
        else if (_chaseProgTarget != default)
        {
            // No approach positions recorded (e.g. Navigation-driven movement without
            // Approach key presses) — use the current chase target as the direction.
            // This gives TryUnstuck a meaningful projection for goals like FFG whose
            // navigation is driven entirely by Navigation internals.
            dx = _chaseProgTarget.X - currentW.X;
            dy = _chaseProgTarget.Y - currentW.Y;
            logger.LogInformation("[NAV] ApproachEscape: no recorded approach positions — using chase target for direction.");
        }
        else
        {
            // No recorded positions and no chase target — fall back to facing direction.
            float facing = playerReader.Direction;
            dx = Cos(facing);
            dy = Sin(facing);
            logger.LogInformation("[NAV] ApproachEscape: no direction data — using facing direction.");
        }

        float len = Sqrt(dx * dx + dy * dy);
        if (len < 0.01f)
        {
            float facing = playerReader.Direction;
            dx = Cos(facing);
            dy = Sin(facing);
            len = 1f;
        }

        return new Vector3(
            currentW.X + (dx / len) * yards,
            currentW.Y + (dy / len) * yards,
            currentW.Z);
    }

    private void LogRefill(string msg)
    {
        long now = DateTime.UtcNow.Ticks;
        if (now - lastRefillLogTick > RefillLogCooldownTicks)
        {
            lastRefillLogTick = now;
            logger.LogInformation(msg);
        }
    }

    private bool IsNearBlacklistEdge(Vector3 posW, float extra)
    {
        if (AreaBlacklist == null) return false;
        return AreaBlacklist.TryGetContainingRectInflated(posW, extra, out _);
    }

    private void RefillRouteToNextWaypoint(CancellationToken token)
    {
        long _refillStartTick = Environment.TickCount64;
        string _phase = "enter";

        try
        {
            LogRefill(
            $"[NAV-SANITY] Refill entered. " +
            $"insideBlacklist={AreaBlacklist?.ContainsWorld(Nav2D(playerReader.WorldPos)) == true} " +
            $"waypoints={wayPoints.Count} route={routeToNextWaypoint.Count}");

            LogRefill("[NAV] RefillRouteToNextWaypoint tick");

            if (routeToNextWaypoint.Count > 0)
            {
                LogRefill($"[NAV] Refill: exit (routeToNextWaypoint.Count={routeToNextWaypoint.Count})");
                RefillExit("routeAlreadyNonEmpty");
                _phase = "exit_routeAlreadyNonEmpty";
                goto REFILL_EXIT;
            }

            if (wayPoints.Count == 0)
            {
                _phase = "exit_noWaypoints";
                LogRefill("[NAV] Refill: exit (no waypoints)");
                RefillExit("noWaypoints");
                UpdateTotalRoute();
                CompleteDestinationReached();
                goto REFILL_EXIT;
            }

            if (DateTime.UtcNow < noPathCooldownUntilUtc)
            {
                LogRefill("[NAV] Refill: exit (no path rejection cooldown)");
                RefillExit("noPathCooldown");
                _phase = "exit_noPathCooldown";
                goto REFILL_EXIT;
            }

            if (DateTime.UtcNow < blacklistRejectCooldownUntilUtc)
            {
                LogRefill("[NAV] Refill: exit (blacklist rejection cooldown)");
                RefillExit("blacklistRejectCooldown");
                _phase = "exit_blacklistRejectCooldown";
                goto REFILL_EXIT;
            }

            if (waitingForPathResult || Volatile.Read(ref pathRequestPending) == 1)
            {
                _phase = "exit_requestInFlight_gate";
                RefillExit("requestInFlight_gate");
                goto REFILL_EXIT;
            }

            routeToNextWaypoint.Clear();

            Vector3 startW = Nav2D(playerReader.WorldPos);

            // Escape-mode guard
            if (escapeActive)
            {
                if (AreaBlacklist?.ContainsWorld(startW) != true)
                {
                    float edgeBuffer = DetourMargin + 6f;
                    bool nearEdge = AreaBlacklist != null &&
                                    AreaBlacklist.TryGetContainingRectInflated(startW, edgeBuffer, out _);

                    if (!nearEdge)
                    {
                        escapeActive = false;
                    }
                }

                if (escapeActive)
                {
                    if (routeToNextWaypoint.Count == 0)
                    {
                        escapeActive = false;
                        escapeRouteInProgress = false;
                    }
                    else
                    {
                        SyncRouteStateToTop(false);
                        RefillExit("escapeActive_hold");
                        _phase = "exit_escapeActive_hold";
                        goto REFILL_EXIT;
                    }
                }
            }

            if (TryComputeEscapeOutOfBlacklist(startW, out var escapeW))
            {
                escapeActive = true;
                escapeTargetW = Nav2D(escapeW);
                escapeBestDist = float.MaxValue;
                escapeNoProgressSinceUtc = DateTime.UtcNow;

                if (lastEscapePoint.WorldDistanceXYTo(escapeW) < 0.1f &&
                    escapeInsertCooldownTicks > 0)
                {
                    RefillExit("sameEscapeRecentlyInserted");
                    _phase = "exit_sameEscapeRecentlyInserted";
                    goto REFILL_EXIT;
                }

                lastEscapePoint = escapeW;
                escapeInsertCooldownTicks = 10;

                if (startW.WorldDistanceXYTo(escapeW) < ReachedDistance(OutDoorMinDistance))
                {
                    logger.LogWarning($"[BL] Escape computed but too close; start={startW} escape={escapeW}");
                    RefillExit("escapePointAvoidThrashing");
                    _phase = "exit_escapePointAvoidThrashing";
                    goto REFILL_EXIT;
                }

                routeToNextWaypoint.Clear();
                routeToNextWaypoint.Push(Nav2D(escapeW));

                escapeRouteInProgress = true;
                escapeRouteEndW = Nav2D(escapeW);

                SyncRouteStateToTop(false);
                logger.LogInformation($"[BL] Escape-first ROUTE set: {startW} -> {escapeW}");
                RefillExit("escapeRouteSet");
                _phase = "exit_escapeRouteSet";
                goto REFILL_EXIT;
            }

            int pending = Volatile.Read(ref pathRequestPending);
            if (waitingForPathResult || pending == 1)
            {
                LogRefill($"[NAV] Refill: exit (waitingForPathResult={waitingForPathResult}, pathRequestPending={pending})");
                RefillExit("waitingForPathResultOrPendingTrue");
                _phase = "exit_waitingForPathResultOrPendingTrue";
                goto REFILL_EXIT;
            }

            if (!SkipBlacklistedWaypoints())
            {
                // Fix #1: removed duplicate OnDestinationReached?.Invoke() here.
                // CompleteDestinationReached() already fires the event internally.
                RefillExit("SkipBlacklistedWaypoints_false_destinationReached");
                logger.LogInformation("[NAV] Refill: SkipBlacklistedWaypoints returned false -> destination reached");
                CompleteDestinationReached();
                _phase = "exit_SkipBlacklistedWaypoints_false_destinationReached";
                goto REFILL_EXIT;
            }

            Vector3 targetRaw = wayPoints.Peek();
            Vector3 targetW = Nav2D(wayPoints.Peek());

            float distance = startW.WorldDistanceXYTo(targetW);

            logger.LogWarning(
                $"[NAV-DBG] REFILL start={startW} wpTop(raw)={targetRaw} wpTop(norm)={targetW} " +
                $"dist={distance:0.00} usePather? TBD wpCount={wayPoints.Count}");

            float wpReached = ReachedDistance(OutDoorMinDistance);
            float wpPopThreshold = MathF.Max(wpReached + 0.35f, POP_DIST - 0.05f);

            if (distance <= wpPopThreshold)
            {
                var completed = wayPoints.Peek();
                wayPoints.Pop();

                logger.LogWarning(
                    $"[NAV] REFILL: waypoint already reached -> POP {completed} " +
                    $"newWpTop={(wayPoints.Count > 0 ? wayPoints.Peek().ToString() : "<none>")} " +
                    $"d={distance:0.00} thr={wpPopThreshold:0.00}");

                routeToNextWaypoint.Clear();
                SyncRouteStateToTop();

                OnWayPointReached?.Invoke();

                if (wayPoints.Count == 0)
                {
                    // Do NOT call CompleteDestinationReached / OnDestinationReached here.
                    //
                    // Firing OnDestinationReached from inside RefillRouteToNextWaypoint creates
                    // an unbreakable feedback loop:
                    //   OnDestinationReached -> Navigation_OnDestinationReached
                    //   -> RefillWaypoints -> SetWayPoints(closePoint)
                    //   -> wpAlreadyReached_pop fires again -> OnDestinationReached -> repeat
                    //
                    // Instead, leave wp=0 route=0. The very next Update() tick enters the
                    // noWork block (wp=0 && route=0) which fires CompleteDestinationReached
                    // exactly once under the latch, cleanly breaking the loop.
                    //
                    // FRG is responsible for ensuring the waypoints it provides via
                    // SetWayPoints are actually ahead of the player (world-distance checked
                    // against Navigation.POP_DIST before calling SetWayPoints).
                    RefillExit("wpAlreadyReached_pop_noWpLeft");
                    _phase = "exit_wpAlreadyReached_pop_noWpLeft";
                    goto REFILL_EXIT;
                }
            }

            bool usePather = distance > MaxDistance || distance > AvgDistance * 2;

            BlacklistRect r = default;
            bool directBlocked = AreaBlacklist != null &&
                                 TryGetBlockingRectEscapeAware(startW, targetW, out r);

            if (directBlocked)
            {
                if (TryInsertDetour(startW, targetW, r))
                {
                    logger.LogInformation($"[NAV] Refill: detour inserted for blocked segment start={startW} end={targetW}");
                    RefillExit("tryForwardDetour");
                    _phase = "exit_tryForwardDetour";
                    goto REFILL_EXIT;
                }

                logger.LogInformation($"[NAV] Refill: detour failed -> forcing pather start={startW} end={targetW}");
                usePather = true;
            }

            if (usePather)
            {
                _phase = "exit_enqueuePathRequest";
                // Committing to a pather request means we have real work ahead — clear latch.
                ClearDestinationLatch();
                stopMoving.Stop();

                logger.LogWarning(
                    $"[NAV-DBG] ENQUEUE PATH start={startW} end(targetWp raw)={targetRaw} end(targetWp norm)={targetW} dist={distance:0.00}");

                NavDbg($"REFILL enqueuePath start={startW} end={targetW} dist={distance:0.00}");

                EnqueuePathRequest(playerReader.UIMapId.Value, startW, targetW, distance);
                RefillExit("enqueuePathRequest");
                goto REFILL_EXIT;
            }

            // Direct route: add midpoint for long segments to reduce jitter.
            Vector3 mid = default;
            bool hasMid = false;

            if (distance > 8f)
            {
                mid = new Vector3(
                    (startW.X + targetW.X) * 0.5f,
                    (startW.Y + targetW.Y) * 0.5f,
                    startW.Z
                );

                hasMid = !IsBlacklistedPoint(mid) && !SegmentBlockedEscapeAware(startW, mid);
            }

            routeToNextWaypoint.Push(Nav2D(targetW));

            if (hasMid)
                routeToNextWaypoint.Push(Nav2D(mid));

            noProgressBestDist = float.MaxValue;
            noProgressSinceUtc = DateTime.MinValue;
            noProgressTargetW = routeToNextWaypoint.Count > 0 ? routeToNextWaypoint.Peek() : default;

            // Route is real and ahead of the player — safe to clear the latch now.
            ClearDestinationLatch();
            SyncRouteStateToTop(false);

            NavDbg(
                $"REFILL builtDirect routeCount={routeToNextWaypoint.Count} " +
                $"top={(routeToNextWaypoint.Count > 0 ? routeToNextWaypoint.Peek().ToString() : "<none>")} " +
                $"wpTop(raw)={(wayPoints.Count > 0 ? wayPoints.Peek().ToString() : "<none>")} " +
                $"wpTop(norm)={(wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()).ToString() : "<none>")}");

            RefillExit("builtDirectRoute");
            _phase = "end_normal";

        REFILL_EXIT:;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                $"[NAV-REFILL-EXCEPTION] phase={_phase} " +
                $"wp={wayPoints.Count} route={routeToNextWaypoint.Count} " +
                $"waiting={waitingForPathResult} pending={Volatile.Read(ref pathRequestPending)} " +
                $"pos={Nav2D(playerReader.WorldPos)} wpTop={(wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()).ToString() : "<none>")}");
            throw;
        }
        finally
        {
            RefillEnd(_phase, _refillStartTick);
        }
    }

    private void EnqueuePathRequest(int mapId, Vector3 startW, Vector3 endW, float distance)
    {
        if (!TryBeginPathRequest(startW, endW, out long requestId))
        {
            logger.LogInformation($"[NAV] EnqueuePathRequest: blocked (pending). start={startW} end={endW}");
            return;
        }

        logger.LogInformation($"[NAV] EnqueuePathRequest: QUEUED reqId={requestId} start={startW} end={endW} dist={distance}");

        var req = new PathRequest(
            mapId,
            startW,
            endW,
            distance,
            result => PathCalculatedCallback(requestId, result)
        );

        PathRequest(req);
    }

    private void PathRequest(PathRequest pathRequest)
    {
        waitingForPathResult = true;
        waitingSinceUtc = DateTime.UtcNow;
        logger.LogInformation($"[BL] PathRequest queued: {pathRequest.StartW} -> {pathRequest.EndW} dist={pathRequest.Distance}");
        pathRequests.Enqueue(pathRequest);
        manualReset.Set();
    }

    private bool TryBeginPathRequest(Vector3 startW, Vector3 endW, out long requestId)
    {
        requestId = 0;

        if (Interlocked.CompareExchange(ref pathRequestPending, 1, 0) != 0)
            return false;

        requestId = Interlocked.Increment(ref nextPathRequestId);

        Volatile.Write(ref activePathRequestId, requestId);
        Volatile.Write(ref waitingRequestId, requestId);

        lastRequestStart = startW;
        lastRequestEnd = endW;

        return true;
    }

    private void EndPathRequest(long requestId)
    {
        long activeId = Volatile.Read(ref activePathRequestId);

        if (activeId == requestId)
        {
            Interlocked.Exchange(ref pathRequestPending, 0);
            Volatile.Write(ref activePathRequestId, 0);
        }
    }

    private void ResetChaseProgressWatchdog(Vector3? newTarget = null)
    {
        var now = DateTime.UtcNow;

        _chaseProgTarget = newTarget ?? default;
        _chaseProgBestDist = float.MaxValue;
        _chaseProgSinceUtc = DateTime.MinValue;

        _chaseLastPos = Nav2D(playerReader.WorldPos);
        _chaseLastMovedUtc = now;

        _chaseUnstuckCooldownUntilUtc = DateTime.MinValue;
    }

    private void ResetNoProgressWatchdog(Vector3? newTarget = null)
    {
        noProgressBestDist = float.MaxValue;
        noProgressSinceUtc = DateTime.MinValue;
        noProgressTargetW = newTarget ?? default;
    }

    private bool HandleRepeatedNoPath(Vector3 startW, Vector3 endW)
    {
        bool same =
            lastNoPathStartW.WorldDistanceXYTo(startW) < 0.1f &&
            lastNoPathEndW.WorldDistanceXYTo(endW) < 0.1f;

        if (!same)
        {
            lastNoPathStartW = startW;
            lastNoPathEndW = endW;
            sameNoPathCount = 0;
        }

        sameNoPathCount++;

        logger.LogWarning(
            $"[NAV] Repeated no-path count={sameNoPathCount} start={startW} end={endW} wpCount={wayPoints.Count}");

        if (wayPoints.Count == 1)
        {
            float dist = startW.WorldDistanceXYTo(endW);

            if (sameNoPathCount == 2 && dist <= 30f)
            {
                logger.LogWarning(
                    $"[NAV] No-path fallback: building direct route start={startW} end={endW} dist={dist:0.00}");

                routeToNextWaypoint.Clear();
                routeToNextWaypoint.Push(Nav2D(endW));
                stuckDetector.SetTargetLocation(StuckOwnerId, Nav2D(endW));
                ResetChaseProgressWatchdog(Nav2D(endW));
                ResetNoProgressWatchdog(Nav2D(endW));
                UpdateTotalRoute();

                noPathCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
                return true;
            }

            if (sameNoPathCount == 3)
            {
                logger.LogWarning(
                    $"[NAV] No-path fallback: forcing unstuck start={startW} end={endW}");

                stuckDetector.SetTargetLocation(StuckOwnerId, Nav2D(endW));
                stuckDetector.Update(StuckOwnerId, token);

                noPathCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(1000);
                return true;
            }
        }

        if (sameNoPathCount >= MaxSameNoPathBeforeFallback)
        {
            logger.LogWarning(
                $"[NAV] No-path fallback: escalating to OnPathFailed start={startW} end={endW}");

            sameNoPathCount = 0;
            OnPathFailed?.Invoke(startW, endW);
            noPathCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(1000);
            return true;
        }

        return false;
    }

    private void PathCalculatedCallback(long requestId, PathResult result)
    {
        long activeId = Volatile.Read(ref activePathRequestId);
        long waitingId = Volatile.Read(ref waitingRequestId);

        if (activeId != requestId)
        {
            logger.LogInformation(
                $"[NAV] Ignoring stale path result reqId={requestId} waitingReqId={waitingId} activeReqId={activeId}");
            return;
        }

        Volatile.Write(ref waitingRequestId, 0);

        EndPathRequest(requestId);
        waitingForPathResult = false;

        logger.LogInformation($"[NAV] PathCalculatedCallback reqId={requestId} pathLen={result.Path.Length} start={result.StartW} end={result.EndW}");

        Vector3 wpTop = wayPoints.Count > 0 ? wayPoints.Peek() : default;
        logger.LogWarning(
            $"[NAV-DBG] PATH RESULT requestId={requestId} resultStart={result.StartW} resultEnd={result.EndW} " +
            $"currentWpTop={wpTop} endMatchesWp={(wayPoints.Count > 0 ? wpTop.WorldDistanceXYTo(result.EndW) : -1):0.00}");

        if (!active)
            return;

        if (result.Path.Length == 0)
        {
            noPathCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(350);

            if (lastFailedDestination != result.EndW)
            {
                lastFailedDestination = result.EndW;
                LogPathfinderFailed(logger, result.StartW, result.EndW, result.ElapsedMs);
            }

            failedAttempt++;

            if (HandleRepeatedNoPath(Nav2D(result.StartW), Nav2D(result.EndW)))
                return;

            if (failedAttempt > 2)
            {
                failedAttempt = 0;
                stuckDetector.SetTargetLocation(StuckOwnerId, Nav2D(result.EndW));
                stuckDetector.Update(StuckOwnerId);
            }

            return;
        }

        failedAttempt = 0;
        sameNoPathCount = 0;
        lastNoPathStartW = default;
        lastNoPathEndW = default;

        LogPathfinderSuccess(logger, result.Distance, result.StartW, result.EndW, result.ElapsedMs);

        const float MIN_FIRST_STEP_DIST = 1.0f;

        routeToNextWaypoint.Clear();

        Vector3 start2D = Nav2D(result.StartW);

        for (int i = result.Path.Length - 1; i >= 0; i--)
        {
            Vector3 p = Nav2D(result.Path[i]);

            if (p.WorldDistanceXYTo(start2D) < MIN_FIRST_STEP_DIST)
                continue;

            routeToNextWaypoint.Push(Nav2D(p));
        }

        if (routeToNextWaypoint.Count == 0 && wayPoints.Count > 0)
            routeToNextWaypoint.Push(Nav2D(wayPoints.Peek()));

        if (SimplifyRouteToWaypoint)
            SimplyfyRouteToWaypoint();

        if (routeToNextWaypoint.Count == 0 && wayPoints.Count > 0)
            routeToNextWaypoint.Push(Nav2D(wayPoints.Peek()));

        logger.LogWarning(
            $"[NAV-DBG] ROUTESET reqId={requestId} routeCount={routeToNextWaypoint.Count} " +
            $"routeTop={(routeToNextWaypoint.Count > 0 ? Nav2D(routeToNextWaypoint.Peek()).ToString() : "<none>")} " +
            $"wpTop={(wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()).ToString() : "<none>")} " +
            $"player={playerReader.WorldPos}"
            );

        // If this was an escape path, reset the no-progress timer so the watchdog
        // gives the player a full second to start moving before it can re-trigger.
        if (escapeRouteInProgress)
            escapeNoProgressSinceUtc = DateTime.UtcNow;

        if (AreaBlacklist != null)
        {
            bool startedInside = AreaBlacklist.ContainsWorld(Nav2D(result.StartW));

            if (!startedInside)
            {
                bool nearEdge = AreaBlacklist.TryGetContainingRectInflated(
                    Nav2D(result.StartW),
                    DetourMargin + 6f,
                    out _
                );

                if (!nearEdge)
                {
                    var steps = routeToNextWaypoint.ToArray();

                    Vector3 prev = Nav2D(result.StartW);

                    for (int i = 0; i < steps.Length; i++)
                    {
                        Vector3 cur = Nav2D(steps[i]);

                        if (AreaBlacklist.ContainsWorld(cur))
                        {
                            logger.LogWarning(
                                $"[BL] Path node inside blacklist; rejecting. " +
                                $"badPoint={cur}");

                            if (TryInsertDetourFromRejectedPath(result.StartW, result.EndW, cur))
                            {
                                sameRejectCount = 0;
                                lastRejectStartW = default;
                                lastRejectEndW = default;

                                blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
                                ResetNoProgressWatchdog();
                                return;
                            }

                            if (HandleBlacklistReject(result))
                            {
                                ResetNoProgressWatchdog();
                                return;
                            }

                            blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(500);

                            routeToNextWaypoint.Clear();
                            SyncRouteStateToTop();
                            return;
                        }

                        if (SegmentBlockedEscapeAware(prev, cur))
                        {
                            logger.LogWarning(
                                $"[BL] Path segment crosses blacklist; rejecting. " +
                                $"seg=({prev} -> {cur}) i={i} " +
                                $"routeTop={(routeToNextWaypoint.Count > 0 ? Nav2D(routeToNextWaypoint.Peek()).ToString() : "<none>")} " +
                                $"routeEnd={(steps.Length > 0 ? Nav2D(steps[^1]).ToString() : "<none>")}");

                            Vector3 hint = new Vector3((prev.X + cur.X) * 0.5f, (prev.Y + cur.Y) * 0.5f, 0f);

                            if (TryInsertDetourFromRejectedPath(result.StartW, result.EndW, hint))
                            {
                                sameRejectCount = 0;
                                lastRejectStartW = default;
                                lastRejectEndW = default;

                                blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
                                ResetNoProgressWatchdog();
                                return;
                            }

                            if (HandleBlacklistReject(result))
                            {
                                ResetNoProgressWatchdog();
                                return;
                            }

                            blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(500);

                            routeToNextWaypoint.Clear();
                            SyncRouteStateToTop();
                            return;
                        }

                        prev = cur;
                    }
                }
            }
        }

        if (routeToNextWaypoint.Count == 0)
        {
            SyncRouteStateToTop();
            noPathCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(350);
            OnPathFailed?.Invoke(result.StartW, result.EndW);
            return;
        }

        // Route from pather is real and ahead of the player — safe to clear the latch now.
        ClearDestinationLatch();
        SyncRouteStateToTop(false);

        OnPathCalculated?.Invoke();
    }

    private bool TryInsertDetourFromRejectedPath(Vector3 startW, Vector3 endW, Vector3 badPointW)
    {
        if (AreaBlacklist == null)
            return false;

        if (wayPoints.Count == 0 || wayPoints.Peek().WorldDistanceXYTo(endW) > 0.1f)
            return false;

        if (!AreaBlacklist.TryGetContainingRect(Nav2D(badPointW), out var rect))
            return false;

        float m = DetourMargin;
        float z = 0f;

        var inflated = rect.Inflate(m * 0.5f);

        Vector3 best = default;
        float bestScore = float.MaxValue;
        bool found = false;

        foreach (var d in BuildDetourCandidates(inflated, m, z))
        {
            if (IsBlacklistedPoint(d)) continue;

            if (SegmentBlockedEscapeAware(startW, d)) continue;
            if (SegmentBlockedEscapeAware(d, endW)) continue;

            float score = startW.WorldDistanceXYTo(d) + d.WorldDistanceXYTo(endW);
            if (score < bestScore)
            {
                bestScore = score;
                best = d;
                found = true;
            }
        }

        if (!found)
            return false;

        Vector3 target = Nav2D(wayPoints.Pop());
        wayPoints.Push(Nav2D(target));
        wayPoints.Push(Nav2D(best));

        routeToNextWaypoint.Clear();
        SyncRouteStateToTop();

        logger.LogWarning($"[BL] Inserted detour after rejection: detour={best} target={endW} badPoint={badPointW}");
        return true;
    }

    private bool HandleBlacklistReject(PathResult result)
    {
        bool same = lastRejectStartW.WorldDistanceXYTo(result.StartW) < 0.1f &&
                    lastRejectEndW.WorldDistanceXYTo(result.EndW) < 0.1f;

        if (!same)
        {
            lastRejectStartW = result.StartW;
            lastRejectEndW = result.EndW;
            sameRejectCount = 0;
        }

        sameRejectCount++;

        if (sameRejectCount < MaxSameRejectBeforeFallback)
        {
            blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
            return false;
        }

        logger.LogError($"[BL] Rejected same path {sameRejectCount} times. Skipping waypoint {result.EndW} to avoid deadlock.");

        sameRejectCount = 0;

        if (wayPoints.Count > 0 && wayPoints.Peek().WorldDistanceXYTo(result.EndW) < 0.1f)
            wayPoints.Pop();

        routeToNextWaypoint.Clear();
        SyncRouteStateToTop();

        blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddSeconds(1);

        return true;
    }

    private void PathFinderThread()
    {
        while (!token.IsCancellationRequested)
        {
            manualReset.Reset();

            while (pathRequests.TryDequeue(out var pathRequest))
            {
                try
                {
                    logger.LogInformation($"[BL] PathFinderThread processing: {pathRequest.StartW} -> {pathRequest.EndW}");

                    Vector3[] path = pather.FindWorldRoute(pathRequest.MapId, pathRequest.StartW, pathRequest.EndW);

                    pathResults.Enqueue(new PathResult(pathRequest, path, pathRequest.Callback));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[NAV] PathFinderThread exception; returning empty path");
                    pathResults.Enqueue(new PathResult(pathRequest, Array.Empty<Vector3>(), pathRequest.Callback));
                }
            }

            manualReset.Wait();
        }
    }

    private float ReachedDistance(float minDistance)
    {
        return mountHandler.IsMounted()
            ? MinDistanceMount
            : bits.Indoors()
                ? IndoorMinDistance
                : minDistance;
    }

    private void ReduceByDistance(Vector3 playerW, float minDistance)
    {
        bool poppedAny = false;
        Vector3 lastPopped = default;

        while (routeToNextWaypoint.Count > 0 &&
               playerW.WorldDistanceXYTo(routeToNextWaypoint.Peek()) < ReachedDistance(minDistance))
        {
            lastPopped = routeToNextWaypoint.Pop();
            poppedAny = true;
        }

        if (!poppedAny)
            return;

        if (NavDebugDue())
        {
            Vector3 newTop = routeToNextWaypoint.Count > 0 ? routeToNextWaypoint.Peek() : default;
            Vector3 wpTop = wayPoints.Count > 0 ? wayPoints.Peek() : default;
            logger.LogWarning($"[NAV-DBG] ReduceByDistance poppedLast={lastPopped} newRouteTop={newTop} wpTop={wpTop}");
        }

        SetLastSafeAnchor(lastPopped);

        noProgressBestDist = float.MaxValue;
        noProgressSinceUtc = DateTime.MinValue;
        noProgressTargetW = routeToNextWaypoint.Count > 0 ? routeToNextWaypoint.Peek() : default;

        if (routeToNextWaypoint.Count == 0)
            noProgressRefillCooldownUntilUtc = DateTime.MinValue;
    }

    private void AdjustHeading(float heading, CancellationToken token)
    {
        float diff1 = Abs(Tau + heading - playerReader.Direction) % Tau;
        float diff2 = Abs(heading - playerReader.Direction - Tau) % Tau;

        float diff = Min(diff1, diff2);
        if (diff > minAngleToTurn)
        {
            if (diff > minAngleToStopBeforeTurn)
            {
                stopMoving.Stop();
            }

            playerDirection.SetDirection(heading, Nav2D(routeToNextWaypoint.Peek()), OutDoorMinDistance, token);
        }
    }

    private bool AdjustNextWaypointPointToClosest()
    {
        if (wayPoints.Count < 2) { return false; }

        Vector3 A = Nav2D(wayPoints.Pop());
        Vector3 B = wayPoints.Peek();
        Vector2 result = VectorExt.GetClosestPointOnLineSegment(A.AsVector2(), B.AsVector2(), playerReader.WorldPos.AsVector2());
        Vector3 newPoint = new(result.X, result.Y, 0f);

        if (newPoint.WorldDistanceXYTo(wayPoints.Peek()) > OutDoorMinDistance)
        {
            wayPoints.Push(Nav2D(newPoint));
            if (debug)
                LogDebug("Adjusted resume point");

            return false;
        }

        if (debug)
            LogDebug("Skipped next point in path");

        return true;
    }

    private void V1_AttemptToKeepRouteToWaypoint()
    {
        float totalDistance = VectorExt.TotalDistance<Vector3>(TotalRoute, VectorExt.WorldDistanceXY);
        if (totalDistance > MaxDistance / 2)
        {
            Vector3 playerW = Nav2D(playerReader.WorldPos);
            float distanceToRoute = playerW.WorldDistanceXYTo(routeToNextWaypoint.Peek());
            float distanceToPrevLoc = playerW.WorldDistanceXYTo(playerWorldPos);

            if (!RouteTargetsWaypointTop())
            {
                logger.LogWarning("[NAV] KeepRoute vetoed: route does not target current waypoint. Clearing.");
                routeToNextWaypoint.Clear();
                return;
            }

            if (distanceToRoute > 2 * MinDistanceMount &&
                distanceToPrevLoc > 2 * MinDistanceMount)
            {
                LogV1ClearRouteToWaypoint(logger, patherName, distanceToRoute);
                routeToNextWaypoint.Clear();
            }
            else
            {
                LogV1KeepRouteToWaypoint(logger, patherName, distanceToRoute);
                ResetStuckParameters();
            }
        }
        else
        {
            LogV1ClearRouteToWaypointTooFar(logger, patherName, totalDistance, MaxDistance / 2);
            routeToNextWaypoint.Clear();
        }
    }

    private void SimplyfyRouteToWaypoint()
    {
        const bool HighQuality = false;
        Span<Vector3> reduced = PathSimplify.Simplify(routeToNextWaypoint.ToArray(), OutDoorMinDistance / 2, HighQuality);
        if (debug)
            LogDebug($"{nameof(SimplyfyRouteToWaypoint)} {routeToNextWaypoint.Count} -> {reduced.Length} | HQ: {HighQuality}");

        routeToNextWaypoint.Clear();
        for (int i = reduced.Length - 1; i >= 0; i--)
        {
            routeToNextWaypoint.Push(Nav2D(reduced[i]));
        }
    }

    private void UpdateTotalRoute()
    {
        TotalRoute = new Vector3[routeToNextWaypoint.Count + wayPoints.Count];
        routeToNextWaypoint.CopyTo(TotalRoute, 0);
        wayPoints.CopyTo(TotalRoute, routeToNextWaypoint.Count);
    }

    public bool IsInBlacklistArea()
    {
        bool inside = AreaBlacklist?.ContainsWorld(Nav2D(playerReader.WorldPos)) == true;
        return inside;
    }

    private bool IsBlacklistedPoint(Vector3 worldPoint)
        => AreaBlacklist != null && AreaBlacklist.ContainsWorld(worldPoint);

    private bool TryGetBlockingRectEscapeAware(Vector3 startW, Vector3 endW, out BlacklistRect blockingRect)
    {
        startW = Nav2D(startW);
        endW = Nav2D(endW);

        blockingRect = default;
        if (AreaBlacklist == null) return false;

        if (AreaBlacklist.TryGetContainingRect(startW, out var containingRect))
        {
            var endInsideSame = containingRect.Contains(new Vector2(endW.X, endW.Y));
            if (!endInsideSame)
            {
                return AreaBlacklist.TryGetBlockingRectExcluding(startW, endW, containingRect, out blockingRect);
            }
        }

        return AreaBlacklist.TryGetBlockingRect(startW, endW, out blockingRect);
    }

    private bool SegmentBlockedEscapeAware(Vector3 startW, Vector3 endW)
        => TryGetBlockingRectEscapeAware(startW, endW, out _);

    private bool SkipBlacklistedWaypoints()
    {
        if (AreaBlacklist == null) return wayPoints.Count > 0;

        if (AreaBlacklist.TryGetContainingRect(Nav2D(playerReader.WorldPos), out _))
            return wayPoints.Count > 0;

        int removed = 0;
        while (wayPoints.Count > 0 && IsBlacklistedPoint(wayPoints.Peek()))
        {
            wayPoints.Pop();
            removed++;
        }

        if (removed > 0)
            UpdateTotalRoute();

        return wayPoints.Count > 0;
    }

    private static IEnumerable<Vector3> BuildDetourCandidates(BlacklistRect r, float m, float z)
    {
        float[] t = { 0.25f, 0.5f, 0.75f };

        foreach (var u in t)
        {
            float x = Lerp(r.MinX, r.MaxX, u);
            float y = Lerp(r.MinY, r.MaxY, u);

            yield return new Vector3(r.MinX - m, y, z);
            yield return new Vector3(r.MaxX + m, y, z);
            yield return new Vector3(x, r.MinY - m, z);
            yield return new Vector3(x, r.MaxY + m, z);
        }

        yield return new Vector3(r.MinX - m, r.MinY - m, z);
        yield return new Vector3(r.MaxX + m, r.MinY - m, z);
        yield return new Vector3(r.MaxX + m, r.MaxY + m, z);
        yield return new Vector3(r.MinX - m, r.MaxY + m, z);
    }

    private bool TryInsertDetour(Vector3 startW, Vector3 targetW, BlacklistRect blockingRect)
    {
        if (lastDetourTarget != targetW)
        {
            lastDetourTarget = targetW;
            detourAttemptsForTarget = 0;
        }
        detourAttemptsForTarget++;
        if (detourAttemptsForTarget > MaxDetourAttemptsPerTarget)
        {
            logger.LogWarning($"[BL] detour failed start={startW} target={targetW} rect=({blockingRect.MinX},{blockingRect.MinY})-({blockingRect.MaxX},{blockingRect.MaxY})");
            return false;
        }

        float m = DetourMargin;
        float z = 0f;

        var inflated = blockingRect.Inflate(m * 0.5f);

        Vector3 best = default;
        float bestScore = float.MaxValue;
        bool found = false;

        foreach (var d in BuildDetourCandidates(inflated, m, z))
        {
            if (IsBlacklistedPoint(d)) continue;
            if (SegmentBlockedEscapeAware(startW, d)) continue;
            if (SegmentBlockedEscapeAware(d, targetW)) continue;

            float score = startW.WorldDistanceXYTo(d) + d.WorldDistanceXYTo(targetW);
            if (score < bestScore)
            {
                bestScore = score;
                best = d;
                found = true;
            }
        }

        if (!found)
        {
            logger.LogWarning(
                $"[BL] detour failed (no candidate): start={startW} target={targetW} rect=({blockingRect.MinX},{blockingRect.MinY})-({blockingRect.MaxX},{blockingRect.MaxY})");
            return false;
        }

        static bool SamePoint(Vector3 a, Vector3 b)
            => a.WorldDistanceXYTo(b) < 0.01f;

        if (wayPoints.Count == 0 || !SamePoint(wayPoints.Peek(), targetW))
        {
            logger.LogInformation($"[BL] detour aborted (target changed): start={startW} target={targetW}");
            return false;
        }

        wayPoints.Pop();
        wayPoints.Push(Nav2D(targetW));
        wayPoints.Push(Nav2D(best));

        routeToNextWaypoint.Clear();
        SyncRouteStateToTop();

        logger.LogInformation($"[BL] detour inserted: {best} before target {targetW}");
        return true;
    }

    private static float Clamp(float v, float min, float max)
        => v < min ? min : (v > max ? max : v);

    private bool TryComputeEscapeOutOfBlacklist(Vector3 startW, out Vector3 escapeW)
    {
        escapeW = startW;

        if (AreaBlacklist == null)
            return false;

        if (!AreaBlacklist.TryGetContainingRect(escapeW, out var rect))
            return false;

        float m = DetourMargin;
        float z = 0f;

        Vector3 goalW = wayPoints.Count > 0 ? wayPoints.Peek() : startW;

        for (int i = 0; i < 8; i++)
        {
            if (!AreaBlacklist.TryGetContainingRect(escapeW, out rect))
                return true;

            if (!TryPickEscapeCandidate(escapeW, goalW, rect, m, z, out var picked))
                return false;

            escapeW = picked;
        }

        return !AreaBlacklist.TryGetContainingRect(escapeW, out _) && !IsBlacklistedPoint(escapeW);
    }

    private bool TryPickEscapeCandidate(
        Vector3 fromW,
        Vector3 goalW,
        BlacklistRect rect,
        float margin,
        float z,
        out Vector3 best)
    {
        best = default;

        var inflated = rect.Inflate(margin * 0.5f);

        var sides = GetSidesByDistance(fromW, rect);
        int sidesToTry = Math.Min(2, sides.Length);

        var near = new List<Vector3>(32);
        for (int i = 0; i < sidesToTry; i++)
        {
            near.AddRange(BuildEdgeEscapeCandidates(fromW, inflated, sides[i], margin, z));
        }

        DedupByXY(near, 0.05f);

        near.Sort((a, b) => fromW.WorldDistanceXYTo(a).CompareTo(fromW.WorldDistanceXYTo(b)));

        foreach (var c in near)
        {
            if (IsBlacklistedPoint(c)) continue;
            if (SegmentBlockedEscapeAware(fromW, c)) continue;

            best = c;
            return true;
        }

        var broad = new List<Vector3>(64);
        foreach (var c in BuildDetourCandidates(inflated, margin, z))
            broad.Add(c);

        DedupByXY(broad, 0.05f);

        broad.Sort((a, b) =>
        {
            float sa = fromW.WorldDistanceXYTo(a) + 0.15f * a.WorldDistanceXYTo(goalW);
            float sb = fromW.WorldDistanceXYTo(b) + 0.15f * b.WorldDistanceXYTo(goalW);
            return sa.CompareTo(sb);
        });

        foreach (var c in broad)
        {
            if (IsBlacklistedPoint(c)) continue;
            if (SegmentBlockedEscapeAware(fromW, c)) continue;

            best = c;
            return true;
        }

        return false;
    }

    private static RectSide[] GetSidesByDistance(Vector3 p, BlacklistRect r)
    {
        float dL = MathF.Abs(p.X - r.MinX);
        float dR = MathF.Abs(r.MaxX - p.X);
        float dB = MathF.Abs(p.Y - r.MinY);
        float dT = MathF.Abs(r.MaxY - p.Y);

        var sides = new (RectSide side, float d)[]
        {
            (RectSide.Left, dL),
            (RectSide.Right, dR),
            (RectSide.Bottom, dB),
            (RectSide.Top, dT),
        };

        Array.Sort(sides, (a, b) => a.d.CompareTo(b.d));

        return new[] { sides[0].side, sides[1].side, sides[2].side, sides[3].side };
    }

    private static IEnumerable<Vector3> BuildEdgeEscapeCandidates(
        Vector3 fromW,
        BlacklistRect r,
        RectSide side,
        float margin,
        float z)
    {
        const float band = 3.0f;
        float[] offsets = { 0f, band, -band, 2 * band, -2 * band };

        float x = fromW.X;
        float y = fromW.Y;

        foreach (var off in offsets)
        {
            switch (side)
            {
                case RectSide.Left:
                    yield return new Vector3(r.MinX - margin, Clamp(y + off, r.MinY, r.MaxY), z);
                    break;

                case RectSide.Right:
                    yield return new Vector3(r.MaxX + margin, Clamp(y + off, r.MinY, r.MaxY), z);
                    break;

                case RectSide.Bottom:
                    yield return new Vector3(Clamp(x + off, r.MinX, r.MaxX), r.MinY - margin, z);
                    break;

                default: // Top
                    yield return new Vector3(Clamp(x + off, r.MinX, r.MaxX), r.MaxY + margin, z);
                    break;
            }
        }

        yield return side switch
        {
            RectSide.Left => new Vector3(r.MinX - margin, r.MinY, z),
            RectSide.Right => new Vector3(r.MaxX + margin, r.MinY, z),
            RectSide.Bottom => new Vector3(r.MinX, r.MinY - margin, z),
            _ => new Vector3(r.MinX, r.MaxY + margin, z),
        };

        yield return side switch
        {
            RectSide.Left => new Vector3(r.MinX - margin, r.MaxY, z),
            RectSide.Right => new Vector3(r.MaxX + margin, r.MaxY, z),
            RectSide.Bottom => new Vector3(r.MaxX, r.MinY - margin, z),
            _ => new Vector3(r.MaxX, r.MaxY + margin, z),
        };
    }

    private static void DedupByXY(List<Vector3> pts, float eps)
    {
        for (int i = 0; i < pts.Count; i++)
        {
            for (int j = pts.Count - 1; j > i; j--)
            {
                if (pts[i].WorldDistanceXYTo(pts[j]) < eps)
                    pts.RemoveAt(j);
            }
        }
    }

    private bool HasBeenActiveRecently()
    {
        return (DateTime.UtcNow - LastActive).TotalSeconds < 2;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private void LogDebug(string text)
    {
        logger.LogDebug($"D: {text}");
    }

    // Helper: pop route node if close enough. Returns true if it popped.
    private bool TryPopRouteTopIfReached(in Vector3 playerPos)
    {
        if (routeToNextWaypoint == null || routeToNextWaypoint.Count == 0)
            return false;

        var routeTop = Nav2D(routeToNextWaypoint.Peek());

        float d = playerPos.WorldDistanceXYTo(routeTop);

        // If this is the FINAL route node, align pop threshold with waypoint reach logic
        // so route pop and waypoint pop stay consistent and don't create a refill loop.
        float popDist = POP_DIST;

        if (routeToNextWaypoint.Count == 1 && wayPoints.Count > 0)
        {
            float wpReach = ReachedDistance(OutDoorMinDistance) + 0.35f;
            popDist = wpReach + 0.05f;

            if (popDist < STOP_DIST)
                popDist = STOP_DIST;
        }

        if (d <= popDist)
        {
            routeToNextWaypoint.Pop();
            return true;
        }

        return false;
    }

    // Fix #3: removed dead TryPopWaypointTopIfReached (used POP_DIST_SQ inconsistently with TryConsumeReachedWaypoint)
    // Fix #11: removed dead _dbgBestWpDist, _dbgWpSinceUtc, _dbgWpTarget fields
    // Fix #12: removed dead EscapePointFromRect method
    // Fix #13: removed dead ShouldTreatAsReached method and its fields (reachLatchTarget, reachLatched, reachLatchUntilTicks)

    #region Logging

    [LoggerMessage(
        EventId = 0040,
        Level = LogLevel.Warning,
        Message = "Unable to find path {start} -> {end}. Character may stuck! {elapsedMs}ms")]
    static partial void LogPathfinderFailed(ILogger logger, Vector3 start, Vector3 end, double elapsedMs);

    [LoggerMessage(
        EventId = 0041,
        Level = LogLevel.Information,
        Message = "Pathfinder - {distance} - {start} -> {end} {elapsedMs}ms")]
    static partial void LogPathfinderSuccess(ILogger logger, float distance, Vector3 start, Vector3 end, double elapsedMs);

    [LoggerMessage(
        EventId = 0042,
        Level = LogLevel.Information,
        Message = "Clear route to waypoint! Stucked for {elapsedMs}ms")]
    static partial void LogClearRouteToWaypointStuck(ILogger logger, double elapsedMs);

    [LoggerMessage(
        EventId = 0043,
        Level = LogLevel.Information,
        Message = "[{name}] distance from nearlest point is {distance}. Have to clear RouteToWaypoint.")]
    static partial void LogV1ClearRouteToWaypoint(ILogger logger, string name, float distance);

    [LoggerMessage(
        EventId = 0044,
        Level = LogLevel.Information,
        Message = "[{name}] distance is close {distance}. Keep RouteToWaypoint.")]
    static partial void LogV1KeepRouteToWaypoint(ILogger logger, string name, float distance);

    [LoggerMessage(
        EventId = 0045,
        Level = LogLevel.Information,
        Message = "[{name}] total distance {totalDistance} > {maxDistancehalf}. Have to clear RouteToWaypoint.")]
    static partial void LogV1ClearRouteToWaypointTooFar(ILogger logger, string name, float totalDistance, float maxDistancehalf);

    #endregion
}
