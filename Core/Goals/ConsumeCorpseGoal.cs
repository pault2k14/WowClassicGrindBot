using Core.GOAP;

using Microsoft.Extensions.Logging;
using System.Numerics;
using System.Threading;

namespace Core.Goals;

public sealed partial class ConsumeCorpseGoal : GoapGoal, IGoapEventListener
{
    public override float Cost => 4.1f;

    private readonly ILogger<ConsumeCorpseGoal> logger;
    private readonly ClassConfiguration classConfig;
    private readonly GoapAgentState state;
    private readonly ChatReader chatReader;
    private readonly AddonBits bits;

    public ConsumeCorpseGoal(ILogger<ConsumeCorpseGoal> logger,
        ClassConfiguration classConfig, GoapAgentState state,
        ChatReader chatReader, AddonBits bits)
        : base(nameof(ConsumeCorpseGoal))
    {
        this.logger = logger;
        this.classConfig = classConfig;
        this.state = state;
        this.chatReader = chatReader;
        this.bits = bits;

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
        AddPrecondition(GoapKey.damagedone, false);
        AddPrecondition(GoapKey.damagetaken, false);

        AddPrecondition(GoapKey.producedcorpse, true);
        AddPrecondition(GoapKey.consumecorpse, false);
        AddPrecondition(GoapKey.assistrequestreturn, false);
        AddPrecondition(GoapKey.focuscombat, false);
        AddPrecondition(GoapKey.pethastarget, false);


        AddEffect(GoapKey.producedcorpse, false);
        
        if (classConfig.Loot)
        {
            AddEffect(GoapKey.consumecorpse, true);
            AddEffect(GoapKey.shouldloot, true);

            if (classConfig.GatherCorpse)
            {
                AddEffect(GoapKey.shouldgather, true);
            }
        }
    }

    public override void OnEnter()
    {
        if (chatReader.ForcedFollow)
        {
            AddEffect(GoapKey.forcedfollow, true);
            return;
        }

        LogConsume(logger);
        SendGoapEvent(new GoapStateEvent(GoapKey.consumecorpse, true));

        if (classConfig.Loot)
        {
            state.LootableCorpseCount++;
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
                        logger.LogInformation("ConsumeCorpseGoal: OnGoapEvent - AssistRequestReturn to X: "
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
        EventId = 0100,
        Level = LogLevel.Information,
        Message = "Safe to consume a corpse.")]
    static partial void LogConsume(ILogger logger);
}
