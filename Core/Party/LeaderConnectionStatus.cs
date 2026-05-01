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

    private volatile LeaderState? _lastLeaderState;
    private volatile bool _everConnected;
    private volatile bool _wasStale;
    private int _consecutiveFailures;

    // Use the assist's own local clock to measure age — immune to clock skew
    // between the leader and assist machines.
    private DateTime _lastSuccessfulUpdateUtc = DateTime.MinValue;

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
    /// Milliseconds since the assist last received a successful GET response,
    /// measured on the assist's own clock. This is immune to clock skew between
    /// the leader and assist machines. Use this for all staleness checks.
    /// </summary>
    public double LocalAgeMs => _lastSuccessfulUpdateUtc == DateTime.MinValue
        ? double.MaxValue
        : (DateTime.UtcNow - _lastSuccessfulUpdateUtc).TotalMilliseconds;

    /// <summary>
    /// True when at least one successful poll has completed AND the last
    /// response was received within <see cref="PartyApiConfig.StaleThresholdMs"/>
    /// on the assist's local clock (immune to cross-machine clock skew).
    /// </summary>
    public bool HasValidLeaderState => _everConnected && LocalAgeMs < staleThresholdMs;

    /// <summary>Called by <see cref="LeaderStatePoller"/> on a successful GET.</summary>
    public void UpdateLeaderState(LeaderState state)
    {
        bool wasStale = _wasStale;
        _lastLeaderState = state;
        _lastSuccessfulUpdateUtc = DateTime.UtcNow; // local clock — immune to skew
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
            double localAge = LocalAgeMs;
            logger.LogWarning(
                $"[LeaderConnection] Leader state is now STALE " +
                $"(localAge={localAge:0}ms > threshold={staleThresholdMs}ms). " +
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
