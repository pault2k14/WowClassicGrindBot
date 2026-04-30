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
using Vortice.Direct3D11;

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
    private readonly StuckDetector stuckDetector;
    private readonly Navigation navigation;
    
    private float lastDirection;
    private float lastMinDistance;
    private float lastMaxDistance;
    private int lastTargetGuid;
    private int consecutiveApproach;
    private int consecutiveNoAction;
    private bool debug;

    private bool _evadeRecoveryActive;

    private const double GhostCombatTimeoutSec = 20.0;
    private DateTime _ghostCombatSinceUtc = DateTime.MinValue;
    private bool _ghostCombatActive;
    private int _ghostCombatDamageSnapshot;

    private const double UnreachableMobTimeoutSec = 18.0;
    private DateTime _stuckApproachingSinceUtc = DateTime.MinValue;
    private bool _stuckApproachingActive;
    private int _stuckApproachingDamageSnapshot;

    public CombatGoal(ILogger<CombatGoal> logger, ConfigurableInput input,
        Wait wait, PlayerReader playerReader, StopMoving stopMoving, AddonBits bits,
        ClassConfiguration classConfiguration, ClassConfiguration classConfig,
        CastingHandler castingHandler, CombatLog combatLog,
        IMountHandler mountHandler, ChatReader chatReader,
        StuckDetector stuckDetector, Navigation navigation)
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
        this.stuckDetector = stuckDetector;
        this.navigation = navigation;

        if (classConfig.Mode == Mode.AssistFocus)
        {
            AddPrecondition(GoapKey.partymembercombat, true);
            AddPrecondition(GoapKey.forcedfollow, false);
            AddPrecondition(GoapKey.evadeRecovery, false);
        }
        else if(classConfig.Mode == Mode.PartyLeader)
        {
            AddPrecondition(GoapKey.partyleadercombat, true);
            AddPrecondition(GoapKey.forcedfollow, false);
            AddPrecondition(GoapKey.evadeRecovery, false);
        }
        else
        {
            AddPrecondition(GoapKey.incombat, true);
            AddPrecondition(GoapKey.forcedfollow, false);
            AddPrecondition(GoapKey.hastarget, true);
            AddPrecondition(GoapKey.targetisalive, true);
            AddPrecondition(GoapKey.targethostile, true);
            AddPrecondition(GoapKey.incombatrange, true);
            AddPrecondition(GoapKey.evadeRecovery, false);
        }

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
        if (e is GoapStateEvent s)
        {
            if (s.Key == GoapKey.producedcorpse)
            {
                float distance = (lastMaxDistance + lastMinDistance) / 2f;
                SendGoapEvent(new CorpseEvent(GetCorpseLocation(distance), distance, playerReader.Direction));
            }
            else if (s.Key == GoapKey.evadeRecovery)
            {
                _evadeRecoveryActive = s.Value;
            }
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
        wait.Update();
        stuckDetector.Reset();
        if (!navigation.IsApproachEscapeActive) navigation.ResetApproachEscape();

        if (mountHandler.IsMounted())
        {
            mountHandler.Dismount();
        }

        lastDirection = playerReader.Direction;

        input.PressDisableSoftInteract();
        wait.Update();

        _stuckApproachingActive = false;
        _stuckApproachingSinceUtc = DateTime.MinValue;
        _stuckApproachingDamageSnapshot = 0;

        _ghostCombatActive = false;
        _ghostCombatSinceUtc = DateTime.MinValue;
        _ghostCombatDamageSnapshot = 0;
    }

    public override void OnExit()
    {
        if (combatLog.DamageTakenCount() > 0 && !bits.Target())
        {
            stopMoving.Stop();
        }

        if (!navigation.IsApproachEscapeActive) navigation.ResetApproachEscape();

        input.PressEnableSoftInteract();
        wait.Update();
    }

    public void logPartyMember1SpellInRangeBitMask()
    {
        logger.LogInformation($"[CombatGoal] PartyMember1MinRange: {playerReader.PartyMember1MinRange()}");
        logger.LogInformation($"[CombatGoal] PartyMember1MaxRange: {playerReader.PartyMember1MaxRange()}");
        logger.LogInformation($"[CombatGoal] PartyMember1 Fortitude in Rnage: {playerReader.PartyMember1SpellInRange.Priest_Power_World_Fortitude}");
        logger.LogInformation($"[CombatGoal] PartyMember1 Shield in Rnage: {playerReader.PartyMember1SpellInRange.Priest_Power_Word_Shield}");
        logger.LogInformation($"[CombatGoal] PartyMember1 Lesser Heal in Range: {playerReader.PartyMember1SpellInRange.Priest_Lesser_Heal}");


        PartyMember1SpellInRange p1range = playerReader.PartyMember1SpellInRange;
        SpellInRange range = playerReader.SpellInRange;


        int bitMask6 = Mask.M[6];
        int bitMask7 = Mask.M[7];
        int bitMask8 = Mask.M[8];

        bool f6 = range[bitMask6];
        bool f7 = range[bitMask7];
        bool f8 = range[bitMask8];

        bool p1f6 = p1range[bitMask6];
        bool p1f7 = p1range[bitMask7];
        bool p1f8 = p1range[bitMask8];

        logger.LogInformation("Values from PartyMember1SpellInRange array");
        logger.LogInformation($"playerReader.PartyMember1SpellInRange Fortitude: {p1f6}");
        logger.LogInformation($"playerReader.PartyMember1SpellInRange Shield: {p1f7}");
        logger.LogInformation($"playerReader.PartyMember1SpellInRange Lesser Heal: {p1f8}");

        logger.LogInformation("Values from SpellInRange array");
        logger.LogInformation($"playerReader.SpellInRange Fortitude: {f6}");
        logger.LogInformation($"playerReader.SpellInRange Shield: {f7}");
        logger.LogInformation($"playerReader.SpellInRange Lesser Heal: {f8}");
    }

    public override void Update()
    {
        bool targetGuidChanged = false;
        bool castOnTargetThisUpdate = false;
        wait.Update();

        if (chatReader.ForcedFollow)
        {
            AddEffect(GoapKey.forcedfollow, true);
            return;
        }

        if (_evadeRecoveryActive)
        {
            logger.LogInformation("[CombatGoal] Evade recovery active — exiting combat goal.");
            input.PressStopAttack();
            wait.Update();
            input.PressClearTarget();
            wait.Update();
            stopMoving.Stop();
            return;
        }

        if (classConfig.Mode == Mode.PartyLeader
            && bits.Target()
            && playerReader.TargetGuid != 0
            && combatLog.EvadeMobs.Contains(playerReader.TargetGuid))
        {
            logger.LogInformation($"[CombatGoal] Target guid={playerReader.TargetGuid} is evading — broadcasting and exiting.");
            SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid));
            playerReader.IgnoreTarget(playerReader.TargetGuid);
            // Clear dynamic stuck rects so the evade retreat path is not blocked
            // by rects placed during the failed approach to this mob.
            navigation.ClearStuckRects();
            input.PressStopAttack();
            wait.Update();
            input.PressClearTarget();
            wait.Update();
            stopMoving.Stop();
            return;
        }

        if (classConfig.Mode == Mode.AssistFocus && chatReader.LeaderBlacklistTarget)
        {
            int blacklistGuid = chatReader.LeaderBlacklistTargetId;
            chatReader.LeaderBlacklistTarget = false;
            chatReader.LeaderBlacklistTargetId = 0;

            if (blacklistGuid != 0)
            {
                logger.LogInformation($"[CombatGoal] Leader blacklisted guid={blacklistGuid} while in combat — ignoring and exiting.");
                playerReader.IgnoreTarget(blacklistGuid);
            }

            input.PressStopAttack();
            wait.Update();
            input.PressClearTarget();
            wait.Update();
            stopMoving.Stop();

            SendGoapEvent(new EvadeBlacklistEvent(blacklistGuid));

            chatReader.AssistRequestReturn = true;
            input.PressAssistCantFollow();
            return;
        }

        if (classConfig.Mode == Mode.PartyLeader || classConfig.Mode == Mode.AssistFocus)
        {
            bool noHostileTarget = !bits.Target_Hostile() && !bits.FocusTarget_Hostile();
            int currentCombinedDamage = combatLog.DamageDoneCount() + combatLog.DamageTakenCount();

            if (noHostileTarget)
            {
                if (!_ghostCombatActive)
                {
                    _ghostCombatActive = true;
                    _ghostCombatSinceUtc = DateTime.UtcNow;
                    _ghostCombatDamageSnapshot = currentCombinedDamage;
                    logger.LogInformation($"[CombatGoal] Ghost combat timer started — no hostile target on either bot. DamageSnapshot={_ghostCombatDamageSnapshot}.");
                }
                else if (currentCombinedDamage > _ghostCombatDamageSnapshot)
                {
                    logger.LogInformation($"[CombatGoal] Ghost combat timer reset — damage increased ({_ghostCombatDamageSnapshot} -> {currentCombinedDamage}).");
                    _ghostCombatActive = false;
                    _ghostCombatSinceUtc = DateTime.MinValue;
                    _ghostCombatDamageSnapshot = 0;
                }
                else
                {
                    double ghostSec = (DateTime.UtcNow - _ghostCombatSinceUtc).TotalSeconds;
                    logger.LogInformation($"[CombatGoal] Ghost combat timer: {ghostSec:0.0}s / {GhostCombatTimeoutSec}s — no hostile target, no damage.");
                    if (ghostSec >= GhostCombatTimeoutSec)
                    {
                        logger.LogWarning($"[CombatGoal] Ghost combat detected after {ghostSec:0.0}s — escaping via route.");
                        _ghostCombatActive = false;
                        _ghostCombatSinceUtc = DateTime.MinValue;
                        _ghostCombatDamageSnapshot = 0;
                        input.PressStopAttack();
                        wait.Update();
                        input.PressClearTarget();
                        wait.Update();
                        stopMoving.Stop();
                        SendGoapEvent(new EvadeBlacklistEvent(0));
                        return;
                    }
                }
            }
            else
            {
                if (_ghostCombatActive)
                {
                    logger.LogInformation("[CombatGoal] Ghost combat timer reset — hostile target acquired.");
                    _ghostCombatActive = false;
                    _ghostCombatSinceUtc = DateTime.MinValue;
                    _ghostCombatDamageSnapshot = 0;
                }
            }
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

        if (consecutiveApproach >= 5 
            && (!playerReader.IsInMeleeRange() || combatLog.DamageDoneCount() == 0))
        {
            if (!stuckDetector.IsMoving())
            {
                logger.LogInformation("StuckDetector: We aren't moving");
                stuckDetector.Update();

                if (!_stuckApproachingActive)
                {
                    _stuckApproachingActive = true;
                    _stuckApproachingSinceUtc = DateTime.UtcNow;
                    _stuckApproachingDamageSnapshot = combatLog.DamageDoneCount();
                    logger.LogInformation($"[CombatGoal] Geometry trap timer started — stuck approaching. DamageDone snapshot={_stuckApproachingDamageSnapshot}.");
                }
                else
                {
                    int currentDamage = combatLog.DamageDoneCount();
                    if (currentDamage > _stuckApproachingDamageSnapshot)
                    {
                        logger.LogInformation($"[CombatGoal] Geometry trap timer reset — damage increased ({_stuckApproachingDamageSnapshot} -> {currentDamage}).");
                        _stuckApproachingActive = false;
                        _stuckApproachingSinceUtc = DateTime.MinValue;
                        _stuckApproachingDamageSnapshot = 0;
                    }
                    else
                    {
                        double stuckSec = (DateTime.UtcNow - _stuckApproachingSinceUtc).TotalSeconds;
                        if (stuckSec >= UnreachableMobTimeoutSec)
                        {
                            logger.LogWarning($"[CombatGoal] Geometry trap detected after {stuckSec:0.0}s — mob unreachable, disengaging.");
                            playerReader.IgnoreTarget(playerReader.TargetGuid);
                            input.PressStopAttack();
                            wait.Update();
                            input.PressClearTarget();
                            wait.Update();
                            stopMoving.Stop();
                            // Clear dynamic stuck rects so the retreat path is not blocked
                            // by rects placed during the failed approach. The mob is now
                            // ignored so re-approach is already prevented permanently.
                            navigation.ClearStuckRects();
                            // Use pather-based escape: project waypoint along approach vector
                            // (10-30 yards, incrementing by 1) to route around the obstacle.
                            // Falls back to random turn/jump if all pather attempts fail.
                            navigation.TryUnstuck();
                            _stuckApproachingActive = false;
                            _stuckApproachingSinceUtc = DateTime.MinValue;
                            _stuckApproachingDamageSnapshot = 0;
                            consecutiveApproach = 0;
                            return;
                        }
                    }
                }
            }
            else
            {
                logger.LogInformation("StuckDetector: We are moving");
                if (_stuckApproachingActive)
                {
                    _stuckApproachingActive = false;
                    _stuckApproachingSinceUtc = DateTime.MinValue;
                    _stuckApproachingDamageSnapshot = 0;
                    logger.LogInformation("[CombatGoal] Geometry trap timer reset — character is moving.");
                }
            }
        }
        else if (consecutiveApproach >= 5 
            && (playerReader.IsInMeleeRange() && combatLog.DamageDoneCount() > 0))
        {
            consecutiveApproach = 0;
            logger.LogInformation("StuckDetector: Reset consecutiveApproach");
            if (_stuckApproachingActive)
            {
                _stuckApproachingActive = false;
                _stuckApproachingSinceUtc = DateTime.MinValue;
                _stuckApproachingDamageSnapshot = 0;
            }
        }

        if(consecutiveNoAction >= 20 && combatLog.DamageDoneCount() == 0)
        {
            input.PressInteract();
            wait.Update();
        }

        if(!bits.Target() || !bits.Target_Alive())
        {
            if(debug)
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
            }

            if (bits.FocusTarget() && bits.Focus_Combat()
                && bits.FocusTarget_Alive() && bits.FocusTarget_Hostile()
                && !combatLog.EvadeMobs.Contains(playerReader.FocusTargetGuid)
                && !playerReader.IsIgnored(playerReader.FocusTargetGuid))
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
        if (debug) {
            logger.LogInformation("bits.Target_Alive(): " + bits.Target_Alive());
            logger.LogInformation("Target Guids We Know of");
            logger.LogInformation($"playerReader.TargetGuid: {playerReader.TargetGuid}");
            logger.LogInformation($"playerReader.FocusTargetGuid: {playerReader.FocusTargetGuid}");
            logger.LogInformation($"playerReader.PTCurrent (Rage): {playerReader.PTCurrent()}");
        }
        
        if(playerReader.TargetGuid != lastTargetGuid)
        {
            targetGuidChanged = true;
            logger.LogInformation($"Target Changed To: {playerReader.TargetGuid}");
        }

        lastTargetGuid = playerReader.TargetGuid;

        ReadOnlySpan<KeyAction> span = Keys;
        for (int i = 0; bits.Target_Alive() && i < span.Length; i++)
        {
            if(debug)
            {
                logger.LogInformation("Inside KeyAction loop: span[" + i + "]");
            }

            if (debug && playerReader.TargetGuid != playerReader.FocusTargetGuid)
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

            if ((classConfig.Mode == Mode.AssistFocus
                && string.IsNullOrEmpty(keyAction.ChangeTargetTo)
                && ((bits.Focus_Combat() && bits.FocusTarget_Hostile() && bits.FocusTarget_Alive()
                     && playerReader.TargetGuid != playerReader.FocusTargetGuid
                     && !playerReader.TargetsMe())
                   )
                )
                || ((classConfig.Mode == Mode.PartyLeader)
                     && string.IsNullOrEmpty(keyAction.ChangeTargetTo)
                     && (!bits.Target() || bits.Target_Dead()) && bits.FocusTarget() 
                     && bits.FocusTarget_Alive() && bits.Focus_Combat() 
                     && bits.FocusTarget_Hostile()
                     && !combatLog.EvadeMobs.Contains(playerReader.FocusTargetGuid)
                     && !playerReader.IsIgnored(playerReader.FocusTargetGuid)
                   )
                )
            {
                if (debug && classConfig.Mode == Mode.AssistFocus)
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
                else if (debug && classConfig.Mode == Mode.PartyLeader)
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

            if (classConfig.Mode == Mode.AssistFocus
                && playerReader.OutOfCombatRange()
                && !playerReader.TargetsMe()
                && combatLog.DamageTakenCount() > 0
                && string.IsNullOrEmpty(keyAction.ChangeTargetTo)
                && playerReader.TargetGuid == playerReader.FocusTargetGuid)
            {
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

            if (classConfig.Mode == Mode.AssistFocus
                && playerReader.hasTriangleIcon())
            {
                input.PressClearTarget();
                wait.Update();
                return;
            }

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
                if (!CheckCrowdControl(keyAction))
                {
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

            if ((currentTargetHasRaidIcon && !keyAction.CrowdControl) ||
                (currentTargetHasRaidIcon && keyAction.CrowdControl && !foundValidCrowdControlAction))
            {
                logger.LogInformation("Target has crowd control icon, but I don't have the right kind of crowd control");
                wait.Update();
                input.PressStopAttack();
                wait.Update();
                continue;
            }

            bool interrupt() => bits.Target_Alive() && keyAction.CanBeInterrupted();

            if (castingHandler.CastIfReady(keyAction, interrupt))
            {
                if(keyAction.Name.Equals("Approach"))
                {
                    if (navigation.IsApproachEscapeActive)
                    {
                        navigation.Update(CancellationToken.None);
                        if (navigation.IsApproachEscapeActive)
                            navigation.TryUnstuck();
                        return;
                    }

                    navigation.RecordApproachPosition(playerReader.WorldPos);
                    consecutiveApproach = consecutiveApproach + 1;
                }
                else
                {
                    consecutiveApproach = 0; 
                }
                
                successfulCast = true;
                castOnTargetThisUpdate = true;
                consecutiveNoAction = 0;
                logger.LogInformation("castOnTargetThisUpdate: " + castOnTargetThisUpdate);
                break;
            }

            if (validChangeToTarget)
            {
                input.PressLastTarget();
                wait.Update();
            }

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

        if(debug)
        {
            logger.LogInformation("After combat actions loop");
            logger.LogInformation("castOnTargetThisUpdate: " + castOnTargetThisUpdate);
            logger.LogInformation("targetGuidChanged: " + targetGuidChanged);
        }

        if (crowdControlAction && successfulCast)
        {
            logger.LogInformation("Clear target of crowdcontrol");
            wait.Update();
            input.PressClearTarget();
            wait.Update();
        }

        if (!castOnTargetThisUpdate)
        {
            consecutiveNoAction = consecutiveNoAction + 1;
        }

        if (!bits.Target_Hostile() && !bits.Target_Alive() && !bits.Combat() 
            && !bits.Focus_Combat() && !playerReader.PetTarget() 
            && classConfig.Loot && bits.SoftInteract_Enabled())
        {
            logger.LogInformation("Deal with soft interact");
            DealWithSoftInteract();
        }

        if (!bits.Target() || (!bits.Target_Combat() && combatLog.DamageTakenCount() > 0) || (bits.Target() && bits.Target_Dead()))
        {
            logger.LogInformation("Lost target!");

            if ((combatLog.DamageTakenCount() > 0)
                || (bits.Pet())
                || ((classConfig.Mode == Mode.PartyLeader || classConfig.Mode == Mode.AssistFocus) 
                      && bits.Focus_Combat() && bits.FocusTarget() && bits.FocusTarget_Alive() && bits.FocusTarget_Hostile()))
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
        if (bits.Pet())
        {
            float elapsedPetFoundTarget = wait.Until(CastingHandler.GCD,
                () => playerReader.PetTarget() && bits.PetTarget_Alive());

            if (elapsedPetFoundTarget < 0
                 && (classConfig.Mode != Mode.AssistFocus || classConfig.Mode != Mode.PartyLeader))
            {
                logger.LogWarning("Pet not found target!");
                input.PressClearTarget();
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
            && !combatLog.EvadeMobs.Contains(playerReader.FocusTargetGuid)
            && !playerReader.IsIgnored(playerReader.FocusTargetGuid)
            )
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
            if (combatLog.EvadeMobs.Contains(playerReader.TargetGuid)
                || playerReader.IsIgnored(playerReader.TargetGuid))
            {
                int evadingGuid = playerReader.TargetGuid;
                logger.LogInformation($"[CombatGoal] FindPossibleThreats: NearestTarget guid={evadingGuid} is evading/ignored — re-firing evade escape to move away.");
                input.PressClearTarget();
                wait.Update();
                stopMoving.Stop();
                SendGoapEvent(new EvadeBlacklistEvent(evadingGuid));
                return;
            }
            else if (bits.Target_Combat() && bits.TargetTarget_PlayerOrPet())
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
    }

    public bool CheckTargetsTargetingMe()
    {
        int originalTargetGuid = playerReader.TargetGuid;
        Dictionary<int, int> unitGuidDictonary = new Dictionary<int, int>();

        unitGuidDictonary.Add(playerReader.TargetGuid, playerReader.TargetRaidIcon());

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
        
        unitGuidDictonary.Add(playerReader.TargetGuid, playerReader.TargetRaidIcon());
        
        wait.Update();

        for (int x = 0; x < 10; x++)
        {
            wait.Update();
            input.PressNearestTarget();
            wait.Update();

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

        if (playerReader.TargetGuid != originalTargetGuid)
        {
            wait.Update();
            input.PressClearTarget();
            wait.Update();
        }

        return false;
    }

    public bool CheckNonFocusAttack()
    {
        Dictionary<int, int> unitGuidDictonary = new Dictionary<int, int>();
        unitGuidDictonary.Add(playerReader.TargetGuid, playerReader.TargetRaidIcon());

        wait.Update();

        for (int x = 0; x < 10; x++)
        {
            input.PressNearestTarget();
            waitForTargetChange();

            if (bits.Target_Alive() && classConfig.RaidIconsToAttackWithoutFocus
                .Contains(playerReader.TargetRaidIcon()))
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
