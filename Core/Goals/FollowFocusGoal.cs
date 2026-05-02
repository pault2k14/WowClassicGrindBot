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
    /// Dead-band exit:  dist &lt; NavigatingExitYards (10y) → report Following, keep navigating.
    /// The 4y hysteresis band (10-14y) prevents the oscillation that occurred when exit=entry=14y
    /// (state flipped every 100-500ms, causing 68+ pather restarts per run and lateral drift).
    /// NavigatingExitYards no longer transitions to Idle — it only updates the Following status
    /// so the leader knows the assist is close enough to resume patrol. Navigation stays active
    /// and the assist runs continuously alongside the leader.</summary>
    private const float NavigatingMinYards = 14f;

    /// <summary>Distance at which the assist reports Following status while staying in
    /// NavigatingToLeader. Must be less than <see cref="NavigatingMinYards"/> (14y) to
    /// provide hysteresis, and greater than <see cref="FollowingMaxYards"/> (7y) so the
    /// leader can resume patrol before the assist reaches the minimum following distance.</summary>
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
    private readonly LeaderNavigationProvider leaderNavProvider;

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
    // Waypoint-sharing mode
    // -----------------------------------------------------------------------
    /// <summary>
    /// True once the assist has confirmed Following (≤ FollowingMaxYards) while
    /// the leader is Patrolling. Gates the switch from position-chasing to
    /// waypoint-sharing mode — ensures both bots start each patrol leg from
    /// roughly the same position so their pather paths converge.
    /// Cleared when the leader leaves Patrolling (combat, looting, etc.)
    /// or on FFG.OnEnter().
    /// </summary>
    private bool _rendezvousConfirmed;

    /// <summary>Last shared waypoint world position the assist navigated toward.
    /// Used to detect when the leader advances to a new waypoint so navigation
    /// can be refreshed without spamming SetSingleWaypoint every tick.</summary>
    private Vector3 _lastSharedWaypointW;

    // -----------------------------------------------------------------------
    // Mob blacklist — API-based replacement for chatReader.LeaderBlacklistTarget.
    // The assist diffs leader.BlacklistedMobGuids each tick. Any GUID present
    // in the new snapshot but not in _knownBlacklistedGuids triggers an
    // IgnoreTarget call and EvadeBlacklistEvent, mirroring the old chat flow.
    // -----------------------------------------------------------------------
    private readonly System.Collections.Generic.HashSet<int> _knownBlacklistedGuids = new();

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
        LeaderNavigationProvider leaderNavProvider,
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
        this.leaderNavProvider = leaderNavProvider;

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

        // Always reset rendezvous on goal entry — the assist must re-confirm
        // proximity to the leader before waypoint-sharing mode activates.
        _rendezvousConfirmed = false;
        _lastSharedWaypointW = default;

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

        // ── API-based mob blacklist ─────────────────────────────────────────
        // The leader publishes BlacklistedMobGuids in every LeaderState response.
        // We diff against _knownBlacklistedGuids; any new GUID triggers the same
        // flow the old chat message did: stop attack, IgnoreTarget, EvadeBlacklistEvent.
        LeaderState? leaderForBlacklist = leaderConnection.LastLeaderState;
        if (leaderForBlacklist != null &&
            leaderForBlacklist.BlacklistedMobGuids is { Length: > 0 })
        {
            foreach (int guid in leaderForBlacklist.BlacklistedMobGuids)
            {
                if (guid != 0 && _knownBlacklistedGuids.Add(guid))
                {
                    logger.LogInformation($"[FFG] New blacklisted mob guid={guid} from API — ignoring target.");
                    input.PressStopAttack();
                    wait.Update();
                    playerReader.IgnoreTarget(guid);
                    input.PressClearTarget();
                    wait.Update();

                    SendGoapEvent(new EvadeBlacklistEvent(guid));
                    // assistStatusProvider.CantFollow keeps assistshouldfollow=true so FFG
                    // remains selectable throughout evade recovery, even when dmgTaken/dmgDone
                    // would otherwise block it. Cleared when the assist re-enters Following range.
                    assistStatusProvider.CantFollow = true;
                }
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

            // Confirm rendezvous when both bots are co-located during patrol.
            // This gates the switch to waypoint-sharing mode — the assist must be
            // within FollowingMaxYards (7y) of the leader before it starts navigating
            // to shared waypoints, ensuring both paths start from roughly the same point.
            if (!_rendezvousConfirmed && leader.Status == BotStatus.Patrolling)
            {
                _rendezvousConfirmed = true;
                _lastSharedWaypointW = default; // force waypoint refresh on first use
                logger.LogInformation("[FFG] Rendezvous confirmed — waypoint-sharing mode active.");
            }
        }
        else
        {
            // When leader leaves Patrolling (combat, looting, resting), clear the
            // rendezvous so we revert to position-chasing until next co-location.
            if (_rendezvousConfirmed && leader.Status != BotStatus.Patrolling)
            {
                _rendezvousConfirmed = false;
                _lastSharedWaypointW = default;
                logger.LogInformation($"[FFG] Leader status {leader.Status} — clearing rendezvous, reverting to position-chase.");
            }

            // Dead-band: NavigatingExitYards ≤ dist ≤ NavigatingMinYards.
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

        // Within NavigatingExitYards (10y) — report Following so the leader knows
        // the assist is close enough and can resume patrol if it was paused.
        // DO NOT stop navigation or transition to Idle.
        //
        // Stopping here causes a stop-start leapfrog:
        //   1. Assist stops at 10y, sets Following.
        //   2. Leader (paused by distance gate) resumes — now running at ~7y/s.
        //   3. Assist is stationary: gap grows 10y → 14y in ~0.57s.
        //   4. Assist re-engages NavigatingToLeader at 14y, but leader is still running.
        //   5. Assist path through the mesh is ≥ leader's direct route → gap keeps growing.
        //   6. Gap hits 20y → leader pauses again → cycle repeats every 2–4 seconds.
        //
        // By keeping navigation active, both bots run at the same speed. The gap
        // holds at ~10y, shouldPause stays false (10y < LeaderPauseYards=20y), and
        // the leader never needs to pause. The natural exit is via FollowingMaxYards
        // (7y) when the leader actually stops, giving 13y of clean runway before the
        // pause threshold.
        if (dist < NavigatingExitYards && !navigation.IsApproachEscapeActive)
        {
            if (assistStatusProvider.CurrentStatus != BotStatus.Following)
            {
                logger.LogInformation(
                    $"[FFG] Within follow range ({dist:0.0}y < {NavigatingExitYards}y) — reporting Following, keeping navigation active.");
                assistStatusProvider.CurrentStatus = BotStatus.Following;
                assistStatusProvider.CantFollow = false;
            }
            // Fall through — navigation continues; no stop, no Idle transition.
        }

        // Update navigation target.
        // Waypoint-sharing mode: when the rendezvous has been confirmed and the leader
        // is patrolling with a published waypoint, navigate to the SAME waypoint rather
        // than chasing the leader's live position. This eliminates the systematic gap
        // growth caused by the assist's pather finding a slightly-longer path to a
        // moving target — both bots navigate to the same endpoint so their paths converge.
        //
        // Position-chasing mode (fallback): when leader is in combat, looting, resting,
        // or rendezvous has not yet been confirmed, chase the leader's body directly so
        // the assist automatically follows any unplanned detour.
        Vector3 currentNavigationTarget = GetNavigationTarget(leader);
        float navTargetDrift = currentNavigationTarget.WorldDistanceXYTo(_lastNavigatedToLeaderWorldPos);
        if (navTargetDrift > WaypointUpdateThresholdYards)
        {
            _lastNavigatedToLeaderWorldPos = currentNavigationTarget;
            navigation.SetSingleWaypoint(currentNavigationTarget); // world coords — SetWayPoints detects non-map range
        }

        // Record position for TryUnstuck direction.
        // NOTE: RecordApproachPosition is intentionally NOT called here.
        // That call is for mob-approach scenarios (ATG/PTG) where TryUnstuck()
        // needs a direction vector toward the mob. Calling it during leader
        // navigation records the direction of travel toward the leader, so when
        // TryUnstuck() fires it projects a 10y escape *further away from the
        // leader*, overshooting by 47+ yards and then adding a stuck rect that
        // causes blacklist detours for the rest of the run.
        // Navigation.Update() already handles route stuck recovery internally
        // via the chase watchdog and TryRouteUnstuck().
        navigation.Update(CancellationToken.None);

        // A navigation event (OnDestinationReached / OnWayPointReached) may have
        // fired during Update() and transitioned the state to Idle. Return
        // immediately to avoid overwriting the status set by the callback and to
        // avoid running stale NavigatingToLeader logic on an already-Idle state.
        if (_navState != NavState.NavigatingToLeader)
            return;

        // ── Co-located terrain fallback ─────────────────────────────────────
        if (!navigation.HasWaypoint() && !navigation.HasNext() && !navigation.IsApproachEscapeActive
            && dist > NavigatingMinYards)
        {
            logger.LogWarning(
                "[FFG] No active waypoint but still far from leader — refreshing waypoint.");
            Vector3 fallbackTarget = GetNavigationTarget(leader);
            _lastNavigatedToLeaderWorldPos = fallbackTarget;
            navigation.SetSingleWaypoint(fallbackTarget);
        }

        // Guard against overwriting Following — set by the NavigatingExitYards block
        // above (when dist < 10y) or by nav callbacks before the _navState guard above.
        // Without this guard, the assignment runs every tick and resets Following →
        // NavigatingToLeader, causing the NavigatingExitYards block to re-log and
        // re-set Following on every single tick (log spam + HTTP noise).
        if (assistStatusProvider.CurrentStatus != BotStatus.Following)
        {
            assistStatusProvider.CurrentStatus = BotStatus.NavigatingToLeader;
        }

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
        Vector3 target = GetNavigationTarget(leader);
        _lastNavigatedToLeaderWorldPos = target;
        _navAttempt = 0;
        _navRewindActive = false;
        _navRewindAnchorW = default;
        _navTimerInit = false;
        _navActiveElapsed = TimeSpan.Zero;
        _navProgressCheckPosW = playerReader.WorldPos;

        navigation.SetSingleWaypoint(target);
        EnterState(NavState.NavigatingToLeader);
        assistStatusProvider.CurrentStatus = BotStatus.NavigatingToLeader;
    }

    /// <summary>
    /// Selects the navigation target in world-space coordinates.
    ///
    /// <para><b>Waypoint-sharing mode</b> (when all conditions met):<br/>
    /// Returns the leader's current patrol waypoint rather than the leader's body.
    /// Both bots navigate to the same endpoint so their pather paths converge,
    /// eliminating the systematic gap growth caused by the assist following a
    /// slightly-longer path to a moving target.</para>
    ///
    /// <para><b>Position-chasing mode</b> (fallback):<br/>
    /// Returns the map-space position from <see cref="ComputeFollowTargetMapPos"/>
    /// — the leader's body offset by <see cref="FollowStopShortYards"/> toward
    /// the assist. This is used when the leader is in combat, looting, resting,
    /// evading, or the rendezvous has not yet been confirmed after the last
    /// non-Patrolling state.</para>
    /// </summary>
    private Vector3 GetNavigationTarget(LeaderState leader)
    {
        if (_rendezvousConfirmed &&
            leader.Status == BotStatus.Patrolling &&
            leader.HasTargetWaypoint)
        {
            Vector3 sharedWp = new(leader.TargetWaypointWorldX, leader.TargetWaypointWorldY, 0f);

            // Log only when the shared waypoint changes meaningfully — avoids per-tick spam.
            if (sharedWp.WorldDistanceXYTo(_lastSharedWaypointW) > WaypointUpdateThresholdYards)
            {
                _lastSharedWaypointW = sharedWp;
                logger.LogInformation(
                    $"[FFG] Waypoint-sharing: navigating to leader waypoint {sharedWp}");
            }

            return sharedWp;
        }

        // Fallback — position-chasing: navigate to the leader's body with stop-short offset.
        // Uses map-space coords which SetSingleWaypoint's IsMapPoint() check handles correctly.
        if (_rendezvousConfirmed)
        {
            // Rendezvous was confirmed but leader is not Patrolling — clear it so we
            // don't try waypoint mode again until the next co-location during patrol.
            _rendezvousConfirmed = false;
            _lastSharedWaypointW = default;
        }

        return ComputeFollowTargetMapPos(leader);
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
                Vector3 rewindTarget = GetNavigationTarget(leader);
                _lastNavigatedToLeaderWorldPos = rewindTarget;
                navigation.SetSingleWaypoint(rewindTarget);
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
            Vector3 retryTarget = GetNavigationTarget(currentLeader);
            _lastNavigatedToLeaderWorldPos = retryTarget;
            navigation.SetSingleWaypoint(retryTarget);
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
