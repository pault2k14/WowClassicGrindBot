using Core.GOAP;
using Microsoft.Extensions.Logging;

namespace Core.Goals;

public sealed class FollowFocusGoal : GoapGoal
{
    public override float Cost => 19f;

    private readonly ConfigurableInput input;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly Wait wait;
    private readonly ClassConfiguration classConfig;
    private readonly ILogger<FollowFocusGoal> logger;
    private readonly RestHandler restHandler;

    public FollowFocusGoal(ConfigurableInput input,
        PlayerReader playerReader,
        AddonBits bits,
        Wait wait,
        ClassConfiguration classConfig,
        ILogger<FollowFocusGoal> logger,
        RestHandler restHandler
        )
        : base(nameof(FollowFocusGoal))
    {
        this.input = input;
        this.playerReader = playerReader;
        this.bits = bits;
        this.wait = wait;
        this.classConfig = classConfig;
        this.logger = logger;
        this.restHandler = restHandler;

        if (classConfig.UnitToFollow == "focus")
        {
            AddPrecondition(GoapKey.hasfocus, true);
        }
        
        AddPrecondition(GoapKey.dangercombat, false);
        AddPrecondition(GoapKey.damagedone, false);
        AddPrecondition(GoapKey.damagetaken, false);
        AddPrecondition(GoapKey.producedcorpse, false);
        AddPrecondition(GoapKey.consumecorpse, false);
    }

    public override void OnEnter()
    {
        while (restHandler.IsResting())
        {
            wait.Update(1000);
        }

        if (input.IsKeyDown(input.ForwardKey))
        {
            input.StopForward(true);
        }
    }

    public override void OnExit()
    {
        if (classConfig.UnitToFollow == "focus")
        {
            if (playerReader.TargetGuid == playerReader.FocusGuid)
            {
                input.PressClearTarget();
                wait.Update();
            }
        }
        else if (classConfig.UnitToFollow == "party1")
        {
            if (playerReader.TargetGuid == playerReader.PartyMember1Guid)
            {
                input.PressClearTarget();
                wait.Update();
            }
        }
        else if (classConfig.UnitToFollow == "party2")
        {
            if (playerReader.TargetGuid == playerReader.PartyMember2Guid)
            {
                input.PressClearTarget();
                wait.Update();
            }
        }
        else if (classConfig.UnitToFollow == "party3")
        {
            if (playerReader.TargetGuid == playerReader.PartyMember3Guid)
            {
                input.PressClearTarget();
                wait.Update();
            }
        }
        else if (classConfig.UnitToFollow == "party4")
        {
            if (playerReader.TargetGuid == playerReader.PartyMember4Guid)
            {
                input.PressClearTarget();
                wait.Update();
            }
        }

    }

    public override void Update()
    {
        if (classConfig.UnitToFollow == "focus")
        {
            if (playerReader.TargetGuid != playerReader.FocusGuid)
            {
                input.PressTargetFocus();
                wait.Update();
            }

            if (playerReader.TargetGuid == playerReader.FocusGuid &&
                playerReader.SpellInRange.Focus_Inspect &&
                !bits.AutoFollow() &&
                !input.FollowTarget.OnCooldown())
            {
                input.PressFollowTarget();
            }
        }
        else if (classConfig.UnitToFollow == "party1")
        {
            if (playerReader.TargetGuid != playerReader.PartyMember1Guid)
            {
                input.PressTargetFocus();
                wait.Update();
            }

            if (playerReader.TargetGuid == playerReader.PartyMember1Guid &&
                playerReader.SpellInRange.PartyMember1_Inspect &&
                !bits.AutoFollow() &&
                !input.FollowTarget.OnCooldown())
            {
                input.PressFollowTarget();
            }
        }
        else if (classConfig.UnitToFollow == "party2")
        {
            if (playerReader.TargetGuid != playerReader.PartyMember2Guid)
            {
                input.PressTargetFocusPartyMemberTwo();
                wait.Update();
            }

            if (playerReader.TargetGuid == playerReader.PartyMember2Guid &&
                playerReader.SpellInRange.PartyMember2_Inspect &&
                !bits.AutoFollow() &&
                !input.FollowTarget.OnCooldown())
            {
                input.PressFollowTarget();
            }
        }
        else if (classConfig.UnitToFollow == "party3")
        {
            if (playerReader.TargetGuid != playerReader.PartyMember3Guid)
            {
                input.PressTargetFocusPartyMemberThree();
                wait.Update();
            }

            if (playerReader.TargetGuid == playerReader.PartyMember3Guid &&
                playerReader.SpellInRange.PartyMember3_Inspect &&
                !bits.AutoFollow() &&
                !input.FollowTarget.OnCooldown())
            {
                input.PressFollowTarget();
            }
        }
        else if (classConfig.UnitToFollow == "party4")
        {
            if (playerReader.TargetGuid != playerReader.PartyMember4Guid)
            {
                input.PressTargetFocusPartyMemberFour();
                wait.Update();
            }

            if (playerReader.TargetGuid == playerReader.PartyMember4Guid &&
                playerReader.SpellInRange.PartyMember4_Inspect &&
                !bits.AutoFollow() &&
                !input.FollowTarget.OnCooldown())
            {
                input.PressFollowTarget();
            }
        }

        wait.Update();
    }
}
