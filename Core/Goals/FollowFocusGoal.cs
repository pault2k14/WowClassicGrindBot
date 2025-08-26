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

    public FollowFocusGoal(ConfigurableInput input,
        PlayerReader playerReader,
        AddonBits bits,
        Wait wait,
        ClassConfiguration classConfig,
        ILogger<FollowFocusGoal> logger
        )
        : base(nameof(FollowFocusGoal))
    {
        this.input = input;
        this.playerReader = playerReader;
        this.bits = bits;
        this.wait = wait;
        this.classConfig = classConfig;
        this.logger = logger;

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
        logger.LogWarning("playerReader.SpellInRange.PartyMember1_Inspect: " + playerReader.SpellInRange.PartyMember1_Inspect);
        logger.LogWarning("playerReader.SpellInRange.PartyMember1_Trade: " + playerReader.SpellInRange.PartyMember1_Trade);
        logger.LogWarning("playerReader.SpellInRange.PartyMember1_Duel: " + playerReader.SpellInRange.PartyMember1_Duel);

        logger.LogWarning("playerReader.SpellInRange.PartyMember2_Inspect: " + playerReader.SpellInRange.PartyMember2_Inspect);
        logger.LogWarning("playerReader.SpellInRange.PartyMember2_Trade: " + playerReader.SpellInRange.PartyMember2_Trade);
        logger.LogWarning("playerReader.SpellInRange.PartyMember2_Duel: " + playerReader.SpellInRange.PartyMember2_Duel);

        logger.LogWarning("playerReader.SpellInRange.PartyMember3_Inspect: " + playerReader.SpellInRange.PartyMember3_Inspect);
        logger.LogWarning("playerReader.SpellInRange.PartyMember3_Trade: " + playerReader.SpellInRange.PartyMember3_Trade);
        logger.LogWarning("playerReader.SpellInRange.PartyMember3_Duel: " + playerReader.SpellInRange.PartyMember3_Duel);

        logger.LogWarning("playerReader.SpellInRange.PartyMember4_Inspect: " + playerReader.SpellInRange.PartyMember4_Inspect);
        logger.LogWarning("playerReader.SpellInRange.PartyMember4_Trade: " + playerReader.SpellInRange.PartyMember4_Trade);
        logger.LogWarning("playerReader.SpellInRange.PartyMember4_Duel: " + playerReader.SpellInRange.PartyMember4_Duel);

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
