using Core.AreaBlacklist;
using Core.GOAP;

using Game;

using Microsoft.Extensions.Logging;

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
    private volatile bool _disposing;
    private bool suppressNavigation;
    private bool navStoppedForTarget;
    private int _pauseNavRequested;   // set by worker thread
    private int _resumeNavRequested;  // set by main thread when it wants nav back
    private bool _pausedByLocalLogic; // tracks if we paused nav due to target/local movement

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

    private bool _assistRewindActive;
    private Vector3 _assistRewindAnchorW;

    // 0 = normal attempt, 1 = after rewind retry
    private int _assistAttempt;

    // Assist-return timeout should be based on ACTIVE navigation time (not wall clock time)
    private const double ASSIST_RETURN_TIMEOUT_ACTIVE_SEC = 25.0;
    private TimeSpan _assistReturnActiveElapsed;
    private DateTime _assistReturnLastTickUtc;
    private bool _assistReturnTimerInit;

    // Path traversal direction for PathThereAndBack routes.
    //  0 = unknown/uninitialized
    // +1 = move forward through mapRoute (index increasing)
    // -1 = move backward through mapRoute (index decreasing)
    private int _pathTraversalDirection;

    private int _suppressedBlacklistedGuid;
    private DateTime _suppressedBlacklistedUntilUtc;


    #region IRouteProvider

    public DateTime LastActive => navigation.LastActive;

    public Vector3[] MapRoute() => mapRoute;

    public Vector3[] PathingRoute()
    {
        return navigation.TotalRoute;
    }

    public bool HasNext()
    {
        return navigation.HasNext();
    }

    public Vector3 NextMapPoint()
    {
        return navigation.NextMapPoint();
    }

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
        ChatReader chatReader)
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
        navigation.OnPathCalculated += Navigation_OnPathCalculated;
        navigation.OnDestinationReached += Navigation_OnDestinationReached;
        navigation.OnWayPointReached += Navigation_OnWayPointReached;
        navigation.OnPathFailed += Navigation_OnPathFailed;

        this.chatReader = chatReader;

        if(classConfig.Mode == Mode.PartyLeader)
        {
            AddPrecondition(GoapKey.assistrequestreturnorisfollowing, true);
        }

        if (classConfig.Mode == Mode.AttendedGather)
        {
            AddPrecondition(GoapKey.dangercombat, false);
            navigation.OnAnyPointReached += Navigation_OnWayPointReached;
        }
        else
        {
            if (classConfig.Loot)
            {
                AddPrecondition(GoapKey.incombat, false);
            }

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
            // Stop side thread first
            sideActivityCts.Cancel();
            sideActivityManualReset.Set();

            // Join thread so it cannot call into dependencies after disposal
            if (sideActivityThread is { IsAlive: true })
            {
                if (!sideActivityThread.Join(millisecondsTimeout: 2000))
                {
                    logger.LogWarning("FollowRouteGoal: sideActivityThread did not stop within timeout.");
                    // You generally should not call Thread.Abort (not supported in .NET Core)
                }
            }

            sideActivityCts.Dispose();

            navigation.OnPathFailed -= Navigation_OnPathFailed;

            // Now it’s safe to dispose other objects (or let DI do it)
            navigation.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FollowRouteGoal.Dispose failed");
        }

    }

    private void Abort()
    {
        
        if (!targetBlacklist.Is())
            // Restored this to debug running off into distance
            // after combat starts
            navigation.StopMovement();

        navigation.PausePathing();

        sideActivityManualReset.Reset();
        targetFinder.Reset();
        ResetRefillWaypointsGuard();
    }

    private void Resume()
    {
        // Apply per-route area blacklists (map rects -> world rects)
        if (pathSettings.MapBlacklistRects is { Length: > 0 })
        {
            navigation.AreaBlacklist = BlacklistConversion.BuildWorldBlacklistFromMapRects(
                pathSettings.MapBlacklistRects,
                playerReader.WorldMapArea
            );

            // Tune these as desired
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

        while (restHandler.IsResting() && !chatReader.AssistRequestReturn)
        {
            wait.Update(1000);
        }

        onEnterTime = DateTime.UtcNow;
        ResetRefillWaypointsGuard();

        if (sideActivityCts.IsCancellationRequested)
        {
            sideActivityCts = new();
        }
        sideActivityManualReset.Set();

        // If navigation already has progress, preserve it.
        // This is critical when FollowRouteGoal is re-entered after temporary plans
        // like Blacklist Target, combat interruptions, etc.
        if (navigation.HasWaypoint() || navigation.HasNext())
        {
            logger.LogInformation("[FRG] Resume - preserving existing navigation progress");
            navigation.Resume();
        }
        else if (classConfig.Mode == Mode.PartyLeader && chatReader.AssistIsFollowing)
        {
            ClearAssistReturnState();
            navigation.ClearAllRoutes();
            RefillWaypoints(true);
        }
        else if (classConfig.Mode == Mode.PartyLeader && chatReader.AssistRequestReturn)
        {
            Vector3 assistWaypoint = new Vector3(chatReader.AssistXPos, chatReader.AssistYPos, playerReader.MapPos.Z);
            logger.LogInformation("FollowRouteGoal: Resume - Calling GoToOneWaypoint of " + assistWaypoint);
            GoToOneWaypoint(assistWaypoint);
        }
        else
        {
            RefillWaypoints(false);
        }

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
                    if (!chatReader.AssistIsFollowing)
                    {
                        logger.LogInformation("FollowRouteGoal: OnGoapEvent - assist stopped following");
                        Abort();
                    }
                    else
                    {
                        logger.LogInformation("FollowRouteGoal: OnGoapEvent - assist is following again");

                        // IMPORTANT:
                        // If we were in assist-return mode, clear it first so Resume() does not
                        // get pulled back into the stale assist-return waypoint path.
                        ClearAssistReturnState();

                        // Clear any one-off nav state from the assist return move.
                        navigation.ClearAllRoutes();

                        Resume();
                    }

                    break;

                case GoapKey.assistrequestreturn:
                    if (chatReader.AssistRequestReturn)
                    {
                        logger.LogInformation("FollowRouteGoal: OnGoapEvent - AssistRequestReturn to X: "
                            + chatReader.AssistXPos
                            + " Y: "
                            + chatReader.AssistYPos);

                        Vector3 assistWaypoint = new Vector3(chatReader.AssistXPos, chatReader.AssistYPos, playerReader.MapPos.Z);
                        logger.LogInformation("FollowRouteGoal: OnGoapEvent - Calling GoToOneWaypoint of " + assistWaypoint);
                        GoToOneWaypoint(assistWaypoint);
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
                        logger.LogInformation("FollowRouteGoal: OnGoapEvent - Party entered Combat while trying to follow route, trying to exit!");
                        Abort();
                    }

                    break;
            }
        }

        if (e.GetType() == typeof(AbortEvent))
        {
            Abort();
        }
        else if (e.GetType() == typeof(ResumeEvent))
        {
            Resume();
        }
    }

    public override void OnEnter() => Resume();

    public override void OnExit() => Abort();

    public override void Update()
    {
        // 1) Consume pause requests (from side thread)
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
                LogWarning($"Unable to clear target! Check Bindpad settings!");
            }
        }

        if (bits.Drowning())
        {
            input.PressJump();
        }

        if (IsSuppressedBlacklistedTarget())
        {
            Log("Suppressed recently-blacklisted target reacquired, clearing again");
            input.PressClearTarget();
            wait.Update();
            return;
        }

        if (!chatReader.AssistIsFollowing && !chatReader.AssistRequestReturn && classConfig.Mode == Mode.PartyLeader) 
        {
            logger.LogInformation("Assist Is NOT following AND Mode is PartyLeader");
            Abort();
            return;
        }

        // 2) Determine whether we WANT navigation paused this tick
        bool wantNavPaused = bits.Target() && bits.Target_Hostile() 
            && bits.Target_Alive() && !bits.Target_Tagged() && playerReader.WithInCombatRange()
            && !targetBlacklist.Is();
            // && playerReader.WithInPullRange() 

        // 3) If policy says pause, do it (main thread)
        if (wantNavPaused && !_pausedByLocalLogic)
        {
            logger.LogInformation("[FRG] Target acquired -> stopping navigation");
            navigation.PausePathing();
            _pausedByLocalLogic = true;
        }

        if (!wantNavPaused && bits.Target() && !bits.Target_Dead())
        {
            Log("Target did not meet requirements.");
            Log($"bits.Target(): {bits.Target()}");
            Log($"bits.Target_Hostile(): {bits.Target_Hostile()}");
            Log($"bits.Target_Alive(): {bits.Target_Alive()}");
            Log($"!bits.Target_Tagged(): {!bits.Target_Tagged()}");
            Log($"playerReader.WithInCombatRange(): {playerReader.WithInCombatRange()}");
            Log($"playerReader.WithInPullRange(): {playerReader.WithInPullRange()}");
            Log($"!targetBlacklist.Is(): {!targetBlacklist.Is()}");

            input.PressClearTarget();
            wait.Update();

            // If we rejected the target, resume searching immediately.
            // Otherwise the side thread can remain paused forever (sideActivityManualReset.Reset() happened on "Found target!").
            targetFinder.Reset();          // safe even if already reset
            sideActivityManualReset.Set(); // re-arm the scanning thread

            // If nav was paused due to prior target logic, resume it now.
            // Don't just clear the flag, because that can strand nav in a paused state.
            if (_pausedByLocalLogic)
            {
                navigation.Resume();
                _pausedByLocalLogic = false;
            }

            // Also clear any pending pause request that might be queued from earlier timing.
            Interlocked.Exchange(ref _pauseNavRequested, 0);
        }

        // 4) If policy says resume, request it and consume it (main thread)
        if (!wantNavPaused && _pausedByLocalLogic)
        {
            // You can either call Resume() directly...
            navigation.Resume();
            _pausedByLocalLogic = false;
        }

        // Assist return state machine: active-time timeout + rewind retry
        if (_assistReturnActive)
        {
            // Tick active-time timeout (won't count time while paused/combat)
            TickAssistReturnTimeout(wantNavPaused);

            // If TickAssistReturnTimeout aborted it, stop here
            if (!_assistReturnActive)
                return;

            if (_assistRewindActive)
            {
                float distToAnchor = playerReader.WorldPos.WorldDistanceXYTo(_assistRewindAnchorW);

                // When anchor is reached, retry assist target once
                if (distToAnchor < 2.5f)
                {
                    _assistRewindActive = false;

                    logger.LogInformation($"[FRG] AssistReturn rewind reached. Retrying assist target {_assistReturnTargetW}");
                    navigation.SetSingleWaypoint(_assistReturnTargetW);
                }
            }
        }

        // Drive navigation only when not paused
        if (!wantNavPaused)
        {
            //logger.LogInformation($"[FRG] Calling navigation.Update navHash={navigation.GetHashCode()}");
            navigation.Update(CancellationToken.None);
        }

        // TODO moved from assistrequestreturn check and past the navigation resume checks
        // test that this still works
        if (bits.Combat() && classConfig.Mode != Mode.AttendedGather) { return; }

        RandomJump();

        wait.Update();
    }

    private bool IsDuplicateRecentRefill(Vector3 topMapPoint, int waypointCount)
    {
        if (_lastRefillWaypointCount != waypointCount)
            return false;

        if (_lastRefillTopMap == default)
            return false;

        float d = _lastRefillTopMap.MapDistanceXYTo(topMapPoint);
        if (d > 0.01f)
            return false;

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
        if (!bits.Target())
            return;

        _suppressedBlacklistedGuid = playerReader.TargetGuid;
        _suppressedBlacklistedUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
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
                if(bits.Target() && bits.TargetTarget_PlayerOrPet() 
                    && playerReader.IsIgnored(playerReader.TargetGuid) 
                    && (bits.Combat() || bits.Focus_Combat()) )
                {
                    Log("Found target area blacklisted target, but they are targeting us and we are in combat!");
                    sideActivityManualReset.Reset();   // pause searching
                    targetFinder.Reset();              // optional
                    Interlocked.Exchange(ref _pauseNavRequested, 1);
                }
                else if (bits.Target() && targetBlacklist.Is())
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

                    // Only pause searching if we actually intend to act on this target soon.
                    // Otherwise keep searching and let Update() clear/ignore it naturally.
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
                        sideActivityManualReset.Reset();   // pause searching
                        targetFinder.Reset();
                        Interlocked.Exchange(ref _pauseNavRequested, 1);
                    }
                    else
                    {
                        // Not actionable: keep searching and avoid deadlocking the scanner.
                        // Optionally clear immediately here, but Update() already handles it.
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
            {
                AlternateGatherTypes();
            }
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
            logger.LogInformation($"[{oldestKey.Key}] {oldestKey.Name} pressed for {InputDuration.DefaultPress}ms");
            input.PressRandom(oldestKey);
            oldestKey.SetClicked();
        }
    }

    private void TickAssistReturnTimeout(bool wantNavPaused)
    {
        if (!_assistReturnActive)
            return;

        var now = DateTime.UtcNow;

        // Initialize the timer on first tick so we don't count a giant delta.
        if (!_assistReturnTimerInit)
        {
            _assistReturnTimerInit = true;
            _assistReturnLastTickUtc = now;
            _assistReturnActiveElapsed = TimeSpan.Zero;
            return;
        }

        // Only count time when we are actually allowed to drive navigation.
        // This prevents timeout while paused for combat/targets or other local logic.
        bool countActive =
            !wantNavPaused &&
            !_pausedByLocalLogic &&
            !bits.Combat() &&
            !bits.Focus_Combat();

        if (countActive)
        {
            _assistReturnActiveElapsed += (now - _assistReturnLastTickUtc);
        }

        _assistReturnLastTickUtc = now;

        if (_assistReturnActiveElapsed.TotalSeconds >= ASSIST_RETURN_TIMEOUT_ACTIVE_SEC)
        {
            AbortAssistReturn($"timeout (active={_assistReturnActiveElapsed.TotalSeconds:0.0}s)");
        }
    }

    private void BeginAssistReturn(Vector3 assistTargetW)
    {
        _assistReturnActive = true;
        _assistReturnTargetW = assistTargetW;

        _assistRewindActive = false;
        _assistRewindAnchorW = default;

        _assistAttempt = 0;

        // Active-time timeout init
        _assistReturnActiveElapsed = TimeSpan.Zero;
        _assistReturnTimerInit = false;          // will init on first TickAssistReturnTimeout
        _assistReturnLastTickUtc = DateTime.UtcNow;

        logger.LogInformation($"[FRG] AssistReturn begin -> {assistTargetW}");
    }

    private void AbortAssistReturn(string reason)
    {
        if (!_assistReturnActive)
            return;

        logger.LogWarning($"[FRG] AssistReturn abort: {reason}");

        ClearAssistReturnState();

        // Clear only the local single-waypoint routing; the normal route logic will refill next.
        navigation.ClearAllRoutes();
    }

    private void ClearAssistReturnState()
    {
        _assistReturnActive = false;
        _assistRewindActive = false;
        _assistAttempt = 0;

        _assistReturnTargetW = default;
        _assistRewindAnchorW = default;

        _assistReturnActiveElapsed = TimeSpan.Zero;
        _assistReturnLastTickUtc = DateTime.UtcNow;
        _assistReturnTimerInit = false;
    }

    private void Navigation_OnPathFailed(Vector3 startW, Vector3 endW)
    {
        if (!_assistReturnActive)
            return;

        // Only react if the failed destination matches our assist target (within tolerance)
        if (endW.WorldDistanceXYTo(_assistReturnTargetW) > 3.0f)
            return;

        // If we already tried rewind, abort (prevents ping-pong / runaway)
        if (_assistAttempt >= 1)
        {
            AbortAssistReturn("Path failed after rewind retry");
            return;
        }

        // Need an anchor to rewind to
        if (!navigation.HasLastSafeAnchor)
        {
            AbortAssistReturn("No last safe anchor available for rewind");
            return;
        }

        var anchor = navigation.LastSafeAnchorW;

        // Safety: if anchor is inside blacklist, don't rewind into it
        if (navigation.AreaBlacklist != null && navigation.AreaBlacklist.ContainsWorld(anchor))
        {
            AbortAssistReturn("Last safe anchor is inside blacklist");
            return;
        }

        _assistAttempt = 1;
        _assistRewindActive = true;
        _assistRewindAnchorW = anchor;

        logger.LogWarning($"[FRG] AssistReturn path failed. Rewind to anchor={anchor} then retry target={_assistReturnTargetW}");

        // Override route to go to anchor first
        navigation.SetSingleWaypoint(anchor);
    }

    private void MountIfPossible()
    {
        float totalDistance = VectorExt.TotalDistance<Vector3>(navigation.TotalRoute, VectorExt.WorldDistanceXY);

        if (classConfig.UseMount && mountHandler.CanMount() &&
            (MountHandler.ShouldMount(totalDistance) ||
            (navigation.TotalRoute.Length > 0 &&
            mountHandler.ShouldMount(navigation.TotalRoute[^1]))
            ))
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
        if (debug)
            LogDebug("Navigation_OnDestinationReached");

        if (classConfig.Mode == Mode.PartyLeader && (_assistReturnActive || _assistRewindActive))
        {
            logger.LogInformation("[FRG] AssistReturn destination reached.");

            ClearAssistReturnState();

            // Intentionally idle until assist-following signal arrives
            navigation.PausePathing();
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
        return;
    }

    public void GoToOneWaypoint(Vector3 waypointToGoTo)
    {
        logger.LogInformation("FollowRouteGoal: GoToOneWaypoint!");

        // If this is the PartyLeader responding to assist request, enable rewind/retry logic.
        if (classConfig.Mode == Mode.PartyLeader && chatReader.AssistRequestReturn)
        {
            if (!_assistReturnActive || _assistReturnTargetW.WorldDistanceXYTo(waypointToGoTo) > 1.0f)
                BeginAssistReturn(waypointToGoTo);
        }
        else
        {
            // If something else is forcing a one-off move, disable assist mode
            ClearAssistReturnState();
        }

        ResetRefillWaypointsGuard();

        navigation.SetWayPoints(stackalloc Vector3[1] { waypointToGoTo });
    }

    private static int ChooseForwardResumeIndex(ReadOnlySpan<Vector3> pathMap, Vector3 playerMap, int closestIndex)
    {
        if (pathMap.Length == 0)
            return 0;

        if (closestIndex < 0)
            return 0;

        if (closestIndex >= pathMap.Length - 1)
            return closestIndex;

        Vector3 a = pathMap[closestIndex];
        Vector3 b = pathMap[closestIndex + 1];

        float abX = b.X - a.X;
        float abY = b.Y - a.Y;
        float abLenSq = (abX * abX) + (abY * abY);

        // Degenerate segment, keep closest
        if (abLenSq <= 0.0001f)
            return closestIndex;

        float apX = playerMap.X - a.X;
        float apY = playerMap.Y - a.Y;

        // Projection of player onto segment A->B in map space
        float t = ((apX * abX) + (apY * abY)) / abLenSq;

        float dA = playerMap.MapDistanceXYTo(a);
        float dB = playerMap.MapDistanceXYTo(b);

        // Prefer the next point if:
        // 1) player is already past the midpoint of the segment, or
        // 2) next point is almost as close as the closest point
        if (t >= 0.55f || dB <= dA + 0.75f)
            return closestIndex + 1;

        return closestIndex;
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
        float mapDistanceToLast = playerMap.MapDistanceXYTo(pathMap[^1]);

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
            if (debug)
                LogDebug($"{nameof(RefillWaypoints)}: Closest wayPoint: {mapClosestPoint}");

            if (canSkipDuplicateRefill && IsDuplicateRecentRefill(mapClosestPoint, 1))
            {
                Log($"{nameof(RefillWaypoints)} - skipped duplicate recent closest refill");
                return;
            }

            RecordRefillWaypoints(mapClosestPoint, 1);
            navigation.SetWayPoints(stackalloc Vector3[1] { mapClosestPoint });
            return;
        }

        // Compute a forward-biased resume index once, and reuse it below.
        int resumeIndex = closestIndex;

        // If we're extremely close to the current closest point and there is a next point,
        // bias forward so we don't keep re-adding the point we just passed.
        if (resumeIndex < pathMap.Length - 1)
        {
            float dHere = playerMap.MapDistanceXYTo(pathMap[resumeIndex]);
            float dNext = playerMap.MapDistanceXYTo(pathMap[resumeIndex + 1]);

            if (dHere < 1.5f || dNext <= dHere * 1.25f)
            {
                resumeIndex++;
            }
        }

        // ------------------------------
        // There-and-back: preserve direction across pauses/resumes.
        // Do NOT choose by nearest endpoint every time.
        // ------------------------------
        if (pathSettings.PathThereAndBack)
        {
            // First time direction is unknown: infer from nearest endpoint.
            // Near the start -> go forward, near the end -> go backward.
            if (_pathTraversalDirection == 0)
            {
                _pathTraversalDirection = mapDistanceToFirst <= mapDistanceToLast ? 1 : -1;
            }

            // If we're actually at an endpoint, flip/lock direction appropriately.
            if (closestIndex == 0)
            {
                _pathTraversalDirection = 1;
            }
            else if (closestIndex == pathMap.Length - 1)
            {
                _pathTraversalDirection = -1;
            }

            if (_pathTraversalDirection > 0)
            {
                Span<Vector3> forwardPoints = pathMap[resumeIndex..];

                if (forwardPoints.Length == 0)
                    return;

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
                Span<Vector3> backwardPoints = stackalloc Vector3[closestIndex + 1];
                for (int i = 0; i <= closestIndex; i++)
                {
                    backwardPoints[i] = pathMap[closestIndex - i];
                }

                if (backwardPoints.Length == 0)
                    return;

                if (canSkipDuplicateRefill && IsDuplicateRecentRefill(backwardPoints[0], backwardPoints.Length))
                {
                    Log($"{nameof(RefillWaypoints)} - skipped duplicate recent backward refill");
                    return;
                }

                RecordRefillWaypoints(backwardPoints[0], backwardPoints.Length);

                Log($"{nameof(RefillWaypoints)} - Set destination from backward resume point to start - with {backwardPoints.Length} waypoints");
                navigation.SetWayPoints(backwardPoints);
            }

            return;
        }

        // One-way route: always continue forward from the closest resume point.
        // Never choose "nearest endpoint", because that can send us backwards after
        // temporary interruptions.
        Span<Vector3> points = pathMap[resumeIndex..];

        if (points.Length == 0)
            return;

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
        // TODO: Cheap way to avoid override all FollowRouteGoal
        // to the same path
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

    private void LogDebug(string text)
    {
        logger.LogDebug(text);
    }

    private void LogWarning(string text)
    {
        logger.LogWarning(text);
    }

    private void Log(string text)
    {
        logger.LogInformation(text);
    }
}