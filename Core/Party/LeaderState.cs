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
