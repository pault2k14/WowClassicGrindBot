using Core.GOAP;

using Game;

using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using SharedLib;
using System;
using System.Collections.Generic;
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
    
    private float lastDirection;
    private float lastMinDistance;
    private float lastMaxDistance;

    public CombatGoal(ILogger<CombatGoal> logger, ConfigurableInput input,
        Wait wait, PlayerReader playerReader, StopMoving stopMoving, AddonBits bits,
        ClassConfiguration classConfiguration, ClassConfiguration classConfig,
        CastingHandler castingHandler, CombatLog combatLog,
        IMountHandler mountHandler)
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

        if(classConfig.Mode == Mode.AssistFocus)
        {
            AddPrecondition(GoapKey.focuscombat, true);
        }
        else
        {
            AddPrecondition(GoapKey.incombat, true);
        }

        AddPrecondition(GoapKey.hastarget, true);
        AddPrecondition(GoapKey.targetisalive, true);
        AddPrecondition(GoapKey.targethostile, true);
        //AddPrecondition(GoapKey.targettargetsus, true);
        AddPrecondition(GoapKey.incombatrange, true);

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
    }

    public override void OnExit()
    {
        if (combatLog.DamageTakenCount() > 0 && !bits.Target())
        {
            stopMoving.Stop();
        }
    }

    public override void Update()
    {
        wait.Update();

        if (MathF.Abs(lastDirection - playerReader.Direction) > MathF.PI / 2)
        {
            logger.LogInformation("Turning too fast!");
            stopMoving.Stop();
        }

        lastDirection = playerReader.Direction;
        lastMinDistance = playerReader.MinRange();
        lastMaxDistance = playerReader.MaxRange();

        if (bits.Drowning())
        {
            input.PressJump();
            return;
        }

        if (classConfig.AutoPetAttack &&
            bits.Pet() &&
            (!playerReader.PetTarget() || playerReader.PetTargetGuid != playerReader.TargetGuid) &&
            !input.PetAttack.OnCooldown())
        {
            input.PressPetAttack();
        }

        /*
        if (classConfig.Mode == Mode.AssistFocus)
        {
            if (bits.Target != bits.FocusTarget)
            {
                input.PressTargetFocus();
                input.PressTargetOfTarget();
                wait.Update();
            }

            // Use the skull icon to force melee range
            if(playerReader.hasSkullIcon && !playerReader.IsInMeleeRange())
            {
                input.PressFastInteract();
                wait.Update();
            }
        }
        */

        bool crowdControlAction = false;
        bool successfulCast = false;
        ReadOnlySpan<KeyAction> span = Keys;
        for (int i = 0; bits.Target_Alive() && i < span.Length; i++)
        {
            crowdControlAction = false;
            successfulCast = false;
            KeyAction keyAction = span[i];

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
                input.PressFastInteract();
                wait.Update();
                continue;
            }

            if (castingHandler.SpellInQueue() && !keyAction.BaseAction)
            {
                continue;
            }

            logger.LogInformation("keyAction.Name" + keyAction.Name);
            logger.LogInformation("keyAction.CrowdControl " + keyAction.CrowdControl);

            if (keyAction.CrowdControl)
            {
                crowdControlAction = keyAction.CrowdControl;
                logger.LogInformation("Checking Crowd Control");
                if (!CheckCrowdControl(keyAction))
                {
                    logger.LogInformation("No crowd control mobs found, targeting focus target");
                    waitForTargetChange();
                    input.PressTargetFocus();
                    input.PressTargetOfTarget();
                    waitForTargetChange();
                    continue;
                }
            }

            if ((classConfig.RaidIconsToSkipInCombat
                .IndexOf(playerReader.TargetRaidIcon()) != -1 
                && !keyAction.CrowdControl) || 
                (classConfig.RaidIconsToSkipInCombat
                .IndexOf(playerReader.TargetRaidIcon()) != -1 
                && keyAction.CrowdControl && !keyAction.CanRun()))
            {
                logger.LogInformation("Target has crowd control icon, but I don't have the right kind of crowd control");
                input.PressStopAttack();
                wait.Update();
                continue;
            }

            bool interrupt() => bits.Target_Alive() && keyAction.CanBeInterrupted();

            if (castingHandler.CastIfReady(keyAction, interrupt))
            {
                //logger.LogInformation("CombatGoals: Successful Cast");
                successfulCast = true;
                break;
            }

        }

        if (crowdControlAction && successfulCast)
        {
            logger.LogInformation("Clear target of crowdcontrol");
            input.PressClearTarget();
            wait.Update();
        }

        if (classConfig.Loot && bits.SoftInteract_Enabled())
        {
            DealWithSoftInteract();
        }

        if (!bits.Target() || (bits.Target() && bits.Target_Dead()))
        {
            logger.LogInformation("Lost target!");

            if (combatLog.DamageTakenCount() > 0)
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

            if (elapsedPetFoundTarget < 0)
            {
                logger.LogWarning("Pet not found target!");
                input.PressClearTarget();
                return;
            }

            ResetCooldowns();

            input.PressTargetPet();
            input.PressTargetOfTarget();
            wait.Update();

            if(classConfig.RaidIconsToSkipInCombat.IndexOf(playerReader.TargetRaidIcon()) != -1)
            {
                input.PressClearTarget();
                wait.Update();
            }
                
            logger.LogWarning($"Found new target by pet. {elapsedPetFoundTarget}ms");

            return;
        }

        if (classConfig.Mode == Mode.AssistFocus && bits.FocusTarget_Hostile() && bits.FocusTarget_Combat())
        { 
            logger.LogWarning($"Found new combat target of focus.");
            ResetCooldowns();

            waitForTargetChange();
            input.PressTargetFocus();
            input.PressTargetOfTarget();
            waitForTargetChange();

            if (classConfig.RaidIconsToSkipInCombat.IndexOf(playerReader.TargetRaidIcon()) != -1)
            {
                input.PressClearTarget();
                wait.Update();
            }

            return;
        }
        else
        {
            logger.LogInformation("Checking target in front...");
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
                return;
            }

            logger.LogWarning("Dont pull non-hostile target!");
            input.PressClearTarget();
            wait.Update();
        }

        logger.LogWarning($"Waiting for target to exists or lose combat. Possible threats {combatLog.DamageTakenCount()}!");
        wait.Till(CastingHandler.GCD * 2,
            () => bits.Target_Alive() || !bits.Combat());
    }

    public bool CheckCrowdControl(KeyAction item)
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

            if (bits.Target_Alive() && item.CanRun())
            {
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
            input.PressFastInteract();

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
