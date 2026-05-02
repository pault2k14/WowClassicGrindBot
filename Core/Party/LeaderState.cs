using System;
using System.Numerics;
using System.Text.Json.Serialization;

namespace Core.Party;

/// <summary>
/// Snapshot of the leader bot's current state, served by
/// <c>GET /party/leader/state</c> and consumed by assist bots.
/// <para>
/// Coordinates use flat floats rather than <see cref="Vector3"/> to avoid
/// any dependency on the Blazor server's custom Vector3Converter. Convenience
/// properties reconstruct the vectors on the assist side.
/// </para>
/// </summary>
public sealed class LeaderState
{
    // World-space position (PPather / navigation coordinate space)
    public float WorldX { get; set; }
    public float WorldY { get; set; }
    public float WorldZ { get; set; }

    // Map-space position (addon cell 1/2 * 10, used for SetSingleWaypoint)
    public float MapX { get; set; }
    public float MapY { get; set; }

    public int UIMapId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BotStatus Status { get; set; }

    public int HealthPercent { get; set; }
    public bool InCombat { get; set; }
    public int TargetGuid { get; set; }
    public DateTime Timestamp { get; set; }

    // ------------------------------------------------------------------
    // Waypoint sharing — leader's current patrol navigation target.
    // Only valid when HasTargetWaypoint = true AND Status = Patrolling.
    // Coordinates are world-space (large negative values for Azeroth).
    // ------------------------------------------------------------------

    /// <summary>True when the leader has an active waypoint to share.</summary>
    public bool HasTargetWaypoint { get; set; }

    /// <summary>World X of the leader's current navigation waypoint.</summary>
    public float TargetWaypointWorldX { get; set; }

    /// <summary>World Y of the leader's current navigation waypoint.</summary>
    public float TargetWaypointWorldY { get; set; }

    // ------------------------------------------------------------------
    // Approach-start anchor — leader's world position at the moment it
    // entered ApproachTargetGoal or PullTargetGoal. The assist navigates
    // to this fixed point instead of chasing the leader's moving body
    // during the approach, so both bots start the interact-key close on
    // the mob from the same geographic location. Cleared on ATG/PTG exit.
    // ------------------------------------------------------------------

    /// <summary>True when the leader is in ATG/PTG and has published an anchor.</summary>
    public bool HasApproachStart { get; set; }

    /// <summary>World X of the leader's position when approach began.</summary>
    public float ApproachStartWorldX { get; set; }

    /// <summary>World Y of the leader's position when approach began.</summary>
    public float ApproachStartWorldY { get; set; }

    // ------------------------------------------------------------------
    // Mob blacklist — GUIDs evaded or permanently blacklisted by the
    // leader. Assist bots diff this list each poll and call
    // playerReader.IgnoreTarget for any new entries.
    // ------------------------------------------------------------------

    /// <summary>Snapshot of all mob GUIDs the leader has blacklisted this session.</summary>
    public int[] BlacklistedMobGuids { get; set; } = System.Array.Empty<int>();

    // ------------------------------------------------------------------
    // Convenience — not serialised, reconstructed on the consumer side.
    // ------------------------------------------------------------------

    [JsonIgnore]
    public Vector3 WorldPos => new(WorldX, WorldY, WorldZ);

    /// <summary>Map position as a <see cref="Vector3"/> with Z=0 (same form as
    /// <c>playerReader.MapPosNoZ</c>) for direct use with
    /// <c>navigation.SetSingleWaypoint</c>.</summary>
    [JsonIgnore]
    public Vector3 MapPosNoZ => new(MapX, MapY, 0f);

    /// <summary>Age of this snapshot in milliseconds. The assist uses this to
    /// detect stale data (leader process crash / network loss).</summary>
    [JsonIgnore]
    public double AgeMs => (DateTime.UtcNow - Timestamp).TotalMilliseconds;
}
