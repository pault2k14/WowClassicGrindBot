using Core.GOAP;
using Core.Party;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SharedLib;
using SharedLib.Extensions;

using System;
using System.Numerics;
using System.Threading;

namespace Core.Goals;

public sealed class FollowFocusGoal : GoapGoal, IGoapEventListener
{
    public override float Cost => 19f;

    // -----------------------------------------------------------------------
    /// <summary>How many yards short of the leader the assist targets when navigating.
    /// Prevents the assist from running through/past the leader since WoW has no
    /// player-vs-player collision. The bot stops this many yards behind the leader,
    /// which is well within <see cref="FollowingMaxYards"/>.</summary>
    private const float FollowStopShortYards = 5f;

    // Distance thresholds (world yards, direction-agnostic XY distance)
    // -----------------------------------------------------------------------

    /// <summary>Assist posts <see cref="BotStatus.Following"/> when closer than this.</summary>
    public const float FollowingMaxYards = 10f;

    /// <summary>Assist transitions to NavigatingToLeader when farther than this.
    /// Dead-band between FollowingMaxYards and NavigatingMinYards prevents rapid
    /// status switching when the leader is just ahead.</summary>
    private const float NavigatingMinYards = 15f;

    /// <summary>Leader must be this close before the assist exits CantFollow.</summary>
    public const float LeaderArrivedYards = 2f;

    /// <summary>Minimum leader movement before the navigation waypoint is refreshed.</summary>
    private const float WaypointUpdateThresholdYards = 3f;

    /// <summary>Leader must pause when assist exceeds this distance.</summary>
    public const float LeaderPauseYards = 20f;

    /// <summary>Leader may resume when assist is within this distance (hysteresis).</summary>
    public const float LeaderResumeYards = 15f;

    // -----------------------------------------------------------------------
    // Timeouts
    // -----------------------------------------------------------------------
    private const double NavigationActiveTimeoutSec = 30.0;
    private const double CantFollowTimeoutSec = 120.0;

    // -----------------------------------------------------------------------
    // Stuck detection
    // -----------------------------------------------------------------------
    private const double StuckCheckIntervalSec = 3.0;
    private const float StuckMinMovementWorld = 1.0f;
    private const double StuckEscapeCooldownSec = 10.0;

    // -----------------------------------------------------------------------
    // Dependencies
    // -----------------------------------------------------------------------
    private readonly ConfigurableInput input;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly Wait wait;
    private readonly ILogger<FollowFocusGoal> logger;
    private readonly RestHandler restHandler;
    private readonly ChatReader chatReader;
    private readonly Navigation navigation;
    private readonly AssistStatusProvider assistStatusProvider;
    private readonly LeaderConnectionStatus leaderConnection;

    // Set by OnGoapEvent when GoapAgent broadcasts evadeRecovery=true.
    private bool _evadeRecoveryActive;

    // For combat-assist targeting (unchanged from original).
    private readonly Action<CancellationToken> FocusTargetInput;

    // -----------------------------------------------------------------------
    // Nav state machine
    // -----------------------------------------------------------------------
    private enum NavState { Idle, NavigatingToLeader, CantFollow }
    private NavState _navState = NavState.Idle;
    private DateTime _navStateEnteredUtc;

    // -----------------------------------------------------------------------
    // Leader position tracking
    // -----------------------------------------------------------------------
    /// <summary>Last leader world position we navigated toward (for waypoint update threshold).</summary>
    private Vector3 _lastNavigatedToLeaderWorldPos;

    // -----------------------------------------------------------------------
    // Navigation active-time timeout
    // -----------------------------------------------------------------------
    private TimeSpan _navActiveElapsed;
    private DateTime _navLastTickUtc;
    private bool _navTimerInit;
    private int _navAttempt;
    private bool _navRewindActive;
    private Vector3 _navRewindAnchorW;

    // -----------------------------------------------------------------------
    // Stuck detection fields
    // -----------------------------------------------------------------------
    private Vector3 _stuckCheckPosW;
    private DateTime _stuckCheckLastUtc = DateTime.MinValue;
    private DateTime _stuckEscapeLastUtc = DateTime.MinValue;

    // -----------------------------------------------------------------------
    // CantFollow: hold position, wait for leader within LeaderArrivedYards
    // -----------------------------------------------------------------------
    private DateTime _cantFollowEnteredUtc;

    public FollowFocusGoal(
        ConfigurableInput input,
        PlayerReader playerReader,
        AddonBits bits,
        Wait wait,
        ClassConfiguration classConfig,
        ILogger<FollowFocusGoal> logger,
        RestHandler restHandler,
        ChatReader chatReader,
        Navigation navigation,
        AssistStatusProvider assistStatusProvider,
        LeaderConnectionStatus leaderConnection,
        IOptions<PartyApiConfig> configOptions)
        : base(nameof(FollowFocusGoal))
    {
        this.input = input;
        this.playerReader = playerReader;
        this.bits = bits;
        this.wait = wait;
        this.logger = logger;
        this.restHandler = restHandler;
        this.chatReader = chatReader;
        this.navigation = navigation;
        this.assistStatusProvider = assistStatusProvider;
        this.leaderConnection = leaderConnection;

        if (classConfig.UnitToFollow == "focus")
            AddPrecondition(GoapKey.hasfocus, true);

        AddPrecondition(GoapKey.assistshouldfollow, true);
        AddPrecondition(GoapKey.shouldloot, false);
        AddPrecondition(GoapKey.shouldgather, false);
        AddPrecondition(GoapKey.consumecorpse, false);

        FocusTargetInput = classConfig.UnitToFollow switch
        {
            "party2" => input.PressTargetFocusPartyMemberTwo,
            "party3" => input.PressTargetFocusPartyMemberThree,
            "party4" => input.PressTargetFocusPartyMemberFour,
            _        => input.PressTargetFocus
        };

        navigation.OnDestinationReached += Navigation_OnDestinationReached;
        navigation.OnWayPointReached    += Navigation_OnWayPointReached;
        navigation.OnPathFailed         += Navigation_OnPathFailed;
    }

    private void Cleanup()
    {
        navigation.OnDestinationReached -= Navigation_OnDestinationReached;
        navigation.OnWayPointReached    -= Navigation_OnWayPointReached;
        navigation.OnPathFailed         -= Navigation_OnPathFailed;
    }

    // -----------------------------------------------------------------------
    // IGoapEventListener
    // -----------------------------------------------------------------------

    public void OnGoapEvent(GoapEventArgs e)
    {
        if (e is GoapStateEvent s && s.Key == GoapKey.evadeRecovery)
            _evadeRecoveryActive = s.Value;
    }

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    public override void OnEnter()
    {
        while (restHandler.IsResting())
            wait.Update(1000);

        _stuckCheckLastUtc = DateTime.MinValue;
        navigation.ResetApproachEscape();

        if (input.IsKeyDown(input.ForwardKey))
            input.StopForward(true);

        if (_navState == NavState.NavigatingToLeader)
        {
            logger.LogInformation("[FFG] OnEnter: was NavigatingToLeader — stopping navigation.");
            navigation.Stop();
            ResetNavState();
            _navState = NavState.Idle;
        }
        else if (_navState == NavState.CantFollow)
        {
            // Remain in CantFollow across plan cycles — the assist should not start
            // moving just because another goal briefly preempted us.
            logger.LogInformation("[FFG] OnEnter: resuming CantFollow state.");
        }
    }

    public override void OnExit()
    {
        navigation.ResetApproachEscape();
        input.StepBackwards();
        wait.Update();

        if (_navState == NavState.NavigatingToLeader)
        {
            logger.LogInformation("[FFG] OnExit: stopping navigation.");
            navigation.Stop();
        }

        if (_navState != NavState.CantFollow)
            _navState = NavState.Idle;
        // CantFollow persists across plan cycles so the assist holds position.
    }

    // -----------------------------------------------------------------------
    // Main update
    // -----------------------------------------------------------------------

    public override void Update()
    {
        if (bits.Drowning())
            input.PressJump();

        // ── Evade blacklist (still chat-based) ─────────────────────────────
        if (chatReader.LeaderBlacklistTarget)
        {
            int blacklistGuid = chatReader.LeaderBlacklistTargetId;
            chatReader.LeaderBlacklistTarget = false;
            chatReader.LeaderBlacklistTargetId = 0;

            logger.LogInformation($"[FFG] LeaderBlacklistTarget received guid={blacklistGuid}");

            if (blacklistGuid != 0)
            {
                input.PressStopAttack();
                wait.Update();
                playerReader.IgnoreTarget(blacklistGuid);
                input.PressClearTarget();
                wait.Update();

                SendGoapEvent(new EvadeBlacklistEvent(blacklistGuid));
                // Keep chatReader.AssistRequestReturn as local override so
                // assistshouldfollow stays true during evade recovery.
                chatReader.AssistRequestReturn = true;
            }
            else
            {
                logger.LogInformation("[FFG] Ghost combat escape (guid=0) — starting evade recovery.");
                SendGoapEvent(new EvadeBlacklistEvent(0));
                chatReader.AssistRequestReturn = true;
            }
        }

        // ── Combat assist ─────────────────────────────────────────────────
        if (!_evadeRecoveryActive && bits.Focus_Combat() && bits.FocusTarget())
        {
            wait.Update();
            FocusTargetInput(default);
            input.PressTargetOfTarget();
            wait.Update();
            input.PressInteract();
            wait.Update();
            return;
        }

        if (chatReader.ForcedFollow)
        {
            AddEffect(GoapKey.forcedfollow, true);
            return;
        }

        while (restHandler.IsResting())
        {
            logger.LogInformation("[FFG] Waiting while resting.");
            wait.Update(1000);
        }

        // ── State machine ──────────────────────────────────────────────────
        switch (_navState)
        {
            case NavState.Idle:              UpdateIdle();              break;
            case NavState.NavigatingToLeader: UpdateNavigatingToLeader(); break;
            case NavState.CantFollow:        UpdateCantFollow();        break;
        }
    }

    // -----------------------------------------------------------------------
    // State: Idle — within FollowingMaxYards, posting Following
    // -----------------------------------------------------------------------
    private void UpdateIdle()
    {
        LeaderState? leader = leaderConnection.LastLeaderState;

        if (leader == null || leaderConnection.LocalAgeMs > leaderConnection.StaleThresholdMs)
        {
            // No fresh data — stay put, don't post Following.
            assistStatusProvider.CurrentStatus = BotStatus.Waiting;
            wait.Update();
            return;
        }

        float dist = playerReader.WorldPos.WorldDistanceXYTo(leader.WorldPos);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                $"[FFG] Idle: dist={dist:0.0}y leader=({leader.MapX:0.00},{leader.MapY:0.00}) " +
                $"status={leader.Status} localAge={leaderConnection.LocalAgeMs:0}ms");
        }

        // Beyond dead-band upper threshold → start navigating.
        if (dist > NavigatingMinYards)
        {
            logger.LogInformation(
                $"[FFG] Leader out of range ({dist:0.0}y > {NavigatingMinYards}y) — starting navigation.");
            StartNavigatingToLeader(leader);
            return;
        }

        if (dist < FollowingMaxYards)
        {
            // Clearly within range — post Following.
            if (assistStatusProvider.CurrentStatus != BotStatus.Following)
            {
                logger.LogInformation(
                    $"[FFG] Within {dist:0.0}y of leader (threshold={FollowingMaxYards}y) — posting Following.");
                assistStatusProvider.CurrentStatus = BotStatus.Following;
                chatReader.AssistRequestReturn = false;
            }
        }
        else
        {
            // Dead-band: 10y ≤ dist ≤ 15y.
            // Only maintain Following if we are ALREADY Following — this prevents
            // small distance fluctuations from oscillating the leader's movement gate.
            // If we are NOT already Following (e.g. first entry, or recovering from
            // NavigatingToLeader), navigate to close the gap below FollowingMaxYards.
            if (assistStatusProvider.CurrentStatus == BotStatus.Following)
            {
                // Stay Following — minor fluctuation, no action needed.
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(
                        $"[FFG] Dead-band ({dist:0.0}y) — maintaining Following.");
                }
            }
            else
            {
                logger.LogInformation(
                    $"[FFG] Dead-band ({dist:0.0}y) but not yet Following — navigating to close gap.");
                StartNavigatingToLeader(leader);
                return;
            }
        }

        wait.Update();
    }

    // -----------------------------------------------------------------------
    // State: NavigatingToLeader — pather routing to leader's live position
    // -----------------------------------------------------------------------
    private void UpdateNavigatingToLeader()
    {
        // ── Active-time timeout ─────────────────────────────────────────────
        TickNavActiveTimeout();
        if (_navState != NavState.NavigatingToLeader)
            return;

        LeaderState? leader = leaderConnection.LastLeaderState;

        if (leader == null || leaderConnection.LocalAgeMs > leaderConnection.StaleThresholdMs)
        {
            logger.LogWarning(
                $"[FFG] NavigatingToLeader: leader state stale/missing " +
                $"(localAge={leaderConnection.LocalAgeMs:0}ms) — holding current waypoint.");
            assistStatusProvider.CurrentStatus = BotStatus.NavigatingToLeader;
            navigation.Update(CancellationToken.None);
            wait.Update();
            return;
        }

        float dist = playerReader.WorldPos.WorldDistanceXYTo(leader.WorldPos);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                $"[FFG] NavigatingToLeader: dist={dist:0.0}y " +
                $"leaderStatus={leader.Status} localAge={leaderConnection.LocalAgeMs:0}ms");
        }

        // Within FollowingMaxYards → transition to Idle.
        if (dist < FollowingMaxYards && !navigation.IsApproachEscapeActive)
        {
            logger.LogInformation(
                $"[FFG] Reached follow position (dist={dist:0.0}y < {FollowingMaxYards}y) — entering Idle.");
            navigation.Stop();
            input.StopForward(true); // explicitly stop — prevents momentum carry-through
            ResetNavState();
            EnterState(NavState.Idle);
            assistStatusProvider.CurrentStatus = BotStatus.Following;
            chatReader.AssistRequestReturn = false;
            return;
        }

        // Update waypoint if leader has moved significantly from the last target.
        float waypointDrift = leader.WorldPos.WorldDistanceXYTo(_lastNavigatedToLeaderWorldPos);
        if (waypointDrift > WaypointUpdateThresholdYards)
        {
            logger.LogDebug(
                $"[FFG] Leader moved {waypointDrift:0.0}y — refreshing waypoint.");
            _lastNavigatedToLeaderWorldPos = leader.WorldPos;
            navigation.SetSingleWaypoint(ComputeFollowTargetMapPos(leader));
        }

        // Record position for TryUnstuck direction.
        navigation.RecordApproachPosition(playerReader.WorldPos);
        navigation.Update(CancellationToken.None);

        // ── Escape handling ─────────────────────────────────────────────────
        if (navigation.IsApproachEscapeActive)
        {
            assistStatusProvider.CurrentStatus = BotStatus.Stuck;
            if (!navigation.TryUnstuck())
                InjectPhysStuckEscape();
            return;
        }

        // ── Idle stuck detection ────────────────────────────────────────────
        if (navigation.HasWaypoint() || navigation.HasNext())
            TickIdleStuckDetection();

        // ── Co-located terrain fallback ─────────────────────────────────────
        if (!navigation.HasWaypoint() && !navigation.HasNext() && !navigation.IsApproachEscapeActive
            && dist > NavigatingMinYards)
        {
            logger.LogWarning(
                "[FFG] No active waypoint but still far from leader — refreshing waypoint.");
            _lastNavigatedToLeaderWorldPos = leader.WorldPos;
            navigation.SetSingleWaypoint(leader.MapPosNoZ);
        }

        assistStatusProvider.CurrentStatus = navigation.IsApproachEscapeActive
            ? BotStatus.Stuck
            : BotStatus.NavigatingToLeader;

        wait.Update();
    }

    // -----------------------------------------------------------------------
    // State: CantFollow — hold position, wait for leader to arrive
    // -----------------------------------------------------------------------
    private void UpdateCantFollow()
    {
        assistStatusProvider.CurrentStatus = BotStatus.CantFollow;
        navigation.Stop();

        LeaderState? leader = leaderConnection.LastLeaderState;

        double cantFollowSec = (DateTime.UtcNow - _cantFollowEnteredUtc).TotalSeconds;

        if (leader != null && leaderConnection.LocalAgeMs <= leaderConnection.StaleThresholdMs)
        {
            float dist = playerReader.WorldPos.WorldDistanceXYTo(leader.WorldPos);

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    $"[FFG] CantFollow: leader dist={dist:0.0}y " +
                    $"(threshold={LeaderArrivedYards}y) waited={cantFollowSec:0.0}s localAge={leaderConnection.LocalAgeMs:0}ms");
            }

            if (dist <= LeaderArrivedYards)
            {
                logger.LogInformation(
                    $"[FFG] CantFollow: leader arrived ({dist:0.0}y) — resuming navigation.");
                chatReader.AssistRequestReturn = false;
                ResetNavState();
                StartNavigatingToLeader(leader);
                return;
            }
        }

        if (cantFollowSec >= CantFollowTimeoutSec)
        {
            logger.LogWarning(
                $"[FFG] CantFollow timed out after {cantFollowSec:0.0}s — " +
                "retrying navigation in case leader moved.");
            if (leader != null)
                StartNavigatingToLeader(leader);
            else
                EnterState(NavState.Idle);
            return;
        }

        wait.Update();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private void StartNavigatingToLeader(LeaderState leader)
    {
        _lastNavigatedToLeaderWorldPos = leader.WorldPos;
        _navAttempt = 0;
        _navRewindActive = false;
        _navRewindAnchorW = default;
        _navTimerInit = false;
        _navActiveElapsed = TimeSpan.Zero;

        navigation.SetSingleWaypoint(ComputeFollowTargetMapPos(leader));
        EnterState(NavState.NavigatingToLeader);
        assistStatusProvider.CurrentStatus = BotStatus.NavigatingToLeader;
    }

    /// <summary>
    /// Returns a map-space position that is <see cref="FollowStopShortYards"/> behind
    /// the leader (toward the assist), preventing the assist from running through and
    /// past the leader. WoW has no player-vs-player collision, so without this offset
    /// the assist would overshoot the leader's position at running speed.
    /// <para>
    /// Direction is computed in map space and scaled using the current
    /// <see cref="WorldMapArea"/> bounds for accuracy across different zones.
    /// </para>
    /// </summary>
    private Vector3 ComputeFollowTargetMapPos(LeaderState leader)
    {
        // Direction from leader toward assist in map space.
        Vector3 dirToAssist = playerReader.MapPosNoZ - leader.MapPosNoZ;
        float mapLen = MathF.Sqrt(dirToAssist.X * dirToAssist.X + dirToAssist.Y * dirToAssist.Y);

        if (mapLen < 0.001f)
            return leader.MapPosNoZ; // co-located — navigate to exact position

        // Approximate world-units-per-map-unit from WorldMapArea bounds.
        // Average of X and Y scales; good enough for small offsets.
        var wma = playerReader.WorldMapArea;
        float avgWorldPerMap = wma != null
            ? (MathF.Abs(wma.LocRight - wma.LocLeft) + MathF.Abs(wma.LocBottom - wma.LocTop)) / 200f
            : 44f; // sensible fallback for Azeroth zones

        float offsetMapUnits = FollowStopShortYards / avgWorldPerMap;

        // Target = leader's map position shifted toward the assist by offsetMapUnits.
        return new Vector3(
            leader.MapPosNoZ.X + (dirToAssist.X / mapLen) * offsetMapUnits,
            leader.MapPosNoZ.Y + (dirToAssist.Y / mapLen) * offsetMapUnits,
            0f);
    }

    private void EnterCantFollow()
    {
        logger.LogWarning(
            $"[FFG] Navigation exhausted — entering CantFollow. " +
            "Assist will hold position until leader arrives within " +
            $"{LeaderArrivedYards}y.");
        navigation.Stop();
        chatReader.AssistRequestReturn = true;
        _cantFollowEnteredUtc = DateTime.UtcNow;
        assistStatusProvider.CurrentStatus = BotStatus.CantFollow;
        EnterState(NavState.CantFollow);
    }

    private void EnterState(NavState newState)
    {
        logger.LogInformation($"[FFG] NavState: {_navState} → {newState}");
        _navState = newState;
        _navStateEnteredUtc = DateTime.UtcNow;

        if (newState == NavState.NavigatingToLeader)
            _stuckCheckLastUtc = DateTime.MinValue;
    }

    private void ResetNavState()
    {
        _navAttempt = 0;
        _navRewindActive = false;
        _navRewindAnchorW = default;
        _navTimerInit = false;
        _navActiveElapsed = TimeSpan.Zero;
        _lastNavigatedToLeaderWorldPos = default;
    }

    // -----------------------------------------------------------------------
    // Active-time navigation timeout
    // -----------------------------------------------------------------------
    private void TickNavActiveTimeout()
    {
        var now = DateTime.UtcNow;

        if (!_navTimerInit)
        {
            _navTimerInit = true;
            _navLastTickUtc = now;
            _navActiveElapsed = TimeSpan.Zero;
            return;
        }

        bool countActive = !bits.Combat() && !_evadeRecoveryActive;
        if (countActive)
            _navActiveElapsed += now - _navLastTickUtc;

        _navLastTickUtc = now;

        if (_navActiveElapsed.TotalSeconds >= NavigationActiveTimeoutSec)
        {
            logger.LogWarning(
                $"[FFG] Navigation active timeout ({_navActiveElapsed.TotalSeconds:0.0}s) — escalating to CantFollow.");
            EnterCantFollow();
        }
    }

    // -----------------------------------------------------------------------
    // Idle stuck detection
    // -----------------------------------------------------------------------
    private void TickIdleStuckDetection()
    {
        if (bits.Combat() || _evadeRecoveryActive)
        {
            _stuckCheckLastUtc = DateTime.MinValue;
            return;
        }

        var now = DateTime.UtcNow;
        Vector3 currentW = playerReader.WorldPos;

        if (_stuckCheckLastUtc == DateTime.MinValue)
        {
            _stuckCheckPosW = currentW;
            _stuckCheckLastUtc = now;
            return;
        }

        double elapsed = (now - _stuckCheckLastUtc).TotalSeconds;
        if (elapsed < StuckCheckIntervalSec)
            return;

        float moved = currentW.WorldDistanceXYTo(_stuckCheckPosW);
        _stuckCheckPosW = currentW;
        _stuckCheckLastUtc = now;

        if (moved >= StuckMinMovementWorld)
            return;

        if ((now - _stuckEscapeLastUtc).TotalSeconds < StuckEscapeCooldownSec)
            return;

        _stuckEscapeLastUtc = now;
        logger.LogWarning(
            $"[FFG] Stuck detected — moved only {moved:0.00}y in {elapsed:0.0}s. " +
            "Attempting pather-based escape.");

        assistStatusProvider.CurrentStatus = BotStatus.Stuck;

        if (!navigation.TryUnstuck())
            InjectPhysStuckEscape();
    }

    // -----------------------------------------------------------------------
    // PhysStuck injection
    // -----------------------------------------------------------------------
    private void InjectPhysStuckEscape()
    {
        if (!navigation.IsApproachEscapePhysicallyStuck)
            return;

        navigation.IsApproachEscapePhysicallyStuck = false;
        logger.LogWarning("[FFG] Physically trapped — injecting jump + reverse.");
        input.StopForward(false);
        input.PressJump();
        Thread.Sleep(400);
        input.StartBackward(false);
        input.PressJump();
        Thread.Sleep(600);
        input.PressJump();
        Thread.Sleep(400);
        input.StopBackward(false);
    }

    // -----------------------------------------------------------------------
    // Navigation events
    // -----------------------------------------------------------------------

    private void Navigation_OnDestinationReached()
    {
        if (_navState != NavState.NavigatingToLeader)
            return;

        if (_navRewindActive)
        {
            _navRewindActive = false;
            LeaderState? leader = leaderConnection.LastLeaderState;
            if (leader != null)
            {
                logger.LogInformation("[FFG] Rewind reached — retrying leader target.");
                _lastNavigatedToLeaderWorldPos = leader.WorldPos;
                navigation.SetSingleWaypoint(leader.MapPosNoZ);
            }
            return;
        }

        LeaderState? currentLeader = leaderConnection.LastLeaderState;
        if (currentLeader == null) return;

        float dist = playerReader.WorldPos.WorldDistanceXYTo(currentLeader.WorldPos);

        logger.LogInformation(
            $"[FFG] Destination reached. dist={dist:0.0}y to leader.");

        if (dist < FollowingMaxYards)
        {
            navigation.Stop();
            input.StopForward(true);
            ResetNavState();
            EnterState(NavState.Idle);
            assistStatusProvider.CurrentStatus = BotStatus.Following;
            chatReader.AssistRequestReturn = false;
            return;
        }

        // Leader moved while we were navigating — try once more to the updated position.
        if (_navAttempt == 0)
        {
            logger.LogWarning(
                $"[FFG] Arrived but still {dist:0.0}y from leader — refreshing waypoint.");
            _navAttempt = 1;
            _lastNavigatedToLeaderWorldPos = currentLeader.WorldPos;
            navigation.SetSingleWaypoint(ComputeFollowTargetMapPos(currentLeader));
            return;
        }

        logger.LogWarning("[FFG] Arrived at destination but still out of range after retry — escalating to CantFollow.");
        EnterCantFollow();
    }

    private void Navigation_OnWayPointReached()
    {
        if (_navState != NavState.NavigatingToLeader)
            return;

        LeaderState? leader = leaderConnection.LastLeaderState;
        if (leader == null) return;

        float dist = playerReader.WorldPos.WorldDistanceXYTo(leader.WorldPos);
        if (dist < FollowingMaxYards)
        {
            navigation.Stop();
            ResetNavState();
            EnterState(NavState.Idle);
            assistStatusProvider.CurrentStatus = BotStatus.Following;
            chatReader.AssistRequestReturn = false;
        }
    }

    private void Navigation_OnPathFailed(Vector3 startW, Vector3 endW)
    {
        if (_navState != NavState.NavigatingToLeader)
            return;

        if (_navAttempt >= 1)
        {
            logger.LogWarning("[FFG] Path to leader failed after retry — escalating to CantFollow.");
            EnterCantFollow();
            return;
        }

        if (!navigation.HasLastSafeAnchor)
        {
            logger.LogWarning("[FFG] Path failed — no safe anchor. Escalating to CantFollow.");
            EnterCantFollow();
            return;
        }

        _navAttempt = 1;
        _navRewindActive = true;
        _navRewindAnchorW = navigation.LastSafeAnchorW;

        logger.LogWarning(
            $"[FFG] Path failed. Rewinding to anchor={_navRewindAnchorW}.");
        navigation.SetSingleWaypoint(_navRewindAnchorW);
    }
}
