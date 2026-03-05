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

    // Reach hysteresis for final route point
    private Vector3 reachLatchTarget;
    private bool reachLatched;
    private long reachLatchUntilTicks;

    // Escape no progess fallback to pather variables
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

    // Backoff to prevent request spam on repeated blacklist rejections
    private DateTime blacklistRejectCooldownUntilUtc = DateTime.MinValue;
    private DateTime noPathCooldownUntilUtc = DateTime.MinValue;
    private Vector3 lastRejectStartW;
    private Vector3 lastRejectEndW;
    private int sameRejectCount;
    private const int MaxSameRejectBeforeFallback = 4;

    private long _navDebugNextTick;
    private const int NavDebugEveryMs = 250;

    private float _dbgBestWpDist = float.MaxValue;
    private DateTime _dbgWpSinceUtc = DateTime.MinValue;
    private Vector3 _dbgWpTarget;

    private long waitingRequestId;

    // Movement can stop at STOP_DIST, but we only advance (pop) at POP_DIST.
    // IMPORTANT: POP_DIST MUST BE >= STOP_DIST to avoid deadlocks.
    private const float STOP_DIST = 3.0f;              // when we stop applying movement input
    private const float POP_DIST = 3.6f;              // when we pop route node / waypoint (>= STOP_DIST)
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

        this.pathSettings = pathSettings;
        this.dataConfig = dataConfig;

        // Apply per-route area blacklists (map rects -> world rects)
        if (pathSettings.MapBlacklistRects is { Length: > 0 })
        {
            logger.LogInformation("[NAV]: pathSettings.MapBlacklistRects.Length: " + pathSettings.MapBlacklistRects.Length);

            AreaBlacklist = BlacklistConversion.BuildWorldBlacklistFromMapRects(
                pathSettings.MapBlacklistRects,
                playerReader.WorldMapArea
            );

            // Tune these as desired
            DetourMargin = 12f;
            MaxDetourAttemptsPerTarget = 6;
        }
        else
        {
            logger.LogInformation("[NAV]: pathSettings.MapBlacklistRects.Length: " + pathSettings.MapBlacklistRects.Length);
            AreaBlacklist = null;
        }
    }

    private static Vector3 Nav2D(Vector3 w) => new Vector3(w.X, w.Y, 0f);

    // (Optional but recommended) If you use 2D distances for nav, keep it consistent:
    private static float DistSq2D(in Vector3 a, in Vector3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    // Helper: pop route node if close enough. Returns true if it popped.
    private bool TryPopRouteTopIfReached(in Vector3 playerPos)
    {
        if (routeToNextWaypoint == null || routeToNextWaypoint.Count == 0)
            return false;

        var routeTop = Nav2D(routeToNextWaypoint.Peek());

        // Use 2D distance consistently
        float d = playerPos.WorldDistanceXYTo(routeTop);

        // IMPORTANT:
        // If this is the FINAL route node (very common in "direct" routing),
        // do NOT pop using POP_DIST (3.6), because that can be > waypoint reach threshold
        // (e.g., 3.35). That creates a refill loop where route pops but waypoint never pops.
        float popDist = POP_DIST;

        if (routeToNextWaypoint.Count == 1 && wayPoints.Count > 0)
        {
            // Keep this aligned with waypoint reach logic
            float wpReach = ReachedDistance(OutDoorMinDistance) + 0.35f;

            // Ensure popDist is not larger than what we consider "waypoint reached"
            // (Add a tiny epsilon to avoid float jitter at the boundary)
            popDist = wpReach + 0.05f;

            // Still guarantee POP_DIST >= STOP_DIST behavior
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

    // Helper: pop waypoint if close enough. Returns true if it popped.
    private bool TryPopWaypointTopIfReached(in Vector3 playerPos)
    {
        if (wayPoints == null || wayPoints.Count == 0)
            return false;

        var wpTop = Nav2D(wayPoints.Peek());

        float dSq = DistSq2D(playerPos, wpTop);
        if (dSq <= POP_DIST_SQ)
        {
            wayPoints.Pop();

            // If you have a progress watchdog, reset it here:
            // ResetNoProgressWatchdog();

            return true;
        }
        return false;
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
        // Keep it short but high-signal.
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
        // Gate this however you want (navDebugDue, debug flag, etc.)
        if(NavDebugDue())
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
        if(NavDebugDue())
        {
            logger.LogInformation($"[NAV-SANITY] Update entered navHash={GetHashCode()} tokenCancelled={token.IsCancellationRequested}");
        }

        if (!active)
        {
            //ResetStuckParameters();
            // Don't clear pathRequestPending here: a request may still be in flight.
            // We simply don't drive movement/stuck while inactive.
            return;
        }

        NavDbg($"STATE enter active={active} " +
       $"wp={wayPoints.Count} route={routeToNextWaypoint.Count} " +
       $"waiting={waitingForPathResult} pending={Volatile.Read(ref pathRequestPending)} " +
       $"waitingReq={Volatile.Read(ref waitingRequestId)} activeReq={Volatile.Read(ref activePathRequestId)} " +
       $"noPathCd={(noPathCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0}ms " +
       $"blCd={(blacklistRejectCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0}ms " +
       $"npCd={(noProgressRefillCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0}ms");

        // Apply per-route area blacklists (map rects -> world rects)
        if (AreaBlacklist == null && pathSettings.MapBlacklistRects is { Length: > 0 })
        {
            logger.LogInformation("[NAV]: Update - pathSettings.MapBlacklistRects.Length: " + pathSettings.MapBlacklistRects.Length);

            AreaBlacklist = BlacklistConversion.BuildWorldBlacklistFromMapRects(
                pathSettings.MapBlacklistRects,
                playerReader.WorldMapArea
            );

            // Tune these as desired
            DetourMargin = 12f;
            MaxDetourAttemptsPerTarget = 6;
        }
        else if (pathSettings.MapBlacklistRects is { Length: 0 })
        {
            logger.LogInformation("[NAV]: Update - pathSettings.MapBlacklistRects.Length: " + pathSettings.MapBlacklistRects.Length);
            AreaBlacklist = null;
        }

        if (escapeInsertCooldownTicks > 0)
            escapeInsertCooldownTicks--;

        if (wayPoints.Count == 0 && routeToNextWaypoint.Count == 0)
        {
            logger.LogInformation(
                $"[NAV-SANITY] EXIT noWork inside={AreaBlacklist?.ContainsWorld(Nav2D(playerReader.WorldPos)) == true}");

            // IMPORTANT: don't leave "W" held down from previous tick
            stopMoving.Stop();
            input.StopForward(true);

            // Also reset stuck state so we don't think we're still chasing something
            ResetStuckParameters();

            OnDestinationReached?.Invoke();
            NavDbg("RETURN noWork (wp=0 route=0) -> OnDestinationReached");
            return;
        }

        // Drop any waypoints that are inside forbidden regions
        SkipBlacklistedWaypoints();
        if (wayPoints.Count == 0 && routeToNextWaypoint.Count == 0)
        {
            stopMoving.Stop();
            input.StopForward(true);
            ResetStuckParameters();

            OnDestinationReached?.Invoke();
            NavDbg("RETURN afterSkipBlacklistedWaypoints noWork (wp=0 route=0)");
            return;
        }

        while (pathResults.TryDequeue(out PathResult result))
        {
            result.Callback(result);
        }

        // Watchdog for path gates
        if (waitingForPathResult && (DateTime.UtcNow - waitingSinceUtc).TotalSeconds > 3)
        {
            logger.LogWarning("[NAV] Path wait timeout; resetting pending/waiting gates.");
            waitingForPathResult = false;
            Interlocked.Exchange(ref pathRequestPending, 0);

            // Important: also clear the "who are we waiting for" id.
            Volatile.Write(ref waitingRequestId, 0);

            // Optional (recommended): do NOT clear activePathRequestId here.
            // Let the next enqueue overwrite it; clearing it here makes stale handling harder.
        }

        if (NavDebugDue() && routeToNextWaypoint.Count > 0 && wayPoints.Count > 0)
        {
            var end = routeToNextWaypoint.ToArray()[^1];
            logger.LogWarning($"[NAV-DBG] routeTop={routeToNextWaypoint.Peek()} routeEnd={end} wpTop={wayPoints.Peek()} endToWp={end.WorldDistanceXYTo(wayPoints.Peek()):0.00}");
        }

        // If we have a route but it's not actually routing to the current waypoint top,
        // it's stale. Clear it and force a refill.
        if (routeToNextWaypoint.Count > 0 && wayPoints.Count > 0)
        {
            bool insideBlacklist = AreaBlacklist?.ContainsWorld(Nav2D(playerReader.WorldPos)) == true;

            if (!insideBlacklist && !escapeActive && !escapeRouteInProgress)
            {
                // Don't stomp a route while we're actively waiting for a newer one.
                if (!waitingForPathResult && Volatile.Read(ref pathRequestPending) == 0)
                {
                    if (!RouteTargetsWaypointTop())
                    {
                        var arr = routeToNextWaypoint.ToArray();
                        var routeEnd = arr[arr.Length - 1];

                        logger.LogWarning(
                            $"[NAV] Stale route detected; clearing. " +
                            $"routeTop={routeToNextWaypoint.Peek()} routeEnd={routeEnd} wpTop={wayPoints.Peek()}");

                        routeToNextWaypoint.Clear();
                        UpdateTotalRoute();
                        ResetNoProgressWatchdog();
                    }
                }
            }
        }

        if (NavDebugDue())
        {
            logger.LogInformation(
                $"[NAV-SANITY] afterResults route={routeToNextWaypoint.Count} waypoints={wayPoints.Count} " +
                $"waiting={waitingForPathResult} pending={Volatile.Read(ref pathRequestPending)} " +
                $"reqQ={pathRequests.Count} resQ={pathResults.Count} threadTokenCancelled={token.IsCancellationRequested}");
        }


        int pending = Volatile.Read(ref pathRequestPending);

        // If pending is set, but we're not waiting and there is nothing queued,
        // the gate is stuck. Clear it so we can refill again.
        if (pending == 1 &&
            !waitingForPathResult &&
            pathRequests.Count == 0 &&
            pathResults.Count == 0)
        {
            logger.LogWarning("[NAV] pathRequestPending=1 but no queued/in-flight work. Clearing pending gate.");
            Interlocked.Exchange(ref pathRequestPending, 0);
        }


        // If we're "waiting", but there is no pending request and nothing queued, the flag is stale.
        if (waitingForPathResult && pending == 0 && pathRequests.Count == 0)
        {
            logger.LogWarning("[NAV] waitingForPathResult was TRUE but no request is pending/queued. Resetting.");
            waitingForPathResult = false;
        }

        if (token.IsCancellationRequested)
            return;

        // If we have a route already, KEEP MOVING even if a request is in flight.
        if (routeToNextWaypoint.Count > 0)
        {
            // continue to movement section
        }
        else
        {
            var now = DateTime.UtcNow;

            if (waitingForPathResult)
            {
                NavDbg("RETURN routeEmpty: waitingForPathResult=TRUE");
                return;
            }

            // NEW: hard backoff to prevent refill spam loops
            if (now < emptyRouteRefillCooldownUntilUtc)
            {
                return;
            }

            if (now < noProgressRefillCooldownUntilUtc)
            {
                NavDbg($"RETURN routeEmpty: noProgressRefillCooldown active {(noProgressRefillCooldownUntilUtc - now).TotalMilliseconds:0}ms");
                // Still apply a small backoff so we don't hammer this every tick
                emptyRouteRefillCooldownUntilUtc = now.AddMilliseconds(EmptyRouteRefillMinIntervalMs);
                return;
            }

            // Set backoff BEFORE calling refill so even early-exit inside refill won't spam
            emptyRouteRefillCooldownUntilUtc = now.AddMilliseconds(EmptyRouteRefillMinIntervalMs);

            NavDbg("routeEmpty: calling RefillRouteToNextWaypoint()");

            logger.LogWarning(
                $"[NAV-EMPTY] route=0 wp={wayPoints.Count} " +
                $"waiting={waitingForPathResult} pending={Volatile.Read(ref pathRequestPending)} " +
                $"reqQ={pathRequests.Count} resQ={pathResults.Count} " +
                $"noPathCdMs={(noPathCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
                $"blCdMs={(blacklistRejectCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
                $"npCdMs={(noProgressRefillCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
                $"pos={Nav2D(playerReader.WorldPos)} wpTop={(wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()).ToString() : "<none>")}");

            RefillRouteToNextWaypoint(token);

            NavDbg($"RETURN afterRefill: wp={wayPoints.Count} route={routeToNextWaypoint.Count} waiting={waitingForPathResult}");
            return;
        }

        // Comment this out due to having start forward later for heading/stuck logic
        //LastActive = DateTime.UtcNow;
        //input.StartForward(true);

        // main loop
        Vector3 playerW = Nav2D(playerReader.WorldPos);
        playerWorldPos = playerW;

        // If inside blacklist, do not progress along route points that are also blacklisted.
        // We want to escape first.
        if (AreaBlacklist != null && AreaBlacklist.TryGetContainingRect(Nav2D(playerReader.WorldPos), out _))
        {
            // If current next-step is inside blacklist, clear route so refill chooses escape.
            if (routeToNextWaypoint.Count > 0 && IsBlacklistedPoint(routeToNextWaypoint.Peek()))
            {
                routeToNextWaypoint.Clear();
                UpdateTotalRoute();
                return;
            }
        }

        Vector3 targetW = Nav2D(routeToNextWaypoint.Peek());
        if (routeToNextWaypoint.Count > 0 && Math.Abs(routeToNextWaypoint.Peek().Z) > 0.001f)
            logger.LogWarning($"[NAV] Z LEAK routeTop={routeToNextWaypoint.Peek()}");

        NavDbg($"MOVE target(routeTop)={targetW} player={playerReader.WorldPos} d={playerReader.WorldPos.WorldDistanceXYTo(targetW):0.00}");

        if (NavDebugDue())
        {
            Vector3 wpTop = wayPoints.Count > 0 ? wayPoints.Peek() : default;
            float distToWp = wayPoints.Count > 0 ? playerW.WorldDistanceXYTo(wpTop) : -1;
            float distToRoute = playerW.WorldDistanceXYTo(targetW);
            float dAnyWp = MinDistToAny(playerW, wayPoints);

            logger.LogWarning(
                $"[NAV-DBG]  dAnyWp={dAnyWp:0.00} player={playerW} routeTop={targetW} dRoute={distToRoute:0.00} " +
                $"wpTop={wpTop} dWp={distToWp:0.00} routeCount={routeToNextWaypoint.Count} wpCount={wayPoints.Count}");
        }

        if (noProgressSinceUtc != DateTime.MinValue && noProgressTargetW.WorldDistanceXYTo(targetW) > 0.05f)
        {
            // target changed -> reset watchdog state
            noProgressTargetW = targetW;
            noProgressBestDist = float.MaxValue;
            noProgressSinceUtc = DateTime.MinValue;
        }
        else if (noProgressSinceUtc == DateTime.MinValue)
        {
            // initialize target tracking the first time
            noProgressTargetW = targetW;
        }


        float worldDistance = playerW.WorldDistanceXYTo(targetW);
        
        if(NavDebugDue())
        {
            logger.LogInformation($"[NAV] chase player={playerW} target={targetW} dist={worldDistance:0.00} routeCount={routeToNextWaypoint.Count} wpCount={wayPoints.Count}");
        }


        // ---- ESCAPE FALLBACK WATCHDOG
        if (escapeActive && routeToNextWaypoint.Count > 0)
        {
            // Stop escape mode once we are outside
            if (AreaBlacklist?.ContainsWorld(Nav2D(playerReader.WorldPos)) != true)
            {
                // Still treat as escaping if we are near the boundary
                float edgeBuffer = DetourMargin + 6f; // tune: 6–15
                if (!IsNearBlacklistEdge(Nav2D(playerReader.WorldPos), edgeBuffer))
                {
                    escapeActive = false;
                }
            }
            else
            {
                float d = worldDistance;

                if (d + 0.05f < escapeBestDist)
                {
                    escapeBestDist = d;
                    escapeNoProgressSinceUtc = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - escapeNoProgressSinceUtc).TotalSeconds > 1.0 &&
                         !waitingForPathResult &&
                         Volatile.Read(ref pathRequestPending) == 0)
                {
                    logger.LogWarning(
                        $"[BL] Escape direct blocked; pathing to escape target {escapeTargetW}");

                    stopMoving.Stop();
                    EnqueuePathRequest(
                        playerReader.UIMapId.Value,
                        Nav2D(playerReader.WorldPos),
                        Nav2D(escapeTargetW),
                        d
                    );

                    escapeRouteInProgress = true;
                    escapeRouteEndW = Nav2D(escapeTargetW);

                    // keep escapeActive true until we're outside
                }
            }
        }

        // -------------------- NAV HYSTERESIS UPDATE GATE START --------------------
        var playerPos = playerW;

        // 1) Always try to pop route nodes first. This prevents “stuck near routeTop”.
        if (TryPopRouteTopIfReached(playerPos))
        {
            stopMoving.Stop();
            input.StopForward(true);

            // If we just consumed the last node of an escape route, exit escape mode and
            // allow normal waypoint routing to resume immediately.
            if (escapeRouteInProgress && routeToNextWaypoint.Count == 0)
            {
                escapeRouteInProgress = false;
                escapeActive = false;

                escapeBestDist = float.MaxValue;
                escapeNoProgressSinceUtc = DateTime.UtcNow;

                // allow immediate refill next tick
                noProgressRefillCooldownUntilUtc = DateTime.MinValue;

                // Also reset chase watchdog state so it doesn't instantly force another refill
                _chaseProgSinceUtc = DateTime.MinValue;
                _chaseLastMovedUtc = DateTime.MinValue;
                _chaseProgBestDist = float.MaxValue;
            }

            UpdateTotalRoute();
            ResetNoProgressWatchdog();

            // Retarget stuck detector to the new route top (or reset if none)
            if (routeToNextWaypoint.Count > 0)
                stuckDetector.SetTargetLocation(StuckOwnerId, Nav2D(routeToNextWaypoint.Peek()));
            else
                stuckDetector.Reset(StuckOwnerId);

            return;
        }

        if (routeToNextWaypoint.Count == 0)
        {
            if (routeToNextWaypoint.Count == 0 && wayPoints.Count > 0)
            {
                // Normalize Z=0 on the current waypoint so reach checks don't get stuck.
                Vector3 wpTopRaw = wayPoints.Peek();
                Vector3 wpTop = Nav2D(wpTopRaw);

                // If your TryPopWaypointTopIfReached() uses distance internally,
                // it may still be using 3D distance. So do a direct XY check here.
                float dWpXY = playerPos.WorldDistanceXYTo(wpTop);

                // Use your waypoint hysteresis / reach radius (tune as needed).
                float wpReach = ReachedDistance(OutDoorMinDistance) + 0.35f;

                if (dWpXY <= wpReach)
                {
                    var completed = Nav2D(wayPoints.Pop());
                    logger.LogWarning($"[NAV] POP WAYPOINT (XY reached) completed={completed} newWpTop={(wayPoints.Count > 0 ? wayPoints.Peek().ToString() : "<none>")} dWpXY={dWpXY:0.00}");

                    stopMoving.Stop();
                    input.StopForward(true);

                    routeToNextWaypoint.Clear();
                    UpdateTotalRoute();
                    ResetNoProgressWatchdog();

                    OnWayPointReached?.Invoke();
                    return;
                }
            }
        }
        // -------------------- NAV HYSTERESIS UPDATE GATE END --------------------

        // If we're not getting closer for a short period, force a route refresh.
        // Helps when route has 1 point but movement is "stalled" or jittering.
        if (routeToNextWaypoint.Count > 0)
        {
            // ------------------- CHASE PROGRESS WATCHDOG (MOVEMENT-AWARE) -------------------
            var now = DateTime.UtcNow;
            Vector3 chaseW = targetW;
            float dChase = playerW.WorldDistanceXYTo(chaseW);

            // Reset if chase target changes
            if (_chaseProgSinceUtc == DateTime.MinValue || _chaseProgTarget.WorldDistanceXYTo(chaseW) > 0.05f)
            {
                _chaseProgTarget = chaseW;
                _chaseProgBestDist = dChase;
                _chaseProgSinceUtc = now;

                _chaseLastPos = playerW;
                _chaseLastMovedUtc = now;
            }
            else
            {
                // Track whether we are physically moving (even if dChase temporarily increases)
                float moved = playerW.WorldDistanceXYTo(_chaseLastPos);
                if (moved > 0.75f) // tune: 0.5–1.5
                {
                    _chaseLastPos = playerW;
                    _chaseLastMovedUtc = now;
                }

                // Track best distance-to-chase (with a little slack)
                if (dChase + 0.10f < _chaseProgBestDist)
                {
                    _chaseProgBestDist = dChase;
                    _chaseProgSinceUtc = now;
                }

                double sinceBestSec = (now - _chaseProgSinceUtc).TotalSeconds;
                double sinceMoveSec = (now - _chaseLastMovedUtc).TotalSeconds;

                // Only declare "stuck" if:
                // 1) we haven't improved for a while AND
                // 2) we also haven't actually moved recently.
                //
                // This prevents downhill / switchback / turning cases from causing replans.
                if (sinceBestSec > 4.0 && sinceMoveSec > 1.5 &&
                    !waitingForPathResult && Volatile.Read(ref pathRequestPending) == 0)
                {
                    logger.LogWarning(
                        $"[NAV] No chase progress while stationary. " +
                        $"player={playerW} chase={chaseW} dChase={dChase:0.00} best={_chaseProgBestDist:0.00} " +
                        $"sinceBest={sinceBestSec:0.00}s sinceMove={sinceMoveSec:0.00}s");

                    // 1) Try actual unstuck FIRST (turn/move/jump), rate-limited.
                    if (now >= _chaseUnstuckCooldownUntilUtc)
                    {
                        stopMoving.Stop();

                        // make sure stuck detector is aiming at the same chase target
                        stuckDetector.SetTargetLocation(StuckOwnerId, Nav2D(chaseW));
                        stuckDetector.Update(StuckOwnerId, token);

                        // give the unstuck routine time to play out before we try again
                        _chaseUnstuckCooldownUntilUtc = now.AddMilliseconds(1400);

                        // reset movement tracking so we don't instantly re-trigger
                        _chaseLastPos = playerW;
                        _chaseLastMovedUtc = now;

                        return;
                    }

                    // 2) Only if we've been stuck for a while, replan (route refresh).
                    // This avoids infinite "replan spam" when we're physically wedged on terrain.
                    if (sinceBestSec > 6.0 && now >= noProgressRefillCooldownUntilUtc)
                    {
                        noProgressRefillCooldownUntilUtc = now.AddMilliseconds(1500);

                        stopMoving.Stop();
                        routeToNextWaypoint.Clear();
                        UpdateTotalRoute();

                        ResetNoProgressWatchdog();
                        ResetChaseProgressWatchdog(); // important: don't keep inherited timers
                        return;
                    }
                }
            }
        }

        Vector3 playerM = WorldMapAreaDB.ToMap_FlipXY(playerW, playerReader.WorldMapArea);
        Vector3 targetM = WorldMapAreaDB.ToMap_FlipXY(targetW, playerReader.WorldMapArea);
        float heading = DirectionCalculator.CalculateMapHeading(playerM, targetM);

        // NOTE: Reached/popping is handled by the hysteresis gate above.
        // Keep this block ONLY for Z-sync / route simplification if you still want it.
        if (ShouldTreatAsReached(playerW, targetW, OutDoorMinDistance))
        {
            // IMPORTANT: do NOT force player Z to match the route node.
            // On slopes / stairs this causes oscillation when route nodes have different Z.
            //if (targetW.Z != 0 && targetW.Z != playerW.Z)
            //{
            //    playerReader.WorldPosZ = targetW.Z;
            //}

            if (SimplifyRouteToWaypoint)
            {
                // Disable so we can test navigation hysteresis
                //ReduceByDistance(playerW, OutDoorMinDistance);
                UpdateTotalRoute();
            }

            // Do NOT pop here anymore.
        }

        if (routeToNextWaypoint.Count == 0)
        {
            ResetStuckParameters();

            if (!waitingForPathResult)

            logger.LogWarning(
                    $"[NAV-EMPTY] route=0 wp={wayPoints.Count} " +
                    $"waiting={waitingForPathResult} pending={Volatile.Read(ref pathRequestPending)} " +
                    $"reqQ={pathRequests.Count} resQ={pathResults.Count} " +
                    $"noPathCdMs={(noPathCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
                    $"blCdMs={(blacklistRejectCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
                    $"npCdMs={(noProgressRefillCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
                    $"pos={Nav2D(playerReader.WorldPos)} wpTop={(wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()).ToString() : "<none>")}");

            RefillRouteToNextWaypoint(token);
            return;
        }

        // If we're inside STOP_DIST of the chase, stop driving forward and let the pop gate advance next tick.
        if (!ShouldMoveToward(playerW, targetW))
        {
            stopMoving.Stop();
            input.StopForward(true);
            return;
        }

        // once we get here, always drive movement this tick
        LastActive = DateTime.UtcNow;
        input.StartForward(true);

        if (routeToNextWaypoint.Count > 0)
        {
            // CRITICAL: always sync stuck detector to the current chase target
            stuckDetector.SetTargetLocation(StuckOwnerId, targetW);

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
                    stuckDetector.Reset(StuckOwnerId);
                    routeToNextWaypoint.Clear();
                    return;
                }

                if (HasBeenActiveRecently())
                {
                    stuckDetector.Update(StuckOwnerId, token);
                    worldDistance = playerW.WorldDistanceXYTo(targetW);
                }
            }
        }

        lastWorldDistance = worldDistance;
    }

    public void Resume()
    {
        active = true;
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

    // Helper: record anchor only if it's non-default and not identical (avoid spam)
    private void SetLastSafeAnchor(Vector3 w)
    {
        if (w == default)
            return;

        // Keep it stable; don’t thrash for tiny noise
        if (lastSafeAnchorW.WorldDistanceXYTo(w) < 0.05f)
            return;

        lastSafeAnchorW = w;
    }

    public void ClearAllRoutes()
    {
        routeToNextWaypoint.Clear();
        // Do not clear wayPoints here; caller may be doing a "temporary" move.
        UpdateTotalRoute();
    }

    public void SetSingleWaypoint(Vector3 worldOrMapPoint)
    {
        // Reuse your SetWayPoints conversion logic.
        Span<Vector3> tmp = stackalloc Vector3[1] { worldOrMapPoint };
        SetWayPoints(tmp);
    }

    public void PausePathing()
    {
        active = false;

        // Don’t stomp request gates; a request may be in flight.
        // waitingForPathResult can stay true/false; it won’t matter while inactive.

        // Ensure we don't deadlock on a dropped/in-flight request while paused.
        // Any result that comes back will still be ignored if !active, but gates will clear.
        waitingForPathResult = false;
        Interlocked.Exchange(ref pathRequestPending, 0);
        Volatile.Write(ref waitingRequestId, 0);

        // Freeze stuck behavior cleanly
        stuckDetector.Release(StuckOwnerId);

        // Keep route/waypoints so Resume() can continue exactly where it left off.
    }

    public void Stop()
    {
        active = false;
        stuckDetector.Release(StuckOwnerId);

        // Invalidate any in-flight path results
        Volatile.Write(ref activePathRequestId, Interlocked.Increment(ref nextPathRequestId));
        waitingForPathResult = false;

        if (pather.GetType() == typeof(RemotePathingAPIV3))
            routeToNextWaypoint.Clear();

        ResetStuckParameters();
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

    public Vector3 NextMapPoint()
    {
        return WorldMapAreaDB.ToMap_FlipXY(Nav2D(routeToNextWaypoint.Peek()), playerReader.WorldMapArea);
    }

    public void  SetWayPoints(Span<Vector3> points)
    {
        active = true;
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

        UpdateTotalRoute();

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

        // If you're near any rect (inside inflated by extra), treat as still "edge zone"
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

        // Don’t log "called" every frame; only log occasionally.
        LogRefill("[NAV] RefillRouteToNextWaypoint tick");

        if (routeToNextWaypoint.Count > 0)
        {
                LogRefill($"[NAV] Refill: exit (routeToNextWaypoint.Count={routeToNextWaypoint.Count})");
                RefillExit("routeAlreadyNonEmpty");
                _phase = "exit_routeAlreadyNonEmpty";
                
                goto REFILL_EXIT;
                //return;
        }

        if (wayPoints.Count == 0)
        {
                _phase = "exit_noWaypoints";
                LogRefill("[NAV] Refill: exit (no waypoints)");
                RefillExit("noWaypoints");
                UpdateTotalRoute();
                OnDestinationReached?.Invoke();
                
                goto REFILL_EXIT;
                //return;
            }

        if (DateTime.UtcNow < noPathCooldownUntilUtc) 
        {   
                LogRefill("[NAV] Refill: exit (no path rejection cooldown)");
                RefillExit("noPathCooldown");
                _phase = "exit_noPathCooldown";
                goto REFILL_EXIT;
                //return;
        }

        if (DateTime.UtcNow < blacklistRejectCooldownUntilUtc)
        {
                LogRefill("[NAV] Refill: exit (blacklist rejection cooldown)");
                RefillExit("blacklistRejectCooldown");
                _phase = "exit_blacklistRejectCooldown";
                goto REFILL_EXIT;
                //return;
        }
        
        // Strong gating: if a request is in flight, do NOT try again
        if (waitingForPathResult || Volatile.Read(ref pathRequestPending) == 1)
        {
                _phase = "exit_requestInFlight_gate";
                RefillExit("requestInFlight_gate");
                goto REFILL_EXIT;
                //return;
        }
            

            routeToNextWaypoint.Clear();

            Vector3 startW = Nav2D(playerReader.WorldPos);
        
            // Escape-mode guard — do not plan normal routing while escapeActive
            if (escapeActive)
            {
                // If we are already safely outside + away from edge, allow escape mode to end.
                // (Optional, but recommended once you add TryGetContainingRectInflated)
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
            
                // IMPORTANT:
                // Keep the "near edge" stabilization behavior, BUT never deadlock.
                // If we have NO escape route node to chase, we must NOT "hold" here.
                // Instead, drop escapeActive and continue normal routing.
                if (escapeActive)
                {
                    if (routeToNextWaypoint.Count == 0)
                    {
                        // No escape chase target -> do NOT hold; proceed with normal planning
                        escapeActive = false;
                    }
                    else
                    {
                         RefillExit("escapeActive_hold");
                         _phase = "exit_escapeActive_hold";
                         goto REFILL_EXIT;
                    }
                }
            }


        // If we are currently inside a forbidden rect, escape FIRST.
        // Escape-first: if inside any blacklist, generate an escape step NOW.
        if (TryComputeEscapeOutOfBlacklist(startW, out var escapeW))
        {
            escapeActive = true;
            escapeTargetW = Nav2D(escapeW);
            escapeBestDist = float.MaxValue;
            escapeNoProgressSinceUtc = DateTime.UtcNow;

            // --- RATE-LIMIT GUARD (PLACE IT HERE) ---
            if (lastEscapePoint.WorldDistanceXYTo(escapeW) < 0.1f &&
                escapeInsertCooldownTicks > 0)
            {
                    RefillExit("sameEscapeRecentlyInserted");
                    _phase = "exit_sameEscapeRecentlyInserted";
                    goto REFILL_EXIT;
                    //return; // same escape recently inserted; do nothing this tick
            }

            // Update rate-limit state
            lastEscapePoint = escapeW;
            escapeInsertCooldownTicks = 10; // ~10 frames (tune as needed)

            // If escape point is basically where we are, avoid thrashing
            if (startW.WorldDistanceXYTo(escapeW) < ReachedDistance(OutDoorMinDistance))
            {
                    logger.LogWarning($"[BL] Escape computed but too close; start={startW} escape={escapeW}");
                    // You could increase margin here, or just return to avoid spam.
                    RefillExit("escapePointAvoidThrashing");
                    _phase = "exit_escapePointAvoidThrashing";
                    goto REFILL_EXIT;
                    //return;
            }

                // Ensure it becomes the immediate movement target this frame
                routeToNextWaypoint.Clear();
                routeToNextWaypoint.Push(Nav2D(escapeW));
                stuckDetector.SetTargetLocation(StuckOwnerId, Nav2D(escapeW));

                // Mark this as an escape route so popping the last node can terminate escape mode
                escapeRouteInProgress = true;
                escapeRouteEndW = Nav2D(escapeW);

                UpdateTotalRoute();
                logger.LogInformation($"[BL] Escape-first ROUTE set: {startW} -> {escapeW}");
                RefillExit("escapeRouteSet");
                _phase = "exit_escapeRouteSet";
                goto REFILL_EXIT;
            }

        // now gate the normal routing behavior
        int pending = Volatile.Read(ref pathRequestPending);
        if (waitingForPathResult || pending == 1)
        {
                LogRefill($"[NAV] Refill: exit (waitingForPathResult={waitingForPathResult}, pathRequestPending={pending})");
                RefillExit("waitingForPathResultOrPendingTrue");
                _phase = "exit_waitingForPathResultOrPendingTrue";
                goto REFILL_EXIT;
                //return;
            }


        if (!SkipBlacklistedWaypoints())
        {
                RefillExit("SkipBlacklistedWaypoints_false_destinationReached");
                logger.LogInformation("[NAV] Refill: SkipBlacklistedWaypoints returned false -> destination reached");
                UpdateTotalRoute();
                OnDestinationReached?.Invoke();
                _phase = "exit_SkipBlacklistedWaypoints_false_destinationReached";
                goto REFILL_EXIT;
                //return;
            }

        Vector3 targetRaw = wayPoints.Peek();
        Vector3 targetW = Nav2D(wayPoints.Peek());

        float distance = startW.WorldDistanceXYTo(targetW);

        logger.LogWarning(
            $"[NAV-DBG] REFILL start={startW} wpTop(raw)={targetRaw} wpTop(norm)={targetW} " +
            $"dist={distance:0.00} usePather? TBD wpCount={wayPoints.Count}");

        // -------------------- WAYPOINT HYSTERESIS: POP IF ALREADY REACHED --------------------
        // If we're already within reach distance of the waypoint, don't try to build a route.
        // Pop it and let the next Update/Refill tick move on.
        float wpReached = ReachedDistance(OutDoorMinDistance);   // uses your existing method

        // Add a little hysteresis so we don't thrash near the boundary
        float wpPopThreshold = MathF.Max(wpReached + 0.35f, POP_DIST - 0.05f);                // tune 0.25–0.75
        if (distance <= wpPopThreshold)
        {
            var completed = wayPoints.Peek();
            wayPoints.Pop();

            logger.LogWarning(
                $"[NAV] REFILL: waypoint already reached -> POP {completed} " +
                $"newWpTop={(wayPoints.Count > 0 ? wayPoints.Peek().ToString() : "<none>")} " +
                $"d={distance:0.00} thr={wpPopThreshold:0.00}");

            // Clear any stale route just in case and reset progress tracking
            routeToNextWaypoint.Clear();
            ResetNoProgressWatchdog();
            UpdateTotalRoute();

            OnWayPointReached?.Invoke();

            // If that was the last waypoint, destination reached
            if (wayPoints.Count == 0)
                {
                    OnDestinationReached?.Invoke();

                    RefillExit("wpAlreadyReached_pop");
                    _phase = "exit_wpAlreadyReached_pop";
                    goto REFILL_EXIT;
                    //return;
                }
            }

        // If you removed “directBlocked forcing pather”, then you may never enqueue.
        // Decide your policy:
        bool usePather = distance > MaxDistance || distance > AvgDistance * 2;

        // Or if you still have blacklist rectangles, you probably want this too:
        BlacklistRect r = default;
        bool directBlocked = AreaBlacklist != null &&
                             TryGetBlockingRectEscapeAware(startW, targetW, out r);

        if (directBlocked)
        {
            // Try a cheap local detour first.
            if (TryInsertDetour(startW, targetW, r))
            {
                    logger.LogInformation($"[NAV] Refill: detour inserted for blocked segment start={startW} end={targetW}");
                    RefillExit("tryForwardDetour");
                    _phase = "exit_tryForwardDetour";
                    goto REFILL_EXIT;
                    //return; // next Update tick will refill using the new top-of-stack detour
                }

            // If detour failed, fall back to the pather.
            logger.LogInformation($"[NAV] Refill: detour failed -> forcing pather start={startW} end={targetW}");
            usePather = true;
        }

        if (usePather)
        {
                _phase = "exit_enqueuePathRequest";
                stopMoving.Stop();

                logger.LogWarning(
                    $"[NAV-DBG] ENQUEUE PATH start={startW} end(targetWp raw)={targetRaw} end(targetWp norm)={targetW} dist={distance:0.00}");

                NavDbg($"REFILL enqueuePath start={startW} end={targetW} dist={distance:0.00}");

                EnqueuePathRequest(playerReader.UIMapId.Value, startW, targetW, distance);
                RefillExit("enqueuePathRequest");
                goto REFILL_EXIT;
                //return;
        }

        // else direct:
        // Add a midpoint for long direct segments to reduce jitter on single-point movement.
        Vector3 mid = default;
        bool hasMid = false;

        if (distance > 8f)
        {
            mid = new Vector3(
                (startW.X + targetW.X) * 0.5f,
                (startW.Y + targetW.Y) * 0.5f,
                startW.Z   // keep in same plane
            );

            hasMid = !IsBlacklistedPoint(mid) && !SegmentBlockedEscapeAware(startW, mid);
        }

        // Push final target first, then optional mid so mid is on top (next step).
        routeToNextWaypoint.Push(Nav2D(targetW));

        if (hasMid)
            routeToNextWaypoint.Push(Nav2D(mid));

        noProgressBestDist = float.MaxValue;
        noProgressSinceUtc = DateTime.MinValue;
        noProgressTargetW = routeToNextWaypoint.Count > 0 ? routeToNextWaypoint.Peek() : default;

        if (routeToNextWaypoint.Count > 0)
        {
            var chase = Nav2D(routeToNextWaypoint.Peek());
            stuckDetector.SetTargetLocation(StuckOwnerId, Nav2D(chase));
            ResetChaseProgressWatchdog(chase);
        }

        UpdateTotalRoute();
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

        // Only one request at a time. If another thread already set it, we won't enqueue.
        if (Interlocked.CompareExchange(ref pathRequestPending, 1, 0) != 0)
            return false;

        requestId = Interlocked.Increment(ref nextPathRequestId);

        // These must be set AFTER we successfully acquired the pending gate.
        Volatile.Write(ref activePathRequestId, requestId);
        Volatile.Write(ref waitingRequestId, requestId);

        lastRequestStart = startW;
        lastRequestEnd = endW;

        return true;
    }

    private void EndPathRequest(long requestId)
    {
        long activeId = Volatile.Read(ref activePathRequestId);

        // Only clear if this is the currently-active request.
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

        // allow an unstuck immediately after a route/target change
        _chaseUnstuckCooldownUntilUtc = DateTime.MinValue;
    }

    private void ResetNoProgressWatchdog(Vector3? newTarget = null)
    {
        noProgressBestDist = float.MaxValue;
        noProgressSinceUtc = DateTime.MinValue;
        noProgressTargetW = newTarget ?? default;
    }

    private void PathCalculatedCallback(long requestId, PathResult result)
    {
        long activeId = Volatile.Read(ref activePathRequestId);
        long waitingId = Volatile.Read(ref waitingRequestId);

        // Primary: only accept the currently active request id.
        if (activeId != requestId)
        {
            logger.LogInformation(
                $"[NAV] Ignoring stale path result reqId={requestId} waitingReqId={waitingId} activeReqId={activeId}");
            return;
        }

        // We are processing the active request -> clear "waiting" markers now.
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
            // throttle refills so we don’t enqueue again the same frame
            noPathCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(350);

            // Let higher-level logic react (assist return rewind, backoff, etc.)
            OnPathFailed?.Invoke(result.StartW, result.EndW);

            if (lastFailedDestination != result.EndW)
            {
                lastFailedDestination = result.EndW;
                LogPathfinderFailed(logger, result.StartW, result.EndW, result.ElapsedMs);
            }

            failedAttempt++;
            if (failedAttempt > 2)
            {
                failedAttempt = 0;
                stuckDetector.SetTargetLocation(StuckOwnerId, Nav2D(result.EndW));
                stuckDetector.Update(StuckOwnerId);
            }
            return;
        }

        failedAttempt = 0;

        // (Keep your blacklist validation here if needed)
        // ...

        LogPathfinderSuccess(logger, result.Distance, result.StartW, result.EndW, result.ElapsedMs);

        const float MIN_FIRST_STEP_DIST = 1.0f;

        routeToNextWaypoint.Clear();

        Vector3 start2D = Nav2D(result.StartW);
        Vector3 end2D = Nav2D(result.EndW);

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

        // ----------------------------------------
        // Validate pather route against blacklist
        // ----------------------------------------
        if (AreaBlacklist != null)
        {
            bool startedInside = AreaBlacklist.ContainsWorld(Nav2D(result.StartW));

            // Only enforce when starting OUTSIDE
            if (!startedInside)
            {
                // Optional: avoid rejecting while near the edge (prevents oscillation)
                bool nearEdge = AreaBlacklist.TryGetContainingRectInflated(
                    Nav2D(result.StartW),
                    DetourMargin + 6f,
                    out _
                );

                // If near edge, allow path (escape logic handles it)
                if (!nearEdge)
                {
                    var steps = routeToNextWaypoint.ToArray();
                    Array.Reverse(steps); // travel order

                    Vector3 prev = Nav2D(result.StartW);

                    for (int i = 0; i < steps.Length; i++)
                    {
                        Vector3 cur = steps[i];

                        // 1. Point check (cheap)
                        if (AreaBlacklist.ContainsWorld(cur))
                        {
                            logger.LogWarning(
                                $"[BL] Path node inside blacklist; rejecting. " +
                                $"badPoint={cur}");

                            // Try to change the problem by inserting a detour around the offending rect
                            if (TryInsertDetourFromRejectedPath(result.StartW, result.EndW, cur))
                            {
                                sameRejectCount = 0;
                                lastRejectStartW = default;
                                lastRejectEndW = default;

                                // small cooldown so we don't refill-spam same tick
                                blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
                                ResetNoProgressWatchdog();
                                return; // next Update will refill with the new detour waypoint on top
                            }

                            // Detour insertion failed -> use reject-counter fallback to avoid deadlock
                            if (HandleBlacklistReject(result))
                            {
                                ResetNoProgressWatchdog();
                                return;
                            }

                            // If detour insertion failed, still backoff to avoid tight loop spam
                            blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(500);

                            routeToNextWaypoint.Clear();
                            UpdateTotalRoute();
                            ResetNoProgressWatchdog();
                            return;
                        }

                        // 2. Segment check (critical)
                        if (SegmentBlockedEscapeAware(prev, cur))
                        {
                            logger.LogWarning(
                                $"[BL] Path segment crosses blacklist; rejecting. " +
                                $"seg=({prev} -> {cur})");

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

                            // Detour insertion failed -> use reject-counter fallback to avoid deadlock
                            if (HandleBlacklistReject(result))
                            {
                                ResetNoProgressWatchdog();
                                return;
                            }

                            blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(500);

                            routeToNextWaypoint.Clear();
                            UpdateTotalRoute();
                            ResetNoProgressWatchdog();
                            return;
                        }

                        prev = cur;
                    }
                }
            }
        }

        if (routeToNextWaypoint.Count == 0)
        {
            UpdateTotalRoute();
            // Optional: treat as failed so caller can react / refill.
            noPathCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(350);
            OnPathFailed?.Invoke(result.StartW, result.EndW);
            return;
        }

        var chase = Nav2D(routeToNextWaypoint.Peek());
        stuckDetector.SetTargetLocation(StuckOwnerId, chase);

        // IMPORTANT: route changed -> reset chase watchdog so it doesn't inherit old "stuck" timers
        ResetChaseProgressWatchdog(chase);

        ResetNoProgressWatchdog(chase);
        UpdateTotalRoute();

        emptyRouteRefillCooldownUntilUtc = DateTime.MinValue;

        OnPathCalculated?.Invoke();
    }

    private bool TryInsertDetourFromRejectedPath(Vector3 startW, Vector3 endW, Vector3 badPointW)
    {
        if (AreaBlacklist == null)
            return false;

        // We only support detouring the current goal (top waypoint)
        if (wayPoints.Count == 0 || wayPoints.Peek().WorldDistanceXYTo(endW) > 0.1f)
            return false;

        // Find which rect caused the rejection
        if (!AreaBlacklist.TryGetContainingRect(Nav2D(badPointW), out var rect))
            return false;

        float m = DetourMargin;
        float z = 0f;

        // Generate candidate points around the rect boundary
        var inflated = rect.Inflate(m * 0.5f);

        Vector3 best = default;
        float bestScore = float.MaxValue;
        bool found = false;

        foreach (var d in BuildDetourCandidates(inflated, m, z))
        {
            if (IsBlacklistedPoint(d)) continue;

            // Critical: don't cross forbidden region
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

        // Insert detour BEFORE the current waypoint
        Vector3 target = Nav2D(wayPoints.Pop()); // == endW
        wayPoints.Push(Nav2D(target));
        wayPoints.Push(Nav2D(best));

        routeToNextWaypoint.Clear();
        UpdateTotalRoute();

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
            // short cooldown so we don't insta-thrash
            blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
            return false; // did NOT skip waypoint
        }

        logger.LogError($"[BL] Rejected same path {sameRejectCount} times. Skipping waypoint {result.EndW} to avoid deadlock.");

        sameRejectCount = 0;

        // Drop the unreachable waypoint (only if it's the current target)
        if (wayPoints.Count > 0 && wayPoints.Peek().WorldDistanceXYTo(result.EndW) < 0.1f)
            wayPoints.Pop();

        routeToNextWaypoint.Clear();
        UpdateTotalRoute();

        // longer cooldown to avoid thrash
        blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddSeconds(1);

        return true; // skipped waypoint and we already cleaned up; caller must return
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

                    // IMPORTANT:
                    // Always enqueue the result so PathCalculatedCallback runs and clears
                    // waitingForPathResult / pathRequestPending gates.
                    // The callback itself already bails out if !active, but it clears gates first.
                    pathResults.Enqueue(new PathResult(pathRequest, path, pathRequest.Callback));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[NAV] PathFinderThread exception; returning empty path");
                    pathResults.Enqueue(new PathResult(pathRequest, Array.Empty<Vector3>(), pathRequest.Callback));
                }
            }

            if (pathRequests.IsEmpty && Volatile.Read(ref pathRequestPending) == 1 && waitingForPathResult)
            {
                logger.LogError("[NAV] Path gate stuck: pending=1 waiting=true but request queue is empty.");
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

        // Record the furthest progressed point this tick as anchor
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

            // NEW: if the current route doesn't end near the current waypoint, it is stale.
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
        //Console.WriteLine("IsInBlacklistArea AreaBlacklist: " + AreaBlacklist);
        bool inside = AreaBlacklist?.ContainsWorld(Nav2D(playerReader.WorldPos)) == true;

        /*
        logger.LogInformation(
            $"[NAV] IsInBlacklistArea navHash={GetHashCode()} " +
            $"blacklistNull={AreaBlacklist is null} " +
            $"blacklistHash={(AreaBlacklist?.GetHashCode().ToString() ?? "null")} " +
            $"pos={playerReader.WorldPos} inside={inside}");
        */
        return inside;
    }

    private bool IsBlacklistedPoint(Vector3 worldPoint)
    => AreaBlacklist != null && AreaBlacklist.ContainsWorld(worldPoint);

    /// <summary>
    /// Returns true if the segment should be blocked by blacklists,
    /// with "allow leaving only" behavior:
    /// - if start is inside a rect and end is outside that same rect, crossing that rect boundary is allowed
    /// - but intersections with OTHER rects are still blocked.
    /// </summary>
    private bool TryGetBlockingRectEscapeAware(Vector3 startW, Vector3 endW, out BlacklistRect blockingRect)
    {
        startW = Nav2D(startW);
        endW = Nav2D(endW);

        blockingRect = default;
        if (AreaBlacklist == null) return false;

        // If we're inside a rect and moving to a point outside that rect, allow leaving that rect.
        if (AreaBlacklist.TryGetContainingRect(startW, out var containingRect))
        {
            var endInsideSame = containingRect.Contains(new Vector2(endW.X, endW.Y));
            if (!endInsideSame)
            {
                // Ignore the containing rect; still block if we intersect any other rect.
                return AreaBlacklist.TryGetBlockingRectExcluding(startW, endW, containingRect, out blockingRect);
            }
        }

        // Normal rule
        return AreaBlacklist.TryGetBlockingRect(startW, endW, out blockingRect);
    }

    private bool SegmentBlockedEscapeAware(Vector3 startW, Vector3 endW)
        => TryGetBlockingRectEscapeAware(startW, endW, out _);

    private bool SkipBlacklistedWaypoints()
    {
        if (AreaBlacklist == null) return wayPoints.Count > 0;

        // Escape-first handles "inside" behavior
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
        // 3 samples per side (25%, 50%, 75%)
        float[] t = { 0.25f, 0.5f, 0.75f };

        foreach (var u in t)
        {
            float x = Lerp(r.MinX, r.MaxX, u);
            float y = Lerp(r.MinY, r.MaxY, u);

            yield return new Vector3(r.MinX - m, y, z); // left
            yield return new Vector3(r.MaxX + m, y, z); // right
            yield return new Vector3(x, r.MinY - m, z); // bottom
            yield return new Vector3(x, r.MaxY + m, z); // top
        }

        // plus corners (still useful)
        yield return new Vector3(r.MinX - m, r.MinY - m, z);
        yield return new Vector3(r.MaxX + m, r.MinY - m, z);
        yield return new Vector3(r.MaxX + m, r.MaxY + m, z);
        yield return new Vector3(r.MinX - m, r.MaxY + m, z);
    }

    private bool ShouldTreatAsReached(Vector3 playerW, Vector3 targetW, float baseMinDistance)
    {
        float dist = playerW.WorldDistanceXYTo(targetW);

        // Base reach distance (your existing logic)
        float reach = ReachedDistance(baseMinDistance);

        // If this is the very last step, be a bit more lenient.
        if (routeToNextWaypoint.Count == 1)
            reach *= 1.25f; // tweak: 1.15–1.5 depending on how jittery things are

        // Hysteresis parameters
        float release = reach * 1.6f;                    // how far we allow drifting after latching
        long latchWindowTicks = TimeSpan.FromSeconds(1).Ticks; // how long the latch stays valid

        // Reset latch if target changes
        if (!SamePointXY(reachLatchTarget, targetW))
        {
            reachLatchTarget = targetW;
            reachLatched = false;
            reachLatchUntilTicks = 0;
        }

        // If we're within reach, latch it.
        if (dist <= reach)
        {
            reachLatched = true;
            reachLatchUntilTicks = DateTime.UtcNow.Ticks + latchWindowTicks;
            return true;
        }

        // If we latched recently, allow reaching as long as we're not too far away.
        if (reachLatched && DateTime.UtcNow.Ticks <= reachLatchUntilTicks && dist <= release)
        {
            return true;
        }

        // Latch expired or too far
        if (DateTime.UtcNow.Ticks > reachLatchUntilTicks)
            reachLatched = false;

        return false;

        static bool SamePointXY(Vector3 a, Vector3 b)
            => a.WorldDistanceXYTo(b) < 0.05f; // tolerance, tune if needed
    }

    private bool TryInsertDetour(Vector3 startW, Vector3 targetW, BlacklistRect blockingRect)
    {
        // Loop protection per-target
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

        // Slightly inflate the rect to give extra clearance when generating points
        var inflated = blockingRect.Inflate(m * 0.5f); // smaller than m

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
            => a.WorldDistanceXYTo(b) < 0.01f; // or whatever tolerance you like

        // Inject detour before target (stack top)
        if (wayPoints.Count == 0 || !SamePoint(wayPoints.Peek(), targetW))
        {
            logger.LogInformation($"[BL] detour aborted (target changed): start={startW} target={targetW}");
            return false;
        }

        wayPoints.Pop();          // remove target
        wayPoints.Push(Nav2D(targetW));  // restore target
        wayPoints.Push(Nav2D(best));     // detour is next

        routeToNextWaypoint.Clear(); // force recalculation immediately
        UpdateTotalRoute();

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

        // Prefer escaping toward the next real goal if we have one
        Vector3 goalW = wayPoints.Count > 0 ? wayPoints.Peek() : startW;

        for (int i = 0; i < 8; i++)
        {
            if (!AreaBlacklist.TryGetContainingRect(escapeW, out rect))
                return true; // already outside

            // Choose the best candidate exit around THIS rect
            if (!TryPickEscapeCandidate(escapeW, goalW, rect, m, z, out var picked))
                return false;

            escapeW = picked;
            // loop continues in case of overlapping rects
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

        // -------- 1) NEAREST-EDGE CANDIDATES (shortest-first) --------
        var sides = GetSidesByDistance(fromW, rect);
        int sidesToTry = Math.Min(2, sides.Length); // try closest 2 sides (good near corners)

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
            return true; // first viable (shortest) wins
        }

        // -------- 2) FALLBACK: BROADER CANDIDATES AROUND RECT (shortest-first) --------
        var broad = new List<Vector3>(64);
        foreach (var c in BuildDetourCandidates(inflated, margin, z))
            broad.Add(c);

        DedupByXY(broad, 0.05f);

        //broad.Sort((a, b) => fromW.WorldDistanceXYTo(a).CompareTo(fromW.WorldDistanceXYTo(b)));
        // Sort to prefer exits that point towards the route
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
        // Sample a few points along the nearest edge around the current coordinate.
        // These are "short" exits that don't require crossing the rect.
        const float band = 3.0f; // tune: 2–5
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

        // Add corners on that side as last-resort near-edge options
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



    private static Vector3 EscapePointFromRect(Vector3 posW, BlacklistRect rect, float margin, float z)
    {
        float x = posW.X;
        float y = posW.Y;

        float dLeft = MathF.Abs(x - rect.MinX);
        float dRight = MathF.Abs(rect.MaxX - x);
        float dBottom = MathF.Abs(y - rect.MinY);
        float dTop = MathF.Abs(rect.MaxY - y);

        // choose nearest side
        float minD = dLeft;
        int side = 0; // 0=left,1=right,2=bottom,3=top
        if (dRight < minD) { minD = dRight; side = 1; }
        if (dBottom < minD) { minD = dBottom; side = 2; }
        if (dTop < minD) { minD = dTop; side = 3; }

        return side switch
        {
            0 => new Vector3(rect.MinX - margin, Clamp(y, rect.MinY, rect.MaxY), z),
            1 => new Vector3(rect.MaxX + margin, Clamp(y, rect.MinY, rect.MaxY), z),
            2 => new Vector3(Clamp(x, rect.MinX, rect.MaxX), rect.MinY - margin, z),
            _ => new Vector3(Clamp(x, rect.MinX, rect.MaxX), rect.MaxY + margin, z),
        };
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