using Core.GOAP;

using Game;

using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using SharedLib;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Numerics;
using System.Threading;

namespace Core.Goals;

public sealed class CombatGoal : GoapGoal, IGoapEventListener
{
    public override float Cost => 4f;

    private readonly ILogger<CombatGoal> logger;
    private readonly ConfigurableInput input;
    private readonly ClassConfiguration classConfig;
    private readonly Wait wait;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly StopMoving stopMoving;
    private readonly CastingHandler castingHandler;
    private readonly IMountHandler mountHandler;
    private readonly CombatLog combatLog;
    private readonly ChatReader chatReader;
    
    private float lastDirection;
    private float lastMinDistance;
    private float lastMaxDistance;
    private int lastTargetGuid;

    public CombatGoal(ILogger<CombatGoal> logger, ConfigurableInput input,
        Wait wait, PlayerReader playerReader, StopMoving stopMoving, AddonBits bits,
        ClassConfiguration classConfiguration, ClassConfiguration classConfig,
        CastingHandler castingHandler, CombatLog combatLog,
        IMountHandler mountHandler, ChatReader chatReader)
        : base(nameof(CombatGoal))
    {
        this.logger = logger;
        this.input = input;

        this.wait = wait;
        this.playerReader = playerReader;
        this.bits = bits;
        this.combatLog = combatLog;

        this.stopMoving = stopMoving;
        this.castingHandler = castingHandler;
        this.mountHandler = mountHandler;
        this.classConfig = classConfig;
        this.chatReader = chatReader;

        if (classConfig.Mode == Mode.AssistFocus)
        {
            AddPrecondition(GoapKey.partymembercombat, true);
            AddPrecondition(GoapKey.forcedfollow, false);
        }
        else if(classConfig.Mode == Mode.PartyLeader)
        {
            AddPrecondition(GoapKey.partyleadercombat, true);
            AddPrecondition(GoapKey.forcedfollow, false);
        }
        else
        {
            AddPrecondition(GoapKey.incombat, true);
            AddPrecondition(GoapKey.forcedfollow, false);
            AddPrecondition(GoapKey.hastarget, true);
            AddPrecondition(GoapKey.targetisalive, true);
            AddPrecondition(GoapKey.targethostile, true);
            //AddPrecondition(GoapKey.targettargetsus, true);
            AddPrecondition(GoapKey.incombatrange, true);
        }

        // Removed this due to if getting attack in combat while
        // moving back to the assist location would not be able to fight
        // back and assist also won't fight back.
        //AddPrecondition(GoapKey.assistrequestreturn, false);

        if(classConfig.Loot)
        {
            AddEffect(GoapKey.producedcorpse, true);
        }
        else
        {
            AddEffect(GoapKey.producedcorpse, false);
        }
            
        AddEffect(GoapKey.targetisalive, false);
        AddEffect(GoapKey.hastarget, false);

        Keys = classConfiguration.Combat.Sequence;
    }

    public void OnGoapEvent(GoapEventArgs e)
    {
        if (e is GoapStateEvent s && s.Key == GoapKey.producedcorpse)
        {
            // have to check range
            // ex. target died far away have to consider the range and approximate
            float distance = (lastMaxDistance + lastMinDistance) / 2f;
            SendGoapEvent(new CorpseEvent(GetCorpseLocation(distance), distance, playerReader.Direction));
        }
    }

    private void ResetCooldowns()
    {
        ReadOnlySpan<KeyAction> span = Keys;
        for (int i = 0; i < span.Length; i++)
        {
            KeyAction keyAction = span[i];
            if (keyAction.ResetOnNewTarget)
            {
                keyAction.ResetCooldown();
                keyAction.ResetCharges();
            }
        }
    }

    public override void OnEnter()
    {
        if (mountHandler.IsMounted())
        {
            mountHandler.Dismount();
        }

        lastDirection = playerReader.Direction;

        // Let's just totally disable soft interact for nwo
        // to see if that will help
        // Scenario
        // 1. Target Mob
        // 2. Press Disable Soft Interact
        // 3. Additional mob
        // 4. Kill first mob
        // 5. Interact with additional mob (that is targeted) actually
        //    interacts with Original mob as soft interact that was
        //    established before disabling soft interact is not cleared
        // - Possible solutions
        //   Option 1 - Completely disable soft interact
        //   Option 2 -
        //      1. Disable soft interact
        //      2. clear target
        //      3. target last target
        //   Option 3 - 
        //      1. Enable soft interact on entering loot
        //      2. Disable soft interact after leaving skinning
        //input.PressClearTarget();
        //wait.Update();
        input.PressDisableSoftInteract();
        wait.Update();
        //input.PressLastTarget();
        //wait.Update();
    }

    public override void OnExit()
    {
        if (combatLog.DamageTakenCount() > 0 && !bits.Target())
        {
            stopMoving.Stop();
        }

        // Let's just totally disable soft interact for nwo
        // to see if that will help
        // Scenario
        // 1. Target Mob
        // 2. Press Disable Soft Interact
        // 3. Additional mob
        // 4. Kill first mob
        // 5. Interact with additional mob (that is targeted) actually
        //    interacts with Original mob as soft interact that was
        //    established before disabling soft interact is not cleared
        // - Possible solutions
        //   Option 1 - Completely disable soft interact
        //   Option 2 -
        //      1. Disable soft interact
        //      2. clear target
        //      3. target last target
        //   Option 3 - 
        //      1. Enable soft interact on entering loot
        //      2. Disable soft interact after leaving skinning
        input.PressEnableSoftInteract();
        wait.Update();
    }

    public override void Update()
    {
        bool targetGuidChanged = false;
        bool castOnTargetThisUpdate = false;
        wait.Update();
        logger.LogInformation("In CombatGoals Update!");

        if (chatReader.ForcedFollow)
        {
            AddEffect(GoapKey.forcedfollow, true);
            return;
        }


        if (classConfig.Loot && !chatReader.AssistRequestReturn)
        {
            AddEffect(GoapKey.producedcorpse, true);
        }
        else
        {
            AddEffect(GoapKey.producedcorpse, false);
        }

        if (MathF.Abs(lastDirection - playerReader.Direction) > MathF.PI / 2)
        {
            logger.LogInformation("Turning too fast!");
            stopMoving.Stop();

            if(bits.Target())
            {
                wait.Update(100);
                input.PressInteract();
                wait.Update(100);
            }
        }

        lastDirection = playerReader.Direction;
        lastMinDistance = playerReader.MinRange();
        lastMaxDistance = playerReader.MaxRange();

        if (bits.Drowning())
        {
            input.PressJump();
            return;
        }

        // If we have no target or our target is dead we should
        // try to figure out why we are still in combat
        if(!bits.Target() || !bits.Target_Alive())
        {
            logger.LogInformation("No target or Target_Dead()");
            logger.LogInformation("playerReader.TargetGuid: " + playerReader.TargetGuid);
            logger.LogInformation("playerReader.FocusTargetGuid: " + playerReader.FocusTargetGuid);
            logger.LogInformation("!bits.Target(): " + !bits.Target());
            logger.LogInformation("!bits.Target_Alive(): " + !bits.Target_Alive());
            logger.LogInformation("bits.FocusTarget(): " + bits.FocusTarget());
            logger.LogInformation("bits.Focus_Combat(): " + bits.Focus_Combat());
            logger.LogInformation("bits.FocusTarget_Alive(): " + bits.FocusTarget_Alive());
            logger.LogInformation("bits.FocusTarget_Hostile(): " + bits.FocusTarget_Hostile());

            if (bits.FocusTarget() && bits.Focus_Combat()
                && bits.FocusTarget_Alive() && bits.FocusTarget_Hostile())
            // Doesn't always seem to work && bits.FocusTarget_Alive()
            {
                logger.LogInformation("Targeting target of focus as they are in combat");

                wait.Update();
                input.PressTargetFocus();
                input.PressTargetOfTarget();
                wait.Update();
                input.PressInteract();
                wait.Update();
            }
        }

        if (classConfig.AutoPetAttack &&
            bits.Pet() &&
            (!playerReader.PetTarget() || playerReader.PetTargetGuid != playerReader.TargetGuid) &&
            !input.PetAttack.OnCooldown())
        {
            input.PressPetAttack();
        }

        bool crowdControlAction = false;
        bool foundValidCrowdControlAction = false;
        bool successfulCast = false;
        bool currentTargetHasRaidIcon = classConfig.RaidIconsToSkipInCombat
                .IndexOf(playerReader.TargetRaidIcon()) != -1;

        logger.LogInformation("bits.Target_Alive(): " + bits.Target_Alive());
        logger.LogInformation("Target Guids We Know of");
        logger.LogInformation($"playerReader.TargetGuid: {playerReader.TargetGuid}");
        logger.LogInformation($"playerReader.FocusTargetGuid: {playerReader.FocusTargetGuid}");
        logger.LogInformation($"playerReader.PTCurrent (Rage): {playerReader.PTCurrent()}");
        
        // Check to see if our target guid changed since the
        // last time we ran combat actions
        if(playerReader.TargetGuid != lastTargetGuid)
        {
            targetGuidChanged = true;
        }

        // After comparison update lastTargetGuid to current target.
        lastTargetGuid = playerReader.TargetGuid;

        logger.LogInformation($"targetGuidCHange: {targetGuidChanged}");

        ReadOnlySpan<KeyAction> span = Keys;
        for (int i = 0; bits.Target_Alive() && i < span.Length; i++)
        {
            logger.LogInformation("Inside KeyAction loop: span[" + i + "]");

            if (playerReader.TargetGuid != playerReader.FocusTargetGuid)
            {
                logger.LogInformation("playerReader.TargetGuid != playerReader.FocusTargetGuid");
                logger.LogInformation($"playerReader.TargetGuid: {playerReader.TargetGuid}");
                logger.LogInformation($"playerReader.FocusTargetGuid: {playerReader.FocusTargetGuid}");
            }

            KeyAction keyAction = span[i];
            bool validChangeToTarget = false;

            if (chatReader.ForcedFollow)
            {
                AddEffect(GoapKey.forcedfollow, true);
                return;
            }

            wait.Update();

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

            // TODO Do we need Pet check to be put here? 
            if ((classConfig.Mode == Mode.AssistFocus
                && string.IsNullOrEmpty(keyAction.ChangeTargetTo)
                && ((bits.Focus_Combat() && bits.Target_Hostile() && bits.FocusTarget_Alive()
                     // does not seem to work corretly all of the time && bits.FocusTarget_Alive() && bits.FocusTarget_Combat()
                     && playerReader.TargetGuid != playerReader.FocusTargetGuid)
                   )
                )
                || ((classConfig.Mode == Mode.PartyLeader)
                     && string.IsNullOrEmpty(keyAction.ChangeTargetTo)
                     && (!bits.Target() || bits.Target_Dead()) && bits.FocusTarget() 
                     && bits.FocusTarget_Alive() && bits.Focus_Combat() 
                     && bits.FocusTarget_Hostile()
                   // doesn't always seem to work correctly && bits.FocusTarget_Alive()  && bits.FocusTarget_Combat() 
                   )
                )
            {
                if (classConfig.Mode == Mode.AssistFocus)
                {
                    logger.LogInformation("targetGuid not equal to FocusTargetGuid");
                    logger.LogInformation("bits.Focus_Combat(): " + bits.Focus_Combat());
                    logger.LogInformation("bits.FocusTarget_Combat(): " + bits.FocusTarget_Combat());
                    logger.LogInformation("bits.FocusTarget_Alive(): " + bits.FocusTarget_Alive());
                    logger.LogInformation("playerReader.TargetGuid: " + playerReader.TargetGuid);
                    logger.LogInformation("playerReader.FocusTargetGuid: " + playerReader.FocusTargetGuid);
                    logger.LogInformation("!bits.Target_Alive(): " + !bits.Target_Alive());
                    logger.LogInformation("!bits.Target_Combat(): " + !bits.Target_Combat());
                    logger.LogInformation("bits.Target_Tagged(): " + bits.Target_Tagged());
                }
                else if (classConfig.Mode == Mode.PartyLeader)
                {
                    logger.LogInformation("no target, but focus has target in combat, changing to that target");
                    logger.LogInformation("bits.Focus_Combat(): " + bits.Focus_Combat());
                    logger.LogInformation("bits.FocusTarget_Combat(): " + bits.FocusTarget_Combat());
                    logger.LogInformation("bits.FocusTarget_Alive(): " + bits.FocusTarget_Alive());
                    logger.LogInformation("playerReader.TargetGuid: " + playerReader.TargetGuid);
                    logger.LogInformation("playerReader.FocusTargetGuid: " + playerReader.FocusTargetGuid);
                    logger.LogInformation("!bits.Target_Alive(): " + !bits.Target_Alive());
                    logger.LogInformation("!bits.Target_Combat(): " + !bits.Target_Combat());
                    logger.LogInformation("bits.Target_Tagged(): " + bits.Target_Tagged());
                }

                wait.Update();
                input.PressTargetFocus();
                input.PressTargetOfTarget();
                wait.Update();
                input.PressInteract();
                wait.Update();
                return;
            }

            // Sometimes a mob will attack our assist while the party leader
            // is pulling a different mob.
            if (classConfig.Mode == Mode.AssistFocus
                && playerReader.OutOfCombatRange()
                && !playerReader.TargetsMe()
                && combatLog.DamageTakenCount() > 0
                && string.IsNullOrEmpty(keyAction.ChangeTargetTo)
                && playerReader.TargetGuid == playerReader.FocusTargetGuid)
            {
                // We are taking damage, we are out of combat range,
                // and our target is the same as the focus, we probably
                // were attacked while the focus was pulling / approaching
                // and will not be able to get into combat range with the focus's target
                // let's find and attack the mob that is attacking us.
                logger.LogInformation("Update: AssistFocus Taking damage but not within combat range of focus target, checking who is targeting me.");
                CheckTargetsTargetingMe();
                return;
            }

            int originalTargetGuid = playerReader.TargetGuid;
            currentTargetHasRaidIcon = classConfig.RaidIconsToSkipInCombat
                .IndexOf(playerReader.TargetRaidIcon()) != -1;
            crowdControlAction = false;
            foundValidCrowdControlAction = false;
            successfulCast = false;


            // Use specific raid icons to force attacking of a non focus target
            /*
            if (classConfig.Mode == Mode.AssistFocus
                && classConfig.RaidIconsToAttackWithoutFocus.Length > 0
                && !playerReader.hasSkullIcon
                && !keyAction.CrowdControl)
            {
                //logger.LogInformation("CombatGoals: Check for RaidIconsToAttackWithoutFocus");
                
                if (!CheckNonFocusAttack())
                {
                    //logger.LogInformation("CombatGoals: Didn't find a RaidIconToAttackWithoutFocus");
                    wait.Update();
                    input.PressTargetFocus();
                    input.PressTargetOfTarget();
                    wait.Update();
                }
                
            }
            */

            /* Use the triangle icon to prevent combat to a target */
            if (classConfig.Mode == Mode.AssistFocus
                && playerReader.hasTriangleIcon())
            {
                input.PressClearTarget();
                wait.Update();
                return;
            }

            /* Use the skull icon to force melee range */
            if (classConfig.Mode == Mode.AssistFocus
                && playerReader.hasSkullIcon()
                && !playerReader.IsInMeleeRange()
                && !keyAction.CrowdControl)
            {
                input.PressInteract();
                wait.Update();
                continue;
            }

            if (castingHandler.SpellInQueue() && !keyAction.BaseAction)
            {
                foundValidCrowdControlAction = true;
                continue;
            }

            if (keyAction.CrowdControl)
            {
                crowdControlAction = keyAction.CrowdControl;
                //logger.LogInformation("Checking Crowd Control");
                if (!CheckCrowdControl(keyAction))
                {
                    //logger.LogInformation("No valid crowd control mobs found for this action.");

                    if (playerReader.TargetGuid != playerReader.FocusTargetGuid
                        || !bits.Target() || (bits.Target() && bits.Target_Dead()))
                    {
                        if (!bits.Target())
                        {
                            logger.LogInformation("No Target returning.");
                        }
                        else if (bits.Target() && bits.Target_Dead()) {
                            logger.LogInformation("We have a target but it's dead returning");
                        }
                        else if (playerReader.TargetGuid != playerReader.FocusTargetGuid)
                        {
                            logger.LogInformation("target is not the same as focus returning");
                        }

                        return;
                    }

                    currentTargetHasRaidIcon = classConfig.RaidIconsToSkipInCombat
                .IndexOf(playerReader.TargetRaidIcon()) != -1;
                    continue;
                }
                else
                {
                    currentTargetHasRaidIcon = true;
                    foundValidCrowdControlAction = true;
                }
            }

            //logger.LogInformation("Update keyAction.CanRun(): " + keyAction.CanRun());

            // We Don't have the correct kind of crowd control continue on 
            if ((currentTargetHasRaidIcon && !keyAction.CrowdControl) ||
                (currentTargetHasRaidIcon && keyAction.CrowdControl && !foundValidCrowdControlAction))
            {
                logger.LogInformation("Target has crowd control icon, but I don't have the right kind of crowd control");
                wait.Update();
                input.PressStopAttack();
                wait.Update();
                continue;
            }

            //logger.LogInformation("Update currentTargetHasRaidIcon #2: " + currentTargetHasRaidIcon);

            bool interrupt() => bits.Target_Alive() && keyAction.CanBeInterrupted();

            if (castingHandler.CastIfReady(keyAction, interrupt))
            {
                //logger.LogInformation("CombatGoals: Successful Cast");
                successfulCast = true;
                castOnTargetThisUpdate = true;
                logger.LogInformation("castOnTargetThisUpdate: " + castOnTargetThisUpdate);
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

        logger.LogInformation("After combat actions loop");
        logger.LogInformation("castOnTargetThisUpdate: " + castOnTargetThisUpdate);
        logger.LogInformation("targetGuidChanged: " + targetGuidChanged);

        /* DISABLE THIS FOR NOW TO SEE IF IT IS REALLY NEEDED
        if(bits.TargetTarget_PlayerOrPet() && !castOnTargetThisUpdate && !targetGuidChanged
            && playerReader.TargetGuid == playerReader.FocusTargetGuid)
        {
            logger.LogInformation("bits.TargetTarget_PlayerOrPet(): " + bits.TargetTarget_PlayerOrPet()
                + "playerReader.TargetGuid == playerReader.FocusTargetGuid"
                + "!castOnTargetThisUpdate: " + !castOnTargetThisUpdate
                + "!targetGuidChanged: " + !targetGuidChanged);
            input.PressInteract();
            wait.Update();
        }
        */

        if (crowdControlAction && successfulCast)
        {
            logger.LogInformation("Clear target of crowdcontrol");
            wait.Update();
            input.PressClearTarget();
            wait.Update();
        }

        // TODO Could this be moved further down?
        // If we are a partyleader or assistfocus we don't want to try to loot/soft interact if our focus 
        // is still in combat
        if (!bits.Target_Hostile() && !bits.Target_Alive() && !bits.Combat() 
            && !bits.Focus_Combat() && !playerReader.PetTarget() 
            && classConfig.Loot && bits.SoftInteract_Enabled())
        {
            logger.LogInformation("Deal with soft interact");
            DealWithSoftInteract();
        }


        // TODO make sure before picking up ANY new target that
        // 1 of Us, Focus, or Pet is in combat with a target
        // trying to clean up picking up extra targets when they weren't
        // actually in combat with us
        if (!bits.Target() || (!bits.Target_Combat() && combatLog.DamageTakenCount() > 0) || (bits.Target() && bits.Target_Dead()))
        {
            logger.LogInformation("Lost target!");

            if ((combatLog.DamageTakenCount() > 0)
                || (bits.Pet())
                || ((classConfig.Mode == Mode.PartyLeader || classConfig.Mode == Mode.AssistFocus) 
                      && bits.Focus_Combat() && bits.FocusTarget() && bits.FocusTarget_Alive() && bits.FocusTarget_Hostile()))
            // doesn't seem to work correcly all of the time && bits.FocusTarget_Alive()  && bits.FocusTarget_Combat()
            {
                if (bits.Target() && bits.Target_Dead())
                {
                    logger.LogInformation("Clear current dead target!");
                    input.PressClearTarget();
                    wait.Update();
                }

                logger.LogWarning("Search Possible Threats!");
                stopMoving.Stop();

                FindPossibleThreats();
            }
            else
            {
                logger.LogInformation("No damage taken, clear target.");
                wait.Update();
                input.PressClearTarget();
                wait.Update();
            }
        }
    }

    private void waitForTargetChange()
    {
        int totalTime = playerReader.GCD.Value + playerReader.NetworkLatency;
        wait.Update(totalTime);
    }

    private void FindPossibleThreats()
    {

        if (bits.Pet_Defensive())
        {
            float elapsedPetFoundTarget = wait.Until(CastingHandler.GCD,
                () => playerReader.PetTarget() && bits.PetTarget_Alive());

            if (elapsedPetFoundTarget < 0
                 && (classConfig.Mode != Mode.AssistFocus || classConfig.Mode != Mode.PartyLeader))
            {
                logger.LogWarning("Pet not found target!");
                input.PressClearTarget();
                // remove early return so we can check for targets on
                // our focus or other hostile targets near us
                // return;
            } else if(elapsedPetFoundTarget > 0)
            {
                ResetCooldowns();

                input.PressTargetPet();
                input.PressTargetOfTarget();
                input.PressInteract();
                wait.Update();

                if (classConfig.RaidIconsToSkipInCombat.IndexOf(playerReader.TargetRaidIcon()) != -1)
                {
                    wait.Update();
                    input.PressClearTarget();
                    wait.Update();
                }

                logger.LogWarning($"Found new target by pet. {elapsedPetFoundTarget}ms");

                return;
            }
        }

        if ((classConfig.Mode == Mode.AssistFocus || classConfig.Mode == Mode.PartyLeader) 
            && bits.Focus_Combat() && bits.FocusTarget() 
            && bits.FocusTarget_Hostile()
            && bits.FocusTarget_Alive()
            )
        // seems to be false incorrectly sometimes && bits.FocusTarget_Alive() && bits.FocusTarget_Combat()
        {
            logger.LogWarning($"Found new combat target of focus.");
            logger.LogInformation("bits.Focus_Combat(): " + bits.Focus_Combat());
            logger.LogInformation("bits.FocusTarget(): " + bits.FocusTarget());
            logger.LogInformation("bits.FocusTarget_Hostile(): " + bits.FocusTarget_Hostile());
            logger.LogInformation("bits.FocusTarget_Combat(): " + bits.FocusTarget_Combat());
            logger.LogInformation("bits.FocusTarget_Alive(): " + bits.FocusTarget_Alive());


            ResetCooldowns();

            wait.Update();
            input.PressTargetFocus();
            input.PressTargetOfTarget();
            wait.Update();
            input.PressInteract();
            wait.Update();

            if (classConfig.RaidIconsToSkipInCombat.IndexOf(playerReader.TargetRaidIcon()) != -1)
            {
                wait.Update();
                input.PressClearTarget();
                wait.Update();
            }

            return;
        }
        else
        {
            logger.LogInformation("Checking target in front...");
            logger.LogInformation("bits.Focus_Combat(): " + bits.Focus_Combat());
            logger.LogInformation("bits.FocusTarget(): " + bits.FocusTarget());
            logger.LogInformation("bits.FocusTarget_Hostile(): " + bits.FocusTarget_Hostile());
            logger.LogInformation("bits.FocusTarget_Combat(): " + bits.FocusTarget_Combat());
            logger.LogInformation("bits.FocusTarget_Alive(): " + bits.FocusTarget_Alive());
            input.PressNearestTarget();
            wait.Update();
        }

        if (bits.Target() && !bits.Target_Dead() && bits.Target_Hostile())
        {
            if (bits.Target_Combat() && bits.TargetTarget_PlayerOrPet())
            {
                if (classConfig.RaidIconsToSkipInCombat.IndexOf(playerReader.TargetRaidIcon()) != -1)
                {
                    input.PressClearTarget();
                    wait.Update();
                    return;
                }

                ResetCooldowns();

                logger.LogWarning("Found new target!");
                wait.Update();
                input.PressInteract();
                wait.Update();
                return;
            } else if(bits.Focus_Combat() && bits.FocusTarget() && bits.FocusTarget_Hostile() && bits.FocusTarget_Alive())
            // doesn't seem to always work correctly && bits.FocusTarget_Alive() && bits.FocusTarget_Combat()
            {
                logger.LogWarning("Found new target of focus!");
                ResetCooldowns();
                wait.Update();
                input.PressTargetFocus();
                input.PressTargetOfTarget();
                wait.Update();
                input.PressInteract();
                wait.Update();
                return;
            }


            logger.LogWarning("Dont pull non-hostile target!");
            input.PressClearTarget();
            wait.Update();
        }

        logger.LogWarning($"Waiting for target to exists or lose combat. Possible threats {combatLog.DamageTakenCount()}!");
        wait.Till(CastingHandler.GCD * 2,
            () => bits.Target_Alive() || !bits.Combat());

        if (classConfig.Mode == Mode.AssistFocus || classConfig.Mode == Mode.PartyLeader)
        {
            // Added this so we can hopefully pickup the target
            // later if it is in combat with us
            logger.LogInformation("CHECK - Added clear target to exit of FindPossibleThreats");
            input.PressClearTarget();
            wait.Update();
        }

    }

    public bool CheckTargetsTargetingMe()
    {
        int originalTargetGuid = playerReader.TargetGuid;
        Dictionary<int, int> unitGuidDictonary = new Dictionary<int, int>();

        // Add current target
        unitGuidDictonary.Add(playerReader.TargetGuid, playerReader.TargetRaidIcon());

        /* Tab through all nearby hostile units recording their GUID
         * if we find a mob targeting me keep it targeted  
         */

        wait.Update();

        for (int x = 0; x < 6; x++)
        {
            wait.Update();
            input.PressNearestTarget();
            wait.Update();

            if (playerReader.TargetsMe() && playerReader.WithInCombatRange() 
                && bits.Target_Hostile() && bits.Target_Alive())
            {
                logger.LogInformation("CheckTargetsTargetingMe targets me, target within combat range, target hostile, and target alive!");
                input.PressInteract();
                wait.Update();
                return true;
            }

            if (unitGuidDictonary.ContainsKey(playerReader.TargetGuid))
            {
                break;
            }

            unitGuidDictonary.Add(playerReader.TargetGuid, playerReader.TargetRaidIcon());
        }

        if (playerReader.TargetGuid != originalTargetGuid)
        {
            logger.LogInformation("CheckTargetsTargetingMe exiting function, couldn't find a target that is targeting me");
            wait.Update();
        }

        return false;
    }

    public bool CheckCrowdControl(KeyAction item)
    {
        int originalTargetGuid = playerReader.TargetGuid;
        Dictionary<int, int> unitGuidDictonary = new Dictionary<int, int>();
        
        // Add current target
        unitGuidDictonary.Add(playerReader.TargetGuid, playerReader.TargetRaidIcon());
        
        /* Tab through all nearby hostile units recording their GUID
         * if we find a mob with a raid icon add it to list of targets  
         */

        wait.Update();

        for (int x = 0; x < 10; x++)
        {
            wait.Update();
            input.PressNearestTarget();
            wait.Update();

            if (bits.Target_Alive() && item.CanRun())
            {
                //logger.LogInformation("CheckCrowdControl target alive and CanRun true");
                return true;
            }

            if (unitGuidDictonary.ContainsKey(playerReader.TargetGuid))
            {
                //logger.LogInformation("CheckCrowdControl Found the same target as we saw before break");
                break;
            }

            unitGuidDictonary.Add(playerReader.TargetGuid, playerReader.TargetRaidIcon());
        }

        if (playerReader.TargetGuid != originalTargetGuid)
        {
            //logger.LogInformation("CheckCrowdControl exiting function, targetGuid and originalTargetGuid not the same, clear target!");
            wait.Update();
            input.PressClearTarget();
            wait.Update();
        }

        return false;
    }

    public bool CheckNonFocusAttack()
    {
        Dictionary<int, int> unitGuidDictonary = new Dictionary<int, int>();
        // Add current target
        unitGuidDictonary.Add(playerReader.TargetGuid, playerReader.TargetRaidIcon());

        /* Tab through all nearby hostile units recording their GUID
         * if we find a mob with a raid icon add it to list of targets  
         */

        wait.Update();

        for (int x = 0; x < 10; x++)
        {
            input.PressNearestTarget();
            waitForTargetChange();

            if (bits.Target_Alive() && classConfig.RaidIconsToAttackWithoutFocus
    .Contains(playerReader.TargetRaidIcon()))
            {
                //logger.LogInformation("CombatGoals: Found RaidIconsToAttackWithoutFocus");
                return true;
            }

            if (unitGuidDictonary.ContainsKey(playerReader.TargetGuid))
            {
                break;
            }

            unitGuidDictonary.Add(playerReader.TargetGuid, playerReader.TargetRaidIcon());
        }

        return false;
    }

    private Vector3 GetCorpseLocation(float distance)
    {
        return PointEstimator.GetMapPos(playerReader.WorldMapArea, playerReader.WorldPos, playerReader.Direction, distance);
    }

    private void DealWithSoftInteract()
    {
        if (!playerReader.IsInMeleeRange() ||
            playerReader.IsCasting() ||
            !InvalidSoftInteractExists() ||
            playerReader.TargetGuid == playerReader.SoftInteract_Guid)
        {
            return;
        }

        if (playerReader.TargetGuid == playerReader.PetGuid 
            || playerReader.TargetGuid == playerReader.FocusGuid
            || playerReader.TargetGuid == playerReader.PartyMember1Guid
            || playerReader.TargetGuid == playerReader.PartyMember2Guid
            || playerReader.TargetGuid == playerReader.PartyMember3Guid
            || playerReader.TargetGuid == playerReader.PartyMember4Guid)
        {
            logger.LogInformation("We are targeting our pet or a party member, clear target and return");
            input.PressClearTarget();
            wait.Update();
            return;
        }

        ConsoleKey key = Random.Shared.Next(2) == 0
            ? input.TurnLeftKey
            : input.TurnRightKey;

        logger.LogWarning($"Invalid SoftInteract Detected Turn away({key}) then face target!");

        input.SetKeyState(key, true, false);
        while (InvalidSoftInteractExists())
        {
            wait.Update();
        }
        input.SetKeyState(key, false, false);
        wait.Fixed(playerReader.DoubleNetworkLatency);
        wait.Update();

        if (bits.Target() && !InvalidSoftInteractExists())
        {
            input.PressInteract();

            const int updateCount = 2;
            float e = wait.AfterEquals(playerReader.SpellQueueTimeMs,
                updateCount, playerReader._Direction);

            stopMoving.StopForward();
        }
    }

    private bool InvalidSoftInteractExists()
    {
        return
            bits.SoftInteract() &&
            (
            playerReader.SoftInteract_Type != GuidType.Creature ||
            bits.SoftInteract_Dead() ||
            bits.SoftInteract_Tagged()
            );
    }
}
