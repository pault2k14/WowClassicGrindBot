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

    /// <summary>
    /// Tracks the leader's status from the previous UpdateIdle tick so we can detect
    /// the non-Patrolling → Patrolling transition that signals FRG has just resumed.
    /// Initialised to <c>null</c> in <see cref="OnEnter"/> so the very first tick
    /// always evaluates the transition correctly.
    /// </summary>
    private BotStatus? _lastLeaderStatus;

    /// <summary>
    /// Tracks whether the leader had a published patrol waypoint on the previous
    /// UpdateIdle tick. A false→true transition (<c>leaderJustPublishedWaypoint</c>)
    /// means the leader just armed its patrol (FRG sync-pause resolved + RefillWaypoints
    /// published the first waypoint). We immediately break the dead-band and start
    /// navigating so both bots begin moving within one API poll cycle (~250ms) of
    /// each other instead of the assist waiting for the 14y dead-band exit.
    /// Initialised to <c>false</c> in <see cref="OnEnter"/> so the very first tick
    /// where the leader has a waypoint always fires the detection.
    /// </summary>
    private bool _lastLeaderHadTargetWaypoint;

    /// <summary>
    /// Tracks whether the leader had a published approach-start anchor on the
    /// previous UpdateIdle tick. A false→true transition fires immediately when
    /// the leader enters ATG/PTG and breaks the dead-band so the assist navigates
    /// to the anchor before the leader has moved far from its starting position.
    /// Initialised to <c>false</c> in <see cref="OnEnter"/>.
    /// </summary>
    private bool _lastLeaderHadApproachStart;

    /// <summary>
    /// Session latch: set true when the approach-start anchor is first detected to
    /// be co-located (within Navigation.POP_DIST of the assist) during the current
    /// approach phase. Once latched, <see cref="GetNavigationTarget"/> commits to
    /// position-chasing for the rest of the phase rather than re-evaluating each
    /// tick. Without this latch, the assist oscillated around the POP_DIST (3.6y)
    /// boundary: anchorDist=3.5y → return chase target (east of bot, near leader)
    /// → bot moves east → anchorDist=3.7y → return anchor (now west of bot) →
    /// SetSingleWaypoint fires (drift ≈ 9y > WaypointUpdateThresholdYards) → bot
    /// turns 180°. Visible in log 17 as alternating ~948ms RightArrow/LeftArrow
    /// presses (~85° turns) at assist 02:37:37–02:37:38. Reset to false when the
    /// approach phase ends (HasApproachStart=false observed) and on FFG.OnEnter.
    /// </summary>
    private bool _approachAnchorColocated;

    /// <summary>
    /// Discriminates the three possible navigation target sources returned by
    /// <see cref="GetNavigationTarget"/>: the fixed approach-start anchor, the
    /// shared patrol waypoint, or the leader's live body (position-chasing).
    /// Used purely for diagnostic logging on mode transitions — the per-tick
    /// SetSingleWaypoint call in <see cref="UpdateNavigatingToLeader"/> was
    /// previously silent, masking the anchor↔chase flip-flop bug. With mode
    /// tracking, only mode CHANGES log; in-mode drift updates remain silent
    /// to avoid spam during steady-state chasing.
    /// </summary>
    private enum NavTargetMode { Anchor, PositionChase, WaypointSharing }
    private NavTargetMode _currentNavTargetMode = NavTargetMode.PositionChase;
    private NavTargetMode _lastLoggedNavTargetMode = NavTargetMode.PositionChase;

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
        _lastLeaderStatus = null; // force Patrolling-transition check on first UpdateIdle tick
        _lastLeaderHadTargetWaypoint = false; // force waypoint-published detection on first UpdateIdle tick
        _lastLeaderHadApproachStart = false;  // force approach-start detection on first UpdateIdle tick
        _approachAnchorColocated = false;     // reset co-located latch on each FFG entry
        _lastLoggedNavTargetMode = NavTargetMode.PositionChase; // first transition will log

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
        {
            // Clear the API status so the leader's FRG does not see a stale
            // Following/NavigatingToLeader from the previous FFG run. If FFG is
            // interrupted mid-navigation (e.g. the assist enters its own Combat or
            // Loot goal), the navigation is stopped above but CurrentStatus would
            // otherwise remain NavigatingToLeader. The leader's sync-pause resolves
            // on AnyAssistNavigating()=true, causing FRG to resume patrol while the
            // assist is actually looting — which is the "leader moving on while assist
            // is still looting" bug. Setting Waiting here ensures the leader sees the
            // correct Waiting status until FFG re-enters and posts Following again.
            assistStatusProvider.CurrentStatus = BotStatus.Waiting;
            _navState = NavState.Idle;
        }
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

            // Keep the transition tracker up to date so that crossing from < 7y into
            // the dead-band doesn't produce a false leaderJustResumedPatrol trigger.
            _lastLeaderStatus = leader.Status;

            // Track the waypoint transition for the 7-14y block below.
            // Do NOT call StartNavigatingToLeader here even if leaderJustPublishedWaypoint
            // is true: the assist is already within FollowingMaxYards (7y) of the leader,
            // so NavigatingToLeader would immediately exit back to Idle on the very next
            // GOAP tick (dist < 7y → "Reached follow position"). That 15ms flash produces
            // no movement and no benefit — the FRG sync-pause already resolves via
            // AnyAssistIsFollowing()=true which covers both Following and NavigatingToLeader.
            bool leaderJustPublishedWaypoint = leader.HasTargetWaypoint && !_lastLeaderHadTargetWaypoint
                && !leader.HasApproachStart;
            _lastLeaderHadTargetWaypoint = leader.HasTargetWaypoint;
            // (leaderJustPublishedWaypoint is used in the 7-14y block below)

            // Break the dead-band when the leader just entered ATG/PTG.
            // The approach-start anchor is the leader's world position at ATG entry —
            // navigating there immediately ensures the assist reaches the leader's
            // starting point before the leader has pressed interact far toward the mob.
            bool leaderStartedApproaching = leader.HasApproachStart && !_lastLeaderHadApproachStart;
            _lastLeaderHadApproachStart = leader.HasApproachStart;
            if (leaderStartedApproaching)
            {
                logger.LogInformation(
                    $"[FFG] Leader started approaching mob while co-located (dist={dist:0.0}y) — " +
                    "breaking dead-band: navigating to approach-start anchor.");
                StartNavigatingToLeader(leader);
                return;
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

            // ── Patrolling-transition detection ─────────────────────────────────
            // When the leader's FRG resumes after combat/loot, its status flips from
            // non-Patrolling → Patrolling. If we are in the dead-band (7–14y) AND
            // the rendezvous is already confirmed, the normal dead-band logic would
            // silently wait up to 2 seconds before the leader exceeds NavigatingMinYards
            // (14y / 7y/s = 2s), giving the leader a 14y head start. Detecting this
            // transition here breaks the silent wait and starts navigation immediately,
            // which in combination with the FRG sync-pause (see FollowRouteGoal) results
            // in both bots starting to move together within one API poll cycle (~250ms).
            bool leaderJustResumedPatrol =
                leader.Status == BotStatus.Patrolling &&
                _lastLeaderStatus.HasValue &&              // not the initialisation sentinel (null)
                _lastLeaderStatus != BotStatus.Patrolling; // genuine non→Patrol transition

            // leaderJustPublishedWaypoint fires when HasTargetWaypoint transitions false→true.
            // This is the reliable signal that FRG's sync-pause resolved and RefillWaypoints
            // (or PublishPatrolWaypoint) ran. Works even when the leader was already
            // Patrolling and the status doesn't change — which is the common post-combat case.
            // Suppressed when HasApproachStart=true: the anchor detection below takes
            // priority, and the patrol waypoint is irrelevant once ATG has started.
            // Without this suppression, both transitions fire in sequence (~800ms apart),
            // producing a wasted NavigatingToLeader→Idle cycle from the waypoint detection
            // before the anchor detection correctly navigates to the right target.
            bool leaderJustPublishedWaypoint = leader.HasTargetWaypoint && !_lastLeaderHadTargetWaypoint
                && !leader.HasApproachStart;

            // leaderStartedApproaching fires when HasApproachStart transitions false→true,
            // meaning the leader just entered ATG/PTG. Break the dead-band immediately so
            // the assist is at the anchor before the leader has pressed interact far toward the mob.
            bool leaderStartedApproaching = leader.HasApproachStart && !_lastLeaderHadApproachStart;

            BotStatus? previousLeaderStatus = _lastLeaderStatus; // capture before updating; may be null on first tick
            _lastLeaderStatus = leader.Status; // update for next tick
            _lastLeaderHadTargetWaypoint = leader.HasTargetWaypoint;
            _lastLeaderHadApproachStart = leader.HasApproachStart;

            if (leaderJustResumedPatrol ||
                (leaderJustPublishedWaypoint && leader.Status == BotStatus.Patrolling) ||
                leaderStartedApproaching)
            {
                logger.LogInformation(
                    $"[FFG] Leader resumed patrol/started approaching " +
                    $"(was {previousLeaderStatus?.ToString() ?? "Initial"}, waypointPublished={leaderJustPublishedWaypoint}, approachStarted={leaderStartedApproaching}) — " +
                    $"starting navigation immediately to match (dist={dist:0.0}y).");
                StartNavigatingToLeader(leader);
                return;
            }

            // Dead-band: NavigatingExitYards ≤ dist ≤ NavigatingMinYards.
            // Only stay silent here if we are ALREADY Following AND rendezvous is confirmed.
            // If rendezvous is not confirmed, we must navigate to close the gap to < 7y —
            // otherwise the leader will patrol away during the silent wait, making rendezvous
            // impossible for the entire next patrol leg (causing position-chasing instead of
            // waypoint-sharing, which then leads to the assist falling further and further behind).
            //
            // This commonly occurs when FFG re-enters after combat/loot with the assist in the
            // dead-band and status still set to Following from the previous session. Without this
            // fix, UpdateIdle silently waits 3-4s while the leader runs to 14y+, then the assist
            // starts chasing but can never close to < 7y before the next combat stop.
            if (assistStatusProvider.CurrentStatus == BotStatus.Following && _rendezvousConfirmed)
            {
                // Already Following with confirmed rendezvous — minor distance fluctuation, stay put.
            }
            else
            {
                // Navigate to close the gap: either status is not yet Following, or rendezvous
                // has not been confirmed since the last OnEnter. Both cases require reaching
                // < FollowingMaxYards (7y) before the leader pulls too far ahead.
                string reason = !_rendezvousConfirmed
                    ? "closing gap to confirm rendezvous"
                    : "not yet Following";
                logger.LogInformation(
                    $"[FFG] Dead-band ({dist:0.0}y) — {reason}.");
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

        // Mode-change diagnostic — fires whenever GetNavigationTarget switches between
        // Anchor / PositionChase / WaypointSharing. Independent of the drift gate
        // below: a non-Update caller (StartNavigatingToLeader, dead-band refresh,
        // rewind path) may have already SetSingleWaypoint on the new target before
        // we get here, leaving drift ≈ 0 on this tick. Without separating these
        // concerns, the mode change would be silently lost. Within-mode drift
        // updates do NOT log (chase-mode target moves with leader every tick); only
        // mode transitions log, so spam is bounded by the rate of approach/patrol
        // phase changes (~1 per few seconds in practice).
        if (_currentNavTargetMode != _lastLoggedNavTargetMode)
        {
            logger.LogInformation(
                $"[FFG] Nav target mode change: {_lastLoggedNavTargetMode} → {_currentNavTargetMode} " +
                $"(target={currentNavigationTarget}, drift={navTargetDrift:0.0}y).");
            _lastLoggedNavTargetMode = _currentNavTargetMode;
        }

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

            // Same co-located guard as Navigation_OnDestinationReached: if the target is
            // within POP_DIST, setting it would produce an immediate pop with no movement.
            float fallbackDist = playerReader.WorldPos.WorldDistanceXYTo(fallbackTarget);
            if (fallbackDist < Navigation.POP_DIST)
            {
                logger.LogWarning(
                    $"[FFG] Fallback target co-located ({fallbackDist:0.0}y < {Navigation.POP_DIST}y) — " +
                    "stale shared waypoint; reverting to position-chasing.");
                _rendezvousConfirmed = false;
                _lastSharedWaypointW = default;
                fallbackTarget = ComputeFollowTargetWorldPos(leader);
            }

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
    /// Returns the world-space position from <see cref="ComputeFollowTargetWorldPos"/>
    /// — the leader's body offset by <see cref="FollowStopShortYards"/> toward
    /// the assist. This is used when the leader is in combat, looting, resting,
    /// evading, or the rendezvous has not yet been confirmed after the last
    /// non-Patrolling state.</para>
    /// </summary>
    private Vector3 GetNavigationTarget(LeaderState leader)
    {
        // Priority 0: Leader is approaching a mob — navigate to the fixed approach-start anchor.
        // The anchor is the leader's world position at the moment ATG/PTG began, published via
        // LeaderNavigationProvider. Using a fixed target eliminates the moving-target problem:
        // chasing the leader's live body during approach means the pather continuously recomputes
        // a route to a retreating point, arriving in different terrain. Both bots navigating to
        // the same anchor start the final interact-key approach from the same geographic location.
        if (leader.HasApproachStart)
        {
            if (_rendezvousConfirmed)
            {
                _rendezvousConfirmed = false;
                _lastSharedWaypointW = default;
                logger.LogInformation("[FFG] Leader approaching mob — clearing rendezvous, locking to approach-start anchor.");
            }

            // Latch short-circuit: once the anchor was determined to be co-located in
            // this approach phase, commit to position-chasing for the rest of the phase.
            // The latch is reset in the else-branch below when HasApproachStart goes
            // false (ATG.OnExit fires ClearApproachStart on the leader; the assist
            // observes this via the next API poll).
            //
            // Without this latch the assist oscillates around the POP_DIST (3.6y)
            // boundary because GetNavigationTarget is called every GOAP tick:
            //   tick t₀ — anchorDist=3.5y → return chase target (east of bot, near leader)
            //   tick t₁ — bot moved east → anchorDist=3.7y → return anchor (now west of bot)
            //             SetSingleWaypoint fires (drift ≈ 9y > WaypointUpdateThresholdYards=3y)
            //             bot turns ~180° to face anchor
            //   tick t₂ — bot crosses 3.6y boundary again → flip back, another 180° turn
            // This was visible in log 17 as alternating ~948ms RightArrow / 949ms
            // LeftArrow presses (each ~85° rotation) at assist 02:37:37–02:37:38.
            if (_approachAnchorColocated)
            {
                _currentNavTargetMode = NavTargetMode.PositionChase;
                return ComputeFollowTargetWorldPos(leader);
            }

            Vector3 anchor = new(leader.ApproachStartWorldX, leader.ApproachStartWorldY, 0f);

            // Co-location guard: the approach-start anchor is the leader's world position
            // at ATG entry. If the assist had been navigating toward the same location as
            // the FRG patrol waypoint (common — the leader was just there), it may have
            // arrived within Navigation.POP_DIST of the anchor. Setting a waypoint at a
            // co-located point causes the navigation system's "already reached" check to
            // fire immediately, popping the waypoint without movement and triggering
            // OnDestinationReached on the very next navigation.Update() call. When that
            // happens, fall back to position-chasing toward the leader's live body —
            // guaranteed to be > POP_DIST away — and LATCH the decision via
            // _approachAnchorColocated so subsequent ticks don't re-evaluate.
            float anchorDist = playerReader.WorldPos.WorldDistanceXYTo(anchor);
            if (anchorDist < Navigation.POP_DIST)
            {
                _approachAnchorColocated = true;
                logger.LogWarning(
                    $"[FFG] Approach-start anchor co-located ({anchorDist:0.0}y < {Navigation.POP_DIST}y) — " +
                    "anchor would be immediately popped; reverting to position-chasing for the remainder of this approach phase.");
                _currentNavTargetMode = NavTargetMode.PositionChase;
                return ComputeFollowTargetWorldPos(leader);
            }

            // Anchor is not co-located — return world-space anchor directly.
            // SetSingleWaypoint's IsMapPoint check will not match (values are large
            // negative for Azeroth) and uses it as-is.
            _currentNavTargetMode = NavTargetMode.Anchor;
            return anchor;
        }
        else
        {
            // Approach phase ended — reset the latch so the next approach episode
            // (ATG re-entry, possibly on a new mob with a different anchor position)
            // is evaluated fresh. Safe across rapid re-entry: ATG.OnExit always fires
            // ClearApproachStart before PTG/ATG re-entry sets a new anchor, so the
            // assist observes HasApproachStart=false at least once between phases
            // (poll interval ~250ms).
            _approachAnchorColocated = false;
        }

        // Priority 1: Waypoint-sharing during patrol.
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

            _currentNavTargetMode = NavTargetMode.WaypointSharing;
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

        _currentNavTargetMode = NavTargetMode.PositionChase;
        return ComputeFollowTargetWorldPos(leader);
    }

    /// <summary>
    /// Returns a world-space position that is <see cref="FollowStopShortYards"/>
    /// behind the leader (toward the assist), preventing the assist from running
    /// through and past the leader. WoW has no player-vs-player collision, so
    /// without this offset the assist would overshoot the leader's position at
    /// running speed.
    /// <para>
    /// Operates entirely in world coordinates (yards). The previous map-space
    /// implementation produced cross-coordinate-system drift values when the
    /// caller compared its result against world-space targets returned by the
    /// anchor and waypoint-sharing branches of <see cref="GetNavigationTarget"/>:
    /// <c>WorldDistanceXYTo</c> between a map coord (e.g. <c>&lt;46, 60&gt;</c>) and
    /// a world coord (e.g. <c>&lt;-317, -4417&gt;</c>) is ~4500y, which silently
    /// disabled the per-tick drift gate in <see cref="UpdateNavigatingToLeader"/>
    /// and forced a path recomputation on every mode change. Visible in log 18 as
    /// a 50° body rotation when a brief (388ms) leader ATG re-entry triggered an
    /// Anchor↔PositionChase flip with a real target difference of only 1.85y.
    /// World-space throughout makes the drift gate work as designed and removes
    /// the cross-zone scale factor that the map-space version needed.
    /// </para>
    /// </summary>
    private Vector3 ComputeFollowTargetWorldPos(LeaderState leader)
    {
        // Direction from leader toward assist, in world coordinates (yards).
        Vector3 leaderW = leader.WorldPos;
        Vector3 assistW = playerReader.WorldPos;

        float dx = assistW.X - leaderW.X;
        float dy = assistW.Y - leaderW.Y;
        float lenXY = MathF.Sqrt(dx * dx + dy * dy);

        if (lenXY < 0.001f)
            return new Vector3(leaderW.X, leaderW.Y, 0f); // co-located — navigate to exact position

        // Target = leader's world position shifted FollowStopShortYards toward the
        // assist. Z is zeroed to match the format of every other path returned by
        // GetNavigationTarget (anchor, shared waypoint) — Navigation only uses XY.
        float invLen = 1f / lenXY;
        return new Vector3(
            leaderW.X + dx * invLen * FollowStopShortYards,
            leaderW.Y + dy * invLen * FollowStopShortYards,
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

        // Still in dead-band (FollowingMaxYards < dist < NavigatingMinYards).
        //
        // Previously this branch transitioned immediately to Idle and set Following.
        // That caused rapid oscillation every 250ms:
        //   1. OnDestinationReached → Idle, Following=True
        //   2. UpdateIdle: Following=True but _rendezvousConfirmed=False
        //      → dead-band fires → StartNavigatingToLeader
        //   3. ComputeFollowTargetWorldPos returns a target within POP_DIST of the
        //      assist (originally via map↔world conversion precision loss when the
        //      function returned map coords; now possible only when leader and
        //      assist are genuinely co-located near each other)
        //   4. Waypoint immediately popped → OnDestinationReached again → back to 1
        //
        // Fix: stay in NavigatingToLeader and refresh the waypoint to the leader's
        // current live position. If the refresh target is also within POP_DIST (leader
        // genuinely co-located), only then accept Idle — that case is already handled
        // by the dist < FollowingMaxYards branch above, so reaching here means the
        // leader has drifted and we should keep chasing.
        if (dist < NavigatingMinYards)
        {
            Vector3 refreshTarget = GetNavigationTarget(currentLeader);
            float refreshDist = playerReader.WorldPos.WorldDistanceXYTo(refreshTarget);

            if (refreshDist > Navigation.POP_DIST)
            {
                // Leader has moved since the waypoint was set — chase the new position
                // without cycling through Idle/dead-band.
                logger.LogInformation(
                    $"[FFG] Destination reached in dead-band (leader={dist:0.0}y) — refreshing waypoint (target={refreshDist:0.0}y away), staying NavigatingToLeader.");
                _lastNavigatedToLeaderWorldPos = refreshTarget;
                navigation.SetSingleWaypoint(refreshTarget);
                // Do NOT transition to Idle — remain in NavigatingToLeader.
                return;
            }

            // Refresh target is also co-located (refreshDist ≤ POP_DIST) — cannot
            // navigate any closer with the current map resolution. Accept Idle so the
            // assist does not spin indefinitely trying to reach an unreachable position.
            logger.LogInformation(
                $"[FFG] Destination reached in dead-band (leader={dist:0.0}y, target co-located {refreshDist:0.0}y) — entering Idle.");
            navigation.Stop();
            input.StopForward(true);
            ResetNavState();
            EnterState(NavState.Idle);
            assistStatusProvider.CurrentStatus = BotStatus.Following;
            assistStatusProvider.CantFollow = false;
            return;
        }

        // The leader has moved beyond NavigatingMinYards since we set the waypoint.
        // This is NOT a navigation failure — the pather successfully reached the target.
        // The leader is simply patrolling. Keep chasing; let TickNavActiveTimeout (30s)
        // be the sole CantFollow escalation path for a genuinely unreachable leader.
        logger.LogWarning(
            $"[FFG] Arrived but leader moved on ({dist:0.0}y) — refreshing waypoint to current position.");
        Vector3 retryTarget = GetNavigationTarget(currentLeader);

        // Guard: waypoint-sharing can return the patrol waypoint we just arrived at if the
        // leader hasn't published a new one yet. Navigation.RefillRouteToNextWaypoint calls
        // IsAtFinalWaypoint (reach ≈ 3.35y) — a co-located target is immediately popped
        // without producing movement. With destinationReachedLatched=true the
        // CompleteDestinationReached inside that pop path is a no-op, so navigation exits
        // with HasWaypoint()=false, triggering the fallback loop below every tick indefinitely.
        // Fix: if the target is within POP_DIST the shared waypoint is stale — drop
        // waypoint-sharing and navigate to the leader's live position instead.
        float retryDist = playerReader.WorldPos.WorldDistanceXYTo(retryTarget);
        if (retryDist < Navigation.POP_DIST)
        {
            logger.LogWarning(
                $"[FFG] Retry target co-located ({retryDist:0.0}y < {Navigation.POP_DIST}y) — " +
                "stale shared waypoint; reverting to position-chasing.");
            _rendezvousConfirmed = false;
            _lastSharedWaypointW = default;
            retryTarget = ComputeFollowTargetWorldPos(currentLeader);
        }

        _lastNavigatedToLeaderWorldPos = retryTarget;
        navigation.SetSingleWaypoint(retryTarget);
    }

    private void Navigation_OnWayPointReached()
    {
        if (_navState != NavState.NavigatingToLeader)
            return;

        LeaderState? leader = leaderConnection.LastLeaderState;
        if (leader == null) return;

        float dist = playerReader.WorldPos.WorldDistanceXYTo(leader.WorldPos);

        if (dist >= NavigatingMinYards)
            return; // still far out — keep navigating, no action needed here

        if (dist < FollowingMaxYards)
        {
            // Truly arrived within following range.
            navigation.Stop();
            ResetNavState();
            EnterState(NavState.Idle);
            assistStatusProvider.CurrentStatus = BotStatus.Following;
            assistStatusProvider.CantFollow = false;
            return;
        }

        // Dead-band zone (FollowingMaxYards < dist < NavigatingMinYards).
        // An intermediate waypoint was reached but the leader is still ahead.
        // Refresh the waypoint instead of stopping — avoids the same Idle/dead-band
        // oscillation described in Navigation_OnDestinationReached.
        Vector3 refreshTarget = GetNavigationTarget(leader);
        float refreshDist = playerReader.WorldPos.WorldDistanceXYTo(refreshTarget);

        if (refreshDist > Navigation.POP_DIST)
        {
            _lastNavigatedToLeaderWorldPos = refreshTarget;
            navigation.SetSingleWaypoint(refreshTarget);
            // Stay in NavigatingToLeader.
        }
        else
        {
            // Target co-located — can't get closer; stop.
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
