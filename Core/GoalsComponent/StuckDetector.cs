using Core.Goals;

using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.Extensions;

using System;
using System.Numerics;
using System.Threading;

using static System.Diagnostics.Stopwatch;

#pragma warning disable 162

namespace Core;

public sealed class StuckDetector
{
    private const bool debug = false;

    private const float MIN_RANGE_DIFF = 2f;
    private const float MIN_DISTANCE = 0.2f;
    private const float MAX_RANGE = 999999;
    private const double UNSTUCK_AFTER_MS = 2000;
    private const double ACTION_STUCK_TIME = 3000;

    private readonly ILogger<StuckDetector> logger;
    private readonly ConfigurableInput input;

    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly PlayerDirection playerDirection;
    private readonly StopMoving stopMoving;

    private Vector3 worldTarget;

    private float prevDistance = MAX_RANGE;
    private long startTime;
    private long attemptTime;

    
    // --- Ownership / enable gate ---
    // If a goal doesn't own the detector, it can't update/reset/retarget it.
    // This prevents "foreign" Update() calls from timing out a stale target set by Navigation.
    private int ownerId; // 0 = legacy/unowned
    public bool Enabled { get; private set; }
    public int OwnerId => ownerId;

    public double ActionDurationMs => GetElapsedTime(startTime).TotalMilliseconds;
    private double UnstuckMs => GetElapsedTime(attemptTime).TotalMilliseconds;

    // --- Movement-aware progress tracking (prevents false stuck on slopes) ---
    private float bestDistance = MAX_RANGE;
    private Vector3 lastPos;
    private long lastMoveTime;

    // Tunables
    private const float PROGRESS_EPS = 0.08f;         // accept smaller progress steps
    private const float MOVE_EPS = 0.20f;             // accept smaller XY movement per tick
    private const double NO_MOVE_MS = 1800;           // more tolerant before declaring stationary
    private const double NO_BEST_PROGRESS_MS = 3500;  // more tolerant on slopes/switchbacks

    public StuckDetector(ILogger<StuckDetector> logger, ConfigurableInput input,
        AddonBits bits, PlayerReader playerReader, PlayerDirection playerDirection,
        StopMoving stopMoving)
    {
        this.logger = logger;
        this.input = input;

        this.bits = bits;
        this.playerReader = playerReader;
        this.playerDirection = playerDirection;
        this.stopMoving = stopMoving;

        // Default: legacy/unowned and enabled.
        // Navigation should call Acquire(navOwnerId) when it wants exclusive control.
        ownerId = 0;
        Enabled = true;
        ResetInternal();
    }

    /// <summary>
    /// Acquire exclusive control for a specific owner (e.g., Navigation).
    /// Once acquired, calls from other owners are ignored.
    /// </summary>
    public void Acquire(int ownerId)
    {
        this.ownerId = ownerId;
        Enabled = true;
        ResetInternal();
    }

    /// <summary>
    /// Release control for the specified owner. After release the detector is disabled
    /// (so it won't accumulate stuck time while "paused").
    /// </summary>
    public void Release(int ownerId)
    {
        if (!Enabled || this.ownerId != ownerId)
            return;

        this.ownerId = 0;
        Enabled = true;   // allow legacy use again
        ResetInternal();
    }

    private bool IsOwner(int callerOwnerId)
    {
        if (!Enabled) return false;

        // Unowned: allow legacy (0) only
        if (ownerId == 0)
            return callerOwnerId == 0;

        // Owned: only the owner may act
        return ownerId == callerOwnerId;
    }

    // Legacy API (ownerId=0)
    public void SetTargetLocation(Vector3 worldTarget) => SetTargetLocation(0, worldTarget);
    public void SetTargetLocation(int callerOwnerId, Vector3 worldTarget)
    {
        if (!IsOwner(callerOwnerId))
            return;

        if (this.worldTarget != worldTarget)
        {
            this.worldTarget = worldTarget;
            ResetInternal();
        }
    }

    // Legacy API (ownerId=0)
    public void Reset() => Reset(0);

    public void Reset(int callerOwnerId)
    {
        if (!IsOwner(callerOwnerId))
            return;

        ResetInternal();
    }

    private void ResetInternal()
    {
        long now = GetTimestamp();

        attemptTime = now;
        startTime = now;

        prevDistance = MAX_RANGE;
        bestDistance = MAX_RANGE;

        lastPos = playerReader.WorldPos;
        lastMoveTime = now;
    }

    // Legacy API (ownerId=0)
    public void Update(CancellationToken token = default) => Update(0, token);
    public void Update(int callerOwnerId, CancellationToken token = default)
    {
        if (!IsOwner(callerOwnerId))
            return;
        
        if (bits.Falling())
            return;

        if (debug)
            logger.LogDebug($"Stuck for {ActionDurationMs}ms, last tried to unstick {UnstuckMs}ms ago.");

        if (UnstuckMs > UNSTUCK_AFTER_MS)
        {
            stopMoving.Stop();

            // Turn
            int turnDuration = Random.Shared.Next(350);
            logger.LogInformation($"Unstuck by turning for {turnDuration}ms");
            input.TurnRandomDir(turnDuration, token);

            // Move
            ConsoleKey moveKey = Random.Shared.Next(100) >= 25 ? input.ForwardKey : input.BackwardKey;
            int moveDuration = Random.Shared.Next(750) + 1000;
            logger.LogInformation($"Unstuck by moving for {moveDuration}ms");
            input.PressFixed(moveKey, moveDuration, token);

            input.PressJump();

            Vector3 targetM = WorldMapAreaDB.ToMap_FlipXY(worldTarget, playerReader.WorldMapArea);
            float heading = DirectionCalculator.CalculateMapHeading(playerReader.MapPos, targetM);
            playerDirection.SetDirection(heading, targetM, PlayerDirection.DefaultIgnoreDistance, token);

            attemptTime = GetTimestamp();
        }
        else
        {
            if (!bits.Flying())
                input.PressJump();
        }
    }

    // Legacy API (ownerId=0)
    public bool IsGettingCloser() => IsGettingCloser(0);

    public bool IsGettingCloser(int callerOwnerId)
    {
        // If the caller doesn't own the detector, treat as "not stuck".
        // This prevents other goals from tripping stuck logic on a stale Navigation target.
        if (!IsOwner(callerOwnerId))
            return true;

        float distance = playerReader.WorldPos.WorldDistanceXYTo(worldTarget);
        if (distance <= prevDistance - MIN_RANGE_DIFF)
        {
            ResetInternal();
            prevDistance = distance;
            return true;
        }

        return ActionDurationMs < ACTION_STUCK_TIME;
    }

    // Legacy API (ownerId=0)
    public bool IsMoving() => IsMoving(0);

    public bool IsMoving(int callerOwnerId)
    {
        if (!IsOwner(callerOwnerId))
            return true;

        Vector3 pos = playerReader.WorldPos;
        long now = GetTimestamp();

        float moved = pos.WorldDistanceXYTo(lastPos);
        if (moved > MOVE_EPS)
        {
            lastPos = pos;
            lastMoveTime = now;

            // If we are moving, refresh timers so we don't accumulate stuck time.
            startTime = now;
            return true;
        }

        // Not moving much. Only call it "not moving" once we've been stationary long enough.
        double sinceMoveMs = GetElapsedTime(lastMoveTime).TotalMilliseconds;
        return sinceMoveMs < NO_MOVE_MS;
    }
}