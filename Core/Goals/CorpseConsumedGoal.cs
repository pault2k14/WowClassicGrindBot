using Core.GOAP;

using Microsoft.Extensions.Logging;

using System;

namespace Core.Goals;

public sealed partial class CorpseConsumedGoal : GoapGoal, IGoapEventListener
{
    public override float Cost => 4.7f;

    private readonly ILogger<CorpseConsumedGoal> logger;
    private readonly GoapAgentState goapAgentState;
    private readonly Wait wait;
    private readonly RestHandler restHandler;
    private readonly ChatReader chatReader;
    private readonly ClassConfiguration classConfig;
    private readonly AddonBits bits;

    private readonly bool lootEnabled;

    public CorpseConsumedGoal(ILogger<CorpseConsumedGoal> logger,
        ClassConfiguration classConfig, GoapAgentState goapAgentState, 
        Wait wait, RestHandler restHandler, ChatReader chatReader, AddonBits bits)
        : base(nameof(CorpseConsumedGoal))
    {
        this.logger = logger;
        this.goapAgentState = goapAgentState;
        this.wait = wait;
        this.chatReader = chatReader;
        this.classConfig = classConfig;
        this.bits = bits;

        this.lootEnabled = classConfig.Loot;

        if (classConfig.Mode == Mode.AssistFocus)
        {
            AddPrecondition(GoapKey.partymembercombat, false);
        }
        else if (classConfig.Mode == Mode.PartyLeader)
        {
            AddPrecondition(GoapKey.partyleadercombat, false);
        }

        if (classConfig.KeyboardOnly)
        {
            AddPrecondition(GoapKey.consumablecorpsenearby, true);
        }
        AddPrecondition(GoapKey.forcedfollow, false);
        AddPrecondition(GoapKey.pulled, false);
        AddPrecondition(GoapKey.dangercombat, false);
        AddPrecondition(GoapKey.incombat, false);
        AddPrecondition(GoapKey.assistrequestreturn, false);
        //AddPrecondition(GoapKey.focuscombat, false);
        AddPrecondition(GoapKey.pethastarget, false);

        AddPrecondition(GoapKey.consumecorpse, true);

        AddEffect(GoapKey.consumecorpse, false);
        this.restHandler = restHandler;
    }

    public override void OnEnter()
    {
        if (chatReader.ForcedFollow)
        {
            AddEffect(GoapKey.forcedfollow, true);
            return;
        }

        if (classConfig.Mode == Mode.PartyLeader && (bits.Combat() || bits.Focus_Combat()))
        {
            logger.LogInformation("CorpseConsumedGoal: In Combat aborting skinning!");
            AddEffect(GoapKey.producedcorpse, false);
            AddEffect(GoapKey.consumecorpse, false);
            AddEffect(GoapKey.shouldloot, false);
            AddEffect(GoapKey.shouldgather, false);
            AddEffect(GoapKey.consumablecorpsenearby, false);
            return;
        }

        while (restHandler.IsResting())
        {
            wait.Update(1000);
        }

        goapAgentState.ConsumableCorpseCount = Math.Max(goapAgentState.ConsumableCorpseCount - 1, 0);
        if (goapAgentState.ConsumableCorpseCount == 0)
        {
            goapAgentState.LastCombatKillCount = 0;
            goapAgentState.RecentlyLooted.Clear();
        }

        LogConsumed(logger, goapAgentState.LastCombatKillCount, goapAgentState.ConsumableCorpseCount);

        SendGoapEvent(new GoapStateEvent(GoapKey.consumecorpse, false));

        if (goapAgentState.LastCombatKillCount > 1)
        {
            wait.Fixed(Loot.LOOTFRAME_AUTOLOOT_DELAY_MS);
            wait.Update();
        }

        if (!lootEnabled)
        {
            SendGoapEvent(new RemoveClosestPoi(CorpseEvent.NAME));
            wait.Fixed(Loot.LOOTFRAME_AUTOLOOT_DELAY_MS / 2);
        }
    }

    public void OnGoapEvent(GoapEventArgs e)
    {
        if (e is GoapStateEvent g)
        {
            switch (g.Key)
            {
                case GoapKey.assistrequestreturn:
                    if (classConfig.Mode == Mode.PartyLeader && chatReader.AssistRequestReturn)
                    {
                        logger.LogInformation("CorpseConsumedGoal: OnGoapEvent - AssistRequestReturn to X: "
                            + chatReader.AssistXPos
                            + " Y: "
                            + chatReader.AssistYPos);

                        AddEffect(GoapKey.producedcorpse, false);
                        AddEffect(GoapKey.consumecorpse, false);
                        AddEffect(GoapKey.shouldloot, false);
                        AddEffect(GoapKey.shouldgather, false);
                        AddEffect(GoapKey.consumablecorpsenearby, false);
                    }

                    break;
            }
        }
    }


    [LoggerMessage(
        EventId = 0120,
        Level = LogLevel.Information,
        Message = "Total: {total} | Remaining: {remains}")]
    static partial void LogConsumed(ILogger logger, int total, int remains);
}
