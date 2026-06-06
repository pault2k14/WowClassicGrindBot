using Core.AreaBlacklist;
using Core.GOAP;
using Core.Party;

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
    private readonly AssistStateStore assistStateStore;
    private readonly LeaderNavigationProvider leaderNavProvider;
    private volatile bool _disposing;

    private int _pauseNavRequested;
    private volatile bool _pausedByLocalLogic;

    // API-based distance gate: leader pauses when assist is too far, stuck, or cant follow.
    private bool _pausedByAssistDistance;

    // ── Fix FA (run-152 standoff) — party-combat auto-resume flag ──
    //
    // Set in OnGoapEvent's partyincombat=true branch when we Abort() in
    // response to a party-combat broadcast. Cleared either by FRG.Update's
    // auto-resume gate (when bits.Combat()=false AND, on PartyLeader, no
    // fresh polled assist reports InCombat) or by an explicit Resume()
    // call (e.g. via OnEnter on plan transition back to FRG).
    //
    // Why this flag exists: FRG.Abort pauses pathing and clears the
    // published waypoint, but FRG remains the current goal — the planner
    // expects Combat to be selected next, then to transition back to FRG
    // (which would call OnEnter → Resume). Run-152's failure mode is that
    // Combat's preconditions (partyleadercombat=true,
    // allPartyTargetsIsIgnored=false) flake at the planner tick immediately
    // after FRG.Abort: bits.FocusTarget flickers false right after the
    // partyincombat broadcast, partyleadercombat collapses to false, Combat
    // plan stays blocked, the planner re-selects FRG (no transition), no
    // OnEnter, no Resume — and the bot sits paused forever (16:5 minutes
    // of "Random jump" with no movement in the 13:31:55 → 13:35:59 leader
    // log window).
    //
    // The auto-resume gate doesn't rely on a partyincombat=false broadcast
    // because that broadcast may never fire if bits.Focus_Combat sticks at
    // a stale TRUE value (assist was 424y away in run-152, far beyond the
    // WoW client's focus-refresh range). Fix EY's polled
    // AssistStateStore.AnyAssistInCombat is the authoritative source for
    // "is the assist in combat" that we check directly each Update tick.
    private bool _pausedByPartyCombat;

    private Vector3[] mapRoute
    {
        get => pathSettings.Path;
        set => pathSettings.Path = value;
    }

    private DateTime onEnterTime;

    // Fix CY one-shot log throttle — set true after first FIX-FIRE CY log
    // within a single grace window, reset when grace elapses.
    private bool _cyGraceLogged;
    private DateTime _lastRefillWaypointsUtc = DateTime.MinValue;
    private Vector3 _lastRefillTopMap = default;
    private int _lastRefillWaypointCount = -1;

    private const int RefillWaypointsDuplicateCooldownMs = 750;

    // Assist-return rewind logic
    private bool _assistReturnActive;
    private Vector3 _assistReturnTargetW;
    private bool _assistWaitingForFollowing;

    public bool WaitingForAssist =>
        _assistReturnActive || _assistWaitingForFollowing;

    // ── Fix FN (run-162) — caster-in-BL retreat backtrack state ──
    //
    // State machine that drives the leader back through prior route
    // waypoints when an IsIgnored caster inside a BL rect is hitting us
    // from outside our reachable range. At each backtrack waypoint, we
    // face the mob (Interact + StepBackwards to cancel auto-run), wait
    // ~700ms for TargetMapPos to refresh, then re-check IsTargetLikelyIn
    // BlacklistRect. If OUT-OF-RECT, signal Combat to engage (via
    // navigation.BacktrackEngageGuid). If still IN-RECT, advance to
    // the next prior waypoint. See UpdateBacktrackStateMachine for the
    // full state transitions.
    //
    // State persists across Combat-plan preemption (Combat steals briefly
    // when a non-IsIgnored mob aggros during travel, then returns to FRG;
    // backtrack resumes from the preserved phase). State clears on Abort()
    // or when self-defense conditions no longer hold (combat drop, target
    // lost, target killed, or route start reached).
    private enum BacktrackPhase { None, Navigating, Evaluating, EngageWindow }
    private BacktrackPhase _btPhase = BacktrackPhase.None;
    private int _btStartRouteIdx = -1;
    private int _btStepsBack;
    private int _btTargetGuid;
    private DateTime _btArrivalUtc = DateTime.MinValue;
    private Vector3 _btCurrentWaypointW;
    private const int BtEvalDelayMs = 700;
    private const float BtArrivalYards = 3.5f;

    // ── Fix FT (run-165) — BL-escape backtrack sub-mode ──
    //
    // Distinguishes Fix FN's existing combat-driven backtrack (target-tracking,
    // EngageWindow re-engage on out-of-rect verdict) from a new no-target
    // sub-mode entered when the leader finds itself inside a BL rect with no
    // fightable target slot (e.g., Blacklist Target plan cleared the target
    // after AreaBlacklistMob on attack). In BL-escape mode the state machine
    // walks the prior route waypoints in reverse until the bot itself is back
    // outside every static rect, then exits to normal patrol. No EngageWindow
    // phase fires in this mode — there's no specific mob to track. If a non-
    // BL aggro hits us during the escape, normal Combat plan preempts FRG;
    // when Combat ends, FRG resumes and re-enters backtrack via this same
    // trigger if the bot is still in a BL rect.
    private bool _btIsBlEscapeMode;

    private bool _assistRewindActive;
    private Vector3 _assistRewindAnchorW;
    private int _assistAttempt;

    private const double ASSIST_RETURN_TIMEOUT_ACTIVE_SEC = 25.0;
    private TimeSpan _assistReturnActiveElapsed;
    private DateTime _assistReturnLastTickUtc;
    private bool _assistReturnTimerInit;

    private int _pathTraversalDirection;

    /// <summary>
    /// Set by both AssistReturn completion paths before calling Resume().
    /// Consumed (cleared) inside the ThereAndBack block of RefillWaypoints(false).
    /// Prevents the endpoint-forcing logic from reversing direction when the leader
    /// was placed at a route endpoint by AssistReturn rather than by completing a
    /// natural patrol leg — the leader should continue in the original direction,
    /// not treat the assist's CantFollow position as a ThereAndBack reversal point.
    /// </summary>
    private bool _suppressDirectionEndpointForcing;

    private int _suppressedBlacklistedGuid;
    private DateTime _suppressedBlacklistedUntilUtc;
    private DateTime _suppressTargetFinderUntilUtc = DateTime.MinValue;

    // Distance thresholds matching FollowFocusGoal constants.
    private const float LeaderPauseYards   = FollowFocusGoal.LeaderPauseYards;   // 20y
    private const float LeaderResumeYards  = FollowFocusGoal.LeaderResumeYards;  // 15y

    /// <summary>
    /// When FRG resumes with existing patrol waypoints and the assist is further than
    /// <see cref="FollowFocusGoal.FollowingMaxYards"/> (7y), the leader briefly pauses
    /// here before starting to move. This gives the assist — which our FFG dead-band fix
    /// ensures actively navigates toward the leader whenever rendezvous is unconfirmed —
    /// enough time to close to &lt;7y and confirm rendezvous before the leader pulls away.
    /// Without this pause, both bots move at the same speed and the gap never closes.
    /// </summary>
    private bool _syncPauseActive;
    private DateTime _syncPauseStartUtc;

    // Fix BO (log-97 19:31:14:411 → 19:33:38, FRG sync-pauses completed in 0-100 ms during
    // catch-up): distance-aware completion. The old condition (assistNavigating || assistFollowing
    // || timeout) exits immediately because the assist is in NavigatingToLeader almost continuously
    // during catch-up — proximity was ignored. Companion to Fix BN on the ATG side.
    // Timeout raised 1.0s → 2.0s (briefer than BN's 3.0s — FRG fires on every patrol resume,
    // a long pause here would feel sluggish; the proximity gate handles the common case).
    // See HANDOFF Fix BO for full evidence.
    private const double SyncPauseTimeoutSec = 2.0;
    private const float FrgSyncReadyYards = 14.0f;

    // ── Fix CY (log-122 evidence: 16:00:11:635 and 16:00:39:003) ──
    //
    // Two leader-side ATG→FRG→ATG planner micro-flickers (32ms and 31ms FRG
    // duration respectively) caused the leader's target to switch from the
    // mob it was approaching to a different (further) mob. Mechanism:
    //
    //   1. ATG running on target A
    //   2. Brief precondition flicker → planner picks FRG for one tick (32ms)
    //   3. FRG.OnEnter → Resume() → sync-pause check: timer carried from
    //      previous Resume long ago, so "Sync-pause complete" fires IMMEDIATELY
    //      (BO log shows elapsed=01s or 41s on re-entry — already past 2s
    //      timeout from previous activation)
    //   4. Sync-pause-complete path calls TryEnableSideTargetFinding() →
    //      sideActivityManualReset.Set()
    //   5. Thread_LookingForTarget's Wait returns within ~1ms; calls
    //      targetFinder.Search() which presses Tab
    //   6. Tab cycles target to a different nearby hostile (target B)
    //   7. Plan flicks back to ATG, but ATG.OnEnter now uses the NEW target B
    //   8. ATG warns "Stick to initial target!" and tries G (PressLastTarget)
    //      to restore, but the OnEnter/OnExit reset initialTargetGuid before
    //      the restore — leader proceeds to engage B instead of A
    //
    // Verified: at both 16:00:11:666 and 16:00:39:035, the post-flicker ATG
    // OnEnter shows a different target GUID (1018241 vs prior 1018283;
    // 1022541 vs prior 1022416), with Tab pressed within 50ms after the
    // plan change and "Found target!" logged from FollowRouteGoal even
    // though FRG is no longer active — confirming the side thread was
    // mid-Search across the plan boundary.
    //
    // CY fix: add a brief grace check inside Thread_LookingForTarget after
    // the manual-reset Wait, before targetFinder.Search. If onEnterTime
    // is within FrgSideThreadOnEnterGraceMs (500ms — 15× margin over
    // observed 32ms flickers), skip the Search and loop back to Wait. Brief
    // plan-flicker FRG runs (sub-100ms typical, sub-500ms safe margin) won't
    // trigger Tab presses; the side thread re-enables itself on the next
    // legitimate FRG.OnEnter (or stays paused if FRG.OnExit fires first,
    // which cancels the thread anyway).
    //
    // Legitimate FRG behavior unaffected: post-combat patrol FRG runs are
    // multi-second windows, sync-pause itself is 2s — both well past the
    // 500ms grace.
    private const double FrgSideThreadOnEnterGraceMs = 500.0;

    // Fix (run-143): when the side-thread search Tab-acquires a NO-ENGAGE mob
    // (a mob in an operator-defined route blacklist rect) while patrolling past
    // the rect, briefly disable the finder so the patrol can advance out of the
    // rect's aggro band instead of re-Tab-ing the same in-rect mobs every tick.
    // Run-143 21:05:34-21:06:21: two in-rect Battleguards (1472740/1472838) drove
    // 15 Blacklist-Target<->Follow plan flips in 47s with only 6 waypoints of
    // progress, because each Tab re-acquisition reset patrol nav. Tunable.
    private const int FrgNoEngageSearchSuppressMs = 3000;

    // Stale logging — avoid spamming every tick
    private bool _assistWasStaleLogged;

    #region IRouteProvider

    public DateTime LastActive => navigation.LastActive;
    public Vector3[] MapRoute() => mapRoute;
    public Vector3[] PathingRoute() => navigation.TotalRoute;
    public bool HasNext() => navigation.HasNext();
    public Vector3 NextMapPoint() => navigation.NextMapPoint();

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
        ChatReader chatReader,
        AssistStateStore assistStateStore,
        LeaderNavigationProvider leaderNavProvider)
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
        this.chatReader = chatReader;
        this.assistStateStore = assistStateStore;
        this.leaderNavProvider = leaderNavProvider;

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
        navigation.OnPathCalculated   += Navigation_OnPathCalculated;
        navigation.OnDestinationReached += Navigation_OnDestinationReached;
        navigation.OnWayPointReached  += Navigation_OnWayPointReached;
        navigation.OnPathFailed       += Navigation_OnPathFailed;

        // Fix S (log-63): TryInsertDetour pushes a new waypoint without firing
        // OnWayPointReached (which only fires on pop). In PartyLeader mode the
        // assist needs to know about the new wpTop so it doesn't keep trying
        // to path through the now-bypassed blacklist. See OnTopWaypointChanged
        // declaration in Navigation.cs for the full rationale.
        navigation.OnTopWaypointChanged += Navigation_OnTopWaypointChanged;

        if (classConfig.Mode == Mode.PartyLeader)
        {
            AddPrecondition(GoapKey.assistrequestreturnorisfollowing, true);
        }

        if (classConfig.Mode == Mode.AttendedGather)
        {
            AddPrecondition(GoapKey.dangercombat, false);
            navigation.OnAnyPointReached += Navigation_OnWayPointReached;
        }
        else if (classConfig.Mode == Mode.PartyLeader)
        {
            AddPrecondition(GoapKey.partyleadercanfollowroute, true);
        }
        else
        {
            if (classConfig.Loot)
                AddPrecondition(GoapKey.incombat, false);

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
            navigation.OnPathCalculated   -= Navigation_OnPathCalculated;
            navigation.OnDestinationReached -= Navigation_OnDestinationReached;
            navigation.OnWayPointReached  -= Navigation_OnWayPointReached;
            navigation.OnPathFailed       -= Navigation_OnPathFailed;
            navigation.OnTopWaypointChanged -= Navigation_OnTopWaypointChanged;
            if (classConfig.Mode == Mode.AttendedGather)
                navigation.OnAnyPointReached -= Navigation_OnWayPointReached;

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
        // ── Fix FU (run-166) — preserve backtrack state across plan flicker ──
        //
        // WoW Classic auto-retargets the leader to whatever mob is hitting it
        // whenever the target slot becomes empty. During Fix FN/FT backtrack,
        // any plan that calls PressClearTarget (ATG.OnEnter E5 gate, Blacklist
        // Target plan, Combat Fix M bail) creates a brief target=0 window —
        // the BL caster's next swing then re-fills the target slot with that
        // same in-rect mob, satisfying ATG/BlacklistTarget/Combat-via-Fix-17
        // preconditions on the very next planner tick. Each such preemption
        // triggers FRG.OnExit → Abort.
        //
        // Run-166 evidence (leader log, leader-clock +49.35s ahead of assist):
        //   LC=20:43:37:228  Fix FT enters BL-ESCAPE BACKTRACK at <954.9, 291.3>
        //                    inside rect [952..999, 277..327]. First backtrack
        //                    target <944.43, 284.25>. IsBacktrackingActive=true.
        //   LC=20:43:38:532  ATG fires for guid 2336265 (Deepmoss Venomspitter,
        //                    in-rect, IsIgnored — WoW auto-retargeted us after
        //                    Fix M bail's ClearTarget at 33:806). FRG.OnExit →
        //                    Abort → ExitBacktrack → IsBacktrackingActive=false,
        //                    _btPhase=None. ATG.OnEnter runs E5 gate, presses
        //                    DisableSoftInteract + ClearTarget at 38:701. ATG
        //                    exits. FRG re-enters, Fix FT entry condition fires
        //                    again because leader still in BL — but starts over
        //                    from _btStepsBack=1.
        //   LC=20:43:48:636  Second backtrack enter (same waypoint <944.43,
        //                    284.25>) at <956.2, 295.4> — only 1.7y of progress
        //                    in 11 seconds.
        //   LC=20:43:50:451  ATG aborts again. Third cycle entry at <953.5,
        //                    290.8>.
        //   LC=20:43:55:555 → 56:636  Third cycle aborts via NO PLAN.
        //   LC=20:44:06:048  Log ends — leader still bouncing in/at the rect
        //                    boundary, never reaching the first backtrack
        //                    waypoint.
        //
        // The existing _btPhase comment (line 132-136) claims state "persists
        // across Combat-plan preemption" — but Abort()'s unconditional
        // ExitBacktrack falsified that comment. Restore the documented
        // behavior: when backtrack is in progress, Abort pauses navigation
        // but leaves _btPhase, _btIsBlEscapeMode, _btStartRouteIdx,
        // _btStepsBack, _btTargetGuid, _btCurrentWaypointW, and
        // navigation.IsBacktrackingActive intact. Resume()'s preserved-state
        // branch re-pushes the saved waypoint on the next FRG.OnEnter and
        // backtrack picks up where it left off — instead of restarting from
        // step 1.
        //
        // ExitBacktrack is now called only from within UpdateBacktrackStateMachine
        // when backtrack genuinely completes (BL escape complete, target killed
        // / lost, route start reached) or invalidates (continuation check fails).
        bool backtrackPreserved = _btPhase != BacktrackPhase.None;
        if (backtrackPreserved)
        {
            logger.LogInformation(
                $"[FRG] {(_btIsBlEscapeMode ? "FT" : "FN")} [FIX-FIRE] FU: " +
                $"Backtrack state PAUSED across plan flicker — phase={_btPhase}, " +
                $"stepsBack={_btStepsBack}, target={_btTargetGuid}, " +
                $"blEscapeMode={_btIsBlEscapeMode}, IsBacktrackingActive=" +
                $"{navigation.IsBacktrackingActive}, waypoint=<{_btCurrentWaypointW.X:F2}," +
                $"{_btCurrentWaypointW.Y:F2}>. State preserved for Resume on next FRG.OnEnter.");
            // DO NOT call ExitBacktrack — state preserved for Resume.
            // navigation.IsBacktrackingActive stays true (set by None→Navigating);
            // BlacklistTargetGoal:52 and Fix 17's `!IsBacktrackingActive` gate
            // remain in effect across the pause, blocking re-entry by those plans.
        }

        if (bits.Target() && targetBlacklist.Is())
            SuppressCurrentTargetBriefly();

        if (!targetBlacklist.Is())
            navigation.StopMovement();

        navigation.PausePathing();

        // Clear the published waypoint — leader is no longer actively navigating
        // the patrol route (combat, evade, paused for assist, etc.).
        // Fix FU: if backtrack is preserved, Resume() will re-publish the
        // backtrack waypoint, so clearing it here is safe.
        leaderNavProvider.ClearTargetWaypoint();

        _syncPauseActive = false;

        sideActivityManualReset.Reset();
        targetFinder.Reset();
        ResetRefillWaypointsGuard();
    }

    private void Resume()
    {
        // Apply per-route area blacklists
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

        // ── Fix FU (run-166) — Resume preserved backtrack state ──
        //
        // If backtrack was paused across a plan flicker (see Abort), pick
        // up where we left off rather than running the normal Resume
        // waypoint discovery logic. Validate the preserved state is still
        // applicable; if not, clean up via ExitBacktrack and fall through
        // to normal Resume.
        if (_btPhase != BacktrackPhase.None)
        {
            bool stateValid;
            if (_btIsBlEscapeMode)
            {
                // BL-escape mode: still applicable as long as the bot is
                // still inside any static rect. If the bot drifted out
                // during the plan flicker (e.g., a Combat plan for a
                // non-BL aggro pushed the bot away), backtrack is done.
                stateValid = navigation.IsInBlacklistArea();
            }
            else
            {
                // Combat-driven mode: still applicable if we're still in
                // combat with the same IsIgnored target. Reuses the same
                // semantic as IsBacktrackContinuationValid for that mode.
                stateValid = bits.Combat()
                    && bits.Target()
                    && playerReader.TargetGuid == _btTargetGuid
                    && targetBlacklist.Is();
            }

            if (!stateValid)
            {
                ExitBacktrack($"[FIX-FIRE] FU: preserved backtrack state no longer applicable on Resume (mode={(_btIsBlEscapeMode ? "BL-escape" : "combat")}, btTarget={_btTargetGuid}, currentTarget={playerReader.TargetGuid}, inBL={navigation.IsInBlacklistArea()})");
                // Fall through to normal Resume below.
            }
            else
            {
                // Re-push the saved waypoint and publish for the assist.
                navigation.SetSingleWaypoint(_btCurrentWaypointW);
                leaderNavProvider.SetTargetWaypoint(_btCurrentWaypointW);
                // IsBacktrackingActive stayed true across the pause; ensure
                // it matches the phase invariant: true during Navigating/
                // Evaluating, false during EngageWindow (Combat owns it).
                navigation.IsBacktrackingActive = (_btPhase != BacktrackPhase.EngageWindow);

                // Reset transient flags that the normal Resume path would
                // reset — leaking these across the pause would confuse the
                // distance gate / party-combat auto-resume logic.
                onEnterTime = DateTime.UtcNow;
                _pausedByLocalLogic = false;
                _pausedByPartyCombat = false;
                ResetRefillWaypointsGuard();

                logger.LogInformation(
                    $"[FRG] {(_btIsBlEscapeMode ? "FT" : "FN")} [FIX-FIRE] FU: " +
                    $"Resuming preserved backtrack — phase={_btPhase}, stepsBack={_btStepsBack}, " +
                    $"target={_btTargetGuid}, blEscapeMode={_btIsBlEscapeMode}, " +
                    $"waypoint=<{_btCurrentWaypointW.X:F2},{_btCurrentWaypointW.Y:F2}>. " +
                    $"Skipping normal Resume waypoint setup. Side-target finder NOT re-enabled " +
                    $"during backtrack (would Tab-cycle to BL mobs).");
                return;
            }
        }

        while (restHandler.IsResting() && !assistStateStore.AnyAssistCantFollow())
            wait.Update(1000);

        onEnterTime = DateTime.UtcNow;
        ResetRefillWaypointsGuard();

        if (sideActivityCts.IsCancellationRequested)
            sideActivityCts = new();

        // Reset the "wantNavPaused caused us to pause" flag on Resume. Abort()
        // pauses navigation through a different code path and does not touch
        // _pausedByLocalLogic, so it can carry stale state across Abort/Resume
        // cycles. Without this reset, if wantNavPaused happens to be true on
        // the first post-Resume Update tick, the transition log
        // "[FRG] Target acquired -> stopping navigation" (line 747) does not
        // fire because the gate `wantNavPaused && !_pausedByLocalLogic` is
        // false — silencing the diagnostics for an actual navigation pause.
        // Observed in log 24 (leader 21:39:36:079 → 21:39:51:090): 15 seconds
        // of complete silence after ATG's focus-chain race re-acquired the
        // blacklisted mob just before the goal switch into FRG.
        _pausedByLocalLogic = false;

        // Fix FA: a legitimate Resume() (e.g. OnEnter on plan transition back
        // to FRG) supersedes any prior party-combat Abort — clear the flag so
        // we don't immediately re-fire the auto-resume gate in Update.
        _pausedByPartyCombat = false;

        if (_suppressTargetFinderUntilUtc != DateTime.MinValue && DateTime.UtcNow < _suppressTargetFinderUntilUtc)
            logger.LogInformation($"[FRG] Resume: target finder still suppressed for {(_suppressTargetFinderUntilUtc - DateTime.UtcNow).TotalMilliseconds:F0}ms.");
        TryEnableSideTargetFinding();

        bool assistIsFollowing  = assistStateStore.AnyAssistIsFollowing();
        bool assistCantFollow   = assistStateStore.AnyAssistCantFollow();

        logger.LogInformation(
            $"[FRG] Resume: HasWaypoint={navigation.HasWaypoint()} HasNext={navigation.HasNext()} " +
            $"AssistIsFollowing={assistIsFollowing} AssistCantFollow={assistCantFollow} " +
            $"navActive={navigation.Active} wp={navigation.WaypointCount} route={navigation.RouteCount}");

        // Fix EP (run-140): if we're resuming Follow while standing INSIDE a blacklist rect,
        // the existing waypoint cannot be trusted. It is typically a leaked ApproachEscape
        // target (a non-route point the escape projected toward a mob in/across the rect;
        // run-140: <-304.65,-4848.47>) that the pather cannot reach from inside the rect
        // ("Closest spot is too far from target. 8.17>5"), leaving the leader looping a failed
        // path forever and the assist trapped in the propagated rect. A bot must never resume
        // patrol from inside a rect on a stale escape target, so skip the existing-waypoint
        // branch and fall through to RefillWaypoints(true) (findClosest) below, which snaps to
        // the nearest ROUTE waypoint -- outside the rect and reachable back the way we came --
        // so the leader paths out and rejoins the route. Confirmed recoverable in run-140:
        // route waypoints <-265.99,-4875> (~19y) and <-224.93,-4893> (~25y) sit beside the
        // stuck spot and the pather already reaches <-306,-4844>, so a near route waypoint is
        // reachable; only the leaked target was unreachable.
        bool insideBlacklistOnResume =
            navigation.AreaBlacklist?.ContainsWorld(playerReader.WorldPos) == true;
        if (insideBlacklistOnResume && (navigation.HasWaypoint() || navigation.HasNext()))
            logger.LogWarning(
                "[FRG] [FIX-FIRE] EP: resuming Follow while inside a blacklist rect with existing " +
                $"waypoints (wp={navigation.WaypointCount} route={navigation.RouteCount} " +
                $"pos={playerReader.WorldPos}) -- discarding them and snapping to the closest route " +
                "waypoint to path out of the rect.");

        if ((navigation.HasWaypoint() || navigation.HasNext()) && !insideBlacklistOnResume)
        {
            if (_pausedByAssistDistance)
            {
                // The distance gate was active when this Resume() was triggered (e.g. by a
                // transient goal like BlacklistTargetGoal causing an Abort/Resume cycle).
                // Re-apply the pause — do NOT restart navigation just because another goal
                // briefly preempted FRG. Without this, the leader keeps moving forward every
                // ~400ms as each BlacklistTarget cycle undoes the distance gate pause.
                logger.LogInformation("[FRG] Resume - distance gate still active, re-applying pause.");
                navigation.PausePathing();
            }
            else
            {
                // In PartyLeader mode, wait for the assist to be within FollowingMaxYards
                // before resuming patrol. If we start moving immediately and the assist is
                // in the dead-band (7–14y), both bots run at the same speed and the assist
                // can never close the gap to confirm rendezvous. The FFG dead-band fix
                // guarantees the assist actively navigates during this pause — together the
                // two halves ensure rendezvous is confirmed before the leader pulls away.
                if (classConfig.Mode == Mode.PartyLeader && assistIsFollowing)
                {
                    // Publish the current top patrol waypoint BEFORE pausing. Abort()
                    // called ClearTargetWaypoint(), so HasTargetWaypoint=false during the
                    // pause unless we explicitly re-publish. FFG's leaderJustPublishedWaypoint
                    // detection (false→true transition) fires within one API poll (~250ms)
                    // and sets NavigatingToLeader → AnyAssistNavigating()=true → fast resolve.
                    PublishPatrolWaypoint();

                    // Guard: FRG.OnEnter() can fire twice in rapid succession when GOAP
                    // briefly selects another goal (e.g. CorpseConsumed) and immediately
                    // re-selects FRG (~15ms later). A second Resume() call would reset
                    // _syncPauseStartUtc, extending the pause by up to 1s and causing the
                    // assist's brief NavigatingToLeader flash (from the first arm) to fall
                    // outside the new timer window. If already armed, just re-publish the
                    // waypoint (to keep it fresh) and leave the original timer intact.
                    if (_syncPauseActive)
                    {
                        logger.LogInformation(
                            "[FRG] Resume - sync-pause already active: re-published top patrol waypoint, keeping original timer.");
                        navigation.PausePathing();
                        return;
                    }

                    logger.LogInformation(
                        "[FRG] Resume - sync-pause (existing waypoints): published top patrol waypoint, " +
                        "waiting for assist to begin navigating before resuming patrol.");
                    _syncPauseActive = true;
                    _syncPauseStartUtc = DateTime.UtcNow;
                    navigation.PausePathing();

                    // Disable the side target-finding thread for the duration of the
                    // sync-pause. Helper at line 340 Set the event before this branch
                    // ran (pause flags were both false on Resume entry); undo that
                    // Set now that _syncPauseActive=true. Single-shot — the helper
                    // checks _syncPauseActive before any future Set, so no other
                    // call site can re-enable until sync-pause completes.
                    sideActivityManualReset.Reset();
                    targetFinder.Reset();
                    return;
                }

                logger.LogInformation("[FRG] Resume - preserving existing navigation progress");
                navigation.Resume();
            }
        }
        else if (classConfig.Mode == Mode.PartyLeader && assistIsFollowing)
        {
            // Load the closest patrol waypoint and publish it BEFORE sync-pausing.
            // This gives FFG a HasTargetWaypoint false→true transition within one API
            // poll (~250ms), triggering leaderJustPublishedWaypoint → NavigatingToLeader
            // → AnyAssistNavigating()=true → sync-pause resolves fast.
            // Without the sync-pause the leader would sprint away immediately; without
            // the pre-publish the assist can't react until after the 1s timeout expires.
            logger.LogInformation(
                "[FRG] Resume - AssistIsFollowing branch (no existing waypoints): loading waypoint, " +
                "publishing, then sync-pausing until assist begins navigating.");
            ClearAssistReturnState();
            navigation.ClearAllRoutes();
            RefillWaypoints(true);   // sets waypoints AND calls PublishPatrolWaypoint()

            if (_syncPauseActive)
            {
                // Second Resume() in rapid succession — keep the original timer, just
                // hold pathing. See the equivalent guard in the HasWaypoint branch above.
                logger.LogInformation(
                    "[FRG] Resume - sync-pause already active (no-waypoint branch): keeping original timer.");
                navigation.PausePathing();
            }
            else
            {
                _syncPauseActive = true;
                _syncPauseStartUtc = DateTime.UtcNow;
                navigation.PausePathing(); // hold — don't start moving yet

                // Disable the side target-finding thread — same reasoning as
                // the HasWaypoint sync-pause entry above.
                sideActivityManualReset.Reset();
                targetFinder.Reset();
            }
        }
        else if (classConfig.Mode == Mode.PartyLeader && assistCantFollow)
        {
            if (_assistReturnActive)
            {
                logger.LogInformation("[FRG] Resume: AssistCantFollow=true but AssistReturn already active — holding position.");
                navigation.PausePathing();
            }
            else
            {
                AssistState? cantFollowState = assistStateStore.GetCantFollowState();
                if (cantFollowState != null)
                {
                    Vector3 assistWaypoint = cantFollowState.MapPosNoZ;
                    logger.LogInformation($"[FRG] Resume - navigating to assist CantFollow position {assistWaypoint}");
                    GoToOneWaypoint(assistWaypoint);
                }
                else
                {
                    logger.LogWarning("[FRG] Resume: AnyAssistCantFollow=true but no valid state — holding.");
                    navigation.PausePathing();
                }
            }
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
                    if (!assistStateStore.AnyAssistIsFollowing() && !assistStateStore.AnyAssistNavigating())
                    {
                        // Fix I-2 (log-55 00:12:47:710, leader inside BL with
                        // active Escape-first ROUTE got Abort()-ed by this
                        // handler firing on the assist's NavigatingToLeader
                        // → CantFollow transition): when the leader is
                        // inside a BL rect, the escape route is in flight
                        // and Abort() would call PausePathing() — killing
                        // navigation while it's mid-escape. Worse, Abort
                        // doesn't set _pausedByAssistDistance, so the
                        // pause-for-assist gate at line 891 can't pick up
                        // the pause and the "Distance gate suppressed"
                        // branch at line 836 can't resume (it requires
                        // _pausedByAssistDistance=true). Result: nav
                        // paused indefinitely until something else
                        // (combat, plan change) re-activates it. In
                        // log-55 the leader sat at <950.88, 296.46>
                        // (0.45 y west of rect) for 24 s until a mob
                        // attack forced Combat.
                        //
                        // Fix: skip Abort when inside BL. The escape
                        // will complete normally, then pause-for-assist
                        // (with Fix I-3's near-edge buffer) will hold
                        // the leader once it's meaningfully clear of
                        // the rect. The status-CantFollow diff in
                        // GoapAgent (Fix I-1) will have separately
                        // fired GoToOneWaypoint on this same tick if
                        // the status is actually CantFollow, so the
                        // leader will navigate toward the assist after
                        // escape completes.
                        bool insideBlacklist =
                            navigation.AreaBlacklist?.ContainsWorld(playerReader.WorldPos) == true;
                        if (insideBlacklist)
                        {
                            logger.LogInformation(
                                "FollowRouteGoal: OnGoapEvent - assist unavailable but leader " +
                                "inside blacklist — skipping Abort to let escape complete. " +
                                "Pause-for-assist will handle the wait once leader is meaningfully clear.");
                        }
                        else
                        {
                            // Assist is genuinely unavailable — not following and not navigating toward us.
                            logger.LogInformation("FollowRouteGoal: OnGoapEvent - assist truly unavailable (not following, not navigating) — aborting.");
                            Abort();
                        }
                    }
                    else
                    {
                        bool wasWaiting = _assistWaitingForFollowing;
                        ClearAssistReturnState();

                        if (wasWaiting)
                        {
                            // Leader navigated to the assist's position and was paused
                            // waiting for "i'm following" confirmation. Now the assist has
                            // confirmed — clear the old return route and refill from here.
                            logger.LogInformation("[FRG] Assist confirmed following after leader navigated to them — resuming patrol.");
                            // Suppress the endpoint direction-forcing for the same reason as
                            // Navigation_OnDestinationReached Path A: the leader is resuming
                            // from the assist's CantFollow position, not from a natural
                            // ThereAndBack reversal point.
                            _suppressDirectionEndpointForcing = pathSettings.PathThereAndBack;
                            navigation.ClearAllRoutes();
                            Resume();
                        }
                        else
                        {
                            // Assist became available (initial connection or stale recovery).
                            // Do NOT call ClearAllRoutes() here — if the leader already has
                            // active waypoints from a normal patrol, wiping them causes a stall:
                            // Resume() would then call RefillWaypoints(true) (single closest
                            // waypoint), the leader walks to it, OnDestinationReached fires, and
                            // RefillWaypoints starts over — freezing the leader for several seconds
                            // whenever the assist's POST delivery briefly goes stale and recovers.
                            // Resume() already handles "HasWaypoint → preserve, no waypoints →
                            // refill" correctly without any pre-clearing.
                            logger.LogInformation(
                                $"[FRG] Assist available (following={assistStateStore.AnyAssistIsFollowing()} " +
                                $"navigating={assistStateStore.AnyAssistNavigating()}) — resuming. " +
                                $"navActive={navigation.Active} wp={navigation.WaypointCount}");
                            Resume();
                        }
                    }
                    break;

                case GoapKey.assistrequestreturn:
                    // The actual AssistReturn navigation is started by
                    // GoapAgent's diff-loop at GoapAgent.cs:343-346,
                    // which calls goal.GoToOneWaypoint(MapPosNoZ) on
                    // every FollowRouteGoal whenever AnyAssistCantFollow
                    // transitions to true. That code path executes
                    // *concurrently with* this event handler (the event
                    // is broadcast from GoapAgent on the same tick), so
                    // by the time this handler runs the AssistReturn is
                    // already in flight.
                    //
                    // This handler is informational only — it logs that
                    // we observed the transition. The Fix 32 cleanup
                    // that removed the GoToOneWaypoint call here is
                    // pending revision: per user direction we are
                    // restoring leader-returns-to-assist behavior with
                    // safeguards (A targeting actual assist position,
                    // B per-CantFollow-event retry bound, C segment-clear
                    // fast path) in a follow-up change. Until then,
                    // GoapAgent's direct call is the path that fires.
                    //
                    // Fix 33 (log-53 leader line 484-487: "[FRG] OnGoapEvent
                    // assistrequestreturn ... Leader will hold position
                    // (no AssistReturn)" was logged 1 ms before
                    // "[FRG] AssistReturn begin -> <60.3843, 60.5665, 0>"
                    // — the log lied about the actual behavior): correct
                    // the log message to describe what GoapAgent does,
                    // not what Fix 32 intended.
                    if (assistStateStore.AnyAssistCantFollow())
                    {
                        AssistState? cantFollow = assistStateStore.GetCantFollowState();
                        if (cantFollow != null)
                        {
                            logger.LogInformation(
                                $"[FRG] OnGoapEvent assistrequestreturn — assist at " +
                                $"({cantFollow.MapX:0.00},{cantFollow.MapY:0.00}). " +
                                $"GoapAgent diff-loop (GoapAgent.cs:343) will fire " +
                                $"GoToOneWaypoint on this same tick.");
                        }
                    }
                    else if (_assistReturnActive)
                    {
                        // Fix CE-2 (log-108): Falling-edge handler for the
                        // assistrequestreturn broadcast. The GoapAgent's
                        // union diff (GoapAgent.cs line ~366) calls
                        // AssistNotRequestReturn() when AnyAssistCantFollow()
                        // transitions from true to false, which broadcasts
                        // GoapKey.assistrequestreturn=false to all goals.
                        // Previously this branch was empty — the case body
                        // only handled the rising edge via the
                        // AnyAssistCantFollow() check above. As a result,
                        // an in-flight AssistReturn (set by the union diff's
                        // sibling rising-edge call to GoToOneWaypoint, or by
                        // the status diff at GoapAgent line ~437) would
                        // persist on the leader's nav stack until either:
                        //   1. The leader reached the AssistReturn destination
                        //      (where _assistWaitingForFollowing pauses
                        //      indefinitely waiting for assist's "following"
                        //      confirmation),
                        //   2. The 25s ASSIST_RETURN_TIMEOUT_ACTIVE_SEC
                        //      timeout fired (TickAssistReturnTimeout), OR
                        //   3. A subsequent path failure triggered
                        //      AbortAssistReturn via the rewind retry path.
                        //
                        // None of these capture the common case where the
                        // assist briefly entered CantFollow during combat
                        // preemption, then the assist's AK gate cleared the
                        // CantFollow state at combat end (Fix CE-1 clears
                        // the flag too) — the leader's AssistReturn
                        // destination becomes stale immediately but the
                        // leader keeps walking to it.
                        //
                        // Log-108 manifestation:
                        //   13:31:42:689  leader: union diff rising edge,
                        //                 AssistReturn begin → <-515.76>
                        //   13:31:42:740  assist: NavState → CantFollow
                        //                 (path-rejection at <-515.55>)
                        //   13:31:42-47   combat preempts both bots
                        //   13:31:48:114  leader: Resume PRESERVE branch
                        //                 keeps AssistReturn dest on stack
                        //   13:31:48:251  assist: AK gate clears _navState
                        //                 (+ Fix CE-1 clears flag)
                        //   13:31:48:252  assist: BM-3 picks route[47] EAST
                        //   13:31:48+     leader walks WEST (AssistReturn),
                        //                 assist walks EAST (route) —
                        //                 user-visible corridor loop
                        //
                        // With CE-1 clearing the flag at 13:31:48:251, the
                        // GoapAgent's union diff sees AnyAssistCantFollow
                        // fall from true to false on the next agent tick,
                        // broadcasts assistrequestreturn=false. This
                        // handler catches that broadcast and aborts the
                        // stale AssistReturn — leader's nav stack is
                        // cleared, Resume() refills with normal patrol
                        // waypoints from the leader's current position,
                        // and the two bots converge on the same route.
                        //
                        // Safety: _assistReturnActive guard prevents
                        // spurious aborts when the broadcast fires but no
                        // AssistReturn was ever initiated (e.g., flag
                        // briefly rose and fell before any goal could
                        // pick it up).
                        logger.LogInformation(
                            "[FRG] OnGoapEvent assistrequestreturn (falling edge) — " +
                            "assist no longer reports CantFollow. Aborting in-flight " +
                            "AssistReturn (Fix CE-2) so the leader doesn't continue " +
                            "walking to a stale rescue destination.");
                        AbortAssistReturn("Fix CE-2: assistrequestreturn falling edge");
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
                    // Fix 14: gate Abort on whether Combat is actually runnable.
                    //
                    // FRG.Abort here is a courtesy to CombatGoal — drop the
                    // patrol so the planner can pick Combat to fight whatever
                    // mob the party engaged. But CombatGoal in PartyLeader /
                    // AssistFocus mode has AddPrecondition(allPartyTargetsIsIgnored,
                    // false) (CombatGoal.cs:90, :98). When every party target
                    // slot points to a mob on the IsIgnored map, CombatGoal
                    // cannot be selected by the planner. Aborting the patrol
                    // in that case accomplishes nothing except halting movement,
                    // which during an evade window is exactly the opposite of
                    // the design intent (the patrol IS the retreat from the
                    // blacklist rect).
                    //
                    // Computed inline rather than via broadcast subscription
                    // because the partyincombat broadcast is itself the trigger
                    // here — at the moment FRG sees partyincombat=true, the
                    // GoapAgent has NOT yet run UpdateWorldState for this
                    // tick (NextGoal runs after the broadcast block in
                    // GoapThread). A broadcast-based subscription would deliver
                    // the value one tick late. Inline computation reads the
                    // current playerReader / bits / IsIgnored state directly,
                    // which is the same data UpdateWorldState would have used.
                    //
                    // Fix 13 self-defense override deliberately not replicated
                    // here: that override fires only after the evade window
                    // ends, at which point partyincombat is already true (it
                    // went true when the mob first attacked, before evade).
                    // The override's effect on allPartyTargetsIsIgnored never
                    // coincides with a partyincombat rising-edge, so FRG
                    // doesn't need to mirror it.
                    //
                    // Log-42 motivating sequence:
                    //   17:22:33 leader's escape-first carries it out of the rect.
                    //   17:22:37 leader patrol resumes, walking toward next waypoint.
                    //   17:22:39:377 partyincombat=true broadcast (assist still
                    //                hit by the blacklisted mob). Before this
                    //                fix, FRG.Abort fires unconditionally →
                    //                leader frozen for the remaining ~19 s of
                    //                evade. With this fix, FRG computes
                    //                allPartyTargetsIsIgnored=true inline (leader
                    //                has no target → targetIgnored=true; focus
                    //                target 163662 is on IsIgnored →
                    //                focusTargetIgnored=true), skips Abort,
                    //                retreat patrol continues uninterrupted.
                    //
                    // Handles cleanly:
                    //   - Evade window with blacklisted mob still attacking
                    //     (allPartyTargetsIsIgnored=true) → no abort, patrol
                    //     carries us out of the rect.
                    //   - Mixed combat: blacklisted mob plus a non-blacklisted
                    //     mob attacks during evade. allPartyTargetsIsIgnored
                    //     becomes false (the non-blacklisted slot is fightable)
                    //     → abort fires, CombatGoal engages the fightable mob.
                    //   - Normal combat outside evade (target not on IsIgnored)
                    //     → allPartyTargetsIsIgnored=false → abort fires as
                    //     before. No behavior change for the normal path.
                    if ((classConfig.Mode == Mode.PartyLeader || classConfig.Mode == Mode.AssistFocus)
                        && (bits.Combat() || bits.Focus_Combat()))
                    {
                        bool partyTargetIgnored =
                            !bits.Target() || playerReader.IsIgnored(playerReader.TargetGuid);
                        bool partyFocusTargetIgnored =
                            !bits.FocusTarget() || playerReader.IsIgnored(playerReader.FocusTargetGuid);
                        bool allPartyTargetsIsIgnored = partyTargetIgnored && partyFocusTargetIgnored;

                        if (!allPartyTargetsIsIgnored)
                        {
                            logger.LogInformation("FollowRouteGoal: OnGoapEvent - Party entered Combat, trying to exit!");
                            Abort();
                            // Fix FA: tag this Abort as "paused by party-combat" so the
                            // Update auto-resume gate can fire when conditions clear,
                            // even if no plan transition (FRG→Combat→FRG) ever happens.
                            // See _pausedByPartyCombat field comment for the run-152
                            // standoff motivation.
                            _pausedByPartyCombat = true;
                        }
                        else
                        {
                            logger.LogInformation(
                                $"[FRG] partyincombat=true but allPartyTargetsIsIgnored=true " +
                                $"(targetIgnored={partyTargetIgnored} focusTargetIgnored={partyFocusTargetIgnored}) — " +
                                $"Combat cannot run, continuing retreat patrol uninterrupted.");
                        }
                    }
                    break;
            }
        }

        if (e.GetType() == typeof(AbortEvent))
            Abort();
        else if (e.GetType() == typeof(ResumeEvent))
            Resume();
    }

    public override void OnEnter() => Resume();
    public override void OnExit() => Abort();

    public override void Update()
    {
        // ── Target finder suppression expiry (must run before early returns) ──
        // The per-target evade-blacklist suppression window has elapsed.
        // Use the helper so the side thread is only re-enabled when the
        // leader is actually patrolling — if a pause-for-assist is currently
        // in effect, the helper skips Set and the pause-exit path will
        // re-enable the side thread when patrol resumes.
        if (_suppressTargetFinderUntilUtc != DateTime.MinValue &&
            DateTime.UtcNow >= _suppressTargetFinderUntilUtc)
        {
            _suppressTargetFinderUntilUtc = DateTime.MinValue;
            logger.LogInformation("[FRG] Target finder suppression elapsed.");
            TryEnableSideTargetFinding();
        }

        // ── Sync-pause: hold position until the assist starts navigating toward us ──
        // Set in Resume() when the leader has existing patrol waypoints.
        // Resolves when the assist is actively navigating (NavigatingToLeader) OR already
        // Following. The original condition — NavigatingToLeader only — was too narrow:
        // when the assist is co-located (<7y) on re-entry, FFG's leaderJustPublishedWaypoint
        // detection calls StartNavigatingToLeader, but UpdateNavigatingToLeader immediately
        // exits to Idle on the very next tick (dist < FollowingMaxYards=7y). The
        // NavigatingToLeader status lasts ~15ms — far too brief for the leader's API poll
        // (~250ms) to catch. Adding AnyAssistIsFollowing() covers the co-located case:
        // if the assist is Following AND within range, they are already in position and
        // there is no head-start problem to solve.
        if (_syncPauseActive)
        {
            double elapsed = (DateTime.UtcNow - _syncPauseStartUtc).TotalSeconds;

            // Fix BO: require BOTH a nav-state signal AND proximity. The old condition
            // (assistNavigating || assistFollowing alone) exited immediately because the
            // assist is in NavigatingToLeader almost continuously during catch-up — proximity
            // was ignored. New condition: distance <= FrgSyncReadyYards counts as ready
            // (handles both navigating-toward and co-located cases); otherwise wait for the
            // (raised) timeout. Companion to Fix BN in ApproachTargetGoal.cs.
            float distToLeader = assistStateStore.GetNearestAssistDistanceYards(playerReader.WorldPos);
            bool assistNearby = distToLeader <= FrgSyncReadyYards;
            bool assistNavigating = assistStateStore.AnyAssistNavigating();
            bool assistFollowing  = assistStateStore.AnyAssistIsFollowing();
            bool assistReady      = assistNearby && (assistNavigating || assistFollowing);

            if (assistReady || elapsed >= SyncPauseTimeoutSec)
            {
                _syncPauseActive = false;
                logger.LogInformation(
                    $"[FRG] [FIX-FIRE] BO: Sync-pause complete: distToLeader={distToLeader:0.0}y " +
                    $"(threshold={FrgSyncReadyYards}y), assistNavigating={assistNavigating} " +
                    $"assistFollowing={assistFollowing} elapsed={elapsed:0.1}s " +
                    $"(timeout={SyncPauseTimeoutSec:0.0}s) — resuming patrol navigation.");
                navigation.Resume();

                // Re-enable side target-finding alongside navigation.
                // _syncPauseActive was just cleared above, so the helper's
                // pause-flag gate passes; the suppression-window gate is
                // applied identically to Resume() line 340.
                TryEnableSideTargetFinding();
            }
            else
            {
                wait.Update();
                return;
            }
        }

        // NOTE: The watchdog that previously re-enabled the side thread here was removed.
        // It caused a race on bot stop: Active=false fires Abort() (resets the event) on
        // the setter thread while GoapThread is still in Update() — the watchdog would
        // immediately re-enable the event, leaving Thread_LookingForTarget running after
        // the bot was stopped. Resume() is the correct and only place to re-enable the
        // side thread; Abort() is the correct place to disable it.

        // ── Fix FA (run-152 13:31:55 → 13:35:59 standoff) — party-combat auto-resume ──
        //
        // When OnGoapEvent's partyincombat=true branch called Abort() at
        // 13:31:55:077, the leader's pathing paused and FRG remained as the
        // current goal. The expected recovery path was FRG → Combat → FRG
        // via plan transition (Combat.OnExit → FRG.OnEnter → Resume), but
        // Combat plan's precondition partyleadercombat=true failed at the
        // planner tick that followed Abort (bits.FocusTarget flickered FALSE
        // between the broadcast and the planner tick, collapsing
        // PartyLeaderInCombat()'s disjunct 2). The planner re-selected FRG
        // without a transition → no OnEnter → no Resume → bot frozen at
        // <361,-4238> emitting only Random jump every 10s for 4 minutes
        // until the log ended.
        //
        // This gate runs every Update tick while FRG is the current goal.
        // If we're in the party-combat-paused state AND the party is no
        // longer in combat (per the safest available data sources), resume
        // navigation directly without waiting for a plan transition.
        //
        // Data sources, in safest-against-stuck order:
        //   - bits.Combat() is always accurate for our own combat state
        //     (own bits don't go stale). If true, we're still in combat;
        //     don't resume.
        //   - In PartyLeader mode: AssistStateStore.AnyAssistInCombat() is
        //     authoritative for the assist's combat state — it reads the
        //     polled AssistState.InCombat from the assist's
        //     PartyStatePublisher (Fix EY data path), gated by IsStale
        //     (3000ms). When all polled state is stale or no assist has
        //     contacted us, AnyAssistInCombat returns false — which biases
        //     toward resuming (the user-requested "safer against stuck"
        //     trade-off: if we've lost contact with the assist, we'd rather
        //     resume patrol and re-Abort on the next legitimate broadcast
        //     than sit frozen indefinitely).
        //   - In AssistFocus / Grind modes: skip the assist check; only the
        //     bot's own bits.Combat() matters.
        //
        // bits.Focus_Combat() is DELIBERATELY NOT in the gate. That bit can
        // stick at stale TRUE when the focus is out of WoW client visibility
        // range (assist moved to 424y in run-152), which is the exact
        // failure mode this fix exists to escape. Trusting the polled state
        // (which the assist itself authoritatively publishes) over the stale
        // bit is the whole point.
        //
        // No debounce: the operator's directive was "safer for ensuring we
        // don't get stuck in that bad state." A debounce would introduce a
        // window during which transient bit-flicker could re-trigger
        // Abort while the auto-resume gate is still waiting, perpetuating
        // the stuck state. If the resume fires prematurely, the next
        // partyincombat=true broadcast (driven by UpdateWorldState's diff
        // loop in GoapAgent) will re-Abort cleanly. Cost of a false
        // positive resume is bounded; cost of staying stuck is not.
        //
        // Other pause flags (_syncPauseActive, _pausedByAssistDistance,
        // _pausedByLocalLogic) gate this gate — if any of them are set,
        // their own pause logic owns the resume decision and we don't want
        // to undercut them.
        // Mode gate (PartyLeader only): the polled check uses
        // AssistStateStore, which is a leader-side singleton (populated by
        // POSTs from assists). On AssistFocus, the store is empty —
        // AnyAssistInCombat would always return false, and the gate would
        // fire on bits.Combat()=false alone (the assist's OWN combat being
        // over), prematurely resuming while the LEADER is still in combat
        // (the trigger condition we Aborted for). A symmetric AssistFocus
        // auto-resume would need leaderConnection.LastLeaderState.InCombat
        // — that requires constructor-injecting LeaderConnectionStatus into
        // FRG, which is a wider change deferred until a real AssistFocus-
        // running-FRG scenario shows the symptom. For now, the existing
        // bits-based behavior on AssistFocus (Abort fires, no auto-resume,
        // relies on plan transition for recovery) is preserved unchanged.
        if (_pausedByPartyCombat
            && classConfig.Mode == Mode.PartyLeader
            && !bits.Combat()
            && !assistStateStore.AnyAssistInCombat()
            && !_syncPauseActive
            && !_pausedByAssistDistance
            && !_pausedByLocalLogic)
        {
            _pausedByPartyCombat = false;
            logger.LogInformation(
                "[FRG] [FIX-FIRE] FA: Party-combat pause cleared (bits.Combat=false, " +
                $"polled-assist-InCombat={assistStateStore.AnyAssistInCombat()}) " +
                "— resuming patrol navigation directly without a plan transition.");
            navigation.Resume();

            // Abort() called leaderNavProvider.ClearTargetWaypoint() — the
            // published waypoint is empty until we re-publish. PublishPatrolWaypoint
            // restores it so the assist's poll on the next interval sees a
            // fresh top waypoint and resumes its own follow logic.
            PublishPatrolWaypoint();

            // Re-enable the side target-finding thread (Abort() called
            // sideActivityManualReset.Reset()). Same call pattern as the
            // sync-pause complete branch above.
            TryEnableSideTargetFinding();
        }

        // ── Consume pause requests from side thread ──
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
                LogWarning($"Unable to clear target!");
            }
        }

        if (bits.Drowning())
            input.PressJump();

        // ── Fix FN (run-162) — caster-in-BL retreat backtrack ──
        //
        // Drives the leader back through prior route waypoints when an
        // IsIgnored caster inside a BL rect is hitting us from outside our
        // reachable range. See UpdateBacktrackStateMachine for details and
        // the BacktrackPhase fields near the class top.
        //
        // Must run BEFORE IsSuppressedBlacklistedTarget below (which would
        // call PressClearTarget on our tracking target) and BEFORE the
        // PartyLeader pause-for-assist block (which Fix FM clears for self-
        // defense already, but backtrack supersedes that: backtrack drives
        // navigation directly rather than just lifting the pause).
        //
        // Leader-only — the assist's FFG navigates to the leader's published
        // TargetWaypoint, so when backtrack pushes a backwards waypoint and
        // publishes it via leaderNavProvider.SetTargetWaypoint, the assist
        // follows naturally without code changes on the FFG side.
        if (classConfig.Mode == Mode.PartyLeader && UpdateBacktrackStateMachine())
        {
            // Backtrack owns this tick.
            //   • Navigating: drive navigation toward the backtrack waypoint.
            //   • Evaluating: stationary while we wait for facing/refresh
            //     and the verdict re-check.
            //   • EngageWindow: Combat plan owns the bot (selected by GOAP
            //     planner via Fix FN engage signals); we just hold state.
            if (_btPhase == BacktrackPhase.Navigating)
                navigation.Update(CancellationToken.None);
            wait.Update();
            return;
        }

        if (IsSuppressedBlacklistedTarget())
        {
            Log("Suppressed recently-blacklisted target reacquired, clearing again");
            input.PressClearTarget();
            wait.Update();
            return;
        }

        // ── PartyLeader: enforce assist is following ──────────────────────
        if (classConfig.Mode == Mode.PartyLeader)
        {
            bool assistIsFollowing = assistStateStore.AnyAssistIsFollowing();
            bool assistCantFollow  = assistStateStore.AnyAssistCantFollow();
            bool assistStale       = assistStateStore.HasSeenAnyAssist && assistStateStore.AnyAssistStale();

            if (!assistIsFollowing && !assistCantFollow)
            {
                if (!assistStale)
                {
                    // Assist exists but neither Following nor CantFollow — still navigating.
                    // Normal; this is handled by the distance gate below.
                }
                else
                {
                    // Assist has gone stale — hold position.
                    if (!_assistWasStaleLogged)
                    {
                        _assistWasStaleLogged = true;
                        logger.LogWarning("[FRG] Assist state is STALE — holding position indefinitely.");
                    }
                    navigation.PausePathing();
                    wait.Update();
                    return;
                }
            }
            else
            {
                _assistWasStaleLogged = false;
            }

            // ── API-based distance / status gate ───────────────────────────
            if (classConfig.Mode == Mode.PartyLeader)
            {
                // Fix 6 (log-36 02:52:55:728 → 02:53:21:994): suppress the
                // pause-for-assist gate while the leader is inside a blacklist.
                // The blacklist-escape route in Navigation.Refill
                // (TryComputeEscapeOutOfBlacklist) only runs when navigation.Update
                // is processing — which requires active=true. PausePathing() sets
                // active=false, so a paused leader cannot escape the rect it's
                // sitting in. The previous behavior held the leader stationary
                // inside the forbidden rect indefinitely (26 s in log-36) until
                // the assist's 30 s TickNavActiveTimeout fired CantFollow →
                // AssistReturn → SetWayPoints + Resume re-activated nav, at
                // which point Refill ran and the escape route was finally set.
                //
                // Fix: don't pause inside a blacklist. If currently paused for
                // assist distance, force-resume so navigation.Update at line 928
                // can fire Refill. The escape route (~24 y to the rect's safe
                // edge in log-36) brings the leader out; on the next tick the
                // distance gate evaluates normally (now outside) and the leader
                // pauses there if still > LeaderPauseYards from the assist.
                // Assist's position-chase target is then a clean point outside
                // the rect — assist can navigate to it without the SetWaypoint/
                // SkipBlacklistedWaypoints loop that prompted Fixes 4 & 5.
                bool insideBlacklist =
                    navigation.AreaBlacklist?.ContainsWorld(playerReader.WorldPos) == true;

                // Fix I-3 (log-55 00:13:27:802, leader paused at <952.34, 292.33>
                // 0.05 y west of rect MinX=952.39 while still inside the
                // mesh-edge-to-interior connection zone): the strict
                // insideBlacklist check above is boundary-exclusive — the
                // moment the leader exits the rect by any amount,
                // insideBlacklist flips false and the pause fires below.
                // But at sub-yard distances the navmesh from the leader's
                // position still routes through interior nodes (log-54
                // failure mode: leader at 0.43 y outside rect, all paths
                // through <954.539, 290.37> inside rect). Pausing here
                // strands the leader at the boundary because subsequent
                // patrol attempts re-trigger the same wedge.
                //
                // Fix: extend "suppress pause" to include the inflated
                // zone (6 y buffer) around any BL rect. The leader keeps
                // moving until clear of the navmesh-edge connections,
                // then pauses at a position where the next pather call
                // can route freely. 6 y is chosen to match
                // LeaderArrivedYards (the natural "near" threshold) and
                // typical navmesh resolution, smaller than the 18 y
                // DetourMargin+6 used by Navigation's escape-continuation
                // logic (which holds an existing escape, vs this
                // suppressing new pauses).
                const float NearBLEdgeBufferYards = 6f;
                bool nearBlacklistEdge = !insideBlacklist
                    && navigation.AreaBlacklist != null
                    && navigation.AreaBlacklist.TryGetContainingRectInflated(
                        playerReader.WorldPos, NearBLEdgeBufferYards, out _);

                bool inOrNearBlacklist = insideBlacklist || nearBlacklistEdge;

                // ── Fix FM (run-162 LC=01:39:21:933→01:40:01:418 — leader paused
                //    at <974.47, 270.15> for ~32 s under caster fire while assist
                //    was 32.7 y away; pause set at LC=01:39:37:022 "Pausing for
                //    assist (at waypoint) — dist=32.7y status=TooFar") ──
                //
                // Fix I-3's 6 y near-BL buffer caught "leader strands AT rect
                // edge" but missed "leader strands JUST OUTSIDE the buffer under
                // attack from a caster INSIDE the rect." Casters have ~30 y range,
                // so the danger zone extends far beyond rect+6 y inflate.
                //
                // Run-162 geometry: rect 4007 = [952..999, 277..327], inflated
                // by 6 → [946..1005, 271..333]. Leader at Y=270.15 was 0.85 y
                // outside the inflated MinY=271. nearBlacklistEdge=false →
                // Fix I-3 didn't fire. _pausedByAssistDistance stayed true.
                // The caster (Deepmoss Venomspitter guid=2267577) kept hitting
                // the leader from inside the rect for the full 32 s until the
                // user paused the bot manually.
                //
                // The Fix-32 design comment at line ~2243 promises "Combat
                // self-defense via Fix 17/22/26 still works" — but for an
                // IN-RECT caster CombatGoal Fix 17 is gated by engageAllowed
                // (CombatGoal.cs:528-532), which evaluates false when target
                // reads in-rect on most ticks (P3 degenerate / P1 hit). So
                // Fix 17 mostly doesn't fire, the combat-path escape from the
                // pause never engages, and the leader is stranded.
                //
                // More precise signal: leader is in ACTIVE SELF-DEFENSE
                // against an IsIgnored target — same conditions as GoapAgent's
                // IsIgnored self-defense override (line 1693-1698) minus
                // dmgTaken and !evadeRecoveryActive (omitted to avoid pulling
                // CombatLog and AssistStatusProvider into FRG; same trade-off
                // documented in BlacklistTargetGoal.cs Fix FL). When this
                // state holds, pause-for-assist becomes a death trap — retreat
                // is the path out. FRG's existing BL-avoidance (DetourMargin,
                // RefillWaypoints rect-rejection) handles routing safely.
                //
                // Rendezvous safety: when both bots are in self-defense
                // together (typical: assist's Fix 17 first-activation sets
                // CantFollow via Fix 23, which is one of the inputs to the
                // leader's distance gate), the assist's NavigatingToLeader
                // path resumes following once combat dissolves on its side.
                // Rendezvous reconstitutes naturally after caster aggro
                // breaks.
                bool inSelfDefenseVsIsIgnored =
                    bits.Target() &&
                    targetBlacklist.Is() &&
                    bits.Combat() &&
                    (playerReader.TargetTarget is UnitsTarget.Me
                                              or UnitsTarget.Pet
                                              or UnitsTarget.PartyOrPet);

                if ((inOrNearBlacklist || inSelfDefenseVsIsIgnored) && _pausedByAssistDistance)
                {
                    string locationDesc;
                    if (inSelfDefenseVsIsIgnored && !inOrNearBlacklist)
                    {
                        locationDesc = $"in active self-defense vs IsIgnored target guid=" +
                                       $"{playerReader.TargetGuid} (TargetTarget={playerReader.TargetTarget}) " +
                                       "[Fix FM]";
                    }
                    else if (insideBlacklist)
                    {
                        locationDesc = "inside a blacklist [Fix I-3]";
                    }
                    else
                    {
                        locationDesc = $"within {NearBLEdgeBufferYards:0}y of a blacklist [Fix I-3]";
                    }
                    logger.LogWarning(
                        $"[FRG] Distance gate suppressed: leader is {locationDesc} at " +
                        $"<{playerReader.WorldPos.X:F2},{playerReader.WorldPos.Y:F2},{playerReader.WorldPos.Z:F2}> " +
                        "— resuming pathing so the escape/retreat route can fire. " +
                        "Distance gate will re-evaluate after escape on the next tick.");
                    _pausedByAssistDistance = false;
                    leaderNavProvider.SetPausedForAssist(false); // Fix T
                    navigation.Resume();
                }

                // When inside-or-near a blacklist OR in self-defense vs IsIgnored,
                // force shouldPause=false so the pause-entry branch below cannot
                // re-enter the pause we just cleared. _pausedByAssistDistance is
                // already false (either we just cleared it above, or the leader
                // was never paused), so the resume-exit branch (`else if`) also
                // cannot fire.
                bool retreatPriorityActive = inOrNearBlacklist || inSelfDefenseVsIsIgnored;
                bool shouldPause = !retreatPriorityActive && assistStateStore.ShouldLeaderPauseForAssist(
                    playerReader.WorldPos, LeaderPauseYards);

                bool shouldResume = assistStateStore.ShouldLeaderResumePatrol(
                    playerReader.WorldPos, LeaderResumeYards);

                // Fix 25 (log-48 10:31:38:362 → 10:32:17, leader paused
                // permanently after starting AssistReturn): when AssistReturn
                // is already active (the leader was just dispatched toward the
                // CantFollow assist via OnGoapEvent.assistrequestreturn or the
                // GoapAgent diff-loop), the pause-for-assist block below would
                // call navigation.PausePathing() and immediately kill the
                // AssistReturn navigation that was started 1 ms earlier. Order
                // of events in log-48 at 10:31:38:362–:364:
                //   1. OnGoapEvent.assistrequestreturn → GoToOneWaypoint
                //      → SetWayPoints (active=true, "was paused, re-Acquired")
                //      → _assistReturnActive=true
                //   2. GoapAgent diff-loop → GoToOneWaypoint (already active)
                //   3. FRG.Update reaches pause-for-assist:
                //      - shouldPause=true (assist.Status=CantFollow)
                //      - !_pausedByAssistDistance=true → ENTERED pause block
                //      - PausePathing() killed the AssistReturn nav
                //      - inner `if (assistCantFollow && !_assistReturnActive)`
                //        was skipped (latch already active), no re-setup
                // Deadlock: leader paused, assist in CantFollow holding for
                // leader-within-6 y, leader can't move to satisfy that, assist
                // can't move without leader arrival → both standing still
                // forever (39 s in log-48 until log ended).
                //
                // Fix: AssistReturn and pause-for-assist are responses to the
                // same underlying condition (assist needs help), but pause-
                // for-assist is for "wait here, assist will catch up" while
                // AssistReturn is for "go fetch the assist". They are mutually
                // exclusive — adding !_assistReturnActive prevents the latter
                // from overriding the former. When AssistReturn completes or
                // aborts (ClearAssistReturnState / AbortAssistReturn clears
                // _assistReturnActive), the next FRG.Update tick re-evaluates
                // pause-for-assist on the assist's current (post-arrival)
                // status and acts appropriately.
                // ────────────────────────────────────────────────────────
                // Fix BF (log-91 17:54:11→17:54:52, log-92 18:59:55→19:00:50):
                // The pause-SET logic that used to live here has MOVED to
                // Navigation_OnWayPointReached (line 1726+).
                //
                // Why: the per-tick check that previously fired here would
                // pause the leader MID-STRIDE — typically at an arbitrary
                // off-route position after combat carried the leader away
                // from the patrol path. The assist's _cachedLeaderRouteIdx
                // (derived from the leader's last-published TargetWaypoint)
                // would then point to a route waypoint the leader had not
                // physically reached. The asymmetry between
                //   - "where the leader IS" (off-route WorldPos), and
                //   - "where the leader CLAIMS to be on the route" (cached idx
                //      pointing at the next route waypoint it was heading to
                //      pre-combat)
                // drove the BE route-span code to build spans toward a
                // phantom destination, producing both observed oscillations.
                //
                // By deferring the pause check to waypoint arrival, the leader
                // ALWAYS pauses on-route (at a route waypoint it just reached).
                // The cache then matches the leader's physical position, and
                // route-walking works correctly.
                //
                // Resume side STAYS HERE in Update so the leader unpauses with
                // minimal latency once the assist catches up — pause overshoot
                // (up to one stride) is acceptable, but resume overshoot is
                // pure wasted time.
                //
                // The shouldPause local is still computed below because the
                // resume branch (`!shouldPause && shouldResume && _pausedByAssistDistance`)
                // needs it. The pause-SET IF block is gone; only the resume
                // ELSE IF remains.
                // ────────────────────────────────────────────────────────
                if (!shouldPause && shouldResume && _pausedByAssistDistance)
                {
                    float dist = assistStateStore.GetNearestAssistDistanceYards(playerReader.WorldPos);
                    logger.LogInformation(
                        $"[FRG] Assist Following and within range (dist={dist:0.0}y) — resuming patrol.");

                    // Same diagnostic detail as the pause path. The pause and
                    // resume thresholds are different (LeaderPauseYards=25
                    // after Fix BF, LeaderResumeYards=15), and during a flapping
                    // window the resume side can also fire on stale data.
                    Vector3 leaderPos = playerReader.WorldPos;
                    logger.LogInformation(
                        $"[FRG] Resume-detail: leaderPos=<{leaderPos.X:F2},{leaderPos.Y:F2},{leaderPos.Z:F2}> " +
                        $"resumeYards={LeaderResumeYards}");
                    foreach (AssistState s in assistStateStore.GetAll())
                    {
                        float d = leaderPos.WorldDistanceXYTo(s.WorldPos);
                        logger.LogInformation(
                            $"[FRG] Resume-detail: assist id='{s.AssistId}' " +
                            $"pos=<{s.WorldX:F2},{s.WorldY:F2},{s.WorldZ:F2}> " +
                            $"status={s.Status} cantFollow={s.CantFollow} " +
                            $"routeIdx={s.AssistRouteIndex} " +    // Fix BF: published route progress
                            $"dist={d:F2}y ageMs={s.AgeMs:F0} stale={assistStateStore.IsStale(s)}");
                    }

                    _pausedByAssistDistance = false;
                    leaderNavProvider.SetPausedForAssist(false); // Fix T

                    // Fix 8 (log-38 ping-pong, cycles 4-7): preserve runtime
                    // waypoint state on resume from pause-for-assist. The
                    // wpStack is intact across PausePathing (which only sets
                    // active=false and releases the stuck detector; wayPoints
                    // and routeToNextWaypoint are untouched). The previous
                    // behavior — `navigation.ClearAllRoutes(); RefillWaypoints(false);` —
                    // rebuilt wpStack from the patrol path's "forward resume
                    // point" relative to the leader's CURRENT position. When
                    // the leader had navigated off-path toward a runtime
                    // blacklist detour before the pause, the rebuild picked
                    // up a patrol waypoint BEHIND the detour and republished
                    // it as TargetWaypoint. The leader then navigated BACK to
                    // that old patrol waypoint, popped it, hit the same
                    // blacklist rejection, inserted the same detour, and
                    // navigated forward again — yo-yo for the entire session
                    // (log-38 observed leader oscillating between <-132,-1536>
                    // and <-128,-1562>, ~25 y apart, for ~2 minutes with no
                    // patrol progress; assist's waypoint-sharing target
                    // flapping <-62.06> ↔ <-124.82> in lockstep).
                    //
                    // Fix: if wpStack is intact, just Resume() so the leader
                    // continues toward its current wpTop (e.g., the runtime
                    // detour) — preserving forward progress through the
                    // blacklist. PublishPatrolWaypoint refreshes the broadcast
                    // so the assist re-syncs. Fall back to the original
                    // ClearAllRoutes + RefillWaypoints only if wpStack is
                    // genuinely empty (no progress to preserve, e.g. after a
                    // route completion).
                    navigation.ClearAllRoutes();
                    if (navigation.HasWaypoint())
                    {
                        navigation.Resume();
                        PublishPatrolWaypoint();
                    }
                    else
                    {
                        RefillWaypoints(false);
                    }

                    // Re-enable the side target-finding thread now that we're
                    // resuming patrol. _pausedByAssistDistance was just cleared
                    // above, so the helper's pause-flag gate passes; the
                    // suppression-window gate is applied identically to
                    // Resume() line 340.
                    TryEnableSideTargetFinding();
                }

                if (_pausedByAssistDistance && !_assistReturnActive)
                {
                    wait.Update();
                    return;
                }
            }
        }

        // ── Combat target gate ─────────────────────────────────────────────
        // wantNavPaused must reject targets that are in playerReader.IsIgnored,
        // not just targetBlacklist (the zone-rect blacklist). They are different
        // sets: targetBlacklist gates region-based avoidance; IsIgnored gates
        // per-mob blacklisting via evade dispatches (HandleGoapEvent's session-25
        // Fix 1, ATG/CombatGoal/PTG real-evade sites, and the agent-level diff
        // loop on the assist).
        //
        // Without the IsIgnored filter, the leader pauses navigation on a mob
        // that was just blacklisted — defeating the entire point of the 25 s
        // evade-recovery window, since the leader can't retreat and the mob
        // keeps attacking. Observed in log 26 (leader 23:42:10:687 → 23:42:34:667,
        // 24 s of stationary inside the first window, then 23:42:37:265 →
        // 23:42:46:283, 9 s of stationary inside the second window). Side-thread
        // suppression (Fix 2) was working — zero Tab presses during the windows
        // — but the leader's bits.Target() stayed pointing at guid=7968930
        // throughout (confirmed by the CombatGoal IsIgnored bail at 23:42:34:759
        // seeing the same guid from before the evade), so wantNavPaused was
        // being driven by a blacklisted target.
        //
        // After this filter:
        //   wantNavPaused = false (target is in IsIgnored)
        //   → "Target did not meet requirements" branch at line 765 fires:
        //     - PressClearTarget (re-press, in case the previous one was lost)
        //     - targetFinder.Reset
        //     - sideActivityManualReset.Set (harmless during suppression because
        //       Fix 2a's targetFinder.DisableUntil makes Search() return false)
        //   → !wantNavPaused at line 820 lets navigation.Update run
        //   → leader physically retreats along the patrol route
        bool wantNavPaused = bits.Target() && bits.Target_Hostile()
            && bits.Target_Alive() && !bits.Target_Tagged() && playerReader.WithInCombatRange()
            && !targetBlacklist.Is()
            && !playerReader.IsIgnored(playerReader.TargetGuid);

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
            targetFinder.Reset();
            sideActivityManualReset.Set();

            if (_pausedByLocalLogic)
            {
                navigation.Resume();
                _pausedByLocalLogic = false;
            }

            Interlocked.Exchange(ref _pauseNavRequested, 0);
        }

        if (!wantNavPaused && _pausedByLocalLogic)
        {
            navigation.Resume();
            _pausedByLocalLogic = false;
            // Re-enable the target finder. This handles the case where a target
            // disappeared completely (bits.Target()=false) while _pausedByLocalLogic
            // was true — the "target did not meet requirements" block above only fires
            // when bits.Target() is true, so without this Set() the side thread would
            // stay paused indefinitely. This is the specific scenario the broad watchdog
            // was masking; fixing it here is safe because it only fires when
            // !wantNavPaused (no live target warrants a pause).
            sideActivityManualReset.Set();
        }

        // ── Assist return state machine ─────────────────────────────────────
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
                    AssistState? cantFollow = assistStateStore.GetCantFollowState();
                    if (cantFollow != null)
                    {
                        logger.LogInformation($"[FRG] AssistReturn rewind reached. Retrying assist target.");
                        navigation.SetSingleWaypoint(cantFollow.MapPosNoZ);
                    }
                }
            }
        }

        if (!wantNavPaused)
            navigation.Update(CancellationToken.None);

        if (bits.Combat() && classConfig.Mode != Mode.AttendedGather)
            return;

        RandomJump();
        wait.Update();
    }

    private bool IsDuplicateRecentRefill(Vector3 topMapPoint, int waypointCount)
    {
        if (_lastRefillWaypointCount != waypointCount) return false;
        if (_lastRefillTopMap == default) return false;
        float d = _lastRefillTopMap.MapDistanceXYTo(topMapPoint);
        if (d > 0.01f) return false;
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
        if (!bits.Target()) return;
        _suppressedBlacklistedGuid = playerReader.TargetGuid;
        _suppressedBlacklistedUntilUtc = DateTime.UtcNow.AddMilliseconds(1200);
    }

    public void SuppressTargetFinderBriefly(int durationMs)
    {
        _suppressTargetFinderUntilUtc = DateTime.UtcNow.AddMilliseconds(durationMs);

        // Two layers of defence:
        //
        // 1. ManualResetEventSlim.Reset() — blocks the side thread on its NEXT
        //    Wait() call. Insufficient on its own: if Wait() already returned
        //    before the Reset was issued (HTTP thread runs HandleGoapEvent
        //    while the side thread is mid-iteration), the in-flight Search()
        //    continues, presses Tab, and acquires whatever's nearest.
        //
        // 2. targetFinder.DisableUntil(utc) — the next Search() call short-
        //    circuits inside its own IsTargetFinderDisabled() check
        //    (TargetFinder.cs:94) and returns false BEFORE pressing Tab. This
        //    is the layer that actually stops the in-flight case once the
        //    side thread loops back to the top of its Search(). For the
        //    extreme race where Search is already past line 94 and about to
        //    press Tab, the post-Search guard inside Thread_LookingForTarget
        //    catches and discards the result.
        sideActivityManualReset.Reset();
        targetFinder.DisableUntil(_suppressTargetFinderUntilUtc);

        logger.LogInformation($"[FRG] Target finder suppressed for {durationMs}ms after evade-blacklist broadcast.");
    }

    /// <summary>
    /// Re-enable <see cref="Thread_LookingForTarget"/> by signalling
    /// <see cref="sideActivityManualReset"/> if and only if BOTH gates pass:
    /// <list type="number">
    /// <item>No per-target evade-blacklist suppression window is active
    ///       (<see cref="_suppressTargetFinderUntilUtc"/> elapsed or unset).</item>
    /// <item>The leader is not paused waiting for the assist —
    ///       neither <see cref="_pausedByAssistDistance"/> nor
    ///       <see cref="_syncPauseActive"/> is true.</item>
    /// </list>
    /// <para>
    /// Used in place of direct <c>sideActivityManualReset.Set()</c> at every
    /// call site that could fire while the leader is stationary — namely
    /// <see cref="Resume"/>'s top (which runs externally; pause flags may
    /// already be true on entry) and the suppression-window-expiry watchdog
    /// (which runs every <see cref="Update"/> tick regardless of pause state).
    /// </para>
    /// <para>
    /// Pause-ENTRY transitions still need an explicit
    /// <c>sideActivityManualReset.Reset()</c> to disable an already-running
    /// side thread; this helper covers re-enabling only.
    /// </para>
    /// </summary>
    private void TryEnableSideTargetFinding()
    {
        if (_suppressTargetFinderUntilUtc != DateTime.MinValue &&
            DateTime.UtcNow < _suppressTargetFinderUntilUtc)
            return;

        if (_pausedByAssistDistance || _syncPauseActive)
            return;

        // Fix 32 — also gate on AnyAssistCantFollow. The pause-for-assist
        // block at line ~870 has a `!insideBlacklist` clause on its
        // shouldPause expression, so when the leader is inside a BL rect
        // AND an assist is CantFollow, the pause block is skipped:
        // _pausedByAssistDistance stays false. Without this gate, the
        // side target-finding thread would resume while the leader is
        // walking out of the rect via Navigation's escape-first, and
        // could acquire a target mid-escape — pulling the leader into
        // combat instead of completing the escape. Per user direction
        // during rescue mode: "neither should be actively looking for
        // targets to fight."
        //
        // Self-defense remains unaffected: it does not depend on the
        // side target-finding thread (which is for proactive Tab-target
        // acquisition). Fix 17/22/26's override path keys off the
        // already-targeted attacker hitting the bot (TargetTarget=Me),
        // independent of this thread's state.
        if (assistStateStore.AnyAssistCantFollow())
            return;

        sideActivityManualReset.Set();
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

            // ── Fix CY: brief plan-flicker guard ──
            // See the FrgSideThreadOnEnterGraceMs constant block (~line 138)
            // for full evidence and rationale. Summary: skip targetFinder.Search
            // if FRG.OnEnter fired within FrgSideThreadOnEnterGraceMs. Prevents
            // brief ATG→FRG→ATG planner micro-flickers (32ms observed) from
            // letting the side thread press Tab and swap the leader's target
            // mid-approach.
            double sinceOnEnterMs =
                (DateTime.UtcNow - onEnterTime).TotalMilliseconds;
            if (sinceOnEnterMs < FrgSideThreadOnEnterGraceMs)
            {
                if (!_cyGraceLogged)
                {
                    logger.LogInformation(
                        "[FRG] [FIX-FIRE] CY: side-thread Search skipped " +
                        "({0:0}ms since FRG.OnEnter < {1:0}ms grace). Prevents " +
                        "Tab press during brief plan-flicker FRG runs that would " +
                        "otherwise swap the leader's approach target.",
                        sinceOnEnterMs, FrgSideThreadOnEnterGraceMs);
                    _cyGraceLogged = true;
                }
                wait.Update();
                continue;
            }
            // Out of grace — reset the throttle so the next OnEnter logs again.
            _cyGraceLogged = false;

            if (pathSettings.CanRunSideActivity() &&
                targetFinder.Search(NpcNameToFind, bits.Target_NotDead, sideActivityCts.Token))
            {
                // Belt-and-suspenders for the extreme race: SuppressTargetFinderBriefly
                // was called by HandleGoapEvent on a different thread WHILE this Search()
                // was already past TargetFinder.cs:94's IsTargetFinderDisabled() check
                // and pressed Tab. If that happened, discard the result — do not let
                // the existing branches below queue _pauseNavRequested, since that would
                // leave wantNavPaused=true on subsequent FRG.Update ticks and stall
                // navigation for the rest of the suppression window.
                //
                // Observed in log 25:
                //   23:01:23:458  HandleGoapEvent → SuppressTargetFinderBriefly
                //                  → ManualResetEventSlim.Reset() (no effect on
                //                    in-flight thread) + DisableUntil (now also added,
                //                    but thread was already past the Search check)
                //   23:01:23:731  Tab pressed (273 ms later, mid-Search)
                //   23:01:23:732  "Found target!" → _pauseNavRequested=1
                //   23:01:23:xxx  navigation paused, wantNavPaused=true 25 s
                //
                // After this guard, the same race produces:
                //   "[FRG] Side thread acquired target during suppression — discarding."
                //   ClearTarget pressed, _pauseNavRequested NOT queued, navigation
                //   continues per FRG's normal patrol logic.
                if (_suppressTargetFinderUntilUtc != DateTime.MinValue &&
                    DateTime.UtcNow < _suppressTargetFinderUntilUtc)
                {
                    logger.LogInformation(
                        "[FRG] Side thread acquired target during suppression — discarding.");
                    if (bits.Target())
                    {
                        input.PressClearTarget();
                        wait.Update();
                    }
                    targetFinder.Reset();
                    sideActivityManualReset.Reset();
                    wait.Update();
                    continue;
                }

                // ── Edit 1: pre-engagement geometric in-rect filter ──
                //
                // Tab finder acquired a mob whose position is inside a static
                // route blacklist rect. Per operator principle (see commit
                // history): we never engage mobs in blacklist areas via the
                // finder, and we should not blacklist mobs we haven't actively
                // engaged. So: cycle Tab a few times to try other nearby
                // hostiles, and only if all the local hostiles are in-rect do
                // we fall back to briefly suppressing the finder so the patrol
                // can advance past the cluster.
                //
                // Crucially: no IsIgnored mutation, no SendGoapEvent — the
                // mob never enters either bot's blacklist list. If/when it
                // walks out into open terrain and attacks us, regular Combat
                // engages it; if we're attacking it in the rect later (e.g.,
                // self-defense while declaredStuck), the engageAllowedForJoin
                // gate at the combat-side decision points handles it.
                //
                // Distinct from the Fix(run-143) block below: that one is the
                // backstop for mobs that did get IsNoEngage-flagged via some
                // other path. With Edit 1 catching first-encounter via the
                // geometric check, the IsNoEngage flag should almost never be
                // set anymore — but the block stays as defense-in-depth.
                if (bits.Target() && navigation.IsTargetLikelyInBlacklistRect())
                {
                    const int MaxRectCycleAttempts = 3;
                    int cycleAttempts = 0;
                    while (bits.Target()
                        && navigation.IsTargetLikelyInBlacklistRect()
                        && cycleAttempts < MaxRectCycleAttempts
                        && !sideActivityCts.IsCancellationRequested)
                    {
                        cycleAttempts++;
                        logger.LogInformation(
                            $"[FRG] Tab acquired in-rect mob guid={playerReader.TargetGuid} " +
                            $"(cycle {cycleAttempts}/{MaxRectCycleAttempts}) — pressing " +
                            "Tab to cycle to next target.");

                        while (input.TargetNearestTarget.OnCooldown()
                            && !sideActivityCts.IsCancellationRequested)
                            wait.Update();

                        int beforeTabGuid = playerReader.TargetGuid;
                        input.PressNearestTarget(sideActivityCts.Token);

                        // Throttle: wait up to 300 ms for the addon to reflect
                        // the target change. Matches the 200 ms throttle inside
                        // TargetFinder.LookForTarget — Tab press → game state
                        // update → addon read → AddonBits/PlayerReader refresh
                        // is not guaranteed within a single wait.Update() tick.
                        wait.Till(300, () =>
                            playerReader.TargetGuid != beforeTabGuid
                            || sideActivityCts.IsCancellationRequested);
                    }

                    if (bits.Target() && navigation.IsTargetLikelyInBlacklistRect())
                    {
                        Log($"[FRG] {MaxRectCycleAttempts} cycle attempts all " +
                            "in-rect — clearing target and suppressing finder " +
                            $"{FrgNoEngageSearchSuppressMs}ms so patrol can advance " +
                            "past the cluster (not blacklisting — we never engaged).");
                        input.PressClearTarget();
                        wait.Update();
                        targetFinder.Reset();
                        SuppressTargetFinderBriefly(FrgNoEngageSearchSuppressMs);
                        sideActivityManualReset.Reset();
                        wait.Update();
                        continue;
                    }
                    // Cycle landed on a non-rect target — fall through to the
                    // existing actionable / IsNoEngage / etc. branches below.
                }

                // Fix (run-143): NO-ENGAGE mob re-acquired while patrolling.
                // A no-engage mob lives in a route blacklist rect; self-defense
                // is intentionally suppressed for it (the !IsNoEngage gate in
                // GoapAgent/CombatGoal), so the self-defense paths below would
                // pause nav uselessly, and the plain clear path lets the side
                // thread immediately re-Tab it next tick — the run-143 churn.
                // Instead: clear and disable the finder for a short window so the
                // patrol advances past the rect rather than thrashing on the
                // in-rect mobs. Scoped to IsNoEngage ONLY — regular IsIgnored
                // mobs (which may warrant self-defense) keep their paths below.
                if (bits.Target()
                    && playerReader.IsNoEngage(playerReader.TargetGuid))
                {
                    Log("[FRG] No-engage mob re-acquired while patrolling — " +
                        "clearing and suppressing side-thread search " +
                        $"{FrgNoEngageSearchSuppressMs}ms so patrol can advance " +
                        "past the blacklist rect (not engaging; not pausing nav).");
                    input.PressClearTarget();
                    wait.Update();
                    targetFinder.Reset();
                    SuppressTargetFinderBriefly(FrgNoEngageSearchSuppressMs);
                    continue;
                }

                if (bits.Target() && bits.TargetTarget_PlayerOrPet()
                    && playerReader.IsIgnored(playerReader.TargetGuid)
                    && (bits.Combat() || bits.Focus_Combat()))
                {
                    Log("Found area blacklisted target targeting us in combat!");
                    sideActivityManualReset.Reset();
                    targetFinder.Reset();
                    Interlocked.Exchange(ref _pauseNavRequested, 1);
                }
                else if (bits.Target() && (targetBlacklist.Is() || playerReader.IsIgnored(playerReader.TargetGuid)))
                {
                    // Fix R (log-62 18:20:27:604 onwards): WoW UI TargetTarget
                    // propagation lag race. When the side thread's Tab acquires
                    // a hostile IsIgnored mob that's actively attacking the bot,
                    // bits.TargetTarget_PlayerOrPet() may not yet reflect Me/Pet
                    // within the same tick (UnitTargetOfUnit lags Tab by 30-100+
                    // ms, sometimes longer). Path 1 above (line ~1416) requires
                    // it, so we fall here, ClearTarget runs, hasTarget→false,
                    // and Fix 17 self-defense in GoapAgent/CombatGoal (which
                    // require hasTarget=true) cannot fire. The mob keeps hitting,
                    // auto-target re-acquires, Tab finds it again, same loop.
                    //
                    // Observed in log-62: leader's evade recovery elapsed at
                    // 18:20:27:386, 340309 (the previously-blacklisted Deepmoss
                    // Venomspitter) reappeared and started attacking at
                    // 18:20:27:604. Three "Blacklist Target" plans fired in
                    // succession (27:605, 28:056, 29:331), each one Tab-ing
                    // 340309 and ClearTarget-ing via this Path 2. Fix 17 didn't
                    // engage until 18:20:32:022 — 4.4 seconds of damage exposure
                    // during which the leader took hits with no ability to
                    // defend. The kill eventually happened (inside BL, via
                    // Fix 17 self-defense at 18:20:43:406), but the delay is
                    // unsafe in scenarios with higher mob damage, multiple
                    // attackers, or lower starting HP.
                    //
                    // Fix: in party mode, when in combat AND the target is on
                    // IsIgnored (but not also on permanent targetBlacklist —
                    // permanent blacklist still wins, no Fix 17 path for it),
                    // wait up to 200ms for TargetTarget to propagate. 200ms is
                    // 6-12 frames at 30-60 fps — generous enough for UI state
                    // propagation. If TargetTarget becomes PlayerOrPet within
                    // that window, fall through to Path 1's behavior (pause
                    // nav, let Combat plan fire so Fix 17 engages). If it
                    // doesn't, clear as before — preserves original "drive-by
                    // clear" semantics for genuine non-attacking BL mobs the
                    // bot stumbles past while patrolling.
                    //
                    // Standalone Grind mode unaffected (isPartyMode short-
                    // circuits). targetBlacklist.Is() case unaffected (we only
                    // defer when IsIgnored is the trigger and permanent
                    // blacklist is NOT active — if both, blacklist wins via
                    // the !targetBlacklist.Is() conjunct).
                    bool isPartyMode = classConfig.Mode == Mode.PartyLeader
                                    || classConfig.Mode == Mode.AssistFocus;
                    if (isPartyMode
                        && bits.Combat()
                        && playerReader.IsIgnored(playerReader.TargetGuid)
                        && !targetBlacklist.Is())
                    {
                        wait.Till(200, () => bits.TargetTarget_PlayerOrPet());
                        if (bits.TargetTarget_PlayerOrPet())
                        {
                            Log("Fix R: Blacklisted target attacking us in combat (detected after TargetTarget propagation) — pausing nav so Fix 17 self-defense can fire.");
                            sideActivityManualReset.Reset();
                            targetFinder.Reset();
                            Interlocked.Exchange(ref _pauseNavRequested, 1);
                            continue;
                        }
                        // Fall through to clear if TargetTarget never propagated
                        // to PlayerOrPet within 200ms — genuine "BL mob nearby,
                        // not attacking us" scenario, original Path 2 behavior
                        // is correct.
                        Log("Fix R: Blacklisted target found in combat but TargetTarget did not propagate to PlayerOrPet within 200ms — falling through to clear (not a self-defense scenario).");
                    }

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
                    else
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
                AlternateGatherTypes();
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
            logger.LogInformation($"[{oldestKey.Key}] {oldestKey.Name} pressed");
            input.PressRandom(oldestKey);
            oldestKey.SetClicked();
        }
    }

    private void TickAssistReturnTimeout(bool wantNavPaused)
    {
        if (!_assistReturnActive) return;
        var now = DateTime.UtcNow;

        if (!_assistReturnTimerInit)
        {
            _assistReturnTimerInit = true;
            _assistReturnLastTickUtc = now;
            _assistReturnActiveElapsed = TimeSpan.Zero;
            return;
        }

        bool countActive = !wantNavPaused && !_pausedByLocalLogic && !bits.Combat() && !bits.Focus_Combat();
        if (countActive)
            _assistReturnActiveElapsed += now - _assistReturnLastTickUtc;

        _assistReturnLastTickUtc = now;

        if (_assistReturnActiveElapsed.TotalSeconds >= ASSIST_RETURN_TIMEOUT_ACTIVE_SEC)
            AbortAssistReturn($"timeout (active={_assistReturnActiveElapsed.TotalSeconds:0.0}s)");
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
        if (!_assistReturnActive) return;
        logger.LogWarning($"[FRG] AssistReturn abort: {reason}");
        ClearAssistReturnState();
        navigation.ClearAllRoutes();
    }

    private void ClearAssistReturnState()
    {
        _assistReturnActive = false;
        _assistRewindActive = false;
        _assistWaitingForFollowing = false;
        _pausedByAssistDistance = false;
        leaderNavProvider.SetPausedForAssist(false); // Fix T: keep flag in sync with field
        _assistAttempt = 0;
        _assistReturnTargetW = default;
        _assistRewindAnchorW = default;
        _assistReturnActiveElapsed = TimeSpan.Zero;
        _assistReturnLastTickUtc = DateTime.UtcNow;
        _assistReturnTimerInit = false;
    }

    private void Navigation_OnPathFailed(Vector3 startW, Vector3 endW)
    {
        if (!_assistReturnActive) return;
        if (endW.WorldDistanceXYTo(_assistReturnTargetW) > 3.0f) return;

        if (_assistAttempt >= 1)
        {
            AbortAssistReturn("Path failed after rewind retry");
            return;
        }

        if (!navigation.HasLastSafeAnchor)
        {
            AbortAssistReturn("No last safe anchor for rewind");
            return;
        }

        var anchor = navigation.LastSafeAnchorW;
        if (navigation.AreaBlacklist != null && navigation.AreaBlacklist.ContainsWorld(anchor))
        {
            AbortAssistReturn("Last safe anchor inside blacklist");
            return;
        }

        _assistAttempt = 1;
        _assistRewindActive = true;
        _assistRewindAnchorW = anchor;
        logger.LogWarning($"[FRG] AssistReturn path failed. Rewind to anchor={anchor}");
        navigation.SetSingleWaypoint(anchor);
    }

    private void MountIfPossible()
    {
        float totalDistance = VectorExt.TotalDistance<Vector3>(navigation.TotalRoute, VectorExt.WorldDistanceXY);
        if (classConfig.UseMount && mountHandler.CanMount() &&
            (MountHandler.ShouldMount(totalDistance) ||
            (navigation.TotalRoute.Length > 0 && mountHandler.ShouldMount(navigation.TotalRoute[^1]))))
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
        if (debug) LogDebug("Navigation_OnDestinationReached");

        if (classConfig.Mode == Mode.PartyLeader && (_assistReturnActive || _assistRewindActive))
        {
            logger.LogInformation(
                $"[FRG] AssistReturn destination reached. " +
                $"AssistIsFollowing={assistStateStore.AnyAssistIsFollowing()} " +
                $"AssistCantFollow={assistStateStore.AnyAssistCantFollow()}");

            ClearAssistReturnState();

            if (assistStateStore.AnyAssistIsFollowing())
            {
                logger.LogInformation("[FRG] AssistReturn reached — assist already following, resuming patrol.");
                // Suppress the endpoint direction-forcing in the next RefillWaypoints(false)
                // call. The leader was brought here by AssistReturn, not by completing a
                // natural patrol leg, so reaching a route endpoint here must NOT reverse
                // _pathTraversalDirection.
                _suppressDirectionEndpointForcing = pathSettings.PathThereAndBack;
                navigation.ClearAllRoutes();
                Resume();
                return;
            }

            _assistWaitingForFollowing = true;
            navigation.PausePathing();
            logger.LogInformation("[FRG] AssistReturn reached — pausing until assist reports Following.");
            return;
        }

        RefillWaypoints(false);
        MountIfPossible();
    }

    private void Navigation_OnWayPointReached()
    {
        // Publish the NEW top waypoint so assist bots know the leader's next target.
        // Only during normal patrol — not during AssistReturn (GoToOneWaypoint), which
        // navigates to the assist's position and should not override the patrol waypoint.
        //
        // Use TopPublishableWaypointW (not TopWaypointW) — log-35 14:08:16:226 case:
        // the just-popped-to top can be inside a blacklist; SkipBlacklistedWaypoints
        // will remove it later in the same Update tick at line 759, but only after
        // this event has already fired. Publishing the unfiltered top broadcasts a
        // transient bad waypoint to the API, where the assist polls it ~600ms later
        // and immediately enters an infinite SetSingleWaypoint→OnDestinationReached
        // loop because the assist's own SkipBlacklistedWaypoints pops it on every
        // Update tick. TopPublishableWaypointW walks the stack top-down and returns
        // the first non-blacklisted entry, matching what the leader will navigate
        // to once SkipBlacklistedWaypoints runs.
        if (classConfig.Mode == Mode.PartyLeader && !_assistReturnActive)
        {
            Vector3 nextWp = navigation.TopPublishableWaypointW;
            if (nextWp != default)
                leaderNavProvider.SetTargetWaypoint(nextWp);
            else
                leaderNavProvider.ClearTargetWaypoint(); // all waypoints exhausted/blacklisted — route will wrap
        }

        MountIfPossible();

        // ────────────────────────────────────────────────────────────────────
        // Fix BF (log-91 17:54:11→17:54:52, log-92 18:59:55→19:00:50):
        // pause-for-assist check, MOVED here from Update() per Option D.
        //
        // Firing at waypoint arrival guarantees that when the leader pauses,
        // it pauses ON-ROUTE at a route waypoint it just reached. The assist's
        // _cachedLeaderRouteIdx then matches the leader's physical position
        // (not "where the leader was heading"), eliminating the stale-cache
        // asymmetry that drove both observed oscillations.
        //
        // Trade-off vs the previous per-tick check: pause activation can
        // overshoot by up to one route stride (~10y) — the bot walks an
        // extra waypoint before noticing. Combined with LeaderPauseYards=25y
        // (raised from 20y), worst-case actual pause distance is ~35y, still
        // comfortably within healer range. This is fine because the urgent
        // cases (CantFollow, Stuck) are also handled by the
        // OnGoapEvent.assistrequestreturn / AssistReturn event-driven path,
        // which is not throttled by this per-waypoint cadence.
        //
        // The blacklist-escape resume, the assist-following resume, and the
        // pause early-return remain in Update() — those are urgent or need
        // every-tick evaluation while paused.
        //
        // Fix 25 interlock preserved: only enter pause-SET when
        // !_assistReturnActive (AssistReturn nav must not be killed by
        // PausePathing). Same condition as the original Update-site check.
        // ────────────────────────────────────────────────────────────────────
        if (classConfig.Mode != Mode.PartyLeader)
            return;
        if (_pausedByAssistDistance)
            return; // already paused
        if (_assistReturnActive)
            return; // Fix 25: don't kill AssistReturn nav

        // ── Fix BG (log-93 00:03:54:288 incident) ────────────────────────
        // The Fix BF pause-SET should ONLY fire when the bot just popped one
        // of MANY route waypoints (normal patrol progress). When the popped
        // waypoint was the LAST in the stack, three pathological things
        // happen at once:
        //
        // 1. The bot likely isn't AT a route waypoint at all — it's at
        //    an orphaned single-waypoint target left by AssistReturn,
        //    RouteEscape, AssistRewind, or another sub-navigation that
        //    completed/aborted. Log-93 example: AssistReturn aborted at
        //    00:03:52:482 leaving the AssistReturn target <-732.11,-4355.06>
        //    in the stack. At 00:03:54:288 the bot reached that orphan,
        //    popped it, and my Fix BF handler fired at the non-route
        //    position <-729.15,-4356.54>. Pause-at-waypoint's invariant
        //    ("pause only at route waypoints") was broken.
        //
        // 2. After OnWayPointReached fires here, Navigation.cs:991-994
        //    will call CompleteDestinationReached() (because
        //    wayPoints.Count == 0). CompleteDestinationReached fires
        //    OnDestinationReached, which FRG handles by calling
        //    RefillWaypoints(false). RefillWaypoints calls
        //    SetWayPoints(count=N), which sets active=true again — UNDOING
        //    the PausePathing we just did. Log-93 evidence: the second
        //    pause's diagnostic shows "SetWayPoints(count=118) wasActive=
        //    False ... (was paused, re-Acquired)" within the same
        //    millisecond as the PausePathing call.
        //
        // 3. The leader's TargetWaypoint cache is mid-transition (just
        //    popped, about to be refilled), so even the pause's
        //    Status=Waiting broadcast doesn't carry useful route-context
        //    for the assist.
        //
        // The correct behavior: don't pause here. Let CompleteDestination-
        // Reached → RefillWaypoints chain run normally, refilling the
        // patrol stack. The bot starts walking the new batch. At the FIRST
        // pop of the new batch (a real route waypoint, with count > 0
        // after), this handler fires again and the pause-SET takes hold
        // correctly.
        if (navigation.WaypointCount == 0)
            return;
        // ──────────────────────────────────────────────────────────────────

        // Recompute inOrNearBlacklist locally (was computed in Update upstream).
        // Suppresses pause when the leader is inside or near a blacklist rect
        // so the existing escape logic in Navigation.RefillWaypoints can fire.
        bool insideBlacklistHere = navigation.AreaBlacklist != null
            && navigation.AreaBlacklist.ContainsWorld(playerReader.WorldPos);
        const float NearBLEdgeBufferYards_BF = 6f;
        bool nearBlacklistEdgeHere = !insideBlacklistHere
            && navigation.AreaBlacklist != null
            && navigation.AreaBlacklist.TryGetContainingRectInflated(
                playerReader.WorldPos, NearBLEdgeBufferYards_BF, out _);
        // Fix FM (run-162): also skip pause-SET when the leader is in active
        // self-defense vs an IsIgnored target. Casters inside a BL rect can
        // hit the leader from beyond the 6 y near-BL buffer; setting the
        // pause here would strand the leader in the caster's range until
        // combat ends some other way. Mirror the Update-path Fix FM logic
        // at line ~1217 — same conditions, same intent (retreat priority
        // over pause-for-assist). See that block for the full rationale.
        bool inSelfDefenseVsIsIgnoredHere =
            bits.Target() &&
            targetBlacklist.Is() &&
            bits.Combat() &&
            (playerReader.TargetTarget is UnitsTarget.Me
                                      or UnitsTarget.Pet
                                      or UnitsTarget.PartyOrPet);
        if (insideBlacklistHere || nearBlacklistEdgeHere || inSelfDefenseVsIsIgnoredHere)
            return; // let the leader escape the blacklist / retreat from in-rect caster

        bool shouldPauseHere = assistStateStore.ShouldLeaderPauseForAssist(
            playerReader.WorldPos, LeaderPauseYards);
        if (!shouldPauseHere)
            return;

        // ── Pause-SET (the body moved from Update lines 973-1083 in the
        // pre-Fix-BF version) ───────────────────────────────────────────
        float distHere = assistStateStore.GetNearestAssistDistanceYards(playerReader.WorldPos);
        AssistState? stuckAssistHere = assistStateStore.GetCantFollowState()
            ?? assistStateStore.GetAll().FirstOrDefault(
                a => !assistStateStore.IsStale(a) && a.Status == BotStatus.Stuck);

        logger.LogInformation(
            $"[FRG] Pausing for assist (at waypoint) — dist={distHere:0.0}y " +
            $"status={stuckAssistHere?.Status.ToString() ?? "TooFar"}");

        // Diagnostic detail: print the inputs that produced `distHere`,
        // so we can distinguish "leader genuinely > 25y away from assist"
        // from "leader is reading a stale assist snapshot". Added after
        // log-30 (per the original block's comment).
        Vector3 leaderPosHere = playerReader.WorldPos;
        logger.LogInformation(
            $"[FRG] Pause-detail: leaderPos=<{leaderPosHere.X:F2},{leaderPosHere.Y:F2},{leaderPosHere.Z:F2}> " +
            $"pauseYards={LeaderPauseYards}");
        foreach (AssistState s in assistStateStore.GetAll())
        {
            float d = leaderPosHere.WorldDistanceXYTo(s.WorldPos);
            logger.LogInformation(
                $"[FRG] Pause-detail: assist id='{s.AssistId}' " +
                $"pos=<{s.WorldX:F2},{s.WorldY:F2},{s.WorldZ:F2}> " +
                $"status={s.Status} cantFollow={s.CantFollow} " +
                $"routeIdx={s.AssistRouteIndex} " +    // Fix BF: published route progress
                $"dist={d:F2}y ageMs={s.AgeMs:F0} stale={assistStateStore.IsStale(s)}");
        }

        _pausedByAssistDistance = true;
        // Fix T preserved: broadcast pause-for-assist via LeaderNavigationProvider.
        // Switches leader.Status to Waiting so the assist's FFG can engage its
        // route-walking-during-Waiting path (Fix BF, FFG line ~4093).
        leaderNavProvider.SetPausedForAssist(true);
        // StopMovement must be called before PausePathing — see original
        // block comment for the rationale (forward key handling).
        navigation.StopMovement();
        navigation.PausePathing();

        // Disable the side target-finding thread on this transition.
        // Single-shot, gated by _pausedByAssistDistance for symmetric re-enable.
        sideActivityManualReset.Reset();
        targetFinder.Reset();

        // Fix 32 design (preserved from the original block): the leader STAYS
        // PUT when assist is CantFollow. Inside-BL escape is handled by the
        // `inOrNearBlacklist` early-return above. Combat self-defense via
        // Fix 17/22/26 still works. Resume flows through the Update-side
        // ELSE IF (`!shouldPause && shouldResume && _pausedByAssistDistance`).
    }

    /// <summary>
    /// Fix S (log-63 19:46:56:993): handler for the non-pop wpTop change event,
    /// currently fired only from <see cref="GoalsComponent.Navigation.TryInsertDetour"/>.
    /// Re-publishes <c>TopPublishableWaypointW</c> so the assist's
    /// waypoint-sharing target tracks the actual top after a detour has been
    /// pushed onto the stack, not the original (now-buried) target that the
    /// detour was inserted to bypass.
    /// <para>
    /// Same publish logic as <see cref="Navigation_OnWayPointReached"/> minus
    /// <c>MountIfPossible()</c> — a detour insertion is not "I reached a
    /// waypoint", it's "I just rerouted around an obstacle", and mounting at
    /// that moment would be incorrect (the bot is mid-recovery, not at a
    /// natural pause).
    /// </para>
    /// </summary>
    private void Navigation_OnTopWaypointChanged()
    {
        if (classConfig.Mode == Mode.PartyLeader && !_assistReturnActive)
        {
            Vector3 nextWp = navigation.TopPublishableWaypointW;
            if (nextWp != default)
                leaderNavProvider.SetTargetWaypoint(nextWp);
            else
                leaderNavProvider.ClearTargetWaypoint();
        }
    }

    public void ClearWaypoints()
    {
        logger.LogInformation("FollowRouteGoal: ClearWaypoints!");
        navigation.SetWayPoints(stackalloc Vector3[1] { playerReader.MapPos });
    }

    public void GoToOneWaypoint(Vector3 waypointToGoTo)
    {
        logger.LogInformation($"FollowRouteGoal: GoToOneWaypoint → {waypointToGoTo}");

        if (classConfig.Mode == Mode.PartyLeader && assistStateStore.AnyAssistCantFollow())
        {
            // Fix 33 (log-52 17:38:42 → end-of-log, leader paused 80 s in
            // _assistWaitingForFollowing state with destination 22 y from
            // actual assist), user direction A+B+C:
            //
            // (A) Target the actual assist position. The previous Fix 9
            //     projection landed on the leader-side edge of the BL
            //     rect — geometrically incapable of satisfying the
            //     assist's CantFollow leader-arrived check (FFG line
            //     1053: dist <= LeaderArrivedYards=6 y) when the rect is
            //     wider than ~12 y, because the projection moves AWAY
            //     from the assist. New rule: if the assist's recorded
            //     position is inside a BL rect, project to the rect
            //     boundary CLOSEST TO THE ASSIST (with 0.5 y outward
            //     margin so SkipBlacklistedWaypoints leaves it alone),
            //     not toward the leader. For typical rects with the
            //     assist near an edge this lands within ~5.5 y of the
            //     assist, triggering the leader-arrived check on
            //     arrival. For deep-in-large-rect cases the projection
            //     may still be > 6 y from the assist; (B) handles that
            //     case via the pause-for-assist fallback after timeout.
            //
            // (B) One try per CantFollow event. Already in place via
            //     Fix 32: GoapAgent.cs:322-360 fires GoToOneWaypoint
            //     exactly once per previousAssistCantFollow →
            //     currentAssistCantFollow transition (not per tick
            //     while the assist persistently CantFollows). When
            //     AbortAssistReturn fires (TickAssistReturnTimeout at
            //     25 s, OnPathFailed after one rewind retry, etc.),
            //     ClearAssistReturnState sets _assistReturnActive=false
            //     and the next FRG.Update tick enters the pause-for-
            //     assist branch at line 891 (shouldPause &&
            //     !_pausedByAssistDistance && !_assistReturnActive) —
            //     leader holds position until the assist exits
            //     CantFollow. The old retry-loop failure mode from
            //     log-52 (80 s of repeated AssistReturn cycles after
            //     each timeout) cannot recur because nothing here
            //     re-triggers a fresh GoToOneWaypoint until the assist
            //     formally exits and re-enters CantFollow.
            //
            // (C) Segment-clear visibility. With (A) producing a
            //     reachable target, the pather handles segment-clear
            //     and segment-blocked cases uniformly — clear → direct
            //     route, blocked → detour insertion (Fix 29 + Fix H
            //     handle persistent failures). No separate code path
            //     needed. Adding a diagnostic log line so future log
            //     analysis can distinguish the two cases at a glance.
            Vector3 leaderW = playerReader.WorldPos;
            Vector3 targetW = IsLikelyMapPoint(waypointToGoTo)
                ? WorldMapAreaDB.ToWorld_FlipXY(waypointToGoTo, playerReader.WorldMapArea)
                : waypointToGoTo;

            if (navigation.AreaBlacklist != null
                && navigation.AreaBlacklist.TryGetContainingRect(targetW, out var rect))
            {
                // Closest rect boundary to assist's actual position. Use
                // 0.5 y outward margin so ContainsWorld (boundary-
                // exclusive) reliably treats the projection as outside,
                // preventing SkipBlacklistedWaypoints from popping it
                // before the pather computes a route. The Math.Min
                // chain selects the nearest of {west, east, south,
                // north} edges; ties (assist on a corner) resolve in
                // declaration order, which is fine — all four corner
                // boundary points are equidistant.
                float distW = targetW.X - rect.MinX;
                float distE = rect.MaxX - targetW.X;
                float distS = targetW.Y - rect.MinY;
                float distN = rect.MaxY - targetW.Y;
                float minDist = Math.Min(Math.Min(distW, distE), Math.Min(distS, distN));
                const float outwardMargin = 0.5f;

                Vector3 projected;
                if (minDist == distW)
                    projected = new Vector3(rect.MinX - outwardMargin, targetW.Y, 0f);
                else if (minDist == distE)
                    projected = new Vector3(rect.MaxX + outwardMargin, targetW.Y, 0f);
                else if (minDist == distS)
                    projected = new Vector3(targetW.X, rect.MinY - outwardMargin, 0f);
                else
                    projected = new Vector3(targetW.X, rect.MaxY + outwardMargin, 0f);

                float distToAssist = projected.WorldDistanceXYTo(targetW);
                logger.LogInformation(
                    $"[FRG] Fix 33 (A): assist target {targetW} inside rect " +
                    $"({rect.MinX:0.0},{rect.MinY:0.0})-({rect.MaxX:0.0},{rect.MaxY:0.0}); " +
                    $"projecting to closest boundary {projected} " +
                    $"(dist to assist={distToAssist:0.0}y, " +
                    $"leader-arrived threshold=6.0y).");

                targetW = projected;
            }

            // Fix 33 (C): diagnostic log for segment-clear vs blocked.
            bool segmentBlocked = navigation.AreaBlacklist != null
                && navigation.AreaBlacklist.TryGetBlockingRect(leaderW, targetW, out _);
            if (segmentBlocked)
            {
                logger.LogInformation(
                    $"[FRG] Fix 33 (C): leader→target segment crosses a BL rect — " +
                    $"pather will detour around (Fix 29/H handle persistent rejection cycles). " +
                    $"leader={leaderW} target={targetW}.");
            }
            else
            {
                logger.LogInformation(
                    $"[FRG] Fix 33 (C): leader→target segment clear — " +
                    $"pather can route directly. leader={leaderW} target={targetW}.");
            }

            BeginAssistReturn(targetW);
            waypointToGoTo = targetW;
        }
        else
        {
            ClearAssistReturnState();
        }

        ResetRefillWaypointsGuard();
        navigation.SetWayPoints(stackalloc Vector3[1] { waypointToGoTo });
    }

    /// <summary>
    /// Matches the heuristic in <see cref="GoalsComponent.Navigation.SetWayPoints"/>
    /// (line 1374) for distinguishing map coords (0..100 in both X and Y) from
    /// world coords. Local copy so Fix 33's projection can convert map→world before
    /// blacklist checks without exposing Navigation.IsMapPoint publicly.
    /// </summary>
    private static bool IsLikelyMapPoint(Vector3 p)
    {
        return p.X is >= 0 and <= 100 && p.Y is >= 0 and <= 100;
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
        float mapDistanceToLast  = playerMap.MapDistanceXYTo(pathMap[^1]);

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
            if (debug) LogDebug($"{nameof(RefillWaypoints)}: Closest wayPoint: {mapClosestPoint}");
            if (canSkipDuplicateRefill && IsDuplicateRecentRefill(mapClosestPoint, 1))
            {
                Log($"{nameof(RefillWaypoints)} - skipped duplicate recent closest refill");
                return;
            }
            RecordRefillWaypoints(mapClosestPoint, 1);
            navigation.SetWayPoints(stackalloc Vector3[1] { mapClosestPoint });
            PublishPatrolWaypoint();
            return;
        }

        int resumeIndex = closestIndex;
        if (resumeIndex < pathMap.Length - 1)
        {
            float dHere = playerMap.MapDistanceXYTo(pathMap[resumeIndex]);
            float dNext = playerMap.MapDistanceXYTo(pathMap[resumeIndex + 1]);

            // Distance-based: leader is essentially at the closest waypoint, or past
            // the midpoint of the segment to the next one (B is at most 25% farther
            // than A by straight-line distance).
            bool incByDistance = dHere < 1.5f || dNext <= dHere * 1.25f;

            // Projection-based: leader has positive progress along segment A→B (t > 0
            // means the leader's foot of perpendicular onto the segment line lies past
            // A in the direction of B). Catches the case where the leader is mid-
            // segment, slightly closer to A in straight-line distance, but has already
            // traveled past A toward B — incByDistance misses this for long segments
            // (e.g. segment 55y, leader 22y past A but 33y from B → dNext/dHere = 1.52,
            // fails the 1.25 cutoff). Without this projection check, the distance-pause
            // exit at line 813 sets the just-passed waypoint as the new resume target,
            // and PublishPatrolWaypoint broadcasts it to the assist as a regression.
            // Observed at 47:48, 47:56, 48:11 in leader_movement_32.txt.
            //
            // Map coordinates throughout (consistent with dHere/dNext above); sign of t
            // is preserved across the map↔world FlipXY transform so the test result is
            // identical in either coordinate system.
            bool incByProgress = false;
            Vector3 a = pathMap[resumeIndex];
            Vector3 b = pathMap[resumeIndex + 1];
            float abx = b.X - a.X;
            float aby = b.Y - a.Y;
            float abLenSq = abx * abx + aby * aby;
            if (abLenSq > 0.001f)
            {
                float apx = playerMap.X - a.X;
                float apy = playerMap.Y - a.Y;
                float t = (apx * abx + apy * aby) / abLenSq;
                if (t > 0.0f)
                    incByProgress = true;
            }

            if (incByDistance || incByProgress)
                resumeIndex++;
        }

        var wma = playerReader.WorldMapArea;
        Vector3 playerW = playerReader.WorldPos;
        Vector3 ToWorldCoord(Vector3 mapPt) => WorldMapAreaDB.ToWorld_FlipXY(mapPt, wma);

        while (resumeIndex < pathMap.Length - 1)
        {
            if (playerW.WorldDistanceXYTo(ToWorldCoord(pathMap[resumeIndex])) < Navigation.POP_DIST)
            {
                logger.LogWarning(
                    $"[FRG] RefillWaypoints: skipping already-reached resumeIndex={resumeIndex} " +
                    $"dist={playerW.WorldDistanceXYTo(ToWorldCoord(pathMap[resumeIndex])):0.00} < POP_DIST={Navigation.POP_DIST:0.00}");
                resumeIndex++;
            }
            else
                break;
        }

        if (pathSettings.PathThereAndBack)
        {
            if (_pathTraversalDirection == 0)
                _pathTraversalDirection = mapDistanceToFirst <= mapDistanceToLast ? 1 : -1;

            // Only force direction at endpoints when the leader arrived there via normal
            // patrol completion. When AssistReturn placed the leader at the assist's
            // CantFollow position (which may happen to be a route endpoint), the original
            // direction must be preserved — the leader was not actually completing a
            // ThereAndBack leg. _suppressDirectionEndpointForcing is set by both AssistReturn
            // completion paths and consumed exactly once here.
            if (_suppressDirectionEndpointForcing)
            {
                _suppressDirectionEndpointForcing = false;
                logger.LogInformation(
                    $"[FRG] RefillWaypoints: suppressing endpoint direction forcing " +
                    $"(closestIndex={closestIndex}, direction preserved as {_pathTraversalDirection}).");
            }
            else
            {
                if (closestIndex == 0)
                    _pathTraversalDirection = 1;
                else if (closestIndex == pathMap.Length - 1)
                    _pathTraversalDirection = -1;
            }

            if (_pathTraversalDirection > 0)
            {
                Span<Vector3> forwardPoints = pathMap[resumeIndex..];
                if (forwardPoints.Length == 0) return;
                if (canSkipDuplicateRefill && IsDuplicateRecentRefill(forwardPoints[0], forwardPoints.Length))
                {
                    Log($"{nameof(RefillWaypoints)} - skipped duplicate recent forward refill");
                    return;
                }
                RecordRefillWaypoints(forwardPoints[0], forwardPoints.Length);
                Log($"{nameof(RefillWaypoints)} - Set destination from forward resume point - with {forwardPoints.Length} waypoints");
                navigation.SetWayPoints(forwardPoints);
                PublishPatrolWaypoint();
            }
            else
            {
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
                    backwardPoints[i] = pathMap[backwardStartIndex - i];

                if (backwardPoints.Length == 0) return;
                if (canSkipDuplicateRefill && IsDuplicateRecentRefill(backwardPoints[0], backwardPoints.Length))
                {
                    Log($"{nameof(RefillWaypoints)} - skipped duplicate recent backward refill");
                    return;
                }
                RecordRefillWaypoints(backwardPoints[0], backwardPoints.Length);
                Log($"{nameof(RefillWaypoints)} - Set destination from backward resume point - with {backwardPoints.Length} waypoints");
                navigation.SetWayPoints(backwardPoints);
                PublishPatrolWaypoint();
            }
            return;
        }

        Span<Vector3> points = pathMap[resumeIndex..];
        if (points.Length == 0) return;

        if (points.Length == 1)
        {
            float distToOnly = playerW.WorldDistanceXYTo(ToWorldCoord(points[0]));
            if (distToOnly < Navigation.POP_DIST * 2f)
            {
                Log($"{nameof(RefillWaypoints)} - last point reached, wrapping route to start");
                int wrapResumeIndex = 0;
                while (wrapResumeIndex < pathMap.Length - 1 &&
                       playerW.WorldDistanceXYTo(ToWorldCoord(pathMap[wrapResumeIndex])) < Navigation.POP_DIST)
                    wrapResumeIndex++;

                Span<Vector3> wrapPoints = pathMap[wrapResumeIndex..];
                if (wrapPoints.Length == 0) wrapPoints = pathMap;
                RecordRefillWaypoints(wrapPoints[0], wrapPoints.Length);
                Log($"{nameof(RefillWaypoints)} - Set destination from wrap-around index={wrapResumeIndex} - with {wrapPoints.Length} waypoints");
                navigation.SetWayPoints(wrapPoints);
                PublishPatrolWaypoint();
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
        PublishPatrolWaypoint();
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

    private void LogDebug(string text) => logger.LogDebug(text);
    private void LogWarning(string text) => logger.LogWarning(text);
    private void Log(string text) => logger.LogInformation(text);

    /// <summary>
    /// Publishes the leader's current top patrol waypoint to
    /// <see cref="LeaderNavigationProvider"/> so assist bots can navigate to
    /// the same destination (waypoint-sharing mode).
    /// Only called during normal patrol refill — GoToOneWaypoint (AssistReturn)
    /// must NOT publish, as that navigates to the assist's CantFollow position.
    ///
    /// Uses <see cref="Navigation.TopPublishableWaypointW"/> instead of
    /// <see cref="Navigation.TopWaypointW"/> — the former walks past entries
    /// that <see cref="Navigation.SkipBlacklistedWaypoints"/> is about to filter
    /// out, preventing transient blacklisted-waypoint broadcasts that put the
    /// assist into an infinite SetSingleWaypoint loop (log-35 14:08:16:226).
    /// </summary>
    private void PublishPatrolWaypoint()
    {
        if (classConfig.Mode != Mode.PartyLeader)
            return;

        Vector3 wp = navigation.TopPublishableWaypointW;
        if (wp != default)
        {
            leaderNavProvider.SetTargetWaypoint(wp);
            logger.LogDebug($"[FRG] Published patrol waypoint -> {wp}");
        }
        else
        {
            leaderNavProvider.ClearTargetWaypoint();
        }
    }

    // ── Fix FN (run-162) — caster-in-BL retreat backtrack state machine ──
    //
    // Returns true if backtrack owns this tick (caller skips normal forward
    // navigation logic). False otherwise (backtrack is idle and FRG should
    // proceed with its standard patrol behavior).
    //
    // Per-phase responsibilities:
    //   • None — entry detection. When self-defense conditions hold AND the
    //     verdict says target is in a BL rect AND we have route waypoints
    //     behind us, transition to Navigating and push the first backtrack
    //     waypoint. Set navigation.IsBacktrackingActive=true so BL goal
    //     yields and GoapAgent's self-defense override suppresses (preventing
    //     Combat steal during travel).
    //   • Navigating — drive navigation toward the current backtrack waypoint.
    //     On arrival, stop motion, face the target (PressInteract — and
    //     immediately tap StepBackwards to cancel the Interact auto-run),
    //     transition to Evaluating.
    //   • Evaluating — wait BtEvalDelayMs (~700ms) for facing + TargetMapPos
    //     refresh, then re-check IsTargetLikelyInBlacklistRect:
    //       - verdict=false (mob outside rect) → set BacktrackEngageGuid,
    //         clear IsBacktrackingActive, transition to EngageWindow. Combat
    //         plan will become eligible via Fix 17 btEngageOverride and
    //         engage this guid.
    //       - verdict=true (still in rect) → advance to next prior waypoint
    //         and transition back to Navigating.
    //   • EngageWindow — Combat owns the bot. We monitor for exit conditions:
    //       - bits.Combat() false → exit (combat dropped).
    //       - target lost or guid changed → exit (kill credit or escape).
    //       - mob re-enters rect → resume backtrack from current spot.
    //
    // Exit conditions (any phase): self-defense continuation check fails
    // (target lost, combat dropped, target swapped) → ExitBacktrack with
    // diagnostic reason logged.
    private bool UpdateBacktrackStateMachine()
    {
        // Same condition signature as Fix FM in FRG line ~1260 PLUS the
        // verdict requirement: we only enter backtrack when target reads
        // IN the rect (= can't engage from current spot). If verdict says
        // OUT, the existing override path handles engagement.
        bool inSelfDefenseInRect =
            bits.Target() &&
            targetBlacklist.Is() &&
            bits.Combat() &&
            (playerReader.TargetTarget is UnitsTarget.Me
                                       or UnitsTarget.Pet
                                       or UnitsTarget.PartyOrPet) &&
            navigation.IsTargetLikelyInBlacklistRect();

        // ── Fix FT (run-165) — BL-escape entry trigger ──
        //
        // When the leader is geographically inside any static BL rect but the
        // self-defense-in-rect path doesn't fire (because target slot has been
        // cleared by Blacklist Target plan after AreaBlacklistMob on attack,
        // or the bot isn't actively being damaged this tick), we still want
        // to backtrack out. Evidence — run-165 leader log around LC=16:37:11:
        // PressClearTarget at LC=16:37:10:224 → target=0, hastarget=False;
        // inblacklistarea was False at LC=16:37:11:057 (bot at rect boundary
        // <955.x, 277.x>) but True by LC=16:37:14:523 (bot at <952.88, 281.24>,
        // deep inside rect [952..999, 277..327]). Leader sat in NO PLAN for
        // 36 seconds until manual intervention. Per operator design: any time
        // the leader is in a BL area, it should backtrack via prior route
        // waypoints; if a non-BL mob aggros during the escape, normal Combat
        // plan preempts FRG and resumes after kill; if the bot becomes
        // physically stuck inside the rect, Fix 17 self-defense is re-allowed
        // (see GoapAgent.cs Fix 17 declaredStuck override).
        //
        // Scope: PartyLeader mode only. The assist's analogous case (assist
        // inside BL, leader outside) is handled differently — FFG's segment-
        // crossing check is broadened in FollowFocusGoal.cs to let the assist
        // navigate OUT toward an outside-target leader.
        bool inBlEscape = !inSelfDefenseInRect
            && classConfig.Mode == Mode.PartyLeader
            && navigation.IsInBlacklistArea();

        switch (_btPhase)
        {
            case BacktrackPhase.None:
                if (!inSelfDefenseInRect && !inBlEscape)
                    return false;
                int curIdx = ProjectCurrentRouteIndex();
                if (curIdx <= 0)
                {
                    // No route loaded, or already at start of route — cannot backtrack.
                    return false;
                }
                _btStartRouteIdx = curIdx;
                _btStepsBack = 1;
                _btIsBlEscapeMode = inBlEscape;
                _btTargetGuid = inSelfDefenseInRect ? playerReader.TargetGuid : 0;
                navigation.IsBacktrackingActive = true;
                navigation.BacktrackEngageGuid = 0;
                if (!PushBacktrackWaypoint())
                {
                    ExitBacktrack("could not push first backtrack waypoint");
                    return false;
                }
                _btPhase = BacktrackPhase.Navigating;
                if (_btIsBlEscapeMode)
                {
                    logger.LogWarning(
                        $"[FRG] [FIX-FIRE] FT: Entering BL-ESCAPE BACKTRACK — leader is inside a BL rect " +
                        $"with no fightable target. Starting route idx={_btStartRouteIdx}; first backtrack target " +
                        $"<{_btCurrentWaypointW.X:F2},{_btCurrentWaypointW.Y:F2}>. Will exit when bot is outside all rects.");
                }
                else
                {
                    logger.LogWarning(
                        $"[FRG] [FIX-FIRE] FN: Entering BACKTRACK — IsIgnored caster guid={_btTargetGuid} reads in-rect. " +
                        $"Starting route idx={_btStartRouteIdx}; first backtrack target " +
                        $"<{_btCurrentWaypointW.X:F2},{_btCurrentWaypointW.Y:F2}>.");
                }
                return true;

            case BacktrackPhase.Navigating:
                if (!IsBacktrackContinuationValid())
                {
                    ExitBacktrack("self-defense conditions cleared during navigation");
                    return false;
                }
                float dist = playerReader.WorldPos.WorldDistanceXYTo(_btCurrentWaypointW);
                if (dist < BtArrivalYards)
                {
                    // At the arrival moment two distinct motion sources are
                    // active and they're cancelled differently:
                    //
                    //   (1) Navigation's held ForwardKey — pressed by
                    //       navigation.Update each tick while pathing toward
                    //       the backtrack waypoint. Cancel = release the W key.
                    //       navigation.StopMovement() does exactly that:
                    //       input.StopForward(true) → release ForwardKey if held.
                    //       Nothing else (no waypoint clearing, no events,
                    //       no state-machine side effects — it's a one-line
                    //       wrapper).
                    //
                    //   (2) Interact's click-to-move — initiated by PressInteract
                    //       on the next line. WoW internally drives the bot
                    //       toward the target's CURRENT position WITHOUT
                    //       pressing any movement key. So step (1) does NOT
                    //       cancel this — releasing W has zero effect on an
                    //       in-flight click-to-move. Cancel = any direct
                    //       movement input. StepBackwards taps BackwardKey
                    //       for 100ms which (a) cancels the click-to-move
                    //       and (b) displaces the bot ~0.5y away from the
                    //       target — slight extra retreat for free.
                    //
                    // Both calls are required: drop (1) and the held W key
                    // resumes pushing us forward after StepBackwards releases
                    // the back key. Drop (2) and the click-to-move keeps
                    // pulling us toward the caster (= into the rect we're
                    // retreating from). Net result with both: stationary,
                    // facing the mob, ~0.5y further from the rect.
                    //
                    // Fix FT: in BL-escape mode there's no target to face, so
                    // skip the Interact+StepBackwards facing dance. Just stop
                    // movement and transition straight to Evaluating; the
                    // BtEvalDelayMs settling window still applies so the bot
                    // pauses briefly at the waypoint before deciding to
                    // advance or exit.
                    navigation.StopMovement();   // cancel (1) — release held ForwardKey
                    if (!_btIsBlEscapeMode)
                    {
                        input.PressInteract();       // face target (starts (2) click-to-move)
                        input.StepBackwards();       // cancel (2) — 100ms Backward tap
                    }
                    _btArrivalUtc = DateTime.UtcNow;
                    _btPhase = BacktrackPhase.Evaluating;
                    logger.LogInformation(
                        $"[FRG] {(_btIsBlEscapeMode ? "FT" : "FN")}: Arrived at backtrack waypoint #{_btStepsBack} " +
                        $"(routeIdx={(_btStartRouteIdx - _btStepsBack)}). " +
                        $"{(_btIsBlEscapeMode ? "BL-escape mode — settling" : "Faced target via Interact+StepBackwards; settling")} {BtEvalDelayMs}ms for verdict.");
                }
                return true;

            case BacktrackPhase.Evaluating:
                if (!IsBacktrackContinuationValid())
                {
                    ExitBacktrack("self-defense conditions cleared during evaluation");
                    return false;
                }
                if ((DateTime.UtcNow - _btArrivalUtc).TotalMilliseconds < BtEvalDelayMs)
                    return true;  // wait for facing + addon refresh

                if (_btIsBlEscapeMode)
                {
                    // Fix FT: in BL-escape mode the verdict is "is the BOT
                    // itself still inside any BL rect?" — not the target's
                    // position. If we've stepped back to a waypoint outside
                    // every static rect, the escape is complete and the bot
                    // can resume normal patrol from this position. Otherwise,
                    // advance to the next prior waypoint.
                    if (!navigation.IsInBlacklistArea())
                    {
                        ExitBacktrack($"BL escape complete — bot is outside all rects at waypoint #{_btStepsBack}");
                        return false;
                    }
                    _btStepsBack++;
                    int escNextIdx = _btStartRouteIdx - _btStepsBack;
                    if (escNextIdx >= 0 && PushBacktrackWaypoint())
                    {
                        _btPhase = BacktrackPhase.Navigating;
                        logger.LogInformation(
                            $"[FRG] FT: Bot still inside BL rect at waypoint #{_btStepsBack - 1} — advancing to backtrack waypoint #{_btStepsBack} (routeIdx={escNextIdx}).");
                    }
                    else
                    {
                        ExitBacktrack($"BL-escape reached start of route still inside rect (stepsBack={_btStepsBack} from start={_btStartRouteIdx})");
                        return false;
                    }
                    return true;
                }

                bool stillInRect = navigation.IsTargetLikelyInBlacklistRect();
                if (!stillInRect)
                {
                    // Mob has stepped out — engage via Fix FN signal. We
                    // intentionally keep IsIgnored sticky on this guid (per
                    // design call); the BacktrackEngageGuid signal makes
                    // CombatGoal Fix 17 and GoapAgent's self-defense override
                    // ignore the IsIgnored map for THIS guid only.
                    navigation.IsBacktrackingActive = false;
                    navigation.BacktrackEngageGuid = _btTargetGuid;
                    _btPhase = BacktrackPhase.EngageWindow;
                    logger.LogWarning(
                        $"[FRG] [FIX-FIRE] FN: Verdict at backtrack waypoint #{_btStepsBack} = OUT-OF-RECT " +
                        $"for guid={_btTargetGuid}. Signaling BacktrackEngageGuid; Combat plan will engage.");
                }
                else
                {
                    _btStepsBack++;
                    int nextIdx = _btStartRouteIdx - _btStepsBack;
                    if (nextIdx >= 0 && PushBacktrackWaypoint())
                    {
                        _btPhase = BacktrackPhase.Navigating;
                        logger.LogInformation(
                            $"[FRG] FN: Verdict still IN-RECT — advancing to backtrack waypoint #{_btStepsBack} (routeIdx={nextIdx}).");
                    }
                    else
                    {
                        ExitBacktrack($"reached start of route (stepsBack={_btStepsBack} from start={_btStartRouteIdx})");
                        return false;
                    }
                }
                return true;

            case BacktrackPhase.EngageWindow:
                // Combat plan owns the bot during this phase. We just hold
                // state and watch for exit conditions.
                if (!bits.Combat() || !bits.Target() || playerReader.TargetGuid != _btTargetGuid)
                {
                    ExitBacktrack("combat dropped or target changed during engage window");
                    return false;
                }
                if (navigation.IsTargetLikelyInBlacklistRect())
                {
                    // Mob slipped back into the rect during the engage
                    // attempt. Resume backtracking from current position
                    // (don't reset _btStartRouteIdx — keep retreating).
                    logger.LogWarning(
                        $"[FRG] FN: Engage window — guid={_btTargetGuid} re-entered rect. Resuming backtrack.");
                    navigation.BacktrackEngageGuid = 0;
                    navigation.IsBacktrackingActive = true;
                    _btStepsBack++;
                    int nextIdx = _btStartRouteIdx - _btStepsBack;
                    if (nextIdx >= 0 && PushBacktrackWaypoint())
                    {
                        _btPhase = BacktrackPhase.Navigating;
                    }
                    else
                    {
                        ExitBacktrack("mob re-entered rect but no more backwards waypoints");
                        return false;
                    }
                }
                // Returning true keeps the caller from running normal FRG
                // forward-navigation logic — Combat plan is selected by the
                // GOAP planner via the engage signal and drives the bot.
                return true;
        }
        return false;
    }

    private bool IsBacktrackContinuationValid()
    {
        // Fix FT: BL-escape continuation is just "still inside any static rect."
        // No target tracking applies — if the bot is no longer in a BL area,
        // Evaluating phase will exit cleanly the next time it checks. We
        // return true here so we don't bail mid-Navigating; the Evaluating
        // verdict handles the actual exit logic, ensuring we always finish
        // the current waypoint before declaring success.
        if (_btIsBlEscapeMode)
            return navigation.IsInBlacklistArea();

        return bits.Combat()
            && bits.Target()
            && playerReader.TargetGuid == _btTargetGuid
            && targetBlacklist.Is();
    }

    private void ExitBacktrack(string reason)
    {
        logger.LogInformation(
            $"[FRG] {(_btIsBlEscapeMode ? "FT" : "FN")}: Exiting backtrack — {reason}. " +
            $"phase={_btPhase}→None, stepsBack={_btStepsBack}, guid={_btTargetGuid}, blEscapeMode={_btIsBlEscapeMode}.");
        _btPhase = BacktrackPhase.None;
        _btStartRouteIdx = -1;
        _btStepsBack = 0;
        _btTargetGuid = 0;
        _btCurrentWaypointW = default;
        _btArrivalUtc = DateTime.MinValue;
        _btIsBlEscapeMode = false;
        navigation.IsBacktrackingActive = false;
        navigation.BacktrackEngageGuid = 0;
    }

    private int ProjectCurrentRouteIndex()
    {
        Vector3[] path = pathSettings.Path;
        if (path.Length == 0)
            return -1;

        Vector3 playerW = playerReader.WorldPos;
        int bestIdx = -1;
        float bestDistSq = float.MaxValue;
        for (int i = 0; i < path.Length; i++)
        {
            Vector3 wpW = WorldMapAreaDB.ToWorld_FlipXY(path[i], playerReader.WorldMapArea);
            float dx = wpW.X - playerW.X;
            float dy = wpW.Y - playerW.Y;
            float dSq = dx * dx + dy * dy;
            if (dSq < bestDistSq)
            {
                bestDistSq = dSq;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    private bool PushBacktrackWaypoint()
    {
        Vector3[] path = pathSettings.Path;
        int idx = _btStartRouteIdx - _btStepsBack;
        if (idx < 0 || idx >= path.Length)
            return false;
        Vector3 wpW = WorldMapAreaDB.ToWorld_FlipXY(path[idx], playerReader.WorldMapArea);
        _btCurrentWaypointW = wpW;
        navigation.SetSingleWaypoint(wpW);
        leaderNavProvider.SetTargetWaypoint(wpW);
        return true;
    }
}
