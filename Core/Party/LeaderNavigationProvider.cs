using System.Collections.Generic;
using System.Numerics;

namespace Core.Party;

/// <summary>
/// Singleton bridge between goal-layer writers and party-layer readers.
///
/// <para>
/// <b>Waypoint sharing:</b>
/// <see cref="FollowRouteGoal"/> writes <see cref="CurrentTargetWaypointW"/> whenever
/// the leader advances to a new patrol waypoint.
/// <see cref="LeaderStateService"/> reads it and includes it in the HTTP response
/// so assist bots can navigate to the same destination rather than chasing the
/// leader's live position.
/// </para>
///
/// <para>
/// <b>Mob blacklist:</b>
/// <see cref="GoapAgent"/> writes <see cref="BlacklistedMobGuids"/> when an evade
/// event fires. <see cref="LeaderStateService"/> includes the current set in every
/// response. Assist bots diff the set on each poll and call
/// <c>playerReader.IgnoreTarget</c> for any new entries.
/// </para>
///
/// <para>
/// <b>Stuck-rect propagation (Fix AV / Route A):</b>
/// <see cref="GoapAgent"/> subscribes to <c>Navigation.OnStuckRectAdded</c> and
/// <c>Navigation.OnStuckRectsCleared</c> (leader mode only) and forwards each
/// rect into <see cref="AddStuckRect"/> / <see cref="ClearStuckRects"/> here.
/// <see cref="LeaderStateService"/> reads the snapshot each poll. Assist bots
/// apply the rects via <c>Navigation.AddPropagatedStuckRect</c> so their
/// pathfinder routes around the same terrain the leader marked as bad.
/// </para>
///
/// <para>Thread safety: all writes happen on the GOAP/addon thread; reads happen on
/// the HTTP server thread. The snapshot arrays are replaced atomically with
/// <see langword="volatile"/> semantics so readers always see a consistent array
/// reference, never a partially-written one.</para>
/// </summary>
public sealed class LeaderNavigationProvider
{
    // -----------------------------------------------------------------------
    // Waypoint sharing
    // -----------------------------------------------------------------------

    private volatile bool _hasTargetWaypoint;
    private float _targetWaypointWorldX;
    private float _targetWaypointWorldY;

    /// <summary>True when the leader has an active patrol waypoint to share.</summary>
    public bool HasTargetWaypoint => _hasTargetWaypoint;

    /// <summary>World X of the leader's current navigation target waypoint.</summary>
    public float TargetWaypointWorldX => _targetWaypointWorldX;

    /// <summary>World Y of the leader's current navigation target waypoint.</summary>
    public float TargetWaypointWorldY => _targetWaypointWorldY;

    /// <summary>
    /// Called by <see cref="FollowRouteGoal"/> when the leader begins navigating
    /// toward a new waypoint. <paramref name="worldPos"/> must be in world-space
    /// coordinates (large negative values typical for Azeroth), not map-space (0-100).
    /// </summary>
    public void SetTargetWaypoint(Vector3 worldPos)
    {
        _targetWaypointWorldX = worldPos.X;
        _targetWaypointWorldY = worldPos.Y;
        _hasTargetWaypoint = worldPos != default; // volatile write last — acts as memory fence
    }

    /// <summary>
    /// Clears the published waypoint. Called by <see cref="FollowRouteGoal"/>
    /// when the leader pauses, evades, or enters combat — any state where
    /// waypoint sharing should not drive the assist's navigation.
    /// </summary>
    public void ClearTargetWaypoint()
    {
        _hasTargetWaypoint = false; // volatile write — readers see null waypoint immediately
        _targetWaypointWorldX = 0f;
        _targetWaypointWorldY = 0f;
    }

    // -----------------------------------------------------------------------
    // Fix GA (run-167) — Backtrack-mode coordination publish (scope fix 2026-06-06)
    //
    // Originally these lived on Navigation, but Navigation is scoped per-bot
    // while the publishers (PartyStatePublisher / LeaderStateService) are
    // singletons. The .NET DI validator rejects a singleton depending on a
    // scoped service. Moving the fields here — LeaderNavigationProvider is
    // already a singleton bridge between scoped goal writers and singleton
    // publish readers — preserves the publish semantics without the scope
    // collision.
    //
    // FollowRouteGoal writes these from its UpdateBacktrackStateMachine on
    // backtrack entry, per-waypoint, and exit. The publishers read the
    // snapshot.
    // -----------------------------------------------------------------------

    private volatile BacktrackPhase _backtrackPhase = BacktrackPhase.None;
    private volatile bool _insideBlacklistArea;
    private volatile int _backtrackAggressorGuid;
    private volatile bool _backtrackAggressorInRect;
    private volatile int _backtrackCurrentWaypointIdx = -1;

    /// <summary>True iff this bot is currently inside an FRG backtrack
    /// state-machine (phase != None). Mirror of <c>navigation.IsBacktrackingActive</c>
    /// for partner-state publish.</summary>
    public BacktrackPhase BacktrackPhase => _backtrackPhase;

    public void SetBacktrackPhase(BacktrackPhase phase) => _backtrackPhase = phase;

    /// <summary>True iff this bot is currently inside any static BL rect.
    /// Snapshot of <c>navigation.IsInBlacklistArea()</c> taken at publish points.</summary>
    public bool InsideBlacklistArea => _insideBlacklistArea;

    /// <summary>Guid of the in-rect aggressor driving the backtrack, or 0 when
    /// not backtracking / Fix FT BL-escape mode (no specific aggressor).</summary>
    public int BacktrackAggressorGuid => _backtrackAggressorGuid;

    /// <summary>True iff the recheck cache currently reads InRect for the
    /// aggressor (authoritative verdict from the active recheck operation).</summary>
    public bool BacktrackAggressorInRect => _backtrackAggressorInRect;

    /// <summary>Route waypoint index this bot is retreating to, or -1 when
    /// not backtracking.</summary>
    public int BacktrackCurrentWaypointIdx => _backtrackCurrentWaypointIdx;

    /// <summary>
    /// FRG entry — set all backtrack-publish fields atomically. Called when
    /// FRG transitions into BacktrackPhase.None → Navigating.
    /// </summary>
    public void SetBacktrackEntry(int aggressorGuid, bool aggressorInRect, bool insideBl, int waypointIdx)
    {
        _backtrackAggressorGuid = aggressorGuid;
        _backtrackAggressorInRect = aggressorInRect;
        _insideBlacklistArea = insideBl;
        _backtrackCurrentWaypointIdx = waypointIdx;
    }

    /// <summary>
    /// FRG per-waypoint update — refresh the current rect verdict, waypoint idx,
    /// and inside-BL snapshot during an active backtrack.
    /// </summary>
    public void UpdateBacktrackProgress(bool aggressorInRect, bool insideBl, int waypointIdx)
    {
        _backtrackAggressorInRect = aggressorInRect;
        _insideBlacklistArea = insideBl;
        _backtrackCurrentWaypointIdx = waypointIdx;
    }

    /// <summary>
    /// Fix GF (run-168, phase D) — FT-mode opportunistic recheck setter.
    /// FT-mode (BL-escape) enters with BacktrackAggressorGuid=0 because there's
    /// no specific tracked aggressor. When the FT-mode waypoint Evaluating phase
    /// runs an opportunistic recheck on the current IsIgnored target, this
    /// setter refines the published guid so the assist can consult the verdict
    /// (via leader.BacktrackAggressorGuid + leader.BacktrackAggressorInRect).
    /// Called from FRG.UpdateBacktrackStateMachine before UpdateBacktrackProgress.
    /// </summary>
    public void UpdateBacktrackAggressorGuid(int aggressorGuid)
    {
        _backtrackAggressorGuid = aggressorGuid;
    }

    /// <summary>
    /// FRG exit — clear backtrack-publish fields. Called when FRG transitions
    /// out of backtrack (any exit path).
    /// </summary>
    public void ClearBacktrack()
    {
        _backtrackAggressorGuid = 0;
        _backtrackAggressorInRect = false;
        _backtrackCurrentWaypointIdx = -1;
        // Note: _insideBlacklistArea is NOT cleared here — it reflects current
        // bot position regardless of backtrack state. FRG continues to publish
        // it via UpdateInsideBlacklistArea below during normal patrol.
    }

    /// <summary>
    /// Standalone update for InsideBlacklistArea — written each FRG tick so
    /// the partner sees current position even outside backtrack. Cheap volatile
    /// write; no allocation.
    /// </summary>
    public void UpdateInsideBlacklistArea(bool insideBl)
    {
        _insideBlacklistArea = insideBl;
    }

    // -----------------------------------------------------------------------
    // Approach-start anchor
    // -----------------------------------------------------------------------

    private volatile bool _hasApproachStart;
    private float _approachStartWorldX;
    private float _approachStartWorldY;

    /// <summary>True when the leader is in ATG/PTG and has published an approach anchor.</summary>
    public bool HasApproachStart => _hasApproachStart;

    /// <summary>World X of the leader's position when ATG/PTG began.</summary>
    public float ApproachStartWorldX => _approachStartWorldX;

    /// <summary>World Y of the leader's position when ATG/PTG began.</summary>
    public float ApproachStartWorldY => _approachStartWorldY;

    /// <summary>
    /// Called by <see cref="Goals.ApproachTargetGoal"/> on the leader side when it
    /// begins approaching a mob. Records the leader's current world position as a
    /// stable anchor the assist navigates to instead of chasing the leader's moving
    /// body during the approach phase.
    /// </summary>
    public void SetApproachStart(Vector3 worldPos)
    {
        _approachStartWorldX = worldPos.X;
        _approachStartWorldY = worldPos.Y;
        _hasApproachStart = true; // volatile write last — acts as memory fence
    }

    /// <summary>
    /// Called by <see cref="Goals.ApproachTargetGoal"/> on leader exit.
    /// Clears the anchor so the assist reverts to normal patrol-follow behaviour.
    /// </summary>
    public void ClearApproachStart()
    {
        _hasApproachStart = false; // volatile write first — readers see cleared flag immediately
        _approachStartWorldX = 0f;
        _approachStartWorldY = 0f;
    }

    // -----------------------------------------------------------------------
    // Pause-for-assist flag
    // -----------------------------------------------------------------------

    private volatile bool _isPausedForAssist;

    /// <summary>
    /// True when <see cref="Goals.FollowRouteGoal"/> is actively pausing in
    /// the pause-for-assist branch (assist too far / Stuck / CantFollow).
    /// <para>
    /// Fix T (log-63 19:47:02 → 19:48:21): without this signal,
    /// <see cref="LeaderStateService.DetermineStatus"/> reports
    /// <see cref="BotStatus.Patrolling"/> while the leader is actually
    /// standing still waiting for the assist (because the leader's current
    /// goal is still FRG by name). The assist's FFG keys
    /// waypoint-sharing-vs-position-chase off <c>leader.Status == Patrolling</c>,
    /// so it stays in waypoint-sharing mode with the leader's last-published
    /// waypoint as the nav target — even when that waypoint is unreachable
    /// (e.g., blocked by blacklist). Switching the reported status to
    /// <see cref="BotStatus.Waiting"/> during a pause-for-assist breaks the
    /// waypoint-sharing branch and lets the assist target the leader's
    /// actual body via position-chase, which is what's actually wanted while
    /// the leader is stationary.
    /// </para>
    /// <para>Thread-safe via <see langword="volatile"/>; written on the GOAP
    /// thread by FRG, read on the HTTP thread by LeaderStateService.</para>
    /// </summary>
    public bool IsPausedForAssist => _isPausedForAssist;

    /// <summary>
    /// Called by <see cref="Goals.FollowRouteGoal"/> when it enters or exits the
    /// pause-for-assist branch. See <see cref="IsPausedForAssist"/> for rationale.
    /// </summary>
    public void SetPausedForAssist(bool value)
    {
        _isPausedForAssist = value;
    }

    // -----------------------------------------------------------------------
    // Mob blacklist sharing
    // -----------------------------------------------------------------------

    private readonly HashSet<int> _blacklistedGuids = new();

    // Snapshot exposed to LeaderStateService / HTTP serialization.
    // Replaced as a whole reference so readers always get a consistent array.
    private volatile int[] _snapshot = System.Array.Empty<int>();

    /// <summary>
    /// Current set of mob GUIDs the leader has blacklisted due to evade or
    /// other unrecoverable states. Snapshot is rebuilt on every change.
    /// </summary>
    public int[] BlacklistedMobGuidsSnapshot => _snapshot;

    // E4: the IN-RECT subset of the blacklist — mobs the leader determined to be
    // inside a blacklist rect at blacklist time. The assist mirrors these into
    // PlayerReader.IsNoEngage so its self-defense is suppressed for the same mobs
    // (otherwise the assist could be recruited to chase an in-rect mob). Always a
    // subset of _blacklistedGuids.
    private readonly HashSet<int> _noEngageGuids = new();
    private volatile int[] _noEngageSnapshot = System.Array.Empty<int>();

    // S7.8: evade subset of the blacklist (cross-bot IsEvade mirror). Exact
    // parallel to _noEngageGuids above. Published as EvadeMobGuids so the assist
    // can distinguish evade entries from positional ones when adopting leader guids.
    private readonly HashSet<int> _evadeGuids = new();
    private volatile int[] _evadeSnapshot = System.Array.Empty<int>();

    /// <summary>
    /// Snapshot of the in-rect ("no-engage") subset, published alongside
    /// <see cref="BlacklistedMobGuidsSnapshot"/>. Rebuilt on every change.
    /// </summary>
    public int[] NoEngageMobGuidsSnapshot => _noEngageSnapshot;

    /// <summary>
    /// S7.8: snapshot of the evade subset, published alongside
    /// <see cref="NoEngageMobGuidsSnapshot"/>. Rebuilt on every change.
    /// </summary>
    public int[] EvadeMobGuidsSnapshot => _evadeSnapshot;

    /// <summary>
    /// Records a newly-evaded mob GUID. No-op if <paramref name="guid"/> is 0
    /// or already present. Rebuilds the snapshot atomically.
    /// </summary>
    public void AddBlacklistedMobGuid(int guid, bool inRect = false, bool isEvade = false)
    {
        if (guid == 0)
            return;

        if (_blacklistedGuids.Add(guid))
            _snapshot = System.Linq.Enumerable.ToArray(_blacklistedGuids);

        // E4: record the in-rect subset separately so the assist can mirror it
        // into IsNoEngage. A guid only ever transitions false->true here (a mob
        // determined in-rect stays in-rect for the session); we never demote.
        if (inRect && _noEngageGuids.Add(guid))
            _noEngageSnapshot = System.Linq.Enumerable.ToArray(_noEngageGuids);

        // S7.8: record the evade subset separately (cross-bot IsEvade mirror).
        // Parallel to _noEngageGuids above; never-demote. Supplies EvadeMobGuids
        // so the assist can classify adopted guids as evade vs positional.
        if (isEvade && _evadeGuids.Add(guid))
            _evadeSnapshot = System.Linq.Enumerable.ToArray(_evadeGuids);
    }

    /// <summary>
    /// Removes all blacklisted GUIDs (e.g. on bot restart).
    /// Rebuilds the snapshot atomically.
    /// </summary>
    public void ClearBlacklistedMobGuids()
    {
        if (_blacklistedGuids.Count == 0)
            return;

        _blacklistedGuids.Clear();
        _snapshot = System.Array.Empty<int>();

        _noEngageGuids.Clear();
        _noEngageSnapshot = System.Array.Empty<int>();

        _evadeGuids.Clear();
        _evadeSnapshot = System.Array.Empty<int>();
    }

    // -----------------------------------------------------------------------
    // Fix AV (Route A) — Stuck-rect sharing
    //
    // Mirrors the leader's Navigation._stuckWorldRects list so the leader's
    // current dynamic blacklist rects can be serialized into LeaderState
    // and consumed by the assist. The leader-side wiring (in GoapAgent)
    // subscribes to Navigation.OnStuckRectAdded and
    // Navigation.OnStuckRectsCleared and forwards the events here. Reads
    // happen on the HTTP server thread when LeaderStateService builds the
    // snapshot for a poll response.
    //
    // Lifetime semantics on this side:
    //   - AddStuckRect appends if the new center is not already inside an
    //     existing rect (dedup matches Navigation.AddStuckRect's dedup) and
    //     stamps the add time.
    //   - ClearStuckRects empties the list (matches
    //     Navigation.ClearStuckRects). On the next poll the assist sees
    //     an empty array and lets its propagated rects expire via the
    //     TTL in Navigation (PropagatedStuckRectTtlSec).
    //   - PruneExpiredStuckRects drops any published rect older than
    //     PublishedStuckRectTtlSec, called once per leader GOAP tick.
    //
    // ── Fix ES (run-148 deadlock) — the publish side now HAS its own TTL ──
    //
    // The previous design comment claimed "The leader does NOT need its own
    // TTL here ... the leader's plan transitions (ATG.OnExit, CombatGoal
    // post-combat) already drive ClearStuckRects on Navigation, which we
    // mirror." That assumption was FALSE. ClearStuckRects is invoked ONLY on
    // failure paths — ATG bail-out (ApproachTargetGoal.cs:509/543), PTG
    // bail (PullTargetGoal.cs:327), and CombatGoal evade/geometry-trap
    // (CombatGoal.cs:381/755/942). It is NOT called on ATG.OnExit or on a
    // normal kill. During ordinary grinding the leader therefore never
    // cleared its rects, and because Navigation's LOCAL rects carry a
    // DateTime.MaxValue expiry (never auto-pruned), the published list grew
    // monotonically and was broadcast forever.
    //
    // Evidence (run-148): the leader added a local stuck rect at
    // <-555.7984,-4844.085> at 01:18:01, never cleared it (leader stuck-rect
    // total climbed 1→5 over 19 min across 106 ATG OnEnters with zero
    // clears), and kept publishing it. The assist re-applied it every ~250ms
    // poll, refreshing its 90s propagated TTL indefinitely (Navigation.cs
    // TryAddStuckRectInternal propagated-refresh path). 17 minutes later the
    // assist had to path through that rect to rejoin the leader; every path
    // crossed it → blacklistRejectCooldown → assist frozen → leader paused
    // for the too-far assist → mutual standstill.
    //
    // The leader's Navigation list is still the source of truth for the
    // leader's OWN pather, but the PUBLISHED copy must not outlive the
    // terrain's relevance just because ClearStuckRects didn't happen to
    // fire. A self-contained age cap here bounds the propagated rect's life:
    // once it ages out of the snapshot the assist stops refreshing it and
    // its copy expires (<= PropagatedStuckRectTtlSec later) on the assist's
    // own prune (which Fix ER made unconditional). A genuinely-persistent
    // bad spot is re-shared the next time the leader sticks there and
    // Navigation re-fires OnStuckRectAdded after its own rect has cleared.
    // -----------------------------------------------------------------------

    // How long a published stuck rect is broadcast before PruneExpiredStuckRects
    // drops it, independent of whether ClearStuckRects ever fires. Matched to
    // the consumer-side PropagatedStuckRectTtlSec (90s) so the publish window
    // and the assist's local TTL are symmetric and easy to reason about: a rect
    // is broadcast for 90s after the leader adds it, and the assist's copy
    // expires at most 90s after the broadcast stops. Worst-case propagated
    // lifetime ≈ this + PropagatedStuckRectTtlSec ≈ 180s.
    private const double PublishedStuckRectTtlSec = 90.0;

    private readonly List<StuckRectInfo> _stuckRects = new();

    // Parallel to _stuckRects (same index = same rect): UTC time the rect was
    // appended, used by PruneExpiredStuckRects for the age cap. Single-writer
    // (GOAP thread), mirroring _stuckRects' threading model.
    private readonly List<System.DateTime> _stuckRectAddedUtc = new();

    // Snapshot exposed to LeaderStateService / HTTP serialization.
    // Replaced as a whole reference (volatile) so HTTP readers always
    // see a consistent array — same pattern as _snapshot above.
    private volatile StuckRectInfo[] _stuckRectsSnapshot = System.Array.Empty<StuckRectInfo>();

    /// <summary>
    /// Current set of dynamic stuck rects the leader has added via its
    /// <c>Navigation.AddStuckRect</c> path. Snapshot is rebuilt on every
    /// change (add, clear, or prune). Consumed by <see cref="LeaderStateService"/>
    /// when assembling the <see cref="LeaderState.StuckRects"/> field.
    /// </summary>
    public StuckRectInfo[] StuckRectsSnapshot => _stuckRectsSnapshot;

    /// <summary>
    /// Records a stuck rect the leader's Navigation just added. Dedups
    /// against existing rects (skips if <paramref name="centerX"/>,
    /// <paramref name="centerY"/> is already inside an existing rect —
    /// matches the dedup criterion in <c>Navigation.AddStuckRect</c>).
    /// Rebuilds the snapshot atomically.
    /// </summary>
    public void AddStuckRect(float centerX, float centerY, float halfSize)
    {
        // Single-writer (GOAP thread) — no lock needed for the list itself,
        // only the snapshot reference swap needs ordering, which volatile
        // semantics provide.
        for (int i = 0; i < _stuckRects.Count; i++)
        {
            StuckRectInfo r = _stuckRects[i];
            if (centerX >= r.CenterX - r.HalfSize && centerX <= r.CenterX + r.HalfSize &&
                centerY >= r.CenterY - r.HalfSize && centerY <= r.CenterY + r.HalfSize)
            {
                return; // already covered — Navigation's own dedup would also skip
            }
        }

        _stuckRects.Add(new StuckRectInfo
        {
            CenterX = centerX,
            CenterY = centerY,
            HalfSize = halfSize
        });
        _stuckRectAddedUtc.Add(System.DateTime.UtcNow);
        _stuckRectsSnapshot = _stuckRects.ToArray();
    }

    /// <summary>
    /// Fix ES (run-148): drops published stuck rects older than
    /// <see cref="PublishedStuckRectTtlSec"/>. Must be called on the GOAP
    /// thread (single-writer with AddStuckRect / ClearStuckRects). The leader
    /// invokes this once per GOAP tick so a stale rect ages out of the
    /// broadcast even when no plan transition ever fires ClearStuckRects.
    /// Rebuilds the snapshot atomically only when something was removed.
    /// </summary>
    public void PruneExpiredStuckRects()
    {
        if (_stuckRects.Count == 0)
            return;

        System.DateTime now = System.DateTime.UtcNow;
        bool removedAny = false;
        for (int i = _stuckRects.Count - 1; i >= 0; i--)
        {
            if ((now - _stuckRectAddedUtc[i]).TotalSeconds >= PublishedStuckRectTtlSec)
            {
                _stuckRects.RemoveAt(i);
                _stuckRectAddedUtc.RemoveAt(i);
                removedAny = true;
            }
        }

        if (removedAny)
        {
            _stuckRectsSnapshot = _stuckRects.Count > 0
                ? _stuckRects.ToArray()
                : System.Array.Empty<StuckRectInfo>();
        }
    }

    /// <summary>
    /// Clears the published stuck-rect list. Called by the leader-side
    /// subscriber when the leader's <c>Navigation.ClearStuckRects</c> fires
    /// (the bail/evade/geometry-trap paths in ATG/PTG/CombatGoal). On the
    /// next assist poll, the assist will see an empty array and its
    /// locally-tracked propagated rects will expire via TTL — see
    /// <c>Navigation.PropagatedStuckRectTtlSec</c>. The age-based
    /// <see cref="PruneExpiredStuckRects"/> covers the normal-grind case
    /// where this clear never fires.
    /// </summary>
    public void ClearStuckRects()
    {
        if (_stuckRects.Count == 0)
            return;

        _stuckRects.Clear();
        _stuckRectAddedUtc.Clear();
        _stuckRectsSnapshot = System.Array.Empty<StuckRectInfo>();
    }
}
