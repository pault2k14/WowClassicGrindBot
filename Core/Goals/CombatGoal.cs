using Core.GOAP;
using Core.Party;

using Game;

using Microsoft.Extensions.Logging;

using SharedLib;

using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private readonly BlacklistRecheckCache recheckCache;
    private readonly AssistStateStore assistStateStore;
    private readonly LeaderConnectionStatus leaderConnection;
    private readonly BlacklistRecheckOperation recheckOperation;
    private readonly AssistStatusProvider assistStatusProvider;

    private float lastDirection;
    private float lastMinDistance;
    private float lastMaxDistance;
    private int lastTargetGuid;
    private int consecutiveApproach;
    private int consecutiveNoAction;
    private bool debug;

    // Ghost combat detection moved to GoapAgent.CheckGhostCombat — fields
    // removed. See Fix BS in GoapAgent.cs (~line 65) for full rationale.

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

    // Fix GJ (run-168, phase E) — Combat plan engagement gate for non-IsIgnored.
    // Latches the current target guid being gated so we get one log per
    // gate episode plus a release log when the gate exits. Paired with
    // _combatOverrideGuid (self-defense latch) and _partyAssistMirrorGuid
    // (Fix L mirror latch) — all serve the same once-per-activation logging
    // pattern.
    private int _combatGateGuid;

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
        AssistStatusProvider assistStatusProvider,
        BlacklistRecheckCache recheckCache,
        LeaderConnectionStatus leaderConnection,
        BlacklistRecheckOperation recheckOperation)
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
        this.leaderConnection = leaderConnection;
        this.recheckOperation = recheckOperation;
        this.assistStatusProvider = assistStatusProvider;
        this.recheckCache = recheckCache;

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
            // ── Fix FQ (run-163) — require in-range target to engage ──
            //
            // Previously absent from the AssistFocus branch (Grind has it
            // at line ~188), which meant Combat ran even when the assist
            // was 40+ y from the focus chain target it was trying to
            // engage — the assist locked into the "Taking damage but not
            // within combat range of focus target" branch at line ~1205,
            // spinning Tab for 15 s+ while taking caster fire (run-163
            // AC=12:24:02→17 at <952, 291.6>). With this precondition,
            // Combat exits when out of attack range; FFG takes over via
            // the new assistshouldfollow disjunct (GoapAgent.cs ~line
            // 1916) and navigates the assist toward the leader. Once
            // close enough, incombatrange flips true and Combat re-fires
            // — assist joins the leader's engagement via Fix FJ.
            //
            // For Priest (the run-163 assist class) WithinCombatRange
            // checks Priest_Smite at ~30 y range (SpellInRange.cs:168).
            // CW hysteresis (1500 ms grace, published at GoapAgent
            // ~line 800) prevents flicker at the boundary so Combat
            // doesn't oscillate as the assist moves through the edge
            // of range. Mirrors the gating ATG.AssistFocus already uses.
            //
            // ── Fix GS (run-173) — REMOVES Fix FQ's incombatrange gate ──
            //
            // Operator directive following run-173 22:14:00-22:14:50 deadlock
            // (see Fix GR delivery for the full trace): "we need to make
            // sure both bots can get into combat goal — the ability for
            // both bots to enter combat if a mob was attacking us even
            // if we had an invalid target."
            //
            // Run-173 failure path: assist took proximity aggro at
            // 22:14:00:108 while its target slot still pointed at the
            // corpse from the prior kill (consume/loot/follow chain).
            // State at 22:14:03:691:
            //   partymembercombat=True   incombatrange=False
            //   targetisalive=False      damagetaken=True
            // Fix FQ's `incombatrange=true` precondition meant Combat
            // could NOT fire — there was no live in-range target to
            // engage. The other AssistFocus-pool goals also rejected
            // the state (ATG needs targetisalive, AssistFocusGoal needs
            // a castable spell key, TargetFocusTargetGoal needs
            // incombat=false). Planner produced NO PLAN, assist froze
            // 47 s and died.
            //
            // The run-163 design pattern (Combat exits when out of
            // range → FFG navigates to leader → Combat re-fires) assumed
            // that the assist's "invalid target" reason is "focus chain
            // target out of range" and that motion toward the leader
            // resolves the range gap. In run-173 the reason is "stale
            // dead-target slot from prior kill" — motion toward the
            // leader does NOT resolve it (the corpse stays where it
            // died; incombatrange-vs-corpse can never become true).
            //
            // Fix GS reverts the AssistFocus precondition set to its
            // pre-FQ state. CombatGoal can now fire whenever the assist
            // is in combat, regardless of current-target validity. The
            // goal's existing runtime handles invalid-target cases:
            //   - dead target slot → PressClearTarget + lost-target branch
            //   - no target → FindPossibleThreats → PressNearestTarget(Tab)
            //   - focus chain swap → PressTargetFocus + PressTargetOfTarget
            // These paths existed before Fix FQ and were the system's
            // self-defense mechanism. Fix FQ inadvertently gated all of
            // them off when the bot was out of range.
            //
            // The run-163 Tab-spin regression Fix FQ was added to
            // prevent (15s of caster fire while spinning Tab) may
            // re-surface in similar long-range caster scenarios. If it
            // does, the proper fix is at the CombatGoal RUNTIME side
            // (bound the Tab-spin loop, fall through to FFG after N
            // attempts) — not at the precondition. Restoring the entry
            // path takes priority because a dead assist contributes
            // zero combat and zero loot/skin, while a Tab-spinning
            // assist at least continues to threaten the attacker.
            //
            // FFG can still take over when the assist genuinely needs
            // to follow the leader rather than engage: GoapAgent's Fix FQ
            // disjunct on `assistshouldfollow` (kept intact, plus Fix GR
            // for the dead-target case) makes FFG eligible when in-combat
            // and unable to engage. CombatGoal's cost (4f) is below FFG
            // (19f), so when both are eligible the planner picks Combat —
            // matching the operator's design intent.
            // AddPrecondition(GoapKey.incombatrange, true);   // ← Fix FQ, removed by Fix GS
        }
        else if(classConfig.Mode == Mode.PartyLeader)
        {
            // ── Fix GS (run-173) — broaden trigger from leader-self to party ──
            //
            // Run-173 also exposed the symmetric leader-side block: leader's
            // CombatGoal required `partyleadercombat=true` (= the leader
            // itself in combat). With the assist proximity-aggroed and the
            // leader patrolling, partyleadercombat=False and partymembercombat=True.
            // The leader observed `partymembercombat=True` but had no goal
            // that could fire on that signal alone — leader's ATG needs
            // `assistisfollowing=true` (assist is frozen, can't follow),
            // FollowRouteGoal needs `assistrequestreturnorisfollowing=true`
            // (also false because assist is neither following nor requesting
            // return). NO PLAN at 22:14:08:602 leader-clock.
            //
            // Switch the trigger from `partyleadercombat=true` to
            // `partyincombat=true`. partyincombat is the OR of leader-combat
            // and party-member-combat (already computed in UpdateWorldState
            // as `PartyInCombat()`), so the leader's CombatGoal now fires
            // whenever ANY party member is in combat. The leader's CombatGoal
            // runtime handles the "no own target but party is in combat"
            // case via the focus chain (assist's target) and FindPossibleThreats
            // / PressNearestTarget — same fallback the assist's branch uses.
            // When the proximity-aggro mob attacking the assist comes into
            // the leader's aggro range (as the assist moves toward the leader
            // via Fix GR's FFG fallback OR as the mob's chase brings it
            // closer), the leader will Tab-acquire it and engage.
            AddPrecondition(GoapKey.partyincombat, true);
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

    /// <summary>
    /// Fix GE amendment (run-168, phase E) — mobConfirmedOutside check.
    /// Mirror of GoapAgent.MobConfirmedOutsideForGE; see that doc-comment
    /// for full rationale. Both files use this to override Fix GE's strict
    /// suppression so the assist can engage alongside the leader during the
    /// leader's EngageWindow phase.
    ///
    /// Leader-local check: cache verdict NotInRect.
    /// Assist check: leader's published BacktrackAggressorGuid + !BacktrackAggressorInRect.
    /// </summary>
    private bool MobConfirmedOutsideForGE(int guid)
    {
        if (guid == 0)
            return false;

        if (classConfig.Mode == Mode.PartyLeader)
        {
            RecheckVerdict v = recheckCache.GetVerdict(guid);
            return v == RecheckVerdict.NotInRect;
        }
        else if (classConfig.Mode == Mode.AssistFocus)
        {
            if (!leaderConnection.HasValidLeaderState)
                return false;
            LeaderState? ls = leaderConnection.LastLeaderState;
            if (ls == null)
                return false;
            return ls.BacktrackAggressorGuid == guid && !ls.BacktrackAggressorInRect;
        }
        return false;
    }

    public override void OnEnter()
    {
        // Target-tracking diagnostic (grep TARGET-GUID): live target guid at combat
        // entry, unconditional + Info-level (complements the existing "Target Changed
        // To" which only logs on a change).
        logger.LogInformation($"[Combat] OnEnter TARGET-GUID={playerReader.TargetGuid}");

        // ── Fix FK-DIAG site #1 — OnEnter prelude wait ──
        // Run-161 evidence: this site returned in ~7ms (LC=15:11:06:082 OnEnter
        // log → LC=15:11:06:089 ResetApproachEscape log), so it was NOT the
        // block point in that incident. Instrumented anyway because future
        // freezes could originate here. See WaitUpdateTimed comment block at
        // end of file for the full rationale.
        WaitUpdateTimed("OnEnter:prelude");
        stuckDetector.Reset();
        if (!navigation.IsApproachEscapeActive) navigation.ResetApproachEscape();

        if (mountHandler.IsMounted())
            mountHandler.Dismount();

        lastDirection = playerReader.Direction;
        input.PressDisableSoftInteract();
        // ── Fix FK-DIAG site #2 — OnEnter post-DisableSoftInteract wait ──
        // Run-161 evidence: PRIMARY suspect for the 16s freeze. The last log
        // before the 15s silence was the NumPad7 (DisableSoftInteract) press
        // at LC=15:11:06:159; the next log was LC=15:11:22:193 — exactly the
        // duration we'd expect if THIS wait.Update() blocked. If FK-DIAG fires
        // here in a future run, we have proof.
        WaitUpdateTimed("OnEnter:post-DisableSoftInteract");

        _stuckApproachingActive = false;
        _stuckApproachingSinceUtc = DateTime.MinValue;
        _stuckApproachingDamageSnapshot = 0;
        // Ghost combat fields and reset moved to GoapAgent (Fix BS).

        // ── Fix AR (log-79b 14:21:20:475 → 14:21:23:312) ──
        //
        // Reset per-session approach counters at the start of every Combat.
        // Without these resets, `consecutiveApproach` and `consecutiveNoAction`
        // carry over from the previous Combat session — these are private
        // CombatGoal instance fields that nothing else clears between
        // engagements (OnExit doesn't reset them; the only in-Update reset
        // paths require very specific conditions to fire).
        //
        // Evidence from log-79b first combat:
        //   14:21:20:475  Combat OnEnter (combat starts, fresh session).
        //   14:21:20:660  1st Approach (I key) press — consecutiveApproach
        //                 should be 1 in a clean session.
        //   14:21:21:123  2nd Approach press — should be 2.
        //   14:21:21:570  3rd Approach press — should be 3.
        //   14:21:21:585  *** First StuckDetector log fires *** —
        //                 "We are moving". The Update branch that emits
        //                 this message is gated on `consecutiveApproach
        //                 >= 5`. With only 3 fresh presses in THIS combat,
        //                 the counter must have been ≥ 2 at OnEnter for
        //                 the gate to be open. Carry-over from the prior
        //                 combat is the only path to that state.
        //   14:21:22:334  First "We aren't moving" — bot is genuinely
        //                 stalled (Approach key auto-walk failed to make
        //                 the bot reach the new mob; RecordApproach logs
        //                 show moved=0.00y across every press).
        //   14:21:22:387  Jump (Spacebar) — StuckDetector's first
        //                 recovery action.
        //   14:21:23:230  "Unstuck by turning for 68ms" + 14:21:23:312
        //                 "Unstuck by moving for 1483ms" — the
        //                 user-visible random-direction recovery: a
        //                 LeftArrow tap followed by 1.483 s of held
        //                 UpArrow taking the bot off its current heading.
        //
        // Why the previous session left the counter non-zero: the only
        // reset paths inside Update() are
        //   (a) consecutiveApproach = 0 in the "non-Approach cast" else
        //       branch — fires when a non-Approach key (e.g., an attack
        //       spell) is the one CastIfReady accepts this tick;
        //   (b) consecutiveApproach = 0 in the
        //       `consecutiveApproach >= 5 && IsInMeleeRange && DamageDone`
        //       branch — fires only after the bot is in melee range AND
        //       has dealt damage AFTER consecutiveApproach already hit 5;
        //   (c) consecutiveApproach = 0 in the geometry-trap timeout
        //       branch — fires after UnreachableMobTimeoutSec (18 s).
        // A prior combat that ended via an assist's killing blow while
        // the leader was still mid-Approach, or whose final action was
        // the Approach key itself, leaves the counter non-zero. The
        // counter persists across the Approach Target / Pull Target /
        // Follow / Combat plan transitions and into the next Combat
        // OnEnter.
        //
        // Why both counters: `consecutiveNoAction` has the symmetric
        // issue. Its only reset is `consecutiveNoAction = 0` inside the
        // successful-cast branch, and it gates a "press Interact after
        // 20 idle ticks with no damage done" branch. Carry-over could
        // cause a premature Interact at the start of a new combat —
        // smaller-visibility bug, but the same root cause and a trivial
        // co-fix.
        //
        // Why not `lastTargetGuid`: it's used purely as a "did the target
        // change since last tick?" diagnostic; carry-over only affects
        // whether the "Target Changed To: X" log fires on the first
        // tick of a new combat. No behavioral impact.
        consecutiveApproach = 0;
        consecutiveNoAction = 0;
    }

    public override void OnExit()
    {
        // ── Fix CC (audit finding from run-155 follow-up) ──
        //
        // Unconditional Forward release. The prior code only released via
        // `stopMoving.Stop()` when `DamageTakenCount() > 0 && !bits.Target()`
        // (line 277-278 below) — narrow case. Other exits (clean kill with
        // no incoming damage; plan transition while still holding target)
        // left Forward in its previous state.
        //
        // CombatGoal can have Forward held via:
        //   - ATG → Combat handoff (ATG.OnExit:392 releases, so this leg
        //     is normally clean — but if the transition happens via NO PLAN
        //     intermediate state, ordering is no longer guaranteed).
        //   - ReactCastError.cs:159 StartForward(true) on ERR_BADATTACKFACING
        //     when target is outside pull range. This path does NOT pair
        //     with a Stop().
        //   - Navigation.Update at line ~1859 if Combat triggers an internal
        //     navigate (rare).
        //
        // With Fix BR, navigation.Stop() releases Forward defensively, so
        // any Combat path that called Stop() is covered. The gap this
        // OnExit closes is the kill-no-damage case combined with a
        // ReactCastError-pressed Forward.
        //
        // Idempotency: IsKeyDown guard inside ConfigurableInput.StopForward.
        input.StopForward(false);

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
        // ── Fix FK-DIAG site #3 — Update entry wait ──
        // Fires every Update tick (~60Hz). If OnEnter completed normally but
        // the first Update tick blocks (e.g., addon stalls slightly later),
        // this site catches it. Overhead per call is sub-microsecond; the
        // log warning only fires on abnormal blocks (>5s). See WaitUpdateTimed
        // comment block at end of file.
        WaitUpdateTimed("Update:entry");

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

        // Fix M (log-59 11:44:54:457 → 11:45:40: leader engaged
        // non-blacklisted 316781 then 316729 entirely inside the BL rect;
        // chase via Approach-key presses carried leader from
        // <923.99, 266.94> at 11:44:48:947 (outside) → <962.92, 283.89>
        // at 11:44:58:068 (inside); NO blacklist event ever fired, both
        // mobs killed inside BL; user observed "blacklist areas are just
        // being ignored with combat happening normally"):
        //
        // ATG (line 294-315) and PTG (line 288-311) both have bot-inside-BL
        // bail-outs that dispatch EvadeBlacklistEvent + IgnoreTarget. Both
        // gate by !bits.Combat() — once the Combat plan is selected and
        // CombatGoal is running, neither fires. CombatGoal had no equivalent
        // check.
        //
        // If a chase (Approach key presses follow the target's position)
        // carries the bot across a BL rect boundary DURING combat, the bot
        // continues fighting normally inside BL: no 25 s recovery, no
        // retreat, no API broadcast to the assist. The entire BL mechanism
        // is bypassed for non-IsIgnored targets engaged from outside the rect.
        //
        // Fix: mirror ATG/PTG's BL bail-out inside CombatGoal with the
        // INVERSE combat gate (this fires DURING combat). Skip when target
        // is already IsIgnored — that's Fix 17 self-defense territory,
        // intentionally allowed to operate inside BL per Fix 22B's explicit
        // !IsInBlacklistArea() removal (see comment at line ~287 below).
        //
        // Pattern matches the existing CombatGoal evade-mob bail at
        // line ~555-568: PartyLeader-only EvadeBlacklistEvent dispatch
        // (assist receives via API broadcast), IgnoreTarget for both modes,
        // ClearStuckRects (defensive — same as line 562), StopAttack +
        // ClearTarget + stopMoving.Stop, return. Insertion point chosen so
        // this safety check fires before Fix 17's IsIgnored handling and
        // before Case 2/3 — if we're in BL with a regular target, bail
        // before any other logic runs.
        //
        // !navigation.IsApproachEscapeActive guard: defensive — don't
        // interrupt an in-progress approach escape. CombatGoal doesn't
        // run during ATG escape, but the cheap check provides robustness.
        // ── Fix GH (run-168, phase E) — Fix M cache integration (option (ii)) ──
        //
        // PREVIOUSLY (pre-Phase E): Fix M's trigger was bot-position-only —
        // `IsInBlacklistArea() && !IsIgnored(target)`. Fired AFTER the bot
        // had already chased into the rect. Also over-blacklisted when the
        // mob had walked back out during the chase (bot ended up
        // blacklisting an engageable mob).
        //
        // NOW (Phase E): augment with cache-based trigger. Logic (option ii):
        //   Fire IF: cache verdict definitively InRect for current target
        //            (engagement-time prevention, regardless of bot position)
        //   OR IF: bot inside BL AND cache verdict NOT definitively NotInRect
        //          (covers chase-into-rect AND no-cache-info cases, but NOT
        //           the "mob walked out while bot was inside" case where bot
        //           can continue combat and naturally chase the mob out).
        //
        // Equivalently: fire UNLESS (cache verdict NotInRect AND bot inside BL).
        //
        // With Fix GG's engagement-time recheck (now firing for ALL aggros),
        // the cache populates BEFORE the bot chases in. Net effect: blacklist
        // fires at engagement time when verdict says InRect — bot never
        // enters the rect.
        //
        // Known tradeoff (acceptable per design choice): if the bot crosses
        // into BL while the recheck for current target is still in flight
        // (Pending/SearchFailed), Fix GH bails and blacklists. If the
        // verdict would have resolved NotInRect a moment later, the mob is
        // over-blacklisted. The alternative — Fix GJ waits while bot is
        // inside BL — exposes the bot to damage in an unsafe area. The plan
        // chooses conservative bail.
        RecheckVerdict ghCacheVerdict =
            playerReader.TargetGuid != 0
                ? recheckCache.GetVerdict(playerReader.TargetGuid)
                : RecheckVerdict.Unknown;
        bool ghCacheSaysInRect = ghCacheVerdict == RecheckVerdict.InRect;
        bool ghCacheSaysNotInRect = ghCacheVerdict == RecheckVerdict.NotInRect;
        bool ghPositionTrigger = navigation.IsInBlacklistArea();

        bool ghShouldFire =
            ghCacheSaysInRect
            || (ghPositionTrigger && !ghCacheSaysNotInRect);

        if (ghShouldFire &&
            !navigation.IsApproachEscapeActive &&
            bits.Target() &&
            playerReader.TargetGuid != 0 &&
            !playerReader.IsIgnored(playerReader.TargetGuid))
        {
            string ghTriggerReason;
            if (ghCacheSaysInRect && ghPositionTrigger)
                ghTriggerReason = "BOTH cache-verdict-InRect AND bot-inside-BL";
            else if (ghCacheSaysInRect)
                ghTriggerReason = "cache-verdict-InRect (engagement-time prevention)";
            else  // ghPositionTrigger && !ghCacheSaysNotInRect
                ghTriggerReason = $"bot-inside-BL with non-definitive cache (verdict={ghCacheVerdict}; Fix M legacy path)";

            logger.LogWarning(
                $"[CombatGoal] [FIX-FIRE] GH: Fix M-augmented bail-out — trigger={ghTriggerReason}, " +
                $"guid={playerReader.TargetGuid}, botInsideBL={ghPositionTrigger}, " +
                $"cacheVerdict={ghCacheVerdict}, (Mode={classConfig.Mode})");

            // The inRect parameter for EvadeBlacklistEvent reflects the
            // authoritative verdict where available, falling back to the
            // passive read only if cache is Unknown.
            bool inRect = ghCacheSaysInRect ||
                          (ghCacheVerdict == RecheckVerdict.Unknown
                           && navigation.IsTargetLikelyInBlacklistRect());

            if (classConfig.Mode == Mode.PartyLeader && playerReader.TargetGuid != 0)
                SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid, EvadeReason.ReachabilityBail, inRect));
            playerReader.IgnoreTarget(playerReader.TargetGuid, inRect, isEvade: false);
            navigation.ClearStuckRects();
            input.PressStopAttack();
            wait.Update();
            input.PressClearTarget();
            wait.Update();
            stopMoving.Stop();
            return;
        }

        // ── Fix GJ (run-168, phase E) — Combat plan engagement gate for non-IsIgnored ──
        //
        // Fix GG (Phase E) triggers BeginRecheck for non-IsIgnored aggros at
        // engagement time. The operation takes ~700ms (Tab+Interact+Stop+
        // Settle+Read). During this window the cache verdict is Pending or
        // SearchFailed.
        //
        // The original Fix FY cache gate (fyMirrorCacheGatePasses, below
        // line ~700) gates only the IsIgnored selfDefenseOverride path. For
        // non-IsIgnored targets, no gate existed — CombatGoal would press
        // Approach + Attack keys while the operation pressed Tab + Stop +
        // Interact. Race condition.
        //
        // Fix GJ closes this: if current target is non-IsIgnored AND cache
        // verdict is Pending/SearchFailed (operation in flight), return
        // early — no engagement keys pressed this tick. Bot stays stationary
        // at engagement distance. Next tick, gate re-evaluates against the
        // now-resolved cache.
        //
        // Fix GH (above) wins ordering: if verdict resolves InRect, Fix GH
        // bails with blacklist BEFORE this gate fires. If verdict resolves
        // NotInRect, this gate doesn't fire and normal combat continues.
        //
        // Scope clarification: Fix GJ suppresses CombatGoal's engagement
        // keypresses (Approach/Attack) during the gate window. It does NOT
        // suppress the recheck operation's own keypresses (Tab/Interact/
        // Stop) — the operation runs as an IReader independent of goal
        // selection, ticking every addon frame. The operation owns the
        // Tab/Stop/Interact sequence; Fix GJ just prevents CombatGoal from
        // layering Approach/Attack on top.
        if (!currentTargetIsIgnored
            && bits.Target()
            && playerReader.TargetGuid != 0
            && bits.Combat()
            && !assistStatusProvider.EvadeRecoveryActive)
        {
            RecheckVerdict gjVerdict = recheckCache.GetVerdict(playerReader.TargetGuid);
            if (gjVerdict == RecheckVerdict.Pending
                || gjVerdict == RecheckVerdict.SearchFailed)
            {
                if (_combatGateGuid != playerReader.TargetGuid)
                {
                    _combatGateGuid = playerReader.TargetGuid;
                    logger.LogInformation(
                        $"[CombatGoal] [FIX-FIRE] GJ: Engagement gate — non-IsIgnored " +
                        $"target guid={playerReader.TargetGuid} has cache verdict={gjVerdict} " +
                        $"(recheck operation in flight). Suppressing engagement keys until " +
                        $"verdict resolves. (Mode={classConfig.Mode})");
                }
                return;
            }
            else if (_combatGateGuid != 0
                     && _combatGateGuid == playerReader.TargetGuid)
            {
                logger.LogInformation(
                    $"[CombatGoal] Fix GJ gate released for guid={_combatGateGuid} " +
                    $"(verdict now {gjVerdict}).");
                _combatGateGuid = 0;
            }
        }

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

        // ── Fix GE (run-168, phase D) — Self-backtrack state (mode-aware) ──
        //
        // Used by BOTH the Fix 17 self-defense override (below ~line 664) AND
        // the Fix L mirror (below ~line 875) to suppress IsIgnored-mob
        // engagement during backtrack. Declared once here so both sites can
        // reference the same value. See GoapAgent.cs for full rationale.
        //
        // Mode-aware:
        //   PartyLeader → navigation.IsBacktrackingActive (own FRG state).
        //   AssistFocus → leader's published IsBacktracking (coordinated
        //                 backtrack mirror per Fix GC design line 229-247).
        //   Other modes → false.
        bool selfInBacktrackForGE;
        if (classConfig.Mode == Mode.PartyLeader)
        {
            selfInBacktrackForGE = navigation.IsBacktrackingActive;
        }
        else if (classConfig.Mode == Mode.AssistFocus)
        {
            selfInBacktrackForGE =
                leaderConnection.HasValidLeaderState &&
                leaderConnection.LastLeaderState != null &&
                leaderConnection.LastLeaderState.IsBacktracking;
        }
        else
        {
            selfInBacktrackForGE = false;
        }

        // E4: don't let self-defense chase a mob determined to be inside a blacklist
        // rect at blacklist time (CombatGoal closes distance via direct Approach
        // presses, so without this the bot would walk INTO the rect after the
        // attacker). Per-GUID verdict (PlayerReader.IsNoEngage) riding the IsIgnored
        // TTL — no per-tick position read, no facing required. Kept in lockstep with
        // the planner mirror at GoapAgent:1259.
        bool targetNoEngage = playerReader.IsNoEngage(playerReader.TargetGuid);
        // Position rule (operator-directed; lockstep with GoapAgent): self-defense
        // allowed when OUTSIDE every static rect, OR inside one but DECLARED STUCK
        // (escape physically wedged / exhausted) — RESTORATION_LIST_AB2.md §F survival
        // path. Inside the rect and NOT stuck → suppressed → retreat (escape-first).
        // dmgTaken + TargetTarget==Me below already gate "actively under attack".
        // Section D (caster retreat) — engage decision half, lockstep with GoapAgent:
        // outside the rect, only engage a REACHABLE attacker. A target reading as inside
        // the rect is an unreachable in-rect caster; engaging bounces us at the edge, so
        // suppress and let retreat take over. declaredStuck (survival) overrides.
        //
        // Run-146 caveat (matches GoapAgent caveat verbatim): IsTargetLikelyInBlacklistRect
        // inflates the rect by 6y, which catches mobs hugging the OUTSIDE edge — exactly
        // the "mob walked out to melee us" case. Override: a target in melee range
        // (MaxRange in [1,5]) is reachable without entering the rect, so the rect
        // verdict doesn't apply. At range (>5y) the rect verdict still applies.
        int combatMeleeProbe = playerReader.MaxRange();
        bool combatTargetInMelee = combatMeleeProbe > 0 && combatMeleeProbe <= 5;
        bool engageAllowed =
            navigation.IsApproachEscapePhysicallyStuck
            || navigation.IsApproachEscapeExhausted
            || (!navigation.IsInBlacklistArea()
                && (combatTargetInMelee || !navigation.IsTargetLikelyInBlacklistRect()));
        // ── Fix EZ (run-151 evidence) — engageAllowedForJoin (lockstep with
        // GoapAgent.cs around line 1424-1480) ──
        //
        // `engageAllowed` above couples two gates:
        //   (a) bot's geographic safety (IsInBlacklistArea, declaredStuck)
        //   (b) the bot's OWN current target being reachable (combatTargetInMelee
        //       || !IsTargetLikelyInBlacklistRect)
        // Self-defense (Fix 17 at line ~492) genuinely needs both — the bot already
        // holds a target. But the Fix L party-assist mirror (line ~651) runs BEFORE
        // Case 2 has acquired a target from the focus chain: the bot may have no own
        // target, in which case MaxRange()=0 → IsTargetLikelyInBlacklistRect()
        // returns degenerate-true (Navigation.cs P3 branch) → engageAllowed=false →
        // mirror doesn't fire → Case 3 (line ~715) bails with both ignored → Combat
        // plan exits → back to NO PLAN. The leader sits while the assist fights the
        // partner-side IsIgnored mob alone — exactly the run-151 failure.
        //
        // The Position rule comment at line ~646-650 already states the correct
        // intent: "join the partner's fight only when this bot may itself engage —
        // outside the rect, or inside but declared stuck". That is (a) only.
        // engageAllowedForJoin encodes exactly this: no target-position component.
        //
        // After Fix L's mirror flips focusTargetIsIgnored=false (and currentTarget
        // IsIgnored=false when thisBotTargetMatchesFocus), CombatGoal's Case 2 swap
        // at line ~729 still depends on currentTargetIsIgnored=true to fire on the
        // first tick (when this bot has no own target yet), so the FIRST tick after
        // CombatGoal selection sees: currentTargetIsIgnored=true (no own target),
        // focusTargetIsIgnored=false (after mirror flip), thisBotTargetMatchesFocus
        // =false (no own target). Case 2's `currentTargetIsIgnored && isPartyMode
        // && !focusTargetIsIgnored` evaluates true → swap fires → PressTargetFocus
        // + PressTargetOfTarget acquires the focus's target (the partner's IsIgnored
        // mob). On the next tick the bot now owns the target; the regular
        // engageAllowed (with targetInRect computed against the now-acquired target's
        // bracket) gates self-defense as designed.
        bool engageAllowedForJoin =
            navigation.IsApproachEscapePhysicallyStuck
            || navigation.IsApproachEscapeExhausted
            || !navigation.IsInBlacklistArea();

        // ── Fix FN (run-162) — backtrack engage signal ──
        //
        // FollowRouteGoal's backtrack state machine drives the leader through
        // a sequence of route waypoints in reverse. At each waypoint it faces
        // the IsIgnored caster and runs IsTargetLikelyInBlacklistRect. When
        // that verdict reads FALSE (mob has stepped outside the rect), FRG
        // sets navigation.BacktrackEngageGuid = targetGuid for THIS guid
        // only, signalling Fix 17 to engage on the next tick despite the
        // mob still being on the IsIgnored map (per the operator's call:
        // keep IsIgnored sticky, use a one-shot override for engagement).
        //
        // engageAllowed in the standard path can still be false on the
        // EngageWindow tick (the verdict at navigation.IsTargetLikelyIn
        // BlacklistRect() can flicker between ticks — P3 degenerate may
        // re-fire while FRG's evaluation snapshot showed P1/P2 miss).
        // btEngageOverride bypasses that flicker for the verified guid only.
        // Other IsIgnored GUIDs are unaffected — strictly per-guid scope.
        bool btEngageOverride =
            navigation.BacktrackEngageGuid != 0 &&
            navigation.BacktrackEngageGuid == playerReader.TargetGuid;

        // ── Fix FY (run-167) — Cache verdict gate (CombatGoal mirror) ──
        //
        // Mirror of the GoapAgent.cs Fix FY gate. See full rationale there.
        // CombatGoal's Fix 17 must consult the same recheck cache so the
        // planner-level override and the runtime gate stay in lockstep.
        // declaredStuck escape (engageAllowed via IsApproachEscapePhysically
        // Stuck/Exhausted) is already captured in engageAllowed above; the
        // cache gate adds the additional InRect/Pending/SearchFailed
        // suppression on top.
        //
        // PHASE C: also triggers active recheck if cache is Unknown, and
        // blocks engagement while Pending/SearchFailed (decision #12).
        int fyMirrorGuid = playerReader.TargetGuid;
        RecheckVerdict fyMirrorVerdict = recheckCache.GetVerdict(fyMirrorGuid);

        bool fyMirrorDeclaredStuck =
            navigation.IsApproachEscapePhysicallyStuck
            || navigation.IsApproachEscapeExhausted;

        // BeginRecheck trigger (mirror of GoapAgent's). Note that the planner
        // pass already typically beat us here, so this is mostly idempotent
        // (BlacklistRecheckOperation.BeginRecheck is a no-op when same guid
        // already in flight). Kept here so CombatGoal can also be the entry
        // point if it's selected without the planner pass on this tick.
        //
        // ── Fix GG (run-168, phase E) — engagement-time recheck for ALL aggros ──
        //
        // Mirror of GoapAgent.cs Fix GG. PREVIOUSLY the trigger required
        // `currentTargetIsIgnored` — only IsIgnored mobs got an engagement-
        // time recheck. NOW per design Decision #2 (line 311): recheck for
        // ALL aggros so Fix GH can consult the cache to bail BEFORE the
        // bot chases into a rect.
        //
        // Guards mirror GoapAgent (see comment there for full rationale):
        //   - bits.Target_Combat(): NEW for Fix GG. Filters out friendly-
        //     targeting cases and yellow neutrals (Target_Hostile would
        //     include both, Target_Combat is "target is in combat with us").
        if (fyMirrorVerdict == RecheckVerdict.Unknown &&
            bits.Target() && bits.Combat()
            && combatLog.DamageTakenCount() > 0
            && bits.Target_Combat()  // Fix GG: target is in combat with us
            && playerReader.TargetTarget is UnitsTarget.Me or UnitsTarget.Pet
            && !assistStatusProvider.EvadeRecoveryActive
            && fyMirrorGuid != 0)
        {
            recheckOperation.BeginRecheck(fyMirrorGuid);
            fyMirrorVerdict = recheckCache.GetVerdict(fyMirrorGuid);
            logger.LogInformation(
                $"[CombatGoal] [FIX-FIRE] GG: Engagement-time recheck for guid={fyMirrorGuid} " +
                $"(currentTargetIgnored={currentTargetIsIgnored}, botInsideBL={navigation.IsInBlacklistArea()}, " +
                $"targetCombat={bits.Target_Combat()}). Per design Decision #2: recheck for ALL " +
                $"aggros, not just IsIgnored.");
        }

        bool fyMirrorCacheInRect = fyMirrorVerdict == RecheckVerdict.InRect;
        bool fyMirrorCachePending = fyMirrorVerdict == RecheckVerdict.Pending;
        bool fyMirrorCacheSearchFailed = fyMirrorVerdict == RecheckVerdict.SearchFailed;
        bool fyMirrorCacheBlocking = fyMirrorCacheInRect || fyMirrorCachePending || fyMirrorCacheSearchFailed;
        bool fyMirrorCacheGatePasses = !fyMirrorCacheBlocking || fyMirrorDeclaredStuck;

        if (currentTargetIsIgnored && bits.Target() && bits.Combat()
            && combatLog.DamageTakenCount() > 0
            && playerReader.TargetTarget is UnitsTarget.Me or UnitsTarget.Pet
            && !assistStatusProvider.EvadeRecoveryActive
            && (!selfInBacktrackForGE                                            // Fix GE: don't engage IsIgnored mobs during backtrack
                || MobConfirmedOutsideForGE(playerReader.TargetGuid)             // Fix GE amendment (Phase E): unless mob is verifiably outside
                || fyMirrorDeclaredStuck)                                        // declaredStuck escape per operator rule
            && (engageAllowed || btEngageOverride)
            && fyMirrorCacheGatePasses)
        {
            logger.LogInformation(
                $"[CombatGoal] Self-defense override (Fix 17): target guid={playerReader.TargetGuid} " +
                $"is on IsIgnored but actively attacking us (TargetTarget={playerReader.TargetTarget}, " +
                $"playerCombat=true, dmgTaken=true, evadeRecovery=false, " +
                $"insideBlacklistArea={navigation.IsInBlacklistArea()}, noEngage={targetNoEngage}, latched={overrideAlreadyLatched}, " +
                $"btEngageOverride={btEngageOverride}, fyCacheVerdict={fyMirrorVerdict}) — " +
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
        else if (selfInBacktrackForGE && !fyMirrorDeclaredStuck
                 && !MobConfirmedOutsideForGE(playerReader.TargetGuid)  // Fix GE amendment: don't log SUPPRESSED when amendment would have overridden
                 && currentTargetIsIgnored && bits.Target() && bits.Combat()
                 && combatLog.DamageTakenCount() > 0
                 && playerReader.TargetTarget is UnitsTarget.Me or UnitsTarget.Pet
                 && !assistStatusProvider.EvadeRecoveryActive)
        {
            // Fix GE diagnostic for Fix 17 mirror — log once per backtrack-
            // suppression event. Latches on _combatOverrideGuid to prevent
            // log spam. Fires when the bot is in a backtrack state (own FRG
            // or coordinated mirror via leader's published IsBacktracking)
            // and the IsIgnored mob would otherwise have been engaged via
            // Fix 17 self-defense. Per operator rule (2026-06-06): retreat
            // takes priority unless the mob is verifiably outside the rect
            // (BacktrackEngageGuid path) or the bot is declaredStuck.
            if (_combatOverrideGuid != playerReader.TargetGuid)
            {
                _combatOverrideGuid = playerReader.TargetGuid;
                string selfBacktrackSource = classConfig.Mode == Mode.PartyLeader
                    ? "navigation.IsBacktrackingActive=true (own FRG state)"
                    : "leader.IsBacktracking=true (coordinated backtrack via Fix GA)";
                logger.LogInformation(
                    $"[CombatGoal] [FIX-FIRE] GE: Fix 17 mirror SUPPRESSED for target " +
                    $"guid={playerReader.TargetGuid}  this bot is itself in active backtrack " +
                    $"({selfBacktrackSource}). Engaging IsIgnored mob during backtrack would " +
                    $"defeat the retreat's purpose; staying suppressed so FRG (leader) or " +
                    $"coordinated retreat (assist) can complete its waypoint navigation. " +
                    $"(Mode={classConfig.Mode})");
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
        //   - isPartyMode (PartyLeader OR AssistFocus) — log-60 expansion;
        //     previously AssistFocus-only, but the leader needs the same
        //     mirror when the assist is in Fix 17 self-defense
        //   - focusTargetIsIgnored (raw IsIgnored map says yes)
        //   - bits.FocusTarget() (focus has a target)
        //   - bits.FocusTarget_Combat() (focus target is in combat — only
        //     happens via partner's Fix 17 for IsIgnored targets)
        //   - FocusTargetGuid != 0 (defensive)
        //   - !assistStatusProvider.EvadeRecoveryActive (preserve the
        //     "retreat during recovery" intent; only activate after
        //     recovery elapses)
        //
        // Behavior:
        //   - Always flip focusTargetIsIgnored=false when conditions hold,
        //     so Case 2 fires (initial swap) and Case 3 doesn't fire
        //     (subsequent ticks)
        //   - If this bot's current target ALSO equals focus target
        //     (post-swap), additionally flip currentTargetIsIgnored=false
        //     for runtime consistency with Fix 17 (line 325)
        //   - Log once per new GUID activation (_partyAssistMirrorGuid latch)
        //
        // FFG projection NOT changed: CombatGoal uses direct Approach key
        // presses (line ~570 PressApproachOnCooldown) which bypass FFG
        // entirely. The bot physically walks into BL alongside the
        // partner while Combat runs. When the mob dies, CombatGoal exits
        // naturally, plan returns to FFG, FFG's projection re-engages,
        // and the assist navigates back out of BL.
        // ── Fix FZ (run-167) — Partner-backtrack cascade-break (CombatGoal mirror) ──
        //
        // Mirror of GoapAgent.cs Fix FZ gate. See full rationale there.
        // Both gates must lockstep so the planner-level decision and the
        // runtime decision agree. Reads partner state via Fix GA's published
        // BacktrackAggressorGuid / BacktrackAggressorInRect; suppresses the
        // Fix L mirror when partner is actively backtracking from the
        // current focus guid.
        bool partnerHasInRectBacktrackForFixZ = false;
        if (isPartyMode && playerReader.FocusTargetGuid != 0)
        {
            if (classConfig.Mode == Mode.PartyLeader)
            {
                foreach (var assistState in assistStateStore.GetAll())
                {
                    if (assistStateStore.IsStale(assistState)) continue;
                    if (assistState.IsBacktracking &&
                        assistState.BacktrackAggressorGuid == playerReader.FocusTargetGuid &&
                        assistState.BacktrackAggressorInRect)
                    {
                        partnerHasInRectBacktrackForFixZ = true;
                        break;
                    }
                }
            }
            else // AssistFocus
            {
                LeaderState? ls = leaderConnection.HasValidLeaderState ? leaderConnection.LastLeaderState : null;
                if (ls != null &&
                    ls.IsBacktracking &&
                    ls.BacktrackAggressorGuid == playerReader.FocusTargetGuid &&
                    ls.BacktrackAggressorInRect)
                {
                    partnerHasInRectBacktrackForFixZ = true;
                }
            }
        }

        // ── Fix GE (run-168, phase D) — Self-backtrack cascade-break (CombatGoal Fix L mirror) ──
        //
        // RENAMED from misnamed "Fix GD" 2026-06-06: see GoapAgent.cs for
        // the full rationale and renaming justification (line ~1989). The
        // Fix GD label was already reserved in run167-fix-plan.md lines
        // 255-267 for a vestigial FV revert.
        //
        // Mirror of GoapAgent.cs Fix GE gate. When SELF is in any backtrack
        // state (own FRG-driven on leader, or coordinated mirror on assist
        // via leader's published IsBacktracking), suppress the Fix L mirror
        // so the planner-level decision and the runtime decision stay in
        // lockstep. Without this, GoapAgent's Fix L might be suppressed by
        // Fix GE but CombatGoal's mirror might still flip
        // currentTargetIsIgnored on a different tick, causing the planner-
        // runtime mismatch that flickers Combat plan in/out of selection.
        //
        // selfInBacktrackForGE was declared earlier in Update() (near
        // overrideAlreadyLatched) so both the Fix 17 self-defense gate and
        // this Fix L mirror gate use the same value.

        // Position rule (operator-directed; lockstep with GoapAgent Fix L + the
        // self-defense gate): join the partner's fight only when this bot may itself
        // engage — outside the rect, or inside but declared stuck (engageAllowed,
        // defined above). Inside + not stuck → retreat instead of joining. Replaces the
        // run-144 per-GUID !IsNoEngage gate.
        if (isPartyMode &&
            focusTargetIsIgnored &&
            bits.FocusTarget() &&
            bits.FocusTarget_Combat() &&
            playerReader.FocusTargetGuid != 0 &&
            engageAllowedForJoin &&
            !assistStatusProvider.EvadeRecoveryActive &&
            !partnerHasInRectBacktrackForFixZ &&    // Fix FZ mirror: don't cascade onto partner's backtrack
            (!selfInBacktrackForGE                  // Fix GE mirror: don't fire while self is backtracking (own or coordinated)
             || MobConfirmedOutsideForGE(playerReader.FocusTargetGuid)))  // Fix GE amendment (Phase E): unless mob is verifiably outside the rect
        {
            bool thisBotTargetMatchesFocus =
                bits.Target() &&
                playerReader.TargetGuid != 0 &&
                playerReader.TargetGuid == playerReader.FocusTargetGuid;

            if (_partyAssistMirrorGuid != playerReader.FocusTargetGuid)
            {
                _partyAssistMirrorGuid = playerReader.FocusTargetGuid;
                // Fix R companion (log-62 18:20:37:673): the original message
                // unconditionally said "Case 2 swap will fire next" whenever
                // this bot's target didn't match focus, but Case 2 (line ~601)
                // also requires currentTargetIsIgnored=true. When the bot has
                // a fightable non-IsIgnored target of its own (e.g., add-mob
                // 340571 aggro'd onto the assist in log-62), Case 2 doesn't
                // fire — this bot stays on its own target. Reflect the actual
                // currentTargetIsIgnored state so the log isn't misleading.
                string caseClause;
                if (thisBotTargetMatchesFocus)
                {
                    caseClause = " AND currentTargetIsIgnored=false (this bot's target matches focus post-swap)";
                }
                else if (currentTargetIsIgnored)
                {
                    caseClause = $" (this bot's target guid={playerReader.TargetGuid} is on IsIgnored and doesn't match focus — Case 2 swap will fire next)";
                }
                else
                {
                    caseClause = $" (this bot's target guid={playerReader.TargetGuid} is NOT on IsIgnored — Case 2 won't swap; this bot will keep engaging its own target while planner-level focusTargetIsIgnored=false lets Combat plan run for it)";
                }
                logger.LogInformation(
                    $"[CombatGoal] Fix L party-assist override (mirror): focus target " +
                    $"guid={playerReader.FocusTargetGuid} is on IsIgnored but the " +
                    $"partner ({(classConfig.Mode == Mode.PartyLeader ? "assist" : "leader")}) " +
                    $"is in combat with it. Flipping focusTargetIsIgnored=false" +
                    caseClause +
                    $" so Case 3 won't bail. (Mode={classConfig.Mode})");
            }

            focusTargetIsIgnored = false;
            if (thisBotTargetMatchesFocus)
            {
                currentTargetIsIgnored = false;
            }
        }
        else if (selfInBacktrackForGE
                 && !MobConfirmedOutsideForGE(playerReader.FocusTargetGuid)  // Fix GE amendment: don't log SUPPRESSED when amendment would have overridden
                 && focusTargetIsIgnored && bits.FocusTarget()
                 && bits.FocusTarget_Combat() && playerReader.FocusTargetGuid != 0)
        {
            // Fix GE mirror diagnostic — log once per backtrack-suppression event.
            // Latches on _partyAssistMirrorGuid (paired with GoapAgent's
            // _partyAssistOverrideGuid). If both Fix GE (self-backtrack) and
            // Fix FZ (partner-backtrack) gates would fire, Fix GE is logged
            // first (self-state is the closer cause).
            if (_partyAssistMirrorGuid != playerReader.FocusTargetGuid)
            {
                _partyAssistMirrorGuid = playerReader.FocusTargetGuid;
                string selfBacktrackSource = classConfig.Mode == Mode.PartyLeader
                    ? "navigation.IsBacktrackingActive=true (own FRG state)"
                    : "leader.IsBacktracking=true (coordinated backtrack via Fix GA)";
                logger.LogInformation(
                    $"[CombatGoal] [FIX-FIRE] GE: Fix L mirror SUPPRESSED for focus guid={playerReader.FocusTargetGuid}  " +
                    $"this bot is itself in active backtrack ({selfBacktrackSource}). " +
                    $"Engaging during backtrack would defeat the retreat's purpose; staying suppressed " +
                    $"so FRG (leader) or coordinated retreat (assist) can complete its waypoint navigation. " +
                    $"(Mode={classConfig.Mode})");
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

        // ── Fix FJ (run-161 LC=15:11:26:328 → 15:11:35:090) ──
        //
        // Run-161 final combat session evidence — leader is PartyLeader,
        // focus is the assist:
        //
        //   LC=15:11:26:273  Kill credit detected (mob 2229960 dies — the
        //                    assist's smite/SW:Pain damage finished it; the
        //                    leader was frozen 15s before and contributed
        //                    nothing offensive to this kill).
        //   LC=15:11:26:328  CombatGoal "Lost target!" (line 1216).
        //   LC=15:11:26:374  "Possible threats 2!" (line 1481) +
        //                    PressNearestTarget(Tab) fires (FindPossibleThreats
        //                    line ~1320). Tab is async — keypress takes ~50ms
        //                    OS → ~variable WoW client → next addon GlobalTime
        //                    tick before bits.Target() reflects the new GUID.
        //   LC=15:11:29:406  Case 2 (this block) fires:
        //                       "Current target guid=0 is ignored —
        //                        swapping to focus chain target guid=2229960"
        //                    But 2229960 is dead (the leader saw kill credit
        //                    for it at LC=15:11:26:273). The leader's
        //                    FocusTargetGuid is stale because the assist had
        //                    looted but the assist's status broadcast hadn't
        //                    propagated a refreshed focus state yet.
        //                    PressTargetFocus + PressTargetOfTarget → target
        //                    becomes 2229960 (dead) or 0; WoW immediately
        //                    clears dead target slots.
        //   LC=15:11:29:544  "Lost target!" / "Clear current dead target!"
        //                    (lines 1216, 1225).
        //   LC=15:11:29:606  Search Possible Threats → FindPossibleThreats
        //                    line 1280 correctly skips "Found new combat
        //                    target of focus" because that path gates on
        //                    bits.FocusTarget_Alive() which is FALSE.
        //   LC=15:11:29:652  Tab pressed (PressNearestTarget).
        //   LC=15:11:29:653  Falls through to wait.Till(GCD*2 ≈ 3s) at
        //                    line 1482 waiting for alive target OR
        //                    !bits.Combat().
        //   LC=15:11:32:705  Cycle repeats — Case 2 fires AGAIN with the
        //                    same dead GUID 2229960, overwriting whatever
        //                    Tab finally selected during the 3s wait.
        //   LC=15:11:35:090  Plan transitions to Consume Corpse — second
        //                    mob died (the assist killed it during the
        //                    9-second swap loop the leader spent doing
        //                    nothing useful).
        //
        // Root cause: this block is the only focus-chain swap path in
        // CombatGoal.cs that does NOT gate on bits.FocusTarget_Alive().
        // Every parallel path does:
        //   line 969  (... && bits.FocusTarget_Alive() && bits.FocusTarget_Hostile())
        //   line 1052 (... && bits.FocusTarget_Hostile() && bits.FocusTarget_Alive())
        //   line 1058 (... && bits.FocusTarget_Alive() && bits.Focus_Combat())
        //   line 1221 (... && bits.FocusTarget() && bits.FocusTarget_Alive() && bits.FocusTarget_Hostile())
        //   line 1280 (... && bits.FocusTarget_Alive() && ...)
        //   line 1463 (... && bits.FocusTarget_Hostile() && bits.FocusTarget_Alive())
        //
        // The `!focusTargetIsIgnored` precondition only checks
        //   focusTargetIsIgnored = !bits.FocusTarget() || playerReader.IsIgnored(playerReader.FocusTargetGuid)
        // — a focus target that just died still has its slot populated
        // (bits.FocusTarget()=true) and isn't in the IsIgnored map
        // (IsIgnored=false), so the gate passes. The asymmetry was
        // benign while Case 2 only ran in the "bot auto-aggroed a
        // blacklisted mob, focus is fighting a live one" scenario it was
        // designed for (line 790-795 comment) — focus targets in that
        // scenario are alive by construction. The run-161 scenario
        // exposes it: bot has NO target (TargetGuid=0, which also makes
        // currentTargetIsIgnored=true via !bits.Target()), and the
        // focus's reported target just died. Case 2 was never meant for
        // "swap to whatever the focus is currently pointing at, even if
        // dead" — adding the alive gate restores the original intent.
        //
        // Fix: add `&& bits.FocusTarget_Alive()`. When the focus's target
        // is reported dead/stale, do not swap to it. Fall through to the
        // "Lost target!" / FindPossibleThreats path, which has the
        // matching alive gate at line 1280 and will correctly:
        //   - skip the focus-chain swap (FocusTarget_Alive=false)
        //   - run Tab cycling (PressNearestTarget)
        //   - wait via wait.Till for either alive-target or out-of-combat
        // Tab results that do bind are no longer overwritten by Case 2
        // firing again on the same dead GUID.
        //
        // What this does NOT change:
        //   - Case 2's original use case (auto-aggro onto blacklisted mob
        //     while focus has live fightable target): focus is alive, gate
        //     passes, swap fires identically.
        //   - Case 3 (line 774) bail behavior is untouched — that path
        //     already handles "nothing fightable in any party slot".
        //   - Fix L party-assist mirror (line 710) is upstream and
        //     untouched — it flips focusTargetIsIgnored=false when the
        //     party-assist override conditions hold; those conditions
        //     already include FocusTarget_Alive checks elsewhere.
        if (currentTargetIsIgnored && isPartyMode && !focusTargetIsIgnored
            && bits.FocusTarget_Alive())
        {
            // Case 2: bot's own target is blacklisted but focus chain has a
            // fightable LIVE mob. Swap: TargetFocus selects the focus unit
            // (party member), TargetOfTarget then selects what they're
            // fighting. WoW Classic sticky-targeting will keep us on that
            // GUID for the remainder of the fight (auto-aggro from Mob A
            // will not re-acquire because we already have a target).
            logger.LogInformation(
                $"[CombatGoal] [FIX-FIRE] FJ: Current target guid={playerReader.TargetGuid} " +
                $"is ignored — swapping to focus chain target guid={playerReader.FocusTargetGuid} " +
                $"(focus target alive: gate passed).");
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
            // Decision 2: carry the ACTUAL positional verdict (recurring evade mobs get a
            // static MapBlacklistRect, so they ARE in-rect). Same authoritative-verdict shape
            // as the Fix-GH/M ReachabilityBail path, re-derived with local names (the GH locals
            // are out of scope here). EvadePos captured while the target is still held.
            RecheckVerdict evCacheVerdict = recheckCache.GetVerdict(playerReader.TargetGuid);
            bool inRect = evCacheVerdict == RecheckVerdict.InRect
                          || (evCacheVerdict == RecheckVerdict.Unknown
                              && navigation.IsTargetLikelyInBlacklistRect());
            Vector3 evadePos = playerReader.TargetMapPos;
            SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid, EvadeReason.RealEvade, inRect, isEvade: true, evadePos: evadePos));
            playerReader.IgnoreTarget(playerReader.TargetGuid, inRect, isEvade: true, evadePos);
            navigation.ClearStuckRects();
            input.PressStopAttack();
            wait.Update();
            input.PressClearTarget();
            wait.Update();
            stopMoving.Stop();
            return;
        }

        // Ghost combat detection used to live here; it moved to
        // GoapAgent.CheckGhostCombat (called every planner tick from
        // NextGoal). The CombatGoal-scoped detector couldn't run during
        // the deadlock it was designed to catch: when both target slots
        // are IsIgnored/absent, CombatGoal's `allPartyTargetsIsIgnored=
        // false` precondition (line 119/127) blocks selection, so this
        // block was unreachable in the ghost state. The GoapAgent-scoped
        // detector runs regardless of selected plan, dispatching the
        // same EvadeBlacklistEvent(0, GhostCombat). See Fix BS in
        // GoapAgent.cs (the comment block near _ghostCombatActive
        // declaration) for the full run-156 evidence and rationale.

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
            // Fix EJ (Q4): physical unstuck (turn+move+jump) stays gated on IsMoving --
            // only nudge the character when it is actually stationary.
            if (!stuckDetector.IsMoving())
            {
                logger.LogInformation("StuckDetector: We aren't moving");
                stuckDetector.Update();
            }
            else
            {
                logger.LogInformation("StuckDetector: We are moving");
            }

            // Fix EJ (Q4): the geometry-trap (unreachable-mob) timer is now driven by
            // COMBAT PROGRESS (damage dealt), NOT physical movement. Previously this
            // whole timer block lived inside the if(!IsMoving()) branch above and the
            // else(IsMoving) reset it -- so a bounce against terrain (IsMoving==true but
            // no damage and no closing) wiped the timer every tick and an unreachable
            // mob was never disengaged (the same bounce-defeats-progress flaw Fix EG/EI
            // fixed for the route/approach escapes). The timer now accumulates across
            // thrashing and resets ONLY when DamageDoneCount increases (= the mob is
            // reachable). Damage-only rather than closest-approach because CombatGoal has
            // no precise distance-to-target metric -- only the coarse range-bracket
            // midpoint, which can read 0 and would falsely credit progress.
            // UnreachableMobTimeoutSec (18 s) is the backstop; the consecutiveApproach >= 5
            // gate on the enclosing if already requires repeated failed approaches.
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
                        playerReader.IgnoreTarget(playerReader.TargetGuid, inRect: false, isEvade: false);
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

                // Fix (run-147): self-defense preservation. Without this, the
                // Fix Q "Clearing target only" path below clears an IsIgnored
                // mob that is actively attacking us in melee — breaking the
                // self-defense override → Combat cycle.
                //
                // Run-147 evidence (leader 13:49:07 → 13:50:04, ~53s of
                // oscillation):
                //   - 13:49:07:793 PTG E5 blacklists guid=1531507 (in-rect=true).
                //   - 13:49:08:646 library logs "AreaBlacklistMob on attack!"
                //     (Blacklist.cs:94, IsIgnored precedence check).
                //   - 13:49:12:513 self-defense override fires correctly,
                //     Combat plan engages a DIFFERENT mob the leader was
                //     fighting alongside 1531507.
                //   - 13:49:22:855 the other mob dies — kill credit.
                //   - 13:49:22:855 [CombatGoal] Lost target! → Search
                //     Possible Threats!
                //   - 13:49:22:921 FindPossibleThreats reaches THIS branch.
                //     NearestTarget=1531507 (still alive, still hitting in
                //     melee). Fix Q path at line ~1390 fires (party mode +
                //     focus_combat) → PressClearTarget → target=0 → Combat
                //     plan precondition fails → Follow plan.
                //   - 13:49:23:833 1531507 keeps attacking → SoftInteract
                //     re-acquires → override re-fires at 13:49:24:388 →
                //     Combat → but FindPossibleThreats clears again on the
                //     next lost-target event → loop.
                // Operator had to manually intervene after 53 s.
                //
                // The planner-side override (GoapAgent:1336, run-145 fix) and
                // CombatGoal's own override (this file:487, run-146 fix) both
                // fire correctly — but FindPossibleThreats is a THIRD decision
                // point about IsIgnored targets that wasn't taught about
                // self-defense.
                //
                // Discriminator: same shape as the line-487 override —
                // TargetTarget=Me/Pet ensures THIS mob (not a random IsIgnored
                // we Tab'd onto) is the actual attacker, dmgTaken+playerCombat
                // confirm an active fight, !evadeRecoveryActive excludes the
                // 25 s window, engageAllowed (with melee override from
                // run-146) checks position rule reachability. EvadeMobs is
                // excluded — a game-reported evading mob should still escape.
                bool sdTargetIsIgnored = !combatLog.EvadeMobs.Contains(evadingGuid)
                                      && playerReader.IsIgnored(evadingGuid);
                if (sdTargetIsIgnored)
                {
                    int sdMeleeProbe = playerReader.MaxRange();
                    bool sdTargetInMelee = sdMeleeProbe > 0 && sdMeleeProbe <= 5;
                    bool sdEngageAllowed =
                        navigation.IsApproachEscapePhysicallyStuck
                        || navigation.IsApproachEscapeExhausted
                        || (!navigation.IsInBlacklistArea()
                            && (sdTargetInMelee || !navigation.IsTargetLikelyInBlacklistRect()));
                    if (combatLog.DamageTakenCount() > 0
                        && playerReader.TargetTarget is UnitsTarget.Me or UnitsTarget.Pet
                        && !assistStatusProvider.EvadeRecoveryActive
                        && sdEngageAllowed)
                    {
                        logger.LogInformation(
                            $"[CombatGoal] FindPossibleThreats: NearestTarget guid={evadingGuid} " +
                            $"is IsIgnored BUT actively attacking us in self-defense scope " +
                            $"(TargetTarget={playerReader.TargetTarget}, dmgTaken=true, " +
                            $"insideBlacklistArea={navigation.IsInBlacklistArea()}, " +
                            $"targetInMelee={sdTargetInMelee}) — preserving target so the " +
                            $"self-defense override (this file:487) can engage on the next tick. " +
                            $"Skipping Fix Q clear / EvadeBlacklist re-fire. (Mode={classConfig.Mode})");
                        return;
                    }
                }

                // Fix Q (log-61 16:37:32:478): suppress the evade re-fire when
                // the partner (focus) is in combat. The standalone Grind-mode
                // semantics of this block — "I stumbled onto a previously-
                // blacklisted mob, start a 25s retreat" — collide with the
                // party-assist scenario in a destructive way:
                //
                // 1. Fix L fires for guid X (partner engaging X via Fix 17).
                // 2. Bot's target = X (via Case 2 swap on TargetFocus +
                //    TargetOfTarget).
                // 3. A few seconds later, Fix L transiently clears — typically
                //    because playerReader.FocusTargetGuid is stale (WoW focus
                //    chain hadn't caught up with the partner's target update
                //    after a prior mob in the partner's target slot died).
                //    In log-61 the leader's FocusTargetGuid was still 334093
                //    (a dead non-IsIgnored mob) instead of 334431 (the actual
                //    mob the assist was fighting via Fix 17).
                // 4. Case 2 fires again with stale FocusTargetGuid → swap to
                //    a dead mob → Lost target → FindPossibleThreats fires.
                // 5. Tab acquires the nearest hostile — which is X (still alive,
                //    being fought by the partner near the bot).
                // 6. Without this gate: IsIgnored(X)=true → SendGoapEvent
                //    (EvadeBlacklistEvent(X)) → new 25s recovery window →
                //    BLOCKS Fix L from re-firing for the remaining ~19s of
                //    the partner's combat. User observed: "the leader stood
                //    there and did nothing" while the assist killed X alone.
                //
                // With the gate: when partner is in combat, we recognise that
                // this code path is mis-interpreting the situation and
                // suppress the EvadeBlacklistEvent dispatch. ClearTarget still
                // fires (we don't want to engage an IsIgnored target without
                // Fix L's explicit authorization), TargetGuid drops to 0,
                // Combat plan's precondition fails on next tick, plan re-
                // evaluates. If the focus chain has caught up and Fix L
                // conditions hold, Fix L re-fires for X via the normal path.
                // If not, the bot returns to Follow Route / FFG without the
                // 25s lockout.
                //
                // Mode gate uses the same isPartyMode predicate the rest of
                // the CombatGoal uses (Update() line 251). Standalone Grind
                // mode is unaffected: bits.Focus() returns false (no focus
                // set) so the gate short-circuits and the original behavior
                // is preserved exactly.
                bool isPartyMode = classConfig.Mode == Mode.PartyLeader
                                || classConfig.Mode == Mode.AssistFocus;
                if (isPartyMode && bits.Focus() && bits.Focus_Combat())
                {
                    logger.LogInformation(
                        $"[CombatGoal] FindPossibleThreats: NearestTarget guid={evadingGuid} " +
                        $"is evading/ignored AND partner (focus) is in combat — suppressing " +
                        $"EvadeBlacklistEvent re-fire so Fix L party-assist can re-fire when " +
                        $"focus chain catches up. Clearing target only. (Mode={classConfig.Mode})");
                    input.PressClearTarget();
                    wait.Update();
                    return;
                }

                logger.LogInformation($"[CombatGoal] FindPossibleThreats: NearestTarget guid={evadingGuid} is evading/ignored — re-firing evade escape.");
                input.PressClearTarget();
                wait.Update();
                stopMoving.Stop();
                SendGoapEvent(new EvadeBlacklistEvent(evadingGuid, EvadeReason.RealEvade));
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

        // Fix EV (run-149 11:51:59-11:52:09 leader+assist evidence):
        // PressNearestTarget at line ~1323 is async — the Tab keypress takes
        // time to propagate through the OS + WoW client + addon before
        // bits.Target() reflects the new GUID. The single wait.Update() at
        // line ~1324 is a Thread.Sleep(1)-class tick that is frequently
        // insufficient: the validation if at ~1327 reads bits.Target()==false,
        // the if-block (and its three explicit clear paths) is SKIPPED, and
        // the function falls through to this wait.Till. During the Till the
        // Tab DOES propagate, bits.Target() flips to the Tab'd GUID, and
        // either bits.Target_Alive() or !bits.Combat() satisfies the Till
        // predicate — wait.Till returns with a NEW hostile target SET that
        // CombatGoal never engaged.
        //
        // Run-149 evidence:
        //   11:51:59:632  "Search Possible Threats!" (line ~1248)
        //   11:51:59:632  "Checking target in front..." (line ~1322)
        //                  → input.PressNearestTarget() → Tab pressed
        //                    11:51:59:678 (32ms keypress)
        //   11:51:59:678  "Waiting for target to exist or lose combat.
        //                   Possible threats 2!" (this log)
        //                  → "Dont pull non-hostile target!" (line ~1494)
        //                    NEVER FIRES across the entire run (grep
        //                    confirmed) → proves the if at ~1327 was FALSE
        //                    (bits.Target() not yet reflecting Tab) and the
        //                    entire validation block was skipped.
        //   11:52:00:107  Left Combat after 5.00sec — !bits.Combat() became
        //                  true, Till predicate satisfied, Till returned.
        //   <Tab took effect during Till> leader's WoW client target =
        //                  guid 1607352 (a nearby hostile mob, not the
        //                  Combat target 1610440).
        // CombatGoal.OnExit (~line 279) does NOT clear target. Loot's
        // "Keyboard last target 1610440!" at 11:52:00:378 logs the intent
        // but no G keypress is actually in the input log between 11:51:59:678
        // and 11:52:03:286 (first G is Skinning's at 11:52:03:286). The
        // leaked target stayed set for ~3.6s — long enough for the assist's
        // FFG focus-chain (PressTargetFocus + PressTargetOfTarget, AH+AJ+AN)
        // at 11:52:02:306 to read leader's WoW target = 1607352 and latch
        // it. Assist committed to ATG/Combat on 1607352 (entered combat at
        // 11:52:08:572 — BEFORE the leader's own next Combat at 11:52:12:368
        // on a DIFFERENT mob 1610565). The operator observed this as "the
        // assist pulled a mob that the leader had targeted for a moment".
        //
        // GATE RATIONALE (each conjunct earns its place):
        //
        //   !bits.Combat()
        //     The bot is not currently being attacked. This single check
        //     already excludes BOTH self-defense pathways: the run-147
        //     preservation at line ~1385 requires DamageTakenCount > 0
        //     (which implies bits.Combat()=true), and the line-~492 self-
        //     defense override requires bits.Combat()=true explicitly.
        //     Neither override can fire while !bits.Combat() holds, so
        //     clearing here cannot disturb either preservation pathway —
        //     making the previous EvadeMobs/IsIgnored gates redundant.
        //     (Earlier draft of this fix included those gates; they
        //     filtered nothing the !bits.Combat() gate didn't already
        //     filter and were dropped per operator review.)
        //
        //   bits.Target()
        //     Without a target there is nothing to clear; ClearTarget on
        //     an already-empty slot is wasted input.
        //
        //   !bits.Target_Dead()
        //     Scopes EV strictly to the alive-leak case. Tab via
        //     PressNearestTarget cycles only ALIVE hostiles, so the leak
        //     is always alive. A dead target here is from prior Combat
        //     (a corpse), not the leak — leave it for Loot/Skinning.
        //
        //   !bits.Target_Combat()
        //     If the target is currently in combat with ANYONE (assist,
        //     other party member, NPC), leave it for the GOAP planner to
        //     evaluate next tick. The leader is NOT being attacked (the
        //     !bits.Combat() gate above), but the target is in an active
        //     fight — focus-chain Combat or PartyInCombat-gated ATG may
        //     legitimately pick it up. Clearing here would cost a tick of
        //     re-acquisition (focus-chain would have to re-read it on the
        //     following tick). Scopes EV strictly to the IDLE-leak case
        //     (Tab'd a hostile that's just standing there, attacking
        //     nobody) — exactly the run-149 1607352 scenario at 11:51:59
        //     where 1607352 was not yet in combat with anyone.
        if (!bits.Combat()
            && bits.Target()
            && !bits.Target_Dead()
            && !bits.Target_Combat())
        {
            logger.LogInformation(
                $"[CombatGoal] [FIX-FIRE] EV: FindPossibleThreats exit — " +
                $"line-1327 validation was SKIPPED (bits.Target() was false " +
                $"when checked, async Tab from line-1323 not yet propagated), " +
                $"then wait.Till returned with target (guid={playerReader.TargetGuid}) " +
                $"set by late Tab propagation. Bot is not in combat " +
                $"(bits.Combat()=false), target is alive (Target_Dead=false), " +
                $"target is not in combat with anyone (Target_Combat=false) — " +
                $"this is a leaked idle-Tab target. Clearing so it does not " +
                $"leak to subsequent plans (Loot/Skinning/Follow do not clear " +
                $"it) or to the assist focus-chain (PressTargetFocus + " +
                $"PressTargetOfTarget) which would latch it and pull.");
            input.PressClearTarget();
            wait.Update();
        }
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

    // ── Fix FK-DIAG (run-161 LC=15:11:06:159 → 15:11:22:193) ──
    //
    // Diagnostic instrumentation, NOT a fix. The run-161 leader log
    // showed 15 contiguous seconds of zero log lines from any source
    // (LC=15:11:07 through 15:11:21), bracketed by Combat OnEnter's
    // PressDisableSoftInteract (last log before silence) and a recovery
    // burst of activity (first log after silence). The natural blocking
    // points in that code path are wait.Update() calls — first at
    // OnEnter:191 (post-OnEnter-log), then at OnEnter:200 (post-
    // PressDisableSoftInteract), then at Update:316 (every Update tick).
    //
    // wait.Update() is a blocking call on a ManualResetEventSlim
    // (`globalTime`) that is only signaled by AddonReader.Update() at
    // /Core/Addon/AddonReader.cs:100 when the WoW addon's GlobalTime
    // counter advances. If the addon stalls, wait.Update() blocks
    // indefinitely. But there are other possibilities (chained pauses,
    // OS-level GC, code path I haven't traced) and the prior analysis
    // overstated certainty about the addon-stall hypothesis.
    //
    // This helper times each wait.Update() call at the three suspected
    // sites and logs a WARNING with state context if the call blocked
    // for >5000ms. Normal wait.Update() returns in ~15-30ms (one addon
    // tick at WoW's frame rate). 5000ms threshold:
    //   - Catches the 16s run-161 freeze (would have fired with
    //     elapsedMs ≈ 16034 at the OnEnter:200 site).
    //   - Catches any other abnormal block (3-10s, 30s+).
    //   - Does NOT false-positive on slow but normal ticks.
    //
    // Sites instrumented (semantic labels in the log message, not line
    // numbers — line numbers drift as the file grows):
    //   "OnEnter:prelude"           — wait.Update() at the top of OnEnter,
    //                                 right after the TARGET-GUID diagnostic.
    //   "OnEnter:post-DisableSoftInteract"
    //                               — wait.Update() after the NumPad7
    //                                 keypress. THIS is the suspect for
    //                                 the run-161 freeze.
    //   "Update:entry"              — wait.Update() at Update()'s first
    //                                 line. Fires every tick; if a freeze
    //                                 starts in Update rather than OnEnter,
    //                                 this site catches it.
    //
    // Overhead: Stopwatch.GetTimestamp() is a few hundred ns; the
    // greater-than comparison is one instruction. Net cost per call is
    // sub-microsecond. Update() runs at ~60Hz; total overhead is
    // <0.001% CPU. The branch into the LogWarning body is taken only on
    // abnormal blocks, so the log is never noisy in normal operation.
    //
    // What the next run's evidence tells us:
    //   - If FK-DIAG fires at a site → confirms wait.Update() blocked
    //     there. Cause is addon stall OR a chained pause that left the
    //     globalTime event unsignaled. The state context (bits.Combat,
    //     Target, FocusTarget) at the moment of block helps identify
    //     conditions.
    //   - If a freeze of >5s occurs but FK-DIAG does NOT fire → freeze
    //     is elsewhere (not OnEnter and not Update's first wait). Likely
    //     candidates would then be: a wait.Until/wait.Till loop later in
    //     Update, a wait.Fixed call in a sub-handler (CastingHandler,
    //     MountHandler), or an OS-level pause (GC).
    //   - If freezes recur consistently at OnEnter:post-DisableSoftInteract
    //     in particular → strong evidence the addon-stall hypothesis is
    //     correct AND specifically that DisableSoftInteract is sometimes
    //     followed by an addon-update lag.
    //
    // This diagnostic is not time-limited; it can stay in place
    // permanently. It only logs on abnormal events. If a future fix
    // resolves the underlying cause, FK-DIAG will simply stop firing.
    private void WaitUpdateTimed(string siteLabel)
    {
        long start = Stopwatch.GetTimestamp();
        wait.Update();
        double elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (elapsedMs > 5000)
        {
            logger.LogWarning(
                $"[CombatGoal] [FIX-FIRE] FK-DIAG: wait.Update() at {siteLabel} " +
                $"blocked for {elapsedMs:0}ms (>5000ms threshold). " +
                $"This is anomalous — normal wait.Update() returns in 15-30ms " +
                $"(one WoW addon GlobalTime tick). State at unblock: " +
                $"TargetGuid={playerReader.TargetGuid} " +
                $"bits.Combat={bits.Combat()} " +
                $"bits.Target={bits.Target()} " +
                $"bits.Target_Alive={(bits.Target() ? bits.Target_Alive().ToString() : "n/a")} " +
                $"bits.FocusTarget={bits.FocusTarget()} " +
                $"FocusTargetGuid={playerReader.FocusTargetGuid} " +
                $"Mode={classConfig.Mode}.");
        }
    }
}
