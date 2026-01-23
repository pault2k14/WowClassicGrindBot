using Core.GOAP;
using Microsoft.Extensions.Logging;

namespace Core.Goals;

public sealed class TargetFocusInCombatTargetGoal : GoapGoal
{
    public override float Cost => 1f;

    private readonly ILogger<TargetFocusInCombatTargetGoal> logger;
    private readonly ConfigurableInput input;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly Wait wait;
    private readonly ChatReader chatReader;

    public TargetFocusInCombatTargetGoal(ConfigurableInput input, PlayerReader playerReader,
        AddonBits bits, ClassConfiguration classConfig, Wait wait, 
        ILogger<TargetFocusInCombatTargetGoal> logger, ChatReader chatReader)
        : base(nameof(TargetFocusInCombatTargetGoal))
    {
        this.input = input;
        this.playerReader = playerReader;
        this.bits = bits;
        this.wait = wait;
        this.chatReader = chatReader;

        /* This was preventing AssistFocus mode from returning to combat 
         *  when combat is temporarily left for other plans. Seen when 2 or 3
            mobs attack at the same time.
            [GoapAgent        ] New Plan= NO PLAN
            appears in the log */
        /*
        if (classConfig.Loot)
        {
            AddPrecondition(GoapKey.incombat, false);
        }
        */

        AddPrecondition(GoapKey.forcedfollow, false);
        AddPrecondition(GoapKey.hasfocus, true);
        AddPrecondition(GoapKey.hastarget, false);
        AddPrecondition(GoapKey.focushastarget, true);
        AddPrecondition(GoapKey.focuscombat, true);
        AddPrecondition(GoapKey.incombat, false);
        this.logger = logger;
    }

    public override bool CanRun()
    {
        if (bits.TargetTarget_PlayerOrPet())
            return false;

        return
            (bits.FocusTarget_Hostile() && bits.FocusTarget_Combat()) ||
            !bits.FocusTarget_Hostile();
    }

    public override void OnEnter()
    {
        input.PressTargetFocus();
        wait.Update();
    }

    public override void Update()
    {
        if (chatReader.ForcedFollow)
        {
            AddEffect(GoapKey.forcedfollow, true);
            return;
        }

        if (bits.FocusTarget_Hostile())
        {
            if (bits.FocusTarget_Combat())
            {
                input.PressTargetFocus();
                input.PressTargetOfTarget();
            }
        }
        else if (playerReader.SpellInRange.FocusTarget_Trade)
        {
            logger.LogInformation("TargetFocusInCombatTargetGoal: FocusTarget Not Hostile, Pressing Interact");
            input.PressTargetFocus();
            input.PressTargetOfTarget();
            input.PressInteract();
        }

        wait.Update();
    }

    public override void OnExit()
    {
        if (!bits.FocusTarget())
        {
            input.PressClearTarget();
            wait.Update();
        }
    }
}
