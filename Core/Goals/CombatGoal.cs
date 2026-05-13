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

    // Fix 22 Part B (log-46 02:46:17→02:46:19, ~2s Combat then NO PLAN):
    // Track the GUID currently latched by the Fix 17 self-defense override.
    // Mirrors GoapAgent._selfDefenseOverrideGuid (private to that class) so
    // CombatGoal's local Fix 17 mirror at line ~256 can keep the override
    // active when the bot has entered the blacklist area during the chase.
    // Without this latch, navigation.IsInBlacklistArea()=true caused the Fix 17
    // mirror to NOT fire (currentTargetIsIgnored stayed True), case 3 bail
    // executed (PressStopAttack + PressClearTarget), the target was cleared,
    // GoapAgent's override condition `hasTarget` failed on the next tick, and
    // the planner produced NO PLAN.
    //
    // The latch fires alongside GoapAgent's latch (set in the same tick the
    // override first activates) and clears together (target gone, switched,
    // or override conditions no longer hold).
    private int _combatOverrideGuid;

    // Fix L (log-58 10:12:22:148 → 10:12:36:305 leader engaging blacklisted
    // 311297 alone via Fix 17, assist stood at projection boundary X~946 for
    // 14 s and did not participate): latch for the party-assist override
    // mirror, paired with GoapAgent._partyAssistOverrideGuid. Holds the
    // focus-target GUID currently being engaged via the party-assist path
    // (= the leader's blacklisted target, as resolved through the focus
    // chain). Used purely to gate log output to once-per-activation,
    // matching the existing _combatOverrideGuid / _selfDefenseOverrideGuid
    // pattern. The two flips (focusTargetIsIgnored, currentTargetIsIgnored
    // when target matches focus) fire on every tick the override conditions
    // hold; only the log message is suppressed by this latch.
    private int _partyAssistMirrorGuid;

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

        // Fix 17 (log-44 00:41:12 leader / 00:41:28 assist — Combat ↔ NO PLAN
        // oscillation for ~70 s while a Deepmoss Venomspitter cast on the
        // bot uncontested): Fix 13's self-defense override flips the
        // GoapKey.targetIsIgnored slot to false at the planner level when
        // the bot is being actively attacked by its own ignored target
        // outside any blacklist rect with evade elapsed — so the planner
        // picks Combat. But the case-3 bail check below uses raw
        // playerReader.IsIgnored without the override, so CombatGoal.Update
        // bails on its very first frame, presses StopAttack + ClearTarget,
        // and the loop runs again 0.3-2 s later. Observed in log-44 ~25
        // oscillations on the leader, ~12 on the assist, neither making
        // progress nor letting the patrol continue.
        //
        // The fix is to mirror Fix 13's conditions here. When the override
        // would have flipped targetIsIgnored at the planner level, do the
        // same flip here so the runtime check reaches case 1 (fight the
        // target) instead of case 3 (bail).
        //
        // Conditions identical to GoapAgent.UpdateWorldState Fix 13 block
        // (line ~840): need a target, in combat, taking damage, target is
        // actually targeting us (not just Tab-cycled onto), evade window
        // already elapsed (read via the assistStatusProvider mirror set
        // in GoapAgent line ~502 for both modes), and bot outside every
        // blacklist rect. All conjuncts required — any one false and we
        // fall through to the existing bail behavior.
        //
        // assistStatusProvider.EvadeRecoveryActive is set unconditionally
        // by GoapAgent for both modes (see comment at GoapAgent.cs line
        // ~499) — the "only AssistFocus uses it" comment refers to the
        // PartyStatePublisher consumer, not the source assignment. Safe
        // to read from CombatGoal regardless of mode.
        // Fix 22 Part B + Fix 23 (log-46 02:46:17:399→02:46:19:453):
        //  - Fix 22B: the original Fix 17 condition required
        //    `!navigation.IsInBlacklistArea()`. When the bot was attacked
        //    outside the rect and started chasing the mob, the chase carried
        //    the bot inside the rect after ~2 s. The Fix 17 mirror stopped
        //    firing (currentTargetIsIgnored stayed True), case 3 bail executed,
        //    target was cleared, GoapAgent's override dropped, Combat plan was
        //    cancelled by the planner — only Approach key had fired, no
        //    offensive spells reached cast range.
        //    Latch: once the override fires for a given GUID, allow it to
        //    continue even when the bot is now inside the rect. New
        //    activations still require !IsInBlacklistArea() — preserves the
        //    original guarantee that we only engage IsIgnored mobs we did
        //    NOT seek out from inside a rect.
        //
        //  - Fix 23 (issue 3 in user's report — leader's return-to-assist
        //    not triggered while assist is in self-defense combat): on the
        //    very first activation tick of the override for a given GUID, in
        //    AssistFocus mode, set assistStatusProvider.CantFollow=true and
        //    press the AssistCantFollow key. This propagates through the
        //    PartyStatePublisher API to the leader's AssistStateStore. The
        //    leader's GoapAgent reads it via AnyAssistCantFollow() (line 878),
        //    sets GoapKey.assistrequestreturn=true, GoapKey.assistrequestreturn-
        //    orisfollowing=true (line 897), FollowRouteGoal's PartyLeader
        //    precondition (line 191) is satisfied, FRG can run, and FRG's
        //    pause/resume logic (line 888) calls GoToOneWaypoint to the
        //    assist's last-known position. The CantFollow flag is cleared
        //    naturally by FFG when the assist later reaches the leader.
        // Fix 26 (log-49, paired with the GoapAgent change at line ~870):
        // drop !navigation.IsInBlacklistArea() from initial activation here
        // too, so the mirror keeps parity with the planner-level override.
        // If GoapAgent flips targetIgnored=false via Fix 26's relaxed
        // condition (which now permits insideBL=true), CombatGoal MUST
        // mirror the flip — otherwise case 3 below bails on the very
        // first frame (StopAttack/ClearTarget), GoapAgent's override
        // drops on the next tick because hasTarget=false, and the bot
        // oscillates Combat ↔ NO PLAN exactly as Fix 17 originally
        // diagnosed in log-44. The two override sites must agree on
        // when self-defense is active.
        bool overrideAlreadyLatched =
            _combatOverrideGuid != 0 && _combatOverrideGuid == playerReader.TargetGuid;

        if (currentTargetIsIgnored && bits.Target() && bits.Combat()
            && combatLog.DamageTakenCount() > 0
            && playerReader.TargetTarget is UnitsTarget.Me or UnitsTarget.Pet
            && !assistStatusProvider.EvadeRecoveryActive)
        {
            logger.LogInformation(
                $"[CombatGoal] Self-defense override (Fix 17): target guid={playerReader.TargetGuid} " +
                $"is on IsIgnored but actively attacking us (TargetTarget={playerReader.TargetTarget}, " +
                $"playerCombat=true, dmgTaken=true, evadeRecovery=false, " +
                $"insideBlacklistArea={navigation.IsInBlacklistArea()}, latched={overrideAlreadyLatched}) — " +
                $"treating as fightable, falling through to engage rather than bailing.");
            currentTargetIsIgnored = false;

            // Fix 22B + Fix 23: first-activation handling.
            if (_combatOverrideGuid != playerReader.TargetGuid)
            {
                _combatOverrideGuid = playerReader.TargetGuid;

                if (classConfig.Mode == Mode.AssistFocus && !assistStatusProvider.CantFollow)
                {
                    // Set BOTH the flag AND the status. AssistStateStore's
                    // GetCantFollowState() (line 139) requires Status==CantFollow
                    // strictly — AnyAssistCantFollow() accepts either, but the
                    // leader's GoapAgent diff-loop (line 339-347) calls
                    // GoToOneWaypoint only when GetCantFollowState() returns
                    // non-null. Without the Status set, the leader broadcasts
                    // AssistRequestReturn (FRG precondition is satisfied) but
                    // never receives a navigable target — leader just patrols
                    // normally. With both set, the leader navigates directly
                    // to our last-published position.
                    //
                    // Status is reverted by FFG when combat ends and FFG resumes
                    // (FFG lines 477/658/681/850/etc. set Following / Waiting /
                    // NavigatingToLeader on its own transitions). The flag is
                    // cleared by FFG's normal reach-leader cleanup (line 535/707).
                    //
                    // Fix 28 (log-50, user-reported): removed the legacy
                    //   input.PressAssistCantFollow();
                    // call that used to fire here. That key triggered an
                    // in-game chat macro ("/p i tried following but you are
                    // too far away my position:X,Y") which the leader's
                    // ChatReader.cs:242 used to parse. The architecture moved
                    // to API-based party-state coordination (PartyStatePublisher
                    // → AssistStateStore), and the leader now reads exclusively
                    // from assistStateStore.AnyAssistCantFollow() at
                    // GoapAgent.cs line 924-926 — chatReader.AssistRequestReturn
                    // is dead code (only appears in legacy comments, no
                    // production read site in Core/). The chat keypress was
                    // pure waste: in-game chat noise visible to other players,
                    // a NumPad5 keypress that conflicted with other macros,
                    // and a typing delay (log-50 14:26:58:515 keypress →
                    // 14:27:00:182 chat visible — 1.7 s lag) that the API
                    // path beats by an order of magnitude (PartyApiConfig
                    // .AssistPostIntervalMs=500 ms).
                    //
                    // The two API-path lines above this comment are the
                    // canonical signal:
                    //   - assistStatusProvider.CantFollow = true; → sets
                    //     the assistshouldfollow gate to true (GoapAgent
                    //     line 976 branch) and AnyAssistCantFollow() on
                    //     the leader.
                    //   - assistStatusProvider.CurrentStatus = BotStatus
                    //     .CantFollow; → published via the next
                    //     PartyStatePublisher tick so the leader sees the
                    //     CantFollow state and the position to navigate to.
                    assistStatusProvider.CantFollow = true;
                    assistStatusProvider.CurrentStatus = BotStatus.CantFollow;
                    logger.LogInformation(
                        $"[CombatGoal] Self-defense override first activation (Fix 23): " +
                        $"signaling CantFollow=true AND Status=CantFollow via the " +
                        $"API path (AssistStatusProvider → PartyStatePublisher) so " +
                        $"the leader navigates to our position. " +
                        $"Target guid={playerReader.TargetGuid}.");
                }
            }
        }
        else if (_combatOverrideGuid != 0)
        {
            // Override conditions no longer hold — clear the local latch. The
            // CantFollow flag is intentionally NOT cleared here; FFG's normal
            // reach-leader cleanup (FFG line 535/707) handles that lifecycle.
            logger.LogInformation(
                $"[CombatGoal] Self-defense override latch cleared (guid={_combatOverrideGuid}). " +
                $"Reason: targetIgnored={currentTargetIsIgnored} hasTarget={bits.Target()} " +
                $"playerCombat={bits.Combat()} dmgTaken={combatLog.DamageTakenCount()} " +
                $"targetTarget={playerReader.TargetTarget} evadeRecovery={assistStatusProvider.EvadeRecoveryActive}.");
            _combatOverrideGuid = 0;
        }

        // Fix L party-assist runtime mirror (log-58 10:12:22:148 → 10:12:36:305:
        // leader engaging blacklisted 311297 via Fix 17 from 10:12:22:590 onwards;
        // assist held at projection boundary <945, 270>, never engaged the mob,
        // 0 Target Changed events, 0 Fix 17 messages — user observed and asked
        // "shouldn't the assist also be able to attack the blacklisted mob"):
        //
        // Paired with GoapAgent's Fix L block (see line ~1044). GoapAgent's flip
        // unblocks the planner-level allPartyTargetsIsIgnored precondition so
        // Combat plan can be selected; this CombatGoal flip unblocks the
        // runtime-level Case 2 (line ~417) swap-to-focus-target AND Case 3
        // (line 403) bail. Both are needed:
        //
        //   - Without this CombatGoal flip on the FIRST tick: local
        //     focusTargetIsIgnored=true (raw IsIgnored read at line 236)
        //     → Case 3 bails immediately on the very first Update tick
        //     after OnEnter, presses StopAttack+ClearTarget, plan re-
        //     evaluates, Combat selected again (GoapAgent's Fix L still
        //     holding), OnEnter again, Case 3 bails again — Combat ↔
        //     bail oscillation, same failure mode as the original Fix 17
        //     diagnosed in log-44 / fixed at line 314 above.
        //
        //   - On SUBSEQUENT ticks after Case 2 swaps the assist's target
        //     to the focus's target (= the blacklisted mob): the assist
        //     now has TargetGuid == FocusTargetGuid, both IsIgnored. If
        //     we only flip focusTargetIsIgnored but not currentTargetIsIgnored,
        //     Case 3 (currentTargetIsIgnored && (focusTargetIsIgnored ||
        //     !isPartyMode)) — after our flip Case 3 evaluates to
        //     (true && (false || false)) = false → Case 3 doesn't fire,
        //     fall through to normal combat. BUT Case 1 (the standard
        //     combat path) checks currentTargetIsIgnored downstream too
        //     in some sub-paths (e.g. the evade-broadcast at line ~435).
        //     Flipping currentTargetIsIgnored when target matches focus
        //     keeps the runtime consistent with "this target is fightable
        //     via party-assist", parallel to leader-side Fix 17 flipping
        //     currentTargetIsIgnored=false at line 325.
        //
        // Detection conditions match GoapAgent Fix L exactly:
        //   - AssistFocus mode only
        //   - focusTargetIsIgnored (raw IsIgnored map says yes)
        //   - bits.FocusTarget() (focus has a target)
        //   - bits.FocusTarget_Combat() (focus target is in combat — only
        //     happens via leader-side Fix 17 for IsIgnored targets)
        //   - FocusTargetGuid != 0 (defensive)
        //   - !assistStatusProvider.EvadeRecoveryActive (preserve the
        //     "retreat during recovery" intent; only activate after
        //     recovery elapses)
        //
        // Behavior:
        //   - Always flip focusTargetIsIgnored=false when conditions hold,
        //     so Case 2 fires (initial swap) and Case 3 doesn't fire
        //     (subsequent ticks)
        //   - If assist's current target ALSO equals focus target (post-swap),
        //     additionally flip currentTargetIsIgnored=false for runtime
        //     consistency with leader-side Fix 17
        //   - Log once per new GUID activation (_partyAssistMirrorGuid latch)
        //
        // FFG projection NOT changed: CombatGoal uses direct Approach key
        // presses (line ~570 PressApproachOnCooldown) which bypass FFG
        // entirely. The assist physically walks into BL alongside the
        // leader while Combat runs. When the mob dies, CombatGoal exits
        // naturally, plan returns to FFG, FFG's projection re-engages,
        // and the assist navigates back out of BL.
        if (classConfig.Mode == Mode.AssistFocus &&
            focusTargetIsIgnored &&
            bits.FocusTarget() &&
            bits.FocusTarget_Combat() &&
            playerReader.FocusTargetGuid != 0 &&
            !assistStatusProvider.EvadeRecoveryActive)
        {
            bool assistTargetMatchesFocus =
                bits.Target() &&
                playerReader.TargetGuid != 0 &&
                playerReader.TargetGuid == playerReader.FocusTargetGuid;

            if (_partyAssistMirrorGuid != playerReader.FocusTargetGuid)
            {
                _partyAssistMirrorGuid = playerReader.FocusTargetGuid;
                logger.LogInformation(
                    $"[CombatGoal] Fix L party-assist override (mirror): focus target " +
                    $"guid={playerReader.FocusTargetGuid} is on IsIgnored but the " +
                    $"leader (focus) is in combat with it. Flipping " +
                    $"focusTargetIsIgnored=false" +
                    (assistTargetMatchesFocus
                        ? $" AND currentTargetIsIgnored=false (assist target matches focus post-swap)"
                        : $" (assist target guid={playerReader.TargetGuid} doesn't match focus yet — Case 2 swap will fire next)") +
                    $" so Case 2 can swap and Case 3 won't bail.");
            }

            focusTargetIsIgnored = false;
            if (assistTargetMatchesFocus)
            {
                currentTargetIsIgnored = false;
            }
        }
        else if (_partyAssistMirrorGuid != 0)
        {
            logger.LogInformation(
                $"[CombatGoal] Fix L party-assist override mirror cleared (was " +
                $"guid={_partyAssistMirrorGuid}). Reason: " +
                $"focusTargetIgnored={focusTargetIsIgnored} " +
                $"focusHasTarget={bits.FocusTarget()} " +
                $"focusTargetCombat={bits.FocusTarget_Combat()} " +
                $"evadeRecovery={assistStatusProvider.EvadeRecoveryActive}.");
            _partyAssistMirrorGuid = 0;
        }

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
            //
            // Fix 28: removed the legacy input.PressAssistCantFollow() call
            // that used to follow this assignment. See the explanatory block
            // in the Fix 23 first-activation branch above (~ line 350) for
            // the full rationale; in short, the chat-macro path is dead code
            // because the leader reads from AssistStateStore (API) and not
            // from ChatReader.AssistRequestReturn. The CantFollow=true line
            // alone is the canonical signal — assistshouldfollow flips to
            // true via GoapAgent line 976, and the next 500 ms publisher
            // tick propagates the CantFollow state to the leader's store.
            assistStatusProvider.CantFollow = true;
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
