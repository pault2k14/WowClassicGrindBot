using Core.GOAP;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic;
using System;


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
    private readonly ChatReader chatReader;
    private DateTime lastExecution = DateTime.MinValue;
    private TimeSpan gateInterval = TimeSpan.FromSeconds(30);

    public FollowFocusGoal(ConfigurableInput input,
        PlayerReader playerReader,
        AddonBits bits,
        Wait wait,
        ClassConfiguration classConfig,
        ILogger<FollowFocusGoal> logger,
        RestHandler restHandler,
        ChatReader chatReader
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
        this.chatReader = chatReader;

        if (classConfig.UnitToFollow == "focus")
        {
            AddPrecondition(GoapKey.hasfocus, true);
        }

        if (classConfig.Loot)
        {
            AddPrecondition(GoapKey.producedcorpse, false);
            AddPrecondition(GoapKey.consumecorpse, false);
        }

        AddPrecondition(GoapKey.forcedfollow, false);
        AddPrecondition(GoapKey.dangercombat, false);
        AddPrecondition(GoapKey.damagedone, false);
        AddPrecondition(GoapKey.damagetaken, false);
        // TODO Trying to fix NO GOAL issue, Drinking seems to temporarily become true?
        //AddPrecondition(GoapKey.eating, false);
        //AddPrecondition(GoapKey.drinking, false);
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

        input.StepBackwards();
        
        // Use Macro to say I'm not following in party chat
        input.PressAssistIsNotFollowing();
        wait.Update();

    }

    public override void Update()
    {
        while (restHandler.IsResting())
        {
            logger.LogInformation("FollowFocusGoal: I'm waiting while resting.");
            wait.Update(1000);
        }

        if (chatReader.ForcedFollow)
        {
            AddEffect(GoapKey.forcedfollow, true);
            return;
        }

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

                wait.Update();
                // Use Macro to send i'm following in party chat
                input.PressAssistIsFollowing();
                wait.Update();

                chatReader.AssistRequestReturn = false;
                wait.Update();
            }
            else if (!playerReader.SpellInRange.Focus_Inspect)
            {
                // I want to follow but the party member has gone too far
                // let's tell them and give them our coordinates to find us at
                // 1. Press Macro saying "i tried following but you are too far away my position:x,y"
                // 2. Party leader will recieve the chatReader event, parse the map coordinates
                // 3. Party leader will trigger a GoapEvent and BroadcastGoapEvent to trigger followRouteGoal to
                //    move to the indicated Map Pos
                if (DateTime.Now - lastExecution > gateInterval)
                {
                    lastExecution = DateTime.Now;
                    input.PressAssistCantFollow();
                }

                wait.Update();
                return;
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
                wait.Update();

                // Use Macro to send i'm following in party chat
                input.PressAssistIsFollowing();
                wait.Update();

                chatReader.AssistRequestReturn = false;
                wait.Update();

            } else if(!playerReader.SpellInRange.PartyMember1_Inspect) 
            {
                // I want to follow but the party member has gone too far
                // let's tell them and give them our coordinates to find us at
                // 1. Press Macro saying "i tried following but you are too far away my position:x,y"
                // 2. Party leader will recieve the chatReader event, parse the map coordinates
                // 3. Party leader will trigger a GoapEvent and BroadcastGoapEvent to trigger followRouteGoal to
                //    move to the indicated Map Pos
                if (DateTime.Now - lastExecution > gateInterval)
                {
                    lastExecution = DateTime.Now;
                    input.PressAssistCantFollow();
                }

                wait.Update();
                return;
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
                wait.Update();

                // Use Macro to send i'm following in party chat
                input.PressAssistIsFollowing();
                wait.Update();

                chatReader.AssistRequestReturn = false;
                wait.Update();
            }
            else if (!playerReader.SpellInRange.PartyMember2_Inspect)
            {
                // I want to follow but the party member has gone too far
                // let's tell them and give them our coordinates to find us at
                // 1. Press Macro saying "i tried following but you are too far away my position:x,y"
                // 2. Party leader will recieve the chatReader event, parse the map coordinates
                // 3. Party leader will trigger a GoapEvent and BroadcastGoapEvent to trigger followRouteGoal to
                //    move to the indicated Map Pos
                if (DateTime.Now - lastExecution > gateInterval)
                {
                    lastExecution = DateTime.Now;
                    input.PressAssistCantFollow();
                }

                wait.Update();
                return;
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
                wait.Update();

                // Use Macro to send i'm following in party chat
                input.PressAssistIsFollowing();
                wait.Update();

                chatReader.AssistRequestReturn = false;
                wait.Update();
            }
            else if (!playerReader.SpellInRange.PartyMember3_Inspect)
            {
                // I want to follow but the party member has gone too far
                // let's tell them and give them our coordinates to find us at
                // 1. Press Macro saying "i tried following but you are too far away my position:x,y"
                // 2. Party leader will recieve the chatReader event, parse the map coordinates
                // 3. Party leader will trigger a GoapEvent and BroadcastGoapEvent to trigger followRouteGoal to
                //    move to the indicated Map Pos
                if (DateTime.Now - lastExecution > gateInterval)
                {
                    lastExecution = DateTime.Now;
                    input.PressAssistCantFollow();
                }

                wait.Update();
                return;
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
                wait.Update();

                // Use Macro to send i'm following in party chat
                input.PressAssistIsFollowing();
                wait.Update();

                chatReader.AssistRequestReturn = false;
                wait.Update();
            }
            else if (!playerReader.SpellInRange.PartyMember4_Inspect)
            {
                // I want to follow but the party member has gone too far
                // let's tell them and give them our coordinates to find us at
                // 1. Press Macro saying "i tried following but you are too far away my position:x,y"
                // 2. Party leader will recieve the chatReader event, parse the map coordinates
                // 3. Party leader will trigger a GoapEvent and BroadcastGoapEvent to trigger followRouteGoal to
                //    move to the indicated Map Pos
                if (DateTime.Now - lastExecution > gateInterval)
                {
                    lastExecution = DateTime.Now;
                    input.PressAssistCantFollow();
                }

                wait.Update();
                return;
            }
        }

        wait.Update();
    }
}
