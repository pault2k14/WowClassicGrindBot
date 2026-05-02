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

    /// <summary>
    /// Records a newly-evaded mob GUID. No-op if <paramref name="guid"/> is 0
    /// or already present. Rebuilds the snapshot atomically.
    /// </summary>
    public void AddBlacklistedMobGuid(int guid)
    {
        if (guid == 0)
            return;

        if (_blacklistedGuids.Add(guid))
            _snapshot = System.Linq.Enumerable.ToArray(_blacklistedGuids);
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
    }
}
