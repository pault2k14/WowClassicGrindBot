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

    /// <summary>E4: the in-rect ("no-engage") subset of <see cref="BlacklistedMobGuids"/>.
    /// The assist mirrors these into PlayerReader.IsNoEngage so self-defense is
    /// suppressed for mobs the leader determined to be inside a blacklist rect.</summary>
    public int[] NoEngageMobGuids { get; set; } = System.Array.Empty<int>();

    /// <summary>S7.8: the evade subset of <see cref="BlacklistedMobGuids"/> (cross-bot
    /// IsEvade mirror). The assist reads this to classify an adopted leader guid as an
    /// evade (vs a positional blacklist), so the classification survives the cross-bot
    /// boundary instead of defaulting to non-evade.</summary>
    public int[] EvadeMobGuids { get; set; } = System.Array.Empty<int>();

    // ------------------------------------------------------------------
    // Fix AV (Route A) — Stuck-rect propagation from leader to assist.
    //
    // The leader's Navigation can add dynamic blacklist rects via its
    // TryUnstuck path (ATG no-range-progress, CombatGoal stalls). These
    // mark "bot got stuck here" terrain so the leader's pather routes
    // around the area on future requests. Without propagation, the
    // assist's Navigation has no equivalent knowledge — it computes
    // paths through the same bad terrain and gets stuck there (see
    // log-81 mob 2: leader added rect at <-751.02, -4282.16> at
    // 15:59:55:232, assist followed into the same general area and
    // spent 30 s post-combat unable to compute a path out).
    //
    // The leader publishes its full current set of dynamic rects each
    // poll cycle. The assist applies them via
    // Navigation.AddPropagatedStuckRect, which dedups on overlap and
    // refreshes a TTL on re-application. The published list drains in two
    // ways: ClearStuckRects (bail/evade/geometry-trap paths) empties it
    // immediately, and — Fix ES (run-148) — LeaderNavigationProvider's own
    // age cap (PublishedStuckRectTtlSec) drops individual rects during
    // normal grinding, where ClearStuckRects never fires. Once a rect
    // leaves the published list the assist stops refreshing it and its
    // propagated copy expires via PropagatedStuckRectTtlSec in Navigation.cs.
    // ------------------------------------------------------------------

    /// <summary>
    /// Snapshot of dynamic stuck rects the leader's Navigation has added
    /// during this session via its TryUnstuck path. Each entry's
    /// (CenterX, CenterY) is in world-space coordinates and HalfSize is
    /// in world yards. Assist bots apply each entry via
    /// <c>Navigation.AddPropagatedStuckRect</c> on every poll cycle —
    /// duplicates are deduped on overlap and TTL-refreshed.
    /// </summary>
    public StuckRectInfo[] StuckRects { get; set; } = System.Array.Empty<StuckRectInfo>();

    // ------------------------------------------------------------------
    // Fix GA (run-167) — backtrack-mode coordination fields.
    //
    // When a bot enters BL-backtrack (Fix FN/FT/FX), the partner needs
    // to know in order to coordinate (assist follows leader to same
    // waypoint, doesn't pursue leader's oscillating target, doesn't
    // navigate into the rect when leader is inside, etc.) and to apply
    // Fix FZ's gate on `Fix L party-assist override` for the published
    // aggressor.
    //
    // All five fields are symmetrically defined on AssistState as well
    // so the leader can see assist's backtrack state.
    // ------------------------------------------------------------------

    /// <summary>Coordinated-backtrack phase (Fix FN/FT/FX state machine).
    /// IsActivelyBacktracking() == (BacktrackPhase != None) ≡ legacy IsBacktracking.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BacktrackPhase BacktrackPhase { get; set; }

    /// <summary>The aggressor guid driving the backtrack
    /// (_btTargetGuid), or 0 when IsBacktracking is false or the
    /// backtrack is Fix FT's BL-escape with no specific target.</summary>
    public int BacktrackAggressorGuid { get; set; }

    /// <summary>The bot's local recheck cache result for the
    /// aggressor at last recheck. True = aggressor was IN_RECT at
    /// last evaluation; false = NOT_IN_RECT or unknown. Source of
    /// truth for the partner's Fix FZ gate.</summary>
    public bool BacktrackAggressorInRect { get; set; }

    /// <summary>This bot's current Navigation.IsInBlacklistArea()
    /// result. Used by the partner's FFG to decide whether to follow
    /// the bot's body into a rect (don't) or hold/coordinate.</summary>
    public bool InsideBlacklistArea { get; set; }

    /// <summary>Route waypoint index this bot is currently
    /// retreating to during backtrack, or -1 when not backtracking.
    /// The assist mirrors this to coordinate same-waypoint
    /// targeting in Fix GC.</summary>
    public int BacktrackCurrentWaypointIdx { get; set; } = -1;

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

/// <summary>
/// Fix AV (Route A): wire-format DTO for a single propagated stuck rect.
/// Mirrors the geometry that the leader's <c>Navigation.AddStuckRect</c>
/// would have produced locally — center (already forward-offset if the
/// leader supplied a direction at add time) plus axis-aligned half-size.
/// The assist reconstructs the rect via <c>Navigation.AddPropagatedStuckRect</c>.
/// <para>
/// Uses flat floats for the same reason the rest of <see cref="LeaderState"/>
/// does — to keep JSON serialization free of any Vector3Converter dependency.
/// Mutable properties (rather than a record) because <c>System.Text.Json</c>
/// handles property setters most predictably across the build's net runtime.
/// </para>
/// </summary>
public sealed class StuckRectInfo
{
    /// <summary>World-space X of the rect center.</summary>
    public float CenterX { get; set; }

    /// <summary>World-space Y of the rect center.</summary>
    public float CenterY { get; set; }

    /// <summary>Axis-aligned half-size in world yards. Matches
    /// <c>Navigation.StuckRectHalfSizeY</c> at add time.</summary>
    public float HalfSize { get; set; }
}
