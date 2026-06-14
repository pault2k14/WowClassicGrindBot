namespace Core.Party;

public static class LeaderStateExtensions
{
    /// <summary>True when the leader is in any active backtrack phase
    /// (phase != None). Behavior-equivalent to the legacy bool IsBacktracking.</summary>
    public static bool IsActivelyBacktracking(this LeaderState state)
        => state.BacktrackPhase != BacktrackPhase.None;

    /// <summary>True when the assist is in any active backtrack phase
    /// (phase != None). Behavior-equivalent to the legacy bool IsBacktracking.</summary>
    public static bool IsActivelyBacktracking(this AssistState state)
        => state.BacktrackPhase != BacktrackPhase.None;
}
