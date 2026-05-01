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

    public LeaderStateService(
        PlayerReader playerReader,
        AddonBits bits,
        IBotController botController)
    {
        this.playerReader = playerReader;
        this.bits = bits;
        this.botController = botController;
    }

    public LeaderState GetCurrentState()
    {
        Vector3 world = playerReader.WorldPos;
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
            Timestamp = DateTime.UtcNow
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
            _                       => BotStatus.Patrolling
        };
    }
}
