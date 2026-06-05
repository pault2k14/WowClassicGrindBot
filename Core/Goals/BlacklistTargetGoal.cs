namespace Core.Goals;

public sealed class BlacklistTargetGoal : GoapGoal
{
    public override float Cost => 2;

    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly ConfigurableInput input;
    private readonly Wait wait;
    private readonly IBlacklist targetBlacklist;
    private readonly Navigation navigation;

    public BlacklistTargetGoal(PlayerReader playerReader,
        AddonBits bits,
        ConfigurableInput input,
        IBlacklist blacklist,
        Navigation navigation,
        Wait wait)
        : base(nameof(BlacklistTargetGoal))
    {
        this.playerReader = playerReader;
        this.bits = bits;
        this.input = input;
        this.targetBlacklist = blacklist;
        this.navigation = navigation;
        this.wait = wait;
    }

    public override bool CanRun()
    {
        if (!bits.Target() || !targetBlacklist.Is())
            return false;

        // ── Fix FN (run-162) — caster-in-BL retreat backtrack ──
        //
        // When FollowRouteGoal is driving the leader through the backtrack
        // state machine (Navigating / Evaluating phases — see FRG ~line 1320+),
        // the target slot MUST stay occupied with the IsIgnored caster so we
        // can face it (PressInteract) and run IsTargetLikelyInBlacklistRect
        // at each backtrack waypoint. PressClearTarget here would drop the
        // tracking target and break the evaluation loop on every tick — same
        // pathology Fix FL/FM partially addressed for the pre-backtrack flap,
        // but more total here (entire backtrack collapses, not just one tick).
        //
        // The backtrack flag is set by FRG only while phase ∈ {Navigating,
        // Evaluating}. During EngageWindow it clears (the BacktrackEngageGuid
        // signal takes over via CombatGoal Fix 17). When backtrack exits
        // (success, mob killed, route exhausted, or combat dropped), the flag
        // clears and normal BL plan behavior resumes — including the existing
        // Fix FL self-defense alignment below.
        if (navigation.IsBacktrackingActive)
            return false;

        // ── Fix FB (run-153 17:07:49→17:08:19 — ~28 s oscillation) ──
        //
        // Without this carve-out the leader and a no-engage mob enter a
        // Combat ↔ Blacklist Target oscillation when the mob walks OUT
        // of its rect to attack in melee. Run-153 evidence:
        //
        //   17:07:31:197  ATG E5: target guid=1717604 (Razormane
        //                 Battleguard) detected inside a blacklist rect —
        //                 IgnoreTarget(guid, inRect:true). The InRect flag
        //                 makes playerReader.IsNoEngage(guid)=true.
        //   17:07:49:094  Razormane has walked OUT of the rect to the
        //                 leader. partyincombat broadcast fires; FRG
        //                 logs "Combat cannot run, continuing retreat
        //                 patrol uninterrupted." Plan = NO PLAN.
        //   17:07:49:101  CombatTracker Entered Combat.
        //   17:07:50:712  Fix 13 self-defense override fires
        //                 (GoapAgent.cs:1487-1510): target on IsIgnored,
        //                 TargetTarget=Me, dmgTaken=true, !evadeRecovery,
        //                 engageAllowed → flips WorldState
        //                 targetIgnored=false. Combat plan (cost 4) is
        //                 now eligible.
        //                 Same tick: Blacklist library re-logs
        //                 "AreaBlacklistMob on attack!" — AreaBlacklist's
        //                 existing carve-out at Blacklist.cs:67-75
        //                 (`combatLog.DamageTaken.Contains(guid) &&
        //                 !IsNoEngage(guid)`) FAILS because
        //                 IsNoEngage=true was latched at 17:07:31. So
        //                 targetBlacklist.Is() returns true via the
        //                 IsIgnored branch at line 90-103.
        //   17:07:50:712 →
        //   17:08:18:878  Planner picks Blacklist Target (cost 2) over
        //                 Combat (cost 4) every tick because both are
        //                 satisfied and Blacklist Target is cheaper.
        //                 OnEnter clears target → SoftInteract auto-
        //                 acquires → ClearTarget → loop. Fix 13 fired
        //                 17 times in this window. Combat lasted
        //                 17:07:49:101 → 17:08:27:884 = 38.78 s, of
        //                 which ~29 s was unproductive oscillation.
        //                 The leader took continuous melee damage
        //                 with no engagement; the assist couldn't
        //                 help because the leader's target was
        //                 cleared every cycle, breaking focus-chain
        //                 acquisition.
        //
        // The original Fix FB carve-out used `inMelee` as the
        // discriminator: when the no-engage mob is in melee range AND
        // targeting our side AND this bot is geographically allowed
        // to engage, step aside so the Combat plan can win. `inMelee`
        // captured "mob is reachable without entering the rect" by
        // proxying "mob walked OUT" (a melee mob in melee range from
        // a bot outside the rect must itself be outside or at the
        // rect edge). For melee scenarios this works.
        //
        // ── Fix FL (run-162 LC=01:39:21:933→01:40:01:418 — ~40 s ─
        //    flapping; user reported "the leader stood there not
        //    moving and not seeming to do much") ──
        //
        // Fix FB's inMelee discriminator fails for CASTER mobs in BL.
        // A caster attacks from inside its rect at range — `inMelee`
        // is false → Fix FB doesn't fire → BL plan stays eligible
        // (cost 2) and wins over Combat (cost 4) even when the
        // self-defense override has already declared engagement
        // viable.
        //
        // Run-162 evidence (leader-clock; +42.26s ahead of assist):
        //   LC=01:39:08:607  Leader kills mob 2267598 (last paired
        //                    kill — Session Total 132).
        //   LC=01:39:21:248  PullTargetGoal E5 gate fires for guid
        //                    2267577 (Deepmoss Venomspitter, a
        //                    caster): "target inside a blacklist
        //                    rect — blacklisting (in-rect) instead
        //                    of pulling." Evade event with
        //                    reason=ReachabilityBail. Rect 4007
        //                    spans [952,277 .. 999,327].
        //   LC=01:39:21:933  Plan flap starts: 14 cycles of
        //                    Blacklist Target ↔ Follow Route over
        //                    the next 40 s. Each BL OnEnter does
        //                    PressClearTarget; Tab side-thread /
        //                    SoftInteract re-acquires the caster
        //                    ~3 s later; repeat.
        //   LC=01:39:43:408  IsIgnored self-defense override fires
        //                    (1 of 5): TargetTarget=Me, playerCombat
        //                    =true, dmgTaken=true, evadeRecovery
        //                    =false, insideBlacklistArea=False,
        //                    latched=False. Same tick InRectVerdict
        //                    logs "P1 miss bracket=[10,15] estDist
        //                    =12.5 dir=5.88 player=<974.7954,
        //                    270.01782, 0> est=<985.82166, 265.28223,
        //                    0>" — the dead-reckoning estimate placed
        //                    the caster at Y=265.28 which is below
        //                    the rect's MinY=277 (even with inflate
        //                    6.0 → MinY=271). So IsTargetLikelyIn
        //                    BlacklistRect()=false → targetInRect
        //                    =false → engageAllowed=true → override
        //                    fires per GoapAgent.cs:1693-1698.
        //                    Planner picks Blacklist Target (cost 2)
        //                    1 ms later. ClearTarget. The override's
        //                    decision is wasted.
        //   LC=01:39:46:712,
        //          :51:603,
        //          :58:119,
        //   LC=01:40:01:324  Override fires 4 more times — same
        //                    shape, same outcome. Five unused
        //                    overrides across the window.
        //   LC=01:40:01:418  Combat finally wins (override fires on
        //                    a tick where bits.Target=false momentarily
        //                    after BL ClearTarget, so BL CanRun=false
        //                    and Combat wins by default). Combat then
        //                    flickers with BL another 6 cycles in
        //                    10 s until the user manually paused the
        //                    leader at LC=01:40:29.
        //
        // The asymmetry is precise: GoapAgent's IsIgnored self-defense
        // override at line 1693-1698 uses the full engageAllowed
        // formula (line 1630-1631):
        //     targetInRect  = !targetInMelee && IsTargetLikelyInBlacklistRect()
        //     engageAllowed = declaredStuck
        //                     || (!botInsideBlacklistArea && !targetInRect)
        // CombatGoal's Fix 17 (CombatGoal.cs:528-532) uses an
        // identical engageAllowed. Both fire when the per-tick rect
        // verdict says the target is reachable. The original Fix FB
        // used only `inMelee` — strictly weaker (inMelee is a
        // sufficient-but-not-necessary condition for the same engage
        // decision). So Fix FB's gate was over-restrictive and
        // failed to fire for ranged attackers, leaving BL plan
        // selectable on exactly the ticks Combat would have engaged.
        //
        // Fix FL: align Fix FB's gate with the override's
        // engageAllowed (and CombatGoal Fix 17's engageAllowed) so
        // BL plan yields on every tick the override fires, letting
        // Combat plan win. Combat's existing Fix 17 then takes over
        // engagement via the same conditions.
        //
        // Behavior preservation:
        //   • Run-153 (melee out of rect): inMelee=true →
        //     targetInRect=false → engageAllowed reduces to
        //     !botInsideBlacklistArea (which was true) → Fix FL
        //     fires → BL suppressed → identical to original Fix FB.
        //   • Run-162 P1-miss tick (caster reads outside rect):
        //     inMelee=false, IsTargetLikelyInBlacklistRect=false →
        //     targetInRect=false → engageAllowed=true → Fix FL
        //     fires → BL suppressed → Combat wins, Fix 17 fires
        //     (matching engageAllowed), engagement proceeds.
        //   • Run-162 P3-degenerate / P1-HIT tick (caster reads in
        //     rect or no estimate): targetInRect=true →
        //     engageAllowed=false → Fix FL does NOT fire → BL
        //     plan runs (status quo for that tick). The flap
        //     reduces on engageable ticks but doesn't vanish on
        //     unreachable-verdict ticks — that's the downstream
        //     caster-retreat work the user has flagged as next.
        //
        // Mirrors:
        //   • GoapAgent.cs:1630-1631 engageAllowed (planner-level
        //     mirror of self-defense viability).
        //   • CombatGoal.cs:528-532 engageAllowed (runtime mirror
        //     used by Fix 17).
        //   • GoapAgent.cs:1123-1125 TargetTarget three-way (Me/Pet
        //     for Fix 13/17 self-defense, PartyOrPet for Fix L
        //     partner-defense post-Case-2 swap).
        //
        // Note on omitted conjuncts (dmgTaken, !evadeRecoveryActive):
        // The override at GoapAgent.cs:1693-1698 ALSO requires
        // dmgTaken=true and !evadeRecoveryActive. Fix FL deliberately
        // omits them to avoid pulling CombatLog and AssistStatus
        // Provider into BlacklistTargetGoal's constructor. The
        // consequence is Fix FL may fire on a slightly broader set
        // of ticks than the override — but that's harmless: if Fix
        // FL fires while the override doesn't, BL plan yields BUT
        // Combat plan is still ineligible (targetIgnored stays
        // true), so the planner picks the next-cheapest plan
        // (typically Follow Route), which is the correct behavior
        // (retreat under partial-self-defense signal). If we ever
        // observe a regression where retreat is needed but Fix FL
        // suppresses BL during evadeRecovery, add the dependencies.
        bool declaredStuck = navigation.IsApproachEscapePhysicallyStuck
                           || navigation.IsApproachEscapeExhausted;

        int meleeProbe = playerReader.MaxRange();
        bool inMelee = meleeProbe > 0 && meleeProbe <= 5;
        // targetInRect: matches GoapAgent.cs:1629 and CombatGoal.cs:531.
        // The (!inMelee && IsTargetLikelyInBlacklistRect) short-circuit
        // preserves the original Fix FB melee carve-out semantics —
        // a melee-range target is treated as reachable regardless of
        // the rect verdict (the mob walked OUT to engage us).
        bool targetInRect = !inMelee && navigation.IsTargetLikelyInBlacklistRect();
        bool engageAllowed = declaredStuck
                          || (!navigation.IsInBlacklistArea() && !targetInRect);

        bool targetingUs = playerReader.TargetTarget
            is UnitsTarget.Me
            or UnitsTarget.Pet
            or UnitsTarget.PartyOrPet;

        if (bits.Combat() && targetingUs && engageAllowed)
            return false;

        return true;
    }

    public override void OnEnter()
    {
        if (playerReader.PetTarget() ||
            playerReader.IsCasting() ||
            bits.Any_AutoAttack())
        {
            input.PressStopAttack();
        }

        input.PressClearTarget();
        wait.Update();
    }
}
