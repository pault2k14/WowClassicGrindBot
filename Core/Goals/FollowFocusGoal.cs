using Core.GOAP;
using Core.Goals;
using Microsoft.Extensions.Logging;
using SharedLib;
using System;
using System.Numerics;
using System.Threading;
using static System.MathF;

namespace Core.Goals;

public sealed class FollowFocusGoal : GoapGoal, IGoapEventListener
{
    public override float Cost => 19f;

    // Set by OnGoapEvent when GoapAgent broadcasts evadeRecovery=true.
    // While true, FFG skips the "assist focus combat" block so the assist
    // stays in follow mode rather than trying to re-engage near the evading mob.
    private bool _evadeRecoveryActive;

    private readonly ConfigurableInput input;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly Wait wait;
    private readonly ClassConfiguration classConfig;
    private readonly ILogger<FollowFocusGoal> logger;
    private readonly RestHandler restHandler;
    private readonly ChatReader chatReader;
    private readonly Navigation navigation;

    // --- Follow state ---
    private int focusTargetGuid;
    private Action<CancellationToken> FocusTargetInput;
    private followMessage lastMessageSent = followMessage.None;

    // Prevents pressing FollowTarget every single tick — gives the game time to
    // confirm AutoFollow before we try again.
    private const double FollowAttemptCooldownSec = 2.0;
    private DateTime _lastFollowAttemptUtc = DateTime.MinValue;

    private enum followMessage
    {
        None,
        ImFollowing,
        ImNotFollowing,
        ICantFollow
    }

    // --- Leader-navigation state machine ---
    private enum NavState
    {
        Idle,
        WaitingForPosition,
        NavigatingToLeader,
    }

    private NavState _navState = NavState.Idle;

    // True after SendAssistCantFollow fires — the assist has given up navigating to
    // the leader and asked the leader to come to them instead.
    // While true, UpdateIdle does NOT send position requests (N8) — the leader is
    // (or should be) on their way. Cleared when follow is successfully established.
    // Falls back to allowing position requests after WantsLeaderToReturnTimeoutSec
    // in case the leader never arrives (e.g. also stuck, or timed out returning).
    private bool _wantsLeaderToReturn;
    private DateTime _wantsLeaderToReturnSinceUtc;
    private const double WantsLeaderToReturnTimeoutSec = 60.0;

    // How long to wait for the leader to reply with their position.
    private const double WaitForPositionTimeoutSec = 15.0;

    // How long the assist will actively navigate toward the leader before
    // escalating to AssistCantFollow (the existing "too far away" flow).
    private const double NavigationTimeoutSec = 30.0;

    private DateTime _navStateEnteredUtc;

    // Cooldown so we don't spam the position request if something goes wrong.
    private const double RequestPositionCooldownSec = 35.0;
    private DateTime _lastPositionRequestUtc = DateTime.MinValue;

    public FollowFocusGoal(ConfigurableInput input,
        PlayerReader playerReader,
        AddonBits bits,
        Wait wait,
        ClassConfiguration classConfig,
        ILogger<FollowFocusGoal> logger,
        RestHandler restHandler,
        ChatReader chatReader,
        Navigation navigation
        )
        : base(nameof(FollowFocusGoal))
    {
        this.input = input;
        this.playerReader = playerReader;
        this.bits = bits;
        this.wait = wait;
        this.classConfig = classConfig;
        this.logger = logger;
        this.restHandler = restHandler;
        this.chatReader = chatReader;
        this.navigation = navigation;

        if (classConfig.UnitToFollow == "focus")
        {
            AddPrecondition(GoapKey.hasfocus, true);
        }

        AddPrecondition(GoapKey.assistshouldfollow, true);

        // Try adding these preconditions to let the assit loot
        AddPrecondition(GoapKey.shouldloot, false);
        AddPrecondition(GoapKey.shouldgather, false);
        AddPrecondition(GoapKey.consumecorpse, false);

        switch (classConfig.UnitToFollow)
        {
            case "focus":
                focusTargetGuid = playerReader.FocusGuid;
                FocusTargetInput = input.PressTargetFocus;
                break;
            case "party1":
                focusTargetGuid = playerReader.PartyMember1Guid;
                FocusTargetInput = input.PressTargetFocus;
                break;
            case "party2":
                focusTargetGuid = playerReader.PartyMember2Guid;
                FocusTargetInput = input.PressTargetFocusPartyMemberTwo;
                break;
            case "party3":
                focusTargetGuid = playerReader.PartyMember3Guid;
                FocusTargetInput = input.PressTargetFocusPartyMemberThree;
                break;
            case "party4":
                focusTargetGuid = playerReader.PartyMember4Guid;
                FocusTargetInput = input.PressTargetFocusPartyMemberFour;
                break;
            default:
                focusTargetGuid = playerReader.FocusGuid;
                FocusTargetInput = input.PressTargetFocus;
                break;
        }

        // Subscribe to destination-reached so we can attempt follow on arrival.
        navigation.OnDestinationReached += Navigation_OnDestinationReached;
    }

    // Called when the goal is disposed or the agent shuts down.
    // Unsubscribes the navigation event to prevent callbacks after teardown.
    private void Cleanup()
    {
        navigation.OnDestinationReached -= Navigation_OnDestinationReached;
    }

    public override void OnEnter()
    {
        while (restHandler.IsResting())
        {
            wait.Update(1000);
        }

        if (input.IsKeyDown(input.ForwardKey))
        {
            input.StopForward(true);
        }

        // If we were navigating and got preempted (e.g. entered combat), stop nav cleanly.
        if (_navState == NavState.NavigatingToLeader)
        {
            logger.LogInformation("[FFG] OnEnter: was NavigatingToLeader, stopping navigation.");
            navigation.Stop();
            _navState = NavState.Idle;
        }
    }

    public override void OnExit()
    {
        if (playerReader.TargetGuid == focusTargetGuid)
        {
            input.PressClearTarget();
            wait.Update();
        }

        input.StepBackwards();
        input.PressAssistIsNotFollowing();
        lastMessageSent = followMessage.ImNotFollowing;
        wait.Update();

        // Stop any in-progress navigation so it doesn't continue after we leave.
        if (_navState == NavState.NavigatingToLeader)
        {
            logger.LogInformation("[FFG] OnExit: stopping navigation.");
            navigation.Stop();
        }

        _navState = NavState.Idle;
        // Clear the position flag in case it arrived while we were exiting.
        chatReader.LeaderPositionReceived = false;
    }

    public override void Update()
    {
        if (bits.Drowning())
        {
            input.PressJump();
        }

        // --- Assist-side evade blacklist handling ---
        // If the leader has broadcast "blacklist target: {guid}", the assist must
        // immediately stop attacking, ignore that target, and clear it. This ensures
        // both bots disengage from evading mobs simultaneously.
        if (chatReader.LeaderBlacklistTarget)
        {
            int blacklistGuid = chatReader.LeaderBlacklistTargetId;
            chatReader.LeaderBlacklistTarget = false;
            chatReader.LeaderBlacklistTargetId = 0;

            if (blacklistGuid != 0)
            {
                logger.LogInformation($"[FFG] Leader blacklisted target guid={blacklistGuid} — stopping attack, ignoring and starting evade recovery.");
                input.PressStopAttack();
                wait.Update();
                playerReader.IgnoreTarget(blacklistGuid);
                input.PressClearTarget();
                wait.Update();

                // Fire EvadeBlacklistEvent so the assist's own GoapAgent starts its
                // evadeRecovery timer. Without this, _evadeRecoveryActive stays false
                // and the Focus_Combat block below immediately re-engages the mob.
                SendGoapEvent(new EvadeBlacklistEvent(blacklistGuid));

                // Set AssistRequestReturn=true immediately on the assist side so
                // FollowFocusGoal is selectable right now — no ~1.5s wait for the
                // chat message to echo back. The N5 press below delivers real position
                // coordinates to the leader; the local flag just bridges the gap.
                chatReader.AssistRequestReturn = true;

                // Press N5 (AssistCantFollow) — sends real position coordinates to the
                // leader, setting AssistRequestReturn=true on the leader side with
                // AssistXPos/AssistYPos populated for PauseForAssistNavigation.
                input.PressAssistCantFollow();
            }
            else
            {
                // Ghost combat escape (guid=0): same stop-wait-coordinate flow as real
                // evade, just without IgnoreTarget. Assist fires EvadeBlacklistEvent(0)
                // locally, sets AssistRequestReturn and presses N5 so the leader gets
                // real coordinates and navigates here (or assist navigates to leader via
                // N8/N9). Once following, both continue the route.
                logger.LogInformation("[FFG] Leader ghost combat escape (guid=0) — starting evade recovery, pressing N5.");
                SendGoapEvent(new EvadeBlacklistEvent(0));
                chatReader.AssistRequestReturn = true;
                input.PressAssistCantFollow();
            }
        }

        // If focus is in combat, try to assist instead of following.
        // Skip during evade recovery — the assist must stay in follow mode
        // and not try to re-engage while both bots are moving away from the evading mob.
        if (!_evadeRecoveryActive && bits.Focus_Combat() && bits.FocusTarget())
        {
            wait.Update();
            input.PressTargetFocus();
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

        // --- Drive the leader-navigation state machine ---
        switch (_navState)
        {
            case NavState.Idle:
                UpdateIdle();
                break;

            case NavState.WaitingForPosition:
                UpdateWaitingForPosition();
                break;

            case NavState.NavigatingToLeader:
                UpdateNavigatingToLeader();
                break;
        }
    }

    // -------------------------------------------------------------------------
    // State: Idle
    // Normal follow behaviour. If the leader is too far to follow, request
    // their position so we can navigate to them.
    // -------------------------------------------------------------------------
    public void OnGoapEvent(GoapEventArgs e)
    {
        if (e is GoapStateEvent s && s.Key == GoapKey.evadeRecovery)
        {
            _evadeRecoveryActive = s.Value;
        }
    }

    private void UpdateIdle()
    {
        // If we've already sent "i'm following" and the leader is still in
        // inspect range, we're following fine — do nothing.
        // AutoFollow() drops while the character is running to catch up, so
        // we cannot use it as an ongoing "still following" signal.
        if (lastMessageSent == followMessage.ImFollowing &&
            playerReader.SpellInRange.Focus_Inspect)
        {
            return;
        }

        // Target the unit we want to follow.
        if (playerReader.TargetGuid != focusTargetGuid)
        {
            FocusTargetInput(default);
            wait.Update();
        }

        // Leader is in range — press follow once and announce it.
        // Focus_Inspect range (~30 yards) is larger than AutoFollow range, so we
        // must verify AutoFollow actually established before sending "i'm following".
        // If we send it prematurely the leader resumes patrol and the assist
        // immediately loses them again.
        if (playerReader.TargetGuid == focusTargetGuid &&
            playerReader.SpellInRange.Focus_Inspect &&
            (DateTime.UtcNow - _lastFollowAttemptUtc).TotalSeconds >= FollowAttemptCooldownSec)
        {
            _lastFollowAttemptUtc = DateTime.UtcNow;

            // Stop movement before pressing follow — if the character is still
            // running (or the leader is walking past), AutoFollow may not establish.
            // TryFollowIfInRange does the same thing for consistency.
            input.StopForward(true);
            wait.Update();

            input.PressFollowTarget();
            wait.Update();
            wait.Update();
            wait.Update();

            if (bits.AutoFollow())
            {
                input.StopForward(true);
                chatReader.AssistRequestReturn = false;
                _wantsLeaderToReturn = false;
                SendImFollowing();
                return;
            }

            // AutoFollow didn't establish — leader is in inspect range but not
            // close enough to follow. Stay in UpdateIdle so the out-of-range path
            // below can fire and request their position if needed.
            logger.LogInformation("[FFG] PressFollowTarget in range but AutoFollow not established — staying in Idle.");
        }

        // Leader is out of inspect range — request their position to navigate back.
        if (!playerReader.SpellInRange.Focus_Inspect)
        {
            // If the assist already gave up navigating and asked the leader to come back
            // (_wantsLeaderToReturn=true), do NOT send position requests (N8) again —
            // the leader should be on their way and we must stay put and wait.
            // Allow a fallback after WantsLeaderToReturnTimeoutSec in case the leader
            // never arrives (stuck, timed out, etc.).
            if (_wantsLeaderToReturn)
            {
                // If the leader position reply arrived just after our WaitForPosition
                // timeout fired (a common race at high latency), LeaderPositionReceived
                // will be true even though we've already sent N5 and entered
                // _wantsLeaderToReturn. Consume it and navigate to the leader rather
                // than waiting up to 60s for them to walk to us.
                if (chatReader.LeaderPositionReceived)
                {
                    logger.LogInformation("[FFG] Stale leader position received after timeout — consuming and navigating instead of waiting for leader to return.");
                    _wantsLeaderToReturn = false;
                    EnterState(NavState.WaitingForPosition);
                    return;
                }

                double waitedSec = (DateTime.UtcNow - _wantsLeaderToReturnSinceUtc).TotalSeconds;
                if (waitedSec < WantsLeaderToReturnTimeoutSec)
                {
                    logger.LogInformation(
                        $"[FFG] Waiting for leader to return ({waitedSec:0.0}s / {WantsLeaderToReturnTimeoutSec}s) — suppressing position request.");
                    wait.Update();
                    return;
                }

                // Timeout: leader hasn't arrived. Reset and allow position request as fallback.
                logger.LogWarning(
                    $"[FFG] Leader did not return after {waitedSec:0.0}s — resetting to allow position request.");
                _wantsLeaderToReturn = false;
            }

            double secSinceLastRequest =
                (DateTime.UtcNow - _lastPositionRequestUtc).TotalSeconds;

            if (secSinceLastRequest >= RequestPositionCooldownSec)
            {
                logger.LogInformation("[FFG] Leader out of range — requesting position.");
                _lastPositionRequestUtc = DateTime.UtcNow;
                chatReader.LeaderPositionReceived = false;

                input.StepBackwards();
                wait.Update();
                input.PressAssistRequestLeaderPosition();
                lastMessageSent = followMessage.ICantFollow;

                EnterState(NavState.WaitingForPosition);
            }
            else
            {
                logger.LogInformation(
                    $"[FFG] Leader out of range — position request on cooldown " +
                    $"({secSinceLastRequest:0.0}s / {RequestPositionCooldownSec}s).");
            }

            wait.Update();
        }

        wait.Update();
    }

    // -------------------------------------------------------------------------
    // State: WaitingForPosition
    // We sent the position request. Wait up to 5 seconds for the leader to reply.
    // -------------------------------------------------------------------------
    private void UpdateWaitingForPosition()
    {
        if (chatReader.LeaderPositionReceived)
        {
            float lx = chatReader.LeaderXPos;
            float ly = chatReader.LeaderYPos;
            chatReader.LeaderPositionReceived = false;

            logger.LogInformation($"[FFG] Leader position received: X={lx} Y={ly}. Starting navigation.");

            Vector3 leaderMapPos = new Vector3(lx, ly, playerReader.MapPos.Z);
            Vector3 myMapPos = playerReader.MapPos;

            // Build a dense chain of intermediate waypoints between our current
            // position and the leader's position. Navigation is much more robust
            // with many short segments (as in FRG's patrol route) than a single
            // long waypoint — it gives the stuck detector, refill logic, and
            // escape machinery many intermediate progress points to work with.
            const float WaypointSpacingMap = 2.0f; // ~2 map units between each waypoint

            float dx = lx - myMapPos.X;
            float dy = ly - myMapPos.Y;
            float dist = Sqrt(dx * dx + dy * dy);

            int steps = Math.Max(1, (int)(dist / WaypointSpacingMap));
            int totalPoints = steps + 1; // intermediate steps + leader endpoint

            // Stack-allocate if small enough, heap-allocate for longer routes.
            Vector3[] waypoints = new Vector3[totalPoints];
            for (int i = 0; i < steps; i++)
            {
                float t = (float)(i + 1) / (steps + 1);
                waypoints[i] = new Vector3(
                    myMapPos.X + dx * t,
                    myMapPos.Y + dy * t,
                    myMapPos.Z
                );
            }
            waypoints[steps] = leaderMapPos; // final point is leader's exact position

            logger.LogInformation(
                $"[FFG] Navigating to leader with {totalPoints} waypoints " +
                $"(dist={dist:0.00} map units, spacing={WaypointSpacingMap}).");

            navigation.SetWayPoints(waypoints.AsSpan());
            navigation.Resume();

            EnterState(NavState.NavigatingToLeader);
            return;
        }

        double elapsed = (DateTime.UtcNow - _navStateEnteredUtc).TotalSeconds;
        if (elapsed >= WaitForPositionTimeoutSec)
        {
            logger.LogWarning(
                $"[FFG] No position reply after {elapsed:0.0}s — " +
                "escalating to AssistCantFollow.");
            SendAssistCantFollow();
            EnterState(NavState.Idle);
        }

        wait.Update();
    }

    // -------------------------------------------------------------------------
    // State: NavigatingToLeader
    // Drive navigation each tick. Try to re-establish follow whenever we can.
    // -------------------------------------------------------------------------
    private void UpdateNavigatingToLeader()
    {
        double elapsed = (DateTime.UtcNow - _navStateEnteredUtc).TotalSeconds;
        if (elapsed >= NavigationTimeoutSec)
        {
            logger.LogWarning(
                $"[FFG] Navigation timeout after {elapsed:0.0}s — " +
                "escalating to AssistCantFollow.");
            navigation.Stop();
            SendAssistCantFollow();
            EnterState(NavState.Idle);
            return;
        }

        // Drive navigation this tick.
        navigation.Update(CancellationToken.None);

        // Opportunistically try to follow if we are now in range.
        if (TryFollowIfInRange())
            return;

        wait.Update();
    }

    // -------------------------------------------------------------------------
    // Navigation event: fires when we reach the leader's last known position.
    // Make one final follow attempt; if still out of range, escalate.
    // -------------------------------------------------------------------------
    private void Navigation_OnDestinationReached()
    {
        if (_navState != NavState.NavigatingToLeader)
            return;

        logger.LogInformation("[FFG] Reached leader's last known position. Attempting follow.");

        // Give the game a tick to settle before trying.
        wait.Update();

        if (TryFollowIfInRange())
            return;

        // Still can't follow — escalate.
        logger.LogWarning("[FFG] Reached leader position but still out of follow range. Escalating to AssistCantFollow.");
        SendAssistCantFollow();
        EnterState(NavState.Idle);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Targets the leader and attempts to follow. Returns true if AutoFollow
    /// was established (success), false otherwise.
    /// </summary>
    private bool TryFollowIfInRange()
    {
        if (playerReader.TargetGuid != focusTargetGuid)
        {
            FocusTargetInput(default);
            wait.Update();
        }

        if (playerReader.TargetGuid == focusTargetGuid &&
            playerReader.SpellInRange.Focus_Inspect &&
            (DateTime.UtcNow - _lastFollowAttemptUtc).TotalSeconds >= FollowAttemptCooldownSec)
        {
            _lastFollowAttemptUtc = DateTime.UtcNow;

            // Stop moving before pressing follow so the character doesn't
            // overshoot past the leader due to momentum from navigation.
            navigation.Stop();
            input.StopForward(true);
            wait.Update();

            input.PressFollowTarget();
            wait.Update();
            wait.Update();
            wait.Update();

            if (bits.AutoFollow())
            {
                logger.LogInformation("[FFG] AutoFollow established while navigating to leader. Success.");
                input.StopForward(true);
                SendImFollowing();
                chatReader.AssistRequestReturn = false;
                _wantsLeaderToReturn = false;
                wait.Update();

                EnterState(NavState.Idle);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sends "i'm following" to party chat and records the time.
    /// All callers must go through here to respect the send cooldown.
    /// </summary>
    private void SendImFollowing()
    {
        input.PressAssistIsFollowing();
        lastMessageSent = followMessage.ImFollowing;
        logger.LogInformation("[FFG] Sent AssistIsFollowing.");
        wait.Update();
    }

    /// <summary>
    /// Sends the "i tried following but you are too far away" message,
    /// which triggers AssistRequestReturn on the leader side.
    /// </summary>
    private void SendAssistCantFollow()
    {
        input.StepBackwards();
        wait.Update();
        input.PressAssistCantFollow();
        lastMessageSent = followMessage.ICantFollow;
        logger.LogInformation("[FFG] Sent AssistCantFollow to leader.");

        // Switch to 'wants leader to return' mode. UpdateIdle will not send further
        // position requests (N8) while this is true — the leader should now be moving
        // toward us and we should wait. Cleared when follow is established.
        _wantsLeaderToReturn = true;
        _wantsLeaderToReturnSinceUtc = DateTime.UtcNow;

        // Reset the position request cooldown from NOW, not from the original request.
        // Without this, if NavigatingToLeader took ~30s and RequestPositionCooldownSec
        // is 35s, the assist would fire another position request only 5s after sending N5 —
        // before the leader has even processed the N5 and started returning.
        // That creates a crossing-paths loop: leader starts returning, assist immediately
        // asks for position again, leader cancels return, both confused.
        _lastPositionRequestUtc = DateTime.UtcNow;
    }

    private void EnterState(NavState newState)
    {
        logger.LogInformation($"[FFG] NavState: {_navState} → {newState}");
        _navState = newState;
        _navStateEnteredUtc = DateTime.UtcNow;
    }
}
