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
            //sideActivityCts = new CancellationTokenSource();
            //var localCts = sideActivityCts;

            //sideActivityThread = new Thread(() => Thread_LookingForTarget(localCts));
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
            //navigation.StopMovement();

        navigation.PausePathing();
        //navigation.StopMovement(); // optional, if combat wants to manage movement itself

        sideActivityManualReset.Reset();
        targetFinder.Reset();
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

        if (sideActivityCts.IsCancellationRequested)
        {
            sideActivityCts = new();
        }
        sideActivityManualReset.Set();

        // If we are the PartyLeader and our assist has requested a return to them
        // let's use their location as a waypoint and go to them.
        if (classConfig.Mode == Mode.PartyLeader && chatReader.AssistRequestReturn)
        {
            Vector3 assistWaypoint = new Vector3(chatReader.AssistXPos, chatReader.AssistYPos, playerReader.MapPos.Z);
            logger.LogInformation("FollowRouteGoal: Resume - Calling GoToOneWaypoint of " + assistWaypoint);
            GoToOneWaypoint(assistWaypoint);
        }
        else if (!navigation.HasWaypoint())
        {
            RefillWaypoints(true);
        }
        else
        {
            navigation.Resume();
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
                    if(!chatReader.AssistIsFollowing)
                    {
                        logger.LogInformation("FollowRouteGoal: OnGoapEvent - !chatReader.AssistIsFollowing");
                        Abort();
                    }
                    else if(chatReader.AssistIsFollowing)
                    {
                        logger.LogInformation("FollowRouteGoal: OnGoapEvent - !chatReader.AssistIsFollowing");
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

        if (!chatReader.AssistIsFollowing && !chatReader.AssistRequestReturn && classConfig.Mode == Mode.PartyLeader) 
        {
            logger.LogInformation("Assist Is NOT following AND Mode is PartyLeader");
            Abort();
            return;
        }

        if (bits.Combat() && classConfig.Mode != Mode.AttendedGather) { return; }

        // 2) Determine whether we WANT navigation paused this tick
        bool wantNavPaused = bits.Target() && bits.Target_Hostile() 
            && bits.Target_Alive() && !bits.Target_Tagged() && playerReader.WithInCombatRange()
            && playerReader.WithInPullRange();
        
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
            input.PressClearTarget();
            wait.Update();
        }

        // 4) If policy says resume, request it and consume it (main thread)
        if (!wantNavPaused && _pausedByLocalLogic)
        {
            // You can either call Resume() directly...
            navigation.Resume();
            _pausedByLocalLogic = false;

            // ...or if you prefer to keep it “request based”:
            // Interlocked.Exchange(ref _resumeNavRequested, 1);
        }

        if (!wantNavPaused && !suppressNavigation)
        {
            logger.LogInformation($"[FRG] Calling navigation.Update navHash={navigation.GetHashCode()}");
            navigation.Update(CancellationToken.None);
        }

        RandomJump();

        wait.Update();
    }

    private void Thread_LookingForTarget()
    {

        //sideActivityManualReset.Wait();

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
                    //sideActivityCts.Cancel();
                    //sideActivityManualReset.Reset();
                    //navigation.PausePathing();
                    //navigation.StopMovement(); // optional, if combat wants to manage movement itself
                }
                else if (bits.Target() && targetBlacklist.Is())
                {
                    Log("Blacklisted target found, clearing target");
                    input.PressClearTarget();
                    wait.Update();
                }
                else
                {
                    Log("Found target!");
                    sideActivityManualReset.Reset();   // pause searching
                    targetFinder.Reset();
                    Interlocked.Exchange(ref _pauseNavRequested, 1);// optional
                    //sideActivityCts.Cancel();
                    //sideActivityManualReset.Reset();
                    //navigation.PausePathing();
                    //navigation.StopMovement(); // optional, if combat wants to manage movement itself
                } 
            }

            wait.Update();
            //sideActivityManualReset.Wait();
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
        if (classConfig.Mode == Mode.PartyLeader && chatReader.AssistRequestReturn)
        {
            //Vector3 assistWaypoint = new Vector3(chatReader.AssistXPos, chatReader.AssistYPos, playerReader.MapPos.Z);
            //logger.LogInformation("FollowRouteGoal: Resume - Calling GoToOneWaypoint of " + assistWaypoint);
            //GoToOneWaypoint(assistWaypoint);
            
            // Just wait for assist to say i'm following or
            // for another request to return.
            return;
        } else
        {
            RefillWaypoints(false);
        }
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
        navigation.SetWayPoints(stackalloc Vector3[1] { waypointToGoTo });
        return;
    }

    public void RefillWaypoints(bool onlyClosest)
    {
        Log($"{nameof(RefillWaypoints)} - findClosest:{onlyClosest} - ThereAndBack:{pathSettings.PathThereAndBack}");

        Vector3 playerMap = playerReader.MapPos;

        Span<Vector3> pathMap = stackalloc Vector3[mapRoute.Length];
        mapRoute.CopyTo(pathMap);

        float mapDistanceToFirst = playerMap.MapDistanceXYTo(pathMap[0]);
        float mapDistanceToLast = playerMap.MapDistanceXYTo(pathMap[^1]);

        if (mapDistanceToLast < mapDistanceToFirst)
        {
            pathMap.Reverse();
        }

        int closestIndex = 0;
        Vector3 mapClosestPoint = Vector3.Zero;
        float distance = float.MaxValue;

        for (int i = 0; i < pathMap.Length; i++)
        {
            Vector3 p = pathMap[i];
            float d = playerMap.MapDistanceXYTo(p);
            if (d < distance)
            {
                distance = d;
                closestIndex = i;
                mapClosestPoint = p;
            }
        }

        if (onlyClosest)
        {
            if (debug)
                LogDebug($"{nameof(RefillWaypoints)}: Closest wayPoint: {mapClosestPoint}");

            navigation.SetWayPoints(stackalloc Vector3[1] { mapClosestPoint });

            return;
        }

        if (mapClosestPoint == pathMap[0] || mapClosestPoint == pathMap[^1])
        {
            if (pathSettings.PathThereAndBack)
            {
                navigation.SetWayPoints(pathMap);
            }
            else
            {
                pathMap.Reverse();
                navigation.SetWayPoints(pathMap);
            }
        }
        else
        {
            Span<Vector3> points = pathMap[closestIndex..];
            Log($"{nameof(RefillWaypoints)} - Set destination from closest to nearest endpoint - with {points.Length} waypoints");
            navigation.SetWayPoints(points);
        }
    }

    #endregion

    public void ReceivePath(Vector3[] oldMap, Vector3[] newMap)
    {
        // TODO: Cheap way to avoid override all FollowRouteGoal
        // to the same path
        if (mapRoute.SequenceEqual(oldMap))
        {
            this.mapRoute = newMap;
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