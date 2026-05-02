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
    /// Leader is actively pressing the interact/approach key to close on a mob
    /// (<see cref="Goals.ApproachTargetGoal"/> or <see cref="Goals.PullTargetGoal"/>
    /// is the current goal). The assist uses this to switch from waypoint-sharing
    /// to the approach-start anchor rather than chasing the leader's moving body.
    /// </summary>
    Approaching,
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
