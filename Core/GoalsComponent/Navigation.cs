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
    private const int StuckOwnerId = 1;

    private enum RectSide { Left, Right, Bottom, Top }
   private Vector3 noProgressTargetW;
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
        logger.LogInformation($"[NAV-SANITY] Update entered navHash={GetHashCode()} tokenCancelled={token.IsCancellationRequested}");

        if (!active)
        {
            //ResetStuckParameters();
            // Don't clear pathRequestPending here: a request may still be in flight.
            // We simply don't drive movement/stuck while inactive.
            return;
        }

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
                $"[NAV-SANITY] EXIT noWork inside={AreaBlacklist?.ContainsWorld(playerReader.WorldPos) == true}");

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

        // Watchdog for path gates
        if (waitingForPathResult && (DateTime.UtcNow - waitingSinceUtc).TotalSeconds > 3)
        {
            logger.LogWarning("[NAV] Path wait timeout; resetting pending/waiting gates.");
            waitingForPathResult = false;
            Interlocked.Exchange(ref pathRequestPending, 0);
        }

        logger.LogInformation(
            $"[NAV-SANITY] afterResults route={routeToNextWaypoint.Count} waypoints={wayPoints.Count} " +
            $"waiting={waitingForPathResult} pending={Volatile.Read(ref pathRequestPending)} " +
            $"reqQ={pathRequests.Count} resQ={pathResults.Count} threadTokenCancelled={token.IsCancellationRequested}");
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
            Volatile.Write(ref activePathRequestId, 0);
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
            // continue to movement section (do NOT return)
        }
        else
        {
            if (waitingForPathResult)
                return;

            if (DateTime.UtcNow < noProgressRefillCooldownUntilUtc)
                return;

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
        logger.LogInformation($"[NAV] chase target={targetW} dist={worldDistance:0.00} routeCount={routeToNextWaypoint.Count} wpCount={wayPoints.Count}");

        // ---- ESCAPE FALLBACK WATCHDOG
        if (escapeActive && routeToNextWaypoint.Count > 0)
        {
            // Stop escape mode once we are outside
            if (AreaBlacklist?.ContainsWorld(playerReader.WorldPos) != true)
            {
                escapeActive = false;
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
                        playerReader.WorldPos,
                        escapeTargetW,
                        d
                    );

                    // keep escapeActive true until we're outside
                }
            }
        }

        // If we're not getting closer for a short period, force a route refresh.
        // Helps when route has 1 point but movement is "stalled" or jittering.
        if (routeToNextWaypoint.Count > 0)
        {
            if (worldDistance + 0.05f < noProgressBestDist) // made progress
            {
                noProgressBestDist = worldDistance;
                noProgressSinceUtc = DateTime.UtcNow;
            }
            else
            {
                if (noProgressSinceUtc == DateTime.MinValue)
                    noProgressSinceUtc = DateTime.UtcNow;

                if ((DateTime.UtcNow - noProgressSinceUtc).TotalSeconds > 1.0)
                {
                    logger.LogWarning($"[NAV] No progress for 1s. Clearing route and refilling. dist={worldDistance} best={noProgressBestDist}");
                    routeToNextWaypoint.Clear();
                    noProgressBestDist = float.MaxValue;
                    noProgressSinceUtc = DateTime.MinValue;

                    // set cooldown so the next few frames don’t thrash
                    noProgressRefillCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(250);

                    // DO the refill now (one shot)
                    RefillRouteToNextWaypoint(token);
                    return;
                }
            }
        }

        Vector3 playerM = WorldMapAreaDB.ToMap_FlipXY(playerW, playerReader.WorldMapArea);
        Vector3 targetM = WorldMapAreaDB.ToMap_FlipXY(targetW, playerReader.WorldMapArea);
        float heading = DirectionCalculator.CalculateMapHeading(playerM, targetM);

        if (ShouldTreatAsReached(playerW, targetW, OutDoorMinDistance))
        {
            if (targetW.Z != 0 && targetW.Z != playerW.Z)
            {
                playerReader.WorldPosZ = targetW.Z;
            }

            if (SimplifyRouteToWaypoint)
                ReduceByDistance(playerW, OutDoorMinDistance);
            else
            {
                var popped = routeToNextWaypoint.Pop();

                logger.LogInformation(
                    $"[NAV] POP reached {popped} newTop={(routeToNextWaypoint.Count > 0 ? routeToNextWaypoint.Peek().ToString() : "<none>")}"
                );
            }
                

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
                stuckDetector.SetTargetLocation(StuckOwnerId, targetW);

                playerM = WorldMapAreaDB.ToMap_FlipXY(playerW, playerReader.WorldMapArea);
                targetM = WorldMapAreaDB.ToMap_FlipXY(targetW, playerReader.WorldMapArea);
                heading = DirectionCalculator.CalculateMapHeading(playerM, targetM);

                AdjustHeading(heading, token);

                return;
            }
        }

        if (routeToNextWaypoint.Count == 0)
        {
            ResetStuckParameters();

            if (!waitingForPathResult)
                RefillRouteToNextWaypoint(token);
            return;
        }

        // once we get here, always drive movement this tick
        LastActive = DateTime.UtcNow;
        input.StartForward(true);

        if (routeToNextWaypoint.Count > 0)
        {
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
                    stuckDetector.Update(StuckOwnerId,token);
                    worldDistance = playerW.WorldDistanceXYTo(routeToNextWaypoint.Peek());
                }
            }
        }

        lastWorldDistance = worldDistance;
    }

    public void Resume()
    {
        active = true;
        stuckDetector.Acquire(StuckOwnerId);
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

    public void PausePathing()
    {
        active = false;

        // Don’t stomp request gates; a request may be in flight.
        // waitingForPathResult can stay true/false; it won’t matter while inactive.

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
        return WorldMapAreaDB.ToMap_FlipXY(routeToNextWaypoint.Peek(), playerReader.WorldMapArea);
    }

    public void  SetWayPoints(Span<Vector3> points)
    {
        active = true;
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

    private void RefillRouteToNextWaypoint(CancellationToken token)
    {
        logger.LogInformation(
            $"[NAV-SANITY] Refill entered. " +
            $"insideBlacklist={AreaBlacklist?.ContainsWorld(playerReader.WorldPos) == true} " +
            $"waypoints={wayPoints.Count} route={routeToNextWaypoint.Count}");

        // Don’t log "called" every frame; only log occasionally.
        LogRefill("[NAV] RefillRouteToNextWaypoint tick");

        if (routeToNextWaypoint.Count > 0)
        {
            LogRefill($"[NAV] Refill: exit (routeToNextWaypoint.Count={routeToNextWaypoint.Count})");
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
            escapeActive = true;
            escapeTargetW = escapeW;
            escapeBestDist = float.MaxValue;
            escapeNoProgressSinceUtc = DateTime.UtcNow;

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
            stuckDetector.SetTargetLocation(StuckOwnerId, escapeW);

            // Optional: keep it in waypoints too so "waypoint reached" logic remains consistent
            if (wayPoints.Count == 0 || wayPoints.Peek().WorldDistanceXYTo(escapeW) > 0.1f)
                wayPoints.Push(escapeW);

            UpdateTotalRoute();
            logger.LogInformation($"[BL] Escape-first ROUTE set: {startW} -> {escapeW}");
            return;
        }

        // now gate the normal routing behavior
        int pending = Volatile.Read(ref pathRequestPending);
        if (waitingForPathResult || pending == 1)
        {
            LogRefill($"[NAV] Refill: exit (waitingForPathResult={waitingForPathResult}, pathRequestPending={pending})");
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
        // Add a midpoint for long direct segments to reduce jitter on single-point movement.
        Vector3 mid = default;
        bool hasMid = false;

        if (distance > 8f)
        {
            mid = new Vector3(
                (startW.X + targetW.X) * 0.5f,
                (startW.Y + targetW.Y) * 0.5f,
                playerReader.WorldPosZ
            );

            hasMid = !IsBlacklistedPoint(mid) && !SegmentBlockedEscapeAware(startW, mid);
        }

        // Push final target first, then optional mid so mid is on top (next step).
        routeToNextWaypoint.Push(targetW);

        if (hasMid)
            routeToNextWaypoint.Push(mid);

        noProgressBestDist = float.MaxValue;
        noProgressSinceUtc = DateTime.MinValue;
        noProgressTargetW = routeToNextWaypoint.Count > 0 ? routeToNextWaypoint.Peek() : default;

        if (routeToNextWaypoint.Count > 0)
            stuckDetector.SetTargetLocation(StuckOwnerId, routeToNextWaypoint.Peek());

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
        waitingSinceUtc = DateTime.UtcNow;

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
                stuckDetector.SetTargetLocation(StuckOwnerId, result.EndW);
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

        stuckDetector.SetTargetLocation(StuckOwnerId, routeToNextWaypoint.Peek());

        noProgressBestDist = float.MaxValue;
        noProgressSinceUtc = DateTime.MinValue;
        noProgressTargetW = routeToNextWaypoint.Peek();

        UpdateTotalRoute();
        OnPathCalculated?.Invoke();
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

                    if (active)
                        pathResults.Enqueue(new PathResult(pathRequest, path, pathRequest.Callback));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[NAV] PathFinderThread exception; returning empty path");
                    if (active)
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

        while (routeToNextWaypoint.Count > 0 &&
               playerW.WorldDistanceXYTo(routeToNextWaypoint.Peek()) < ReachedDistance(minDistance))
        {
            routeToNextWaypoint.Pop();
            poppedAny = true;
        }

        if (!poppedAny)
            return;

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

    public bool IsInBlacklistArea()
    {
        //Console.WriteLine("IsInBlacklistArea AreaBlacklist: " + AreaBlacklist);
        bool inside = AreaBlacklist?.ContainsWorld(playerReader.WorldPos) == true;

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

        if (!AreaBlacklist.TryGetContainingRect(escapeW, out var rect))
            return false;

        float m = DetourMargin;
        float z = playerReader.WorldPosZ;

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