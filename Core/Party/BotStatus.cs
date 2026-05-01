namespace Core.Party;

public enum BotStatus
{
    Patrolling,
    Combat,
    Looting,
    Skinning,
    Resting,
    Waiting,
    Evading,
    Dead,
    NavigatingToLeader,
    Following,
    /// <summary>
    /// Assist is actively running an escape or unstuck attempt.
    /// Leader holds position while this is set — the assist cannot
    /// guarantee forward progress until the escape completes.
    /// </summary>
    Stuck,
    /// <summary>
    /// Assist exhausted all escape attempts and cannot reach the leader.
    /// Leader must navigate to the assist's stored position.
    /// Assist holds completely still until the leader arrives within
    /// <see cref="FollowFocusGoal.LeaderArrivedYards"/>.
    /// </summary>
    CantFollow
}
