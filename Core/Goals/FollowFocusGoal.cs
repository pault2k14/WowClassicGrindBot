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
    /// player-vs-player collision. The bot stops this many yards behind the leader.
    /// Must stay small enough to settle well within <see cref="FollowingMaxYards"/>
    /// but not so small the assist oscillates back and forth through the leader position.
    /// 3y is the practical floor given WoW Classic's character radius.</summary>
    private const float FollowStopShortYards = 3f;

    // Distance thresholds (world yards, direction-agnostic XY distance)
    // -----------------------------------------------------------------------

    /// <summary>Assist posts <see cref="BotStatus.Following"/> when closer than this.
    /// Must comfortably exceed <see cref="FollowStopShortYards"/> (3y) so navigation
    /// can complete and the Idle transition fires cleanly.</summary>
    public const float FollowingMaxYards = 7f;

    /// <summary>Assist transitions to NavigatingToLeader when farther than this.
    /// Set below <see cref="LeaderPauseYards"/> (20y) so the assist corrects course
    /// before the leader's distance gate fires — the leader almost never needs to pause.
    /// Dead-band entry: dist > NavigatingMinYards (14y) triggers navigation from Idle.
    /// Dead-band exit:  dist &lt; NavigatingExitYards (10y) returns to Idle.
    /// The 4y hysteresis gap (10-14y) prevents oscillation when running alongside
    /// the leader at ~14y — without it the state flips every 100-500ms, causing
    /// 68+ path re-requests per run and lateral drift as the pather cuts different
    /// mesh corridors to each slightly-off-route live position.</summary>
    private const float NavigatingMinYards = 14f;

    /// <summary>Hysteresis exit threshold for NavigatingToLeader dead-band.
    /// Must be less than <see cref="NavigatingMinYards"/> (14y) to prevent oscillation,
    /// and greater than <see cref="FollowingMaxYards"/> (7y) to retain the circular-route
    /// fix (assists chasing a curved route at similar speed would otherwise never exit
    /// NavigatingToLeader via the FollowingMaxYards path alone).</summary>
    private const float NavigatingExitYards = 10f;

    /// <summary>Leader must be this close before the assist exits CantFollow.
    /// MUST exceed <c>Navigation.POP_DIST</c> (3.6y) — the leader's navigation considers
    /// a waypoint reached at POP_DIST and parks there. If LeaderArrivedYards were below
    /// that threshold the leader would stop at ~3.5y and the assist would never see
    /// dist &lt;= LeaderArrivedYards, causing a permanent deadlock where neither bot moves.
    /// 6y gives comfortable clearance above POP_DIST while still being well inside the
    /// dead-band zone (FollowingMaxYards = 7y).</summary>
    public const float LeaderArrivedYards = 6f;

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

    /// <summary>Minimum position change that resets the navigation active timer.
    /// While the assist moves at least this far the timer does not accumulate —
    /// it only counts time spent genuinely stuck with no forward progress.
    /// Prevents the 30s wall from firing while the assist is actively covering
    /// ground but the pather is slow (e.g. elevated terrain, long path).</summary>
    private const float NavigationProgressResetYards = 3f;

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
    /// <summary>Last recorded position used to detect forward progress during navigation.
    /// Reset each time the assist moves <see cref="NavigationProgressResetYards"/>, which
    /// resets <see cref="_navActiveElapsed"/> so the timeout only accumulates when truly stuck.</summary>
    private Vector3 _navProgressCheckPosW;

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
                // assistStatusProvider.CantFollow keeps assistshouldfollow=true so FFG
                // remains selectable throughout evade recovery, even when dmgTaken/dmgDone
                // would otherwise block it. Cleared when the assist re-enters Following range.
                assistStatusProvider.CantFollow = true;
            }
            else
            {
                logger.LogInformation("[FFG] Ghost combat escape (guid=0) — starting evade recovery.");
                SendGoapEvent(new EvadeBlacklistEvent(0));
                assistStatusProvider.CantFollow = true;
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

        if (restHandler.IsResting())
        {
            logger.LogInformation("[FFG] Waiting while resting.");
            while (restHandler.IsResting())
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
                assistStatusProvider.CantFollow = false;
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

        // Within FollowingMaxYards → transition to Idle, post Following.
        if (dist < FollowingMaxYards && !navigation.IsApproachEscapeActive)
        {
            logger.LogInformation(
                $"[FFG] Reached follow position (dist={dist:0.0}y < {FollowingMaxYards}y) — entering Idle.");
            navigation.Stop();
            input.StopForward(true); // explicitly stop — prevents momentum carry-through
            ResetNavState();
            EnterState(NavState.Idle);
            assistStatusProvider.CurrentStatus = BotStatus.Following;
            assistStatusProvider.CantFollow = false;
            return;
        }

        // Back within hysteresis zone (dist < NavigatingExitYards = 10y) → Idle.
        // NavigatingExitYards (10y) is intentionally LOWER than NavigatingMinYards (14y).
        // Using the same threshold for entry and exit creates a zero-hysteresis bang-bang
        // controller: the assist reaches 13.9y → exits Idle, then immediately re-enters
        // NavigatingToLeader at 14.1y, looping every 100-500ms. Each loop calls
        // StartNavigatingToLeader() → SetSingleWaypoint() → fresh path request, causing
        // 68+ pather restarts per run. The pather re-routes to the leader's shifted live
        // position each time, cutting different mesh corridors and accumulating lateral drift.
        // With NavigatingExitYards = 10y: the 10-14y zone is a stable "keep navigating" band.
        // The assist stays in NavigatingToLeader until it actually closes to 10y, then Idle's
        // dead-band (7-14y) holds it there without re-triggering navigation.
        if (dist < NavigatingExitYards && !navigation.IsApproachEscapeActive)
        {
            logger.LogInformation(
                $"[FFG] Back within hysteresis zone ({dist:0.0}y < {NavigatingExitYards}y) — returning to Idle.");
            navigation.Stop();
            input.StopForward(true);
            ResetNavState();
            EnterState(NavState.Idle);
            assistStatusProvider.CurrentStatus = BotStatus.Following;
            assistStatusProvider.CantFollow = false;
            return;
        }

        // Update waypoint if leader has moved significantly from the last target.
        float waypointDrift = leader.WorldPos.WorldDistanceXYTo(_lastNavigatedToLeaderWorldPos);
        if (waypointDrift > WaypointUpdateThresholdYards)
        {
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

            if (dist <= LeaderArrivedYards)
            {
                logger.LogInformation(
                    $"[FFG] CantFollow: leader arrived ({dist:0.0}y) — resuming navigation.");
                assistStatusProvider.CantFollow = false;
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
        _navProgressCheckPosW = playerReader.WorldPos;

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
        assistStatusProvider.CantFollow = true;
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
        _navProgressCheckPosW = default;
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
            _navProgressCheckPosW = playerReader.WorldPos;
            return;
        }

        bool countActive = !bits.Combat() && !_evadeRecoveryActive;
        if (countActive)
            _navActiveElapsed += now - _navLastTickUtc;

        _navLastTickUtc = now;

        // Reset the timeout whenever the assist makes meaningful forward progress.
        // Without this, the 30s wall fires even when the assist is actively navigating —
        // e.g. the pather gets slow on elevated terrain near the final waypoint and the
        // character stops briefly while waiting for the route result, burning the remaining
        // timer budget even though 170 yards of progress was made in the preceding 28 seconds.
        // By resetting on NavigationProgressResetYards of movement, the timer only accumulates
        // during genuine stalls with zero position change.
        Vector3 currentPos = playerReader.WorldPos;
        float moved = currentPos.WorldDistanceXYTo(_navProgressCheckPosW);
        if (moved >= NavigationProgressResetYards)
        {
            _navProgressCheckPosW = currentPos;
            _navActiveElapsed = TimeSpan.Zero;
        }

        if (_navActiveElapsed.TotalSeconds >= NavigationActiveTimeoutSec)
        {
            logger.LogWarning(
                $"[FFG] Navigation stuck timeout ({_navActiveElapsed.TotalSeconds:0.0}s without progress) — escalating to CantFollow.");
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
            assistStatusProvider.CantFollow = false;
            return;
        }

        // Arrived at destination and dist is in the dead-band — accept as close enough.
        // This handles routes that terminate in the dead-band zone (7-14y): without this,
        // OnDestinationReached would unconditionally retry or escalate to CantFollow even
        // though the assist is at a perfectly acceptable following distance.
        if (dist < NavigatingMinYards)
        {
            logger.LogInformation(
                $"[FFG] Destination reached within dead-band (dist={dist:0.0}y < {NavigatingMinYards}y) — returning to Idle.");
            navigation.Stop();
            input.StopForward(true);
            ResetNavState();
            EnterState(NavState.Idle);
            assistStatusProvider.CurrentStatus = BotStatus.Following;
            assistStatusProvider.CantFollow = false;
            return;
        }
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
        if (dist < NavigatingMinYards)
        {
            navigation.Stop();
            ResetNavState();
            EnterState(NavState.Idle);
            assistStatusProvider.CurrentStatus = BotStatus.Following;
            assistStatusProvider.CantFollow = false;
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
