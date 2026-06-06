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

    /// <summary>
    /// GUID of the assist's current target, or 0 if no target. Symmetric to
    /// <see cref="LeaderState.TargetGuid"/>; used by the leader's
    /// <c>GoapAgent.PartyMemberInCombat()</c> Fix EY disjunct as a polled
    /// fallback when <c>bits.FocusTarget()</c> / <c>bits.FocusTarget_Combat()</c>
    /// go stale because the assist is out of the leader's WoW client
    /// visibility range.
    ///
    /// <para>Fix EY (run-152 13:31:55 standoff): symmetric counterpart to
    /// Fix EX (PartyMemberInCombat's AssistFocus polled fallback at
    /// GoapAgent.cs ~line 2013, which uses
    /// <c>leaderConnection.LastLeaderState.TargetGuid</c>). When the assist
    /// moves &gt;~80y from the leader during Section D caster-retreat (run-152
    /// peak: 424y at &lt;253,-4648&gt;), the leader's WoW client cannot reliably
    /// re-read the focus's target slot, so the addon's
    /// <c>bits.FocusTarget()</c> and <c>bits.FocusTarget_Combat()</c> may
    /// stick at stale values. Without a polled fallback, the leader-side
    /// PartyMemberInCombat disjunct 3 (Fix AP gate, which requires both bits)
    /// can either over-fire (stuck TRUE → permanent partyincombat=true) or
    /// under-fire (stuck FALSE → Combat plan blocked). The polled
    /// AssistState carries the assist's authoritative
    /// <see cref="InCombat"/> AND TargetGuid, allowing a fresh-polled
    /// disjunct that fires when the assist publishes "I am in combat with
    /// target X" regardless of what the leader's bits report.</para>
    /// </summary>
    public int TargetGuid { get; set; }

    /// <summary>
    /// Assist's current route waypoint index, or -1 if unsynced / no route loaded.
    /// Mirrors <c>FollowFocusGoal._assistRouteIndex</c>. Published so the leader
    /// has symmetric route-progress info (the leader already publishes its own
    /// position relative to its route via TargetWaypoint). Diagnostic logging
    /// uses this to show the trailing-by-one geometry (assistRouteIdx,
    /// leaderRouteIdx) when investigating coordination issues.
    ///
    /// <para>Fix BF (log-91, log-92): added as part of the same cleanup that
    /// relaxed the assist's RouteWalk gate to include Status==Waiting. Not
    /// load-bearing for the fix itself, but closes the same "position
    /// published, route-context not published" asymmetry that drove the
    /// off-route-paused-leader bug — applied symmetrically to both
    /// directions of party state.</para>
    /// </summary>
    public int AssistRouteIndex { get; set; } = -1;

    // ------------------------------------------------------------------
    // Fix GA (run-167) — backtrack-mode coordination fields.
    // Mirror of LeaderState.IsBacktracking/etc. so each bot can read
    // the partner's backtrack state via Fix GA's two-way publish.
    // See LeaderState.cs Fix GA block for full semantics.
    // ------------------------------------------------------------------

    /// <summary>True when the assist is in active BL-backtrack
    /// (Fix GC coordinated backtrack, mirroring leader's).</summary>
    public bool IsBacktracking { get; set; }

    /// <summary>Assist's local aggressor guid for backtrack, or 0
    /// when the assist's backtrack is purely coordinated (mirroring
    /// leader's without an assist-side aggressor). When non-zero,
    /// this is the guid the leader's Fix FZ uses to gate Fix L.</summary>
    public int BacktrackAggressorGuid { get; set; }

    /// <summary>Assist's local recheck cache result for its
    /// aggressor. True = IN_RECT at last evaluation.</summary>
    public bool BacktrackAggressorInRect { get; set; }

    /// <summary>Assist's current Navigation.IsInBlacklistArea().</summary>
    public bool InsideBlacklistArea { get; set; }

    /// <summary>Route waypoint index assist is retreating to.</summary>
    public int BacktrackCurrentWaypointIdx { get; set; } = -1;

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
