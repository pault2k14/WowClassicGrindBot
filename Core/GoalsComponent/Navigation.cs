using Core.AreaBlacklist;
using Core.GOAP;

using Microsoft.Extensions.Logging;

using SharedLib;
using SharedLib.Extensions;

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Collections.Concurrent;

using static System.MathF;

#pragma warning disable 162

namespace Core.Goals;

// =====================================================================
// Option B refactor (post-Fix AE): policy-decision snapshots fired by
// Navigation so that goal-specific path-validation logic can live in
// the goal (e.g., FollowFocusGoal) instead of in shared Navigation
// code. The leader's patrol (FollowRouteGoal) does not subscribe to
// any of these events and therefore sees no behavior change from
// Fix AB-1 / Fix AC+AE / Fix AD — the policies they encode apply
// only to subscribers (currently the assist's FollowFocusGoal).
//
// Pattern: Navigation populates a snapshot, fires the event, then
// reads back the subscriber's decision (Reject / Escalate / Veto)
// and performs the corresponding state mutation itself. This keeps
// route-stack and cooldown mutations inside Navigation while moving
// the policy out.
// =====================================================================

/// <summary>
/// Fired by <see cref="Navigation.OnPathResultInspected"/> after a path
/// is computed, after Fix AA filtering, and after PathSimplify, but
/// before the route is committed to drive movement. A subscriber can
/// set <see cref="Reject"/> = true to have Navigation clear the route,
/// set a cooldown, and fire <see cref="Navigation.OnPathFailed"/> — for
/// example, when the simplified routeTop would U-turn the bot.
/// </summary>
public sealed class PathInspectionSnapshot
{
    public long RequestId { get; }
    public Vector3 StartW { get; }
    public Vector3 EndW { get; }
    public int PathLength { get; }
    /// <summary>True if a simplified routeTop was produced after Fix AA + PathSimplify.</summary>
    public bool HasSimplifiedRouteTop { get; }
    /// <summary>The first node the bot would walk to. Valid only when <see cref="HasSimplifiedRouteTop"/> is true.</summary>
    public Vector3 SimplifiedRouteTop { get; }
    public int SimplifiedRouteCount { get; }
    /// <summary>X component of (EndW - StartW), the path-forward direction in 2D.</summary>
    public float ForwardX { get; }
    /// <summary>Y component of (EndW - StartW), the path-forward direction in 2D.</summary>
    public float ForwardY { get; }
    /// <summary>Squared magnitude of the forward vector. Subscribers should skip evaluation when this is below ~0.0001 (degenerate path).</summary>
    public float ForwardLenSq { get; }

    /// <summary>Set by a subscriber to request that the path be rejected.</summary>
    public bool Reject { get; set; }
    /// <summary>Cooldown to apply on reject (default 500ms).</summary>
    public int RejectCooldownMs { get; set; } = 500;

    public PathInspectionSnapshot(long requestId, Vector3 startW, Vector3 endW,
        int pathLength, bool hasSimplifiedRouteTop, Vector3 simplifiedRouteTop,
        int simplifiedRouteCount, float forwardX, float forwardY, float forwardLenSq)
    {
        RequestId = requestId;
        StartW = startW;
        EndW = endW;
        PathLength = pathLength;
        HasSimplifiedRouteTop = hasSimplifiedRouteTop;
        SimplifiedRouteTop = simplifiedRouteTop;
        SimplifiedRouteCount = simplifiedRouteCount;
        ForwardX = forwardX;
        ForwardY = forwardY;
        ForwardLenSq = forwardLenSq;
    }
}

/// <summary>
/// Fired by <see cref="Navigation.OnStaleEmptyPathMatchesActive"/> when
/// the pather returns an empty (no-path) result that arrived stale (a
/// newer request superseded it) BUT whose (start, end) still matches
/// the currently in-flight request within a 1y tolerance. A subscriber
/// can set <see cref="Escalate"/> = true after counting enough such
/// confirmations to have Navigation set a cooldown and fire
/// <see cref="Navigation.OnPathFailed"/>.
/// </summary>
public sealed class StaleEmptyPathSnapshot
{
    public Vector3 ResultStartW { get; }
    public Vector3 ResultEndW { get; }
    public Vector3 ActiveRequestStartW { get; }
    public Vector3 ActiveRequestEndW { get; }

    /// <summary>Set by a subscriber to request that OnPathFailed be fired with cooldown.</summary>
    public bool Escalate { get; set; }
    /// <summary>Cooldown to apply on escalate (default 2000ms).</summary>
    public int EscalateCooldownMs { get; set; } = 2000;

    /// <summary>
    /// Fix AX (Route B Part 1): set by a subscriber to request that
    /// Navigation push a direct one-hop route to <see cref="ActiveRequestEndW"/>
    /// instead of escalating via <see cref="Navigation.OnPathFailed"/>.
    /// Uses the same <c>BuildDirectRouteFallback</c> mechanism as
    /// <see cref="Navigation.HandleRepeatedNoPath"/>'s count==2 branch.
    /// <para>
    /// Mutually exclusive with <see cref="Escalate"/>: if the subscriber sets
    /// both, <see cref="TriggerDirectRoute"/> takes precedence (the direct-
    /// route push is the more constructive recovery action; the watchdog
    /// cascade will fire OnPathFailed if the direct route also fails).
    /// </para>
    /// <para>
    /// Used by FollowFocusGoal's Fix AU branch when <c>_activeStuckReported</c>
    /// is true and a stale-empty matches the active request: the bot is
    /// stationary AND the pather cannot route from this position, so
    /// attempting a direct walk toward the target is more likely to recover
    /// than waiting for another path attempt to succeed.
    /// </para>
    /// </summary>
    public bool TriggerDirectRoute { get; set; }

    public StaleEmptyPathSnapshot(Vector3 resultStartW, Vector3 resultEndW,
        Vector3 activeRequestStartW, Vector3 activeRequestEndW)
    {
        ResultStartW = resultStartW;
        ResultEndW = resultEndW;
        ActiveRequestStartW = activeRequestStartW;
        ActiveRequestEndW = activeRequestEndW;
    }
}

/// <summary>
/// Fired by <see cref="Navigation.OnRepeatedNoPathDirectRouteDecision"/>
/// in HandleRepeatedNoPath at the count==2 branch, before the (potentially
/// dangerous) direct-route push toward an unreachable target. A subscriber
/// can set <see cref="Veto"/> = true to skip the push and have Navigation
/// reset its no-path counter, apply a cooldown, and fire
/// <see cref="Navigation.OnPathFailed"/>.
/// </summary>
public sealed class RepeatedNoPathDecisionSnapshot
{
    public Vector3 StartW { get; }
    public Vector3 EndW { get; }
    public float DistYards { get; }
    public int SameNoPathCount { get; }

    /// <summary>Set by a subscriber to skip the direct-route push and escalate via OnPathFailed.</summary>
    public bool Veto { get; set; }
    /// <summary>Cooldown to apply on veto (default 1000ms).</summary>
    public int VetoCooldownMs { get; set; } = 1000;

    public RepeatedNoPathDecisionSnapshot(Vector3 startW, Vector3 endW,
        float distYards, int sameNoPathCount)
    {
        StartW = startW;
        EndW = endW;
        DistYards = distYards;
        SameNoPathCount = sameNoPathCount;
    }
}

public sealed partial class Navigation : IDisposable
{
    private const bool debug = false;

    private const float DIFF_THRESHOLD = 1.5f;   // within 50% difference
    private const float UNIFORM_DIST_DIV = 2;    // within 50% difference

    private readonly string patherName;

    private readonly ILogger<Navigation> logger;
    private readonly PlayerDirection playerDirection;
    private readonly ConfigurableInput input;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly StopMoving stopMoving;
    private readonly StuckDetector stuckDetector;
    private readonly IPPather pather;
    private readonly IMountHandler mountHandler;
    private readonly PathSettings pathSettings;
    private readonly DataConfig dataConfig;

    private const float MinDistanceMount = 10;
    private float MaxDistance = 200;
    private readonly float IndoorMinDistance = 1f;
    private readonly float OutDoorMinDistance = 3f;

    private float AvgDistance;
    private float lastWorldDistance = float.MaxValue;

    private const float minAngleToTurn = PI / 35f;              // 5.14 degree
    private const float minAngleToStopBeforeTurn = PI / 3f;     // 60 degree

    private readonly Stack<Vector3> wayPoints = new();
    private readonly Stack<Vector3> routeToNextWaypoint = new();

    public Vector3[] TotalRoute { private set; get; } = Array.Empty<Vector3>();

    // ── Route-walking migration: Turn 1 (log-84 baseline → Turn 1 commit) ──
    //
    // LoadedRoute is the assist-side mirror of the leader's patrol route,
    // populated via LoadRoute() at FFG OnEnter. Distinct from TotalRoute
    // (which is a dynamic snapshot of current per-leg path + remaining
    // wayPoints stack) — LoadedRoute is the immutable curated route from
    // pathSettings.Path, normalized to world coords at load time.
    //
    // Turn 1 scope (this commit): purely additive. The property is
    // populated at FFG OnEnter but no code reads from it yet. Validates
    // that the assist can hold route data via the existing DI-injected
    // pathSettings and that the route file is loadable from the assist
    // side. Acceptance: LoadedRoute.Length matches the leader's published
    // route waypoint count, visible in [FIX-CONFIG] header and
    // [ROUTE-LOAD] log line.
    //
    // Turn 2 scope (future): assist-side index state machine consumes
    // from LoadedRoute to compute the next route waypoint, replacing
    // body-chase semantics during patrol.
    public Vector3[] LoadedRoute { get; private set; } = Array.Empty<Vector3>();

    public DateTime LastActive { get; private set; }

    public event Action? OnPathCalculated;
    public event Action? OnWayPointReached;
    public event Action? OnDestinationReached;
    public event Action? OnAnyPointReached;
    public event Action? OnNoPathFound;
    public event Action<Vector3, Vector3>? OnPathFailed; // (startW, endW)

    // Option B refactor: policy-decision events that allow a goal-specific
    // subscriber (currently FollowFocusGoal) to make path-validation
    // decisions that previously lived inline in Navigation as Fix AB-1 /
    // Fix AC+AE / Fix AD. Non-subscribers see no behavior change.
    public event Action<PathInspectionSnapshot>? OnPathResultInspected;
    public event Action<StaleEmptyPathSnapshot>? OnStaleEmptyPathMatchesActive;
    public event Action<RepeatedNoPathDecisionSnapshot>? OnRepeatedNoPathDirectRouteDecision;

    /// <summary>
    /// Fired when the top waypoint changes due to a non-pop operation such as
    /// detour insertion (<see cref="TryInsertDetour"/>) — i.e., a push that
    /// changes wpTop without the bot having reached anything.
    /// <para>
    /// Fix S (log-63 19:46:56:993): pop-based events (OnWayPointReached) cover
    /// the reach-and-advance case, but detour insertion silently swaps wpTop
    /// from the original target to the detour without firing any signal. In
    /// PartyLeader mode that breaks waypoint broadcasting: FRG re-publishes
    /// on OnWayPointReached but not on detour insertion, so the assist sees
    /// a stale waypoint that the leader is no longer actually heading to,
    /// computes paths through the (now-bypassed) blacklist, fails, and
    /// gets stuck for the entire duration of the leader's detour traversal.
    /// </para>
    /// <para>Subscribers should re-read TopPublishableWaypointW and rebroadcast.</para>
    /// </summary>
    public event Action? OnTopWaypointChanged;

    // ── Fix AV (Route A: leader→assist StuckRect propagation) ──
    //
    // Fired when AddStuckRect successfully adds a new dynamic rect to the
    // local _stuckWorldRects list (not on dedup/skip, not on AddPropagated-
    // StuckRect). Designed for a leader-side subscriber (e.g., the leader's
    // FRG or a dedicated propagation service) to forward the rect over IPC
    // to peer bots' Navigation instances via AddPropagatedStuckRect.
    //
    // Event payload is the FINAL center (already offset by forwardDir if
    // applicable, see AddStuckRect) and the half-size in world yards. The
    // subscriber should treat (center, halfSize) as opaque rect identity —
    // the assist's AddPropagatedStuckRect uses the center for dedup and
    // applies the same half-size so both bots blacklist the same area.
    //
    // Non-subscribers see no behavior change. The assist's Navigation does
    // not subscribe (it would be a no-op there — assist's locally-added
    // rects don't need to propagate back to the leader, and the assist has
    // no peer to forward to in the current 2-bot architecture).
    public event Action<Vector3, float>? OnStuckRectAdded;  // (center, halfSize)

    // ── Fix AV (Route A) — Stuck-rect cleared notification ──
    //
    // Fired when ClearStuckRects empties the local _stuckWorldRects list
    // (no-op if the list was already empty — matches the existing early
    // return in ClearStuckRects). The leader-side subscriber forwards
    // this to LeaderNavigationProvider.ClearStuckRects so the published
    // snapshot drains on the next assist poll. The assist's own
    // propagated rects then expire naturally via TTL — see
    // PropagatedStuckRectTtlSec.
    //
    // Pair with OnStuckRectAdded for the full add/clear lifecycle.
    public event Action? OnStuckRectsCleared;
    public bool SimplifyRouteToWaypoint { get; set; } = true;

    private bool active;
    private bool destinationReachedLatched;
    private Vector3 playerWorldPos;

    private readonly ConcurrentQueue<PathRequest> pathRequests = new();
    private readonly ConcurrentQueue<PathResult> pathResults = new();

    private readonly CancellationToken token;
    private readonly Thread pathfinderThread;
    private readonly ManualResetEventSlim manualReset;

    private int failedAttempt;
    private Vector3 lastFailedDestination;

    // The effective blacklist used by all Navigation queries.
    // May be a CompositeAreaBlacklist when stuck rects have been added.
    private IAreaBlacklist? _areaBlacklist;
    public IAreaBlacklist? AreaBlacklist
    {
        get => _areaBlacklist;
        set
        {
            // Callers (FRG Resume, constructor) set the static route blacklist.
            // Store it separately so we can re-composite it with stuck rects.
            _staticAreaBlacklist = value;
            _areaBlacklist = _stuckWorldRects.Count > 0
                ? new CompositeAreaBlacklist(value, new RectBlacklist(_stuckWorldRects))
                : value;
        }
    }

    // The static blacklist set by route configuration (never contains stuck rects).
    private IAreaBlacklist? _staticAreaBlacklist;

    // Runtime stuck rects recorded when the character makes zero movement during an escape.
    private readonly List<BlacklistRect> _stuckWorldRects = new();

    // ── Fix AV (Route A: temporary lifetime for propagated rects) ──
    //
    // Parallel to _stuckWorldRects (same index = same rect). Each entry is
    // either DateTime.MaxValue (rect was discovered locally via TryUnstuck
    // — no automatic expiry, cleared explicitly via ClearStuckRects on
    // plan transitions) or a UTC timestamp (rect was propagated from a
    // peer bot via AddPropagatedStuckRect — auto-pruned by
    // PruneExpiredStuckRects on Update() ticks once UtcNow >= the entry).
    //
    // The two lifetime semantics must coexist because the same Navigation
    // instance can hold rects from both sources — e.g., the leader has
    // only locally-discovered rects, but an assist that adds its own
    // rects via FFG.TickIdleStuckDetection's TryUnstuck path AND also
    // receives the leader's propagated rects via AddPropagatedStuckRect
    // ends up with a mix; the parallel-list design preserves the
    // distinction without changing _stuckWorldRects' element type.
    private readonly List<DateTime> _stuckRectExpiryUtc = new();

    // How long a propagated stuck rect lives before PruneExpiredStuckRects
    // removes it. Sized for "both bots approaching the same mob from the
    // same direction" — the scenario where Route A propagation pays off:
    //
    //   t=0    Leader's ATG hits no-range-progress, TryUnstuck adds rect
    //          and fires OnStuckRectAdded → leader-side service publishes
    //          to assist on the next broadcast cycle (~500 ms).
    //   t≈0.5  Assist's poll picks up the rect, calls AddPropagated-
    //          StuckRect, rect enters assist's _areaBlacklist for pather.
    //   t≈3-10 Assist begins approach navigation; pather routes around
    //          the rect (the win condition for Route A).
    //   t≈3-15 Leader's combat begins; assist's combat begins shortly
    //          after. Both bots are near the rect.
    //   t≈15-30 Combat ends. Assist's post-combat navigation back to
    //          leader; pather still avoids the rect.
    //   t≈30-50 Rendezvous completes. Both bots resume patrol away
    //          from the rect area.
    //
    // 90 s gives margin for extended combat (3-mob pulls, runner mobs,
    // adds) while preventing the rect from polluting later patrol
    // passes through the same area on subsequent route loops. The
    // leader's own rect lifetime is bounded by ClearStuckRects on ATG/
    // CombatGoal plan transitions (much shorter than 90 s in normal
    // flow); a leader-side service that re-publishes each cycle while
    // the rect remains in the leader's list will refresh the assist's
    // TTL (see AddPropagatedStuckRect's dedup-refresh path), so the
    // assist's effective lifetime tracks the leader's actual rect
    // lifetime plus a 90 s cool-down tail.
    private const double PropagatedStuckRectTtlSec = 90.0;

    // Half-size in world yards of the dynamically-added stuck rect.
    // Bottom edge is 1y ahead of the player, top edge is 5y ahead (4y deep).
    // Center is 3y ahead (1y gap + 2y half-size).
    private const float StuckRectHalfSizeY = 2f;

    /// <summary>How far outside a forbidden rect detour points are placed (WORLD units).</summary>
    public float DetourMargin { get; set; } = 12f;

    /// <summary>
    /// Minimum world-yards a detour candidate must be from BOTH the path's start
    /// and its target/end point to be considered. Filters degenerate candidates
    /// produced by <see cref="BuildDetourCandidates"/> when the path endpoints
    /// happen to sit on the rect's perimeter+margin — the score function
    /// (startW.dist(d) + d.dist(endW)) gives such a candidate the minimum
    /// possible score (one term goes to zero), so it always wins, and the
    /// "detour" pushed onto the waypoint stack equals the existing target. Next
    /// refill recomputes the same path → same rejection → same duplicate detour;
    /// wp stack grows unbounded with no progress (log-37 assist 23:46:28→59,
    /// 30 s of growth from wp=1 to wp=153+, 630 identical rejection-detour
    /// insertions, bot stationary). 1 yard is small enough to admit legitimate
    /// close-but-distinct detours, large enough to reject exact and near-exact
    /// duplicates of either endpoint.
    /// </summary>
    public float MinDetourSeparationYards { get; set; } = 1.0f;

    /// <summary>Max detours attempted for the same target waypoint.</summary>
    public int MaxDetourAttemptsPerTarget { get; set; } = 6;

    private Vector3 lastDetourTarget;
    private int detourAttemptsForTarget;
    private volatile bool waitingForPathResult;
    private int pathRequestPending;        // 0 = none, 1 = in-flight

    // Monotonic request id so we can ignore stale results
    private long nextPathRequestId;
    private long activePathRequestId;

    // Optional: keep the last request params for debugging
    private Vector3 lastRequestStart;
    private Vector3 lastRequestEnd;

    private long lastRefillLogTick;
    private static readonly long RefillLogCooldownTicks = TimeSpan.FromMilliseconds(250).Ticks;

    private Vector3 lastEscapePoint;
    private int escapeInsertCooldownTicks;
    private DateTime waitingSinceUtc;

    private float noProgressBestDist = float.MaxValue;
    private DateTime noProgressSinceUtc = DateTime.MinValue;

    // Escape no-progress fallback to pather variables
    private bool escapeActive;
    private Vector3 escapeTargetW;
    private float escapeBestDist = float.MaxValue;
    private DateTime escapeNoProgressSinceUtc;
    private DateTime noProgressRefillCooldownUntilUtc = DateTime.MinValue;

    // Hard backoff so we don't spam Refill() every tick when route is empty
    private DateTime emptyRouteRefillCooldownUntilUtc = DateTime.MinValue;
    private const int EmptyRouteRefillMinIntervalMs = 250; // tune: 150-400

    private const int StuckOwnerId = 1;

    private enum RectSide { Left, Right, Bottom, Top }
    private Vector3 noProgressTargetW;

    // Last "known-good" anchor in WORLD coords.
    // Updated when we actually reach/progress a route point or a waypoint.
    // Used by higher-level logic (FollowRouteGoal) to rewind and retry when pather returns no-path.
    private Vector3 lastSafeAnchorW;

    public bool HasLastSafeAnchor => lastSafeAnchorW != default;
    public Vector3 LastSafeAnchorW => lastSafeAnchorW;

    /// <summary>
    /// True while a pather-based approach escape is actively in progress.
    /// Goals should check this before pressing Approach/Interact to avoid
    /// interrupting Navigation while it is routing around an obstacle.
    /// </summary>
    public bool IsApproachEscapeActive => _approachEscapeActive;
    public int ApproachEscapeTargetGuid => _approachEscapeTargetGuid;
    // Exposed for diagnostic logging in ATG/PTG OnEnter and DefaultApproach heartbeat.
    public float ApproachEscapeCurrentYards => _approachEscapeCurrentYards;
    public DateTime ApproachEscapeStartUtc => _approachEscapeStartUtc;
    public DateTime ApproachEscapeLastAttemptUtc => _approachEscapeLastAttemptUtc;

    /// <summary>
    /// Fix AZ (log-82, mob N post-combat 19:06:54:442 → 19:07:36:990):
    /// True when <see cref="TryRouteUnstuck"/> has an active escape attempt
    /// in progress — i.e., it has called <see cref="SetSingleWaypoint"/> with
    /// a tactical escape target (10y/20y/30y at some directional offset) and
    /// the bot is currently navigating that route, prior to the attempt
    /// reaching its target, timing out, or being declared no-progress.
    ///
    /// <para>
    /// Designed to be consumed by FFG's path-preservation logic in
    /// <c>StartNavigatingToLeader</c>'s drift gate. When this is true, FFG
    /// must NOT overwrite the existing waypoint with the leader-pursuit
    /// target — RouteEscape owns the waypoint and is in the middle of
    /// routing the bot around an obstacle. Overwriting cancels the escape
    /// mid-execution and routes the bot back through the same bad terrain
    /// that caused the original stuck. See FFG's path-preservation block
    /// for the full evidence trail and rationale.
    /// </para>
    ///
    /// <para>
    /// This flag is briefly false (~200 ms) during inter-attempt escalation
    /// (when one attempt has ended and the next hasn't yet fired). During
    /// that window FFG may overwrite the stale wp, but the next escape
    /// attempt overwrites it back. Brief and self-correcting.
    /// </para>
    /// </summary>
    public bool IsRouteEscapeActive => _routeEscapeActive;
    public int StuckRectCount => _stuckWorldRects.Count;

    /// <summary>
    /// True when at least one pather escape attempt has been made for the current
    /// target and the escalation sequence is not yet exhausted (more attempts remain).
    /// While true, goals should suppress the blacklist-area bail-out so the approach
    /// can re-trigger TryUnstuck() for the next escalation level (10y → 20y → 30y).
    /// Cleared when ResetApproachEscape() is called (new target or all attempts done).
    /// </summary>
    public bool IsApproachEscapeEscalating =>
        _approachEscapeTargetGuid != 0 &&
        _approachEscapeCurrentYards > 0 &&
        _approachEscapeCurrentYards <= ApproachEscapeEndYards;

    /// <summary>
    /// Set to true after an escape clears due to no-progress AND the character
    /// made less than 0.5y of total movement — meaning the terrain is physically
    /// trapping the character and pather-based routes can't help. The goal should
    /// respond with a jump + backward movement to physically escape. Reset by the
    /// goal after it handles the condition.
    /// </summary>
    public bool IsApproachEscapePhysicallyStuck { get; set; }

    /// <summary>
    /// True after all pather escape levels (10y → 20y → 30y) have been exhausted for the
    /// current target. ATG/PTG use this to trigger an immediate bail-out instead of waiting
    /// for the player to drift into the blacklist area. Cleared by ResetApproachEscape()
    /// (on target change) and when a new escape attempt succeeds.
    /// </summary>
    public bool IsApproachEscapeExhausted { get; private set; }

    // --- Approach-vector pather escape ---
    // Goals call RecordApproachPosition() each time they press the Approach/Interact key.
    // Navigation keeps the position only when genuine forward progress has been made from it.
    // When TryUnstuck() is called, Navigation projects waypoints along the approach vector
    // starting at 10 world units (yards) and incrementing by 1 up to 30, trying to find a
    // pather path that routes around the obstacle. Falls back to stuckDetector.Update() if
    // all 3 attempts fail (10y, 20y, 30y).
    private const float ApproachEscapeStartYards = 10f;
    private const float ApproachEscapeEndYards = 30f;
    // After all 3 escape levels fail, suppress TryUnstuck for this many seconds so
    // the bail-out checks in ATG/PTG get at least one Update() tick to run.
    private const double PostExhaustionCooldownSec = 5.0;
    private const float ApproachEscapeProgressMinW = 0.75f; // min movement to keep a recorded position
    private const double ApproachEscapeTimeoutSecPerYard = 0.8; // seconds per yard — scales with escape distance
    private const double ApproachEscapeNoMovementSec = 3.0;     // escalate immediately if no movement for this long
    private Vector3 _approachEscapeStartPos;                    // player position when escape began, for initial displacement check
    private Vector3 _approachEscapeLastProgressPos;             // rolling position — updated when character moves >2y, for mid-escape stuck detection
    private DateTime _approachEscapeLastProgressUtc = DateTime.MinValue; // when last progress was recorded
    private Vector3 _approachRecordedW;
    private Vector3 _approachPrevRecordedW;
    // Anchor = the very first position recorded on each approach cycle (seed value from
    // RecordApproachPosition). Never updated after seeding so anchor→_approachRecordedW
    // spans the full approach distance from arrival to when stuck fires. This gives
    // TryUnstuck a reliable multi-yard direction vector even when consecutive
    // RecordApproach steps are only ~0.75y apart (the terrain-sliding case that always
    // caused the locked pair to be "too noisy" for MinLockDisplacementY=3.0y).
    private Vector3 _approachEscapeAnchorW;
    private bool _approachEscapeActive;
    private float _approachEscapeCurrentYards;
    private DateTime _approachEscapeLastAttemptUtc = DateTime.MinValue;
    private DateTime _approachEscapeStartUtc = DateTime.MinValue;
    // Locked approach direction: captured from _approachRecordedW/_approachPrevRecordedW
    // at the moment TryUnstuck first fires, so all escalation attempts (10y->30y) use
    // the same world-space direction toward the mob rather than compounding arc errors
    // from positions recorded after each escape movement.
    private Vector3 _approachEscapeLockedRecordedW;
    private Vector3 _approachEscapeLockedPrevRecordedW;
    private int _approachEscapeTargetGuid;
    // Tracks which integer second the TryUnstuck(active) heartbeat last fired.
    // Prevents double-fire when two consecutive calls both straddle a second boundary.
    private int _approachEscapeHeartbeatLastSec = -1;

    // Pather-based route escape — tried before stuckDetector.Update() when the bot
    // gets stuck while following the patrol route. Uses the player's facing direction
    // to project escape targets at 10y -> 20y -> 30y ahead, same escalation as approach escape.
    private const float RouteEscapeStartYards = 10f;
    private const float RouteEscapeEndYards = 30f;
    private const double RouteEscapeTimeoutSecPerYard = 0.8;
    private const double RouteEscapeNoMovementSec = 3.0;
    private bool _routeEscapeActive;
    private float _routeEscapeCurrentYards;
    private DateTime _routeEscapeLastAttemptUtc = DateTime.MinValue;
    private DateTime _routeEscapeStartUtc = DateTime.MinValue;
    private Vector3 _routeEscapeStartPos;
    private Vector3 _routeEscapeLastProgressPos;
    private DateTime _routeEscapeLastProgressUtc = DateTime.MinValue;

    // Fix BA (log-82 19:06:37:899): the escape target of the current RouteEscape
    // attempt, captured at attempt-fire so the failed direction can be
    // reconstructed in the physTrapped branch (TryRouteUnstuck). Used by
    // AddStuckRect call to mark the bad terrain 5y in the failed direction
    // so future paths through the area route around. Cleared in
    // ResetRouteEscape alongside the other escape state.
    private Vector3 _routeEscapeAttemptTarget;

    // Fix B (log-34b 03:43:43:444): independent watchdog for unreachable RouteEscape
    // targets. The existing TryRouteUnstuck no-progress check (RouteEscapeNoMovementSec)
    // only runs when TryRouteUnstuck is called, which requires
    // stuckDetector.IsGettingCloser==false. Sub-yard drift can keep IsGettingCloser
    // returning true intermittently, so the existing checks never fire and the bot
    // sits on the unreachable target indefinitely. This watchdog runs every Update
    // tick regardless of stuckDetector state and clears the stale waypoint stack
    // after the timeout — relying on the empty-waypoints check at line 700 to fire
    // OnDestinationReached, which FRG converts to RefillWaypoints(false).
    //
    // Threshold rationale:
    //   12s > 10y attempt's per-yard timeout (8s) so existing escalation can run
    //        first when called; long enough to avoid spurious fires on slow legitimate
    //        navigation; matches the observed log-34b stall duration before combat hit.
    //   2y matches the existing progress threshold at line 1583.
    private const double RouteEscapeUnreachableTimeoutSec = 12.0;
    private const float RouteEscapeUnreachableMinDisplacementYards = 2.0f;

    // Fix C: bounded cache of recently-confirmed-unreachable RouteEscape targets.
    // Populated by CheckRouteEscapeUnreachable when it abandons a target; consulted
    // by TryRouteUnstuck before SetSingleWaypoint to skip projections that fall
    // onto a known-failed spot. Synchronous validation is not possible here
    // because the pather is async (IPPather pather, line 37) — no reachability API.
    // Cache is bounded (FIFO eviction) and cleared on Resume() so each patrol
    // session starts fresh.
    //
    // Clearance rationale:
    //   5y is small enough that a future 20y/30y escalation in the same direction
    //   (which lands 10y/20y further than the failed 10y attempt) is not rejected,
    //   so the existing escalation can still find paths around small obstacles.
    //   Large enough to reject re-projecting onto the same cliff/wall.
    private const int RouteEscapeFailedTargetsCapacity = 4;
    private const float RouteEscapeFailedTargetClearance = 5.0f;
    private readonly List<Vector3> _routeEscapeFailedTargets = new(RouteEscapeFailedTargetsCapacity);

    // Backoff to prevent request spam on repeated blacklist rejections
    private DateTime blacklistRejectCooldownUntilUtc = DateTime.MinValue;
    private DateTime noPathCooldownUntilUtc = DateTime.MinValue;
    private Vector3 lastRejectStartW;
    private Vector3 lastRejectEndW;
    private int sameRejectCount;
    private const int MaxSameRejectBeforeFallback = 4;

    // Fix 29 (log-51 16:15:25→16:15:48, leader stuck 22 s at BL west edge):
    // The detour-from-rejected-path machinery has a blind spot when startW
    // sits right on a BL rect boundary and the navmesh routes every western
    // target through the same interior bad point. TryInsertDetour finds
    // geometrically-valid candidates (clear of rect, segments don't graze
    // inflated rect, far enough from start), but the pathfinder's actual
    // route to those candidates STILL passes through the same bad point.
    // Each successful detour insertion resets sameRejectCount (caller line
    // ~2792), so HandleBlacklistReject's existing skip-waypoint safety
    // never accumulates the 4 same-(startW, endW) hits it needs — endW
    // cycles between the candidates while startW stays fixed.
    //
    // Log-51 evidence: 58 path rejections in 17 s, badPoint always
    // <954.539, 290.3721, 0>, detours alternating <934.39343, 286.5877>
    // and <934.39343, 302.12463>, wp count growing 12 → 60+ as each
    // cycle iteration pushed another detour onto the stack. The leader's
    // physical position never changed; only Combat preemption at
    // 16:15:49 broke the loop by moving the character.
    //
    // Fix: track same-(startW, badPointW) instead. Once the same start +
    // same bad point appears MaxSameDetourBadPointBeforeSkip times in a
    // row — meaning the navmesh keeps routing us through the same BL
    // interior point regardless of which detour we picked — pop wpTop
    // directly, clear the route, set a 250 ms cooldown, and return true
    // so the caller short-circuits the rest of its detour/reject pipeline.
    // Once detected, the cycle counter is intentionally NOT reset after
    // each pop so subsequent same-(startW, badPointW) hits keep popping
    // at ~250 ms intervals until the queue drains or the bot is moved
    // by some other mechanism (combat preemption, Refill fire of a
    // different waypoint, etc.). Pop is gated on Peek matching endW so
    // we never pop the wrong waypoint if other code has changed the top.
    private Vector3 _lastDetourAttemptStartW;
    private Vector3 _lastDetourAttemptBadPointW;
    private int _sameDetourBadPointCount;
    private const int MaxSameDetourBadPointBeforeSkip = 4;

    // Fix H (log-54 22:58:19:404 → 22:58:39:779, leader stuck 20 s at
    // <951.97, 291.86>, 0.43 y outside std rect west edge X=952.4): Fix 29's
    // pop-only behavior relies on external events (combat, etc.) to move the
    // bot. In log-54 no such event fired for the entire 20 s window — Fix 29
    // detected the cycle 67 times consecutively (counter 4→70), every
    // iteration popping one waypoint but the bot's physical position never
    // changing. Local navmesh from <951.97, 291.86> connects to
    // <954.539, 290.37> (inside rect) as the natural eastern neighbor, so
    // every pathfinder result routes through that point regardless of which
    // wpTop is chosen as endW. TryRouteUnstuck/stuckDetector.Update never
    // reached during the cycle because routeToNextWaypoint is cleared on
    // each Fix 29 fire and Update()'s stuck-detection branch (line 1281)
    // requires an active route. After 25 s the evade recovery elapsed, the
    // mob attacked again, and self-defense override took over — pulling
    // the bot deeper into BL during combat.
    //
    // Fix: when the same-(startW, badPointW) cycle persists beyond
    // Fix29UnstuckEscalationHits past the last escalation (or initial
    // detection), and the cooldown has elapsed, escalate to:
    //   1. TryPhysicalUnstuck — jump + reverse + jump + reverse, ~1.4 s
    //      blocking. Displaces the bot 1-2 y backward, breaking the
    //      mesh-stuck node.
    //   2. Direct away-from-rect route — compute direction from rect center
    //      to post-unstuck position, push a single routeToNextWaypoint node
    //      DetourMargin+6 = 18 y in that direction. Bypass the pather since
    //      the pather has been routing through the rect interior the entire
    //      cycle. The away-from-center direction guarantees the route
    //      segment moves away from the rect (geometry: target is on the
    //      same side as start, further from center → segment doesn't
    //      cross). Once displaced 10+ y from the rect edge, the local
    //      mesh offers different connections and normal pathing works.
    //
    // Cooldown 5 s prevents spamming TryPhysicalUnstuck (each call blocks
    // 1.4 s). Threshold 8 gives Fix 29's pop-only mode ~1 s past initial
    // detection to resolve through normal navigation before forcing
    // physical intervention. _lastFix29UnstuckHitCount is reset in the
    // else branch (cycle reset) so a fresh cycle starts at full budget;
    // the time-based cooldown persists across cycles (don't escalate
    // faster than 5 s regardless).
    private int _lastFix29UnstuckHitCount;
    private DateTime _fix29UnstuckCooldownUntilUtc;
    private const int Fix29UnstuckEscalationHits = 8;
    private const double Fix29UnstuckCooldownSec = 5.0;

    // Fix 30 (log-51 16:16:42→16:19:22, leader oscillated between two
    // waypoints for 2+ minutes while assist stood still): downstream
    // safety net for queue-duplicate accumulation. Fix 29 closes the
    // upstream path (TryInsertDetourFromRejectedPath no longer pushes
    // unbounded duplicates), but if duplicates ever land in wayPoints
    // via some other code path or pre-existing accumulation, the bot
    // will pop them one at a time and physically traverse between
    // them — visually "running around" while the assist correctly
    // stays in FFG dead-band (since both endpoints are ≤14 y away).
    //
    // Log-51 evidence trail:
    //   16:15:25-16:15:48 — 68 rejections during the earlier stuck
    //                      phase pushed ~68 detour duplicates onto
    //                      the queue (the Fix 29 phenomenon).
    //   16:16:42 — patrol resumes with wp=68, almost entirely two
    //              coordinates: <934.39, 286.5877> and <934.39,
    //              302.12463> (15.5 y apart).
    //   16:16:42 → 16:19:22 — leader pops one every ~4-5 s,
    //              bouncing back and forth. 36 pops over 2.5 min,
    //              only 3 unique coordinates ever observed.
    //   The assist sees both endpoints at 5-9 y, well inside
    //   FollowingMaxYards→NavigatingMinYards dead-band; correctly
    //   stays Idle. User perceives the leader running circles
    //   around a stationary assist.
    //
    // Detection: track the coordinates of the last N popped waypoints.
    // If the set of unique coords (within OscillationToleranceYards
    // tolerance) collapses to ≤ MaxUniqueCoordsForOscillation, the
    // bot is bouncing — drain any consecutive top entries that fall
    // in that small set, then clear the tracking so the next batch
    // of pops gets a fresh window. The drain is gated on Peek
    // membership in the small set, so legitimate non-duplicate
    // waypoints below the duplicate run are preserved.
    //
    // Conservative thresholds so legitimate tight patrols (e.g.,
    // small circular paths) don't trigger: a true circular patrol
    // with > 2 distinct waypoints in 6 pops will not match the
    // "≤ 2 unique" gate. Only genuinely degenerate "ping-pong
    // between 1-2 points" patterns fire.
    private readonly Queue<Vector3> _recentPopHistory = new();
    private const int OscillationDetectionWindow = 6;
    private const float OscillationToleranceYards = 1.0f;
    private const int MaxUniqueCoordsForOscillation = 2;

    private long _navDebugNextTick;
    private const int NavDebugEveryMs = 250;

    private long waitingRequestId;

    // Movement can stop at STOP_DIST, but we only advance (pop) at POP_DIST.
    // IMPORTANT: POP_DIST MUST BE >= STOP_DIST to avoid deadlocks.
    private const float STOP_DIST = 3.0f;              // when we stop applying movement input
    public const float POP_DIST = 3.6f;              // when we pop route node / waypoint (>= STOP_DIST)
    private const float STOP_DIST_SQ = STOP_DIST * STOP_DIST;
    private const float POP_DIST_SQ = POP_DIST * POP_DIST;

    // Fix AQ (log-79 03:55:13:166 → 03:55:16:257, 3.1 s stall):
    // Threshold for the conservative "bot at final waypoint but route
    // still non-empty" short-circuit. See the comment block at the
    // insertion site in Update() for the full evidence trail. Set to
    // 1.5 s so the short-circuit fires comfortably before FFG's
    // ActiveStuckDetection threshold (2.5 s — FollowFocusGoal
    // ActiveStuckThresholdSec) — Navigation pops the waypoint and
    // emits OnDestinationReached, letting FFG set a fresh waypoint
    // before FFG's Stuck flag → leader pause → RouteEscape chain
    // would have to run.
    private const double AtWpStallThresholdSec = 1.5;

    // Chase-progress watchdog with movement hysteresis
    private Vector3 _chaseProgTarget;
    private float _chaseProgBestDist = float.MaxValue;
    private DateTime _chaseProgSinceUtc = DateTime.MinValue;

    /// <summary>
    /// Seconds since the watchdog last recorded a new closest-distance to the
    /// chase target. Returns 0 when no chase is currently being tracked
    /// (e.g., navigation isn't running, or the target hasn't been seen yet).
    /// <para>
    /// This is the "no progress toward target" timer — distinct from raw
    /// displacement, which can keep accumulating while the bot grinds along
    /// geometry without actually closing on the target. <see cref="FollowFocusGoal"/>
    /// reads this to escalate to CantFollow when the assist has been wedged
    /// long enough to indicate an unrecoverable obstacle.
    /// </para>
    /// </summary>
    public double ChaseSinceBestSec =>
        _chaseProgSinceUtc == DateTime.MinValue
            ? 0.0
            : (DateTime.UtcNow - _chaseProgSinceUtc).TotalSeconds;

    /// <summary>
    /// Diagnostic-only pass-through for the stuckDetector's current owner id.
    /// Used by FollowFocusGoal's chase watchdog firing log to confirm whether
    /// the detector was Released (ownerId=0) or held by Navigation (ownerId=1)
    /// at escalation time. See [NAV-DIAG] log lines.
    /// </summary>
    public int StuckDetectorOwnerId => stuckDetector.OwnerId;

    /// <summary>
    /// Diagnostic-only pass-through for the stuckDetector's enabled flag.
    /// </summary>
    public bool StuckDetectorEnabled => stuckDetector.Enabled;

    private Vector3 _chaseLastPos;
    private DateTime _chaseLastMovedUtc = DateTime.MinValue;
    private DateTime _chaseUnstuckCooldownUntilUtc = DateTime.MinValue;

    // True while we're actively following an escape route that does NOT target the waypoint stack.
    private bool escapeRouteInProgress;
    private Vector3 escapeRouteEndW;

    private Vector3 lastNoPathStartW;
    private Vector3 lastNoPathEndW;
    private int sameNoPathCount;
    private const int MaxSameNoPathBeforeFallback = 3;

    // Fix AB-1 stale-empty-path counter state was previously stored here
    // (private fields _staleEmptySameCount / _staleEmptyLastStartW /
    // _staleEmptyLastEndW, with const StaleEmptyResultsBeforeOnPathFailed).
    // Option B refactor moved both the state and the policy into
    // FollowFocusGoal.cs. Navigation now only fires
    // OnStaleEmptyPathMatchesActive when a stale empty-path result
    // arrives whose (start, end) matches the active request, and the
    // subscriber decides whether to escalate. The leader's
    // FollowRouteGoal does not subscribe, so the leader sees no AB-1
    // policy effect (matching the assist-only nature of the original
    // log-68 evidence). See FollowFocusGoal.Navigation_OnStaleEmptyPathMatchesActive
    // for the rationale and the moved counter.

    public Navigation(ILogger<Navigation> logger,
        CancellationTokenSource<GoapAgent> cts,
        PlayerDirection playerDirection,
        ConfigurableInput input,
        PlayerReader playerReader, AddonBits bits,
        StopMoving stopMoving,
        StuckDetector stuckDetector, IPPather pather, IMountHandler mountHandler,
        ClassConfiguration classConfiguration,
        PathSettings pathSettings, DataConfig dataConfig)
    {
        this.logger = logger;
        this.playerDirection = playerDirection;
        this.input = input;
        this.playerReader = playerReader;
        this.bits = bits;
        this.stopMoving = stopMoving;
        this.stuckDetector = stuckDetector;
        this.pather = pather;
        this.mountHandler = mountHandler;
        this.pathSettings = pathSettings;
        this.dataConfig = dataConfig;

        patherName = pather.GetType().Name;

        AvgDistance = OutDoorMinDistance;
        token = cts.Token;
        manualReset = new(false);
        pathfinderThread = new(PathFinderThread);
        pathfinderThread.Start();

        switch (classConfiguration.Mode)
        {
            case Mode.AttendedGather:
                MaxDistance = OutDoorMinDistance;
                SimplifyRouteToWaypoint = false;
                break;
        }

        // Fix #10: removed duplicate assignments of pathSettings and dataConfig that were here

        // Apply per-route area blacklists (map rects -> world rects)
        if (pathSettings.MapBlacklistRects is { Length: > 0 })
        {
            logger.LogInformation("[NAV]: pathSettings.MapBlacklistRects.Length: " + pathSettings.MapBlacklistRects.Length);

            AreaBlacklist = BlacklistConversion.BuildWorldBlacklistFromMapRects(
                pathSettings.MapBlacklistRects,
                playerReader.WorldMapArea
            );

            DetourMargin = 12f;
            MaxDetourAttemptsPerTarget = 6;
        }
        else
        {
            logger.LogInformation("[NAV]: pathSettings.MapBlacklistRects.Length: " + pathSettings.MapBlacklistRects.Length);
            AreaBlacklist = null;
        }
    }

    private void SyncRouteStateToTop(bool resetIfEmpty = true)
    {
        UpdateTotalRoute();

        if (routeToNextWaypoint.Count > 0)
        {
            Vector3 top = Nav2D(routeToNextWaypoint.Peek());

            stuckDetector.SetTargetLocation(StuckOwnerId, top);
            ResetNoProgressWatchdog(top);
            ResetChaseProgressWatchdog(top);

            // If we now have something to chase, allow immediate processing.
            emptyRouteRefillCooldownUntilUtc = DateTime.MinValue;
            return;
        }

        if (resetIfEmpty)
        {
            stuckDetector.Reset(StuckOwnerId);
            ResetNoProgressWatchdog();
            ResetChaseProgressWatchdog();

            emptyRouteRefillCooldownUntilUtc = DateTime.MinValue;
        }
    }

    private bool TryConsumeReachedWaypoint(in Vector3 playerPos)
    {
        if (wayPoints.Count == 0)
            return false;

        Vector3 wpTop = Nav2D(wayPoints.Peek());
        float wpReach = ReachedDistance(OutDoorMinDistance) + 0.35f;

        if (playerPos.WorldDistanceXYTo(wpTop) > wpReach)
            return false;

        Vector3 completed = Nav2D(wayPoints.Pop());
        SetLastSafeAnchor(playerPos);

        logger.LogWarning(
            $"[NAV] POP WAYPOINT (XY reached) completed={completed} " +
            $"newWpTop={(wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()).ToString() : "<none>")}");

        routeToNextWaypoint.Clear();
        SyncRouteStateToTop();

        // Fix X (log-66 23:06:17:122): when a RouteEscape target is reached via
        // pop, the escape has succeeded — clear escape state so the subsequent
        // navigation (typically FFG/FRG refreshing a new target toward the
        // leader / next waypoint) is not still tracked by CheckRouteEscapeUnreachable.
        //
        // Without this, _routeEscapeActive stays true after the escape target
        // is consumed. The watchdog continues to measure displacement from the
        // *original* _routeEscapeStartPos (set when the escape began, 12+
        // seconds ago), not from the new path's start. If the new path goes
        // back through the area near the escape's start (e.g., escape went
        // north up a hill, new path comes back south past the same X to reach
        // the leader), the watchdog sees ~0 net displacement from the anchor
        // and falsely declares the new path "unreachable", clearing valid
        // progress.
        //
        // Observed in log-66: assist's 20y escape at +120° (Fix W's rotated
        // attempt) successfully reached <2000.93, -2136.52> NORTH of the
        // tree. FFG refreshed target to <1977.52, -2168.84> SOUTH (toward
        // leader). Bot navigated SW productively for 9 seconds, covering 17y
        // of valid progress. At 12s after escape *start* the watchdog fired:
        // "RouteEscape unreachable: displacement=0.34y < 2y" — measuring
        // from the old anchor at <1994, -2155>, even though the bot was
        // genuinely making forward progress on a different path. The
        // resulting waypoint clear forced a second full escape cycle,
        // adding ~25 seconds to the stuck duration.
        if (_routeEscapeActive)
        {
            logger.LogInformation(
                $"[NAV] RouteEscape: escape target reached via TryConsumeReachedWaypoint " +
                $"(completed={completed}) — clearing escape state.");
            ResetRouteEscape();
        }

        // Fix 30: track this pop and drain any duplicate-set oscillation.
        // See field comment block near line ~353. Safe to call here because
        // SyncRouteStateToTop has already run; any drain inside the helper
        // updates routes accordingly.
        TrackPopAndDedupIfOscillating(completed);

        OnWayPointReached?.Invoke();

        if (wayPoints.Count == 0)
        {
            CompleteDestinationReached();
        }

        return true;
    }

    // Fix 30 helper — see the long comment at the field declarations
    // (line ~353). Tracks the last OscillationDetectionWindow popped
    // waypoint coordinates and, if those collapse to ≤
    // MaxUniqueCoordsForOscillation unique points (within
    // OscillationToleranceYards), drains any consecutive top entries
    // that fall within that small set so the next non-duplicate
    // waypoint becomes the active target.
    private void TrackPopAndDedupIfOscillating(Vector3 popped)
    {
        _recentPopHistory.Enqueue(popped);
        while (_recentPopHistory.Count > OscillationDetectionWindow)
            _recentPopHistory.Dequeue();

        if (_recentPopHistory.Count < OscillationDetectionWindow)
            return;

        // Build unique-coord set under the tolerance. Small N (= 6),
        // so the O(N²) build is fine.
        Span<Vector3> uniques = stackalloc Vector3[OscillationDetectionWindow];
        int uniqueCount = 0;
        foreach (var p in _recentPopHistory)
        {
            bool matched = false;
            for (int i = 0; i < uniqueCount; i++)
            {
                if (uniques[i].WorldDistanceXYTo(p) < OscillationToleranceYards)
                {
                    matched = true;
                    break;
                }
            }
            if (!matched)
            {
                if (uniqueCount >= uniques.Length)
                {
                    // More uniques than the window can hold — definitely
                    // not the degenerate ping-pong case. Bail.
                    return;
                }
                uniques[uniqueCount++] = p;
            }
        }

        if (uniqueCount > MaxUniqueCoordsForOscillation)
            return;

        // Oscillation: drain consecutive top entries that are in the
        // small set. Stop the moment we see a waypoint outside the set
        // — that's the next legitimate target and must be preserved.
        int drained = 0;
        while (wayPoints.Count > 0)
        {
            Vector3 top = Nav2D(wayPoints.Peek());
            bool topInSet = false;
            for (int i = 0; i < uniqueCount; i++)
            {
                if (uniques[i].WorldDistanceXYTo(top) < OscillationToleranceYards)
                {
                    topInSet = true;
                    break;
                }
            }
            if (!topInSet)
                break;

            wayPoints.Pop();
            drained++;
        }

        if (drained > 0)
        {
            logger.LogError(
                $"[NAV] Fix 30: oscillation detected — {uniqueCount} unique coord(s) " +
                $"in last {OscillationDetectionWindow} pops (tolerance {OscillationToleranceYards:0.0}y). " +
                $"Drained {drained} consecutive top entries to break the cycle. " +
                $"wpCount-after={wayPoints.Count}");

            routeToNextWaypoint.Clear();
            SyncRouteStateToTop();
            _recentPopHistory.Clear();
        }
    }

    private bool TryRefillRouteNow(CancellationToken token, DateTime now)
    {
        if (waitingForPathResult)
            return false;

        if (now < emptyRouteRefillCooldownUntilUtc)
            return false;

        if (now < noProgressRefillCooldownUntilUtc)
        {
            emptyRouteRefillCooldownUntilUtc = now.AddMilliseconds(EmptyRouteRefillMinIntervalMs);
            return false;
        }

        emptyRouteRefillCooldownUntilUtc = now.AddMilliseconds(EmptyRouteRefillMinIntervalMs);

        logger.LogDebug(
            $"[NAV-EMPTY] route=0 wp={wayPoints.Count} " +
            $"waiting={waitingForPathResult} pending={Volatile.Read(ref pathRequestPending)} " +
            $"reqQ={pathRequests.Count} resQ={pathResults.Count} " +
            $"noPathCdMs={(noPathCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"blCdMs={(blacklistRejectCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"npCdMs={(noProgressRefillCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"pos={Nav2D(playerReader.WorldPos)} " +
            $"wpTop={(wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()).ToString() : "<none>")}");

        RefillRouteToNextWaypoint(token);
        return routeToNextWaypoint.Count > 0;
    }

    private void CompleteDestinationReached(bool fireWaypointReached = false)
    {
        if (destinationReachedLatched)
            return;

        destinationReachedLatched = true;

        escapeRouteInProgress = false;
        escapeActive = false;

        StopAndResetAtDestination();

        if (fireWaypointReached)
            OnWayPointReached?.Invoke();

        OnDestinationReached?.Invoke();
    }

    private void StopAndResetAtDestination()
    {
        stopMoving.Stop();
        input.StopForward(true);

        // If this was the completion of a pather-based approach escape, clear
        // the escape state so goals resume normal approach on the next tick.
        if (_approachEscapeActive)
        {
            Vector3 completionPos = Nav2D(playerReader.WorldPos);
            float displaced = _approachEscapeStartPos != default
                ? completionPos.WorldDistanceXYTo(_approachEscapeStartPos)
                : 0f;
            logger.LogInformation(
                $"[NAV] ApproachEscape: escape navigation complete — resuming normal approach. " +
                $"pos={completionPos} displaced={displaced:0.0}y over {_approachEscapeCurrentYards:0}y attempt. " +
                $"[diag: lastAttemptUtc={_approachEscapeLastAttemptUtc:HH:mm:ss.fff} yards={_approachEscapeCurrentYards:0} guid={_approachEscapeTargetGuid}]");
            _approachEscapeActive = false;
        }

        ResetStuckParameters();
        ResetNoProgressWatchdog();
        ResetChaseProgressWatchdog();
    }

    private void ClearDestinationLatch()
    {
        destinationReachedLatched = false;
    }

    private bool IsAtFinalWaypoint(in Vector3 playerPos, out Vector3 finalWp)
    {
        finalWp = default;

        if (wayPoints.Count != 1)
            return false;

        finalWp = Nav2D(wayPoints.Peek());

        float reach = ReachedDistance(OutDoorMinDistance) + 0.35f;
        return playerPos.WorldDistanceXYTo(finalWp) <= reach;
    }

    private void CollapseTinyRouteSteps(in Vector3 playerPos, float minStepDist = 1.25f)
    {
        while (routeToNextWaypoint.Count > 0)
        {
            Vector3 top = Nav2D(routeToNextWaypoint.Peek());

            // Drop nodes that are too close to the player
            if (playerPos.WorldDistanceXYTo(top) <= minStepDist)
            {
                routeToNextWaypoint.Pop();
                continue;
            }

            // Drop nodes that are too close to the next node behind them
            if (routeToNextWaypoint.Count >= 2)
            {
                var arr = routeToNextWaypoint.ToArray(); // top-first
                Vector3 a = Nav2D(arr[0]);
                Vector3 b = Nav2D(arr[1]);

                if (a.WorldDistanceXYTo(b) <= minStepDist)
                {
                    routeToNextWaypoint.Pop();
                    continue;
                }
            }

            break;
        }
    }

    private static Vector3 Nav2D(Vector3 w) => new Vector3(w.X, w.Y, 0f);

    private static float DistSq2D(in Vector3 a, in Vector3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    // Helper: should we apply movement input toward target?
    private static bool ShouldMoveToward(in Vector3 playerPos, in Vector3 targetPos)
    {
        float dSq = DistSq2D(playerPos, targetPos);
        return dSq > STOP_DIST_SQ;
    }

    private bool NavDebugDue()
    {
        long now = Environment.TickCount64;
        if (now < _navDebugNextTick) return false;
        _navDebugNextTick = now + NavDebugEveryMs;
        return true;
    }

    private void RefillExit(string reason)
    {
        logger.LogDebug(
            $"[NAV-REFILL-EXIT] {reason} " +
            $"active={active} escapeActive={escapeActive} insideBL={(AreaBlacklist?.ContainsWorld(Nav2D(playerReader.WorldPos)) == true)} " +
            $"wp={wayPoints.Count} route={routeToNextWaypoint.Count} " +
            $"waiting={waitingForPathResult} pending={Volatile.Read(ref pathRequestPending)} reqQ={pathRequests.Count} resQ={pathResults.Count} " +
            $"noPathCdMs={(noPathCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"blCdMs={(blacklistRejectCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"npCdMs={(noProgressRefillCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"emptyCdMs={(emptyRouteRefillCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"pos={Nav2D(playerReader.WorldPos)}");
    }

    private void RefillEnd(string phase, long startTick)
    {
        long now = Environment.TickCount64;
        logger.LogDebug(
            $"[NAV-REFILL-END] phase={phase} elapsedMs={(now - startTick)} " +
            $"uiMapId={(playerReader.UIMapId.Value > 0 ? playerReader.UIMapId.Value.ToString() : "<null>")} " +
            $"worldMapArea={playerReader.WorldMapArea} " +
            $"active={active} escapeActive={escapeActive} insideBL={(AreaBlacklist?.ContainsWorld(Nav2D(playerReader.WorldPos)) == true)} " +
            $"wp={wayPoints.Count} route={routeToNextWaypoint.Count} " +
            $"waiting={waitingForPathResult} pending={Volatile.Read(ref pathRequestPending)} reqQ={pathRequests.Count} resQ={pathResults.Count} " +
            $"noPathCdMs={(noPathCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"blCdMs={(blacklistRejectCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"npCdMs={(noProgressRefillCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0} " +
            $"pos={Nav2D(playerReader.WorldPos)} wpTop={(wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()).ToString() : "<none>")}");
    }

    private void NavDbg(string msg)
    {
        if (NavDebugDue())
        {
            logger.LogDebug("[NAV-DBG] " + msg);
        }
    }

    private bool RouteTargetsWaypointTop(float eps = 1.5f)
    {
        if (routeToNextWaypoint.Count == 0 || wayPoints.Count == 0)
            return true;

        Vector3 wpTop = wayPoints.Peek();

        // Stack.ToArray() returns top-first. Bottom (final route destination) is last.
        var arr = routeToNextWaypoint.ToArray();
        Vector3 routeEnd = arr[arr.Length - 1];

        return routeEnd.WorldDistanceXYTo(wpTop) <= eps;
    }

    private static float MinDistToAny(Vector3 p, IEnumerable<Vector3> pts)
    {
        float best = float.MaxValue;
        foreach (var v in pts)
        {
            float d = p.WorldDistanceXYTo(v);
            if (d < best) best = d;
        }
        return best == float.MaxValue ? -1 : best;
    }

    public void Dispose()
    {
        stuckDetector.Release(StuckOwnerId);
        manualReset.Set();
    }

    public void Update()
    {
        Update(token);
    }

    public void Update(CancellationToken token)
    {
        if (NavDebugDue())
        {
            logger.LogInformation($"[NAV-SANITY] Update entered navHash={GetHashCode()} tokenCancelled={token.IsCancellationRequested}");
        }

        if (!active)
            return;

        // Fix AV (Route A): prune propagated stuck rects whose TTL has
        // elapsed. Locally-discovered rects (TryUnstuck path) have
        // DateTime.MaxValue expiry and are never pruned here — they are
        // cleared by ClearStuckRects on plan transitions. Cheap O(n) on
        // rect count (typically 0–3); placed at the top of Update so the
        // _areaBlacklist seen by all downstream logic this tick reflects
        // the most up-to-date rect set.
        PruneExpiredStuckRects();

        NavDbg($"STATE enter active={active} " +
               $"wp={wayPoints.Count} route={routeToNextWaypoint.Count} " +
               $"waiting={waitingForPathResult} pending={Volatile.Read(ref pathRequestPending)} " +
               $"waitingReq={Volatile.Read(ref waitingRequestId)} activeReq={Volatile.Read(ref activePathRequestId)} " +
               $"noPathCd={(noPathCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0}ms " +
               $"blCd={(blacklistRejectCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0}ms " +
               $"npCd={(noProgressRefillCooldownUntilUtc - DateTime.UtcNow).TotalMilliseconds:0}ms");

        if (AreaBlacklist == null && pathSettings.MapBlacklistRects is { Length: > 0 })
        {
            AreaBlacklist = BlacklistConversion.BuildWorldBlacklistFromMapRects(
                pathSettings.MapBlacklistRects,
                playerReader.WorldMapArea
            );

            DetourMargin = 12f;
            MaxDetourAttemptsPerTarget = 6;
        }

        if (escapeInsertCooldownTicks > 0)
            escapeInsertCooldownTicks--;

        // Fix B watchdog: if RouteEscape's target is unreachable, clear waypoints so
        // the empty-waypoints check below fires OnDestinationReached — which FRG
        // converts into RefillWaypoints(false), restoring the patrol from path settings.
        // Placed BEFORE the wp=0/route=0 check so a clear in this tick triggers the
        // OnDestinationReached fire on the same tick (no extra latency).
        CheckRouteEscapeUnreachable();

        if (wayPoints.Count == 0 && routeToNextWaypoint.Count == 0)
        {
            logger.LogInformation(
                $"[NAV-SANITY] EXIT noWork inside={AreaBlacklist?.ContainsWorld(Nav2D(playerReader.WorldPos)) == true}");

            if (!destinationReachedLatched)
            {
                destinationReachedLatched = true;
                StopAndResetAtDestination();
                OnDestinationReached?.Invoke();
                NavDbg("RETURN noWork (wp=0 route=0) -> OnDestinationReached");
            }

            if (wayPoints.Count == 0 && routeToNextWaypoint.Count == 0)
                return;
        }

        SkipBlacklistedWaypoints();

        if (wayPoints.Count == 0 && routeToNextWaypoint.Count == 0)
        {
            if (!destinationReachedLatched)
            {
                destinationReachedLatched = true;

                StopAndResetAtDestination();

                OnDestinationReached?.Invoke();
                NavDbg("RETURN afterSkipBlacklistedWaypoints noWork (wp=0 route=0)");
            }

            return;
        }

        while (pathResults.TryDequeue(out PathResult result))
        {
            result.Callback(result);
        }

        if (waitingForPathResult && (DateTime.UtcNow - waitingSinceUtc).TotalSeconds > 3)
        {
            logger.LogWarning("[NAV] Path wait timeout; resetting pending/waiting gates.");
            waitingForPathResult = false;
            Interlocked.Exchange(ref pathRequestPending, 0);
            Volatile.Write(ref waitingRequestId, 0);
        }

        int pending = Volatile.Read(ref pathRequestPending);

        if (pending == 1 &&
            !waitingForPathResult &&
            pathRequests.Count == 0 &&
            pathResults.Count == 0)
        {
            logger.LogWarning("[NAV] pathRequestPending=1 but no queued/in-flight work. Clearing pending gate.");
            Interlocked.Exchange(ref pathRequestPending, 0);
        }

        if (waitingForPathResult && pending == 0 && pathRequests.Count == 0)
        {
            logger.LogWarning("[NAV] waitingForPathResult was TRUE but no request is pending/queued. Resetting.");
            waitingForPathResult = false;
        }

        if (token.IsCancellationRequested)
            return;

        Vector3 playerPos = Nav2D(playerReader.WorldPos);
        playerWorldPos = playerPos;
        DateTime now = DateTime.UtcNow;

        // Clear stale routes only when this is a normal route, not an escape route.
        if (routeToNextWaypoint.Count > 0 &&
            wayPoints.Count > 0 &&
            !escapeActive &&
            !escapeRouteInProgress &&
            AreaBlacklist?.ContainsWorld(playerPos) != true &&
            !waitingForPathResult &&
            Volatile.Read(ref pathRequestPending) == 0 &&
            !RouteTargetsWaypointTop())
        {
            var arr = routeToNextWaypoint.ToArray();
            var routeEnd = arr[arr.Length - 1];

            logger.LogWarning(
                $"[NAV] Stale route detected; clearing. " +
                $"routeTop={routeToNextWaypoint.Peek()} routeEnd={routeEnd} wpTop={wayPoints.Peek()}");

            routeToNextWaypoint.Clear();
            SyncRouteStateToTop();
        }

        // If route is empty, first see if we already reached current waypoint.
        if (routeToNextWaypoint.Count == 0)
        {
            if (IsAtFinalWaypoint(playerPos, out _))
            {
                SetLastSafeAnchor(playerPos);
                wayPoints.Pop();
                routeToNextWaypoint.Clear();
                SyncRouteStateToTop();

                CompleteDestinationReached(fireWaypointReached: true);
                return;
            }

            if (TryConsumeReachedWaypoint(playerPos))
            {
                if (wayPoints.Count == 0)
                    return;

                // ── Fix BG (log-93 00:02:42:243 → 00:03:27:478, 45-second
                //    "paused but walking" incident) ───────────────────────
                // TryConsumeReachedWaypoint fires OnWayPointReached.Invoke,
                // which in PartyLeader mode runs FRG.Navigation_OnWayPointReached
                // (Fix BF's pause-for-assist check). That handler may call
                // navigation.PausePathing() to set active=false MID-Update.
                // Without this check, Update would continue past here to the
                // steering loop at line ~1722 and call input.StartForward(true),
                // re-pressing the forward key the handler just released via
                // StopMovement(). The forward key is a STICKY press
                // (ConfigurableInput.StartForward: SetKeyState(ForwardKey, true)
                // stays down until StopForward); subsequent Update ticks see
                // !active and early-return at line 1309 without releasing the
                // key. Net effect: bot walks forward indefinitely after being
                // "paused" until physical terrain stops it.
                //
                // Log-93 evidence: leader paused at <-746.56,-4315.50> at
                // 00:02:42:244; ZERO meaningful events for 45s (only NAV-SANITY
                // showing Update early-returning); leader's mapPos drifted
                // (44.53,72.46) → (45.28,71.82) (~50y world). At 00:03:27 the
                // leader was at <-714.93,-4360.07>, 54.6y from the pause
                // position — far enough that the subsequent AssistReturn
                // could not navigate back successfully, producing the
                // observed "going around close points" RouteEscape loop.
                //
                // The fix is targeted: only return early if the handler
                // actually cleared active. Normal (no-pause) waypoint pops
                // continue through Refill and steering as before.
                if (!active)
                    return;
            }

            if (routeToNextWaypoint.Count == 0)
            {
                TryRefillRouteNow(token, now);

                if (routeToNextWaypoint.Count == 0)
                    return;

                // Refill produced a real route — safe to clear the latch now.
                ClearDestinationLatch();
            }
        }

        bool approachEscapeOwns = _approachEscapeActive ||
            (_approachEscapeCurrentYards > 0 && _approachEscapeTargetGuid != 0);

        if (!approachEscapeOwns &&
            AreaBlacklist != null &&
            AreaBlacklist.TryGetContainingRect(playerPos, out _) &&
            routeToNextWaypoint.Count > 0 &&
            IsBlacklistedPoint(routeToNextWaypoint.Peek()))
        {
            routeToNextWaypoint.Clear();
            SyncRouteStateToTop();
            return;
        }

        // Consume route nodes that are already effectively reached.
        bool poppedAny = false;
        while (routeToNextWaypoint.Count > 0)
        {
            CollapseTinyRouteSteps(playerPos);

            if (routeToNextWaypoint.Count == 0)
                break;

            if (!TryPopRouteTopIfReached(playerPos))
                break;

            poppedAny = true;
            SetLastSafeAnchor(playerPos);

            if (escapeRouteInProgress && routeToNextWaypoint.Count == 0)
            {
                escapeRouteInProgress = false;
                escapeActive = false;
                escapeBestDist = float.MaxValue;
                escapeNoProgressSinceUtc = DateTime.UtcNow;
                noProgressRefillCooldownUntilUtc = DateTime.MinValue;
            }

            SyncRouteStateToTop(routeToNextWaypoint.Count > 0 || wayPoints.Count > 0);
        }

        // ── Fix AQ (log-79 03:55:13:166 → 03:55:16:257) ──
        //
        // The route-pop loop above drains route nodes the bot is close to
        // (within POP_DIST = 3.6 y of the routeTop, or wpReach for the
        // last node). It does NOT drain nodes the bot can't physically
        // reach — e.g., when the path-finder routed through a corridor
        // the bot's actual movement got blocked from.
        //
        // Symptom: the bot arrives at the final waypoint area
        // straight-line (within IsAtFinalWaypoint reach ≈ 3.35 y) but
        // some intermediate route node remains unreachable. The existing
        // IsAtFinalWaypoint pop just below (line ~1439) only fires when
        // `routeToNextWaypoint.Count == 0`, so a non-empty route blocks
        // the pop. The bot computes targetW = routeTop (unreachable),
        // ShouldMoveToward against routeTop, runs through the chase
        // watchdog (4 s + 1.5 s thresholds), and ultimately gets caught
        // by FFG's ActiveStuckDetection (2.5 s) — by which point the
        // leader's combat has often resolved itself.
        //
        // Evidence from log-79:
        //   03:55:13:166  bot 1.7 y from wpTop <-735.25, -4278.86>;
        //                 path-preservation kept route; route still has
        //                 nodes that the bot will not reach.
        //   03:55:13:970  last LeftArrow press; bot finishes aligning
        //                 with the unreachable routeTop direction.
        //   03:55:13:970 → 03:55:16:257  bot drifts 0.75 y total (slow
        //                 into-terrain motion); ends at <-735.65,
        //                 -4281.47> — 2.64 y from wpTop, still inside
        //                 IsAtFinalWaypoint reach for the entire window.
        //   03:55:16:257  FFG ActiveStuckDetection fires.
        //   03:55:16:448  leader kills the mob (combat ends).
        //   03:55:16:690  RouteEscape fires — 242 ms too late to matter.
        //
        // Conservative gate: requires `_chaseProgSinceUtc` to be valid
        // (a chase has been observed) AND `sinceBestSec >=
        // AtWpStallThresholdSec` (the chase target's closest-distance
        // hasn't improved for ≥ 1.5 s). Without the stall gate, this
        // would fire on the FIRST tick the bot enters IsAtFinalWaypoint
        // reach — which is fine if the bot has actually arrived but
        // wrong during legitimate detours where the bot is straight-line
        // close to wpTop but still needs to traverse the route. The
        // stall gate ensures we only short-circuit when the route has
        // PROVABLY failed to deliver the bot to a closer position.
        //
        // Why 1.5 s: needs to fire before FFG's ActiveStuckDetection
        // (2.5 s) so navigation has first chance to recover the bot
        // via a clean pop + OnDestinationReached. If FFG fires first,
        // the bot's status flips to Stuck (leader pauses) and the
        // existing recovery chain runs — Fix AQ becomes redundant but
        // not harmful. 1.5 s is also short enough that we don't add
        // material latency to legitimate "bot is briefly stuck on a
        // route node but will recover" cases — those typically resolve
        // sub-second.
        //
        // What the short-circuit does: emit OnDestinationReached the
        // same way the existing IsAtFinalWaypoint pop just below
        // (line ~1439) does. FFG's handler accepts the position (if
        // within follow distance) or sets a new waypoint toward the
        // leader, which triggers fresh path-finding from the bot's
        // current position — far more likely to produce a reachable
        // route than the stale one we just abandoned.
        if (routeToNextWaypoint.Count > 0 &&
            wayPoints.Count == 1 &&
            _chaseProgSinceUtc != DateTime.MinValue)
        {
            double sinceBestSec_atWp = (now - _chaseProgSinceUtc).TotalSeconds;
            if (sinceBestSec_atWp >= AtWpStallThresholdSec)
            {
                Vector3 finalWp = Nav2D(wayPoints.Peek());
                float dWp = playerPos.WorldDistanceXYTo(finalWp);
                float wpReach = ReachedDistance(OutDoorMinDistance) + 0.35f;
                if (dWp <= wpReach)
                {
                    logger.LogWarning(
                        $"[NAV] [FIX-FIRE] AQ: route-pop stalled (sinceBest={sinceBestSec_atWp:0.00}s " +
                        $">= {AtWpStallThresholdSec:0.00}s threshold) but bot is within reach " +
                        $"({dWp:0.00}y <= {wpReach:0.00}y) of final waypoint {finalWp}. " +
                        $"Accepting arrival, clearing {routeToNextWaypoint.Count} unreachable " +
                        $"route node(s), and emitting OnDestinationReached.");

                    SetLastSafeAnchor(playerPos);
                    wayPoints.Pop();
                    routeToNextWaypoint.Clear();
                    SyncRouteStateToTop();
                    CompleteDestinationReached(fireWaypointReached: true);
                    return;
                }
            }
        }

        // Route may have emptied after pop; consume waypoint and/or refill immediately in same Update.
        if (routeToNextWaypoint.Count == 0)
        {
            if (IsAtFinalWaypoint(playerPos, out _))
            {
                SetLastSafeAnchor(playerPos);
                wayPoints.Pop();
                routeToNextWaypoint.Clear();
                SyncRouteStateToTop();

                CompleteDestinationReached(fireWaypointReached: true);
                return;
            }

            if (TryConsumeReachedWaypoint(playerPos))
            {
                if (wayPoints.Count == 0)
                    return;

                // Fix BG (see comment at the line-1456 callsite for the full
                // rationale): re-check `active` after TryConsumeReachedWaypoint
                // in case the OnWayPointReached handler called PausePathing.
                // Both callsites of TryConsumeReachedWaypoint in Update share
                // the same hazard; both need the same guard.
                if (!active)
                    return;
            }

            if (routeToNextWaypoint.Count == 0)
            {
                TryRefillRouteNow(token, now);

                if (routeToNextWaypoint.Count == 0)
                    return;

                // Refill produced a real route — safe to clear the latch now.
                ClearDestinationLatch();
            }
        }

        // IMPORTANT: only compute target after all possible route mutations above.
        Vector3 targetW = Nav2D(routeToNextWaypoint.Peek());

        if (Math.Abs(routeToNextWaypoint.Peek().Z) > 0.001f)
            logger.LogWarning($"[NAV] Z LEAK routeTop={routeToNextWaypoint.Peek()}");

        NavDbg($"MOVE target(routeTop)={targetW} player={playerReader.WorldPos} d={playerReader.WorldPos.WorldDistanceXYTo(targetW):0.00}");

        if (NavDebugDue())
        {
            Vector3 wpTop = wayPoints.Count > 0 ? wayPoints.Peek() : default;
            float distToWp = wayPoints.Count > 0 ? playerPos.WorldDistanceXYTo(wpTop) : -1;
            float distToRoute = playerPos.WorldDistanceXYTo(targetW);
            float dAnyWp = MinDistToAny(playerPos, wayPoints);

            logger.LogDebug(
                $"[NAV-DBG] dAnyWp={dAnyWp:0.00} player={playerPos} routeTop={targetW} dRoute={distToRoute:0.00} " +
                $"wpTop={wpTop} dWp={distToWp:0.00} routeCount={routeToNextWaypoint.Count} wpCount={wayPoints.Count}");
        }

        // Escape fallback watchdog
        if (escapeActive && routeToNextWaypoint.Count > 0)
        {
            if (AreaBlacklist?.ContainsWorld(playerPos) != true)
            {
                float edgeBuffer = DetourMargin + 6f;

                if (!escapeRouteInProgress && !IsNearBlacklistEdge(playerPos, edgeBuffer))
                {
                    escapeActive = false;
                }
            }
            else
            {
                float d = playerPos.WorldDistanceXYTo(targetW);

                if (d + 0.05f < escapeBestDist)
                {
                    escapeBestDist = d;
                    escapeNoProgressSinceUtc = DateTime.UtcNow;
                }
                else if ((DateTime.UtcNow - escapeNoProgressSinceUtc).TotalSeconds > 1.0 &&
                         !escapeRouteInProgress &&
                         !waitingForPathResult &&
                         Volatile.Read(ref pathRequestPending) == 0)
                {
                    logger.LogWarning($"[BL] Escape direct blocked; pathing to escape target {escapeTargetW}");

                    stopMoving.Stop();

                    EnqueuePathRequest(
                        playerReader.UIMapId.Value,
                        playerPos,
                        Nav2D(escapeTargetW),
                        d
                    );

                    escapeRouteInProgress = true;
                    escapeRouteEndW = Nav2D(escapeTargetW);
                    return;
                }
            }
        }

        // Stop only at true final destination, not intermediate nodes.
        if (!ShouldMoveToward(playerPos, targetW))
        {
            if (IsAtFinalWaypoint(playerPos, out _))
            {
                SetLastSafeAnchor(playerPos);
                wayPoints.Pop();
                routeToNextWaypoint.Clear();
                SyncRouteStateToTop();

                CompleteDestinationReached(fireWaypointReached: true);
                return;
            }

            stopMoving.Stop();
            input.StopForward(true);
            return;
        }

        // Movement this tick
        LastActive = DateTime.UtcNow;
        input.StartForward(true);

        Vector3 playerM = WorldMapAreaDB.ToMap_FlipXY(playerPos, playerReader.WorldMapArea);
        Vector3 targetM = WorldMapAreaDB.ToMap_FlipXY(targetW, playerReader.WorldMapArea);
        float heading = DirectionCalculator.CalculateMapHeading(playerM, targetM);

        stuckDetector.SetTargetLocation(StuckOwnerId, targetW);

        // Chase watchdog
        float dChase = playerPos.WorldDistanceXYTo(targetW);

        if (_chaseProgSinceUtc == DateTime.MinValue || _chaseProgTarget.WorldDistanceXYTo(targetW) > 0.05f)
        {
            _chaseProgTarget = targetW;
            _chaseProgBestDist = dChase;
            _chaseProgSinceUtc = now;

            _chaseLastPos = playerPos;
            _chaseLastMovedUtc = now;
        }
        else
        {
            float moved = playerPos.WorldDistanceXYTo(_chaseLastPos);
            if (moved > 0.75f)
            {
                _chaseLastPos = playerPos;
                _chaseLastMovedUtc = now;
            }

            if (dChase + 0.10f < _chaseProgBestDist)
            {
                _chaseProgBestDist = dChase;
                _chaseProgSinceUtc = now;
            }

            double sinceBestSec = (now - _chaseProgSinceUtc).TotalSeconds;
            double sinceMoveSec = (now - _chaseLastMovedUtc).TotalSeconds;

            if (sinceBestSec > 4.0 &&
                sinceMoveSec > 1.5 &&
                !waitingForPathResult &&
                Volatile.Read(ref pathRequestPending) == 0)
            {
                logger.LogWarning(
                    $"[NAV] No chase progress while stationary. " +
                    $"player={playerPos} chase={targetW} dChase={dChase:0.00} best={_chaseProgBestDist:0.00} " +
                    $"sinceBest={sinceBestSec:0.00}s sinceMove={sinceMoveSec:0.00}s");

                if (!_routeEscapeActive && now >= _chaseUnstuckCooldownUntilUtc)
                {
                    stopMoving.Stop();
                    stuckDetector.SetTargetLocation(StuckOwnerId, targetW);
                    stuckDetector.Update(StuckOwnerId, token);

                    _chaseUnstuckCooldownUntilUtc = now.AddMilliseconds(1400);
                    _chaseLastPos = playerPos;
                    _chaseLastMovedUtc = now;
                    return;
                }
                else if (!_routeEscapeActive && sinceBestSec > 6.0 && now >= noProgressRefillCooldownUntilUtc)
                {
                    noProgressRefillCooldownUntilUtc = now.AddMilliseconds(1500);

                    stopMoving.Stop();
                    routeToNextWaypoint.Clear();
                    SyncRouteStateToTop();
                    return;
                }
            }
        }

        if (stuckDetector.IsGettingCloser(StuckOwnerId))
        {
            AdjustHeading(heading, token);
        }
        else
        {
            if (stuckDetector.ActionDurationMs > 10_000)
            {
                if (mountHandler.IsMounted())
                    mountHandler.Dismount();

                LogClearRouteToWaypointStuck(logger, stuckDetector.ActionDurationMs);
                routeToNextWaypoint.Clear();
                SyncRouteStateToTop();
                return;
            }

            if (HasBeenActiveRecently())
            {
                // Try pather-based escape (10y -> 20y -> 30y forward) before falling
                // back to the random turn+move unstuck.
                if (!TryRouteUnstuck(token))
                {
                    stuckDetector.Update(StuckOwnerId, token);
                }
            }
        }

        lastWorldDistance = playerPos.WorldDistanceXYTo(targetW);
    }

    public void Resume()
    {
        active = true;
        ClearDestinationLatch();
        SetLastSafeAnchor(playerReader.WorldPos);
        logger.LogInformation(
            $"[NAV-DIAG] Resume() pre: stuckDetector.OwnerId={stuckDetector.OwnerId} " +
            $"Enabled={stuckDetector.Enabled} -> Acquire(StuckOwnerId={StuckOwnerId})");
        stuckDetector.Acquire(StuckOwnerId);
        ResetStuckParameters();
        ResetChaseProgressWatchdog();

        // Fix C: clear the failed-targets cache so each patrol session starts fresh.
        // Without this, a target that was unreachable in a previous session (e.g.,
        // before a death/resurrect or a route change) would still gate escape
        // projections in the new session. ResetStuckParameters above already
        // resets the RouteEscape state flags; this clears the learned cache.
        _routeEscapeFailedTargets.Clear();

        if (pather.GetType() != typeof(RemotePathingAPIV3) && routeToNextWaypoint.Count > 0)
        {
            V1_AttemptToKeepRouteToWaypoint();
        }

        int removed = 0;
        while (AdjustNextWaypointPointToClosest() && removed < 5) { removed++; };
        if (removed > 0)
        {
            UpdateTotalRoute();

            if (debug)
                LogDebug($"Resume: removed {removed} waypoint!");
        }
    }

    private void SetLastSafeAnchor(Vector3 w)
    {
        if (w == default)
            return;

        if (lastSafeAnchorW.WorldDistanceXYTo(w) < 0.05f)
            return;

        lastSafeAnchorW = w;
    }

    public void ClearAllRoutes()
    {
        routeToNextWaypoint.Clear();
        ClearDestinationLatch();
        escapeRouteInProgress = false;
        escapeActive = false;
        SyncRouteStateToTop();
    }

    public void SetSingleWaypoint(Vector3 worldOrMapPoint)
    {
        Span<Vector3> tmp = stackalloc Vector3[1] { worldOrMapPoint };
        SetWayPoints(tmp);
    }

    public void PausePathing()
    {
        active = false;

        waitingForPathResult = false;
        Interlocked.Exchange(ref pathRequestPending, 0);
        Volatile.Write(ref waitingRequestId, 0);

        logger.LogInformation(
            $"[NAV-DIAG] PausePathing() pre: stuckDetector.OwnerId={stuckDetector.OwnerId} " +
            $"Enabled={stuckDetector.Enabled} -> Release(StuckOwnerId={StuckOwnerId})");
        stuckDetector.Release(StuckOwnerId);

        // Fix BG (defense-in-depth, paired with the active check after
        // TryConsumeReachedWaypoint in Update): release the forward key
        // unconditionally when pausing. Callers SHOULD already call
        // navigation.StopMovement() before PausePathing (StopMovement also
        // calls input.StopForward), but a single PausePathing call without
        // a prior StopMovement used to leave the forward key in its
        // previous state. With navigation paused, no subsequent Update tick
        // releases the key on its own — `if (!active) return;` at the top
        // of Update means StartForward / StopForward sites are skipped.
        // Releasing the key here closes that hole.
        input.StopForward(true);
    }

    public void Stop()
    {
        active = false;
        ClearDestinationLatch();
        logger.LogInformation(
            $"[NAV-DIAG] Stop() pre: stuckDetector.OwnerId={stuckDetector.OwnerId} " +
            $"Enabled={stuckDetector.Enabled} -> Release(StuckOwnerId={StuckOwnerId})");
        stuckDetector.Release(StuckOwnerId);

        Volatile.Write(ref activePathRequestId, Interlocked.Increment(ref nextPathRequestId));
        waitingForPathResult = false;
        Interlocked.Exchange(ref pathRequestPending, 0);
        Volatile.Write(ref waitingRequestId, 0);

        // Fix AG (paired with PathRequest's drain): Stop() also bumps
        // activePathRequestId above, which makes every queued path request
        // stale (their reqIds are all smaller). Without draining, the
        // PathFinderThread would still process them one-by-one (~10 s each in
        // log-73's failed-search scenario), and their results would all be
        // stale-ignored. Worse: when CantFollow's projection escape calls
        // SetSingleWaypoint immediately after Stop() (FollowFocusGoal
        // EnterCantFollow path), its new path request lands at the tail of
        // that stale queue and waits behind every stale entry before
        // PathFinderThread can attempt the escape. In log-73 the queue had 7
        // stale entries at CantFollow entry, putting the escape ~80 s behind
        // real-time. See PathRequest's Fix AG comment for the full evidence.
        DrainStalePathRequests("stop");

        routeToNextWaypoint.Clear();
        escapeRouteInProgress = false;
        escapeActive = false;

        ResetStuckParameters();
        ResetNoProgressWatchdog();
        ResetChaseProgressWatchdog();
        UpdateTotalRoute();
    }

    public void StopMovement()
    {
        input.StopForward(true);
    }

    public bool HasWaypoint()
    {
        return wayPoints.Count != 0;
    }

    public bool HasNext()
    {
        return routeToNextWaypoint.Count != 0;
    }

    // Diagnostic properties for logging - not for control flow
    public bool Active => active;
    public int WaypointCount => wayPoints.Count;
    public int RouteCount => routeToNextWaypoint.Count;

    /// <summary>
    /// World-space coordinates of the current top waypoint — the leader's immediate
    /// navigation target. Returns <see langword="default"/> when no waypoints are queued.
    /// Used by <see cref="FollowRouteGoal"/> to publish the leader's current destination
    /// to <see cref="Core.Party.LeaderNavigationProvider"/> so assist bots can navigate
    /// to the same target (waypoint-sharing mode).
    /// </summary>
    public Vector3 TopWaypointW => wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()) : default;

    /// <summary>
    /// World-space coordinates of the topmost <i>non-blacklisted</i> waypoint in the
    /// stack. Walks top-down through <see cref="wayPoints"/> and returns the first
    /// entry that is not contained in any <see cref="AreaBlacklist"/> rect. Returns
    /// <see langword="default"/> when no waypoints are queued or all are blacklisted.
    ///
    /// <para>Why this exists (log-35 14:08:16:226 → 14:08:16:894+):
    /// <see cref="TryConsumeReachedWaypoint"/> pops the just-completed waypoint and
    /// fires <see cref="OnWayPointReached"/> <i>before</i>
    /// <see cref="SkipBlacklistedWaypoints"/> runs later in the same Update tick at
    /// line 759. If the new top is blacklisted, the event subscriber sees the bad
    /// waypoint via <see cref="TopWaypointW"/>; the leader publishes it to
    /// <see cref="Core.Party.LeaderNavigationProvider"/>; the assist polls it and
    /// sets it via <c>SetSingleWaypoint</c>; the assist's own
    /// <see cref="SkipBlacklistedWaypoints"/> immediately pops it; the assist's
    /// <c>OnDestinationReached</c> fires; the FFG handler re-fetches the same bad
    /// waypoint and sets it again — infinite loop every ~15ms.</para>
    ///
    /// <para>Use this property in <see cref="FollowRouteGoal"/> for publication to
    /// the API. Internal Navigation code should continue using <c>wayPoints.Peek()</c>
    /// directly (the in-process pipeline filters blacklists at the right step).</para>
    /// </summary>
    public Vector3 TopPublishableWaypointW
    {
        get
        {
            if (wayPoints.Count == 0)
                return default;

            if (AreaBlacklist == null)
                return Nav2D(wayPoints.Peek());

            // Stack<T>.GetEnumerator iterates top-down (LIFO order). First non-
            // blacklisted entry from the top is the publication candidate the
            // leader will navigate to once SkipBlacklistedWaypoints runs.
            foreach (Vector3 wp in wayPoints)
            {
                Vector3 w = Nav2D(wp);
                if (!AreaBlacklist.ContainsWorld(w))
                    return w;
            }

            return default;
        }
    }

    public Vector3 NextMapPoint()
    {
        return WorldMapAreaDB.ToMap_FlipXY(Nav2D(routeToNextWaypoint.Peek()), playerReader.WorldMapArea);
    }

    /// <summary>
    /// Project a target point that lies inside a blacklist outward along the line
    /// toward <paramref name="fromW"/> until the first point that is not contained
    /// in any blacklist rect, plus a small clearance margin. Returns
    /// <paramref name="targetW"/> unchanged when it's already outside all blacklists
    /// (idempotent) or when <see cref="AreaBlacklist"/> is null. Returns
    /// <paramref name="fromW"/> when the entire line from target to from lies inside
    /// the blacklist (i.e., no projection possible without overshooting the caller).
    /// </summary>
    /// <remarks>
    /// Fix 9 (log-39, 5 cycles of 25 s phantom AssistReturn): the assist enters
    /// CantFollow while standing inside a forbidden rect (FFG Fix 4/5 escalation
    /// when leader-assist segment is entirely blacklisted). The leader's
    /// AssistRequestReturn fires <see cref="FollowRouteGoal.GoToOneWaypoint"/>
    /// with the assist's recorded position — which is inside the blacklist.
    /// <see cref="SkipBlacklistedWaypoints"/> at line 776 pops this target on the
    /// next Update tick (player-outside-blacklist branch), <c>wayPoints.Count</c>
    /// drops to 0, <see cref="OnDestinationReached"/> fires with the leader still
    /// 17 y away from the assist. FRG logs "AssistReturn destination reached" but
    /// the leader never moved; assist's <c>UpdateCantFollow</c> never sees leader
    /// within <c>LeaderArrivedYards=6</c>, so it stays CantFollow indefinitely.
    /// Projection puts the AssistReturn target just outside the blacklist on the
    /// leader-side edge — close enough for the assist's UpdateCantFollow exit to
    /// fire when the leader arrives, unsticking the pair.
    /// </remarks>
    public Vector3 ProjectOutOfBlacklist(Vector3 targetW, Vector3 fromW)
    {
        if (AreaBlacklist == null)
            return targetW;

        if (!AreaBlacklist.ContainsWorld(Nav2D(targetW)))
            return targetW;

        float dx = fromW.X - targetW.X;
        float dy = fromW.Y - targetW.Y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);

        if (dist < 0.001f)
            return targetW;

        float invDist = 1f / dist;
        dx *= invDist;
        dy *= invDist;

        const float StepYards = 0.5f;
        const float MarginYards = 1.0f;

        int maxSteps = (int)(dist / StepYards) + 1;
        for (int i = 1; i <= maxSteps; i++)
        {
            float step = i * StepYards;
            if (step >= dist)
                break;

            Vector3 candidate = new Vector3(
                targetW.X + dx * step,
                targetW.Y + dy * step,
                targetW.Z);

            if (!AreaBlacklist.ContainsWorld(Nav2D(candidate)))
            {
                // Found first outside point. Add margin for clearance — confirms
                // the projected point isn't right against a rect edge where small
                // pathing wobble could re-enter.
                float marginStep = step + MarginYards;
                if (marginStep < dist)
                {
                    Vector3 marginCandidate = new Vector3(
                        targetW.X + dx * marginStep,
                        targetW.Y + dy * marginStep,
                        targetW.Z);
                    if (!AreaBlacklist.ContainsWorld(Nav2D(marginCandidate)))
                        return marginCandidate;
                }
                return candidate;
            }
        }

        // Line from targetW to fromW is entirely blacklisted (fromW also inside).
        // Caller should treat this as "no useful projection"; returning fromW
        // makes the caller's SetWayPoints a no-op (leader at its own position).
        return fromW;
    }

    public void SetWayPoints(Span<Vector3> points)
    {
        bool wasActive = active;
        if (!wasActive)
        {
            // Transitioning from paused/stopped → active. Re-Acquire stuckDetector
            // ownership; otherwise the detector remains in ownerId=0 (Released by
            // PausePathing or Stop), and all subsequent Update / IsGettingCloser /
            // SetTargetLocation calls from Navigation silently no-op (see
            // StuckDetector.IsOwner gate). Without this, the bot stops moving
            // because IsGettingCloser returns true unconditionally and AdjustHeading
            // never fires. Reproduces in AssistReturn after a distance-pause
            // (PausePathing → assistrequestreturn → SetWayPoints) and in
            // FFG.UpdateCantFollow recovery (Stop → SetSingleWaypoint).
            // Acquire is idempotent (resets internal timers); harmless when called
            // twice.
            stuckDetector.Acquire(StuckOwnerId);
        }
        logger.LogInformation(
            $"[NAV-DIAG] SetWayPoints(count={points.Length}) wasActive={wasActive} " +
            $"stuckDetector.OwnerId={stuckDetector.OwnerId} Enabled={stuckDetector.Enabled}" +
            (wasActive ? " (already active, no Acquire)" : " (was paused, re-Acquired)"));
        active = true;
        SetLastSafeAnchor(playerReader.WorldPos);
        wayPoints.Clear();
        routeToNextWaypoint.Clear();

        float mapDistanceXY = 0;
        WorldMapArea wma = playerReader.WorldMapArea;
        for (int i = points.Length - 1; i >= 0; i--)
        {
            Vector3 point = points[i];
            if (IsMapPoint(point))
            {
                point = WorldMapAreaDB.ToWorld_FlipXY(point, wma);
            }

            if (i != points.Length - 1)
            {
                Vector3 prev = wayPoints.Peek();
                mapDistanceXY += point.WorldDistanceXYTo(prev);
            }

            wayPoints.Push(Nav2D(point));
        }

        AvgDistance = wayPoints.Count > 1 ? Max(mapDistanceXY / wayPoints.Count, OutDoorMinDistance) : OutDoorMinDistance;

        escapeRouteInProgress = false;
        escapeActive = false;
        SyncRouteStateToTop();

        static bool IsMapPoint(Vector3 p)
        {
            return
                p.X is >= 0 and <= 100 &&
                p.Y is >= 0 and <= 100;
        }
    }

    /// <summary>
    /// ── Route-walking migration: Turn 1 (log-84 baseline → Turn 1 commit) ──
    ///
    /// Loads the assist's view of the patrol route into <see cref="LoadedRoute"/>
    /// from <c>this.pathSettings.Path</c>, without affecting the active
    /// wayPoints stack. Called by FFG at OnEnter on the assist side. The
    /// leader's FRG does NOT use this — FRG calls
    /// SetWayPoints(pathSettings.Path) directly because the leader USES the
    /// active stack to navigate.
    ///
    /// Why this lives on Navigation rather than FFG: on the assist, the DI
    /// container can't resolve PathSettings as a direct constructor
    /// dependency of FFG (PathSettings registration appears scoped to the
    /// path-aware service tree that Navigation participates in, but not
    /// goals). Navigation already has pathSettings as a constructor field,
    /// so we keep the read inside Navigation and expose a parameterless
    /// method to callers.
    ///
    /// Turn 1 scope: purely additive. LoadedRoute is populated but nothing
    /// reads from it. Verifies the assist can hold route data and the route
    /// file is accessible. Turn 2 will introduce the waypoint-index state
    /// machine and consume from LoadedRoute.
    ///
    /// Coord conversion: input points may be in map coords (0-100 range) or
    /// world coords; we normalize to world coords at load time so Turn 2's
    /// consumption logic doesn't repeat the check. Mirrors the IsMapPoint
    /// check in <see cref="SetWayPoints"/>.
    ///
    /// Safety: if pathSettings.Path is null or empty (e.g., assist class
    /// config has no route configured), LoadedRoute is set to empty and a
    /// warning is logged. The bot continues with body-chase behavior — no
    /// crash, no behavior change.
    /// </summary>
    public void LoadRoute()
    {
        Vector3[] routePoints = pathSettings.Path;
        if (routePoints == null || routePoints.Length == 0)
        {
            LoadedRoute = Array.Empty<Vector3>();
            logger.LogWarning(
                "[NAV] [ROUTE-LOAD] empty or null route in pathSettings.Path — " +
                "route-walking will be unavailable. Assist will continue " +
                "with body-chase behavior. Verify the assist class config " +
                "specifies a PathFilename pointing at the same route file " +
                "the leader uses.");
            return;
        }

        WorldMapArea wma = playerReader.WorldMapArea;
        Vector3[] converted = new Vector3[routePoints.Length];
        int mapConvertedCount = 0;
        for (int i = 0; i < routePoints.Length; i++)
        {
            Vector3 p = routePoints[i];
            if (p.X is >= 0 and <= 100 && p.Y is >= 0 and <= 100)
            {
                p = WorldMapAreaDB.ToWorld_FlipXY(p, wma);
                mapConvertedCount++;
            }
            converted[i] = Nav2D(p);
        }

        LoadedRoute = converted;

        logger.LogInformation(
            $"[NAV] [ROUTE-LOAD] Loaded {converted.Length} route waypoints " +
            $"(mapConverted={mapConvertedCount}). " +
            $"First=<{converted[0].X:0.0},{converted[0].Y:0.0}>, " +
            $"Last=<{converted[^1].X:0.0},{converted[^1].Y:0.0}>. " +
            $"All coords normalized to world space.");
    }

    public void ResetStuckParameters()
    {
        stuckDetector.Reset(StuckOwnerId);
        ResetRouteEscape();
    }

    /// <summary>
    /// Called by goals each time they press the Approach/Interact key.
    /// Navigation keeps the position only when genuine forward progress has been
    /// made since the last recorded position — this gives a reliable approach
    /// direction vector for pather-based stuck escape.
    /// </summary>
    public void RecordApproachPosition(Vector3 worldPos)
    {
        if (_approachRecordedW == default)
        {
            _approachRecordedW = worldPos;
            _approachEscapeAnchorW = worldPos;
            return;
        }

        float moved = worldPos.WorldDistanceXYTo(_approachRecordedW);
        if (moved >= ApproachEscapeProgressMinW)
        {
            _approachPrevRecordedW = _approachRecordedW;
            _approachRecordedW = worldPos;
            if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Trace))
                logger.LogTrace($"[NAV] RecordApproach: ACCEPTED prev={_approachPrevRecordedW} curr={_approachRecordedW} moved={moved:0.00}y (threshold={ApproachEscapeProgressMinW:0.00}y)");
        }
        else
        {
            if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                logger.LogDebug($"[NAV] RecordApproach: REJECTED moved={moved:0.00}y < threshold={ApproachEscapeProgressMinW:0.00}y — pair unchanged: prev={_approachPrevRecordedW} curr={_approachRecordedW}");
        }
    }

    /// <summary>
    /// Shared add-or-dedupe core for both <see cref="AddStuckRect"/> (locally-
    /// discovered, <c>expiryUtc == DateTime.MaxValue</c>) and
    /// <see cref="AddPropagatedStuckRect"/> (propagated, finite TTL).
    ///
    /// <para><b>Fix AW (Guards 1+2) — applies to Route A propagation safety.</b></para>
    ///
    /// <para>
    /// <b>Guard 1 — skip-if-inside (propagated only):</b> If the player is
    /// currently inside the proposed rect, refuse to add. Locally-discovered
    /// rects compute <c>center</c> forward of the player by construction
    /// (see <see cref="AddStuckRect"/>'s forward-offset math), so this
    /// guard is a no-op for the local path. For propagated rects the assist
    /// may be at arbitrary terrain when the rect arrives — and if the rect
    /// lands on top of the assist, adding it would force PPather to reject
    /// the assist's start position on every subsequent path request, which
    /// can suppress the assist's own stuck-detection from upgrading the
    /// rect's lifetime (Guard 2 below). The leader keeps republishing each
    /// poll cycle, so this is a retry-friendly skip: once the assist moves
    /// out, the next propagation succeeds.
    /// </para>
    ///
    /// <para>
    /// <b>Guard 2 — upgrade propagated → local on overlap:</b> If a local
    /// stuck event lands inside a rect that is currently propagated (finite
    /// expiry), promote that rect's expiry to <see cref="DateTime.MaxValue"/>.
    /// The local detection is authoritative — the assist's own
    /// <c>TickIdleStuckDetection</c> has now confirmed the terrain is bad —
    /// so the rect's lifetime should be governed by
    /// <see cref="ClearStuckRects"/> (plan transitions), not by the 90 s
    /// propagation TTL. Without this guard, the rect could expire while
    /// the assist is still struggling at the same spot, and the assist's
    /// own confirmation would be silently lost to dedup.
    /// </para>
    ///
    /// <para>
    /// <b>Dedup matrix on overlap:</b><br/>
    ///   local → local           : keep existing (Info log — legacy behavior preserved).<br/>
    ///   local → propagated      : <i>Guard 2</i> upgrade existing to MaxValue (Info log).<br/>
    ///   propagated → local      : silent no-op (local owns the area; this is just the leader re-broadcasting).<br/>
    ///   propagated → propagated : refresh existing TTL to <paramref name="expiryUtc"/> (Debug log).
    /// </para>
    ///
    /// <para>
    /// Returns <c>true</c> iff a new rect was appended to <see cref="_stuckWorldRects"/>.
    /// Append-only path also rebuilds <see cref="_areaBlacklist"/>.
    /// </para>
    /// </summary>
    private bool TryAddStuckRectInternal(Vector3 center, float halfSize, DateTime expiryUtc)
    {
        bool isLocal = expiryUtc == DateTime.MaxValue;

        // Guard 1: skip-if-inside (propagated only). Local rects are produced
        // forward of the player and cannot collide with the player's current
        // position; locally-added rects never trigger this guard.
        if (!isLocal)
        {
            Vector3 playerPos = playerReader.WorldPos;
            if (MathF.Abs(playerPos.X - center.X) <= halfSize &&
                MathF.Abs(playerPos.Y - center.Y) <= halfSize)
            {
                logger.LogWarning(
                    $"[NAV] Propagated rect REJECTED (Guard 1): bot is currently inside " +
                    $"the proposed rect (player={playerPos}, center={center}, ±{halfSize}y). " +
                    $"Will retry on next propagation cycle once the bot moves out.");
                return false;
            }
        }

        // Dedup against existing rects.
        for (int i = 0; i < _stuckWorldRects.Count; i++)
        {
            if (_stuckWorldRects[i].Contains(new System.Numerics.Vector2(center.X, center.Y)))
            {
                DateTime existingExpiry = _stuckRectExpiryUtc[i];
                bool existingIsLocal = existingExpiry == DateTime.MaxValue;

                if (isLocal && !existingIsLocal)
                {
                    // Guard 2: local stuck event overlapping a propagated rect.
                    // Promote the existing entry's lifetime to permanent.
                    _stuckRectExpiryUtc[i] = DateTime.MaxValue;
                    logger.LogInformation(
                        $"[NAV] StuckRect dedup (Guard 2): existing propagated rect [{i}] " +
                        $"promoted to local — bot's own stuck detection confirms the " +
                        $"terrain at center={center} (was expiring at {existingExpiry:HH:mm:ss.fff}).");
                }
                else if (!isLocal && !existingIsLocal)
                {
                    // Propagated → propagated: refresh TTL so the rect stays
                    // alive as long as the leader keeps republishing.
                    _stuckRectExpiryUtc[i] = expiryUtc;
                    if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                    {
                        logger.LogDebug(
                            $"[NAV] StuckRect dedup: refreshed TTL on existing propagated " +
                            $"rect [{i}] (center={center}, new expiry={expiryUtc:HH:mm:ss.fff}).");
                    }
                }
                else if (isLocal && existingIsLocal)
                {
                    // Local → local: legacy log preserved (pre-Fix AW behavior).
                    logger.LogInformation(
                        $"[NAV] StuckRect skipped: center={center} already inside existing " +
                        $"rect [{i}] — no new rect added (total={_stuckWorldRects.Count}).");
                }
                // else: propagated → local. Silent no-op. Local rect owns
                // the terrain — this is just the leader re-broadcasting
                // something we've already locally confirmed and promoted.

                return false;
            }
        }

        // No overlap — append.
        var rect = new BlacklistRect(
            center.X - halfSize, center.Y - halfSize,
            center.X + halfSize, center.Y + halfSize
        ).Normalized();

        _stuckWorldRects.Add(rect);
        _stuckRectExpiryUtc.Add(expiryUtc);
        _areaBlacklist = new CompositeAreaBlacklist(_staticAreaBlacklist, new RectBlacklist(_stuckWorldRects));
        return true;
    }

    /// <summary>
    /// Boxes a region around the stuck position as a blacklisted area so the pather
    /// routes around it on future requests.
    /// </summary>
    private void AddStuckRect(Vector3 posW, Vector3 forwardDir = default)
    {
        Vector3 center = posW;

        if (forwardDir != default)
        {
            float len = MathF.Sqrt(forwardDir.X * forwardDir.X + forwardDir.Y * forwardDir.Y);
            if (len > 0.01f)
            {
                float offsetY = 5f;
                center = new Vector3(
                    posW.X + (forwardDir.X / len) * offsetY,
                    posW.Y + (forwardDir.Y / len) * offsetY,
                    0f);
            }
        }

        // Fix AW: dedup + append + Guard 2 (propagated→local upgrade) live
        // in the shared helper. AddStuckRect is the local-path entry —
        // expiry is always MaxValue. Guard 1 is bypassed for local adds
        // (the helper checks `isLocal` first).
        bool added = TryAddStuckRectInternal(center, StuckRectHalfSizeY, DateTime.MaxValue);

        if (added)
        {
            logger.LogWarning(
                $"[NAV] StuckRect added: center={center} (player={posW}) ±{StuckRectHalfSizeY}y " +
                $"(total={_stuckWorldRects.Count})");

            // Fix AV: notify subscribers (intended: leader-side propagation
            // service) so the rect can be forwarded to the assist via IPC.
            // Fires ONLY on actual append — Guard 2 promotions and dedup
            // skips do not re-broadcast (the leader would either already
            // know about the area or not need to know).
            OnStuckRectAdded?.Invoke(center, StuckRectHalfSizeY);
        }
    }

    public bool TryUnstuck(CancellationToken token = default)
    {
        if (_approachEscapeActive)
        {
            if (!active)
            {
                logger.LogInformation("[NAV] ApproachEscape: navigation stopped externally — clearing escape.");
                ResetApproachEscape();
                return false;
            }

            var now2 = DateTime.UtcNow;
            Vector3 currentPos = Nav2D(playerReader.WorldPos);
            double escapeSec = (now2 - _approachEscapeStartUtc).TotalSeconds;

            int heartbeatSec = (int)escapeSec;
            if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug) &&
                heartbeatSec != _approachEscapeHeartbeatLastSec)
            {
                _approachEscapeHeartbeatLastSec = heartbeatSec;
                double timeoutSecDbg = _approachEscapeCurrentYards * ApproachEscapeTimeoutSecPerYard;
                logger.LogDebug($"[NAV] TryUnstuck(active): yards={_approachEscapeCurrentYards:0} " +
                    $"escapeSec={escapeSec:0.0}s budget={timeoutSecDbg:0.0}s " +
                    $"startUtc={_approachEscapeStartUtc:HH:mm:ss.fff} " +
                    $"progressPos={_approachEscapeLastProgressPos} " +
                    $"guid={_approachEscapeTargetGuid}");
            }

            if (_approachEscapeLastProgressPos == default)
            {
                _approachEscapeLastProgressPos = currentPos;
                _approachEscapeLastProgressUtc = now2;
            }
            else if (currentPos.WorldDistanceXYTo(_approachEscapeLastProgressPos) >= 2.0f)
            {
                _approachEscapeLastProgressPos = currentPos;
                _approachEscapeLastProgressUtc = now2;
            }

            double sinceProgressSec = (now2 - _approachEscapeLastProgressUtc).TotalSeconds;
            bool noProgress = sinceProgressSec >= ApproachEscapeNoMovementSec;
            double timeoutSec = _approachEscapeCurrentYards * ApproachEscapeTimeoutSecPerYard;

            if (escapeSec > 2.0 * timeoutSec && _approachEscapeStartUtc != DateTime.MinValue)
            {
                logger.LogWarning($"[NAV] ApproachEscape: startUtc is stale (escapeSec={escapeSec:0.1}s > 2x budget={timeoutSec:0.1}s for {_approachEscapeCurrentYards:0}y) — resetting to now. " +
                    $"[staleUtc={_approachEscapeStartUtc:HH:mm:ss.fff} yards={_approachEscapeCurrentYards:0} guid={_approachEscapeTargetGuid}]");
                _approachEscapeStartUtc = now2;
                _approachEscapeLastProgressPos = currentPos;
                _approachEscapeLastProgressUtc = now2;
                escapeSec = 0.0;
                sinceProgressSec = 0.0;
            }

            bool timedOut = escapeSec >= timeoutSec;

            if (noProgress || timedOut)
            {
                string reason = noProgress
                    ? $"no progress for {sinceProgressSec:0.0}s (stuck at {currentPos})"
                    : $"total time {escapeSec:0.0}s exceeded {timeoutSec:0.0}s budget for {_approachEscapeCurrentYards:0}y";
                logger.LogWarning($"[NAV] ApproachEscape: escape stuck ({reason}) — clearing to retry at larger distance. " +
                    $"[diag: startUtc={_approachEscapeStartUtc:HH:mm:ss.fff} yards={_approachEscapeCurrentYards:0} guid={_approachEscapeTargetGuid}]");

                float totalDisplacement = currentPos.WorldDistanceXYTo(_approachEscapeStartPos);
                if (totalDisplacement < 0.5f)
                {
                    IsApproachEscapePhysicallyStuck = true;
                    logger.LogWarning($"[NAV] ApproachEscape: zero movement ({totalDisplacement:0.00}y) — character physically trapped in terrain.");
                }

                _approachEscapeActive = false;
                _approachEscapeStartUtc = DateTime.MinValue;
                _approachEscapeHeartbeatLastSec = -1;
                Stop();
                stopMoving.Stop();

                if (_approachEscapeCurrentYards >= ApproachEscapeEndYards)
                {
                    logger.LogWarning("[NAV] ApproachEscape: max escalation level stuck — marking exhausted immediately.");
                    IsApproachEscapeExhausted = true;
                    _approachEscapeLastAttemptUtc = DateTime.UtcNow.AddSeconds(PostExhaustionCooldownSec);
                }

                return false;
            }

            return true;
        }

        var now = DateTime.UtcNow;

        double gateElapsedMs = (now - _approachEscapeLastAttemptUtc).TotalMilliseconds;
        if (gateElapsedMs < 200)
        {
            if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                logger.LogDebug($"[NAV] TryUnstuck: 200ms gate — {gateElapsedMs:0}ms elapsed (need 200ms) — skipping.");
            return false;
        }

        if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            logger.LogDebug($"[NAV] TryUnstuck: lower-block entry — yards={_approachEscapeCurrentYards:0} (will become {_approachEscapeCurrentYards + 10:0}) " +
                $"gateElapsed={gateElapsedMs:0}ms lastAttempt={_approachEscapeLastAttemptUtc:HH:mm:ss.fff} " +
                $"lockedCurr={_approachEscapeLockedRecordedW} lockedPrev={_approachEscapeLockedPrevRecordedW} " +
                $"stuckRects={_stuckWorldRects.Count} guid={_approachEscapeTargetGuid}");

        _approachEscapeLastAttemptUtc = now;
        _approachEscapeCurrentYards += 10f;

        if (_approachEscapeTargetGuid == 0)
            _approachEscapeTargetGuid = playerReader.TargetGuid;

        if (_approachEscapeLockedRecordedW == default)
        {
            _approachEscapeLockedRecordedW = _approachRecordedW;
            bool anchorUsable = _approachEscapeAnchorW != default &&
                                 _approachEscapeAnchorW != _approachRecordedW;
            _approachEscapeLockedPrevRecordedW = anchorUsable
                ? _approachEscapeAnchorW
                : _approachPrevRecordedW;
            float lockDisp = _approachEscapeLockedPrevRecordedW != default
                ? _approachEscapeLockedRecordedW.WorldDistanceXYTo(_approachEscapeLockedPrevRecordedW)
                : 0f;
            logger.LogInformation($"[NAV] ApproachEscape: locking approach direction — " +
                $"prev={_approachEscapeLockedPrevRecordedW} curr={_approachEscapeLockedRecordedW} " +
                $"disp={lockDisp:0.00}y (anchorUsed={anchorUsable})");

            Vector3 approachDir;
            if (_approachRecordedW != default && _approachPrevRecordedW != default)
            {
                Vector3 dirFrom = (_approachEscapeAnchorW != default && _approachEscapeAnchorW != _approachRecordedW)
                    ? _approachEscapeAnchorW
                    : _approachPrevRecordedW;
                approachDir = new Vector3(
                    _approachRecordedW.X - dirFrom.X,
                    _approachRecordedW.Y - dirFrom.Y,
                    0f);
            }
            else if (_chaseProgTarget != default)
            {
                Vector3 currentW = Nav2D(playerReader.WorldPos);
                approachDir = new Vector3(
                    _chaseProgTarget.X - currentW.X,
                    _chaseProgTarget.Y - currentW.Y,
                    0f);
                logger.LogInformation("[NAV] ApproachEscape: using chase target for stuck rect direction.");
            }
            else if (_stuckWorldRects.Count > 0)
            {
                var prevRect = _stuckWorldRects[_stuckWorldRects.Count - 1];
                float rcx = (prevRect.MinX + prevRect.MaxX) * 0.5f;
                float rcy = (prevRect.MinY + prevRect.MaxY) * 0.5f;
                Vector3 currentW = Nav2D(playerReader.WorldPos);
                approachDir = new Vector3(rcx - currentW.X, rcy - currentW.Y, 0f);
                logger.LogInformation($"[NAV] ApproachEscape: using previous stuck rect center {new Vector3(rcx, rcy, 0f)} for approach direction.");
            }
            else
            {
                logger.LogWarning("[NAV] ApproachEscape: no direction data (prev=<0,0,0>, no stuck rects, " +
                    "no chase target) — SKIPPING stuck rect to avoid player-inside-own-rect. " +
                    "Pather escape runs without rect hint. (Typically caused by BUG B state wipe.)");
                goto AFTER_STUCK_RECT;
            }
            if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
            {
                float dirLen = Sqrt(approachDir.X * approachDir.X + approachDir.Y * approachDir.Y);
                logger.LogDebug($"[NAV] ApproachEscape: stuck rect approachDir=({approachDir.X:0.00},{approachDir.Y:0.00}) len={dirLen:0.00} " +
                    $"player={Nav2D(playerReader.WorldPos)}");
            }
            AddStuckRect(Nav2D(playerReader.WorldPos), approachDir);
            AFTER_STUCK_RECT:;
        }

        if (_approachEscapeCurrentYards > ApproachEscapeEndYards)
        {
            logger.LogWarning(
                "[NAV] ApproachEscape: all pather attempts exhausted — bail-out window open for " +
                $"{PostExhaustionCooldownSec:0}s.");
            ResetApproachEscape();
            IsApproachEscapeExhausted = true;
            _approachEscapeLastAttemptUtc = DateTime.UtcNow.AddSeconds(PostExhaustionCooldownSec);
            stuckDetector.SetTargetLocation(StuckOwnerId, _approachRecordedW == default
                ? Nav2D(playerReader.WorldPos)
                : _approachRecordedW);
            stuckDetector.Update(StuckOwnerId, token);
            return false;
        }

        Vector3 projection = ProjectApproachEscapeTarget(_approachEscapeCurrentYards);

        Vector3 projectionMap = WorldMapAreaDB.ToMap_FlipXY(projection, playerReader.WorldMapArea);
        if (projectionMap.X is < 0 or > 100 || projectionMap.Y is < 0 or > 100)
        {
            logger.LogWarning(
                $"[NAV] ApproachEscape: projected target at {_approachEscapeCurrentYards:0}y is outside map bounds {projectionMap} — skipping.");
            _approachEscapeActive = false;
            return false;
        }

        logger.LogInformation(
            $"[NAV] ApproachEscape: attempt {_approachEscapeCurrentYards:0}y -> {projection}");

        IsApproachEscapeExhausted = false;
        _approachEscapeHeartbeatLastSec = -1;
        SetSingleWaypoint(projection);
        _approachEscapeActive = true;
        _approachEscapeStartUtc = DateTime.UtcNow;
        _approachEscapeStartPos = Nav2D(playerReader.WorldPos);
        _approachEscapeLastProgressPos = default;
        _approachEscapeLastProgressUtc = DateTime.MinValue;
        return true;
    }

    public bool TryRouteUnstuck(CancellationToken token = default)
    {
        if (_routeEscapeActive)
        {
            if (!active)
            {
                logger.LogInformation("[NAV] RouteEscape: navigation stopped externally — clearing escape.");
                ResetRouteEscape();
                return false;
            }

            var now2 = DateTime.UtcNow;
            Vector3 currentPos = Nav2D(playerReader.WorldPos);
            double escapeSec = (now2 - _routeEscapeStartUtc).TotalSeconds;

            if (_routeEscapeLastProgressPos == default)
            {
                _routeEscapeLastProgressPos = currentPos;
                _routeEscapeLastProgressUtc = now2;
            }
            else if (currentPos.WorldDistanceXYTo(_routeEscapeLastProgressPos) >= 2.0f)
            {
                _routeEscapeLastProgressPos = currentPos;
                _routeEscapeLastProgressUtc = now2;
            }

            double sinceProgressSec = (now2 - _routeEscapeLastProgressUtc).TotalSeconds;
            bool noProgress = sinceProgressSec >= RouteEscapeNoMovementSec;
            double timeoutSec = _routeEscapeCurrentYards * RouteEscapeTimeoutSecPerYard;
            bool timedOut = escapeSec >= timeoutSec;

            if (noProgress || timedOut)
            {
                string reason = noProgress
                    ? $"no progress for {sinceProgressSec:0.0}s"
                    : $"timed out after {escapeSec:0.0}s at {_routeEscapeCurrentYards:0}y";

                float totalDisplacement = currentPos.WorldDistanceXYTo(Nav2D(_routeEscapeStartPos));
                bool physicallyTrapped = totalDisplacement < 0.5f;

                logger.LogWarning($"[NAV] RouteEscape: escape stuck ({reason}) — " +
                    $"displacement={totalDisplacement:0.00}y physTrapped={physicallyTrapped}. Escalating.");

                _routeEscapeActive = false;

                if (physicallyTrapped)
                {
                    logger.LogWarning("[NAV] RouteEscape: physically trapped — injecting jump + reverse to escape.");
                    TryPhysicalUnstuck();

                    // ── Fix BA (log-82 19:06:37:899 → 19:06:40:984) ──
                    //
                    // ROUTE-ESCAPE STUCK-RECT ADDITION ON PHYSTRAPPED
                    //
                    // When a RouteEscape attempt finishes with displacement < 0.5y
                    // (physTrapped=True), the bot was unable to move at all in
                    // the attempted direction. This is strong evidence that the
                    // immediate terrain in that direction is blocked. Without
                    // marking it, the pather has no memory of the failure: the
                    // next path request from the area happily routes through
                    // the same bad terrain and the bot gets stuck again at the
                    // same spot.
                    //
                    // Evidence (log-82, mob N post-combat):
                    //   19:06:37:431  FFG: Stuck while navigating at <-740.48,
                    //                 -4290.48>. Reporting Stuck so leader pauses.
                    //   19:06:37:899  RouteEscape attempt 10y → <-736.08, -4299.46>
                    //                 (facing=5.17rad, offset=0°). Direction = SE.
                    //   19:06:40:984  RouteEscape: escape stuck (no progress for
                    //                 3.1s) — displacement=0.00y physTrapped=True.
                    //                 Escalating. → physical-unstuck (jump+reverse)
                    //                 fires.
                    //   19:06:42:695  FFG: Movement resumed (displaced 4.72y via
                    //                 jump+reverse). Reverts to NavigatingToLeader.
                    //                 → normal nav resumes, leader-pursuit path
                    //                 routes back through the same bad terrain.
                    //   19:06:48:598  Stuck AGAIN at <-740.56, -4290.56> (0.1y from
                    //                 the original stuck position).
                    //
                    // The same pattern repeated 3 more times across the 65-second
                    // stuck window (Stuck at <-740.87,-4290.86>, <-740.10,-4290.10>,
                    // <-739.52,-4289.51>) before the leader gave up at 19:07:36:990.
                    //
                    // Fix: when physTrapped fires, call AddStuckRect with the bot's
                    // attempt-start position and the failed direction (escape
                    // target − start position). AddStuckRect's existing semantics
                    // offset the rect center 5y in the supplied forward direction,
                    // matching how ATG's ApproachEscape places its rects: the bot's
                    // current position is NOT inside the rect, only the immediate
                    // terrain ahead.
                    //
                    // Multiple physTrapped failures within the same escape ladder
                    // produce multiple rects (one per failed direction). The
                    // existing dedup in AddStuckRect handles overlapping rects.
                    //
                    // Why physTrapped < 0.5y specifically (not non-physTrapped
                    // failures): non-physTrapped failures (e.g., displacement=4.5y
                    // but didn't reach escape target) often indicate FFG-overwrite
                    // (Fix AZ covers this) or pather-finds-suboptimal-route, NOT
                    // bad terrain. physTrapped is the conclusive "this direction
                    // is blocked" signal — the bot pressed forward keys for 3s and
                    // didn't budge.
                    //
                    // Why offset 5y in failed direction (not at start position):
                    //   - At-start would put the rect AROUND the bot's escape-anchor
                    //     position. Future paths starting from there would have
                    //     start-inside-rect → pather rejects → no path computed.
                    //   - 5y forward marks where the obstacle is (the bot was
                    //     pressing into it for 3s). Paths through the area route
                    //     around the obstacle without rejecting the start position.
                    //
                    // Pairs with Fix AZ: Fix AZ prevents FFG from overwriting the
                    // CURRENT escape route mid-execution. Fix BA prevents future
                    // paths from being computed through the same bad terrain. Both
                    // are needed: without AZ, the escape can't complete; without BA,
                    // the escape completes but the next nav cycle immediately routes
                    // back into the stuck zone.
                    Vector3 failedDir = _routeEscapeAttemptTarget - _routeEscapeStartPos;
                    logger.LogWarning(
                        $"[NAV] [FIX-FIRE] BA: adding StuckRect after physTrapped — " +
                        $"startPos={_routeEscapeStartPos} attemptTarget={_routeEscapeAttemptTarget} " +
                        $"failedDir=<{failedDir.X:0.00},{failedDir.Y:0.00}>. " +
                        $"Future paths through this terrain will route around.");
                    AddStuckRect(_routeEscapeStartPos, failedDir);
                }

                return false;
            }

            return true;
        }

        var now = DateTime.UtcNow;

        if ((now - _routeEscapeLastAttemptUtc).TotalMilliseconds < 200)
            return false;

        _routeEscapeLastAttemptUtc = now;
        _routeEscapeCurrentYards += 10f;

        if (_routeEscapeCurrentYards > RouteEscapeEndYards)
        {
            logger.LogWarning("[NAV] RouteEscape: all pather attempts exhausted — falling back to random unstuck.");
            ResetRouteEscape();
            return false;
        }

        float facing = playerReader.Direction;

        // Fix W (log-65 20:57:13 → 20:58:56, ~93s stuck): rotate the escape
        // projection direction on each escalation so successive attempts
        // probe different cardinal sectors rather than all landing in the
        // same direction.
        //
        // The original code projected every attempt in raw facing direction.
        // When a bot gets stuck against a physical obstacle (a tree, a rock,
        // a wall), its facing is whatever direction it was last trying to
        // walk in — which is precisely INTO the obstacle. The 10y, 20y, and
        // 30y attempts then all project the same vector deeper into / past
        // the same blocker. The failed-targets cache (Fix C) catches the
        // duplicate at the same-direction step and skips, but only after
        // RouteEscapeUnreachableTimeoutSec (12s) has marked the previous
        // attempt as unreachable. With three attempts all in one direction,
        // the bot wastes ~32s minimum (8s + 12s + 12s) before exhausting
        // and falling through to TryPhysicalUnstuck — and even then,
        // physical unstuck just turns the bot a small amount; if the route
        // re-enters with the same facing on the next stuck cycle, the
        // pattern repeats.
        //
        // Observed in log-65: the assist's path through a tree had a curved
        // 14-node route that bent NE-then-S around the obstacle. The bot
        // walked it, got stuck on the east side facing SE. All three
        // RouteEscape attempts (10y/20y/30y) projected SE — further around
        // the tree, even further from the leader (which was SW). Total
        // wasted time: ~93s across three cycles, with StuckDetector's
        // physical turn+move eventually rotating the bot enough that the
        // third 30y attempt found a clear angle. Until then the leader was
        // paused for assist and could not advance.
        //
        // Strategy: keep first attempt at facing (preserves the "just push
        // through" recovery for transient stalls — bot stopped against a
        // bump it can resolve by trying harder forward), but rotate
        // subsequent attempts so the sweep covers ~240° around the bot.
        // 120° steps are wide enough that the failed-targets cache (5y
        // clearance) won't suppress them as duplicates of the previous
        // attempt, and any single direction that's clear gets tried within
        // three attempts.
        //
        //   Attempt 1 (10y):  facing                  (push forward)
        //   Attempt 2 (20y):  facing + 120°           (NW-ish if E)
        //   Attempt 3 (30y):  facing - 120°           (SW-ish if E)
        //
        // _routeEscapeCurrentYards uniquely identifies the attempt index
        // (10/20/30) since it was incremented just above and the escalation
        // ceiling bail-out at line 2055-2060 catches values > 30. No
        // additional state needed.
        const float RotateOffset = PI * 2f / 3f; // 120° in radians
        float angleOffset;
        if (_routeEscapeCurrentYards <= RouteEscapeStartYards + 0.5f)       // 10y
            angleOffset = 0f;
        else if (_routeEscapeCurrentYards <= RouteEscapeStartYards + 10.5f) // 20y
            angleOffset = RotateOffset;
        else                                                                 // 30y
            angleOffset = -RotateOffset;
        float escapeAngle = facing + angleOffset;

        Vector3 playerW = Nav2D(playerReader.WorldPos);
        Vector3 escapeW = new Vector3(
            playerW.X + Cos(escapeAngle) * _routeEscapeCurrentYards,
            playerW.Y + Sin(escapeAngle) * _routeEscapeCurrentYards,
            0f);

        // Fix C: skip projection if it falls onto a known-unreachable target the
        // watchdog (CheckRouteEscapeUnreachable) recently abandoned. Without this,
        // a 20y/30y escalation in the same direction would land near the same
        // failed spot, waste another full timeout cycle, and feed the watchdog
        // again — eating up to 8s + 16s + 24s = 48s of wasted attempts before
        // exhausting. Returning false routes execution back to
        // stuckDetector.Update; the next stuck cycle (if any) re-enters here at
        // the next yards level. Note: _routeEscapeCurrentYards has ALREADY been
        // incremented above (line 1676), so the next entry will project at the
        // higher level; we don't need to escalate again here.
        for (int i = 0; i < _routeEscapeFailedTargets.Count; i++)
        {
            Vector3 failed = _routeEscapeFailedTargets[i];
            float clearance = escapeW.WorldDistanceXYTo(failed);
            if (clearance < RouteEscapeFailedTargetClearance)
            {
                logger.LogInformation(
                    $"[NAV] RouteEscape: skipping {_routeEscapeCurrentYards:0}y projection {escapeW} — " +
                    $"too close to known-unreachable target {failed} " +
                    $"(clearance={clearance:0.00}y < {RouteEscapeFailedTargetClearance}y).");
                return false;
            }
        }

        logger.LogInformation($"[NAV] RouteEscape: attempt {_routeEscapeCurrentYards:0}y -> {escapeW} (facing={facing:0.00}rad, offset={angleOffset * 180f / PI:+0.0;-0.0;0}°)");

        SetSingleWaypoint(escapeW);
        _routeEscapeActive = true;
        // Fix BA (log-82 19:06:37:899): record the escape target so the
        // physTrapped branch in TryRouteUnstuck can reconstruct the failed
        // direction (escapeW − startPos) when adding a stuck rect. Stored
        // here at attempt-fire because escapeW is a local computed value;
        // the rect-add site in the failure branch needs to know it.
        _routeEscapeAttemptTarget = escapeW;
        _routeEscapeStartUtc = now;
        _routeEscapeStartPos = playerW;
        // ── Fix AT (log-80 15:07:25:070 → 15:07:29:812+) ──
        //
        // Initialize the progress tracker at escape-start rather than
        // leaving it as (default, MinValue) for TryRouteUnstuck to lazy-
        // init on its first call. The lazy-init pattern stacks with the
        // StuckDetector's 3 s ACTION_STUCK_TIME grace on the line ~1740
        // call site:
        //
        //   1. Escape starts. SetWayPoints clears + pushes the single
        //      escape waypoint. SyncRouteStateToTop calls
        //      stuckDetector.SetTargetLocation(escape) → ResetInternal()
        //      → startTime = now → IsGettingCloser returns TRUE for the
        //      next 3 s regardless of motion (ACTION_STUCK_TIME = 3 s,
        //      see StuckDetector.cs line 32).
        //   2. For those 3 s the line ~1740 path
        //      (`if (!stuckDetector.IsGettingCloser) TryRouteUnstuck(...)`)
        //      does NOT call TryRouteUnstuck — so its internal noProgress
        //      timer (RouteEscapeNoMovementSec = 3 s) never starts.
        //   3. At t+3 s the StuckDetector's grace expires →
        //      IsGettingCloser returns FALSE → TryRouteUnstuck is called
        //      for the FIRST time → it enters the escape-active branch
        //      → the lazy-init guard
        //      `if (_routeEscapeLastProgressPos == default)` fires →
        //      `_routeEscapeLastProgressUtc = now` (i.e. t+3 s, not t+0).
        //   4. noProgress now needs ANOTHER 3 s of stationary state
        //      before firing — total 6 s before escalation.
        //
        // 6 s is too long for the "leader is in combat, assist needs to
        // catch up" scenario. log-80's leader combat lasted from
        // 15:07:14 → 15:07:20 (6 s); the assist's escape fired at
        // 15:07:25 (already 5 s after combat ended), would have
        // escalated at 15:07:31 under the stacked timer, but the log
        // ends at 15:07:29 — escalation never made it into the captured
        // window, and the user observed "stuck escape did not work very
        // well at all and made no real progress."
        //
        // The "noProgress at 3 s" was the documented intent (it's the
        // "fast path" relative to the 8 s timedOut path, see
        // RouteEscapeNoMovementSec vs RouteEscapeTimeoutSecPerYard at
        // line 421-420). The lazy-init negated the fast path entirely
        // by deferring its zero point. Initializing here restores the
        // intended semantics: 3 s after escape start, if the bot
        // hasn't displaced 2 y, escalate.
        //
        // The existing lazy-init guard in TryRouteUnstuck at
        // line ~2400 is preserved as defensive code — it remains
        // correct (Vector3.WorldDistanceXYTo with default is well-
        // defined) but will never fire in practice now that this site
        // initializes the values. Worth keeping in case some future
        // path enters the escape-active branch without going through
        // this initialization (e.g., a state-restore on reconnect).
        _routeEscapeLastProgressPos = playerW;
        _routeEscapeLastProgressUtc = now;
        return true;
    }

    public void ResetRouteEscape()
    {
        _routeEscapeActive = false;
        _routeEscapeCurrentYards = RouteEscapeStartYards - 10f;
        _routeEscapeLastAttemptUtc = DateTime.MinValue;
        _routeEscapeStartUtc = DateTime.MinValue;
        _routeEscapeStartPos = default;
        _routeEscapeLastProgressPos = default;
        _routeEscapeLastProgressUtc = DateTime.MinValue;
        _routeEscapeAttemptTarget = default; // Fix BA: clear with the rest of escape state
    }

    /// <summary>
    /// Fix 32 — exposes the jump+reverse "physically trapped" sequence as a public
    /// method so other goals can use it as a last-resort unstuck after their own
    /// projection-based escape attempts fail. Originally extracted from the inline
    /// physically-trapped block in <see cref="TryRouteUnstuck"/>.
    ///
    /// Sequence: stop forward, jump, wait 400 ms, start backward, jump, wait 600 ms,
    /// jump, wait 400 ms, stop backward. Total ~1.4 seconds of blocking input. The
    /// sleeps are necessary — the game client needs at least a frame to register each
    /// input edge before the next one. Caller should be prepared for this to block
    /// the current Update tick.
    ///
    /// Used by <see cref="FollowFocusGoal"/>'s CantFollow escape sequence at the
    /// PhysicalUnstuck phase (after 10y/20y/30y projection and LastSafeAnchor
    /// fallback have all failed to produce displacement).
    /// </summary>
    public void TryPhysicalUnstuck()
    {
        input.StopForward(false);
        input.PressJump();
        System.Threading.Thread.Sleep(400);
        input.StartBackward(false);
        input.PressJump();
        System.Threading.Thread.Sleep(600);
        input.PressJump();
        System.Threading.Thread.Sleep(400);
        input.StopBackward(false);
    }

    /// <summary>
    /// Fix B (log-34b 03:43:43:444 → 03:46:23+): Independent watchdog for unreachable
    /// RouteEscape targets. Runs every Update tick.
    ///
    /// Background: <see cref="TryRouteUnstuck"/> projects an escape target 10y/20y/30y
    /// in the player's facing direction and replaces the patrol queue with that single
    /// waypoint via <see cref="SetSingleWaypoint"/> (which calls
    /// <see cref="SetWayPoints"/>, which calls <c>wayPoints.Clear()</c>). If the
    /// projected target is unreachable (e.g., projected onto a Z=24 cliff while the
    /// player stands at Z=0, exactly the log-34b case), the bot has no way to recover:
    ///   * The pather can't find a path; <see cref="PPatherService"/> returns
    ///     "Closest spot is too far from target" repeatedly, but those results are
    ///     superseded as stale before <see cref="PathCalculatedCallback"/>'s failure
    ///     path can fire <see cref="OnPathFailed"/>.
    ///   * <see cref="TryRouteUnstuck"/>'s built-in no-progress check
    ///     (<see cref="RouteEscapeNoMovementSec"/>) only runs when
    ///     <see cref="TryRouteUnstuck"/> is called, which requires
    ///     <c>stuckDetector.IsGettingCloser==false</c>. Sub-yard drift can keep
    ///     <c>IsGettingCloser</c> returning true intermittently, so the existing
    ///     check never fires.
    ///   * Goal cycles (Combat → Loot → Skin → FRG re-entry) preserve the stale
    ///     waypoint because <see cref="HasWaypoint"/> returns true and FRG.Resume's
    ///     <c>HasWaypoint || HasNext</c> branch runs the sync-pause path instead of
    ///     refilling from <c>pathSettings.Path</c>.
    ///
    /// Fix: when <see cref="_routeEscapeActive"/> AND elapsed >=
    /// <see cref="RouteEscapeUnreachableTimeoutSec"/> AND player has displaced &lt;
    /// <see cref="RouteEscapeUnreachableMinDisplacementYards"/> from the escape start
    /// position, declare the target unreachable, clear the waypoint stack, and let
    /// the empty-waypoints check at line 700 fire <see cref="OnDestinationReached"/>
    /// — which FRG.Navigation_OnDestinationReached converts into
    /// <c>RefillWaypoints(false)</c>, restoring the patrol from
    /// <c>pathSettings.Path</c>.
    ///
    /// Also feeds the unreachable target into <see cref="_routeEscapeFailedTargets"/>
    /// for Fix C — the next escape attempt will skip projections too close to this
    /// known-failed target instead of wasting an escalation cycle on the same spot.
    /// </summary>
    private void CheckRouteEscapeUnreachable()
    {
        if (!_routeEscapeActive) return;
        if (_routeEscapeStartUtc == DateTime.MinValue) return;
        if (_routeEscapeStartPos == default) return;

        double escapeSec = (DateTime.UtcNow - _routeEscapeStartUtc).TotalSeconds;
        if (escapeSec < RouteEscapeUnreachableTimeoutSec) return;

        Vector3 playerNow = Nav2D(playerReader.WorldPos);
        float displaced = playerNow.WorldDistanceXYTo(Nav2D(_routeEscapeStartPos));
        if (displaced >= RouteEscapeUnreachableMinDisplacementYards) return;

        Vector3 unreachableTarget = wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()) : default;

        logger.LogWarning(
            $"[NAV] RouteEscape unreachable: escapeSec={escapeSec:0.0}s " +
            $"displacement={displaced:0.00}y < {RouteEscapeUnreachableMinDisplacementYards}y. " +
            $"target={unreachableTarget}. Clearing waypoints to trigger refill via OnDestinationReached.");

        // Fix C: remember this target so the next escape attempt skips re-projecting
        // onto the same unreachable spot. Bounded FIFO; oldest evicted at capacity.
        if (unreachableTarget != default)
        {
            if (_routeEscapeFailedTargets.Count >= RouteEscapeFailedTargetsCapacity)
                _routeEscapeFailedTargets.RemoveAt(0);
            _routeEscapeFailedTargets.Add(unreachableTarget);
        }

        // Clear the unreachable single-waypoint stack. The very next check at
        // line 700 (wp=0 && route=0) fires OnDestinationReached, which FRG's
        // handler converts to RefillWaypoints(false). Also reset escape flags
        // (escapeRouteInProgress, escapeActive) which SetWayPoints would clear,
        // and call SyncRouteStateToTop to reset the stuckDetector target so it
        // doesn't keep pointing at the cleared waypoint.
        wayPoints.Clear();
        routeToNextWaypoint.Clear();
        escapeRouteInProgress = false;
        escapeActive = false;
        ResetRouteEscape();
        SyncRouteStateToTop();

        // Allow OnDestinationReached to fire on the immediately-following empty-
        // waypoints check even if the latch was set earlier in the session.
        ClearDestinationLatch();
    }

    public void ResetApproachEscape([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        bool hasState = _approachEscapeCurrentYards != 0
            || _approachEscapeTargetGuid != 0
            || _approachEscapeLastAttemptUtc != DateTime.MinValue
            || _stuckWorldRects.Count != 0
            || IsApproachEscapeExhausted;

        string logMsg = $"[NAV] ResetApproachEscape (caller={caller}): wiping state — " +
            $"was: active={_approachEscapeActive} yards={_approachEscapeCurrentYards:0} guid={_approachEscapeTargetGuid} " +
            $"exhausted={IsApproachEscapeExhausted} escalating={IsApproachEscapeEscalating} " +
            $"lastAttempt={_approachEscapeLastAttemptUtc:HH:mm:ss.fff} startUtc={_approachEscapeStartUtc:HH:mm:ss.fff} " +
            $"lockedCurr={_approachEscapeLockedRecordedW} lockedPrev={_approachEscapeLockedPrevRecordedW}";

        if (hasState)
            logger.LogWarning(logMsg);
        else
            logger.LogDebug(logMsg);
        _approachEscapeActive = false;
        _approachEscapeCurrentYards = ApproachEscapeStartYards - 10f;
        _approachRecordedW = default;
        _approachPrevRecordedW = default;
        _approachEscapeAnchorW = default;
        _approachEscapeLockedRecordedW = default;
        _approachEscapeLockedPrevRecordedW = default;
        _approachEscapeLastAttemptUtc = DateTime.MinValue;
        _approachEscapeStartUtc = DateTime.MinValue;
        _approachEscapeStartPos = default;
        _approachEscapeLastProgressPos = default;
        _approachEscapeLastProgressUtc = DateTime.MinValue;
        _approachEscapeTargetGuid = 0;
        IsApproachEscapePhysicallyStuck = false;
        IsApproachEscapeExhausted = false;
    }

    public void ResetApproachEscapeForTarget(int targetGuid)
    {
        if (targetGuid == 0)
            return;

        if (targetGuid == _approachEscapeTargetGuid)
        {
            bool wasActive = _approachEscapeActive;

            if (wasActive)
            {
                _approachEscapeActive = false;
                _approachRecordedW = default;
                _approachPrevRecordedW = default;
                _approachEscapeActive = true;
                logger.LogInformation($"[NAV] ApproachEscape: same target {targetGuid} re-entered — preserving {_approachEscapeCurrentYards:0}y escalation and locked direction (escape ACTIVE — timing preserved).");
            }
            else
            {
                _approachEscapeActive = false;
                _approachRecordedW = default;
                _approachPrevRecordedW = default;
                _approachEscapeStartUtc = DateTime.MinValue;
                _approachEscapeStartPos = default;
                _approachEscapeLastProgressPos = default;
                _approachEscapeLastProgressUtc = DateTime.MinValue;
                double lastAttemptAgeMs = (DateTime.UtcNow - _approachEscapeLastAttemptUtc).TotalMilliseconds;
                string lockedDirStatus = _approachEscapeLockedRecordedW != default
                    ? $"locked direction preserved (curr={_approachEscapeLockedRecordedW})"
                    : "locked direction WIPED (BUG B)";
                logger.LogInformation($"[NAV] ApproachEscape: same target {targetGuid} re-entered — " +
                    $"preserving {_approachEscapeCurrentYards:0}y escalation, {lockedDirStatus}. " +
                    $"[diag: lastAttemptAge={lastAttemptAgeMs:0}ms exhausted={IsApproachEscapeExhausted} " +
                    $"lockedCurr={_approachEscapeLockedRecordedW} lockedPrev={_approachEscapeLockedPrevRecordedW} " +
                    $"stuckRects={_stuckWorldRects.Count}]");
            }
        }
        else if (_approachEscapeTargetGuid == 0)
        {
            _approachEscapeTargetGuid = targetGuid;

            if (_approachEscapeCurrentYards == 0)
            {
                _approachEscapeLockedRecordedW = default;
                _approachEscapeLockedPrevRecordedW = default;
            }

            double lastAttemptAgeMs = (DateTime.UtcNow - _approachEscapeLastAttemptUtc).TotalMilliseconds;
            bool genuineBugB = _approachEscapeCurrentYards > 0 || _stuckWorldRects.Count > 0;

            string bugBescalation = _approachEscapeCurrentYards > 0
                ? $"{_approachEscapeCurrentYards:0}y escalation preserved"
                : "escalation WIPED (yards=0)";
            string bugBrects = _stuckWorldRects.Count > 0
                ? $"{_stuckWorldRects.Count} stuckRect(s) preserved"
                : "stuckRects WIPED";

            if (genuineBugB)
            {
                logger.LogWarning($"[NAV] ApproachEscape: BUG B — stored guid was 0, restoring to {targetGuid}. " +
                    $"{bugBescalation}, {bugBrects}. " +
                    $"[diag: lastAttemptAge={lastAttemptAgeMs:0}ms exhausted={IsApproachEscapeExhausted} " +
                    $"lockedCurr={_approachEscapeLockedRecordedW} lockedPrev={_approachEscapeLockedPrevRecordedW} " +
                    $"stuckRects={_stuckWorldRects.Count}]");
            }
            else
            {
                logger.LogDebug($"[NAV] ApproachEscape: first approach, guid restored to {targetGuid} (no prior escape state).");
            }
        }
        else
        {
            logger.LogInformation(
                $"[NAV] ApproachEscape: different target {targetGuid} (was {_approachEscapeTargetGuid}) — full reset.");
            ResetApproachEscape();
            _approachEscapeLockedRecordedW = default;
            _approachEscapeLockedPrevRecordedW = default;
            if (targetGuid != 0)
                _approachEscapeTargetGuid = targetGuid;
        }
    }

    private Vector3 ProjectApproachEscapeTarget(float yards)
    {
        Vector3 currentW = Nav2D(playerReader.WorldPos);

        float dx = 0f, dy = 0f;
        bool directionFound = false;

        const float MinLockDisplacementY = 1.5f;

        if (_approachEscapeLockedPrevRecordedW != default && _approachEscapeLockedRecordedW != default)
        {
            float ldx2 = _approachEscapeLockedRecordedW.X - _approachEscapeLockedPrevRecordedW.X;
            float ldy2 = _approachEscapeLockedRecordedW.Y - _approachEscapeLockedPrevRecordedW.Y;
            float disp = Sqrt(ldx2 * ldx2 + ldy2 * ldy2);
            if (disp >= MinLockDisplacementY)
            {
                dx = ldx2;
                dy = ldy2;
                directionFound = true;
                if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                    logger.LogDebug($"[NAV] ProjectEscapeTarget: using locked pair direction — disp={disp:0.0}y prev={_approachEscapeLockedPrevRecordedW} curr={_approachEscapeLockedRecordedW} dir=({dx:0.00},{dy:0.00})");
            }
            else
            {
                logger.LogWarning($"[NAV] ApproachEscape: locked positions only {disp:0.0}y apart — too noisy, preferring chase-target.");
                dx = ldx2;
                dy = ldy2;
            }
        }

        if (!directionFound && _chaseProgTarget != default)
        {
            dx = _chaseProgTarget.X - currentW.X;
            dy = _chaseProgTarget.Y - currentW.Y;
            directionFound = true;
            logger.LogInformation("[NAV] ApproachEscape: using chase target for direction.");
        }

        if (!directionFound && _stuckWorldRects.Count > 0)
        {
            var lastRect = _stuckWorldRects[_stuckWorldRects.Count - 1];
            float rcx = (lastRect.MinX + lastRect.MaxX) * 0.5f;
            float rcy = (lastRect.MinY + lastRect.MaxY) * 0.5f;
            dx = rcx - currentW.X;
            dy = rcy - currentW.Y;
            float rlen = Sqrt(dx * dx + dy * dy);
            if (rlen >= 0.5f)
            {
                directionFound = true;
                logger.LogInformation($"[NAV] ApproachEscape: using stuck rect center {new Vector3(rcx, rcy, 0f)} for direction.");
            }
        }

        if (!directionFound && _approachEscapeLockedRecordedW != default)
        {
            float ldx = _approachEscapeLockedRecordedW.X - currentW.X;
            float ldy = _approachEscapeLockedRecordedW.Y - currentW.Y;
            float llen = Sqrt(ldx * ldx + ldy * ldy);
            if (llen >= 0.5f)
            {
                dx = ldx;
                dy = ldy;
                directionFound = true;
                logger.LogInformation("[NAV] ApproachEscape: single locked position — projecting toward it.");
            }
        }

        if (!directionFound && _approachPrevRecordedW != default && _approachRecordedW != default)
        {
            dx = _approachRecordedW.X - _approachPrevRecordedW.X;
            dy = _approachRecordedW.Y - _approachPrevRecordedW.Y;
            directionFound = true;
            logger.LogInformation("[NAV] ApproachEscape: using current recorded positions for direction.");
        }

        if (!directionFound && (_approachEscapeLockedPrevRecordedW != default && _approachEscapeLockedRecordedW != default))
        {
            directionFound = true;
            logger.LogInformation("[NAV] ApproachEscape: noisy locked positions — using as last resort before facing.");
        }

        if (!directionFound)
        {
            float facing = playerReader.Direction;
            dx = Cos(facing);
            dy = Sin(facing);
            logger.LogInformation("[NAV] ApproachEscape: no direction data — using facing direction as last resort.");
        }

        float len = Sqrt(dx * dx + dy * dy);
        if (len < 0.01f)
        {
            if (_chaseProgTarget != default)
            {
                dx = _chaseProgTarget.X - currentW.X;
                dy = _chaseProgTarget.Y - currentW.Y;
                len = Sqrt(dx * dx + dy * dy);
            }
            if (len < 0.01f)
            {
                float facing = playerReader.Direction;
                dx = Cos(facing);
                dy = Sin(facing);
                len = 1f;
            }
        }

        return new Vector3(
            currentW.X + (dx / len) * yards,
            currentW.Y + (dy / len) * yards,
            currentW.Z);
    }

    private void LogRefill(string msg)
    {
        long now = DateTime.UtcNow.Ticks;
        if (now - lastRefillLogTick > RefillLogCooldownTicks)
        {
            lastRefillLogTick = now;
            logger.LogInformation(msg);
        }
    }

    private bool IsNearBlacklistEdge(Vector3 posW, float extra)
    {
        if (AreaBlacklist == null) return false;
        return AreaBlacklist.TryGetContainingRectInflated(posW, extra, out _);
    }

    private void RefillRouteToNextWaypoint(CancellationToken token)
    {
        long _refillStartTick = Environment.TickCount64;
        string _phase = "enter";

        try
        {
            LogRefill(
            $"[NAV-SANITY] Refill entered. " +
            $"insideBlacklist={AreaBlacklist?.ContainsWorld(Nav2D(playerReader.WorldPos)) == true} " +
            $"waypoints={wayPoints.Count} route={routeToNextWaypoint.Count}");

            LogRefill("[NAV] RefillRouteToNextWaypoint tick");

            if (routeToNextWaypoint.Count > 0)
            {
                LogRefill($"[NAV] Refill: exit (routeToNextWaypoint.Count={routeToNextWaypoint.Count})");
                RefillExit("routeAlreadyNonEmpty");
                _phase = "exit_routeAlreadyNonEmpty";
                goto REFILL_EXIT;
            }

            if (wayPoints.Count == 0)
            {
                _phase = "exit_noWaypoints";
                LogRefill("[NAV] Refill: exit (no waypoints)");
                RefillExit("noWaypoints");
                UpdateTotalRoute();
                CompleteDestinationReached();
                goto REFILL_EXIT;
            }

            if (DateTime.UtcNow < noPathCooldownUntilUtc)
            {
                LogRefill("[NAV] Refill: exit (no path rejection cooldown)");
                RefillExit("noPathCooldown");
                _phase = "exit_noPathCooldown";
                goto REFILL_EXIT;
            }

            if (DateTime.UtcNow < blacklistRejectCooldownUntilUtc)
            {
                LogRefill("[NAV] Refill: exit (blacklist rejection cooldown)");
                RefillExit("blacklistRejectCooldown");
                _phase = "exit_blacklistRejectCooldown";
                goto REFILL_EXIT;
            }

            if (waitingForPathResult || Volatile.Read(ref pathRequestPending) == 1)
            {
                _phase = "exit_requestInFlight_gate";
                RefillExit("requestInFlight_gate");
                goto REFILL_EXIT;
            }

            routeToNextWaypoint.Clear();

            Vector3 startW = Nav2D(playerReader.WorldPos);

            bool approachEscapeOwns = _approachEscapeActive ||
                (_approachEscapeCurrentYards > 0 && _approachEscapeTargetGuid != 0);

            if (approachEscapeOwns)
            {
                escapeActive = false;
                escapeRouteInProgress = false;
            }
            else if (escapeActive)
            {
                if (AreaBlacklist?.ContainsWorld(startW) != true)
                {
                    float edgeBuffer = DetourMargin + 6f;
                    bool nearEdge = AreaBlacklist != null &&
                                    AreaBlacklist.TryGetContainingRectInflated(startW, edgeBuffer, out _);

                    if (!nearEdge)
                    {
                        escapeActive = false;
                    }
                }

                if (escapeActive)
                {
                    if (routeToNextWaypoint.Count == 0)
                    {
                        escapeActive = false;
                        escapeRouteInProgress = false;
                    }
                    else
                    {
                        SyncRouteStateToTop(false);
                        RefillExit("escapeActive_hold");
                        _phase = "exit_escapeActive_hold";
                        goto REFILL_EXIT;
                    }
                }
            }

            if (!approachEscapeOwns && TryComputeEscapeOutOfBlacklist(startW, out var escapeW))
            {
                escapeActive = true;
                escapeTargetW = Nav2D(escapeW);
                escapeBestDist = float.MaxValue;
                escapeNoProgressSinceUtc = DateTime.UtcNow;

                if (lastEscapePoint.WorldDistanceXYTo(escapeW) < 0.1f &&
                    escapeInsertCooldownTicks > 0)
                {
                    RefillExit("sameEscapeRecentlyInserted");
                    _phase = "exit_sameEscapeRecentlyInserted";
                    goto REFILL_EXIT;
                }

                lastEscapePoint = escapeW;
                escapeInsertCooldownTicks = 10;

                if (startW.WorldDistanceXYTo(escapeW) < ReachedDistance(OutDoorMinDistance))
                {
                    logger.LogWarning($"[BL] Escape computed but too close; start={startW} escape={escapeW}");
                    RefillExit("escapePointAvoidThrashing");
                    _phase = "exit_escapePointAvoidThrashing";
                    goto REFILL_EXIT;
                }

                routeToNextWaypoint.Clear();
                routeToNextWaypoint.Push(Nav2D(escapeW));

                escapeRouteInProgress = true;
                escapeRouteEndW = Nav2D(escapeW);

                SyncRouteStateToTop(false);
                logger.LogInformation($"[BL] Escape-first ROUTE set: {startW} -> {escapeW}");
                RefillExit("escapeRouteSet");
                _phase = "exit_escapeRouteSet";
                goto REFILL_EXIT;
            }

            int pending = Volatile.Read(ref pathRequestPending);
            if (waitingForPathResult || pending == 1)
            {
                LogRefill($"[NAV] Refill: exit (waitingForPathResult={waitingForPathResult}, pathRequestPending={pending})");
                RefillExit("waitingForPathResultOrPendingTrue");
                _phase = "exit_waitingForPathResultOrPendingTrue";
                goto REFILL_EXIT;
            }

            if (!SkipBlacklistedWaypoints())
            {
                RefillExit("SkipBlacklistedWaypoints_false_destinationReached");
                logger.LogInformation("[NAV] Refill: SkipBlacklistedWaypoints returned false -> destination reached");
                CompleteDestinationReached();
                _phase = "exit_SkipBlacklistedWaypoints_false_destinationReached";
                goto REFILL_EXIT;
            }

            Vector3 targetRaw = wayPoints.Peek();
            Vector3 targetW = Nav2D(wayPoints.Peek());

            float distance = startW.WorldDistanceXYTo(targetW);

            logger.LogDebug(
                $"[NAV-DBG] REFILL start={startW} wpTop(raw)={targetRaw} wpTop(norm)={targetW} " +
                $"dist={distance:0.00} usePather? TBD wpCount={wayPoints.Count}");

            float wpReached = ReachedDistance(OutDoorMinDistance);
            float wpPopThreshold = MathF.Max(wpReached + 0.35f, POP_DIST - 0.05f);

            if (distance <= wpPopThreshold)
            {
                var completed = wayPoints.Peek();
                wayPoints.Pop();

                logger.LogWarning(
                    $"[NAV] REFILL: waypoint already reached -> POP {completed} " +
                    $"newWpTop={(wayPoints.Count > 0 ? wayPoints.Peek().ToString() : "<none>")} " +
                    $"d={distance:0.00} thr={wpPopThreshold:0.00}");

                routeToNextWaypoint.Clear();
                SyncRouteStateToTop();

                // Fix X (log-66 23:06:17:122): same rationale as the symmetric
                // clear in TryConsumeReachedWaypoint — clear RouteEscape state
                // when the escape target is reached. This pop site is the one
                // that actually fired in log-66 (the assist's 20y north escape
                // landed within wpPopThreshold of the target so refill popped
                // it here rather than via TryConsumeReachedWaypoint's XY
                // check). Without this clear, _routeEscapeActive stays true,
                // FFG's polling fallback at FollowFocusGoal.cs:1325 refreshes
                // the waypoint to a new target, and CheckRouteEscapeUnreachable
                // keeps measuring displacement from the now-stale _routeEscapeStartPos.
                // 12s later it falsely declares the new path "unreachable" and
                // throws away ~17y of valid SW progress toward the leader,
                // forcing a second full escape cycle.
                //
                // This path also exits via RefillExit("wpAlreadyReached_pop_noWpLeft")
                // rather than calling CompleteDestinationReached, so even a
                // hypothetical fix in StopAndResetAtDestination wouldn't help
                // here. The clear must live at the pop site itself.
                if (_routeEscapeActive)
                {
                    logger.LogInformation(
                        $"[NAV] RouteEscape: escape target reached via Refill wpAlreadyReached " +
                        $"(completed={completed}) — clearing escape state.");
                    ResetRouteEscape();
                }

                // Fix 30 — same oscillation-detection hook as
                // TryConsumeReachedWaypoint. Both pop sites need to feed
                // the history; otherwise this Refill-internal path skips
                // tracking and lets the duplicate run continue.
                TrackPopAndDedupIfOscillating(Nav2D(completed));

                OnWayPointReached?.Invoke();

                if (wayPoints.Count == 0)
                {
                    RefillExit("wpAlreadyReached_pop_noWpLeft");
                    _phase = "exit_wpAlreadyReached_pop_noWpLeft";
                    goto REFILL_EXIT;
                }
            }

            bool usePather = distance > MaxDistance || distance > AvgDistance * 2;

            BlacklistRect r = default;
            bool directBlocked = AreaBlacklist != null &&
                                 TryGetBlockingRectEscapeAware(startW, targetW, out r);

            if (directBlocked)
            {
                if (_approachEscapeActive)
                {
                    logger.LogInformation($"[NAV] Refill: approach escape active — skipping detour, forcing pather for start={startW} end={targetW}");
                    usePather = true;
                }
                else if (TryInsertDetour(startW, targetW, r))
                {
                    logger.LogInformation($"[NAV] Refill: detour inserted for blocked segment start={startW} end={targetW}");
                    RefillExit("tryForwardDetour");
                    _phase = "exit_tryForwardDetour";
                    goto REFILL_EXIT;
                }
                else
                {
                    logger.LogInformation($"[NAV] Refill: detour failed -> forcing pather start={startW} end={targetW}");
                    usePather = true;
                }
            }

            if (usePather)
            {
                _phase = "exit_enqueuePathRequest";
                ClearDestinationLatch();
                stopMoving.Stop();

                logger.LogDebug(
                    $"[NAV-DBG] ENQUEUE PATH start={startW} end(targetWp raw)={targetRaw} end(targetWp norm)={targetW} dist={distance:0.00}");

                NavDbg($"REFILL enqueuePath start={startW} end={targetW} dist={distance:0.00}");

                EnqueuePathRequest(playerReader.UIMapId.Value, startW, targetW, distance);
                RefillExit("enqueuePathRequest");
                goto REFILL_EXIT;
            }

            // Direct route: add midpoint for long segments to reduce jitter.
            Vector3 mid = default;
            bool hasMid = false;

            if (distance > 8f)
            {
                mid = new Vector3(
                    (startW.X + targetW.X) * 0.5f,
                    (startW.Y + targetW.Y) * 0.5f,
                    startW.Z
                );

                hasMid = !IsBlacklistedPoint(mid) && !SegmentBlockedEscapeAware(startW, mid);
            }

            routeToNextWaypoint.Push(Nav2D(targetW));

            if (hasMid)
                routeToNextWaypoint.Push(Nav2D(mid));

            noProgressBestDist = float.MaxValue;
            noProgressSinceUtc = DateTime.MinValue;
            noProgressTargetW = routeToNextWaypoint.Count > 0 ? routeToNextWaypoint.Peek() : default;

            ClearDestinationLatch();
            SyncRouteStateToTop(false);

            NavDbg(
                $"REFILL builtDirect routeCount={routeToNextWaypoint.Count} " +
                $"top={(routeToNextWaypoint.Count > 0 ? routeToNextWaypoint.Peek().ToString() : "<none>")} " +
                $"wpTop(raw)={(wayPoints.Count > 0 ? wayPoints.Peek().ToString() : "<none>")} " +
                $"wpTop(norm)={(wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()).ToString() : "<none>")}");

            RefillExit("builtDirectRoute");
            _phase = "end_normal";

        REFILL_EXIT:;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                $"[NAV-REFILL-EXCEPTION] phase={_phase} " +
                $"wp={wayPoints.Count} route={routeToNextWaypoint.Count} " +
                $"waiting={waitingForPathResult} pending={Volatile.Read(ref pathRequestPending)} " +
                $"pos={Nav2D(playerReader.WorldPos)} wpTop={(wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()).ToString() : "<none>")}");
            throw;
        }
        finally
        {
            RefillEnd(_phase, _refillStartTick);
        }
    }

    private void EnqueuePathRequest(int mapId, Vector3 startW, Vector3 endW, float distance)
    {
        if (!TryBeginPathRequest(startW, endW, out long requestId))
        {
            logger.LogInformation($"[NAV] EnqueuePathRequest: blocked (pending). start={startW} end={endW}");
            return;
        }

        logger.LogInformation($"[NAV] EnqueuePathRequest: QUEUED reqId={requestId} start={startW} end={endW} dist={distance}");

        var req = new PathRequest(
            mapId,
            startW,
            endW,
            distance,
            result => PathCalculatedCallback(requestId, result)
        );

        PathRequest(req);
    }

    private void PathRequest(PathRequest pathRequest)
    {
        waitingForPathResult = true;
        waitingSinceUtc = DateTime.UtcNow;
        logger.LogInformation($"[BL] PathRequest queued: {pathRequest.StartW} -> {pathRequest.EndW} dist={pathRequest.Distance}");

        // Fix AG (log-73 16:43:45:622 → 16:45:06:777+, assist stuck at
        // <-389.84277, -4137.6133> for 50+ seconds, reqQ growing 0→8 while
        // PPather returned "search failed, 10 seconds since last progress,
        // returning the closest spot <-385.2, -4146, 51.866215>" every ~10s):
        //
        // Evidence from log-73:
        //   16:43:45:623   ENQUEUE reqId=375 start=<-389.84> end=<-385.08>
        //                  dist=7.58 (the unreachable query)
        //   16:43:48:649   NAV-EMPTY reqQ=0, Path wait timeout → gate released
        //   16:43:48:650   ENQUEUE reqId=378 (gate re-acquired, activeRequestId
        //                  bumped) → reqQ=1
        //   16:43:51:700   ENQUEUE reqId=379 → reqQ=2
        //   16:43:54:715   ENQUEUE reqId=380 → reqQ=3
        //   ... repeats every ~3s ...
        //   16:44:15:243   At CantFollow entry: ENQUEUE reqId=386 (Projection10
        //                  escape target <-398.97, -4133.54>) → reqQ=8
        //   16:44:15:705 → 16:45:06:776   PPather processes reqId=377, 378, …
        //                  in FIFO order, each taking 10 s to fail with the
        //                  same closest-spot return. reqId=386 (the escape
        //                  path) sits at the queue tail and never gets to run
        //                  before the log ends.
        //
        // Root cause: TryBeginPathRequest atomically bumps activePathRequestId
        // every time a new request is begun, but nothing drains the items
        // already queued in pathRequests. Their results, when PathFinderThread
        // eventually processes them, will be stale-ignored by
        // PathCalculatedCallback (reqId < activeId). This is wasted work AND
        // it blocks the FIFO queue so a NEW request (e.g., a CantFollow
        // projection target the bot urgently needs to escape to) waits ~10 s
        // per stale entry ahead of it. With reqQ=8 the escape was ~80 s
        // behind real-time.
        //
        // Fix: drain the queue here. By the time PathRequest() runs:
        //   - TryBeginPathRequest already succeeded and incremented
        //     activePathRequestId to the new request's id.
        //   - Every item currently in pathRequests has an older reqId
        //     (Interlocked.Increment is strictly monotonic) and would be
        //     stale-ignored on completion.
        //   - The single item currently in PathFinderThread's hands (already
        //     dequeued, mid-FindWorldRoute) is uncancellable; it will return
        //     a stale result that PathCalculatedCallback ignores. No change
        //     in behaviour for that one.
        // Result: only the freshly-enqueued request below remains in the
        // queue. PathFinderThread processes it as soon as the current
        // in-flight item completes — ~10 s in the worst case instead of
        // ~10 s × queue-depth.
        //
        // This call is paired with one in Stop() (~line 1637), which also
        // bumps activePathRequestId and similarly leaves the queue stale.
        DrainStalePathRequests("supersededByNewActiveRequest");

        pathRequests.Enqueue(pathRequest);
        manualReset.Set();
    }

    /// <summary>
    /// Empties the <c>pathRequests</c> queue. Callers must ensure that any
    /// items currently in the queue have a <c>reqId &lt; activePathRequestId</c>
    /// (i.e., are already stale) so the drain is semantically a no-op apart
    /// from saving wasted PathFinderThread work. See the Fix AG comment in
    /// <see cref="PathRequest"/> for the full rationale and evidence trail.
    /// </summary>
    private void DrainStalePathRequests(string reason)
    {
        int drained = 0;
        while (pathRequests.TryDequeue(out _))
            drained++;

        if (drained > 0)
        {
            logger.LogInformation(
                $"[NAV] [FIX-FIRE] AG: drained {drained} stale queued path request(s) " +
                $"(reason={reason}). Their reqIds are all < activePathRequestId={Volatile.Read(ref activePathRequestId)}, " +
                "so results would have been stale-ignored on completion; draining " +
                "frees the PathFinderThread to process the newest request without " +
                "10 s × queue-depth latency. The single item already in PathFinderThread " +
                "(if any) is not affected — it will return and its result will be stale-ignored normally.");
        }
    }

    private bool TryBeginPathRequest(Vector3 startW, Vector3 endW, out long requestId)
    {
        requestId = 0;

        if (Interlocked.CompareExchange(ref pathRequestPending, 1, 0) != 0)
            return false;

        requestId = Interlocked.Increment(ref nextPathRequestId);

        Volatile.Write(ref activePathRequestId, requestId);
        Volatile.Write(ref waitingRequestId, requestId);

        lastRequestStart = startW;
        lastRequestEnd = endW;

        return true;
    }

    private void EndPathRequest(long requestId)
    {
        long activeId = Volatile.Read(ref activePathRequestId);

        if (activeId == requestId)
        {
            Interlocked.Exchange(ref pathRequestPending, 0);
            Volatile.Write(ref activePathRequestId, 0);
        }
    }

    private void ResetChaseProgressWatchdog(Vector3? newTarget = null)
    {
        var now = DateTime.UtcNow;

        _chaseProgTarget = newTarget ?? default;
        _chaseProgBestDist = float.MaxValue;
        _chaseProgSinceUtc = DateTime.MinValue;

        _chaseLastPos = Nav2D(playerReader.WorldPos);
        _chaseLastMovedUtc = now;

        _chaseUnstuckCooldownUntilUtc = DateTime.MinValue;
    }

    private void ResetNoProgressWatchdog(Vector3? newTarget = null)
    {
        noProgressBestDist = float.MaxValue;
        noProgressSinceUtc = DateTime.MinValue;
        noProgressTargetW = newTarget ?? default;
    }

    private bool HandleRepeatedNoPath(Vector3 startW, Vector3 endW)
    {
        bool same =
            lastNoPathStartW.WorldDistanceXYTo(startW) < 0.1f &&
            lastNoPathEndW.WorldDistanceXYTo(endW) < 0.1f;

        if (!same)
        {
            lastNoPathStartW = startW;
            lastNoPathEndW = endW;
            sameNoPathCount = 0;
        }

        sameNoPathCount++;

        logger.LogWarning(
            $"[NAV] Repeated no-path count={sameNoPathCount} start={startW} end={endW} wpCount={wayPoints.Count}");

        if (wayPoints.Count == 1)
        {
            float dist = startW.WorldDistanceXYTo(endW);

            if (sameNoPathCount == 2 && dist <= 30f)
            {
                // Option B refactor (was Fix AD): the dangerous direct-route
                // push toward an unreachable target at non-trivial distance
                // was previously bounded inline by `dist > 5y` and routed
                // through OnPathFailed. That policy now lives in
                // FollowFocusGoal.Navigation_OnRepeatedNoPathDirectRouteDecision,
                // where it can examine context (assist-specific state,
                // CantFollow readiness) before deciding. Navigation just
                // fires the event with a decision snapshot; if the subscriber
                // sets Veto=true, Navigation cleans up its no-path counter
                // and fires OnPathFailed. Non-subscribers (FRG) take the
                // unchanged path: direct-route push when dist <= 30y.
                //
                // Original log-70b evidence (12:32:40:629→12:33:12:706, 32s
                // assist stuck) and full rationale: see
                // FollowFocusGoal.Navigation_OnRepeatedNoPathDirectRouteDecision.
                var decision = new RepeatedNoPathDecisionSnapshot(startW, endW, dist, sameNoPathCount);
                OnRepeatedNoPathDirectRouteDecision?.Invoke(decision);
                if (decision.Veto)
                {
                    logger.LogWarning(
                        $"[NAV] Direct-route push vetoed by subscriber (count=2 dist={dist:0.0}y). " +
                        $"start={startW} end={endW}");
                    sameNoPathCount = 0;
                    lastNoPathStartW = default;
                    lastNoPathEndW = default;
                    noPathCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(decision.VetoCooldownMs);
                    OnPathFailed?.Invoke(startW, endW);
                    return true;
                }

                logger.LogWarning(
                    $"[NAV] No-path fallback: building direct route via shared helper " +
                    $"(count=2 branch). start={startW} end={endW} dist={dist:0.00}");

                BuildDirectRouteFallback(endW);
                return true;
            }

            if (sameNoPathCount == 3)
            {
                logger.LogWarning(
                    $"[NAV] No-path fallback: forcing unstuck start={startW} end={endW}");

                stuckDetector.SetTargetLocation(StuckOwnerId, Nav2D(endW));
                stuckDetector.Update(StuckOwnerId, token);

                noPathCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(1000);
                return true;
            }
        }

        if (sameNoPathCount >= MaxSameNoPathBeforeFallback)
        {
            logger.LogWarning(
                $"[NAV] No-path fallback: escalating to OnPathFailed start={startW} end={endW}");

            sameNoPathCount = 0;
            OnPathFailed?.Invoke(startW, endW);
            noPathCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(1000);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Fix AX (Route B Part 1): shared direct-route push, extracted from
    /// <see cref="HandleRepeatedNoPath"/>'s count==2 branch so the
    /// stale-empty callback path in <see cref="PathCalculatedCallback"/>
    /// can invoke the same recovery mechanism when the subscriber sets
    /// <see cref="StaleEmptyPathSnapshot.TriggerDirectRoute"/>.
    ///
    /// <para>
    /// Pushes <paramref name="endW"/> as the next waypoint directly,
    /// bypassing the pather entirely. The chase-progress and no-progress
    /// watchdogs are reset against <paramref name="endW"/> so that if the
    /// bot is physically wedged and cannot walk forward, the watchdogs
    /// fire <see cref="OnPathFailed"/> within their normal cascade time
    /// (~15s) and FFG's <c>Navigation_OnPathFailed</c> handles rewind/
    /// CantFollow.
    /// </para>
    ///
    /// <para>
    /// Applies a 500 ms <c>noPathCooldownUntilUtc</c> so the next path
    /// request is delayed slightly, giving the bot a moment to start
    /// moving on the direct route before another stale-empty might fire.
    /// </para>
    /// </summary>
    private void BuildDirectRouteFallback(Vector3 endW)
    {
        routeToNextWaypoint.Clear();
        routeToNextWaypoint.Push(Nav2D(endW));
        stuckDetector.SetTargetLocation(StuckOwnerId, Nav2D(endW));
        ResetChaseProgressWatchdog(Nav2D(endW));
        ResetNoProgressWatchdog(Nav2D(endW));
        UpdateTotalRoute();

        noPathCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
    }

    private void PathCalculatedCallback(long requestId, PathResult result)
    {
        long activeId = Volatile.Read(ref activePathRequestId);
        long waitingId = Volatile.Read(ref waitingRequestId);

        if (activeId != requestId)
        {
            logger.LogInformation(
                $"[NAV] Ignoring stale path result reqId={requestId} waitingReqId={waitingId} activeReqId={activeId}");

            // Option B refactor (was Fix AB-1): stale empty-path counting
            // and the OnPathFailed escalation policy used to live inline
            // here. The counter state and the escalation threshold now
            // live in FollowFocusGoal.Navigation_OnStaleEmptyPathMatchesActive.
            // Navigation continues to do the (start, end) match against
            // the in-flight request — that information is Navigation's
            // legitimate concern — and fires the event only when the
            // stale result matches. The subscriber decides whether to
            // escalate; if Escalate=true, Navigation applies the cooldown
            // and fires OnPathFailed. Non-subscribers see no change.
            //
            // 1y tolerance is unchanged: lastRequestStart updates each
            // time a new request is issued, so by the time a 10s-stale
            // result arrives the player may have drifted a fraction of a
            // yard while the requested start has shifted with the bot.
            //
            // Original log-68 evidence (11:03:43:081 → 11:04:12:949, 30s
            // assist stuck at <-517.30, -4448.72>) and full rationale: see
            // FollowFocusGoal.Navigation_OnStaleEmptyPathMatchesActive.
            if (result.Path.Length == 0)
            {
                bool matchesActiveRequest =
                    result.StartW.WorldDistanceXYTo(lastRequestStart) < 1f &&
                    result.EndW.WorldDistanceXYTo(lastRequestEnd) < 1f;

                if (matchesActiveRequest)
                {
                    var snap = new StaleEmptyPathSnapshot(
                        result.StartW, result.EndW,
                        lastRequestStart, lastRequestEnd);
                    OnStaleEmptyPathMatchesActive?.Invoke(snap);

                    // Fix AX (Route B Part 1): subscriber-requested direct-route
                    // recovery for a stale-empty matching active. Takes precedence
                    // over Escalate — direct-route is the more constructive
                    // response; if it fails (bot physically wedged), the
                    // chase/no-progress watchdogs will fire OnPathFailed within
                    // ~15s, which routes through Navigation_OnPathFailed →
                    // rewind/CantFollow. Subscriber (FFG Fix AU) bounds the
                    // number of attempts per cycle.
                    if (snap.TriggerDirectRoute)
                    {
                        float dist = lastRequestStart.WorldDistanceXYTo(lastRequestEnd);
                        logger.LogWarning(
                            $"[NAV] Stale-empty match: subscriber requested direct-route " +
                            $"recovery (Fix AX). start={lastRequestStart} end={lastRequestEnd} " +
                            $"dist={dist:0.00}y. Pushing one-hop route via " +
                            $"BuildDirectRouteFallback.");
                        BuildDirectRouteFallback(lastRequestEnd);
                    }
                    else if (snap.Escalate)
                    {
                        noPathCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(snap.EscalateCooldownMs);
                        OnPathFailed?.Invoke(result.StartW, result.EndW);
                    }
                }
            }
            return;
        }

        Volatile.Write(ref waitingRequestId, 0);

        EndPathRequest(requestId);
        waitingForPathResult = false;

        logger.LogInformation($"[NAV] PathCalculatedCallback reqId={requestId} pathLen={result.Path.Length} start={result.StartW} end={result.EndW}");

        Vector3 wpTop = wayPoints.Count > 0 ? wayPoints.Peek() : default;
        logger.LogDebug(
            $"[NAV-DBG] PATH RESULT requestId={requestId} resultStart={result.StartW} resultEnd={result.EndW} " +
            $"currentWpTop={wpTop} endMatchesWp={(wayPoints.Count > 0 ? wpTop.WorldDistanceXYTo(result.EndW) : -1):0.00}");

        if (!active)
            return;

        if (result.Path.Length == 0)
        {
            noPathCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(350);

            if (lastFailedDestination != result.EndW)
            {
                lastFailedDestination = result.EndW;
                LogPathfinderFailed(logger, result.StartW, result.EndW, result.ElapsedMs);
            }

            failedAttempt++;

            if (HandleRepeatedNoPath(Nav2D(result.StartW), Nav2D(result.EndW)))
                return;

            if (failedAttempt > 2)
            {
                failedAttempt = 0;
                stuckDetector.SetTargetLocation(StuckOwnerId, Nav2D(result.EndW));
                stuckDetector.Update(StuckOwnerId);

                OnNoPathFound?.Invoke();
            }

            return;
        }

        failedAttempt = 0;
        sameNoPathCount = 0;
        lastNoPathStartW = default;
        lastNoPathEndW = default;

        LogPathfinderSuccess(logger, result.Distance, result.StartW, result.EndW, result.ElapsedMs);

        const float MIN_FIRST_STEP_DIST = 1.0f;

        // Fix AA (log-67b 01:20:39:321, assist visibly rotates ~180° then continues
        // in the same direction it was already going): the pather (PPather /
        // RemotePathingAPIV3) returns a path whose path[0] is the navmesh node
        // NEAREST the requested start position. Navmesh nodes are grid-snapped
        // (~0.4-0.6 y spacing in this map), so when the bot's startW falls between
        // grid nodes, path[0] can lie BEHIND the bot's intended direction of travel
        // by 1-2 yards.
        //
        // The MIN_FIRST_STEP_DIST=1.0 filter above catches the "we're already at
        // path[0]" case but not this navmesh-snap case: a 1.46-yard backward node
        // passes that filter. Once accepted, this node becomes the routeTop, the
        // Update loop computes heading=DirectionCalculator.CalculateMapHeading
        // (player, routeTop), and AdjustHeading → playerDirection.SetDirection
        // rotates the bot to face the routeTop — i.e., backward. After ~1-2 s of
        // rotation and ~1.5 y of backward walking, the node pops (POP_DIST=3.6 y),
        // the next routeTop comes into view (forward), and the bot rotates ~180°
        // again to continue. The user sees this as "rotate around and continue in
        // the same direction."
        //
        // Log-67b req=242 evidence:
        //   request start=<2495.02, -2700.84>  end=<2511, -2697.30>  (dist 16.4 y east)
        //   path returned: pathLen=25 (heavily grid-snapped), routeCount-after-simplify=4
        //   routeTop=<2493.6, -2701.20>  — 1.46 y SOUTHWEST of bot
        //   forward direction (end-start) is +16.0 east, +3.5 north
        //   to-routeTop direction is -1.42 west, -0.36 south
        //   dot product = (16.0)(-1.42) + (3.5)(-0.36) = -25.96  (deeply negative)
        //   bot pressed RightArrow 993 ms + RightArrow 966 ms = ~2 s of right rotation
        //
        // 4 of 12 path requests in log-67b produced rear-pointing routeTops:
        //   req=236 (dot=-5.2), req=240 (dot=-8.6), req=242 (dot=-25.6), req=243 (dot=-1.7).
        // All 4 had visible rotation artifacts. The other 8 requests had positive dot
        // (forward or sideways path curves) and no rotation issues.
        //
        // Fix: in the per-node filter loop, additionally drop nodes that are BOTH
        //   (a) in the rear half-plane relative to the path's intended forward
        //       direction (forward · to_node <= 0), AND
        //   (b) within REAR_NODE_DROP_YARDS (3 y) of the start.
        //
        // The (b) bound prevents accidentally dropping deliberately-backward nodes
        // in pathological obstacle-bypass routes — the pather doesn't typically
        // issue such routes, but defensiveness against future cases is cheap. A
        // 3 y cap catches the navmesh-snap case (1-2 y typical) with margin.
        //
        // Why not simply raise MIN_FIRST_STEP_DIST? Raising to 2.0 y would also drop
        // legitimate sideways curves at 1.0-1.5 y from start (observed in log-67b
        // req=241 dist=1.01 y dot=+9.0, req=243 dist=1.02 y dot=-1.7 — same distance,
        // opposite directions). Only the dot-product test discriminates them.
        //
        // Edge cases:
        //   - All nodes filtered: the existing "if Count==0 && wayPoints.Count>0 →
        //     push wpTop" fallback handles this. Bot heads straight to wpTop.
        //   - Single-node path (path[0] = endpoint): forward · to_node is large
        //     positive by construction. Always kept.
        //   - Bot exactly on grid: path[0] coincident with start, dropped by the
        //     existing distance filter. No interaction with this fix.
        //   - Degenerate forward vector (endpoint == start): forwardLenSq < eps,
        //     dot test is skipped, behavior matches pre-fix. Bot wouldn't be
        //     navigating in this case anyway.
        const float REAR_NODE_DROP_YARDS = 3.0f;
        Vector3 forward = new Vector3(
            result.EndW.X - result.StartW.X,
            result.EndW.Y - result.StartW.Y,
            0f);
        float forwardLenSq = forward.X * forward.X + forward.Y * forward.Y;

        // ── Turn 3.5d / Fix BC (log-88 evidence) ──
        // FACING-AWARE GATE ON FIX AA's REAR-NODE DROP
        //
        // Fix AA above drops path nodes within 3y of start that are in the
        // rear half-plane (dot ≤ 0 vs path forward). This was correct for
        // log-67b's snap-back scenario: the bot was moving forward, the
        // pather snapped the start position to a polygon center 1-2y
        // behind, creating a spurious rear node that would have caused a
        // useless 180° U-turn.
        //
        // It is WRONG when the bot is in a terrain pocket and needs to
        // back out to reach a target on the other side. The pather
        // LEGITIMATELY returns rear-pointing first nodes — they're the
        // turnaround arc the bot must follow to clear an obstacle.
        // AA's dot test cannot distinguish "spurious snap-back artifact"
        // from "legitimate exit-the-pocket detour."
        //
        // log-88 evidence at 05:06:43:045:
        //   Bot at <-466.84, -4372.64>, just finished moving NW under
        //   AB-2 Projection10 chase. Facing roughly NW.
        //   New target after re-plan: <-462.11, -4377.96> (SE — back
        //   toward leader). Path forward = SE.
        //   Pather returned 8 nodes. First 2 nodes were NE of bot
        //   (1.04y N and 2.56y NE) — the turnaround arc to swing the
        //   bot from facing-NW to heading-SE around an obstacle.
        //   AA dropped both as "rear-pointing." Bot turned to face the
        //   remaining first node (now SE of bot, past where the dropped
        //   nodes would have routed). Bot held UpArrow and ran SE
        //   directly into the obstacle the dropped nodes were
        //   supposed to route around. Bot drifted UP the hill via
        //   subsequent AB-2 escapes and ended completely stuck at the
        //   hilltop; leader rescue required.
        //
        // AA fired on ~23% of all path computations in log-88, and
        // disproportionately on catch-up paths (where the bot needs
        // to navigate around obstacles back to the leader). This is
        // a high-frequency, silent path corruption — the symptom is
        // "bot runs into terrain" exactly as the operator reported.
        //
        // Discriminator: the bot's CURRENT FACING direction.
        //   - Facing roughly aligned with path forward (dot > 0):
        //     the bot is continuing in its current direction. The
        //     pather's rear-pointing first node is a snap-back
        //     artifact. Apply AA (original behavior — drop the
        //     spurious node).
        //   - Facing roughly opposite path forward (dot ≤ 0):
        //     the bot is about to REVERSE direction. The pather's
        //     rear-pointing first nodes are the legitimate
        //     turnaround maneuver. SKIP AA — preserve all nodes.
        //
        // Threshold of 0 (facing within 90° cone of forward triggers
        // drop) mirrors AA's own dot ≤ 0 cutoff. Edge cases at the
        // ±90° boundary fall on the "preserve nodes" side, which is
        // the safer choice — preserving an unneeded node costs at
        // most one extra waypoint pop; dropping a needed node costs
        // navigation failure.
        //
        // Log-67b's original snap-back: bot was walking forward (say
        // toward route waypoint), facing aligned with path forward,
        // pather added snap-back behind. Dot > 0 — AA applies, fix
        // preserved.
        //
        // The check is per-path (computed once, applied to all nodes
        // of this path), so no per-node overhead.
        bool applyRearDrop = true;
        if (forwardLenSq > 0.0001f)
        {
            float facing = playerReader.Direction;
            float facingX = MathF.Cos(facing);
            float facingY = MathF.Sin(facing);
            float invForwardLen = 1f / MathF.Sqrt(forwardLenSq);
            float facingVsForward = (facingX * forward.X + facingY * forward.Y) * invForwardLen;
            if (facingVsForward <= 0f)
            {
                applyRearDrop = false;
                logger.LogInformation(
                    $"[NAV] [FIX-FIRE] BC: bot facing ({facing:0.00}rad) is in the " +
                    $"rear half-plane of path forward direction " +
                    $"(facing·forward = {facingVsForward:0.00}). " +
                    $"Bot is about to reverse direction; rear-pointing path nodes " +
                    $"are the legitimate turnaround arc, NOT snap-back artifacts. " +
                    $"Skipping Fix AA's rear-node drop for this path — all nodes " +
                    $"preserved. Without this gate, AA would drop the turnaround " +
                    $"nodes and the bot would walk straight forward into the " +
                    $"obstacle the pather was routing around (log-88 hill incident).");
            }
        }

        routeToNextWaypoint.Clear();

        Vector3 start2D = Nav2D(result.StartW);

        for (int i = result.Path.Length - 1; i >= 0; i--)
        {
            Vector3 p = Nav2D(result.Path[i]);

            if (p.WorldDistanceXYTo(start2D) < MIN_FIRST_STEP_DIST)
                continue;

            // Fix AA: rear-half-plane filter (gated by Fix BC).
            if (applyRearDrop && forwardLenSq > 0.0001f)
            {
                float toNodeX = p.X - start2D.X;
                float toNodeY = p.Y - start2D.Y;
                float toNodeLenSq = toNodeX * toNodeX + toNodeY * toNodeY;
                if (toNodeLenSq < REAR_NODE_DROP_YARDS * REAR_NODE_DROP_YARDS)
                {
                    float dot = forward.X * toNodeX + forward.Y * toNodeY;
                    if (dot <= 0f)
                    {
                        logger.LogInformation(
                            $"[NAV] [FIX-FIRE] AA: dropping rear-pointing path node {p} " +
                            $"(dist={MathF.Sqrt(toNodeLenSq):0.00}y from start, " +
                            $"dot={dot:0.00} <= 0 against path forward direction). " +
                            $"Would have caused a U-turn rotation toward a navmesh-snap node behind the bot.");
                        continue;
                    }
                }
            }

            routeToNextWaypoint.Push(Nav2D(p));
        }

        if (routeToNextWaypoint.Count == 0 && wayPoints.Count > 0)
            routeToNextWaypoint.Push(Nav2D(wayPoints.Peek()));

        if (SimplifyRouteToWaypoint)
            SimplyfyRouteToWaypoint();

        if (routeToNextWaypoint.Count == 0 && wayPoints.Count > 0)
            routeToNextWaypoint.Push(Nav2D(wayPoints.Peek()));

        // Option B refactor (was Fix AC and Fix AE): post-simplification
        // routeTop rear-angle inspection used to live inline here. The
        // angle test, the 135° threshold, the 2y minimum, and the
        // OnPathFailed escalation now live in
        // FollowFocusGoal.Navigation_OnPathResultInspected. Navigation
        // builds a PathInspectionSnapshot, fires the event, and acts on
        // the Reject flag if set: clears the route, applies cooldown,
        // fires OnPathFailed. Non-subscribers (FRG) see no change.
        //
        // Original log-69 + log-71 evidence and full geometry rationale
        // (including the angle-threshold math and the log-71 false-
        // positive that drove the Fix AE tightening) live in the
        // FollowFocusGoal handler.
        bool hasSimplifiedRouteTop = routeToNextWaypoint.Count > 0;
        Vector3 simplifiedRouteTop = hasSimplifiedRouteTop
            ? Nav2D(routeToNextWaypoint.Peek())
            : default;
        var inspection = new PathInspectionSnapshot(
            requestId,
            result.StartW,
            result.EndW,
            result.Path.Length,
            hasSimplifiedRouteTop,
            simplifiedRouteTop,
            routeToNextWaypoint.Count,
            forward.X,
            forward.Y,
            forwardLenSq);
        OnPathResultInspected?.Invoke(inspection);
        if (inspection.Reject)
        {
            routeToNextWaypoint.Clear();
            SyncRouteStateToTop();
            noPathCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(inspection.RejectCooldownMs);
            OnPathFailed?.Invoke(result.StartW, result.EndW);
            return;
        }

        logger.LogDebug(
            $"[NAV-DBG] ROUTESET reqId={requestId} routeCount={routeToNextWaypoint.Count} " +
            $"routeTop={(routeToNextWaypoint.Count > 0 ? Nav2D(routeToNextWaypoint.Peek()).ToString() : "<none>")} " +
            $"wpTop={(wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()).ToString() : "<none>")} " +
            $"player={playerReader.WorldPos}"
            );

        if (escapeRouteInProgress)
            escapeNoProgressSinceUtc = DateTime.UtcNow;

        if (AreaBlacklist != null)
        {
            bool startedInside = AreaBlacklist.ContainsWorld(Nav2D(result.StartW));

            if (!startedInside)
            {
                // Fix 21 (log-46 02:46:00→02:46:16, 3 ping-pong cycles between
                // <952.6, 298.1> inside rect and <937.7, 298.1> outside):
                // removed the `nearEdge` gate (TryGetContainingRectInflated with
                // DetourMargin+6=18y) that previously skipped this entire
                // post-pather path-check block whenever the bot was within 18 y
                // of any inflated rect. The pather is navmesh-aware but
                // blacklist-unaware, so its route from <948.97, 297.98> to the
                // SW-corner detour <934.39, 259.05> contained intermediate
                // segments that crossed the strict rect interior. With the gate
                // skipping these checks, the bot followed the route, drifted
                // east into the rect, escape-first fired, exited, re-pathed,
                // re-entered — 3 full cycles in 16 s. Removing the gate is
                // safe because both checks use STRICT rect containment:
                // ContainsWorld is boundary-exclusive (a node exactly on the
                // rect edge returns false) and TryGetBlockingRect uses
                // Liang-Barsky on the strict rect (a segment grazing the edge
                // does not intersect the interior). Genuine reject loops are
                // bounded by Fix 20's `minStartSeparation` candidate filter and
                // HandleBlacklistReject's `sameRejectCount` watchdog (skip wpTop
                // after 4 consecutive identical rejects).
                var steps = routeToNextWaypoint.ToArray();

                Vector3 prev = Nav2D(result.StartW);

                for (int i = 0; i < steps.Length; i++)
                {
                    Vector3 cur = Nav2D(steps[i]);

                    if (AreaBlacklist.ContainsWorld(cur))
                    {
                        logger.LogWarning(
                            $"[BL] Path node inside blacklist; rejecting. " +
                            $"badPoint={cur}");

                        if (TryInsertDetourFromRejectedPath(result.StartW, result.EndW, cur))
                        {
                            sameRejectCount = 0;
                            lastRejectStartW = default;
                            lastRejectEndW = default;

                            blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
                            ResetNoProgressWatchdog();
                            return;
                        }

                        if (HandleBlacklistReject(result))
                        {
                            ResetNoProgressWatchdog();
                            return;
                        }

                        blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(500);

                        routeToNextWaypoint.Clear();
                        SyncRouteStateToTop();
                        return;
                    }

                    if (SegmentBlockedEscapeAware(prev, cur))
                    {
                        logger.LogWarning(
                            $"[BL] Path segment crosses blacklist; rejecting. " +
                            $"seg=({prev} -> {cur}) i={i} " +
                            $"routeTop={(routeToNextWaypoint.Count > 0 ? Nav2D(routeToNextWaypoint.Peek()).ToString() : "<none>")} " +
                            $"routeEnd={(steps.Length > 0 ? Nav2D(steps[^1]).ToString() : "<none>")}");

                        Vector3 hint = new Vector3((prev.X + cur.X) * 0.5f, (prev.Y + cur.Y) * 0.5f, 0f);

                        if (TryInsertDetourFromRejectedPath(result.StartW, result.EndW, hint))
                        {
                            sameRejectCount = 0;
                            lastRejectStartW = default;
                            lastRejectEndW = default;

                            blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
                            ResetNoProgressWatchdog();
                            return;
                        }

                        if (HandleBlacklistReject(result))
                        {
                            ResetNoProgressWatchdog();
                            return;
                        }

                        blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(500);

                        routeToNextWaypoint.Clear();
                        SyncRouteStateToTop();
                        return;
                    }

                    prev = cur;
                }
            }
        }

        if (routeToNextWaypoint.Count == 0)
        {
            SyncRouteStateToTop();
            noPathCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(350);
            OnPathFailed?.Invoke(result.StartW, result.EndW);
            return;
        }

        ClearDestinationLatch();
        SyncRouteStateToTop(false);

        OnPathCalculated?.Invoke();
    }

    private bool TryInsertDetourFromRejectedPath(Vector3 startW, Vector3 endW, Vector3 badPointW)
    {
        if (AreaBlacklist == null)
            return false;

        if (wayPoints.Count == 0 || wayPoints.Peek().WorldDistanceXYTo(endW) > 0.1f)
            return false;

        if (!AreaBlacklist.TryGetContainingRect(Nav2D(badPointW), out var rect))
            return false;

        // Fix 29 — same-(startW, badPointW) cycle detection. See the long
        // explanation at the field declarations near line 317. When this
        // function is called repeatedly with the same start position and
        // the same bad point but different endW values (because each
        // iteration's detour gets pushed and then becomes the next call's
        // endW), the pathfinder is signaling that NO detour around the
        // current rect helps from this start position. The previous code
        // would happily insert a fresh geometrically-valid detour every
        // call, succeed (returning true), reset sameRejectCount on the
        // caller, and never trigger any skip — observed as 58 rejections
        // in 17 s and wp count growing 12 → 60+ in log-51.
        bool sameAttempt =
            startW.WorldDistanceXYTo(_lastDetourAttemptStartW) < 0.1f &&
            badPointW.WorldDistanceXYTo(_lastDetourAttemptBadPointW) < 0.1f;

        if (sameAttempt)
        {
            _sameDetourBadPointCount++;

            if (_sameDetourBadPointCount >= MaxSameDetourBadPointBeforeSkip)
            {
                logger.LogError(
                    $"[BL] Fix 29: detour cycle detected — same startW={startW} + " +
                    $"badPointW={badPointW} hit {_sameDetourBadPointCount}× consecutively. " +
                    $"Popping wpTop {endW} to break deadlock. " +
                    $"(rect=({rect.MinX:0.0},{rect.MinY:0.0})-({rect.MaxX:0.0},{rect.MaxY:0.0}) " +
                    $"wpCount-before={wayPoints.Count})");

                // Pop the rejected target only if it's still the current
                // top — another path can have changed the queue between
                // calls, and we must not pop the wrong waypoint.
                if (wayPoints.Count > 0 && wayPoints.Peek().WorldDistanceXYTo(endW) < 0.1f)
                {
                    wayPoints.Pop();
                }

                routeToNextWaypoint.Clear();
                SyncRouteStateToTop();

                // 250 ms cooldown — short enough to let the next Refill
                // tick try a new wpTop quickly, long enough to keep this
                // from being a tight loop. The caller's own cooldown set
                // on return value true is also 250 ms (line ~2796), so
                // they don't fight each other.
                blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(250);

                // Fix H — escalation: TryPhysicalUnstuck + direct
                // away-from-rect route when the cycle persists past the
                // initial detection by Fix29UnstuckEscalationHits more
                // hits. See the long explanation at the field
                // declarations near line 357.
                if (_sameDetourBadPointCount - _lastFix29UnstuckHitCount >= Fix29UnstuckEscalationHits &&
                    DateTime.UtcNow >= _fix29UnstuckCooldownUntilUtc)
                {
                    logger.LogWarning(
                        $"[BL] Fix H escalation: Fix 29 cycle persists at " +
                        $"{_sameDetourBadPointCount}× (last escalation at " +
                        $"{_lastFix29UnstuckHitCount}×) — bot wedged at {startW}, " +
                        $"local mesh routes through {badPointW} inside rect. " +
                        $"Calling TryPhysicalUnstuck + pushing direct away-from-rect route.");

                    // Step 1: physical unstuck (jump + reverse + jump + reverse).
                    // Blocks ~1.4 s. After it returns, playerReader.WorldPos
                    // reflects the new position — the bot has moved 1-2 y
                    // backward from its facing direction.
                    TryPhysicalUnstuck();

                    // Step 2: compute a target AWAY from the rect's center and
                    // push directly into routeToNextWaypoint, bypassing the
                    // pather. The pather has been routing through the rect
                    // interior the entire cycle — we already know the local
                    // mesh is unreliable from this position. Direct push
                    // gives the bot a fixed movement target while it's
                    // physically clearing the inflated zone.
                    Vector3 postPos = Nav2D(playerReader.WorldPos);
                    Vector3 rectCenter = new Vector3(
                        (rect.MinX + rect.MaxX) * 0.5f,
                        (rect.MinY + rect.MaxY) * 0.5f,
                        0f);

                    float dx = postPos.X - rectCenter.X;
                    float dy = postPos.Y - rectCenter.Y;
                    float lenXY = MathF.Sqrt(dx * dx + dy * dy);

                    if (lenXY > 0.001f)
                    {
                        float invLen = 1f / lenXY;
                        // 18 y away from rect center — same edgeBuffer used
                        // elsewhere (DetourMargin=12 + 6). Guarantees the
                        // target is well clear of the inflated zone.
                        float escapeDist = DetourMargin + 6f;
                        Vector3 escapeTarget = new Vector3(
                            postPos.X + dx * invLen * escapeDist,
                            postPos.Y + dy * invLen * escapeDist,
                            0f);

                        logger.LogWarning(
                            $"[BL] Fix H: direct away-from-rect route " +
                            $"{postPos} -> {escapeTarget} " +
                            $"({escapeDist:0.0}y from rect center {rectCenter}). " +
                            $"Bypassing pather — mesh has been routing through rect interior.");

                        routeToNextWaypoint.Push(Nav2D(escapeTarget));
                        SyncRouteStateToTop(false);
                    }
                    else
                    {
                        // Degenerate: post-unstuck position is at rect
                        // center (shouldn't happen in practice). Skip
                        // the direct route — TryPhysicalUnstuck alone
                        // should have displaced the bot enough that the
                        // next cycle iteration sees different geometry.
                        logger.LogWarning(
                            $"[BL] Fix H: postPos {postPos} is at rect center " +
                            $"(len={lenXY:0.000}); skipping direct route, " +
                            $"relying on TryPhysicalUnstuck alone.");
                    }

                    _lastFix29UnstuckHitCount = _sameDetourBadPointCount;
                    _fix29UnstuckCooldownUntilUtc = DateTime.UtcNow.AddSeconds(Fix29UnstuckCooldownSec);
                }

                // Intentionally do NOT reset _sameDetourBadPointCount.
                // While the bot remains at the same startW and the
                // pathfinder still routes through the same badPointW,
                // subsequent calls will keep matching sameAttempt and
                // pop one waypoint per call. As soon as either changes
                // (bot moved, or a different rect is encountered), the
                // else branch below resets to 0.
                return true;
            }
        }
        else
        {
            _sameDetourBadPointCount = 0;
            _lastDetourAttemptStartW = startW;
            _lastDetourAttemptBadPointW = badPointW;

            // Fix H: reset the escalation tracker so a fresh cycle starts
            // at full budget (next escalation fires at hit count
            // Fix29UnstuckEscalationHits = 8 from 0). The cooldown
            // (_fix29UnstuckCooldownUntilUtc) is time-based and intentionally
            // NOT reset — even if a cycle resolves and a new one starts
            // 1 s later, we don't want to physical-unstuck again that fast.
            _lastFix29UnstuckHitCount = 0;
        }

        float m = DetourMargin;
        float z = 0f;

        var inflated = rect.Inflate(m * 0.5f);

        Vector3 best = default;
        float bestScore = float.MaxValue;
        bool found = false;

        // Fix 20 (log-45 01:32:14:164 → 01:33:21:521, ~67s + still going at
        // log end): leader stuck at <934.3887, 256.05176> with wpTop
        // <945.87354, 298.23877>. Path request to the wp returned a route
        // whose 10-step path contained a node <952.80, 283.2> inside the
        // blacklist; rejected. TryInsertDetourFromRejectedPath then chose
        // candidate <934.39343, 259.05078> — geometrically valid (clear of
        // the rect, segments don't graze the inflated rect), but only 3.0 y
        // from startW. The waypoint-pop threshold is
        // ReachedDistance(OutDoorMinDistance) + 0.35 ≈ 3.35 y outdoors,
        // unmounted. So on the very same tick, before any forward
        // movement, navigation's "XY reached" check popped the freshly-
        // inserted detour, reverting wpTop to <945.87, 298.24>. Next tick:
        // same path, same rejection, same candidate, same no-op
        // insertion → immediate auto-pop. 214 detour insertions logged in
        // 67 s while the bot's position never changed.
        //
        // HandleBlacklistReject's sameRejectCount safety (would skip the
        // wpTop after MaxSameRejectBeforeFallback=4 rejects) is defeated
        // because each insertion returns true ("succeeded"), which resets
        // sameRejectCount to 0 (line 2814). The counter never reaches 4.
        // Zero "Rejected same path" log lines in the entire 67 s stall.
        //
        // Fix: require candidates to be further from startW than the
        // waypoint-pop threshold plus a safety margin. The threshold is
        // computed dynamically via the same ReachedDistance call the pop
        // logic uses (line 475), so the fix adapts correctly to mounted
        // and indoor states where the pop threshold differs. Endpoint
        // separation against endW (MinDetourSeparationYards, 1.0 y) is
        // unchanged — its purpose (Fix 7: prevent degenerate candidates
        // exactly at the target winning the score) is different from this
        // new pop-avoidance check.
        //
        // If no candidate is far enough from startW, this function returns
        // false → caller falls back to HandleBlacklistReject → sameRejectCount
        // increments correctly → wpTop skipped after 4 rejects (~1 s), and
        // the leader patrol advances to the next waypoint. Strictly better
        // than the infinite-loop status quo, which never made any progress.
        float wpReachThreshold = ReachedDistance(OutDoorMinDistance) + 0.35f;
        const float DetourFromStartSafetyMarginYards = 1.0f;
        float minStartSeparation = wpReachThreshold + DetourFromStartSafetyMarginYards;

        foreach (var d in BuildDetourCandidates(inflated, m, z))
        {
            if (IsBlacklistedPoint(d)) continue;

            // Fix 7 (log-37 assist 23:46:28→59): BuildDetourCandidates emits
            // points on the rect's perimeter+margin. When endW or startW itself
            // sits on that perimeter (which happens when the assist is
            // navigating to the leader's previously-inserted detour point —
            // FRG.PublishPatrolWaypoint via TopPublishableWaypointW returns
            // that detour as the publishable wpTop), one candidate coincides
            // exactly with endW (or startW). The score
            // startW.dist(d) + d.dist(endW) collapses to startW.dist(endW) for
            // that candidate, which is the smallest possible score; it always
            // wins. The "detour" pushed onto the stack is the existing target,
            // so the next refill recomputes the same path, gets the same
            // rejection, inserts another duplicate — wp count grows unbounded
            // (1→153+ in 30 s observed in log-37). Reject candidates that
            // don't meaningfully separate from either endpoint; if none
            // remain, HandleBlacklistReject's MaxSameRejectBeforeFallback
            // counter takes over and pops the wpTop after 4 rejects.
            if (d.WorldDistanceXYTo(endW) < MinDetourSeparationYards) continue;
            if (d.WorldDistanceXYTo(startW) < minStartSeparation) continue;

            if (SegmentBlockedEscapeAware(startW, d)) continue;
            if (SegmentBlockedEscapeAware(d, endW)) continue;

            // Fix 11 (parity with TryInsertDetour): require segments to clear
            // the INFLATED blocking rect (the same inflation used for candidate
            // placement) so the connecting paths can't graze the rect's
            // corners. Without this, the pather can route through the corner
            // because the corner clearance is sub-yard and unit drift then
            // carries the bot into the rect. See the longer comment block in
            // TryInsertDetour for the log-41 evidence trail.
            if (SegmentGrazesInflatedRect(startW, d, inflated)) continue;
            if (SegmentGrazesInflatedRect(d, endW, inflated)) continue;

            float score = startW.WorldDistanceXYTo(d) + d.WorldDistanceXYTo(endW);
            if (score < bestScore)
            {
                bestScore = score;
                best = d;
                found = true;
            }
        }

        if (!found)
            return false;

        Vector3 target = Nav2D(wayPoints.Pop());
        wayPoints.Push(Nav2D(target));
        wayPoints.Push(Nav2D(best));

        routeToNextWaypoint.Clear();
        SyncRouteStateToTop();

        logger.LogWarning($"[BL] Inserted detour after rejection: detour={best} target={endW} badPoint={badPointW}");

        // Fix S (log-63 19:46:56:993): notify subscribers (FRG in PartyLeader
        // mode) that wpTop just changed without a pop, so it can rebroadcast
        // TopPublishableWaypointW to the assist. Without this, the assist
        // continues navigating to the original (now-buried) target through
        // the blacklist and stays stuck against the rect boundary.
        OnTopWaypointChanged?.Invoke();
        return true;
    }

    private bool HandleBlacklistReject(PathResult result)
    {
        bool same = lastRejectStartW.WorldDistanceXYTo(result.StartW) < 0.1f &&
                    lastRejectEndW.WorldDistanceXYTo(result.EndW) < 0.1f;

        if (!same)
        {
            lastRejectStartW = result.StartW;
            lastRejectEndW = result.EndW;
            sameRejectCount = 0;
        }

        sameRejectCount++;

        if (sameRejectCount < MaxSameRejectBeforeFallback)
        {
            blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
            return false;
        }

        logger.LogError($"[BL] Rejected same path {sameRejectCount} times. Skipping waypoint {result.EndW} to avoid deadlock.");

        sameRejectCount = 0;

        if (wayPoints.Count > 0 && wayPoints.Peek().WorldDistanceXYTo(result.EndW) < 0.1f)
            wayPoints.Pop();

        routeToNextWaypoint.Clear();
        SyncRouteStateToTop();

        blacklistRejectCooldownUntilUtc = DateTime.UtcNow.AddSeconds(1);

        return true;
    }

    private void PathFinderThread()
    {
        while (!token.IsCancellationRequested)
        {
            manualReset.Reset();

            while (pathRequests.TryDequeue(out var pathRequest))
            {
                try
                {
                    logger.LogInformation($"[BL] PathFinderThread processing: {pathRequest.StartW} -> {pathRequest.EndW}");

                    Vector3[] path = pather.FindWorldRoute(pathRequest.MapId, pathRequest.StartW, pathRequest.EndW);

                    pathResults.Enqueue(new PathResult(pathRequest, path, pathRequest.Callback));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[NAV] PathFinderThread exception; returning empty path");
                    pathResults.Enqueue(new PathResult(pathRequest, Array.Empty<Vector3>(), pathRequest.Callback));
                }
            }

            manualReset.Wait();
        }
    }

    private float ReachedDistance(float minDistance)
    {
        return mountHandler.IsMounted()
            ? MinDistanceMount
            : bits.Indoors()
                ? IndoorMinDistance
                : minDistance;
    }

    private void ReduceByDistance(Vector3 playerW, float minDistance)
    {
        bool poppedAny = false;
        Vector3 lastPopped = default;

        while (routeToNextWaypoint.Count > 0 &&
               playerW.WorldDistanceXYTo(routeToNextWaypoint.Peek()) < ReachedDistance(minDistance))
        {
            lastPopped = routeToNextWaypoint.Pop();
            poppedAny = true;
        }

        if (!poppedAny)
            return;

        if (NavDebugDue())
        {
            Vector3 newTop = routeToNextWaypoint.Count > 0 ? routeToNextWaypoint.Peek() : default;
            Vector3 wpTop = wayPoints.Count > 0 ? wayPoints.Peek() : default;
            logger.LogDebug($"[NAV-DBG] ReduceByDistance poppedLast={lastPopped} newRouteTop={newTop} wpTop={wpTop}");
        }

        SetLastSafeAnchor(lastPopped);

        noProgressBestDist = float.MaxValue;
        noProgressSinceUtc = DateTime.MinValue;
        noProgressTargetW = routeToNextWaypoint.Count > 0 ? routeToNextWaypoint.Peek() : default;

        if (routeToNextWaypoint.Count == 0)
            noProgressRefillCooldownUntilUtc = DateTime.MinValue;
    }

    private void AdjustHeading(float heading, CancellationToken token)
    {
        float diff1 = Abs(Tau + heading - playerReader.Direction) % Tau;
        float diff2 = Abs(heading - playerReader.Direction - Tau) % Tau;

        float diff = Min(diff1, diff2);
        if (diff > minAngleToTurn)
        {
            if (diff > minAngleToStopBeforeTurn)
            {
                stopMoving.Stop();
            }

            playerDirection.SetDirection(heading, Nav2D(routeToNextWaypoint.Peek()), OutDoorMinDistance, token);
        }
    }

    private bool AdjustNextWaypointPointToClosest()
    {
        if (wayPoints.Count < 2) { return false; }

        Vector3 A = Nav2D(wayPoints.Pop());
        Vector3 B = wayPoints.Peek();
        Vector2 result = VectorExt.GetClosestPointOnLineSegment(A.AsVector2(), B.AsVector2(), playerReader.WorldPos.AsVector2());
        Vector3 newPoint = new(result.X, result.Y, 0f);

        if (newPoint.WorldDistanceXYTo(wayPoints.Peek()) > OutDoorMinDistance)
        {
            wayPoints.Push(Nav2D(newPoint));
            if (debug)
                LogDebug("Adjusted resume point");

            return false;
        }

        if (debug)
            LogDebug("Skipped next point in path");

        return true;
    }

    private void V1_AttemptToKeepRouteToWaypoint()
    {
        float totalDistance = VectorExt.TotalDistance<Vector3>(TotalRoute, VectorExt.WorldDistanceXY);
        if (totalDistance > MaxDistance / 2)
        {
            Vector3 playerW = Nav2D(playerReader.WorldPos);
            float distanceToRoute = playerW.WorldDistanceXYTo(routeToNextWaypoint.Peek());
            float distanceToPrevLoc = playerW.WorldDistanceXYTo(playerWorldPos);

            if (!RouteTargetsWaypointTop())
            {
                logger.LogWarning("[NAV] KeepRoute vetoed: route does not target current waypoint. Clearing.");
                routeToNextWaypoint.Clear();
                return;
            }

            if (distanceToRoute > 2 * MinDistanceMount &&
                distanceToPrevLoc > 2 * MinDistanceMount)
            {
                LogV1ClearRouteToWaypoint(logger, patherName, distanceToRoute);
                routeToNextWaypoint.Clear();
            }
            else
            {
                LogV1KeepRouteToWaypoint(logger, patherName, distanceToRoute);
                ResetStuckParameters();
            }
        }
        else
        {
            LogV1ClearRouteToWaypointTooFar(logger, patherName, totalDistance, MaxDistance / 2);
            routeToNextWaypoint.Clear();
        }
    }

    private void SimplyfyRouteToWaypoint()
    {
        const bool HighQuality = false;
        Span<Vector3> reduced = PathSimplify.Simplify(routeToNextWaypoint.ToArray(), OutDoorMinDistance / 2, HighQuality);
        if (debug)
            LogDebug($"{nameof(SimplyfyRouteToWaypoint)} {routeToNextWaypoint.Count} -> {reduced.Length} | HQ: {HighQuality}");

        routeToNextWaypoint.Clear();
        for (int i = reduced.Length - 1; i >= 0; i--)
        {
            routeToNextWaypoint.Push(Nav2D(reduced[i]));
        }
    }

    private void UpdateTotalRoute()
    {
        TotalRoute = new Vector3[routeToNextWaypoint.Count + wayPoints.Count];
        routeToNextWaypoint.CopyTo(TotalRoute, 0);
        wayPoints.CopyTo(TotalRoute, routeToNextWaypoint.Count);
    }

    public bool IsInBlacklistArea()
    {
        bool inside = AreaBlacklist?.ContainsWorld(Nav2D(playerReader.WorldPos)) == true;
        return inside;
    }

    /// <summary>
    /// Clears all dynamically-added stuck rects accumulated during approach attempts.
    /// Restores the effective blacklist to the static route blacklist only.
    /// </summary>
    public void ClearStuckRects()
    {
        if (_stuckWorldRects.Count == 0)
            return;

        int count = _stuckWorldRects.Count;
        _stuckWorldRects.Clear();
        // Fix AV: clear the parallel expiry list in lockstep with the rect
        // list. Both locally-discovered (MaxValue) and propagated (finite)
        // entries are cleared — explicit ClearStuckRects calls (from ATG/
        // CombatGoal plan transitions) deliberately reset all dynamic
        // state, including any propagated rects whose TTL hadn't yet
        // elapsed. The next propagation cycle will re-apply them if the
        // peer is still publishing.
        _stuckRectExpiryUtc.Clear();
        _areaBlacklist = _staticAreaBlacklist;
        logger.LogInformation($"[NAV] ClearStuckRects: {count} dynamic stuck rect(s) cleared — restoring static blacklist only.");

        // Fix AV: notify subscribers (intended: leader-side propagation
        // service) so the published snapshot drains on the next poll. The
        // assist's already-propagated rects then expire naturally via TTL.
        // Fires only on actual clear (post early-return when list was
        // empty) — symmetric with OnStuckRectAdded which fires only on
        // successful add.
        OnStuckRectsCleared?.Invoke();
    }

    /// <summary>
    /// Adds a stuck rect that originated on a peer bot (typically the
    /// leader's Navigation) and was propagated to this Navigation instance
    /// over IPC. Designed to be called from a subscriber that polls the
    /// leader's broadcast (see <c>GoapAgent.GoapThread</c>'s AssistFocus
    /// branch) — see Fix AV commentary at the
    /// <see cref="_stuckRectExpiryUtc"/> field and
    /// <see cref="OnStuckRectAdded"/> event.
    ///
    /// <para>
    /// Unlike <see cref="AddStuckRect(Vector3, Vector3)"/>, propagated
    /// rects have a finite lifetime (<see cref="PropagatedStuckRectTtlSec"/>)
    /// and are pruned by <see cref="PruneExpiredStuckRects"/> on each
    /// Update tick. They do NOT fire <see cref="OnStuckRectAdded"/> —
    /// otherwise a propagation loop would form (assist would publish a
    /// rect it just received).
    /// </para>
    ///
    /// <para>
    /// <b>Fix AW (Guards 1+2):</b> Dedup/append/refresh logic lives in
    /// <see cref="TryAddStuckRectInternal"/>, shared with
    /// <see cref="AddStuckRect"/>. Three skip conditions exist on this
    /// path:
    /// <list type="bullet">
    /// <item><description><b>Guard 1</b>: bot currently inside the proposed rect — refuse
    ///   to add (avoids start-in-blacklist failures on the next path
    ///   request). Retry-friendly: leader keeps republishing each poll.</description></item>
    /// <item><description><b>Dedup vs. propagated</b>: refresh existing TTL.</description></item>
    /// <item><description><b>Dedup vs. local</b>: silent no-op — local rect owns the
    ///   terrain and outlives propagation.</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Returns <c>true</c> only if a new rect was appended. Both Guard 1
    /// rejection and dedup-skip return <c>false</c> — the caller can
    /// treat any <c>false</c> as "no new state change needed."
    /// </para>
    /// </summary>
    public bool AddPropagatedStuckRect(Vector3 center, float halfSize = StuckRectHalfSizeY)
    {
        DateTime newExpiry = DateTime.UtcNow.AddSeconds(PropagatedStuckRectTtlSec);

        // Fix AW: dedup, Guard 1 (skip-if-inside), and TTL refresh on
        // existing propagated rect all live in the shared helper. Returns
        // true only if a new rect was actually appended — Guard 1 rejection
        // and dedup-skip both return false.
        bool added = TryAddStuckRectInternal(center, halfSize, newExpiry);

        if (added)
        {
            logger.LogInformation(
                $"[NAV] Propagated stuck rect added: center={center} ±{halfSize}y, " +
                $"expires in {PropagatedStuckRectTtlSec:0}s at {newExpiry:HH:mm:ss.fff} " +
                $"(total={_stuckWorldRects.Count})");
        }

        return added;
    }

    /// <summary>
    /// Removes propagated stuck rects whose TTL has elapsed. Locally-
    /// discovered rects (expiry == DateTime.MaxValue) are never pruned by
    /// this method — they are cleared explicitly via
    /// <see cref="ClearStuckRects"/> on plan transitions.
    ///
    /// <para>
    /// Called once per <see cref="Update"/> tick. Cheap because
    /// <see cref="_stuckWorldRects"/> is typically 0–3 entries. Walks
    /// the list in reverse to allow safe RemoveAt during iteration.
    /// </para>
    /// </summary>
    private void PruneExpiredStuckRects()
    {
        if (_stuckWorldRects.Count == 0)
            return;

        DateTime now = DateTime.UtcNow;
        int removed = 0;
        for (int i = _stuckWorldRects.Count - 1; i >= 0; i--)
        {
            if (_stuckRectExpiryUtc[i] < now)
            {
                _stuckWorldRects.RemoveAt(i);
                _stuckRectExpiryUtc.RemoveAt(i);
                removed++;
            }
        }

        if (removed > 0)
        {
            _areaBlacklist = _stuckWorldRects.Count > 0
                ? new CompositeAreaBlacklist(_staticAreaBlacklist, new RectBlacklist(_stuckWorldRects))
                : _staticAreaBlacklist;
            logger.LogInformation(
                $"[NAV] PruneExpiredStuckRects: removed {removed} expired propagated rect(s) " +
                $"(remaining={_stuckWorldRects.Count})");
        }
    }

    private bool IsBlacklistedPoint(Vector3 worldPoint)
        => AreaBlacklist != null && AreaBlacklist.ContainsWorld(worldPoint);

    private bool TryGetBlockingRectEscapeAware(Vector3 startW, Vector3 endW, out BlacklistRect blockingRect)
    {
        startW = Nav2D(startW);
        endW = Nav2D(endW);

        blockingRect = default;
        if (AreaBlacklist == null) return false;

        if (AreaBlacklist.TryGetContainingRect(startW, out var containingRect))
        {
            var endInsideSame = containingRect.Contains(new Vector2(endW.X, endW.Y));
            if (!endInsideSame)
            {
                return AreaBlacklist.TryGetBlockingRectExcluding(startW, endW, containingRect, out blockingRect);
            }
        }

        return AreaBlacklist.TryGetBlockingRect(startW, endW, out blockingRect);
    }

    private bool SegmentBlockedEscapeAware(Vector3 startW, Vector3 endW)
        => TryGetBlockingRectEscapeAware(startW, endW, out _);

    /// <summary>
    /// Fix 11 (log-41 15:51:46:369 → 15:52:14: assist oscillating in/out of
    /// blacklist rect (952.39,277.05)-(999.27,327.20) every ~3 s for 28 s while
    /// the leader was paused). <see cref="TryInsertDetour"/> had chosen
    /// candidate &lt;961.11, 259.05&gt; whose connecting segment from start
    /// &lt;940.70, 300.69&gt; cleared the original rect's SW corner by only
    /// 0.22 y at X=952.39; the pather then routed through the corner anyway
    /// and the bot drifted into the rect every cycle.
    ///
    /// Returns true when the segment from <paramref name="a"/> to
    /// <paramref name="b"/> "grazes" <paramref name="inflated"/>: both
    /// endpoints lie outside the rect, yet the segment crosses the rect's
    /// interior. <see cref="TryInsertDetour"/> and
    /// <see cref="TryInsertDetourFromRejectedPath"/> use this on top of
    /// <see cref="SegmentBlockedEscapeAware"/> to reject candidates whose
    /// connecting segments clear only the ORIGINAL rect (the existing check)
    /// while passing dangerously close to its corners — close enough that real
    /// unit-movement drift routes the bot through the corner.
    ///
    /// "inflated" is the same rect used for candidate placement (rect.Inflate(m
    /// * 0.5f)). Reusing it here means the safety buffer that gates candidate
    /// positions now also gates the connecting segments — segments must clear
    /// the original rect by the same 6 y as the candidates themselves.
    ///
    /// If either endpoint is INSIDE the inflated rect, the segment is a
    /// legitimate entry/exit transit (e.g., bot starting position happens to
    /// sit in the rect's inflated zone but outside the original rect), not a
    /// graze — returns false so the candidate isn't rejected for a normal
    /// approach. The existing <see cref="SegmentBlockedEscapeAware"/> check
    /// still gates against the ORIGINAL rect in that case.
    /// </summary>
    private static bool SegmentGrazesInflatedRect(Vector3 a, Vector3 b, BlacklistRect inflated)
    {
        var aV = new Vector2(a.X, a.Y);
        var bV = new Vector2(b.X, b.Y);
        if (inflated.Contains(aV) || inflated.Contains(bV))
            return false;

        return SegmentIntersectsAxisAlignedRect(a, b, inflated);
    }

    /// <summary>
    /// Liang–Barsky parametric segment-vs-axis-aligned-rect intersection test
    /// in XY. Returns true if the segment from <paramref name="a"/> to
    /// <paramref name="b"/> touches or crosses <paramref name="rect"/>;
    /// endpoints on the rect's boundary count as intersecting.
    /// </summary>
    private static bool SegmentIntersectsAxisAlignedRect(Vector3 a, Vector3 b, BlacklistRect rect)
    {
        float dx = b.X - a.X;
        float dy = b.Y - a.Y;

        float u1 = 0f;
        float u2 = 1f;

        if (!LiangBarskyClipTest(-dx, a.X - rect.MinX, ref u1, ref u2)) return false;
        if (!LiangBarskyClipTest( dx, rect.MaxX - a.X, ref u1, ref u2)) return false;
        if (!LiangBarskyClipTest(-dy, a.Y - rect.MinY, ref u1, ref u2)) return false;
        if (!LiangBarskyClipTest( dy, rect.MaxY - a.Y, ref u1, ref u2)) return false;

        return u1 <= u2;
    }

    private static bool LiangBarskyClipTest(float p, float q, ref float u1, ref float u2)
    {
        const float Eps = 1e-6f;
        if (MathF.Abs(p) < Eps)
            return q >= 0f;

        float r = q / p;
        if (p < 0f)
        {
            if (r > u2) return false;
            if (r > u1) u1 = r;
        }
        else
        {
            if (r < u1) return false;
            if (r < u2) u2 = r;
        }
        return true;
    }

    private bool SkipBlacklistedWaypoints()
    {
        if (AreaBlacklist == null) return wayPoints.Count > 0;

        if (AreaBlacklist.TryGetContainingRect(Nav2D(playerReader.WorldPos), out _))
            return wayPoints.Count > 0;

        int removed = 0;
        Vector3 firstPopped = default;
        Vector3 lastPopped = default;
        while (wayPoints.Count > 0 && IsBlacklistedPoint(wayPoints.Peek()))
        {
            Vector3 popped = Nav2D(wayPoints.Peek());
            if (removed == 0) firstPopped = popped;
            lastPopped = popped;

            wayPoints.Pop();
            removed++;
        }

        if (removed > 0)
        {
            // log-35 14:08:16:226: this pop was previously silent (only NavDbg
            // gated at 250ms), masking the leader-publishes-bad-waypoint bug for
            // 35 s of debug session. Surfacing at Warning level makes it visible
            // every time a blacklisted entry is filtered, including the post-
            // OnWayPointReached transient-top window that broadcasts to the API.
            Vector3 newTop = wayPoints.Count > 0 ? Nav2D(wayPoints.Peek()) : default;
            logger.LogWarning(
                $"[NAV] SkipBlacklistedWaypoints popped {removed} blacklisted " +
                $"waypoint(s). first={firstPopped} last={lastPopped} " +
                $"newWpTop={(wayPoints.Count > 0 ? newTop.ToString() : "<none>")} " +
                $"wpCount={wayPoints.Count}");

            UpdateTotalRoute();
        }

        return wayPoints.Count > 0;
    }

    private static IEnumerable<Vector3> BuildDetourCandidates(BlacklistRect r, float m, float z)
    {
        float[] t = { 0.25f, 0.5f, 0.75f };

        foreach (var u in t)
        {
            float x = Lerp(r.MinX, r.MaxX, u);
            float y = Lerp(r.MinY, r.MaxY, u);

            yield return new Vector3(r.MinX - m, y, z);
            yield return new Vector3(r.MaxX + m, y, z);
            yield return new Vector3(x, r.MinY - m, z);
            yield return new Vector3(x, r.MaxY + m, z);
        }

        yield return new Vector3(r.MinX - m, r.MinY - m, z);
        yield return new Vector3(r.MaxX + m, r.MinY - m, z);
        yield return new Vector3(r.MaxX + m, r.MaxY + m, z);
        yield return new Vector3(r.MinX - m, r.MaxY + m, z);
    }

    private bool TryInsertDetour(Vector3 startW, Vector3 targetW, BlacklistRect blockingRect)
    {
        if (lastDetourTarget != targetW)
        {
            lastDetourTarget = targetW;
            detourAttemptsForTarget = 0;
        }
        detourAttemptsForTarget++;
        if (detourAttemptsForTarget > MaxDetourAttemptsPerTarget)
        {
            logger.LogWarning($"[BL] detour failed start={startW} target={targetW} rect=({blockingRect.MinX},{blockingRect.MinY})-({blockingRect.MaxX},{blockingRect.MaxY})");
            return false;
        }

        float m = DetourMargin;
        float z = 0f;

        var inflated = blockingRect.Inflate(m * 0.5f);

        Vector3 best = default;
        float bestScore = float.MaxValue;
        bool found = false;

        // Fix 20 (parity with TryInsertDetourFromRejectedPath — see that
        // function's comment for the log-45 evidence trail). The pre-pather
        // detour path is symmetric to the post-rejection path in that it
        // also pushes a candidate onto the wayPoints stack, where the next
        // navigation update's "XY reached" check (line 475) can immediately
        // pop it if the candidate is within ReachedDistance(OutDoorMinDistance)
        // + 0.35 of startW. Apply the same dynamic threshold here.
        float wpReachThreshold = ReachedDistance(OutDoorMinDistance) + 0.35f;
        const float DetourFromStartSafetyMarginYards = 1.0f;
        float minStartSeparation = wpReachThreshold + DetourFromStartSafetyMarginYards;

        foreach (var d in BuildDetourCandidates(inflated, m, z))
        {
            if (IsBlacklistedPoint(d)) continue;

            // Fix 7 (log-37): same as TryInsertDetourFromRejectedPath — reject
            // candidates within MinDetourSeparationYards of either endpoint so
            // a candidate sitting at exactly the target position cannot win
            // the score and produce a no-op duplicate detour.
            if (d.WorldDistanceXYTo(targetW) < MinDetourSeparationYards) continue;
            if (d.WorldDistanceXYTo(startW) < minStartSeparation) continue;

            if (SegmentBlockedEscapeAware(startW, d)) continue;
            if (SegmentBlockedEscapeAware(d, targetW)) continue;

            // Fix 11 (log-41 15:51:46:369: candidate <961.11, 259.05> chosen
            // because the segment from start <940.70, 300.69> clears the
            // ORIGINAL rect at the SW corner (X=952.39) by only 0.22 y — bot
            // then drifted into the rect during pather-driven movement, was
            // ejected by Escape-first, re-pathed, drifted in again, oscillating
            // every ~3 s for 28 s until combat broke the cycle). Require the
            // connecting segments to also clear the INFLATED rect (the same
            // inflated value already used for candidate placement). This adds
            // the safety buffer to the segment validity check that
            // SegmentBlockedEscapeAware lacks — segments must stay 6 y from the
            // original rect's edges, matching the candidate's own placement
            // margin. SegmentGrazesInflatedRect returns false when an endpoint
            // sits inside the inflated rect (legitimate transit, e.g. start
            // happens to lie in the 6 y inflated zone), preserving existing
            // behaviour for that case while the SegmentBlockedEscapeAware check
            // above continues to gate against the original rect.
            if (SegmentGrazesInflatedRect(startW, d, inflated)) continue;
            if (SegmentGrazesInflatedRect(d, targetW, inflated)) continue;

            float score = startW.WorldDistanceXYTo(d) + d.WorldDistanceXYTo(targetW);
            if (score < bestScore)
            {
                bestScore = score;
                best = d;
                found = true;
            }
        }

        if (!found)
        {
            logger.LogWarning(
                $"[BL] detour failed (no candidate): start={startW} target={targetW} rect=({blockingRect.MinX},{blockingRect.MinY})-({blockingRect.MaxX},{blockingRect.MaxY})");
            return false;
        }

        static bool SamePoint(Vector3 a, Vector3 b)
            => a.WorldDistanceXYTo(b) < 0.01f;

        if (wayPoints.Count == 0 || !SamePoint(wayPoints.Peek(), targetW))
        {
            logger.LogInformation($"[BL] detour aborted (target changed): start={startW} target={targetW}");
            return false;
        }

        wayPoints.Pop();
        wayPoints.Push(Nav2D(targetW));
        wayPoints.Push(Nav2D(best));

        routeToNextWaypoint.Clear();
        SyncRouteStateToTop();

        logger.LogInformation($"[BL] detour inserted: {best} before target {targetW}");
        return true;
    }

    private static float Clamp(float v, float min, float max)
        => v < min ? min : (v > max ? max : v);

    private bool TryComputeEscapeOutOfBlacklist(Vector3 startW, out Vector3 escapeW)
    {
        escapeW = startW;

        if (AreaBlacklist == null)
            return false;

        if (!AreaBlacklist.TryGetContainingRect(escapeW, out var rect))
            return false;

        float m = DetourMargin;
        float z = 0f;

        Vector3 goalW = wayPoints.Count > 0 ? wayPoints.Peek() : startW;

        for (int i = 0; i < 8; i++)
        {
            if (!AreaBlacklist.TryGetContainingRect(escapeW, out rect))
                return true;

            if (!TryPickEscapeCandidate(escapeW, goalW, rect, m, z, out var picked))
                return false;

            escapeW = picked;
        }

        return !AreaBlacklist.TryGetContainingRect(escapeW, out _) && !IsBlacklistedPoint(escapeW);
    }

    private bool TryPickEscapeCandidate(
        Vector3 fromW,
        Vector3 goalW,
        BlacklistRect rect,
        float margin,
        float z,
        out Vector3 best)
    {
        best = default;

        var inflated = rect.Inflate(margin * 0.5f);

        var sides = GetSidesByDistance(fromW, rect);
        int sidesToTry = Math.Min(2, sides.Length);

        var near = new List<Vector3>(32);
        for (int i = 0; i < sidesToTry; i++)
        {
            near.AddRange(BuildEdgeEscapeCandidates(fromW, inflated, sides[i], margin, z));
        }

        DedupByXY(near, 0.05f);

        near.Sort((a, b) => fromW.WorldDistanceXYTo(a).CompareTo(fromW.WorldDistanceXYTo(b)));

        foreach (var c in near)
        {
            if (IsBlacklistedPoint(c)) continue;
            if (SegmentBlockedEscapeAware(fromW, c)) continue;

            best = c;
            return true;
        }

        var broad = new List<Vector3>(64);
        foreach (var c in BuildDetourCandidates(inflated, margin, z))
            broad.Add(c);

        DedupByXY(broad, 0.05f);

        broad.Sort((a, b) =>
        {
            float sa = fromW.WorldDistanceXYTo(a) + 0.15f * a.WorldDistanceXYTo(goalW);
            float sb = fromW.WorldDistanceXYTo(b) + 0.15f * b.WorldDistanceXYTo(goalW);
            return sa.CompareTo(sb);
        });

        foreach (var c in broad)
        {
            if (IsBlacklistedPoint(c)) continue;
            if (SegmentBlockedEscapeAware(fromW, c)) continue;

            best = c;
            return true;
        }

        return false;
    }

    private static RectSide[] GetSidesByDistance(Vector3 p, BlacklistRect r)
    {
        float dL = MathF.Abs(p.X - r.MinX);
        float dR = MathF.Abs(r.MaxX - p.X);
        float dB = MathF.Abs(p.Y - r.MinY);
        float dT = MathF.Abs(r.MaxY - p.Y);

        var sides = new (RectSide side, float d)[]
        {
            (RectSide.Left, dL),
            (RectSide.Right, dR),
            (RectSide.Bottom, dB),
            (RectSide.Top, dT),
        };

        Array.Sort(sides, (a, b) => a.d.CompareTo(b.d));

        return new[] { sides[0].side, sides[1].side, sides[2].side, sides[3].side };
    }

    private static IEnumerable<Vector3> BuildEdgeEscapeCandidates(
        Vector3 fromW,
        BlacklistRect r,
        RectSide side,
        float margin,
        float z)
    {
        const float band = 3.0f;
        float[] offsets = { 0f, band, -band, 2 * band, -2 * band };

        float x = fromW.X;
        float y = fromW.Y;

        foreach (var off in offsets)
        {
            switch (side)
            {
                case RectSide.Left:
                    yield return new Vector3(r.MinX - margin, Clamp(y + off, r.MinY, r.MaxY), z);
                    break;

                case RectSide.Right:
                    yield return new Vector3(r.MaxX + margin, Clamp(y + off, r.MinY, r.MaxY), z);
                    break;

                case RectSide.Bottom:
                    yield return new Vector3(Clamp(x + off, r.MinX, r.MaxX), r.MinY - margin, z);
                    break;

                default: // Top
                    yield return new Vector3(Clamp(x + off, r.MinX, r.MaxX), r.MaxY + margin, z);
                    break;
            }
        }

        yield return side switch
        {
            RectSide.Left => new Vector3(r.MinX - margin, r.MinY, z),
            RectSide.Right => new Vector3(r.MaxX + margin, r.MinY, z),
            RectSide.Bottom => new Vector3(r.MinX, r.MinY - margin, z),
            _ => new Vector3(r.MinX, r.MaxY + margin, z),
        };

        yield return side switch
        {
            RectSide.Left => new Vector3(r.MinX - margin, r.MaxY, z),
            RectSide.Right => new Vector3(r.MaxX + margin, r.MaxY, z),
            RectSide.Bottom => new Vector3(r.MaxX, r.MinY - margin, z),
            _ => new Vector3(r.MaxX, r.MaxY + margin, z),
        };
    }

    private static void DedupByXY(List<Vector3> pts, float eps)
    {
        for (int i = 0; i < pts.Count; i++)
        {
            for (int j = pts.Count - 1; j > i; j--)
            {
                if (pts[i].WorldDistanceXYTo(pts[j]) < eps)
                    pts.RemoveAt(j);
            }
        }
    }

    private bool HasBeenActiveRecently()
    {
        return (DateTime.UtcNow - LastActive).TotalSeconds < 2;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private void LogDebug(string text)
    {
        logger.LogDebug($"D: {text}");
    }

    // Helper: pop route node if close enough. Returns true if it popped.
    private bool TryPopRouteTopIfReached(in Vector3 playerPos)
    {
        if (routeToNextWaypoint == null || routeToNextWaypoint.Count == 0)
            return false;

        var routeTop = Nav2D(routeToNextWaypoint.Peek());

        float d = playerPos.WorldDistanceXYTo(routeTop);

        float popDist = POP_DIST;

        if (routeToNextWaypoint.Count == 1 && wayPoints.Count > 0)
        {
            float wpReach = ReachedDistance(OutDoorMinDistance) + 0.35f;
            popDist = wpReach + 0.05f;

            if (popDist < STOP_DIST)
                popDist = STOP_DIST;
        }

        if (d <= popDist)
        {
            routeToNextWaypoint.Pop();
            return true;
        }

        return false;
    }

    #region Logging

    [LoggerMessage(
        EventId = 0040,
        Level = LogLevel.Warning,
        Message = "Unable to find path {start} -> {end}. Character may stuck! {elapsedMs}ms")]
    static partial void LogPathfinderFailed(ILogger logger, Vector3 start, Vector3 end, double elapsedMs);

    [LoggerMessage(
        EventId = 0041,
        Level = LogLevel.Information,
        Message = "Pathfinder - {distance} - {start} -> {end} {elapsedMs}ms")]
    static partial void LogPathfinderSuccess(ILogger logger, float distance, Vector3 start, Vector3 end, double elapsedMs);

    [LoggerMessage(
        EventId = 0042,
        Level = LogLevel.Information,
        Message = "Clear route to waypoint! Stucked for {elapsedMs}ms")]
    static partial void LogClearRouteToWaypointStuck(ILogger logger, double elapsedMs);

    [LoggerMessage(
        EventId = 0043,
        Level = LogLevel.Information,
        Message = "[{name}] distance from nearlest point is {distance}. Have to clear RouteToWaypoint.")]
    static partial void LogV1ClearRouteToWaypoint(ILogger logger, string name, float distance);

    [LoggerMessage(
        EventId = 0044,
        Level = LogLevel.Information,
        Message = "[{name}] distance is close {distance}. Keep RouteToWaypoint.")]
    static partial void LogV1KeepRouteToWaypoint(ILogger logger, string name, float distance);

    [LoggerMessage(
        EventId = 0045,
        Level = LogLevel.Information,
        Message = "[{name}] total distance {totalDistance} > {maxDistancehalf}. Have to clear RouteToWaypoint.")]
    static partial void LogV1ClearRouteToWaypointTooFar(ILogger logger, string name, float totalDistance, float maxDistancehalf);

    #endregion
}
