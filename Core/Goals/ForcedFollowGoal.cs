using Core.GOAP;
using Microsoft.Extensions.Logging;

namespace Core.Goals;

public sealed class ForcedFollowGoal : GoapGoal
{
    public override float Cost => 19f;

    private readonly ConfigurableInput input;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly Wait wait;
    private readonly ClassConfiguration classConfig;
    private readonly ILogger<ForcedFollowGoal> logger;
    private readonly RestHandler restHandler;
    private readonly ChatReader chatReader;
    private readonly CastingHandler castingHandler;
    private readonly IMountHandler mountHandler;

    public ForcedFollowGoal(ConfigurableInput input,
        PlayerReader playerReader,
        AddonBits bits,
        Wait wait,
        ClassConfiguration classConfig,
        ILogger<ForcedFollowGoal> logger,
        RestHandler restHandler,
        ChatReader chatReader,
        CastingHandler castingHandler,
        IMountHandler mountHandler
        )
        : base(nameof(ForcedFollowGoal))
    {
        this.input = input;
        this.playerReader = playerReader;
        this.bits = bits;
        this.wait = wait;
        this.classConfig = classConfig;
        this.logger = logger;
        this.restHandler = restHandler;
        this.chatReader = chatReader;
        this.castingHandler = castingHandler;
        this.mountHandler = mountHandler;

        this.Keys = classConfig.ForcedFollow.Sequence;

        if (classConfig.UnitToFollow == "focus")
        {
            AddPrecondition(GoapKey.hasfocus, true);
        }

        AddPrecondition(GoapKey.forcedfollow, true);
    }

    public override void OnEnter()
    {
        if (input.IsKeyDown(input.ForwardKey))
        {
            input.StopForward(true);
        }

        wait.Update();

        input.PressClearTarget();

        wait.Update();

        if (!bits.Target() || (
            playerReader.TargetGuid != playerReader.FocusGuid
            && playerReader.TargetGuid != playerReader.PartyMember1Guid
            && playerReader.TargetGuid != playerReader.PartyMember2Guid
            && playerReader.TargetGuid != playerReader.PartyMember3Guid
            && playerReader.TargetGuid != playerReader.PartyMember4Guid
            ))
        {
            for (int i = 0; bits.Target_Alive() && i < Keys.Length; i++)
            {
                bool validChangeToTarget = false;

                KeyAction keyAction = Keys[i];

                if (!string.IsNullOrEmpty(keyAction.ChangeTargetTo) && keyAction.CanRun())
                {
                    wait.Update();

                    switch (keyAction.ChangeTargetTo)
                    {
                        case "focus":
                            validChangeToTarget = true;
                            input.PressTargetFocus();
                            break;
                        case "party1":
                            validChangeToTarget = true;
                            input.PressTargetFocus();
                            break;
                        case "party2":
                            validChangeToTarget = true;
                            input.PressTargetFocusPartyMemberTwo();
                            break;
                        case "party3":
                            validChangeToTarget = true;
                            input.PressTargetFocusPartyMemberThree();
                            break;
                        case "party4":
                            validChangeToTarget = true;
                            input.PressTargetFocusPartyMemberFour();
                            break;
                        default:
                            logger.LogWarning("keyAction.ChangeTargetTo not a valid target: " + keyAction.ChangeTargetTo);
                            break;
                    }

                    wait.Update();
                }

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

                // After performing the action restore our previous target
                if (validChangeToTarget)
                {
                    input.PressLastTarget();
                    wait.Update();
                }

                // Safety valve - if we accidentally target a friendly member we should,
                // clear target.
                if ((!bits.Target_Hostile()
                     || bits.Target_PlayerControlled()
                     || bits.Target_Player()
                     || playerReader.TargetGuid == playerReader.FocusGuid
                     || playerReader.TargetGuid == playerReader.PartyMember1Guid
                     || playerReader.TargetGuid == playerReader.PartyMember2Guid
                     || playerReader.TargetGuid == playerReader.PartyMember3Guid
                     || playerReader.TargetGuid == playerReader.PartyMember4Guid
                    )
                    && string.IsNullOrEmpty(keyAction.ChangeTargetTo)
                    && !validChangeToTarget)
                {
                    logger.LogWarning("We were still targeting a friendly target when KeyAction wasn't ChangeTargetTo");
                    input.PressClearTarget();
                    wait.Update();
                }
            }
        }

        wait.Update();
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

        wait.Update();
    }

    public override void Update()
    {
        //logger.LogInformation("ForcedFollowGoal: Inside Update");
        // Removed check for playerReader.SpellInRange.PartyMember4_Inspect
        // As inpect can't be used in combat
        wait.Update();

        input.PressClearTarget();

        wait.Update();

        if (!bits.Target() || (
            playerReader.TargetGuid != playerReader.FocusGuid
            && playerReader.TargetGuid != playerReader.PartyMember1Guid
            && playerReader.TargetGuid != playerReader.PartyMember2Guid
            && playerReader.TargetGuid != playerReader.PartyMember3Guid
            && playerReader.TargetGuid != playerReader.PartyMember4Guid
            ))
        {
            for (int i = 0; bits.Target_Alive() && i < Keys.Length; i++)
            {

                KeyAction keyAction = Keys[i];
                bool validChangeToTarget = false;

                if (!string.IsNullOrEmpty(keyAction.ChangeTargetTo) && keyAction.CanRun())
                {
                    wait.Update();

                    switch (keyAction.ChangeTargetTo)
                    {
                        case "focus":
                            validChangeToTarget = true;
                            input.PressTargetFocus();
                            break;
                        case "party1":
                            validChangeToTarget = true;
                            input.PressTargetFocus();
                            break;
                        case "party2":
                            validChangeToTarget = true;
                            input.PressTargetFocusPartyMemberTwo();
                            break;
                        case "party3":
                            validChangeToTarget = true;
                            input.PressTargetFocusPartyMemberThree();
                            break;
                        case "party4":
                            validChangeToTarget = true;
                            input.PressTargetFocusPartyMemberFour();
                            break;
                        default:
                            logger.LogWarning("keyAction.ChangeTargetTo not a valid target: " + keyAction.ChangeTargetTo);
                            break;
                    }

                    wait.Update();
                }

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

                // After performing the action restore our previous target
                if (validChangeToTarget)
                {
                    input.PressLastTarget();
                    wait.Update();
                }

                // Safety valve - if we accidentally target a friendly member we should,
                // clear target.
                if ((!bits.Target_Hostile()
                     || bits.Target_PlayerControlled()
                     || bits.Target_Player()
                     || playerReader.TargetGuid == playerReader.FocusGuid
                     || playerReader.TargetGuid == playerReader.PartyMember1Guid
                     || playerReader.TargetGuid == playerReader.PartyMember2Guid
                     || playerReader.TargetGuid == playerReader.PartyMember3Guid
                     || playerReader.TargetGuid == playerReader.PartyMember4Guid
                    )
                    && string.IsNullOrEmpty(keyAction.ChangeTargetTo)
                    && !validChangeToTarget)
                {
                    logger.LogWarning("We were still targeting a friendly target when KeyAction wasn't ChangeTargetTo");
                    input.PressClearTarget();
                    wait.Update();
                }
            }
        }

        wait.Update();

        if(!bits.AutoFollow() && !input.FollowTarget.OnCooldown())
        {
            startFollowing();
        }
        
        wait.Update();
    }

    public void startFollowing()
    {
        wait.Update();

        if (classConfig.UnitToFollow == "focus")
        {
            if (playerReader.TargetGuid != playerReader.FocusGuid)
            {
                input.PressTargetFocus();
                wait.Update();
            }

            if (playerReader.TargetGuid == playerReader.FocusGuid &&
                !bits.AutoFollow() &&
                !input.FollowTarget.OnCooldown())
            {
                input.PressFollowTarget();
            }
            else
            {
                logger.LogInformation("ForcedFollowGoal: Couldn't follow focus");
            }
        }
        else if (classConfig.UnitToFollow == "party1")
        {
            if (playerReader.TargetGuid != playerReader.PartyMember1Guid)
            {
                input.PressTargetFocus();
                wait.Update();
            }

            /*
            logger.LogInformation("ForcedFollowGoal: playerReader.TargetGuid " + playerReader.TargetGuid);
            logger.LogInformation("ForcedFollowGoal: playerReader.PartyMember1Guid " + playerReader.PartyMember1Guid);
            logger.LogInformation("ForcedFollowGoal: playerReader.SpellInRange.PartyMember1_Inspect " + playerReader.SpellInRange.PartyMember1_Inspect);
            logger.LogInformation("ForcedFollowGoal: !bits.AutoFollow() " + !bits.AutoFollow());
            logger.LogInformation("ForcedFollowGoal: !input.FollowTarget.OnCooldown() " + !input.FollowTarget.OnCooldown());
            */

            if (playerReader.TargetGuid == playerReader.PartyMember1Guid &&
                !bits.AutoFollow() &&
                !input.FollowTarget.OnCooldown())
            {
                input.PressFollowTarget();
            }
            else
            {
                logger.LogInformation("ForcedFollowGoal: Couldn't follow party1");
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
                !bits.AutoFollow() &&
                !input.FollowTarget.OnCooldown())
            {
                input.PressFollowTarget();
            }
            else
            {
                logger.LogInformation("ForcedFollowGoal: Couldn't follow party2");
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
                !bits.AutoFollow() &&
                !input.FollowTarget.OnCooldown())
            {
                input.PressFollowTarget();
            }
            else
            {
                logger.LogInformation("ForcedFollowGoal: Couldn't follow party3");
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
                !bits.AutoFollow() &&
                !input.FollowTarget.OnCooldown())
            {
                input.PressFollowTarget();
            }
            else
            {
                logger.LogInformation("ForcedFollowGoal: Couldn't follow party4");
            }
        }

        wait.Update();

    }
}

