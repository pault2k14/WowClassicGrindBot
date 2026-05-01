using System;
using System.Numerics;
using System.Text.Json.Serialization;

namespace Core.Party;

/// <summary>
/// Snapshot of an assist bot's current state, POSTed to
/// <c>POST /party/assist/state</c> on the leader's Blazor server.
/// The leader reads this from <see cref="AssistStateStore"/> in later steps
/// instead of parsing chat messages.
/// </summary>
public sealed class AssistState
{
    /// <summary>
    /// Unique identifier for this assist — must match <see cref="PartyApiConfig.AssistId"/>
    /// configured on each assist bot. Used as the key in <see cref="AssistStateStore"/>.
    /// </summary>
    public string AssistId { get; set; } = string.Empty;

    // World-space position
    public float WorldX { get; set; }
    public float WorldY { get; set; }
    public float WorldZ { get; set; }

    // Map-space position
    public float MapX { get; set; }
    public float MapY { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BotStatus Status { get; set; }

    /// <summary>
    /// True when the assist cannot navigate to the leader and needs the leader
    /// to return. Distinct from <see cref="Status"/> == CantFollow because this
    /// flag is also set during evade recovery (when the status may still be
    /// NavigatingToLeader or Waiting). The leader checks either condition via
    /// <see cref="AssistStateStore.AnyAssistCantFollow"/>.
    /// </summary>
    public bool CantFollow { get; set; }

    public int HealthPercent { get; set; }
    public bool InCombat { get; set; }

    /// <summary>UTC timestamp set (or overwritten) by the leader controller on receipt.</summary>
    public DateTime Timestamp { get; set; }

    // ------------------------------------------------------------------
    // Convenience — not serialised.
    // ------------------------------------------------------------------

    [JsonIgnore]
    public Vector3 WorldPos => new(WorldX, WorldY, WorldZ);

    [JsonIgnore]
    public Vector3 MapPosNoZ => new(MapX, MapY, 0f);

    [JsonIgnore]
    public double AgeMs => (DateTime.UtcNow - Timestamp).TotalMilliseconds;
}
