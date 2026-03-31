using Core.AreaBlacklist;
using Core.GOAP;

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
    private volatile bool _disposing;

    // Fix #5/#6: removed dead fields suppressNavigation, navStoppedForTarget, _resumeNavRequested

    private int _pauseNavRequested;             // set by worker thread
    private volatile bool _pausedByLocalLogic;  // tracks if we paused nav due to target/local movement

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

    // True after reaching assist destination, paused waiting for "i'm following".
    // Cleared in OnGoapEvent(assistisfollowing=true) or ClearAssistReturnState.
    private bool _assistWaitingForFollowing;

    // True after the leader replied to a position request.
    // Leader pauses patrol and waits for the assist to either confirm following
    // (AssistIsFollowing) or send AssistCantFollow (AssistRequestReturn).
    private bool _waitingForAssistAfterPosition;
    private DateTime _waitingForAssistAfterPositionStartUtc;



    /// <summary>
    /// True whenever the leader is in any "busy with assist" state:
    /// waiting after a position reply, actively navigating back to the assist,
    /// or paused at the destination waiting for "i'm following".
    /// Used by GoapAgent to suppress housekeeping goals (Adhoc etc.) during this window.
    /// </summary>
    public bool WaitingForAssist =>
        _waitingForAssistAfterPosition || _assistReturnActive || _assistWaitingForFollowing;

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

        if (classConfig.Mode == Mode.PartyLeader)
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
            // 1. Unsubscribe events FIRST so no callbacks fire during teardown
            navigation.OnPathCalculated -= Navigation_OnPathCalculated;
            navigation.OnDestinationReached -= Navigation_OnDestinationReached;
            navigation.OnWayPointReached -= Navigation_OnWayPointReached;
            navigation.OnPathFailed -= Navigation_OnPathFailed;
            if (classConfig.Mode == Mode.AttendedGather)
                navigation.OnAnyPointReached -= Navigation_OnWayPointReached;

            // 2. Then stop the side thread
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
        if (!targetBlacklist.Is())
            navigation.StopMovement();

        navigation.PausePathing();

        sideActivityManualReset.Reset();
        targetFinder.Reset();
        ResetRefillWaypointsGuard();
    }

    private void Resume()
    {
        // If we were waiting for the assist after a position reply, only clear
        // that state if the assist situation has actually been resolved.
        // If neither AssistIsFollowing nor AssistRequestReturn is true, the assist
        // is still navigating toward us — preserve the pause instead of resuming patrol.
        if (_waitingForAssistAfterPosition)
        {
            if (classConfig.Mode == Mode.PartyLeader &&
                !chatReader.AssistIsFollowing &&
                !chatReader.AssistRequestReturn)
            {
                // Assist hasn't arrived yet. Re-apply the pause and return —
                // don't resume patrol just because the GOAP planner cycled through another goal.
                logger.LogInformation("[FRG] Resume: assist still navigating — re-applying position wait pause.");
                if (sideActivityCts.IsCancellationRequested)
                    sideActivityCts = new();
                sideActivityManualReset.Set();
                navigation.PausePathing();
                return;
            }

            // Assist situation resolved — clear the flag and proceed with normal resume.
            logger.LogInformation("[FRG] Resume: clearing _waitingForAssistAfterPosition (assist situation resolved).");
            _waitingForAssistAfterPosition = false;
        }
        // Apply per-route area blacklists (map rects -> world rects)
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

        logger.LogInformation(
            $"[FRG] Resume: HasWaypoint={navigation.HasWaypoint()} HasNext={navigation.HasNext()} " +
            $"AssistIsFollowing={chatReader.AssistIsFollowing} AssistRequestReturn={chatReader.AssistRequestReturn} " +
            $"navActive={navigation.Active} wp={navigation.WaypointCount} route={navigation.RouteCount}");

        // If navigation already has progress, preserve it.
        if (navigation.HasWaypoint() || navigation.HasNext())
        {
            logger.LogInformation("[FRG] Resume - preserving existing navigation progress");
            navigation.Resume();
        }
        else if (classConfig.Mode == Mode.PartyLeader && chatReader.AssistIsFollowing)
        {
            logger.LogInformation("[FRG] Resume - AssistIsFollowing branch -> RefillWaypoints");
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
                    if (!chatReader.AssistIsFollowing)
                    {
                        logger.LogInformation("FollowRouteGoal: OnGoapEvent - assist stopped following");
                        Abort();
                    }
                    else
                    {
                        logger.LogInformation("FollowRouteGoal: OnGoapEvent - assist is following again");

                        bool wasWaiting = _assistWaitingForFollowing;
                        bool wasWaitingAfterPosition = _waitingForAssistAfterPosition;
                        _waitingForAssistAfterPosition = false;

                        ClearAssistReturnState(); // also clears _assistWaitingForFollowing

                        logger.LogInformation(
                            $"[FRG] OnGoapEvent assistisfollowing=true: " +
                            $"wasWaiting={wasWaiting} wasWaitingAfterPosition={wasWaitingAfterPosition} " +
                            $"navActive={navigation.Active} wp={navigation.WaypointCount} route={navigation.RouteCount}");

                        if (wasWaiting || wasWaitingAfterPosition)
                            logger.LogInformation("[FRG] Assist confirmed following - resuming patrol from pause.");

                        navigation.ClearAllRoutes();
                        Resume();
                        logger.LogInformation(
                            $"[FRG] OnGoapEvent assistisfollowing=true after Resume: " +
                            $"navActive={navigation.Active} wp={navigation.WaypointCount} route={navigation.RouteCount}");
                    }

                    break;

                case GoapKey.assistrequestreturn:
                    if (chatReader.AssistRequestReturn)
                    {
                        logger.LogInformation("FollowRouteGoal: OnGoapEvent - AssistRequestReturn to X: "
                            + chatReader.AssistXPos
                            + " Y: "
                            + chatReader.AssistYPos);

                        // If we're already actively navigating back to the assist, don't restart.
                        // The assist position has drifted because they are now moving toward us —
                        // chasing their updated position creates a crossing-paths loop where both
                        // bots walk past each other indefinitely.
                        if (_assistReturnActive)
                        {
                            logger.LogInformation(
                                "[FRG] OnGoapEvent assistrequestreturn: AssistReturn already active — " +
                                "ignoring updated position to avoid crossing-paths loop.");
                            break;
                        }

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

    /// <summary>
    /// Called by GoapAgent immediately after pressing LeaderReplyPosition.
    /// The assist has just asked "where are you?" meaning they are about to navigate TO the leader.
    /// The leader must stop moving and wait — navigating toward the assist here would cause both
    /// bots to walk toward each other's last-known positions (crossing-paths loop).
    /// 
    /// Navigation toward the assist only happens via OnGoapEvent(assistrequestreturn), which fires
    /// when the assist has given up following and needs the leader to come to them instead.
    /// </summary>
    public void PauseForAssistNavigation()
    {
        if (chatReader.AssistIsFollowing)
        {
            logger.LogInformation("[FRG] PauseForAssistNavigation: assist already following — ignoring stale position request.");
            return;
        }

        // The assist sent "leader what is your position?" — they are about to navigate TO us.
        // Stop moving and wait for them to arrive regardless of any prior AssistRequestReturn state.
        // Do NOT navigate toward the assist here — that would create a crossing-paths loop.
        if (_assistReturnActive)
        {
            // We were navigating to the assist, but now they're navigating to us instead.
            // Cancel our return and hold position.
            logger.LogInformation("[FRG] PauseForAssistNavigation: cancelling in-progress AssistReturn — assist is now navigating to us.");
            navigation.StopMovement();
            ClearAssistReturnState();
        }

        navigation.PausePathing();

        if (!_waitingForAssistAfterPosition)
        {
            logger.LogInformation("[FRG] PauseForAssistNavigation: assist requested position — stopping and waiting for assist to arrive.");
            _waitingForAssistAfterPosition = true;
            _waitingForAssistAfterPositionStartUtc = DateTime.UtcNow;
        }
    }

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

        if (_waitingForAssistAfterPosition)
        {
            // Stay paused until the assist either confirms following or
            // sends a fresh AssistCantFollow (which sets AssistRequestReturn).
            // AssistIsFollowing is handled by OnGoapEvent which clears the flag and resumes.
            //
            // Note: ChatReader clears AssistRequestReturn when "leader what is your position?"
            // arrives, so any AssistRequestReturn seen here is genuinely fresh — the assist
            // gave up navigating to us and wants us to come to them instead.
            if (chatReader.AssistRequestReturn)
            {
                double waited = (DateTime.UtcNow - _waitingForAssistAfterPositionStartUtc).TotalSeconds;
                logger.LogInformation($"[FRG] AssistRequestReturn received while waiting after position reply ({waited:0.0}s) — navigating to assist.");
                _waitingForAssistAfterPosition = false;
                Vector3 assistWaypoint = new Vector3(chatReader.AssistXPos, chatReader.AssistYPos, playerReader.MapPos.Z);
                GoToOneWaypoint(assistWaypoint);
                return;
            }

            // Timeout: if the assist still hasn't arrived after ASSIST_RETURN_TIMEOUT_ACTIVE_SEC,
            // give up waiting and let the GOAP cycle re-evaluate.
            double waitedSec = (DateTime.UtcNow - _waitingForAssistAfterPositionStartUtc).TotalSeconds;
            if (waitedSec >= ASSIST_RETURN_TIMEOUT_ACTIVE_SEC)
            {
                logger.LogWarning($"[FRG] Timed out waiting for assist to arrive after position reply ({waitedSec:0.0}s). Resuming patrol.");
                _waitingForAssistAfterPosition = false;
                return;
            }

            // Still waiting for the assist to arrive — hold position.
            wait.Update();
            return;
        }

        // 2) Determine whether we WANT navigation paused this tick
        bool wantNavPaused = bits.Target() && bits.Target_Hostile()
            && bits.Target_Alive() && !bits.Target_Tagged() && playerReader.WithInCombatRange()
            && !targetBlacklist.Is();

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

            targetFinder.Reset();
            sideActivityManualReset.Set();

            if (_pausedByLocalLogic)
            {
                navigation.Resume();
                _pausedByLocalLogic = false;
            }

            Interlocked.Exchange(ref _pauseNavRequested, 0);
        }

        // 4) If policy says resume, request it and consume it (main thread)
        if (!wantNavPaused && _pausedByLocalLogic)
        {
            navigation.Resume();
            _pausedByLocalLogic = false;
        }

        // Assist return state machine: active-time timeout + rewind retry
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

                    logger.LogInformation($"[FRG] AssistReturn rewind reached. Retrying assist target {_assistReturnTargetW}");
                    navigation.SetSingleWaypoint(_assistReturnTargetW);
                }
            }
        }

        // Drive navigation only when not paused
        if (!wantNavPaused)
        {
            navigation.Update(CancellationToken.None);
        }

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
                if (bits.Target() && bits.TargetTarget_PlayerOrPet()
                    && playerReader.IsIgnored(playerReader.TargetGuid)
                    && (bits.Combat() || bits.Focus_Combat()))
                {
                    Log("Found target area blacklisted target, but they are targeting us and we are in combat!");
                    sideActivityManualReset.Reset();
                    targetFinder.Reset();
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

        if (!_assistReturnTimerInit)
        {
            _assistReturnTimerInit = true;
            _assistReturnLastTickUtc = now;
            _assistReturnActiveElapsed = TimeSpan.Zero;
            return;
        }

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

        _assistReturnActiveElapsed = TimeSpan.Zero;
        _assistReturnTimerInit = false;
        _assistReturnLastTickUtc = DateTime.UtcNow;

        logger.LogInformation($"[FRG] AssistReturn begin -> {assistTargetW}");
    }

    private void AbortAssistReturn(string reason)
    {
        if (!_assistReturnActive)
            return;

        logger.LogWarning($"[FRG] AssistReturn abort: {reason}");

        ClearAssistReturnState();

        navigation.ClearAllRoutes();
    }

    private void ClearAssistReturnState()
    {
        _assistReturnActive = false;
        _assistRewindActive = false;
        _assistWaitingForFollowing = false;
        _waitingForAssistAfterPosition = false;
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

        if (endW.WorldDistanceXYTo(_assistReturnTargetW) > 3.0f)
            return;

        if (_assistAttempt >= 1)
        {
            AbortAssistReturn("Path failed after rewind retry");
            return;
        }

        if (!navigation.HasLastSafeAnchor)
        {
            AbortAssistReturn("No last safe anchor available for rewind");
            return;
        }

        var anchor = navigation.LastSafeAnchorW;

        if (navigation.AreaBlacklist != null && navigation.AreaBlacklist.ContainsWorld(anchor))
        {
            AbortAssistReturn("Last safe anchor is inside blacklist");
            return;
        }

        _assistAttempt = 1;
        _assistRewindActive = true;
        _assistRewindAnchorW = anchor;

        logger.LogWarning($"[FRG] AssistReturn path failed. Rewind to anchor={anchor} then retry target={_assistReturnTargetW}");

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
            logger.LogInformation(
                $"[FRG] AssistReturn destination reached. " +
                $"AssistIsFollowing={chatReader.AssistIsFollowing} " +
                $"AssistRequestReturn={chatReader.AssistRequestReturn} " +
                $"navActive={navigation.Active} wp={navigation.WaypointCount} route={navigation.RouteCount}");

            ClearAssistReturnState();

            // If "i'm following" already arrived before we reached the destination,
            // resume normal patrol immediately — no need to pause and wait.
            if (chatReader.AssistIsFollowing)
            {
                logger.LogInformation("[FRG] AssistReturn destination reached - assist already following, resuming patrol.");
                navigation.ClearAllRoutes();
                Resume();
                return;
            }

            // "i'm following" hasn't arrived yet. Pause and wait; OnGoapEvent(assistisfollowing=true)
            // will call Resume() -> RefillWaypoints when it does.
            _assistWaitingForFollowing = true;
            navigation.PausePathing();
            logger.LogInformation(
                $"[FRG] AssistReturn destination reached - pausing until assist confirms following. " +
                $"navActive={navigation.Active} wp={navigation.WaypointCount} route={navigation.RouteCount}");
            return;
        }

        // Do NOT call ResetRefillWaypointsGuard() here.
        // When OnDestinationReached fires, waypoints=0 and route=0, so canSkipDuplicateRefill=false
        // in RefillWaypoints, meaning the duplicate guard is already bypassed — the reset was always
        // redundant. Worse, resetting it also cleared the recorded refill from the PREVIOUS call,
        // making it impossible for the guard to detect the tight-loop in any future path.
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

        if (classConfig.Mode == Mode.PartyLeader && chatReader.AssistRequestReturn)
        {
            // Always begin/reset the assist return — this resets the active-time
            // timer so stale elapsed time from a previous attempt doesn't cause
            // an immediate timeout abort on the very first Update() tick.
            BeginAssistReturn(waypointToGoTo);
        }
        else
        {
            ClearAssistReturnState();
        }

        ResetRefillWaypointsGuard();

        navigation.SetWayPoints(stackalloc Vector3[1] { waypointToGoTo });
    }

    // Fix #4: ChooseForwardResumeIndex is removed — the inline logic in RefillWaypoints
    // is the single authoritative implementation. Having both was a maintenance hazard.

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

        if (resumeIndex < pathMap.Length - 1)
        {
            float dHere = playerMap.MapDistanceXYTo(pathMap[resumeIndex]);
            float dNext = playerMap.MapDistanceXYTo(pathMap[resumeIndex + 1]);

            if (dHere < 1.5f || dNext <= dHere * 1.25f)
            {
                resumeIndex++;
            }
        }


        // pathMap points are in map-space (route file coordinates, e.g. X=44, Y=40).
        // playerReader.WorldPos is in world-space (e.g. X=370, Y=-4323).
        // Navigation converts map->world internally via WorldMapAreaDB.ToWorld_FlipXY.
        // For our distance comparisons here we must convert pathMap points to world-space
        // before comparing against playerReader.WorldPos / Navigation.POP_DIST (world units).
        var wma = playerReader.WorldMapArea;
        Vector3 playerW = playerReader.WorldPos;
        Vector3 ToWorldCoord(Vector3 mapPt) => WorldMapAreaDB.ToWorld_FlipXY(mapPt, wma);

        // Advance resumeIndex past any waypoints the player has already reached.
        while (resumeIndex < pathMap.Length - 1)
        {
            if (playerW.WorldDistanceXYTo(ToWorldCoord(pathMap[resumeIndex])) < Navigation.POP_DIST)
            {
                logger.LogWarning(
                    $"[FRG] RefillWaypoints: skipping already-reached resumeIndex={resumeIndex} "
                    + $"dist={playerW.WorldDistanceXYTo(ToWorldCoord(pathMap[resumeIndex])):0.00} < POP_DIST={Navigation.POP_DIST:0.00}");
                resumeIndex++;
            }
            else
                break;
        }


        // There-and-back: preserve direction across pauses/resumes.
        if (pathSettings.PathThereAndBack)
        {
            if (_pathTraversalDirection == 0)
            {
                _pathTraversalDirection = mapDistanceToFirst <= mapDistanceToLast ? 1 : -1;
            }

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
                // Advance backward start index past already-reached points (same logic as forward).
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
                {
                    backwardPoints[i] = pathMap[backwardStartIndex - i];
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
        Span<Vector3> points = pathMap[resumeIndex..];

        if (points.Length == 0)
            return;

        // If the only remaining point is already within reach, wrap around to the start.
        // Two cases both require wrap:
        //   1. Only 1 point in the slice and player is close (caught by POP_DIST * 2 threshold).
        //   2. Skip loop stopped at the last index because it can't go further, but the player
        //      IS within POP_DIST of that last point — without this check the halt recurs.
        if (points.Length == 1)
        {
            float distToOnly = playerW.WorldDistanceXYTo(ToWorldCoord(points[0]));
            if (distToOnly < Navigation.POP_DIST * 2f)
            {
                Log($"{nameof(RefillWaypoints)} - last point reached, wrapping route to start");

                // Always restart from index 0. DO NOT search for the "closest" point —
                // the player just finished the route so the closest point is always the
                // last one, which would give back the same single point and halt again.
                // Instead walk forward from 0, skipping any points already within POP_DIST.
                int wrapResumeIndex = 0;
                while (wrapResumeIndex < pathMap.Length - 1 &&
                       playerW.WorldDistanceXYTo(ToWorldCoord(pathMap[wrapResumeIndex])) < Navigation.POP_DIST)
                {
                    wrapResumeIndex++;
                }

                Span<Vector3> wrapPoints = pathMap[wrapResumeIndex..];

                if (wrapPoints.Length == 0)
                    wrapPoints = pathMap;

                // Wrap-around is always authoritative — never skip it via the duplicate guard.
                // canSkipDuplicateRefill may be stale (true from the waypoint that was just popped).
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
