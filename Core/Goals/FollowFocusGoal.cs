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

    /// <summary>
    /// Minimum world-units of movement within <see cref="ActiveStuckThresholdSec"/>
    /// to consider the assist as still making progress while in
    /// <see cref="NavState.NavigatingToLeader"/>. The bot at running speed covers
    /// 7y/s, so 1y over 2.5 seconds is a strict "barely moving" threshold —
    /// reached only when the bot is genuinely caught on terrain.
    /// </summary>
    private const float ActiveStuckMinMovementWorld = 1.0f;

    /// <summary>
    /// Minimum cumulative displacement from the anchor captured AT the moment
    /// of Stuck before clearing the Stuck status. ASYMMETRIC with the trigger
    /// threshold (1y) — recovery must demonstrate real escape from the
    /// obstacle, not just slow incremental drift. Without this, a bot
    /// grinding sideways along a wall at 0.5y/s clears Stuck every ~2 s
    /// (drift exceeds 1y) without ever escaping; the leader sees a flicker
    /// of Stuck and immediately reverts before any recovery action can
    /// take effect. Observed in assist log 01:50:54–01:51:09: four
    /// Stuck→Resume cycles in 10 s while the bot was visibly pinned against
    /// terrain making zero chase progress (sinceBest=140 s).
    /// </summary>
    private const float ActiveStuckResumeMinMovementWorld = 3.0f;

    /// <summary>
    /// Time window over which active-stuck detection requires
    /// <see cref="ActiveStuckMinMovementWorld"/> of movement. Chosen at 2.5s so
    /// the assist reports <see cref="BotStatus.Stuck"/> *before* Navigation.cs's
    /// chase watchdog (4s) attempts unstuck — giving the leader a chance to
    /// pause via <see cref="AssistStateStore.ShouldLeaderPauseForAssist"/>
    /// while the unstuck attempt happens.
    /// </summary>
    private const double ActiveStuckThresholdSec = 2.5;

    /// <summary>
    /// Threshold for the chase-watchdog escalation to CantFollow. When
    /// <see cref="GoalsComponent.Navigation.ChaseSinceBestSec"/> exceeds this
    /// value while the assist is in NavigatingToLeader, FFG concludes that
    /// the assist is wedged on geometry that can't be navigated around (the
    /// chase watchdog's own unstuck attempts at 4 s and route-refill at 6 s
    /// have failed to make progress) and escalates to CantFollow so the
    /// leader navigates back to retrieve the assist. Distinct from
    /// raw-displacement timers (TickNavActiveTimeout, TickActiveStuckDetection)
    /// which can be reset by sideways drift; sinceBest measures progress
    /// toward target and only resets on a genuine new closest-distance.
    /// </summary>
    private const double ChaseWatchdogCantFollowSec = 15.0;

    /// <summary>
    /// SetSingleWaypoint loop guard thresholds. Detects unreachable navigation
    /// targets (most commonly a target inside the leader's blacklist) by
    /// counting consecutive SetSingleWaypoint calls within
    /// <see cref="StationarySetTimeWindowMs"/> of each other where the bot
    /// moved less than <see cref="StationarySetMovementThresholdYards"/>
    /// between sets. After <see cref="MaxConsecutiveStationarySetsBeforeCantFollow"/>
    /// such cycles, escalates immediately to CantFollow instead of waiting for
    /// the 30 s <see cref="TickNavActiveTimeout"/>.
    ///
    /// log-36 02:52:51:236 → 02:53:21:263 baseline: leader inside blacklist,
    /// every position-chase target also blacklisted, ~2000 wasted Update ticks
    /// over 30 s before escalation. With this guard: ~10 ticks, ~150 ms.
    ///
    /// Threshold rationale:
    ///   10 sets — high enough that single legitimate retries (e.g., one
    ///     re-issue after a failed path) don't trip; low enough to escalate
    ///     promptly compared to TickNavActiveTimeout's 30 s wall.
    ///   100 ms window — observed loop runs at ~15 ms per cycle; legitimate
    ///     path-result roundtrips take ≥250 ms, so a sub-100 ms gap between
    ///     consecutive sets is unmistakably the loop.
    ///   1.0 y movement — below 1 y means the bot truly hasn't progressed;
    ///     legitimate navigation moves much more per cycle.
    /// </summary>
    private const int MaxConsecutiveStationarySetsBeforeCantFollow = 10;
    private const int StationarySetTimeWindowMs = 100;
    private const float StationarySetMovementThresholdYards = 1.0f;

    // -----------------------------------------------------------------------
    // Dependencies
    // -----------------------------------------------------------------------
    private readonly ConfigurableInput input;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly Wait wait;
    private readonly ILogger<FollowFocusGoal> logger;
    private readonly RestHandler restHandler;
    private readonly CastingHandler castingHandler;
    private readonly IMountHandler mountHandler;
    private readonly ChatReader chatReader;
    private readonly Navigation navigation;
    private readonly AssistStatusProvider assistStatusProvider;
    private readonly LeaderConnectionStatus leaderConnection;
    private readonly LeaderNavigationProvider leaderNavProvider;

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
    // Active-navigation stuck reporting
    //
    // Detects a stationary assist while in NavigatingToLeader and reports
    // BotStatus.Stuck via the API so the leader's FRG.ShouldLeaderPauseForAssist
    // can pause patrol (which already gates on Status==Stuck per
    // AssistStateStore.ShouldLeaderPauseForAssist line 161). Recovery flips
    // back to BotStatus.NavigatingToLeader so the leader naturally resumes.
    //
    // Without this reporting, the leader sees a stuck assist as
    // status=NavigatingToLeader && dist<20y and continues patrolling — the
    // exact symptom in log 21 where the assist was caught on terrain for
    // ~12 seconds while the leader engaged the next mob. See log 21,
    // assist 00:11:03–00:11:15.
    // -----------------------------------------------------------------------
    private DateTime _activeStuckSinceUtc = DateTime.MinValue;
    private Vector3 _activeStuckCheckPosW;
    private bool _activeStuckReported;

    /// <summary>
    /// World-position anchor captured at the moment Stuck was reported.
    /// Used by TickActiveStuckDetection's asymmetric resume check — the
    /// bot must displace at least <see cref="ActiveStuckResumeMinMovementWorld"/>
    /// from this anchor before the Stuck flag clears.
    /// </summary>
    private Vector3 _activeStuckAnchorW;

    // -----------------------------------------------------------------------
    // CantFollow: hold position, wait for leader within LeaderArrivedYards
    // -----------------------------------------------------------------------
    private DateTime _cantFollowEnteredUtc;

    // -----------------------------------------------------------------------
    // SetSingleWaypoint loop guard (Fix 5, log-36)
    // -----------------------------------------------------------------------
    private int _consecutiveStationarySetWaypointCount;
    private DateTime _lastSetWaypointUtc = DateTime.MinValue;
    private Vector3 _lastSetWaypointPlayerPos;

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
        IOptions<PartyApiConfig> configOptions,
        IMountHandler mountHandler,
        CastingHandler castingHandler)
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
        this.castingHandler = castingHandler;
        this.mountHandler = mountHandler;
        this.Keys = classConfig.FollowFocusActions.Sequence;

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
        // FFG previously cached GoapKey.evadeRecovery state for two purposes:
        //   1. A hold-position gate in Update() that suppressed all
        //      navigation during the evade window.
        //   2. Suppression of stuck-detection inside the navigation timeout,
        //      active-stuck, and idle-stuck reporters.
        // Both are removed in session 27. Fix 4 (FRG.wantNavPaused filtering
        // IsIgnored) lets the leader actually retreat during the evade
        // window, so the original "leader is sitting next to the mob" worry
        // that motivated the hold gate no longer applies — FFG should track
        // the retreating leader normally. And the stuck-detection
        // suppression was protecting the gate's own forced stationary
        // window; with the gate gone, real stalls during evade should be
        // surfaced (BotStatus.Stuck → leader pauses → recovery), not
        // hidden. Nothing in FFG needs to react to the evadeRecovery state
        // any more.
    }

    // -----------------------------------------------------------------------
    // Lifecycle
    // -----------------------------------------------------------------------

    public override void OnEnter()
    {
        while (restHandler.IsResting())
            wait.Update(1000);

        _stuckCheckLastUtc = DateTime.MinValue;
        _activeStuckSinceUtc = DateTime.MinValue;
        _activeStuckReported = false;
        navigation.ResetApproachEscape();
        ResetSetWaypointLoopGuardState();

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
        ResetSetWaypointLoopGuardState();
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

    public bool ChangeToTarget(KeyAction keyAction) 
    {
        bool validChangeToTarget = false;

        if (!string.IsNullOrEmpty(keyAction.ChangeTargetTo) && keyAction.CanRun())
        {
            wait.Update();

            switch (keyAction.ChangeTargetTo)
            {
                case "focus":
                case "party1":
                    validChangeToTarget = true;
                    input.PressTargetFocus();
                    break;
                case "party2":
                    validChangeToTarget = true;
                    input.PressTargetFocusPartyMemberTwo();
                    break;
                case "party3":
                    validChangeToTarget = true;
                    input.PressTargetFocusPartyMemberThree();
                    break;
                case "party4":
                    validChangeToTarget = true;
                    input.PressTargetFocusPartyMemberFour();
                    break;
                default:
                    logger.LogWarning("keyAction.ChangeTargetTo not a valid target: " + keyAction.ChangeTargetTo);
                    break;
            }

            wait.Update();
        }

        return validChangeToTarget;
    }

    // -----------------------------------------------------------------------
    // Main update
    // -----------------------------------------------------------------------

    public override void Update()
    {
        if (bits.Drowning())
            input.PressJump();

        // Note: API-based mob blacklist diff lives in GoapAgent.GoapThread
        // (not here) so the signal interrupts the assist's combat regardless
        // of which goal is active. Combat (cost 4) preempts FFG (cost 19),
        // so an FFG-only diff would only fire after combat ended naturally —
        // observed in log 22 (assist 18:03:34–18:03:46): the assist saw the
        // GUID 3 seconds AFTER kill credit because FFG.OnEnter fired only
        // post-loot. The agent-level diff is the correct location.

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


        for (int i = 0; i < Keys.Length; i++)
        {
            KeyAction keyAction = Keys[i];
            bool validChangeToTarget = ChangeToTarget(keyAction);

            if (castingHandler.SpellInQueue() && !keyAction.BaseAction)
                continue;

            if (keyAction.BeforeCastDismount && mountHandler.IsMounted())
                mountHandler.Dismount();

            if (chatReader.ForcedFollow && !keyAction.UseWithForcedFollow)
                continue;

            if (castingHandler.CastIfReady(keyAction,
                keyAction.Interrupts.Count > 0
                ? keyAction.CanBeInterrupted
                : bits.Target_Alive))
                break;

            if (validChangeToTarget)
            {
                input.PressLastTarget();
                wait.Update();
            }
        }


        // ── Evade-recovery hold (REMOVED in session 27) ────────────────────
        // Session 23 added a hold-position gate here that returned early during
        // _evadeRecoveryActive, on the premise that "the leader is still next
        // to the blacklisted mob when the event fires (and stays there until
        // the leader-side PressClearTarget releases the wantNavPaused gate in
        // FRG.cs:741); chasing the leader's body via PositionChase would
        // route the assist into the danger zone."
        //
        // That premise was true at the time because Defect D (FRG.wantNavPaused
        // didn't filter IsIgnored) prevented the leader from moving during
        // evade. Fix 4 in session 26 closed Defect D — wantNavPaused now
        // filters playerReader.IsIgnored, so the leader actually retreats.
        //
        // Log 27 confirmed the new failure mode created by leaving this gate
        // in place after Fix 4:
        //   00:50:05:631  evade fires
        //   00:50:05:829  leader's FRG starts navigating (Fix 4 working) —
        //                  RightArrow movement keys begin
        //   00:50:08:608  leader has covered ~18y, still moving
        //   00:50:09:602  leader hits LeaderPauseYards=20y → "[FRG] Pausing
        //                  for assist — dist=20.0y status=TooFar"
        //   00:50:05:771–30:610  assist FFG total silence (this gate held it
        //                  in Idle for the full 24.84s window)
        //   00:50:30:646  leader's evade window elapses
        //   00:50:31:011  Tab in CombatGoal.FindPossibleThreats finds the
        //                  same blacklisted mob still adjacent (because the
        //                  leader couldn't continue retreating)
        //   00:50:31:057  re-fired evade → cycle restarts
        //
        // Removing the gate lets FFG's normal state machine run during the
        // evade-recovery window. The assist stays Idle while inside
        // FollowingMaxYards (7y), transitions to NavigatingToLeader when the
        // retreating leader exceeds NavigatingMinYards (14y), and tracks the
        // leader's retreat naturally. The original "into the danger zone"
        // concern is moot because the leader's body is no longer in the
        // danger zone — it's actively retreating away from it.
        //
        // Other defenses remain in place to ensure the assist does not
        // engage the blacklisted mob during the window:
        //   - CombatGoal's AddPrecondition(GoapKey.evadeRecovery, false)
        //     keeps Combat unselectable for the entire 25 s window.
        //   - TFT.CanRun rejects on _evadeRecoveryActive AND, independently,
        //     when playerReader.FocusTargetGuid is in IsIgnored (Fix 3/5a).
        //   - CombatGoal.Update's session-24 IsIgnored short-circuit
        //     (CombatGoal.cs:198) catches any race window where Combat
        //     somehow runs an Update tick.
        //   - TFT.Update's IsIgnored guard before the F press (Fix 5b).
        //
        // Stuck detection now runs unconditionally during the evade window
        // (see TickNavActiveTimeout / TickActiveStuckDetection /
        // TickIdleStuckDetection). The previous code suppressed it during
        // evade because the hold gate above forced the assist stationary
        // and we didn't want false-positive Stuck reports for that forced
        // pause. With the gate gone, the assist navigates during evade and
        // any real stall is a real problem — surfacing it (BotStatus.Stuck
        // → leader pauses → recovery / pather-based escape) is exactly the
        // right behaviour during a flee. Only bits.Combat() remains as an
        // exemption because cast-loop stationarity is genuinely not a stall.

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

        // ── Chase-progress watchdog escalation ──────────────────────────────
        // When Navigation's chase watchdog reports it's been more than
        // ChaseWatchdogCantFollowSec (15 s) since the assist last achieved a
        // new closest-distance to the chase target, escalate to CantFollow.
        // sinceBest is progress-toward-target, not raw displacement — it
        // doesn't reset on geometry-grinding drift the way TickNavActiveTimeout
        // can. By the time it reaches 15 s, the watchdog's own recovery
        // paths (unstuck attempt at 4 s, route refill at 6 s) have failed
        // to make progress, so the obstacle is unrecoverable and the leader
        // should retrieve the assist.
        TickChaseWatchdog();
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
                // NOTE: Do NOT clear assistStatusProvider.CantFollow here.
                // This branch fires while the assist is still actively navigating
                // (see comment block above — we explicitly keep navigation active
                // between 7 y and 10 y to avoid the stop-start leapfrog). Clearing
                // the CantFollow override at this point removes the only thing
                // keeping FFG selectable during evade recovery while dmgDone is
                // still latched from a recent CombatGoal cast — the planner then
                // returns NO PLAN (CombatGoal is blocked by evadeRecovery=false,
                // TFT by _evadeRecoveryActive, FFG by assistshouldfollow=false),
                // GoapAgent never calls FFG.OnExit, and the in-flight movement
                // keys (W/Right/Left from navigation.Update) stay pressed for the
                // remainder of the recovery window. Observed in log 28:
                //   01:23:07:469  this branch fired, CantFollow cleared
                //   01:23:07:484  NO PLAN with dmgDone=True, evadeRecovery=True
                //   01:23:07:484–30:716  total log silence, assist runs forward
                //   01:23:30:844  Combat selected, FFG.OnExit finally fires
                // CantFollow is correctly cleared in two other places:
                //   - line 535 (UpdateIdle, dist < FollowingMaxYards=7y)
                //   - line 707 (UpdateNavigatingToLeader, dist < FollowingMaxYards
                //              and entering Idle)
                // Both fire only when the assist has truly settled — those are
                // the right moments to drop the override.
            }
            // Fall through — navigation continues; no stop, no Idle transition.
        }

        // Active-navigation stuck reporting. May override the Following/NavigatingToLeader
        // status set above to BotStatus.Stuck if the assist has stopped making progress.
        // The leader's ShouldLeaderPauseForAssist already gates on Status==Stuck (line 161
        // of AssistStateStore), so reporting here is sufficient — no additional API change
        // required. See log 21 (assist 00:11:03–00:11:15) for the symptom this fixes.
        TickActiveStuckDetection();

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
            // Path-preservation suppression: when the new navigation target is roughly
            // aligned with the bot's current direction of travel AND further away than
            // the bot's current waypoint, the existing route is still a valid prefix of
            // the route to the new target. Re-running the pather only to lengthen the
            // route by ~12y at the end is wasteful and visibly disruptive:
            // SetSingleWaypoint discards the existing route, the bot momentarily idles
            // waiting for the new path, and the new path's routeTop is often in a
            // slightly different direction than the previous, producing a visible body
            // rotation as the bot turns toward the new heading.
            //
            // Originally added in session 19 for WaypointSharing mode to absorb the
            // leader's per-pop waypoint advances during patrol. Broadened in session 20
            // to cover Anchor and PositionChase modes after observing the same path-
            // discard storm during ATG/PTG cycles:
            //
            //   Log 20 (assist 23:28:07–23:28:14): leader cycled ATG#1 → PTG#1 → ATG#2
            //   → PTG#2 → Combat in 7 seconds. Each Has* toggle flipped the assist's
            //   nav mode, and each flip with drift > 3y discarded the existing path.
            //   Combined with intra-PositionChase drift updates as the leader's body
            //   moved 3-4y per tick, the assist accumulated 5 path rebuilds in 2
            //   seconds. Each rebuild started from the bot's current (already-SW)
            //   position, producing yet another route that began SW. The bot wandered
            //   SW for ~6s before stalling and another ~8s correcting NE before
            //   reaching the leader, where 2 seconds of direct travel would have
            //   sufficed. The user described this as "ran off in opposite direction
            //   before correcting".
            //
            // The geometric principle (existing route is a valid prefix when the new
            // target is in the same general direction, only further) holds regardless
            // of which mode produced the new target. Direction changes (cos < 0.7,
            // e.g. leader pivots, route wraps, or genuine rendezvous) still fail the
            // angle test and refresh as before.
            //
            // Trade-off in PositionChase steady-state: with suppression the bot
            // follows leader's position-from-a-few-seconds-ago instead of leader's
            // live position. When the bot reaches the old position via
            // OnDestinationReached, GetNavigationTarget refreshes to the latest
            // chase point. Net: the bot trails by roughly the distance the leader
            // moves during one nav phase rather than constantly re-pathing. Better
            // than rebuild storms; reactive enough for normal patrol following.
            bool suppressRefresh = false;
            if (navigation.HasWaypoint())
            {
                Vector3 existingWp = navigation.TopWaypointW;
                Vector3 botPos = playerReader.WorldPos;

                float ax = existingWp.X - botPos.X;
                float ay = existingWp.Y - botPos.Y;
                float nx = currentNavigationTarget.X - botPos.X;
                float ny = currentNavigationTarget.Y - botPos.Y;

                float aLen = MathF.Sqrt(ax * ax + ay * ay);
                float nLen = MathF.Sqrt(nx * nx + ny * ny);

                // aLen > 0.001 guards against the degenerate case where the bot has
                // already arrived at the existing waypoint (zero-length vector → cos
                // undefined). nLen >= aLen ensures the new target really is "further
                // along" — if the new target is closer to the bot than the existing
                // waypoint, the existing route overshoots and should be refreshed
                // (e.g., leader pivoted and now is between bot and old wpTop).
                if (aLen > 0.001f && nLen > 0.001f && nLen >= aLen)
                {
                    float cosAngle = (ax * nx + ay * ny) / (aLen * nLen);
                    if (cosAngle > 0.7f)
                    {
                        suppressRefresh = true;
                        logger.LogDebug(
                            $"[FFG] Path-preservation suppression ({_currentNavTargetMode}): " +
                            $"existing wp at {existingWp} ({aLen:0.0}y) still aligned with " +
                            $"new target {currentNavigationTarget} ({nLen:0.0}y), cos={cosAngle:0.00}. " +
                            "Keeping existing route.");
                    }
                }
            }

            // Always update _lastNavigatedToLeaderWorldPos — even on suppression — so the
            // next drift comparison uses the latest target as the baseline. If we left
            // it stale, the drift gate would re-fire on every tick (the new target is
            // still > 3y from the historical _lastNavigatedToLeaderWorldPos).
            _lastNavigatedToLeaderWorldPos = currentNavigationTarget;

            if (!suppressRefresh)
            {
                SetWaypointLoopGuarded(currentNavigationTarget); // world coords — SetWayPoints detects non-map range
            }
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
            // log-36: if SetWaypointLoopGuarded escalates to CantFollow (10
            // consecutive stationary sets within 100ms, e.g. unreachable
            // blacklisted target), the line ~1023 status assignment below
            // would otherwise overwrite the CantFollow status back to
            // NavigatingToLeader. Bail out early on escalation.
            if (!SetWaypointLoopGuarded(fallbackTarget))
                return;
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

        // log-36: if the loop guard escalates to CantFollow (target inside the
        // leader's blacklist on every retry), the EnterState(NavigatingToLeader)
        // and CurrentStatus assignment below would overwrite the CantFollow
        // state set by EnterCantFollow. Bail out on escalation.
        if (!SetWaypointLoopGuarded(target))
            return;
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

            // Defensive blacklist guard (log-35 14:08:16:226 → 14:08:16:894+):
            // The leader can briefly publish a transient blacklisted waypoint via
            // OnWayPointReached before its own SkipBlacklistedWaypoints filters
            // it out. The leader-side fix in FRG.Navigation_OnWayPointReached and
            // PublishPatrolWaypoint now uses TopPublishableWaypointW to avoid this,
            // but timing windows or future leader/assist blacklist divergence can
            // still produce a bad shared waypoint. Without this guard, the assist
            // sets the bad waypoint, Navigation.SkipBlacklistedWaypoints pops it,
            // OnDestinationReached fires (silent — NavDbg gated), this method is
            // called again from the dist-fallback at FFG line 957, returns the
            // same bad waypoint, SetSingleWaypoint is called again — infinite
            // loop every ~15ms, observed for 35+ seconds in log-35.
            //
            // Fall back to position-chase but DON'T clear _rendezvousConfirmed —
            // when the leader publishes a clean waypoint on the next pop,
            // waypoint-sharing resumes automatically without requiring a fresh
            // rendezvous co-location.
            if (navigation.AreaBlacklist != null &&
                navigation.AreaBlacklist.ContainsWorld(sharedWp))
            {
                if (sharedWp.WorldDistanceXYTo(_lastSharedWaypointW) > WaypointUpdateThresholdYards)
                {
                    _lastSharedWaypointW = sharedWp; // remember to dedup log spam
                    logger.LogWarning(
                        $"[FFG] Waypoint-sharing: leader's target waypoint {sharedWp} " +
                        "is in assist's blacklist — falling back to position-chase " +
                        "(rendezvous remains confirmed; will resume sharing on next clean publish).");
                }

                _currentNavTargetMode = NavTargetMode.PositionChase;
                return ComputeFollowTargetWorldPos(leader);
            }

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
        Vector3 target = new Vector3(
            leaderW.X + dx * invLen * FollowStopShortYards,
            leaderW.Y + dy * invLen * FollowStopShortYards,
            0f);

        // log-36 02:52:51:236 → 02:53:21:263: when the leader is inside a blacklist
        // (e.g., killed/looted a mob inside a blacklisted area), the standard target
        // (leader_pos + FollowStopShortYards * dir_to_assist) sits inside the same
        // rect. Navigation.SkipBlacklistedWaypoints pops it on the next Update tick,
        // OnDestinationReached fires, GetNavigationTarget is re-called and returns
        // the same blacklisted point — infinite SetSingleWaypoint→pop loop at
        // ~15ms intervals for 30 seconds until TickNavActiveTimeout escalates.
        //
        // Fix: project further along the leader→assist line in 2y steps until a
        // non-blacklisted point is found. The assist navigates to the closest safe
        // point near the leader (semantically: "get as close as possible without
        // entering forbidden terrain"). If no safe point exists between the leader
        // and the assist's current position, return the assist's position so the
        // SetWaypointLoopGuarded helper detects the no-movement loop and escalates
        // to CantFollow within ~150ms instead of 30s.
        // Fix 19 (log-44 00:40:40 → 00:41:14: assist ping-ponging in/out of
        // blacklist rect at X≈952-955, 6 full oscillations over ~34 s):
        // the original Fix 4 outer trigger check below used strict
        // ContainsWorld(target). When the standard target landed even 1 y
        // outside the rect's strict boundary (e.g. <947.12, 285.62> with
        // rect MinX≈952.4, target X=947.12 is 5.3 y outside), Fix 4 didn't
        // fire — the function returned the standard target as-is. FFG then
        // set this as the wp, the pather built a 7-point route to reach it,
        // and bot movement execution (auto-run + clockwise turn corrections)
        // overshot the wp by ~5-15 y of forward inertia, depositing the bot
        // inside the rect at X≈952.6 within ~3 s. Navigation's escape-first
        // then fired, drove bot west to X≈937, FFG recomputed the same
        // (still-outside) target, pather rebuilt route, bot overshot again.
        //
        // Fix: change the outer trigger from ContainsWorld (strict) to
        // TryGetContainingRectInflated (margin=ProjectionSafetyMarginYards
        // = 6 y). Now ANY target within 6 y of a rect — even technically
        // outside it — triggers the projection loop. The inflated check
        // inside the loop (also 6 y) returns candidates that are ≥6 y from
        // the strict rect, so the wp itself is at least 6 y clear. Bot's
        // overshoot during route execution is bounded by POP_DIST (3.6 y)
        // plus a small amount of inertia, well under the 6 y margin.
        //
        // 6 y margin chosen for consistency with Fix 12's ExitMargin and
        // Fix 18's projection-loop check. Both inner and outer checks use
        // the same margin so the loop always finds a strictly-better
        // candidate than the standard target if one exists at all.
        //
        // If the entire leader→assist line is within 6 y of a rect (loop
        // finds no safe candidate), control falls through to Fix 12's
        // assist self-escape logic below — same failure-mode as before.
        const float ProjectionSafetyMarginYards = 6.0f;
        if (navigation.AreaBlacklist != null &&
            navigation.AreaBlacklist.TryGetContainingRectInflated(
                target, ProjectionSafetyMarginYards, out _))
        {
            const float STEP_YARDS = 2.0f;
            for (float distFromLeader = FollowStopShortYards + STEP_YARDS;
                 distFromLeader < lenXY;
                 distFromLeader += STEP_YARDS)
            {
                Vector3 candidate = new Vector3(
                    leaderW.X + dx * invLen * distFromLeader,
                    leaderW.Y + dy * invLen * distFromLeader,
                    0f);

                if (!navigation.AreaBlacklist.TryGetContainingRectInflated(
                        candidate, ProjectionSafetyMarginYards, out _))
                {
                    logger.LogWarning(
                        $"[FFG] Position-chase: standard target {target} is within " +
                        $"{ProjectionSafetyMarginYards:0.0}y of assist's blacklist; " +
                        $"projected toward assist to {candidate} ({distFromLeader:0.0}y from leader, " +
                        $"{lenXY - distFromLeader:0.0}y from assist).");
                    return candidate;
                }
            }

            // Fix 12 (log-42 17:23:23:280 → 17:24:02:681: assist stuck for 39 s
            // inside leader's blacklist rect (952.39,277.05)-(999.27,327.20)
            // after combat ended; assist at <957.40, 299.97> is 5 y east of the
            // rect's west edge; leader just outside at <951.76, 286.30>; entire
            // leader→assist line crosses the rect. Old fallback returned assist's
            // own position → SetWaypoint loop guard → CantFollow → leader fires
            // AssistReturn → Fix 9 projects rescue target to leader-side edge
            // <951.81, 288.23> → leader stops there, still 15.5 y from assist —
            // LeaderArrivedYards=6 y check in UpdateCantFollow never fires →
            // leader cycles AssistReturn timeouts every 25 s indefinitely; assist
            // never moves. Geometric deadlock: rect is 47 y × 50 y, far wider
            // than 2×LeaderArrivedYards=12 y, so Fix 9's projection geometrically
            // cannot place the leader within 6 y of an assist deep inside the
            // rect, and Navigation's own Escape-first never runs because
            // UpdateCantFollow holds active=false (navigation.Stop every tick).
            //
            // Fix: when the entire leader→assist line is blacklisted AND the
            // assist's own position is inside a blacklist rect, escape the rect
            // first via its closest edge. Return the exit point as the
            // navigation target; the assist moves OUT of the blacklist by ~6-7 y
            // (above the 1 y SetWaypoint loop-guard threshold so the loop guard
            // is naturally reset by real movement), then the next FFG.OnDestinationReached
            // re-enters ComputeFollowTargetWorldPos with the assist outside the
            // rect — standard leader→assist offset target now works since the
            // line no longer crosses the rect from the assist's side.
            //
            // Exit margin = 6 y. POP_DIST is 3.6 y, so a bot popping the wp at
            // 3.6 y short still ends up 2.4 y clear of the rect at worst. Margin
            // also matches DetourMargin/2=6 — the inflation used elsewhere in
            // Navigation when computing safety buffers around blacklist rects.
            //
            // Score = distFromAssist + 0.5*distFromLeader. The 0.5 weight gives
            // a mild lean toward exits on the leader's side without overriding
            // the cheaper close-edge choice when the leader is far.
            //
            // If all 4 axis-aligned exits land in another rect (overlapping
            // blacklist composition), no candidate found → fall through to the
            // old "return assist position" path. Strictly better than current,
            // never worse.
            if (navigation.AreaBlacklist.TryGetContainingRect(assistW, out var containingRect))
            {
                const float ExitMargin = 6.0f;

                ReadOnlySpan<Vector3> exitCandidates = stackalloc Vector3[]
                {
                    new Vector3(containingRect.MinX - ExitMargin, assistW.Y, 0f), // west exit
                    new Vector3(containingRect.MaxX + ExitMargin, assistW.Y, 0f), // east exit
                    new Vector3(assistW.X, containingRect.MinY - ExitMargin, 0f), // south exit
                    new Vector3(assistW.X, containingRect.MaxY + ExitMargin, 0f), // north exit
                };

                Vector3 bestExit = default;
                float bestScore = float.MaxValue;
                bool foundExit = false;

                for (int i = 0; i < exitCandidates.Length; i++)
                {
                    Vector3 c = exitCandidates[i];

                    // Skip candidates that land inside another blacklist rect
                    // (overlapping-rect composition); the bot would just be
                    // stuck in a new rect.
                    if (navigation.AreaBlacklist.ContainsWorld(c))
                        continue;

                    float distFromAssist = c.WorldDistanceXYTo(assistW);
                    float distFromLeader = c.WorldDistanceXYTo(leaderW);
                    float score = distFromAssist + 0.5f * distFromLeader;

                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestExit = c;
                        foundExit = true;
                    }
                }

                if (foundExit)
                {
                    logger.LogWarning(
                        $"[FFG] Position-chase: leader→assist line entirely blacklisted AND assist " +
                        $"inside rect — escaping rect first via exit point {bestExit} " +
                        $"(rect=({containingRect.MinX:0.0},{containingRect.MinY:0.0})-" +
                        $"({containingRect.MaxX:0.0},{containingRect.MaxY:0.0}), assist={assistW}, " +
                        $"exitDist={bestExit.WorldDistanceXYTo(assistW):0.0}y, " +
                        $"leaderDist={bestExit.WorldDistanceXYTo(leaderW):0.0}y).");
                    return bestExit;
                }
            }

            logger.LogWarning(
                $"[FFG] Position-chase: leader→assist segment is entirely blacklisted " +
                $"(leader={leaderW}, assist={assistW}). Returning assist position; " +
                $"SetWaypoint loop guard will escalate to CantFollow.");
            return new Vector3(assistW.X, assistW.Y, 0f);
        }

        return target;
    }

    /// <summary>
    /// Wraps <see cref="Navigation.SetSingleWaypoint"/> with a loop guard that
    /// detects "set wp → immediately popped, no movement" cycles caused by
    /// unreachable navigation targets. The most common trigger is a target
    /// inside the leader's blacklist that <see cref="Navigation.SkipBlacklistedWaypoints"/>
    /// pops on every Update tick.
    ///
    /// <para>Counts consecutive sets within
    /// <see cref="StationarySetTimeWindowMs"/> of each other where the bot
    /// moved less than <see cref="StationarySetMovementThresholdYards"/>. After
    /// <see cref="MaxConsecutiveStationarySetsBeforeCantFollow"/> such cycles,
    /// escalates to <see cref="EnterCantFollow"/> and returns false. Returns
    /// true on a successful set; the time/movement gate naturally resets the
    /// counter when navigation is making progress (legitimate sets are
    /// >100 ms apart and move >1 y between them).</para>
    ///
    /// <para>Without this guard, the existing <see cref="TickNavActiveTimeout"/>
    /// (30 s) is the only escalation path; log-36 02:52:51:236 → 02:53:21:263
    /// shows ~2000 wasted Update iterations during that wait. With it, the
    /// loop is detected after ~150 ms and CantFollow fires immediately.</para>
    /// </summary>
    private bool SetWaypointLoopGuarded(Vector3 target)
    {
        DateTime now = DateTime.UtcNow;
        Vector3 botPos = playerReader.WorldPos;

        if (_lastSetWaypointUtc != DateTime.MinValue)
        {
            double elapsedMs = (now - _lastSetWaypointUtc).TotalMilliseconds;
            float moved = botPos.WorldDistanceXYTo(_lastSetWaypointPlayerPos);

            if (elapsedMs < StationarySetTimeWindowMs && moved < StationarySetMovementThresholdYards)
            {
                _consecutiveStationarySetWaypointCount++;
            }
            else
            {
                _consecutiveStationarySetWaypointCount = 0;
            }

            if (_consecutiveStationarySetWaypointCount >= MaxConsecutiveStationarySetsBeforeCantFollow)
            {
                logger.LogWarning(
                    $"[FFG] SetWaypoint loop guard: {_consecutiveStationarySetWaypointCount} consecutive " +
                    $"sets within {StationarySetTimeWindowMs} ms of each other, moved " +
                    $"<{StationarySetMovementThresholdYards} y per cycle — target {target} is unreachable. " +
                    "Escalating to CantFollow without 30 s TickNavActiveTimeout wait.");
                ResetSetWaypointLoopGuardState();
                EnterCantFollow();
                return false;
            }
        }

        navigation.SetSingleWaypoint(target);
        _lastSetWaypointUtc = now;
        _lastSetWaypointPlayerPos = botPos;
        return true;
    }

    private void ResetSetWaypointLoopGuardState()
    {
        _consecutiveStationarySetWaypointCount = 0;
        _lastSetWaypointUtc = DateTime.MinValue;
        _lastSetWaypointPlayerPos = default;
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
        _activeStuckSinceUtc = DateTime.MinValue;
        _activeStuckReported = false;
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

        // Accumulate elapsed only outside combat. Combat legitimately suspends
        // forward movement (cast loops, etc.) and is handled by CombatGoal —
        // FFG isn't even the active goal then in most cases. Evade-recovery
        // is NOT exempted: during a flee the assist must keep moving, and a
        // genuine stall during the window is exactly the case the timeout
        // should surface (escalation to CantFollow → leader navigates back).
        bool countActive = !bits.Combat();
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
    // Chase-progress watchdog escalation
    //
    // Reads Navigation.ChaseSinceBestSec — the time since the chase watchdog
    // last recorded a new closest-distance to the chase target. Distinct
    // from raw-displacement timers in this class:
    //
    //   - TickNavActiveTimeout: resets on NavigationProgressResetYards (3 y)
    //     of any movement. A bot grinding sideways along terrain can
    //     accumulate 3 y of drift in a few seconds and reset the timer
    //     forever, never escalating.
    //
    //   - TickActiveStuckDetection: triggers/resumes on raw displacement
    //     within a sliding window. Even with the asymmetric resume threshold
    //     (3 y from anchor), 3 y of drift along a wall eventually clears
    //     the Stuck flag — same flapping risk over a longer cycle.
    //
    // ChaseSinceBestSec is the only metric that's resilient to drift
    // because it only resets when the bot achieves a NEW closest distance
    // to the chase target. A bot wedged against geometry making zero
    // progress toward target accumulates sinceBest indefinitely regardless
    // of how much sideways drift occurs.
    //
    // When sinceBest exceeds ChaseWatchdogCantFollowSec (15 s), the chase
    // watchdog's own internal recovery paths (unstuck attempt at 4 s,
    // route refill at 6 s — both in Navigation.cs) have already had two
    // chances to make progress and failed. The obstacle is unrecoverable
    // by automated means; escalate to CantFollow so the leader navigates
    // back to retrieve the assist.
    //
    // Skipped when chase isn't being tracked (ChaseSinceBestSec returns 0).
    // -----------------------------------------------------------------------
    private void TickChaseWatchdog()
    {
        double sinceBest = navigation.ChaseSinceBestSec;
        if (sinceBest < ChaseWatchdogCantFollowSec)
            return;

        logger.LogWarning(
            $"[NAV-DIAG] Chase watchdog fire: stuckDetector.OwnerId={navigation.StuckDetectorOwnerId} " +
            $"Enabled={navigation.StuckDetectorEnabled} sinceBest={sinceBest:0.0}s");
        logger.LogWarning(
            $"[FFG] Chase watchdog: {sinceBest:0.0}s without closing on chase target — " +
            $"escalating to CantFollow. Leader will navigate back to retrieve assist.");
        EnterCantFollow();
    }

    // -----------------------------------------------------------------------
    // Active-navigation stuck reporting
    //
    // Detects when the assist is in NavigatingToLeader but failing to make
    // forward progress, and reports BotStatus.Stuck via the API so the leader's
    // FRG.ShouldLeaderPauseForAssist gates on it (line 161 of AssistStateStore).
    //
    // Without this signal, the leader sees the assist as
    // status=NavigatingToLeader && dist<20y and continues patrolling onto the
    // next mob, leaving the stuck assist behind. Observed in log 21
    // (assist 00:11:03–00:11:15): the assist was caught on terrain at
    // <-404.23, -4062.5> for ~12s while the leader engaged the next mob.
    // Navigation.cs's chase watchdog logged "No chase progress while stationary"
    // every 1.5s during the stuck period but did not surface the condition to
    // the leader API.
    //
    // Triggers on movement < ActiveStuckMinMovementWorld (1y) over
    // ActiveStuckThresholdSec (2.5s). Recovery flips status back to
    // NavigatingToLeader so the leader resumes naturally. Skipped during
    // combat (CombatGoal handles its own positioning, the assist legitimately
    // stops to cast). NOT skipped during evade-recovery: with the session-27
    // hold-gate removal the assist navigates during the window, so a stall
    // there is a real stall — and surfacing it (leader pauses, navigates
    // back to the stuck assist) is the right behaviour for a crucial flee.
    // -----------------------------------------------------------------------
    private void TickActiveStuckDetection()
    {
        if (bits.Combat())
        {
            // Combat naturally suspends progress; clear tracking so we
            // don't immediately flag stuck on resumption.
            _activeStuckSinceUtc = DateTime.MinValue;
            _activeStuckReported = false;
            return;
        }

        Vector3 currentPos = playerReader.WorldPos;
        var now = DateTime.UtcNow;

        if (_activeStuckSinceUtc == DateTime.MinValue)
        {
            // First tick of this navigation phase — anchor the position and start the clock.
            _activeStuckSinceUtc = now;
            _activeStuckCheckPosW = currentPos;
            return;
        }

        // ── Asymmetric resume path ──────────────────────────────────────────
        // While Stuck is reported, the resume check uses a SEPARATE anchor
        // (captured at the moment of Stuck) and a HIGHER threshold (3y vs
        // the 1y trigger). This prevents slow incremental drift along an
        // obstacle from clearing Stuck without real escape — the symptom
        // the user observed in log 01:50:54-01:51:09 where the bot
        // flapped Stuck → NavigatingToLeader four times in 10 s while still
        // pinned against a wall (sinceBest=140 s on the chase watchdog).
        if (_activeStuckReported)
        {
            float displaced = currentPos.WorldDistanceXYTo(_activeStuckAnchorW);
            if (displaced >= ActiveStuckResumeMinMovementWorld)
            {
                _activeStuckReported = false;
                _activeStuckCheckPosW = currentPos;
                _activeStuckSinceUtc = now;
                // Restore NavigatingToLeader so the leader resumes patrol. Note
                // that a concurrent Following assignment in UpdateNavigatingToLeader
                // (when dist<10y) sets Following AFTER us when the next tick runs,
                // so this restoration is safe — the higher-level status logic
                // re-asserts itself naturally on the next pass.
                assistStatusProvider.CurrentStatus = BotStatus.NavigatingToLeader;
                logger.LogInformation(
                    $"[FFG] Movement resumed during navigation (displaced {displaced:0.00}y " +
                    $"from stuck anchor) — reverting status from Stuck to NavigatingToLeader.");
            }
            // Else: still wedged. Keep Stuck status. Don't advance the
            // trigger anchor; the trigger window is irrelevant while
            // already reported. The chase watchdog (TickChaseWatchdog,
            // 15 s on Navigation.ChaseSinceBestSec) is the escalation
            // path when Stuck persists too long.
            return;
        }

        // ── Trigger path ────────────────────────────────────────────────────
        float moved = currentPos.WorldDistanceXYTo(_activeStuckCheckPosW);
        if (moved >= ActiveStuckMinMovementWorld)
        {
            // Made progress — reset the trigger window.
            _activeStuckCheckPosW = currentPos;
            _activeStuckSinceUtc = now;
            return;
        }

        // Movement is below threshold. Has the stationary window elapsed?
        double elapsed = (now - _activeStuckSinceUtc).TotalSeconds;
        if (elapsed >= ActiveStuckThresholdSec)
        {
            _activeStuckReported = true;
            _activeStuckAnchorW = currentPos;  // resume anchor pinned here
            assistStatusProvider.CurrentStatus = BotStatus.Stuck;
            logger.LogWarning(
                $"[FFG] Stuck while navigating — moved only {moved:0.00}y in {elapsed:0.0}s " +
                $"at {currentPos}. Reporting Stuck so leader pauses; " +
                $"resume requires {ActiveStuckResumeMinMovementWorld:0.0}y of displacement " +
                $"or {ChaseWatchdogCantFollowSec:0.0}s of no chase progress (escalates to CantFollow).");
        }
    }

    // -----------------------------------------------------------------------
    // Idle stuck detection
    // -----------------------------------------------------------------------
    private void TickIdleStuckDetection()
    {
        if (bits.Combat())
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
                SetWaypointLoopGuarded(rewindTarget);
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
                SetWaypointLoopGuarded(refreshTarget);
                // Do NOT transition to Idle — remain in NavigatingToLeader.
                return;
            }

            // Refresh target is also co-located (refreshDist ≤ POP_DIST) — cannot
            // navigate any closer with the current map resolution. Accept Idle so the
            // assist does not spin indefinitely trying to reach an unreachable position.
            //
            // Fix 10 (parity with OnWayPointReached's co-located branch): set
            // _rendezvousConfirmed=true so UpdateIdle's line 723 dead-band check
            // takes the stay-put branch on the next tick instead of re-triggering
            // StartNavigatingToLeader. In log-40 the cycle ran through
            // OnWayPointReached (TryConsumeReachedWaypoint fires both events,
            // OnWayPointReached first, transitioning _navState→Idle so this
            // OnDestinationReached returns early at line 1733). Setting it here
            // too handles the SkipBlacklistedWaypoints path (Navigation line 786)
            // where wayPoints drops to 0 without going through
            // TryConsumeReachedWaypoint — OnDestinationReached fires alone.
            logger.LogInformation(
                $"[FFG] Destination reached in dead-band (leader={dist:0.0}y, target co-located {refreshDist:0.0}y) — entering Idle.");
            _rendezvousConfirmed = true;
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
        SetWaypointLoopGuarded(retryTarget);
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
            SetWaypointLoopGuarded(refreshTarget);
            // Stay in NavigatingToLeader.
        }
        else
        {
            // Target co-located — can't get closer; stop.
            //
            // Fix 10 (log-40, three 120s CantFollow timeout cycles at <952.07,297.85>
            // & later at <949.92,294.16>): when ComputeFollowTargetWorldPos returns
            // a target within POP_DIST of the bot — either because the entire
            // leader→assist segment is blacklisted (returns assist's own position)
            // OR because Fix 4 projected to a candidate near the assist that the
            // bot is already adjacent to — we accept Idle here, but
            // _rendezvousConfirmed stays false (line 615-617 only sets it when
            // dist < FollowingMaxYards=7y, and we're in the dead-band 7-14y).
            //
            // On the very next UpdateIdle tick, line 723 check fails:
            //   `Following && _rendezvousConfirmed` → status=Following but
            //   _rendezvousConfirmed=false → falls to else → "Dead-band — closing
            //   gap to confirm rendezvous" → StartNavigatingToLeader → same target
            //   returned → wp popped → OnWayPointReached → here again.
            //
            // The cycle runs at ~30ms per iteration. After 10 stationary
            // SetSingleWaypoint calls in ~300ms, Fix 5's SetWaypointLoopGuarded
            // escalates to CantFollow, which then triggers the leader's
            // ShouldLeaderPauseForAssist (any CantFollow → pause regardless of
            // distance), AssistRequestReturn, and the Fix 9 phantom-AssistReturn
            // loop. log-40 observed three 120s CantFollow cycles (12:57:41:809,
            // 12:59:42:132, 13:01:42:472) with the leader and assist completely
            // stuck for >4 minutes.
            //
            // Semantic justification: geometric impossibility of getting closer
            // (line entirely blacklisted, or projection lands near assist) IS
            // the rendezvous outcome for this geometry. The bot accepts dead-band
            // distance as "close enough"; setting _rendezvousConfirmed=true lets
            // UpdateIdle take the line 723 stay-put branch.
            //
            // Existing safety net at line 1252: GetNavigationTarget clears
            // _rendezvousConfirmed when leader.Status != Patrolling, so this
            // doesn't accidentally persist into combat/loot phases.
            // leaderJustResumedPatrol / leaderJustPublishedWaypoint checks
            // (lines 700 / 673) fire StartNavigatingToLeader on real leader
            // transitions regardless of _rendezvousConfirmed, so the assist
            // re-engages when needed.
            _rendezvousConfirmed = true;

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
        SetWaypointLoopGuarded(_navRewindAnchorW);
    }
}
