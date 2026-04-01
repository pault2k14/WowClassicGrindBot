// Core/GOAP/EvadeBlacklistEvent.cs
// Add this file alongside AbortEvent.cs, ResumeEvent.cs, CorpseEvent.cs etc.
//
// Fired by CombatGoal or ApproachTargetGoal whenever the leader calls
// playerReader.IgnoreTarget() on an evading mob.
// GoapAgent.HandleGoapEvent() catches this and broadcasts a party chat
// message so the assist also ignores the target and clears.

namespace Core.GOAP;

public sealed class EvadeBlacklistEvent : GoapEventArgs
{
    /// <summary>The unit GUID that was ignored/blacklisted due to evade.</summary>
    public int TargetGuid { get; }

    public EvadeBlacklistEvent(int targetGuid)
    {
        TargetGuid = targetGuid;
    }
}
