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
    private const float minAngleToStopBeforeTurn = PI / 3f;     // 60 degree

    private readonly Stack<Vector3> wayPoints = new();
    private readonly Stack<Vector3> routeToNextWaypoint = new();

    public Vector3[] TotalRoute { private set; get; } = Array.Empty<Vector3>();

    public DateTime LastActive { get; private set; }

    public event Action? OnPathCalculated;
    public event Action? OnWayPointReached;
    public event Action? OnDestinationReached;
    public event Action? OnAnyPointReached;
    public event Action? OnNoPathFound;
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

    // The effective blacklist used by all Navigation queries.
    // May be a CompositeAreaBlacklist when stuck rects have been added.
    private IAreaBlacklist? _areaBlacklist;
    public IAreaBlacklist? AreaBlacklist
    {
        get => _areaBlacklist;
        set
        {
            // Callers (FRG Resume, constructor) set the static route blacklist.
            // Store it separately so we can re-composite it with stuck rects.
            _staticAreaBlacklist = value;
            _areaBlacklist = _stuckWorldRects.Count > 0
                ? new CompositeAreaBlacklist(value, new RectBlacklist(_stuckWorldRects))
                : value;
        }
    }

    // The static blacklist set by route configuration (never contains stuck rects).
    private IAreaBlacklist? _staticAreaBlacklist;

    // Runtime stuck rects recorded when the character makes zero movement during an escape.
    private readonly List<BlacklistRect> _stuckWorldRects = new();

    // Half-size in world yards of the dynamically-added stuck rect.
    // Bottom edge is 1y ahead of the player, top edge is 5y ahead (4y deep).
    // Center is 3y ahead (1y gap + 2y half-size).
    private const float StuckRectHalfSizeY = 2f;

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
    private const int EmptyRouteRefillMinIntervalMs = 250; // tune: 150-400

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
    public int ApproachEscapeTargetGuid => _approachEscapeTargetGuid;
    // Exposed for diagnostic logging in ATG/PTG OnEnter and DefaultApproach heartbeat.
    public float ApproachEscapeCurrentYards => _approachEscapeCurrentYards;
    public DateTime ApproachEscapeStartUtc => _approachEscapeStartUtc;
    public DateTime ApproachEscapeLastAttemptUtc => _approachEscapeLastAttemptUtc;
    public int StuckRectCount => _stuckWorldRects.Count;

    /// <summary>
    /// True when at least one pather escape attempt has been made for the current
    /// target and the escalation sequence is not yet exhausted (more attempts remain).
    /// While true, goals should suppress the blacklist-area bail-out so the approach
    /// can re-trigger TryUnstuck() for the next escalation level (10y → 20y → 30y).
    /// Cleared when ResetApproachEscape() is called (new target or all attempts done).
    /// </summary>
    public bool IsApproachEscapeEscalating =>
        _approachEscapeTargetGuid != 0 &&
        _approachEscapeCurrentYards > 0 &&
        _approachEscapeCurrentYards <= ApproachEscapeEndYards;

    /// <summary>
    /// Set to true after an escape clears due to no-progress AND the character
    /// made less than 0.5y of total movement — meaning the terrain is physically
    /// trapping the character and pather-based routes can't help. The goal should
    /// respond with a jump + backward movement to physically escape. Reset by the
    /// goal after it handles the condition.
    /// </summary>
    public bool IsApproachEscapePhysicallyStuck { get; set; }

    /// <summary>
    /// True after all pather escape levels (10y → 20y → 30y) have been exhausted for the
    /// current target. ATG/PTG use this to trigger an immediate bail-out instead of waiting
    /// for the player to drift into the blacklist area. Cleared by ResetApproachEscape()
    /// (on target change) and when a new escape attempt succeeds.
    /// </summary>
    public bool IsApproachEscapeExhausted { get; private set; }

    // --- Approach-vector pather escape ---
    // Goals call RecordApproachPosition() each time they press the Approach/Interact key.
    // Navigation keeps the position only when genuine forward progress has been made from it.
    // When TryUnstuck() is called, Navigation projects waypoints along the approach vector
    // starting at 10 world units (yards) and incrementing by 1 up to 30, trying to find a
    // pather path that routes around the obstacle. Falls back to stuckDetector.Update() if
    // all 3 attempts fail (10y, 20y, 30y).
    private const float ApproachEscapeStartYards = 10f;
    private const float ApproachEscapeEndYards = 30f;
    // After all 3 escape levels fail, suppress TryUnstuck for this many seconds so
    // the bail-out checks in ATG/PTG get at least one Update() tick to run.
    private const double PostExhaustionCooldownSec = 5.0;
    private const float ApproachEscapeProgressMinW = 0.75f; // min movement to keep a recorded position
    private const double ApproachEscapeTimeoutSecPerYard = 0.8; // seconds per yard — scales with escape distance
    private const double ApproachEscapeNoMovementSec = 3.0;     // escalate immediately if no movement for this long
    private Vector3 _approachEscapeStartPos;                    // player position when escape began, for initial displacement check
    private Vector3 _approachEscapeLastProgressPos;             // rolling position — updated when character moves >2y, for mid-escape stuck detection
    private DateTime _approachEscapeLastProgressUtc = DateTime.MinValue; // when last progress was recorded
    private Vector3 _approachRecordedW;
    private Vector3 _approachPrevRecordedW;
    // Anchor = the very first position recorded on each approach cycle (seed value from
    // RecordApproachPosition). Never updated after seeding so anchor→_approachRecordedW
    // spans the full approach distance from arrival to when stuck fires. This gives
    // TryUnstuck a reliable multi-yard direction vector even when consecutive
    // RecordApproach steps are only ~0.75y apart (the terrain-sliding case that always
    // caused the locked pair to be "too noisy" for MinLockDisplacementY=3.0y).
    private Vector3 _approachEscapeAnchorW;
    private bool _approachEscapeActive;
    private float _approachEscapeCurrentYards;
    private DateTime _approachEscapeLastAttemptUtc = DateTime.MinValue;
    private DateTime _approachEscapeStartUtc = DateTime.MinValue;
    // Locked approach direction: captured from _approachRecordedW/_approachPrevRecordedW
    // at the moment TryUnstuck first fires, so all escalation attempts (10y->30y) use
    // the same world-space direction toward the mob rather than compounding arc errors
    // from positions recorded after each escape movement.
    private Vector3 _approachEscapeLockedRecordedW;
    private Vector3 _approachEscapeLockedPrevRecordedW;
    private int _approachEscapeTargetGuid;
    // Tracks which integer second the TryUnstuck(active) heartbeat last fired.
    // Prevents double-fire when two consecutive calls both straddle a second boundary.
    private int _approachEscapeHeartbeatLastSec = -1;

    // Pather-based route escape — tried before stuckDetector.Update() when the bot
    // gets stuck while following the patrol route. Uses the player's facing direction
    // to project escape targets at 10y -> 20y -> 30y ahead, same escalation as approach escape.
    private const float RouteEscapeStartYards = 10f;
    private const float RouteEscapeEndYards = 30f;
    private const double RouteEscapeTimeoutSecPerYard = 0.8;
    private const double RouteEscapeNoMovementSec = 3.0;
    private bool _routeEscapeActive;
    private float _routeEscapeCurrentYards;
    private DateTime _routeEscapeLastAttemptUtc = DateTime.MinValue;
    private DateTime _routeEscapeStartUtc = DateTime.MinValue;
    private Vector3 _routeEscapeStartPos;
    private Vector3 _routeEscapeLastProgressPos;
    private DateTime _routeEscapeLastProgressUtc = DateTime.MinValue;

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

    /// <summary>
    /// Seconds since the watchdog last recorded a new closest-distance to the
    /// chase target. Returns 0 when no chase is currently being tracked
    /// (e.g., navigation isn't running, or the target hasn't been seen yet).
    /// <para>
    /// This is the "no progress toward target" timer — distinct from raw
    /// displacement, which can keep accumulating while the bot grinds along
    /// geometry without actually closing on the target. <see cref="FollowFocusGoal"/>
    /// reads this to escalate to CantFollow when the assist has been wedged
    /// long enough to indicate an unrecoverable obstacle.
    /// </para>
    /// </summary>
    public double ChaseSinceBestSec =>
        _chaseProgSinceUtc == DateTime.MinValue
            ? 0.0
            : (DateTime.UtcNow - _chaseProgSinceUtc).TotalSeconds;

    /// <summary>
    /// Diagnostic-only pass-through for the stuckDetector's current owner id.
    /// Used by FollowFocusGoal's chase watchdog firing log to confirm whether
    /// the detector was Released (ownerId=0) or held by Navigation (ownerId=1)
    /// at escalation time. See [NAV-DIAG] log lines.
    /// </summary>
    public int StuckDetectorOwnerId => stuckDetector.OwnerId;

    /// <summary>
    /// Diagnostic-only pass-through for the stuckDetector's enabled flag.
    /// </summary>
    public bool StuckDetectorEnabled => stuckDetector.Enabled;

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

        logger.LogDebug(
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
            Vector3 completionPos = Nav2D(playerReader.WorldPos);
            float displaced = _approachEscapeStartPos != default
                ? completionPos.WorldDistanceXYTo(_approachEscapeStartPos)
                : 0f;
            logger.LogInformation(
                $"[NAV] ApproachEscape: escape navigation complete — resuming normal approach. " +
                $"pos={completionPos} displaced={displaced:0.0}y over {_approachEscapeCurrentYards:0}y attempt. " +
                $"[diag: lastAttemptUtc={_approachEscapeLastAttemptUtc:HH:mm:ss.fff} yards={_approachEscapeCurrentYards:0} guid={_approachEscapeTargetGuid}]");
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
        logger.LogDebug(
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
        logger.LogDebug(
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
            logger.LogDebug("[NAV-DBG] " + msg);
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

        if (AreaBlacklist == null && pathSettings.MapBlacklistRects is { Length: > 0 })
        {
            AreaBlacklist = BlacklistConversion.BuildWorldBlacklistFromMapRects(
                pathSettings.MapBlacklistRects,
                playerReader.WorldMapArea
            );

            DetourMargin = 12f;
            MaxDetourAttemptsPerTarget = 6;
        }

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
                ClearDestinationLatch();
            }
        }

        bool approachEscapeOwns = _approachEscapeActive ||
            (_approachEscapeCurrentYards > 0 && _approachEscapeTargetGuid != 0);

        if (!approachEscapeOwns &&
            AreaBlacklist != null &&
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

            logger.LogDebug(
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

                if (!_routeEscapeActive && now >= _chaseUnstuckCooldownUntilUtc)
                {
                    stopMoving.Stop();
                    stuckDetector.SetTargetLocation(StuckOwnerId, targetW);
                    stuckDetector.Update(StuckOwnerId, token);

                    _chaseUnstuckCooldownUntilUtc = now.AddMilliseconds(1400);
                    _chaseLastPos = playerPos;
                    _chaseLastMovedUtc = now;
                    return;
                }
                else if (!_routeEscapeActive && sinceBestSec > 6.0 && now >= noProgressRefillCooldownUntilUtc)
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
                // Try pather-based escape (10y -> 20y -> 30y forward) before falling
                // back to the random turn+move unstuck.
                if (!TryRouteUnstuck(token))
                {
                    stuckDetector.Update(StuckOwnerId, token);
                }
            }
        }

        lastWorldDistance = playerPos.WorldDistanceXYTo(targetW);
    }

    public void Resume()
    {
        active = true;
        ClearDestinationLatch();
        SetLastSafeAnchor(playerReader.WorldPos);
        logger.LogInformation(
            $"[NAV-DIAG] Resume() pre: stuckDetector.OwnerId={stuckDetector.OwnerId} " +
            $"Enabled={stuckDetector.Enabled} -> Acquire(StuckOwnerId={StuckOwnerId})");
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

        logger.LogInformation(
            $"[NAV-DIAG] PausePathing() pre: stuckDetector.OwnerId={stuckDetector.OwnerId} " +
            $"Enabled={stuckDetector.Enabled} -> Release(StuckOwnerId={StuckOwnerId})");
        stuckDetector.Release(StuckOwnerId);
    }

    public void Stop()
    {
        active = false;
        ClearDestinationLatch();
        logger.LogInformation(
            $"[NAV-DIAG] Stop() pre: stuckDetector.OwnerId={stuckDetector.OwnerId} " +
            $"Enabled={stuckDetector.Enabled} -> Release(StuckOwnerId={StuckOwnerId})");
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

    /// <summary>
    /// World-space coordinates of the current top waypoint — the leader's immediate
    /// navigation target. Returns <see langword="default"/> when no waypoints are queued.
    /// Used by <see cref="FollowRouteGoal"/> to publish the leader's current destination
    /// to <see cref="Core.Party.LeaderNavigationProvider"/> so assist bots can navigate
    /// to the same target (waypoint-sharing mode).
    /// </summary>
    public Vector3 TopWaypointW => wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()) : default;

    public Vector3 NextMapPoint()
    {
        return WorldMapAreaDB.ToMap_FlipXY(Nav2D(routeToNextWaypoint.Peek()), playerReader.WorldMapArea);
    }

    public void SetWayPoints(Span<Vector3> points)
    {
        bool wasActive = active;
        if (!wasActive)
        {
            // Transitioning from paused/stopped → active. Re-Acquire stuckDetector
            // ownership; otherwise the detector remains in ownerId=0 (Released by
            // PausePathing or Stop), and all subsequent Update / IsGettingCloser /
            // SetTargetLocation calls from Navigation silently no-op (see
            // StuckDetector.IsOwner gate). Without this, the bot stops moving
            // because IsGettingCloser returns true unconditionally and AdjustHeading
            // never fires. Reproduces in AssistReturn after a distance-pause
            // (PausePathing → assistrequestreturn → SetWayPoints) and in
            // FFG.UpdateCantFollow recovery (Stop → SetSingleWaypoint).
            // Acquire is idempotent (resets internal timers); harmless when called
            // twice.
            stuckDetector.Acquire(StuckOwnerId);
        }
        logger.LogInformation(
            $"[NAV-DIAG] SetWayPoints(count={points.Length}) wasActive={wasActive} " +
            $"stuckDetector.OwnerId={stuckDetector.OwnerId} Enabled={stuckDetector.Enabled}" +
            (wasActive ? " (already active, no Acquire)" : " (was paused, re-Acquired)"));
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
        ResetRouteEscape();
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
            _approachEscapeAnchorW = worldPos;
            return;
        }

        float moved = worldPos.WorldDistanceXYTo(_approachRecordedW);
        if (moved >= ApproachEscapeProgressMinW)
        {
            _approachPrevRecordedW = _approachRecordedW;
            _approachRecordedW = worldPos;
            if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
                logger.LogTrace($"[NAV] RecordApproach: ACCEPTED prev={_approachPrevRecordedW} curr={_approachRecordedW} moved={moved:0.00}y (threshold={ApproachEscapeProgressMinW:0.00}y)");
        }
        else
        {
            if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                logger.LogDebug($"[NAV] RecordApproach: REJECTED moved={moved:0.00}y < threshold={ApproachEscapeProgressMinW:0.00}y — pair unchanged: prev={_approachPrevRecordedW} curr={_approachRecordedW}");
        }
    }

    /// <summary>
    /// Boxes a region around the stuck position as a blacklisted area so the pather
    /// routes around it on future requests.
    /// </summary>
    private void AddStuckRect(Vector3 posW, Vector3 forwardDir = default)
    {
        Vector3 center = posW;

        if (forwardDir != default)
        {
            float len = MathF.Sqrt(forwardDir.X * forwardDir.X + forwardDir.Y * forwardDir.Y);
            if (len > 0.01f)
            {
                float offsetY = 5f;
                center = new Vector3(
                    posW.X + (forwardDir.X / len) * offsetY,
                    posW.Y + (forwardDir.Y / len) * offsetY,
                    0f);
            }
        }

        var rect = new BlacklistRect(
            center.X - StuckRectHalfSizeY, center.Y - StuckRectHalfSizeY,
            center.X + StuckRectHalfSizeY, center.Y + StuckRectHalfSizeY
        ).Normalized();

        // Deduplicate — don't add if we already have an overlapping rect.
        for (int i = 0; i < _stuckWorldRects.Count; i++)
        {
            if (_stuckWorldRects[i].Contains(new System.Numerics.Vector2(center.X, center.Y)))
            {
                logger.LogInformation(
                    $"[NAV] StuckRect skipped: center={center} already inside existing rect [{i}] — no new rect added (total={_stuckWorldRects.Count}).");
                return;
            }
        }

        _stuckWorldRects.Add(rect);
        _areaBlacklist = new CompositeAreaBlacklist(_staticAreaBlacklist, new RectBlacklist(_stuckWorldRects));
        logger.LogWarning(
            $"[NAV] StuckRect added: center={center} (player={posW}) ±{StuckRectHalfSizeY}y " +
            $"(total={_stuckWorldRects.Count})");
    }

    public bool TryUnstuck(CancellationToken token = default)
    {
        if (_approachEscapeActive)
        {
            if (!active)
            {
                logger.LogInformation("[NAV] ApproachEscape: navigation stopped externally — clearing escape.");
                ResetApproachEscape();
                return false;
            }

            var now2 = DateTime.UtcNow;
            Vector3 currentPos = Nav2D(playerReader.WorldPos);
            double escapeSec = (now2 - _approachEscapeStartUtc).TotalSeconds;

            int heartbeatSec = (int)escapeSec;
            if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug) &&
                heartbeatSec != _approachEscapeHeartbeatLastSec)
            {
                _approachEscapeHeartbeatLastSec = heartbeatSec;
                double timeoutSecDbg = _approachEscapeCurrentYards * ApproachEscapeTimeoutSecPerYard;
                logger.LogDebug($"[NAV] TryUnstuck(active): yards={_approachEscapeCurrentYards:0} " +
                    $"escapeSec={escapeSec:0.0}s budget={timeoutSecDbg:0.0}s " +
                    $"startUtc={_approachEscapeStartUtc:HH:mm:ss.fff} " +
                    $"progressPos={_approachEscapeLastProgressPos} " +
                    $"guid={_approachEscapeTargetGuid}");
            }

            if (_approachEscapeLastProgressPos == default)
            {
                _approachEscapeLastProgressPos = currentPos;
                _approachEscapeLastProgressUtc = now2;
            }
            else if (currentPos.WorldDistanceXYTo(_approachEscapeLastProgressPos) >= 2.0f)
            {
                _approachEscapeLastProgressPos = currentPos;
                _approachEscapeLastProgressUtc = now2;
            }

            double sinceProgressSec = (now2 - _approachEscapeLastProgressUtc).TotalSeconds;
            bool noProgress = sinceProgressSec >= ApproachEscapeNoMovementSec;
            double timeoutSec = _approachEscapeCurrentYards * ApproachEscapeTimeoutSecPerYard;

            if (escapeSec > 2.0 * timeoutSec && _approachEscapeStartUtc != DateTime.MinValue)
            {
                logger.LogWarning($"[NAV] ApproachEscape: startUtc is stale (escapeSec={escapeSec:0.1}s > 2x budget={timeoutSec:0.1}s for {_approachEscapeCurrentYards:0}y) — resetting to now. " +
                    $"[staleUtc={_approachEscapeStartUtc:HH:mm:ss.fff} yards={_approachEscapeCurrentYards:0} guid={_approachEscapeTargetGuid}]");
                _approachEscapeStartUtc = now2;
                _approachEscapeLastProgressPos = currentPos;
                _approachEscapeLastProgressUtc = now2;
                escapeSec = 0.0;
                sinceProgressSec = 0.0;
            }

            bool timedOut = escapeSec >= timeoutSec;

            if (noProgress || timedOut)
            {
                string reason = noProgress
                    ? $"no progress for {sinceProgressSec:0.0}s (stuck at {currentPos})"
                    : $"total time {escapeSec:0.0}s exceeded {timeoutSec:0.0}s budget for {_approachEscapeCurrentYards:0}y";
                logger.LogWarning($"[NAV] ApproachEscape: escape stuck ({reason}) — clearing to retry at larger distance. " +
                    $"[diag: startUtc={_approachEscapeStartUtc:HH:mm:ss.fff} yards={_approachEscapeCurrentYards:0} guid={_approachEscapeTargetGuid}]");

                float totalDisplacement = currentPos.WorldDistanceXYTo(_approachEscapeStartPos);
                if (totalDisplacement < 0.5f)
                {
                    IsApproachEscapePhysicallyStuck = true;
                    logger.LogWarning($"[NAV] ApproachEscape: zero movement ({totalDisplacement:0.00}y) — character physically trapped in terrain.");
                }

                _approachEscapeActive = false;
                _approachEscapeStartUtc = DateTime.MinValue;
                _approachEscapeHeartbeatLastSec = -1;
                Stop();
                stopMoving.Stop();

                if (_approachEscapeCurrentYards >= ApproachEscapeEndYards)
                {
                    logger.LogWarning("[NAV] ApproachEscape: max escalation level stuck — marking exhausted immediately.");
                    IsApproachEscapeExhausted = true;
                    _approachEscapeLastAttemptUtc = DateTime.UtcNow.AddSeconds(PostExhaustionCooldownSec);
                }

                return false;
            }

            return true;
        }

        var now = DateTime.UtcNow;

        double gateElapsedMs = (now - _approachEscapeLastAttemptUtc).TotalMilliseconds;
        if (gateElapsedMs < 200)
        {
            if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                logger.LogDebug($"[NAV] TryUnstuck: 200ms gate — {gateElapsedMs:0}ms elapsed (need 200ms) — skipping.");
            return false;
        }

        if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            logger.LogDebug($"[NAV] TryUnstuck: lower-block entry — yards={_approachEscapeCurrentYards:0} (will become {_approachEscapeCurrentYards + 10:0}) " +
                $"gateElapsed={gateElapsedMs:0}ms lastAttempt={_approachEscapeLastAttemptUtc:HH:mm:ss.fff} " +
                $"lockedCurr={_approachEscapeLockedRecordedW} lockedPrev={_approachEscapeLockedPrevRecordedW} " +
                $"stuckRects={_stuckWorldRects.Count} guid={_approachEscapeTargetGuid}");

        _approachEscapeLastAttemptUtc = now;
        _approachEscapeCurrentYards += 10f;

        if (_approachEscapeTargetGuid == 0)
            _approachEscapeTargetGuid = playerReader.TargetGuid;

        if (_approachEscapeLockedRecordedW == default)
        {
            _approachEscapeLockedRecordedW = _approachRecordedW;
            bool anchorUsable = _approachEscapeAnchorW != default &&
                                 _approachEscapeAnchorW != _approachRecordedW;
            _approachEscapeLockedPrevRecordedW = anchorUsable
                ? _approachEscapeAnchorW
                : _approachPrevRecordedW;
            float lockDisp = _approachEscapeLockedPrevRecordedW != default
                ? _approachEscapeLockedRecordedW.WorldDistanceXYTo(_approachEscapeLockedPrevRecordedW)
                : 0f;
            logger.LogInformation($"[NAV] ApproachEscape: locking approach direction — " +
                $"prev={_approachEscapeLockedPrevRecordedW} curr={_approachEscapeLockedRecordedW} " +
                $"disp={lockDisp:0.00}y (anchorUsed={anchorUsable})");

            Vector3 approachDir;
            if (_approachRecordedW != default && _approachPrevRecordedW != default)
            {
                Vector3 dirFrom = (_approachEscapeAnchorW != default && _approachEscapeAnchorW != _approachRecordedW)
                    ? _approachEscapeAnchorW
                    : _approachPrevRecordedW;
                approachDir = new Vector3(
                    _approachRecordedW.X - dirFrom.X,
                    _approachRecordedW.Y - dirFrom.Y,
                    0f);
            }
            else if (_chaseProgTarget != default)
            {
                Vector3 currentW = Nav2D(playerReader.WorldPos);
                approachDir = new Vector3(
                    _chaseProgTarget.X - currentW.X,
                    _chaseProgTarget.Y - currentW.Y,
                    0f);
                logger.LogInformation("[NAV] ApproachEscape: using chase target for stuck rect direction.");
            }
            else if (_stuckWorldRects.Count > 0)
            {
                var prevRect = _stuckWorldRects[_stuckWorldRects.Count - 1];
                float rcx = (prevRect.MinX + prevRect.MaxX) * 0.5f;
                float rcy = (prevRect.MinY + prevRect.MaxY) * 0.5f;
                Vector3 currentW = Nav2D(playerReader.WorldPos);
                approachDir = new Vector3(rcx - currentW.X, rcy - currentW.Y, 0f);
                logger.LogInformation($"[NAV] ApproachEscape: using previous stuck rect center {new Vector3(rcx, rcy, 0f)} for approach direction.");
            }
            else
            {
                logger.LogWarning("[NAV] ApproachEscape: no direction data (prev=<0,0,0>, no stuck rects, " +
                    "no chase target) — SKIPPING stuck rect to avoid player-inside-own-rect. " +
                    "Pather escape runs without rect hint. (Typically caused by BUG B state wipe.)");
                goto AFTER_STUCK_RECT;
            }
            if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            {
                float dirLen = Sqrt(approachDir.X * approachDir.X + approachDir.Y * approachDir.Y);
                logger.LogDebug($"[NAV] ApproachEscape: stuck rect approachDir=({approachDir.X:0.00},{approachDir.Y:0.00}) len={dirLen:0.00} " +
                    $"player={Nav2D(playerReader.WorldPos)}");
            }
            AddStuckRect(Nav2D(playerReader.WorldPos), approachDir);
            AFTER_STUCK_RECT:;
        }

        if (_approachEscapeCurrentYards > ApproachEscapeEndYards)
        {
            logger.LogWarning(
                "[NAV] ApproachEscape: all pather attempts exhausted — bail-out window open for " +
                $"{PostExhaustionCooldownSec:0}s.");
            ResetApproachEscape();
            IsApproachEscapeExhausted = true;
            _approachEscapeLastAttemptUtc = DateTime.UtcNow.AddSeconds(PostExhaustionCooldownSec);
            stuckDetector.SetTargetLocation(StuckOwnerId, _approachRecordedW == default
                ? Nav2D(playerReader.WorldPos)
                : _approachRecordedW);
            stuckDetector.Update(StuckOwnerId, token);
            return false;
        }

        Vector3 projection = ProjectApproachEscapeTarget(_approachEscapeCurrentYards);

        Vector3 projectionMap = WorldMapAreaDB.ToMap_FlipXY(projection, playerReader.WorldMapArea);
        if (projectionMap.X is < 0 or > 100 || projectionMap.Y is < 0 or > 100)
        {
            logger.LogWarning(
                $"[NAV] ApproachEscape: projected target at {_approachEscapeCurrentYards:0}y is outside map bounds {projectionMap} — skipping.");
            _approachEscapeActive = false;
            return false;
        }

        logger.LogInformation(
            $"[NAV] ApproachEscape: attempt {_approachEscapeCurrentYards:0}y -> {projection}");

        IsApproachEscapeExhausted = false;
        _approachEscapeHeartbeatLastSec = -1;
        SetSingleWaypoint(projection);
        _approachEscapeActive = true;
        _approachEscapeStartUtc = DateTime.UtcNow;
        _approachEscapeStartPos = Nav2D(playerReader.WorldPos);
        _approachEscapeLastProgressPos = default;
        _approachEscapeLastProgressUtc = DateTime.MinValue;
        return true;
    }

    public bool TryRouteUnstuck(CancellationToken token = default)
    {
        if (_routeEscapeActive)
        {
            if (!active)
            {
                logger.LogInformation("[NAV] RouteEscape: navigation stopped externally — clearing escape.");
                ResetRouteEscape();
                return false;
            }

            var now2 = DateTime.UtcNow;
            Vector3 currentPos = Nav2D(playerReader.WorldPos);
            double escapeSec = (now2 - _routeEscapeStartUtc).TotalSeconds;

            if (_routeEscapeLastProgressPos == default)
            {
                _routeEscapeLastProgressPos = currentPos;
                _routeEscapeLastProgressUtc = now2;
            }
            else if (currentPos.WorldDistanceXYTo(_routeEscapeLastProgressPos) >= 2.0f)
            {
                _routeEscapeLastProgressPos = currentPos;
                _routeEscapeLastProgressUtc = now2;
            }

            double sinceProgressSec = (now2 - _routeEscapeLastProgressUtc).TotalSeconds;
            bool noProgress = sinceProgressSec >= RouteEscapeNoMovementSec;
            double timeoutSec = _routeEscapeCurrentYards * RouteEscapeTimeoutSecPerYard;
            bool timedOut = escapeSec >= timeoutSec;

            if (noProgress || timedOut)
            {
                string reason = noProgress
                    ? $"no progress for {sinceProgressSec:0.0}s"
                    : $"timed out after {escapeSec:0.0}s at {_routeEscapeCurrentYards:0}y";

                float totalDisplacement = currentPos.WorldDistanceXYTo(Nav2D(_routeEscapeStartPos));
                bool physicallyTrapped = totalDisplacement < 0.5f;

                logger.LogWarning($"[NAV] RouteEscape: escape stuck ({reason}) — " +
                    $"displacement={totalDisplacement:0.00}y physTrapped={physicallyTrapped}. Escalating.");

                _routeEscapeActive = false;

                if (physicallyTrapped)
                {
                    logger.LogWarning("[NAV] RouteEscape: physically trapped — injecting jump + reverse to escape.");
                    input.StopForward(false);
                    input.PressJump();
                    System.Threading.Thread.Sleep(400);
                    input.StartBackward(false);
                    input.PressJump();
                    System.Threading.Thread.Sleep(600);
                    input.PressJump();
                    System.Threading.Thread.Sleep(400);
                    input.StopBackward(false);
                }

                return false;
            }

            return true;
        }

        var now = DateTime.UtcNow;

        if ((now - _routeEscapeLastAttemptUtc).TotalMilliseconds < 200)
            return false;

        _routeEscapeLastAttemptUtc = now;
        _routeEscapeCurrentYards += 10f;

        if (_routeEscapeCurrentYards > RouteEscapeEndYards)
        {
            logger.LogWarning("[NAV] RouteEscape: all pather attempts exhausted — falling back to random unstuck.");
            ResetRouteEscape();
            return false;
        }

        float facing = playerReader.Direction;
        Vector3 playerW = Nav2D(playerReader.WorldPos);
        Vector3 escapeW = new Vector3(
            playerW.X + Cos(facing) * _routeEscapeCurrentYards,
            playerW.Y + Sin(facing) * _routeEscapeCurrentYards,
            0f);

        logger.LogInformation($"[NAV] RouteEscape: attempt {_routeEscapeCurrentYards:0}y -> {escapeW}");

        SetSingleWaypoint(escapeW);
        _routeEscapeActive = true;
        _routeEscapeStartUtc = now;
        _routeEscapeStartPos = playerW;
        _routeEscapeLastProgressPos = default;
        _routeEscapeLastProgressUtc = DateTime.MinValue;
        return true;
    }

    public void ResetRouteEscape()
    {
        _routeEscapeActive = false;
        _routeEscapeCurrentYards = RouteEscapeStartYards - 10f;
        _routeEscapeLastAttemptUtc = DateTime.MinValue;
        _routeEscapeStartUtc = DateTime.MinValue;
        _routeEscapeStartPos = default;
        _routeEscapeLastProgressPos = default;
        _routeEscapeLastProgressUtc = DateTime.MinValue;
    }

    public void ResetApproachEscape([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        bool hasState = _approachEscapeCurrentYards != 0
            || _approachEscapeTargetGuid != 0
            || _approachEscapeLastAttemptUtc != DateTime.MinValue
            || _stuckWorldRects.Count != 0
            || IsApproachEscapeExhausted;

        string logMsg = $"[NAV] ResetApproachEscape (caller={caller}): wiping state — " +
            $"was: active={_approachEscapeActive} yards={_approachEscapeCurrentYards:0} guid={_approachEscapeTargetGuid} " +
            $"exhausted={IsApproachEscapeExhausted} escalating={IsApproachEscapeEscalating} " +
            $"lastAttempt={_approachEscapeLastAttemptUtc:HH:mm:ss.fff} startUtc={_approachEscapeStartUtc:HH:mm:ss.fff} " +
            $"lockedCurr={_approachEscapeLockedRecordedW} lockedPrev={_approachEscapeLockedPrevRecordedW}";

        if (hasState)
            logger.LogWarning(logMsg);
        else
            logger.LogDebug(logMsg);
        _approachEscapeActive = false;
        _approachEscapeCurrentYards = ApproachEscapeStartYards - 10f;
        _approachRecordedW = default;
        _approachPrevRecordedW = default;
        _approachEscapeAnchorW = default;
        _approachEscapeLockedRecordedW = default;
        _approachEscapeLockedPrevRecordedW = default;
        _approachEscapeLastAttemptUtc = DateTime.MinValue;
        _approachEscapeStartUtc = DateTime.MinValue;
        _approachEscapeStartPos = default;
        _approachEscapeLastProgressPos = default;
        _approachEscapeLastProgressUtc = DateTime.MinValue;
        _approachEscapeTargetGuid = 0;
        IsApproachEscapePhysicallyStuck = false;
        IsApproachEscapeExhausted = false;
    }

    public void ResetApproachEscapeForTarget(int targetGuid)
    {
        if (targetGuid == 0)
            return;

        if (targetGuid == _approachEscapeTargetGuid)
        {
            bool wasActive = _approachEscapeActive;

            if (wasActive)
            {
                _approachEscapeActive = false;
                _approachRecordedW = default;
                _approachPrevRecordedW = default;
                _approachEscapeActive = true;
                logger.LogInformation($"[NAV] ApproachEscape: same target {targetGuid} re-entered — preserving {_approachEscapeCurrentYards:0}y escalation and locked direction (escape ACTIVE — timing preserved).");
            }
            else
            {
                _approachEscapeActive = false;
                _approachRecordedW = default;
                _approachPrevRecordedW = default;
                _approachEscapeStartUtc = DateTime.MinValue;
                _approachEscapeStartPos = default;
                _approachEscapeLastProgressPos = default;
                _approachEscapeLastProgressUtc = DateTime.MinValue;
                double lastAttemptAgeMs = (DateTime.UtcNow - _approachEscapeLastAttemptUtc).TotalMilliseconds;
                string lockedDirStatus = _approachEscapeLockedRecordedW != default
                    ? $"locked direction preserved (curr={_approachEscapeLockedRecordedW})"
                    : "locked direction WIPED (BUG B)";
                logger.LogInformation($"[NAV] ApproachEscape: same target {targetGuid} re-entered — " +
                    $"preserving {_approachEscapeCurrentYards:0}y escalation, {lockedDirStatus}. " +
                    $"[diag: lastAttemptAge={lastAttemptAgeMs:0}ms exhausted={IsApproachEscapeExhausted} " +
                    $"lockedCurr={_approachEscapeLockedRecordedW} lockedPrev={_approachEscapeLockedPrevRecordedW} " +
                    $"stuckRects={_stuckWorldRects.Count}]");
            }
        }
        else if (_approachEscapeTargetGuid == 0)
        {
            _approachEscapeTargetGuid = targetGuid;

            if (_approachEscapeCurrentYards == 0)
            {
                _approachEscapeLockedRecordedW = default;
                _approachEscapeLockedPrevRecordedW = default;
            }

            double lastAttemptAgeMs = (DateTime.UtcNow - _approachEscapeLastAttemptUtc).TotalMilliseconds;
            bool genuineBugB = _approachEscapeCurrentYards > 0 || _stuckWorldRects.Count > 0;

            string bugBescalation = _approachEscapeCurrentYards > 0
                ? $"{_approachEscapeCurrentYards:0}y escalation preserved"
                : "escalation WIPED (yards=0)";
            string bugBrects = _stuckWorldRects.Count > 0
                ? $"{_stuckWorldRects.Count} stuckRect(s) preserved"
                : "stuckRects WIPED";

            if (genuineBugB)
            {
                logger.LogWarning($"[NAV] ApproachEscape: BUG B — stored guid was 0, restoring to {targetGuid}. " +
                    $"{bugBescalation}, {bugBrects}. " +
                    $"[diag: lastAttemptAge={lastAttemptAgeMs:0}ms exhausted={IsApproachEscapeExhausted} " +
                    $"lockedCurr={_approachEscapeLockedRecordedW} lockedPrev={_approachEscapeLockedPrevRecordedW} " +
                    $"stuckRects={_stuckWorldRects.Count}]");
            }
            else
            {
                logger.LogDebug($"[NAV] ApproachEscape: first approach, guid restored to {targetGuid} (no prior escape state).");
            }
        }
        else
        {
            logger.LogInformation(
                $"[NAV] ApproachEscape: different target {targetGuid} (was {_approachEscapeTargetGuid}) — full reset.");
            ResetApproachEscape();
            _approachEscapeLockedRecordedW = default;
            _approachEscapeLockedPrevRecordedW = default;
            if (targetGuid != 0)
                _approachEscapeTargetGuid = targetGuid;
        }
    }

    private Vector3 ProjectApproachEscapeTarget(float yards)
    {
        Vector3 currentW = Nav2D(playerReader.WorldPos);

        float dx = 0f, dy = 0f;
        bool directionFound = false;

        const float MinLockDisplacementY = 1.5f;

        if (_approachEscapeLockedPrevRecordedW != default && _approachEscapeLockedRecordedW != default)
        {
            float ldx2 = _approachEscapeLockedRecordedW.X - _approachEscapeLockedPrevRecordedW.X;
            float ldy2 = _approachEscapeLockedRecordedW.Y - _approachEscapeLockedPrevRecordedW.Y;
            float disp = Sqrt(ldx2 * ldx2 + ldy2 * ldy2);
            if (disp >= MinLockDisplacementY)
            {
                dx = ldx2;
                dy = ldy2;
                directionFound = true;
                if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                    logger.LogDebug($"[NAV] ProjectEscapeTarget: using locked pair direction — disp={disp:0.0}y prev={_approachEscapeLockedPrevRecordedW} curr={_approachEscapeLockedRecordedW} dir=({dx:0.00},{dy:0.00})");
            }
            else
            {
                logger.LogWarning($"[NAV] ApproachEscape: locked positions only {disp:0.0}y apart — too noisy, preferring chase-target.");
                dx = ldx2;
                dy = ldy2;
            }
        }

        if (!directionFound && _chaseProgTarget != default)
        {
            dx = _chaseProgTarget.X - currentW.X;
            dy = _chaseProgTarget.Y - currentW.Y;
            directionFound = true;
            logger.LogInformation("[NAV] ApproachEscape: using chase target for direction.");
        }

        if (!directionFound && _stuckWorldRects.Count > 0)
        {
            var lastRect = _stuckWorldRects[_stuckWorldRects.Count - 1];
            float rcx = (lastRect.MinX + lastRect.MaxX) * 0.5f;
            float rcy = (lastRect.MinY + lastRect.MaxY) * 0.5f;
            dx = rcx - currentW.X;
            dy = rcy - currentW.Y;
            float rlen = Sqrt(dx * dx + dy * dy);
            if (rlen >= 0.5f)
            {
                directionFound = true;
                logger.LogInformation($"[NAV] ApproachEscape: using stuck rect center {new Vector3(rcx, rcy, 0f)} for direction.");
            }
        }

        if (!directionFound && _approachEscapeLockedRecordedW != default)
        {
            float ldx = _approachEscapeLockedRecordedW.X - currentW.X;
            float ldy = _approachEscapeLockedRecordedW.Y - currentW.Y;
            float llen = Sqrt(ldx * ldx + ldy * ldy);
            if (llen >= 0.5f)
            {
                dx = ldx;
                dy = ldy;
                directionFound = true;
                logger.LogInformation("[NAV] ApproachEscape: single locked position — projecting toward it.");
            }
        }

        if (!directionFound && _approachPrevRecordedW != default && _approachRecordedW != default)
        {
            dx = _approachRecordedW.X - _approachPrevRecordedW.X;
            dy = _approachRecordedW.Y - _approachPrevRecordedW.Y;
            directionFound = true;
            logger.LogInformation("[NAV] ApproachEscape: using current recorded positions for direction.");
        }

        if (!directionFound && (_approachEscapeLockedPrevRecordedW != default && _approachEscapeLockedRecordedW != default))
        {
            directionFound = true;
            logger.LogInformation("[NAV] ApproachEscape: noisy locked positions — using as last resort before facing.");
        }

        if (!directionFound)
        {
            float facing = playerReader.Direction;
            dx = Cos(facing);
            dy = Sin(facing);
            logger.LogInformation("[NAV] ApproachEscape: no direction data — using facing direction as last resort.");
        }

        float len = Sqrt(dx * dx + dy * dy);
        if (len < 0.01f)
        {
            if (_chaseProgTarget != default)
            {
                dx = _chaseProgTarget.X - currentW.X;
                dy = _chaseProgTarget.Y - currentW.Y;
                len = Sqrt(dx * dx + dy * dy);
            }
            if (len < 0.01f)
            {
                float facing = playerReader.Direction;
                dx = Cos(facing);
                dy = Sin(facing);
                len = 1f;
            }
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

            bool approachEscapeOwns = _approachEscapeActive ||
                (_approachEscapeCurrentYards > 0 && _approachEscapeTargetGuid != 0);

            if (approachEscapeOwns)
            {
                escapeActive = false;
                escapeRouteInProgress = false;
            }
            else if (escapeActive)
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

            if (!approachEscapeOwns && TryComputeEscapeOutOfBlacklist(startW, out var escapeW))
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
                RefillExit("SkipBlacklistedWaypoints_false_destinationReached");
                logger.LogInformation("[NAV] Refill: SkipBlacklistedWaypoints returned false -> destination reached");
                CompleteDestinationReached();
                _phase = "exit_SkipBlacklistedWaypoints_false_destinationReached";
                goto REFILL_EXIT;
            }

            Vector3 targetRaw = wayPoints.Peek();
            Vector3 targetW = Nav2D(wayPoints.Peek());

            float distance = startW.WorldDistanceXYTo(targetW);

            logger.LogDebug(
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
                if (_approachEscapeActive)
                {
                    logger.LogInformation($"[NAV] Refill: approach escape active — skipping detour, forcing pather for start={startW} end={targetW}");
                    usePather = true;
                }
                else if (TryInsertDetour(startW, targetW, r))
                {
                    logger.LogInformation($"[NAV] Refill: detour inserted for blocked segment start={startW} end={targetW}");
                    RefillExit("tryForwardDetour");
                    _phase = "exit_tryForwardDetour";
                    goto REFILL_EXIT;
                }
                else
                {
                    logger.LogInformation($"[NAV] Refill: detour failed -> forcing pather start={startW} end={targetW}");
                    usePather = true;
                }
            }

            if (usePather)
            {
                _phase = "exit_enqueuePathRequest";
                ClearDestinationLatch();
                stopMoving.Stop();

                logger.LogDebug(
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
        logger.LogDebug(
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

                OnNoPathFound?.Invoke();
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

        logger.LogDebug(
            $"[NAV-DBG] ROUTESET reqId={requestId} routeCount={routeToNextWaypoint.Count} " +
            $"routeTop={(routeToNextWaypoint.Count > 0 ? Nav2D(routeToNextWaypoint.Peek()).ToString() : "<none>")} " +
            $"wpTop={(wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()).ToString() : "<none>")} " +
            $"player={playerReader.WorldPos}"
            );

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
            logger.LogDebug($"[NAV-DBG] ReduceByDistance poppedLast={lastPopped} newRouteTop={newTop} wpTop={wpTop}");
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

    /// <summary>
    /// Clears all dynamically-added stuck rects accumulated during approach attempts.
    /// Restores the effective blacklist to the static route blacklist only.
    /// </summary>
    public void ClearStuckRects()
    {
        if (_stuckWorldRects.Count == 0)
            return;

        int count = _stuckWorldRects.Count;
        _stuckWorldRects.Clear();
        _areaBlacklist = _staticAreaBlacklist;
        logger.LogInformation($"[NAV] ClearStuckRects: {count} dynamic stuck rect(s) cleared — restoring static blacklist only.");
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
