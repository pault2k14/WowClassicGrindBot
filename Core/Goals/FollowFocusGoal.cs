using Core.GOAP;
using Core.Goals;
using Microsoft.Extensions.Logging;
using SharedLib;
using System;
using System.Numerics;
using System.Threading;

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

    // Maximum ACTIVE navigation time before escalating to AssistCantFollow.
    // Uses elapsed-time-only-when-moving (same pattern as FRG's TickAssistReturnTimeout)
    // so the timeout doesn't burn down while the assist is in combat or evade recovery.
    private const double NavigationActiveTimeoutSec = 30.0;

    private DateTime _navStateEnteredUtc;

    // --- NavigatingToLeader: rewind/retry and active-time timeout ---
    // The leader's map position we're currently navigating to (for rewind retry).
    private Vector3 _navLeaderTargetMapPos;
    // True while rewinding to the last safe anchor before retrying the leader target.
    private bool _navRewindActive;
    // The world-space safe anchor position to rewind to on path failure.
    private Vector3 _navRewindAnchorW;
    // 0 = first attempt, 1 = after rewind retry. Abort on second failure.
    private int _navAttempt;
    // Active-time elapsed since entering NavigatingToLeader.
    // Only increments when not in combat and not in evade recovery.
    private TimeSpan _navActiveElapsed;
    private DateTime _navLastTickUtc;
    private bool _navTimerInit;

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

        // Subscribe to navigation events.
        navigation.OnDestinationReached += Navigation_OnDestinationReached;
        navigation.OnWayPointReached += Navigation_OnWayPointReached;
        navigation.OnPathFailed += Navigation_OnPathFailed;
    }

    // Called when the goal is disposed or the agent shuts down.
    // Unsubscribes the navigation event to prevent callbacks after teardown.
    private void Cleanup()
    {
        navigation.OnDestinationReached -= Navigation_OnDestinationReached;
        navigation.OnWayPointReached -= Navigation_OnWayPointReached;
        navigation.OnPathFailed -= Navigation_OnPathFailed;
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
            ResetNavRetryState();
            _navState = NavState.Idle;
        }
        else if (_navState == NavState.WaitingForPosition)
        {
            // Re-entering after a plan cycle while waiting for a position reply.
            if (chatReader.LeaderPositionReceived)
            {
                // Position arrived during the cycle — leave state as-is so
                // UpdateWaitingForPosition consumes it on the very first tick.
                logger.LogInformation("[FFG] OnEnter: leader position received during plan cycle — will consume immediately.");
            }
            else
            {
                double elapsed = (DateTime.UtcNow - _navStateEnteredUtc).TotalSeconds;
                if (elapsed >= WaitForPositionTimeoutSec)
                {
                    // Timed out while we were in another goal — escalate now.
                    logger.LogWarning($"[FFG] OnEnter: WaitingForPosition timed out while goal was inactive ({elapsed:0.0}s) — escalating to AssistCantFollow.");
                    SendAssistCantFollow();
                    _navState = NavState.Idle;
                }
                else
                {
                    logger.LogInformation($"[FFG] OnEnter: resuming WaitingForPosition ({elapsed:0.0}s / {WaitForPositionTimeoutSec}s elapsed).");
                }
            }
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

        // If we are waiting for a position reply, preserve both the nav state and
        // the LeaderPositionReceived flag across the plan cycle. The leader's reply
        // can arrive at exactly the moment the GOAP planner picks a different goal
        // and OnExit fires — clearing these here discards valid data and leaves the
        // assist stuck waiting out the full 35s cooldown before it can request again.
        // For any other state, reset to Idle and clear the flag as normal.
        if (_navState == NavState.WaitingForPosition)
        {
            logger.LogInformation("[FFG] OnExit: preserving WaitingForPosition state and position data across plan cycle.");
            // Leave _navState and LeaderPositionReceived intact.
        }
        else
        {
            _navState = NavState.Idle;
            chatReader.LeaderPositionReceived = false;
        }
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

            logger.LogInformation($"[FFG] OnGoapEvent LeaderBlacklistTarget received guid={blacklistGuid}");

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
        // Safety net: if LeaderPositionReceived is true but _navState is Idle, a position
        // reply arrived during a plan cycle after _navState was already reset (e.g. from a
        // NavigatingToLeader → Idle transition that raced with the reply). Consume it
        // immediately by entering WaitingForPosition rather than ignoring it.
        if (chatReader.LeaderPositionReceived)
        {
            logger.LogInformation("[FFG] UpdateIdle: leader position received while in Idle — entering WaitingForPosition to consume.");
            EnterState(NavState.WaitingForPosition);
            return;
        }

        // If we've already sent "i'm following" and the leader is still in
        // inspect range AND AutoFollow is active, we're following fine — do nothing.
        // If AutoFollow has dropped (terrain bump, manual keypress, etc.) we must
        // fall through immediately so the follow attempt below re-establishes it,
        // rather than waiting up to ~30 yards for the leader to leave inspect range.
        if (lastMessageSent == followMessage.ImFollowing &&
            playerReader.SpellInRange.Focus_Inspect &&
            bits.AutoFollow())
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

            Vector3 leaderMapPos = new Vector3(lx, ly, playerReader.MapPos.Z);
            float dx = lx - playerReader.MapPos.X;
            float dy = ly - playerReader.MapPos.Y;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);

            logger.LogInformation($"[FFG] Leader position received: X={lx} Y={ly} dist={dist:0.00} map units. Starting navigation.");

            // Store target for rewind retry, reset retry state and active-time timer.
            _navLeaderTargetMapPos = leaderMapPos;
            _navAttempt = 0;
            _navRewindActive = false;
            _navRewindAnchorW = default;
            _navTimerInit = false;
            _navActiveElapsed = TimeSpan.Zero;

            // Pass only the destination and let the pather plan the full route.
            // Previously this generated linear intermediate waypoints which caused
            // the assist to get stuck on terrain — the pather would navigate around
            // one obstacle but the next pre-sliced segment still pointed straight
            // through the next piece of terrain. SetSingleWaypoint lets the pather
            // return a full terrain-aware path with proper intermediate points,
            // giving the stuck detector and escape machinery progress points to work with.
            navigation.SetSingleWaypoint(leaderMapPos);

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
        // Active-time timeout — only counts time when not in combat / evade recovery.
        TickNavActiveTimeout();
        if (_navState != NavState.NavigatingToLeader)
            return;

        // Drive navigation this tick.
        navigation.Update(CancellationToken.None);

        // Opportunistically try to follow if we are now in range.
        if (TryFollowIfInRange())
            return;

        wait.Update();
    }

    // Active-time navigation timeout.
    // Only accumulates elapsed time when the assist is actively moving toward the leader
    // (not in combat, not in evade recovery) so a stuck or paused assist doesn't burn
    // through its budget while standing still.
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
            _navActiveElapsed += (now - _navLastTickUtc);

        _navLastTickUtc = now;

        if (_navActiveElapsed.TotalSeconds >= NavigationActiveTimeoutSec)
        {
            logger.LogWarning(
                $"[FFG] Navigation active timeout after {_navActiveElapsed.TotalSeconds:0.0}s — " +
                "escalating to AssistCantFollow.");
            navigation.Stop();
            ResetNavRetryState();
            SendAssistCantFollow();
            EnterState(NavState.Idle);
        }
    }

    private void ResetNavRetryState()
    {
        _navLeaderTargetMapPos = default;
        _navRewindActive = false;
        _navRewindAnchorW = default;
        _navAttempt = 0;
        _navTimerInit = false;
        _navActiveElapsed = TimeSpan.Zero;
    }

    // -------------------------------------------------------------------------
    // Navigation event: fires when we reach the current navigation destination.
    // If rewinding, retry the leader target. Otherwise make one final follow
    // attempt; if still out of range, escalate to AssistCantFollow.
    // -------------------------------------------------------------------------
    private void Navigation_OnDestinationReached()
    {
        if (_navState != NavState.NavigatingToLeader)
            return;

        if (_navRewindActive)
        {
            // Rewind destination reached — retry the leader target.
            _navRewindActive = false;
            logger.LogInformation($"[FFG] Rewind destination reached. Retrying leader target {_navLeaderTargetMapPos}.");
            navigation.SetSingleWaypoint(_navLeaderTargetMapPos);
            return;
        }

        logger.LogInformation("[FFG] Reached leader's last known position. Attempting follow.");

        wait.Update();

        if (TryFollowIfInRange())
            return;

        logger.LogWarning("[FFG] Reached leader position but still out of follow range. Escalating to AssistCantFollow.");
        ResetNavRetryState();
        SendAssistCantFollow();
        EnterState(NavState.Idle);
    }

    // -------------------------------------------------------------------------
    // Navigation event: fires when an intermediate waypoint is reached.
    // Try opportunistically to follow — the leader may now be in range.
    // -------------------------------------------------------------------------
    private void Navigation_OnWayPointReached()
    {
        if (_navState != NavState.NavigatingToLeader)
            return;

        TryFollowIfInRange();
    }

    // -------------------------------------------------------------------------
    // Navigation event: fires when the pather fails to find a path.
    // On first failure, rewind to last safe anchor then retry the leader target.
    // On second failure, escalate to AssistCantFollow.
    // -------------------------------------------------------------------------
    private void Navigation_OnPathFailed(Vector3 startW, Vector3 endW)
    {
        if (_navState != NavState.NavigatingToLeader)
            return;

        // FFG only ever has one navigation target active at a time so we don't
        // need to verify endW against our target — any path failure while in
        // NavigatingToLeader is for the leader destination or rewind anchor.

        if (_navAttempt >= 1)
        {
            logger.LogWarning("[FFG] Path to leader failed after rewind retry — escalating to AssistCantFollow.");
            ResetNavRetryState();
            SendAssistCantFollow();
            EnterState(NavState.Idle);
            return;
        }

        if (!navigation.HasLastSafeAnchor)
        {
            logger.LogWarning("[FFG] Path to leader failed — no safe anchor available. Escalating to AssistCantFollow.");
            ResetNavRetryState();
            SendAssistCantFollow();
            EnterState(NavState.Idle);
            return;
        }

        _navAttempt = 1;
        _navRewindActive = true;
        _navRewindAnchorW = navigation.LastSafeAnchorW;

        logger.LogWarning(
            $"[FFG] Path to leader failed. Rewinding to anchor={_navRewindAnchorW} then retrying target={_navLeaderTargetMapPos}.");

        navigation.SetSingleWaypoint(_navRewindAnchorW);
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
