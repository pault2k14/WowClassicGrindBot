using Core.GOAP;

using Microsoft.Extensions.Logging;

namespace Core.Goals;

public sealed class PartyMember2Goal : GoapGoal
{
    private readonly ILogger<PartyMember2Goal> logger;
    private readonly ConfigurableInput input;
    private readonly ClassConfiguration classConfig;
    private readonly Wait wait;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly StopMoving stopMoving;
    private readonly CastingHandler castingHandler;
    private readonly IMountHandler mountHandler;
    private readonly CombatLog combatLog;
    private readonly RestHandler restHandler;
    private readonly ChatReader chatReader;
    
    public PartyMember2Goal(ILogger<PartyMember2Goal> logger,
        ConfigurableInput input,
        ClassConfiguration classConfig,
        Wait wait,
        PlayerReader playerReader,
        AddonBits bits,
        StopMoving stopMoving,
        CastingHandler castingHandler,
        IMountHandler mountHandler,
        CombatLog combatLog,
        RestHandler restHandler,
        ChatReader chatReader
        )
        : base(nameof(PartyMember2Goal))
    {
        this.logger = logger;
        this.input = input;
        this.classConfig = classConfig;
        this.wait = wait;
        this.playerReader = playerReader;
        this.bits = bits;
        this.stopMoving = stopMoving;
        this.castingHandler = castingHandler;
        this.mountHandler = mountHandler;
        this.combatLog = combatLog;

        this.Keys = classConfig.PartyMember2.Sequence;
        this.restHandler = restHandler;
        this.chatReader = chatReader;

        AddPrecondition(GoapKey.party2connected, true);
        AddPrecondition(GoapKey.assistisfollowing, false);

        if (classConfig.Mode == Mode.AssistFocus)
        {
            AddPrecondition(GoapKey.partymembercombat, false);
            AddPrecondition(GoapKey.forcedfollow, false);
            AddPrecondition(GoapKey.evadeRecovery, false);
        }
        else if (classConfig.Mode == Mode.PartyLeader)
        {
            AddPrecondition(GoapKey.partyleadercombat, false);
            AddPrecondition(GoapKey.forcedfollow, false);
            AddPrecondition(GoapKey.evadeRecovery, false);
        }
        else
        {
            AddPrecondition(GoapKey.incombat, false);
            AddPrecondition(GoapKey.forcedfollow, false);
        }
    }

    public override float Cost => 3.9f;

    public override bool CanRun()
    {
        for (int i = 0; i < Keys.Length; i++)
        {
            KeyAction key = Keys[i];
            if (key.CanRun())
                return true;
        }

        return false;
    }

    public override void OnEnter()
    {
        wait.Update();
        input.PressTargetFocusPartyMemberTwo();
        wait.Update();

        while (restHandler.IsResting())
        {
            wait.Update(1000);
        }
    }

    public override void OnExit()
    {
        wait.Update();
        input.PressClearTarget();
        wait.Update();
    }

    public override void Update()
    {
        wait.Update();

        if (bits.Drowning())
        {
            input.PressJump();
            return;
        }

        for (int i = 0; bits.Target_Alive() && i < Keys.Length; i++)
        {
            if (chatReader.ForcedFollow)
            {
                AddEffect(GoapKey.forcedfollow, true);
                return;
            }

            KeyAction keyAction = Keys[i];

            if (castingHandler.SpellInQueue() && !keyAction.BaseAction)
            {
                continue;
            }

            if (keyAction.BeforeCastDismount && mountHandler.IsMounted())
            {
                mountHandler.Dismount();
            }

            if (castingHandler.CastIfReady(keyAction,
                keyAction.Interrupts.Count > 0
                ? keyAction.CanBeInterrupted
                : bits.Target_Alive))
            {
                break;
            }
        }
    }

}
