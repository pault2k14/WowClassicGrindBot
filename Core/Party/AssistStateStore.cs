using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using SharedLib.Extensions;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;

namespace Core.Party;

/// <summary>
/// Leader-side singleton. Stores the most recent <see cref="AssistState"/> for
/// each assist bot, keyed by <see cref="AssistState.AssistId"/>.
/// Updated by <c>POST /party/assist/state</c>. Read by <see cref="GOAP.GoapAgent"/>
/// and <see cref="Goals.FollowRouteGoal"/> in place of the old ChatReader flags.
/// </summary>
public sealed class AssistStateStore
{
    private readonly ILogger<AssistStateStore> logger;
    private readonly int staleThresholdMs;
    private readonly ConcurrentDictionary<string, AssistState> _states = new();
    private readonly ConcurrentDictionary<string, bool> _wasStale = new();

    public AssistStateStore(
        ILogger<AssistStateStore> logger,
        IOptions<PartyApiConfig> configOptions)
    {
        this.logger = logger;
        this.staleThresholdMs = configOptions.Value.StaleThresholdMs;
    }

    public void Update(AssistState state)
    {
        bool isNew = !_states.ContainsKey(state.AssistId);
        _states[state.AssistId] = state;

        if (isNew)
        {
            logger.LogInformation(
                $"[AssistStateStore] First contact from assist '{state.AssistId}' — " +
                $"status={state.Status} health={state.HealthPercent}% " +
                $"mapPos=({state.MapX:0.00},{state.MapY:0.00})");
        }

        if (_wasStale.TryGetValue(state.AssistId, out bool prev) && prev)
        {
            _wasStale[state.AssistId] = false;
            logger.LogInformation(
                $"[AssistStateStore] Assist '{state.AssistId}' state recovered — status={state.Status}");
        }
    }

    public bool IsStale(AssistState state) => state.AgeMs > staleThresholdMs;

    public bool HasSeenAnyAssist => !_states.IsEmpty;

    public bool AnyAssistStale()
    {
        bool anyStale = false;
        foreach (var (id, state) in _states)
        {
            if (IsStale(state))
            {
                anyStale = true;
                if (!_wasStale.GetOrAdd(id, false))
                {
                    _wasStale[id] = true;
                    logger.LogWarning(
                        $"[AssistStateStore] Assist '{id}' state is STALE " +
                        $"(age={state.AgeMs:0}ms > threshold={staleThresholdMs}ms). " +
                        "Leader will hold position until contact is restored.");
                }
            }
        }
        return anyStale;
    }

    public AssistState? Get(string assistId) =>
        _states.TryGetValue(assistId, out AssistState? s) && !IsStale(s) ? s : null;

    public IEnumerable<AssistState> GetAll() => _states.Values;

    public void Remove(string assistId)
    {
        _states.TryRemove(assistId, out _);
        _wasStale.TryRemove(assistId, out _);
    }

    public bool AnyAssistIsFollowing()
    {
        foreach (AssistState s in _states.Values)
            if (!IsStale(s) && s.Status == BotStatus.Following)
                return true;
        return false;
    }

    public bool AnyAssistCantFollow()
    {
        foreach (AssistState s in _states.Values)
            if (!IsStale(s) && (s.Status == BotStatus.CantFollow || s.CantFollow))
                return true;
        return false;
    }

    /// <summary>
    /// True if any non-stale assist is actively navigating toward the leader
    /// (<see cref="BotStatus.NavigatingToLeader"/>).
    /// The leader should continue patrolling while this is true — the assist is
    /// catching up and the distance gate already handles pausing if they fall too far.
    /// </summary>
    public bool AnyAssistNavigating()
    {
        foreach (AssistState s in _states.Values)
            if (!IsStale(s) && s.Status == BotStatus.NavigatingToLeader)
                return true;
        return false;
    }

    public bool AnyAssistStuck()
    {
        foreach (AssistState s in _states.Values)
            if (!IsStale(s) && s.Status == BotStatus.Stuck)
                return true;
        return false;
    }

    public AssistState? GetCantFollowState()
    {
        foreach (AssistState s in _states.Values)
            if (!IsStale(s) && s.Status == BotStatus.CantFollow)
                return s;
        return null;
    }

    public float GetNearestAssistDistanceYards(Vector3 leaderWorldPos)
    {
        float min = float.MaxValue;
        foreach (AssistState s in _states.Values)
        {
            if (IsStale(s)) continue;
            float d = leaderWorldPos.WorldDistanceXYTo(s.WorldPos);
            if (d < min) min = d;
        }
        return min;
    }

    public bool ShouldLeaderPauseForAssist(Vector3 leaderWorldPos, float pauseYards)
    {
        foreach (AssistState s in _states.Values)
        {
            if (IsStale(s)) continue;
            if (s.Status == BotStatus.Stuck || s.Status == BotStatus.CantFollow)
                return true;
            if (leaderWorldPos.WorldDistanceXYTo(s.WorldPos) > pauseYards)
                return true;
        }
        return false;
    }

    public bool ShouldLeaderResumePatrol(Vector3 leaderWorldPos, float resumeYards)
    {
        bool any = false;
        foreach (AssistState s in _states.Values)
        {
            if (IsStale(s)) continue;
            any = true;
            if (s.Status != BotStatus.Following)
                return false;
            if (leaderWorldPos.WorldDistanceXYTo(s.WorldPos) > resumeYards)
                return false;
        }
        return any;
    }
}
