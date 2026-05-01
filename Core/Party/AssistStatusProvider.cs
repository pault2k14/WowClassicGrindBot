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
}
