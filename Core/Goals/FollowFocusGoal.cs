using Core.GOAP;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic;
using System;
using System.Threading;


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
    private int focusTargetGuid;
    private Action<CancellationToken> FocusTargetInput;
    private followMessage lastMessageSent = followMessage.None;

    private enum followMessage
    {
        None,
        ImFollowing,
        ImNotFollowing,
        ICantFollow
    }

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

        /* TODO checking loot state currently not supported
         * as it will require multiple additional states to differentiate
         * what to do when assistreturnrequest is true, currently causes NO PLAN
        if (classConfig.Loot)
        {
            AddPrecondition(GoapKey.producedcorpse, false);
            AddPrecondition(GoapKey.consumecorpse, false);
        }
        */

        AddPrecondition(GoapKey.assistshouldfollow, true);
        // TODO Trying to fix NO GOAL issue, Drinking seems to temporarily become true?
        //AddPrecondition(GoapKey.eating, false);
        //AddPrecondition(GoapKey.drinking, false);

        switch(classConfig.UnitToFollow)
        {
            case "focus":
                focusTargetGuid = playerReader.FocusGuid;
                FocusTargetInput = input.PressTargetFocus;
                break;
            case "party1":
                focusTargetGuid = playerReader.PartyMember1Guid;
                FocusTargetInput = input.PressTargetFocus;
                break;
            case "party2":
                focusTargetGuid = playerReader.PartyMember2Guid;
                FocusTargetInput = input.PressTargetFocusPartyMemberTwo;
                break;
            case "party3":
                focusTargetGuid = playerReader.PartyMember3Guid;
                FocusTargetInput = input.PressTargetFocusPartyMemberThree;
                break;
            case "party4":
                focusTargetGuid = playerReader.PartyMember4Guid;
                FocusTargetInput = input.PressTargetFocusPartyMemberFour;
                break;
            default:
                focusTargetGuid = playerReader.FocusGuid;
                FocusTargetInput = input.PressTargetFocus;
                break;
        }
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
        if (playerReader.TargetGuid == focusTargetGuid)
        {
            input.PressClearTarget();
            wait.Update();
        }

        input.StepBackwards();
        
        // Use Macro to say I'm not following in party chat
        input.PressAssistIsNotFollowing();
        lastMessageSent = followMessage.ImNotFollowing;
        wait.Update();

    }

    public override void Update()
    {
        // Situation where we may have gotten stuck following focus
        // and now focus is in combat. Let's try to acquire their target
        // and approach it.
        if(bits.Focus_Combat() && bits.FocusTarget())
        {
            wait.Update();
            input.PressTargetFocus();
            input.PressTargetOfTarget();
            wait.Update();
            input.PressInteract();
            wait.Update();
            return;
        }

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

        if(bits.AutoFollow() && (lastMessageSent == followMessage.ImFollowing))
        {
            // We are already following we don't need to send another message
            return;
        }
        else if(bits.AutoFollow() && (lastMessageSent != followMessage.ImFollowing)) {
            
            input.PressAssistIsFollowing();
            lastMessageSent = followMessage.ImFollowing;
            wait.Update();
            return;
        }

        if (playerReader.TargetGuid != focusTargetGuid)
        {
            FocusTargetInput(default);
            wait.Update();
        }

        if (playerReader.TargetGuid == focusTargetGuid &&
            playerReader.SpellInRange.Focus_Inspect &&
            !bits.AutoFollow() &&
            !input.FollowTarget.OnCooldown())
        {
            input.PressFollowTarget();

            wait.Update();
            // Use Macro to send i'm following in party chat
            input.PressAssistIsFollowing();
            lastMessageSent = followMessage.ImFollowing;
            wait.Update();

            chatReader.AssistRequestReturn = false;
            wait.Update();
        }
        else if (!bits.AutoFollow() && !playerReader.SpellInRange.Focus_Inspect)
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
                input.StepBackwards();
                wait.Update();
                input.PressAssistCantFollow();
                lastMessageSent = followMessage.ICantFollow;
            }

            wait.Update();
            return;
        }

        wait.Update();
    }
}
