using System;
using System.Numerics;

namespace Core.Party;

/// <summary>
/// Leader-side singleton. Builds a <see cref="LeaderState"/> snapshot on demand
/// from the live <see cref="PlayerReader"/> and <see cref="AddonBits"/> values.
/// <para>
/// No background update loop is required: <see cref="PlayerReader"/> is always
/// current because <see cref="AddonReader.Update"/> runs in the addon thread at
/// ~250 Hz. The controller calls <see cref="GetCurrentState"/> on each HTTP GET,
/// so the snapshot is always at most one addon-frame old.
/// </para>
/// </summary>
public sealed class LeaderStateService
{
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly IBotController botController;
    private readonly LeaderNavigationProvider leaderNavProvider;

    // Cache the last valid world position. When the addon briefly returns (0,0,0)
    // (e.g. during loading screens or bot deactivation), we return the cached position
    // rather than sending the assist a bogus ~4400y distance (sqrt(700²+4350²) ≈ 4406y).
    private Vector3 _lastValidWorldPos;

    public LeaderStateService(
        PlayerReader playerReader,
        AddonBits bits,
        IBotController botController,
        LeaderNavigationProvider leaderNavProvider)
    {
        this.playerReader = playerReader;
        this.bits = bits;
        this.botController = botController;
        this.leaderNavProvider = leaderNavProvider;
    }

    public LeaderState GetCurrentState()
    {
        Vector3 world = playerReader.WorldPos;

        // Validate — (0,0,0) means the addon has not yet provided real data
        // or has momentarily lost its feed (loading screen, logout transition).
        if (world.X == 0 && world.Y == 0)
        {
            world = _lastValidWorldPos; // fall back to last known good position
        }
        else
        {
            _lastValidWorldPos = world;
        }

        Vector3 map = playerReader.MapPos;

        return new LeaderState
        {
            WorldX = world.X,
            WorldY = world.Y,
            WorldZ = world.Z,
            MapX = map.X,
            MapY = map.Y,
            UIMapId = playerReader.UIMapId.Value,
            Status = DetermineStatus(),
            HealthPercent = playerReader.HealthPercent(),
            InCombat = bits.Combat(),
            TargetGuid = playerReader.TargetGuid,
            Timestamp = DateTime.UtcNow,

            // Waypoint sharing — only meaningful when leader is Patrolling.
            HasTargetWaypoint = leaderNavProvider.HasTargetWaypoint,
            TargetWaypointWorldX = leaderNavProvider.TargetWaypointWorldX,
            TargetWaypointWorldY = leaderNavProvider.TargetWaypointWorldY,

            // Approach-start anchor — only meaningful when leader is Approaching.
            HasApproachStart = leaderNavProvider.HasApproachStart,
            ApproachStartWorldX = leaderNavProvider.ApproachStartWorldX,
            ApproachStartWorldY = leaderNavProvider.ApproachStartWorldY,

            // Mob blacklist — cumulative for the session.
            BlacklistedMobGuids = leaderNavProvider.BlacklistedMobGuidsSnapshot,
        };
    }

    private BotStatus DetermineStatus()
    {
        if (bits.Dead())
            return BotStatus.Dead;

        if (bits.Combat())
            return BotStatus.Combat;

        if (!botController.IsBotActive)
            return BotStatus.Waiting;

        // Fix T (log-63 19:47:02 → 19:48:21): when FRG is actively paused waiting
        // for the assist (too far / Stuck / CantFollow), the bot is functionally
        // stationary even though its current goal is still FollowRouteGoal by
        // name. The previous goal-name → BotStatus mapping below reported
        // Patrolling in this state, which kept the assist's FFG in waypoint-
        // sharing mode targeting the leader's last-published waypoint. If that
        // waypoint had become unreachable (e.g., the leader just inserted a
        // detour around a blacklist rect — see Fix S), the assist's pathfinder
        // failed on every retry and the bots stayed separated until something
        // external (here, the bot stopping at 19:48:21:068) flipped status off
        // Patrolling. Reporting Waiting during pause-for-assist short-circuits
        // FFG's waypoint-sharing branch (which gates on
        // `leader.Status == Patrolling`) and routes the assist to the leader's
        // actual body via position-chase, which is the correct target while
        // the leader is stationary.
        if (leaderNavProvider.IsPausedForAssist)
            return BotStatus.Waiting;

        // GoapAgent.CurrentGoal is GoapGoal? — use .Name to derive status.
        string? goalName = botController.GoapAgent?.CurrentGoal?.Name;
        return goalName switch
        {
            "LootGoal"              => BotStatus.Looting,
            "SkinningGoal"          => BotStatus.Skinning,
            "DrinkGoal"
                or "EatGoal"
                or "RestGoal"       => BotStatus.Resting,
            "EvadeGoal"             => BotStatus.Evading,
            "FollowRouteGoal"       => BotStatus.Patrolling,
            "ApproachTargetGoal"
                or "PullTargetGoal" => BotStatus.Approaching,
            _                       => BotStatus.Patrolling
        };
    }
}
