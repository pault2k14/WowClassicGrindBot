using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System;

namespace Core.Party;

/// <summary>
/// Assist-side singleton. Tracks whether the leader's API is reachable and
/// whether the last-received <see cref="LeaderState"/> is still fresh enough
/// to act on.
/// <para>
/// Written by <see cref="LeaderStatePoller"/> (on a thread-pool task) and
/// read by <see cref="Core.GOAP.GoapAgent"/> (on the GOAP thread) to gate
/// <see cref="Core.GOAP.GoapKey.assistshouldfollow"/> — the assist will not
/// enter <see cref="Goals.FollowFocusGoal"/> unless
/// <see cref="HasValidLeaderState"/> is true.
/// </para>
/// </summary>
public sealed class LeaderConnectionStatus
{
    private readonly ILogger<LeaderConnectionStatus> logger;
    private readonly int staleThresholdMs;

    // volatile so reads from the GOAP thread always see the latest reference
    // written by the poll task without needing a lock.
    private volatile LeaderState? _lastLeaderState;
    private volatile bool _everConnected;
    private volatile bool _wasStale;
    private int _consecutiveFailures;

    public LeaderConnectionStatus(
        ILogger<LeaderConnectionStatus> logger,
        IOptions<PartyApiConfig> configOptions)
    {
        this.logger = logger;
        this.staleThresholdMs = configOptions.Value.StaleThresholdMs;
    }

    /// <summary>Configured stale threshold in milliseconds. Exposed so
    /// <see cref="Goals.FollowFocusGoal"/> can inline the stale check.</summary>
    public int StaleThresholdMs => staleThresholdMs;

    /// <summary>
    /// The most recent leader state snapshot, or <c>null</c> if no successful
    /// poll has completed yet.
    /// </summary>
    public LeaderState? LastLeaderState => _lastLeaderState;

    /// <summary>
    /// True when at least one successful poll has completed AND the last
    /// snapshot is not older than <see cref="PartyApiConfig.StaleThresholdMs"/>.
    /// This is the gate for <see cref="Core.GOAP.GoapKey.assistshouldfollow"/>.
    /// </summary>
    public bool HasValidLeaderState
    {
        get
        {
            LeaderState? state = _lastLeaderState;
            return state != null && state.AgeMs < staleThresholdMs;
        }
    }

    /// <summary>Called by <see cref="LeaderStatePoller"/> on a successful GET.</summary>
    public void UpdateLeaderState(LeaderState state)
    {
        bool wasStale = _wasStale;
        _lastLeaderState = state;
        _consecutiveFailures = 0;

        if (!_everConnected)
        {
            _everConnected = true;
            logger.LogInformation(
                $"[LeaderConnection] First successful contact with leader API. " +
                $"Leader status={state.Status} health={state.HealthPercent}% " +
                $"mapPos=({state.MapX:0.00},{state.MapY:0.00})");
        }
        else if (wasStale)
        {
            _wasStale = false;
            double ageMs = state.AgeMs;
            logger.LogInformation(
                $"[LeaderConnection] Leader API recovered after stale period. " +
                $"Age={ageMs:0}ms status={state.Status}");
        }
    }

    /// <summary>Called by <see cref="LeaderStatePoller"/> on a failed GET.</summary>
    public void RecordPollFailure(Exception? ex = null)
    {
        _consecutiveFailures++;

        bool nowStale = _everConnected && !HasValidLeaderState;

        if (nowStale && !_wasStale)
        {
            _wasStale = true;
            double ageMs = _lastLeaderState?.AgeMs ?? double.MaxValue;
            logger.LogWarning(
                $"[LeaderConnection] Leader state is now STALE " +
                $"(age={ageMs:0}ms > threshold={staleThresholdMs}ms). " +
                $"Consecutive failures={_consecutiveFailures}. " +
                $"FollowFocusGoal will be blocked until contact is restored. " +
                (ex != null ? $"Error: {ex.GetType().Name}: {ex.Message}" : "No response."));
        }
        else if (_consecutiveFailures % 10 == 0)
        {
            // Periodic reminder during extended outage.
            logger.LogWarning(
                $"[LeaderConnection] Leader API still unreachable " +
                $"(consecutive failures={_consecutiveFailures}). " +
                (ex != null ? $"Error: {ex.GetType().Name}: {ex.Message}" : string.Empty));
        }
    }
}
