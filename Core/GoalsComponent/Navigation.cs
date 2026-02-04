using Core.GOAP;

using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.Extensions;

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

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

    private const float MinDistanceMount = 10;
    private readonly float MaxDistance = 200;
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

    public bool SimplifyRouteToWaypoint { get; set; } = true;

    private bool active;
    private Vector3 playerWorldPos;

    private readonly Queue<PathRequest> pathRequests = new(1);
    private readonly Queue<PathResult> pathResults = new(1);

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

    public Navigation(ILogger<Navigation> logger,
        CancellationTokenSource<GoapAgent> cts,
        PlayerDirection playerDirection,
        ConfigurableInput input,
        PlayerReader playerReader, AddonBits bits,
        StopMoving stopMoving,
        StuckDetector stuckDetector, IPPather pather, IMountHandler mountHandler,
        ClassConfiguration classConfiguration)
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
    }

    public void Dispose()
    {
        manualReset.Set();
    }

    public void Update()
    {
        Update(token);
    }

    public void Update(CancellationToken token)
    {
        active = true;

        if (escapeInsertCooldownTicks > 0)
            escapeInsertCooldownTicks--;

        if (wayPoints.Count == 0 && routeToNextWaypoint.Count == 0)
        {
            OnDestinationReached?.Invoke();
            return;
        }

        // Drop any waypoints that are inside forbidden regions
        SkipBlacklistedWaypoints();
        if (wayPoints.Count == 0 && routeToNextWaypoint.Count == 0)
        {
            OnDestinationReached?.Invoke();
            return;
        }

        while (pathResults.TryDequeue(out PathResult result))
        {
            result.Callback(result);
        }

        if (token.IsCancellationRequested || waitingForPathResult)
            return;

        if (routeToNextWaypoint.Count == 0)
        {
            RefillRouteToNextWaypoint(token);
            return;
        }

        LastActive = DateTime.UtcNow;
        input.StartForward(true);

        // main loop
        Vector3 playerW = playerReader.WorldPos;
        playerWorldPos = playerW;

        // If inside blacklist, do not progress along route points that are also blacklisted.
        // We want to escape first.
        if (AreaBlacklist != null && AreaBlacklist.TryGetContainingRect(playerReader.WorldPos, out _))
        {
            // If current next-step is inside blacklist, clear route so refill chooses escape.
            if (routeToNextWaypoint.Count > 0 && IsBlacklistedPoint(routeToNextWaypoint.Peek()))
            {
                routeToNextWaypoint.Clear();
                UpdateTotalRoute();
                return;
            }
        }

        Vector3 targetW = routeToNextWaypoint.Peek();
        float worldDistance = playerW.WorldDistanceXYTo(targetW);

        Vector3 playerM = WorldMapAreaDB.ToMap_FlipXY(playerW, playerReader.WorldMapArea);
        Vector3 targetM = WorldMapAreaDB.ToMap_FlipXY(targetW, playerReader.WorldMapArea);
        float heading = DirectionCalculator.CalculateMapHeading(playerM, targetM);

        if (worldDistance < ReachedDistance(OutDoorMinDistance))
        {
            if (targetW.Z != 0 && targetW.Z != playerW.Z)
            {
                playerReader.WorldPosZ = targetW.Z;
            }

            if (SimplifyRouteToWaypoint)
                ReduceByDistance(playerW, OutDoorMinDistance);
            else
                routeToNextWaypoint.Pop();

            OnAnyPointReached?.Invoke();

            lastWorldDistance = float.MaxValue;
            UpdateTotalRoute();

            if (routeToNextWaypoint.Count == 0)
            {
                if (wayPoints.Count > 0)
                {
                    wayPoints.Pop();
                    UpdateTotalRoute();

                    if (debug)
                        LogDebug($"Reached wayPoint! Distance: {worldDistance} -- Remains: {wayPoints.Count}");

                    OnWayPointReached?.Invoke();
                }
            }
            else
            {
                targetW = routeToNextWaypoint.Peek();
                stuckDetector.SetTargetLocation(targetW);

                playerM = WorldMapAreaDB.ToMap_FlipXY(playerW, playerReader.WorldMapArea);
                targetM = WorldMapAreaDB.ToMap_FlipXY(targetW, playerReader.WorldMapArea);
                heading = DirectionCalculator.CalculateMapHeading(playerM, targetM);

                AdjustHeading(heading, token);

                return;
            }
        }

        if (routeToNextWaypoint.Count > 0)
        {
            if (stuckDetector.IsGettingCloser())
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
                    stuckDetector.Reset();
                    routeToNextWaypoint.Clear();
                    return;
                }

                if (HasBeenActiveRecently())
                {
                    stuckDetector.Update(token);
                    worldDistance = playerW.WorldDistanceXYTo(routeToNextWaypoint.Peek());
                }
            }
        }

        lastWorldDistance = worldDistance;
    }

    public void Resume()
    {
        ResetStuckParameters();

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

    public void Stop()
    {
        active = false;

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
        return WorldMapAreaDB.ToMap_FlipXY(routeToNextWaypoint.Peek(), playerReader.WorldMapArea);
    }

    public void  SetWayPoints(Span<Vector3> points)
    {
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

            wayPoints.Push(point);
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
        stuckDetector.Reset();
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

    private void RefillRouteToNextWaypoint(CancellationToken token)
    {
        // Don’t log "called" every frame; only log occasionally.
        LogRefill("[NAV] RefillRouteToNextWaypoint tick");

        if (routeToNextWaypoint.Count > 0)
        {
            LogRefill($"[NAV] Refill: exit (routeToNextWaypoint.Count={routeToNextWaypoint.Count})");
            return;
        }

        int pending = Volatile.Read(ref pathRequestPending);
        if (waitingForPathResult || pending == 1)
        {
            LogRefill($"[NAV] Refill: exit (waitingForPathResult={waitingForPathResult}, pathRequestPending={pending})");
            return;
        }

        if (wayPoints.Count == 0)
        {
            LogRefill("[NAV] Refill: exit (no waypoints)");
            UpdateTotalRoute();
            OnDestinationReached?.Invoke();
            return;
        }

        logger.LogInformation("[NAV] RefillRouteToNextWaypoint called");

        if (routeToNextWaypoint.Count > 0)
            return;

        // Strong gating: if a request is in flight, do NOT try again
        if (waitingForPathResult || Volatile.Read(ref pathRequestPending) == 1)
            return;

        routeToNextWaypoint.Clear();

        Vector3 startW = playerReader.WorldPos;

        // If we are currently inside a forbidden rect, escape FIRST.
        // Escape-first: if inside any blacklist, generate an escape step NOW.
        if (TryComputeEscapeOutOfBlacklist(startW, out var escapeW))
        {
            // --- RATE-LIMIT GUARD (PLACE IT HERE) ---
            if (lastEscapePoint.WorldDistanceXYTo(escapeW) < 0.1f &&
                escapeInsertCooldownTicks > 0)
            {
                return; // same escape recently inserted; do nothing this tick
            }

            // Update rate-limit state
            lastEscapePoint = escapeW;
            escapeInsertCooldownTicks = 10; // ~10 frames (tune as needed)

            // If escape point is basically where we are, avoid thrashing
            if (startW.WorldDistanceXYTo(escapeW) < ReachedDistance(OutDoorMinDistance))
            {
                logger.LogWarning($"[BL] Escape computed but too close; start={startW} escape={escapeW}");
                // You could increase margin here, or just return to avoid spam.
                return;
            }

            // Ensure it becomes the immediate movement target this frame
            routeToNextWaypoint.Clear();
            routeToNextWaypoint.Push(escapeW);
            stuckDetector.SetTargetLocation(escapeW);

            // Optional: keep it in waypoints too so "waypoint reached" logic remains consistent
            if (wayPoints.Count == 0 || wayPoints.Peek().WorldDistanceXYTo(escapeW) > 0.1f)
                wayPoints.Push(escapeW);

            UpdateTotalRoute();
            logger.LogInformation($"[BL] Escape-first ROUTE set: {startW} -> {escapeW}");
            return;
        }

        if (!SkipBlacklistedWaypoints())
        {
            logger.LogInformation("[NAV] Refill: SkipBlacklistedWaypoints returned false -> destination reached");
            UpdateTotalRoute();
            OnDestinationReached?.Invoke();
            return;
        }

        Vector3 targetW = wayPoints.Peek();
        float distance = startW.WorldDistanceXYTo(targetW);

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
                return; // next Update tick will refill using the new top-of-stack detour
            }

            // If detour failed, fall back to the pather.
            logger.LogInformation($"[NAV] Refill: detour failed -> forcing pather start={startW} end={targetW}");
            usePather = true;
        }

        if (usePather)
        {
            stopMoving.Stop();
            EnqueuePathRequest(playerReader.UIMapId.Value, startW, targetW, distance);
            return;
        }

        // else direct:
        routeToNextWaypoint.Push(targetW);
        stuckDetector.SetTargetLocation(targetW);
        UpdateTotalRoute();

    }

    private void EnqueuePathRequest(int mapId, Vector3 startW, Vector3 endW, float distance)
    {
        if (!TryBeginPathRequest(startW, endW, out long requestId))
        {
            logger.LogInformation($"[NAV] EnqueuePathRequest: blocked (pending). start={startW} end={endW}");
            return;
        }

        waitingForPathResult = true;

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
        Volatile.Write(ref activePathRequestId, requestId);

        lastRequestStart = startW;
        lastRequestEnd = endW;

        return true;
    }

    private void EndPathRequest(long requestId)
    {
        // Only clear if this is the currently-active request.
        // This avoids a stale callback clearing the gate for a newer request.
        if (Volatile.Read(ref activePathRequestId) == requestId)
        {
            Interlocked.Exchange(ref pathRequestPending, 0);
        }
    }

    private void PathCalculatedCallback(long requestId, PathResult result)
    {
        // Always end the request FIRST (or at least before any early return)
        EndPathRequest(requestId);

        waitingForPathResult = false;

        logger.LogInformation($"[NAV] PathCalculatedCallback reqId={requestId} pathLen={result.Path.Length} start={result.StartW} end={result.EndW}");

        if (!active)
            return;

        // Ignore stale results: if another request has superseded this one
        if (Volatile.Read(ref activePathRequestId) != requestId)
        {
            logger.LogInformation($"[NAV] Ignoring stale path result reqId={requestId} activeReqId={Volatile.Read(ref activePathRequestId)}");
            return;
        }

        if (result.Path.Length == 0)
        {
            if (lastFailedDestination != result.EndW)
            {
                lastFailedDestination = result.EndW;
                LogPathfinderFailed(logger, result.StartW, result.EndW, result.ElapsedMs);
            }

            failedAttempt++;
            if (failedAttempt > 2)
            {
                failedAttempt = 0;
                stuckDetector.SetTargetLocation(result.EndW);
                stuckDetector.Update();
            }
            return;
        }

        failedAttempt = 0;

        // (Keep your blacklist validation here if needed)
        // ...

        LogPathfinderSuccess(logger, result.Distance, result.StartW, result.EndW, result.ElapsedMs);

        const float MIN_FIRST_STEP_DIST = 1.0f;

        routeToNextWaypoint.Clear();

        for (int i = result.Path.Length - 1; i >= 0; i--)
        {
            Vector3 p = result.Path[i];
            if (p.WorldDistanceXYTo(result.StartW) < MIN_FIRST_STEP_DIST)
                continue;

            routeToNextWaypoint.Push(p);
        }

        if (routeToNextWaypoint.Count == 0 && wayPoints.Count > 0)
            routeToNextWaypoint.Push(wayPoints.Peek());

        if (SimplifyRouteToWaypoint)
            SimplyfyRouteToWaypoint();

        if (routeToNextWaypoint.Count == 0 && wayPoints.Count > 0)
            routeToNextWaypoint.Push(wayPoints.Peek());

        stuckDetector.SetTargetLocation(routeToNextWaypoint.Peek());
        UpdateTotalRoute();

        OnPathCalculated?.Invoke();
    }

    private void PathFinderThread()
    {
        while (!token.IsCancellationRequested)
        {
            manualReset.Reset();
            if (pathRequests.TryPeek(out PathRequest pathRequest))
            {
                logger.LogInformation($"[BL] PathFinderThread processing: {pathRequest.StartW} -> {pathRequest.EndW}");

                Vector3[] path = pather.FindWorldRoute(pathRequest.MapId, pathRequest.StartW, pathRequest.EndW);
                if (active)
                {
                    pathResults.Enqueue(new PathResult(pathRequest, path, pathRequest.Callback));
                }
                pathRequests.Dequeue();
            }
            manualReset.Wait();
        }

        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Thread stopped!");
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
        while (routeToNextWaypoint.Count > 0 &&
            playerW.WorldDistanceXYTo(routeToNextWaypoint.Peek()) < ReachedDistance(minDistance))
        {
            routeToNextWaypoint.Pop();
        }
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

            playerDirection.SetDirection(heading, routeToNextWaypoint.Peek(), OutDoorMinDistance, token);
        }
    }

    private bool AdjustNextWaypointPointToClosest()
    {
        if (wayPoints.Count < 2) { return false; }

        Vector3 A = wayPoints.Pop();
        Vector3 B = wayPoints.Peek();
        Vector2 result = VectorExt.GetClosestPointOnLineSegment(A.AsVector2(), B.AsVector2(), playerReader.WorldPos.AsVector2());
        Vector3 newPoint = new(result.X, result.Y, playerReader.WorldPosZ);

        if (newPoint.WorldDistanceXYTo(wayPoints.Peek()) > OutDoorMinDistance)
        {
            wayPoints.Push(newPoint);
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
            Vector3 playerW = playerReader.WorldPos;
            float distanceToRoute = playerW.WorldDistanceXYTo(routeToNextWaypoint.Peek());
            float distanceToPrevLoc = playerW.WorldDistanceXYTo(playerWorldPos);
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
            routeToNextWaypoint.Push(reduced[i]);
        }
    }

    private void UpdateTotalRoute()
    {
        TotalRoute = new Vector3[routeToNextWaypoint.Count + wayPoints.Count];
        routeToNextWaypoint.CopyTo(TotalRoute, 0);
        wayPoints.CopyTo(TotalRoute, routeToNextWaypoint.Count);
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
        if (AreaBlacklist.TryGetContainingRect(playerReader.WorldPos, out _))
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
        float z = playerReader.WorldPosZ;

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
        wayPoints.Push(targetW);  // restore target
        wayPoints.Push(best);     // detour is next

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

        // If not inside at all, nothing to do
        if (!AreaBlacklist.TryGetContainingRect(escapeW, out var rect))
            return false;

        float m = DetourMargin;
        float z = playerReader.WorldPosZ;

        // Loop to handle overlapping rects: escape one, then re-check, escape again, etc.
        // Usually 1-2 iterations; hard cap prevents infinite loops.
        for (int i = 0; i < 8; i++)
        {
            if (!AreaBlacklist.TryGetContainingRect(escapeW, out rect))
                return true; // escaped!

            // Compute nearest point just outside this rect
            escapeW = EscapePointFromRect(escapeW, rect, m, z);

            // If that point is still blacklisted (overlap / other rect), loop continues
            // If it never becomes non-blacklisted, we fail after 8 tries
        }

        // Final check
        return AreaBlacklist.TryGetContainingRect(escapeW, out _) == false && !IsBlacklistedPoint(escapeW);
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