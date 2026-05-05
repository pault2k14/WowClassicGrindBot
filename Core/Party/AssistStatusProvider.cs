namespace Core.Party;

/// <summary>
/// Singleton bridge between <see cref="Goals.FollowFocusGoal"/> (which knows the
/// assist's true navigation state) and <see cref="PartyStatePublisher"/> (which
/// builds the POST payload on the addon thread without access to goal internals).
/// <para>
/// <see cref="Goals.FollowFocusGoal.Update"/> writes <see cref="CurrentStatus"/>
/// every tick. <see cref="PartyStatePublisher.BuildSnapshot"/> reads it.
/// Both run on the GOAP / addon thread family so no lock is needed.
/// </para>
/// </summary>
public sealed class AssistStatusProvider
{
    /// <summary>
    /// The assist's current navigation/combat status as understood by
    /// <see cref="Goals.FollowFocusGoal"/>. Defaults to
    /// <see cref="BotStatus.Patrolling"/> until FFG first runs.
    /// </summary>
    public BotStatus CurrentStatus { get; set; } = BotStatus.Patrolling;

    /// <summary>
    /// True when the assist cannot follow the leader and needs the leader to
    /// come back — set by FollowFocusGoal (navigation exhausted, path failed)
    /// and by combat goals on evade (ATG, CombatGoal, PullTargetGoal) to keep
    /// FollowFocusGoal selectable via the <c>assistshouldfollow</c> world-state
    /// override even when combat conditions would normally block it.
    /// Cleared by FollowFocusGoal when the assist re-enters Following range.
    /// Replaces <c>chatReader.AssistRequestReturn</c> which was never designed
    /// to hold mutable coordination state.
    /// </summary>
    public bool CantFollow { get; set; }

    /// <summary>
    /// Mirror of <c>GoapKey.evadeRecovery</c> — set by <see cref="GOAP.GoapAgent"/>
    /// when the evade-recovery world-state is broadcast, cleared when it elapses.
    /// Read by <see cref="PartyStatePublisher.BuildSnapshot"/> to decide whether
    /// to suppress the Following → Combat override during the 25 s window.
    ///
    /// <para>The override exists to prevent the leader from advancing while
    /// the assist is mid-fight in normal grind operation: if the assist's
    /// status is Following but it has just taken a hit (passing mob), the
    /// snapshot reports Combat so the leader's distance gate pauses until
    /// CombatGoal (cost 4) preempts FFG (cost 19) and OnExit sets status to
    /// Waiting — typically within one GOAP tick (~50 ms).</para>
    ///
    /// <para>During the evade-recovery window the override pathologically
    /// hides the assist's correct Following claim. CombatGoal is
    /// precondition-blocked by <c>evadeRecovery=false</c>, so it cannot
    /// preempt FFG and OnExit cannot fire to clear the Following status —
    /// FFG legitimately keeps publishing Following while bits.Combat() stays
    /// true (the blacklisted mob remains aggroed until the leader retreats
    /// far enough for it to leash). Without this flag the publisher can't
    /// tell the two cases apart.</para>
    /// </summary>
    public bool EvadeRecoveryActive { get; set; }
}
