using Core.GOAP;
using Core.Party;

using Game;

using Microsoft.Extensions.Logging;

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
    private readonly ChatReader chatReader;
    private readonly StuckDetector stuckDetector;
    private readonly Navigation navigation;
    private readonly AssistStateStore assistStateStore;
    private readonly AssistStatusProvider assistStatusProvider;

    private float lastDirection;
    private float lastMinDistance;
    private float lastMaxDistance;
    private int lastTargetGuid;
    private int consecutiveApproach;
    private int consecutiveNoAction;
    private bool debug;

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
        StuckDetector stuckDetector, Navigation navigation,
        AssistStateStore assistStateStore,
        AssistStatusProvider assistStatusProvider)
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
        this.assistStateStore = assistStateStore;
        this.assistStatusProvider = assistStatusProvider;

        if (classConfig.Mode == Mode.AssistFocus)
        {
            AddPrecondition(GoapKey.partymembercombat, true);
            AddPrecondition(GoapKey.forcedfollow, false);
            // Replaces evadeRecovery=false. Allows Combat selection during the
            // evade window when a non-blacklisted aggressor is fightable in
            // either the assist's own target slot or the focus chain
            // (= the leader's target). When only Mob A is around, both slots
            // are ignored → key true → Combat blocked → FFG continues retreat.
            AddPrecondition(GoapKey.allPartyTargetsIsIgnored, false);
        }
        else if(classConfig.Mode == Mode.PartyLeader)
        {
            AddPrecondition(GoapKey.partyleadercombat, true);
            AddPrecondition(GoapKey.forcedfollow, false);
            // Symmetric to AssistFocus above. On the leader the focus chain
            // resolves to the assist's target.
            AddPrecondition(GoapKey.allPartyTargetsIsIgnored, false);
        }
        else
        {
            AddPrecondition(GoapKey.incombat, true);
            AddPrecondition(GoapKey.forcedfollow, false);
            AddPrecondition(GoapKey.hastarget, true);
            AddPrecondition(GoapKey.targetisalive, true);
            AddPrecondition(GoapKey.targethostile, true);
            AddPrecondition(GoapKey.incombatrange, true);
            // Standalone Grind: no party, no focus chain — the only slot is
            // the bot's own target. Keep the simpler evadeRecovery gate.
            AddPrecondition(GoapKey.evadeRecovery, false);
        }

        if(classConfig.Loot)
            AddEffect(GoapKey.producedcorpse, true);
        else
            AddEffect(GoapKey.producedcorpse, false);

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
            // Previously also captured GoapKey.evadeRecovery into a local
            // _evadeRecoveryActive field used by Update() to bail out of
            // combat. Removed: the bail logic now uses fresh per-tick reads
            // of playerReader.IsIgnored against current/focus target GUIDs,
            // and the planner-level gate is the new GoapKey.allPartyTargetsIsIgnored
            // precondition (see ctor). This goal no longer needs to react
            // to evadeRecovery events directly.
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
            mountHandler.Dismount();

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
            stopMoving.Stop();

        if (!navigation.IsApproachEscapeActive) navigation.ResetApproachEscape();

        input.PressEnableSoftInteract();
        wait.Update();
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

        // ── Target / focus-target ignore handling ─────────────────────────
        // Three cases:
        //   (1) current target is fightable           → fall through, fight it.
        //   (2) current is ignored/absent, focus is fightable
        //                                            → swap via PressTargetFocus
        //                                              + PressTargetOfTarget,
        //                                              fall through, fight focus's target.
        //   (3) both ignored/absent                   → bail (StopAttack/ClearTarget).
        //
        // Case 3 is normally prevented by the planner — CombatGoal's
        // PartyLeader/AssistFocus preconditions include allPartyTargetsIsIgnored=false,
        // so the planner won't pick Combat unless at least one slot is fightable.
        // The bail here is defense-in-depth for the tick where world state was
        // computed before a Tab/auto-target flipped the bot's slot to a
        // blacklisted GUID. In standalone Grind mode there's no focus chain,
        // so case 2 doesn't apply — the swap branch is gated on party modes.
        bool currentTargetIsIgnored =
            !bits.Target() || playerReader.IsIgnored(playerReader.TargetGuid);
        bool focusTargetIsIgnored =
            !bits.FocusTarget() || playerReader.IsIgnored(playerReader.FocusTargetGuid);

        bool isPartyMode = classConfig.Mode == Mode.PartyLeader
                         || classConfig.Mode == Mode.AssistFocus;

        if (currentTargetIsIgnored && (focusTargetIsIgnored || !isPartyMode))
        {
            // Case 3: nothing fightable in any party slot.
            logger.LogInformation(
                $"[CombatGoal] Exiting combat goal — current target guid={playerReader.TargetGuid} " +
                $"and focus target guid={playerReader.FocusTargetGuid} are both ignored or absent.");
            input.PressStopAttack();
            wait.Update();
            input.PressClearTarget();
            wait.Update();
            stopMoving.Stop();
            return;
        }

        if (currentTargetIsIgnored && isPartyMode && !focusTargetIsIgnored)
        {
            // Case 2: bot's own target is blacklisted but focus chain has a
            // fightable mob. Swap: TargetFocus selects the focus unit
            // (party member), TargetOfTarget then selects what they're
            // fighting. WoW Classic sticky-targeting will keep us on that
            // GUID for the remainder of the fight (auto-aggro from Mob A
            // will not re-acquire because we already have a target).
            logger.LogInformation(
                $"[CombatGoal] Current target guid={playerReader.TargetGuid} is ignored — " +
                $"swapping to focus chain target guid={playerReader.FocusTargetGuid}.");
            input.PressTargetFocus();
            wait.Update();
            input.PressTargetOfTarget();
            wait.Update();
            // Fall through into normal combat handling against the new target.
        }

        if (classConfig.Mode == Mode.PartyLeader
            && bits.Target()
            && playerReader.TargetGuid != 0
            && combatLog.EvadeMobs.Contains(playerReader.TargetGuid))
        {
            logger.LogInformation($"[CombatGoal] Target guid={playerReader.TargetGuid} is evading — broadcasting and exiting.");
            SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid));
            playerReader.IgnoreTarget(playerReader.TargetGuid);
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

            // assistStatusProvider.CantFollow keeps assistshouldfollow=true so
            // FollowFocusGoal is immediately selectable during evade recovery.
            assistStatusProvider.CantFollow = true;
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
                    logger.LogInformation($"[CombatGoal] Ghost combat timer started. DamageSnapshot={_ghostCombatDamageSnapshot}.");
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
                    logger.LogDebug($"[CombatGoal] Ghost combat: {ghostSec:0.0}s / {GhostCombatTimeoutSec}s.");
                    if (ghostSec >= GhostCombatTimeoutSec)
                    {
                        logger.LogWarning($"[CombatGoal] Ghost combat detected — escaping via route.");
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

        // Gate producedcorpse: don't flag kills for looting when assist can't follow.
        // PartyLeader reads the API store; AssistFocus reads its own CantFollow flag.
        bool assistCantReturn = classConfig.Mode == Mode.PartyLeader
            ? assistStateStore.AnyAssistCantFollow()
            : assistStatusProvider.CantFollow;

        if (classConfig.Loot && !assistCantReturn)
            AddEffect(GoapKey.producedcorpse, true);
        else
            AddEffect(GoapKey.producedcorpse, false);

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
                    logger.LogInformation($"[CombatGoal] Geometry trap timer started. DamageDone snapshot={_stuckApproachingDamageSnapshot}.");
                }
                else
                {
                    int currentDamage = combatLog.DamageDoneCount();
                    if (currentDamage > _stuckApproachingDamageSnapshot)
                    {
                        _stuckApproachingActive = false;
                        _stuckApproachingSinceUtc = DateTime.MinValue;
                        _stuckApproachingDamageSnapshot = 0;
                    }
                    else
                    {
                        double stuckSec = (DateTime.UtcNow - _stuckApproachingSinceUtc).TotalSeconds;
                        if (stuckSec >= UnreachableMobTimeoutSec)
                        {
                            logger.LogWarning($"[CombatGoal] Geometry trap detected after {stuckSec:0.0}s — disengaging.");
                            playerReader.IgnoreTarget(playerReader.TargetGuid);
                            input.PressStopAttack();
                            wait.Update();
                            input.PressClearTarget();
                            wait.Update();
                            stopMoving.Stop();
                            navigation.ClearStuckRects();
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

        if(playerReader.TargetGuid != lastTargetGuid)
        {
            targetGuidChanged = true;
            logger.LogInformation($"Target Changed To: {playerReader.TargetGuid}");
        }

        lastTargetGuid = playerReader.TargetGuid;

        ReadOnlySpan<KeyAction> span = Keys;
        for (int i = 0; bits.Target_Alive() && i < span.Length; i++)
        {
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
                && bits.Focus_Combat() && bits.FocusTarget_Hostile() && bits.FocusTarget_Alive()
                && playerReader.TargetGuid != playerReader.FocusTargetGuid
                && !playerReader.TargetsMe())
                || (classConfig.Mode == Mode.PartyLeader
                     && string.IsNullOrEmpty(keyAction.ChangeTargetTo)
                     && (!bits.Target() || bits.Target_Dead()) && bits.FocusTarget()
                     && bits.FocusTarget_Alive() && bits.Focus_Combat()
                     && bits.FocusTarget_Hostile()
                     && !combatLog.EvadeMobs.Contains(playerReader.FocusTargetGuid)
                     && !playerReader.IsIgnored(playerReader.FocusTargetGuid)))
            {
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
                logger.LogDebug("Update: AssistFocus Taking damage but not within combat range of focus target.");
                CheckTargetsTargetingMe();
                return;
            }

            int originalTargetGuid = playerReader.TargetGuid;
            currentTargetHasRaidIcon = classConfig.RaidIconsToSkipInCombat
                .IndexOf(playerReader.TargetRaidIcon()) != -1;
            crowdControlAction = false;
            foundValidCrowdControlAction = false;
            successfulCast = false;

            if (classConfig.Mode == Mode.AssistFocus && playerReader.hasTriangleIcon())
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
                        return;

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
                    consecutiveApproach++;
                }
                else
                {
                    consecutiveApproach = 0;
                }

                successfulCast = true;
                castOnTargetThisUpdate = true;
                consecutiveNoAction = 0;
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
                 || playerReader.TargetGuid == playerReader.PartyMember4Guid)
                && string.IsNullOrEmpty(keyAction.ChangeTargetTo)
                && !validChangeToTarget)
            {
                logger.LogWarning("We were still targeting a friendly target when KeyAction wasn't ChangeTargetTo");
                input.PressClearTarget();
                wait.Update();
            }
        }

        if (crowdControlAction && successfulCast)
        {
            logger.LogInformation("Clear target of crowdcontrol");
            wait.Update();
            input.PressClearTarget();
            wait.Update();
        }

        if (!castOnTargetThisUpdate)
            consecutiveNoAction++;

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
                || bits.Pet()
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
            }
            else if(elapsedPetFoundTarget > 0)
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
            && !playerReader.IsIgnored(playerReader.FocusTargetGuid))
        {
            logger.LogWarning($"Found new combat target of focus.");
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
            input.PressNearestTarget();
            wait.Update();
        }

        if (bits.Target() && !bits.Target_Dead() && bits.Target_Hostile())
        {
            if (combatLog.EvadeMobs.Contains(playerReader.TargetGuid)
                || playerReader.IsIgnored(playerReader.TargetGuid))
            {
                int evadingGuid = playerReader.TargetGuid;
                logger.LogInformation($"[CombatGoal] FindPossibleThreats: NearestTarget guid={evadingGuid} is evading/ignored — re-firing evade escape.");
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
            }
            else if(bits.Focus_Combat() && bits.FocusTarget() && bits.FocusTarget_Hostile() && bits.FocusTarget_Alive())
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

        logger.LogWarning($"Waiting for target to exist or lose combat. Possible threats {combatLog.DamageTakenCount()}!");
        wait.Till(CastingHandler.GCD * 2, () => bits.Target_Alive() || !bits.Combat());
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
                logger.LogInformation("CheckTargetsTargetingMe targets me, within combat range, hostile, alive!");
                input.PressInteract();
                wait.Update();
                return true;
            }

            if (unitGuidDictonary.ContainsKey(playerReader.TargetGuid))
                break;

            unitGuidDictonary.Add(playerReader.TargetGuid, playerReader.TargetRaidIcon());
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
                return true;

            if (unitGuidDictonary.ContainsKey(playerReader.TargetGuid))
                break;

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
            return;

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

        ConsoleKey key = Random.Shared.Next(2) == 0 ? input.TurnLeftKey : input.TurnRightKey;
        logger.LogWarning($"Invalid SoftInteract Detected Turn away({key}) then face target!");
        input.SetKeyState(key, true, false);
        while (InvalidSoftInteractExists()) wait.Update();
        input.SetKeyState(key, false, false);
        wait.Fixed(playerReader.DoubleNetworkLatency);
        wait.Update();

        if (bits.Target() && !InvalidSoftInteractExists())
        {
            input.PressInteract();
            float e = wait.AfterEquals(playerReader.SpellQueueTimeMs, 2, playerReader._Direction);
            stopMoving.StopForward();
        }
    }

    private bool InvalidSoftInteractExists()
    {
        return bits.SoftInteract() && (
            playerReader.SoftInteract_Type != GuidType.Creature ||
            bits.SoftInteract_Dead() ||
            bits.SoftInteract_Tagged());
    }
}
