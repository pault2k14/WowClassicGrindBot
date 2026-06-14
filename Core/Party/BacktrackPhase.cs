namespace Core.Party;

/// <summary>
/// Coordinated-backtrack phase published on LeaderState/AssistState.
/// V1: producer emits None/Navigating/Evaluating/EngageWindow; BlEscape/TurningToFace
/// are defined for D2 and not yet emitted. IsActivelyBacktracking() == (phase != None),
/// which is behavior-equivalent to the legacy bool IsBacktracking.
/// </summary>
public enum BacktrackPhase
{
    None,
    BlEscape,
    Navigating,
    TurningToFace,
    Evaluating,
    EngageWindow
}
