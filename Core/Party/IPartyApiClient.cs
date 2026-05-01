using System.Threading;
using System.Threading.Tasks;

namespace Core.Party;

/// <summary>
/// Assist-side abstraction over the leader's HTTP API.
/// Defined in Core so <see cref="Goals.FollowFocusGoal"/> (and other goals)
/// can depend on it without referencing BlazorServer or ASP.NET Core.
/// </summary>
public interface IPartyApiClient
{
    /// <summary>
    /// Fetches the leader's current state snapshot.
    /// Returns <c>null</c> if the request fails (network error, timeout, leader offline).
    /// Never throws — all exceptions are swallowed and logged at Debug level.
    /// </summary>
    Task<LeaderState?> GetLeaderStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Posts this assist's current state to the leader.
    /// Fire-and-forget safe: the caller can discard the returned <see cref="Task"/>
    /// or await it — exceptions are always swallowed and logged at Debug level.
    /// </summary>
    Task PostAssistStateAsync(AssistState state, CancellationToken ct = default);
}
