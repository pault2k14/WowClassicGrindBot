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
        // The fix: when the no-engage mob is in melee range AND
        // targeting our side AND this bot is geographically allowed
        // to engage (declaredStuck OR outside every static rect), step
        // aside so the Combat plan can win. Mirrors:
        //   • CombatGoal's "Run-146 caveat" melee carve-out
        //     (CombatGoal.cs:480-491) — a target in melee range
        //     MaxRange ∈ [1,5] is reachable without entering the rect,
        //     so the rect verdict no-engage doesn't apply.
        //   • GoapAgent's engageAllowedForJoin geographic gate
        //     (GoapAgent.cs:1486) — `declaredStuck || !botInsideBlacklist
        //     Area`.
        //   • The three-way TargetTarget check from GoapAgent.cs:1123-
        //     1125 (targettargetsus key) — Me/Pet covers the Fix 13
        //     self-defense path, PartyOrPet covers the Fix L partner-
        //     defense path (after Case 2 swap the mob still reads as
        //     targeting the partner, not us).
        //
        // Validation against run-153 17:07:49:
        //   targetingUs = Me (TargetTarget=Me per Fix 13 log) ✓
        //   inMelee = True (FindPossibleThreats logged targetInMelee=
        //             True at 17:08:11:620) ✓
        //   engageAllowedForJoin = True (insideBlacklistArea=False
        //             per multiple Fix 13 logs in the window) ✓
        //   bits.Combat = True ✓
        //   → CanRun returns false → Blacklist Target suppressed
        //   → Combat plan (eligible via Fix 13) wins → CombatGoal
        //     runtime Fix 17 (CombatGoal.cs:529-541) keeps the bot
        //     engaged via the same conditions → Razormane killed
        //     in ~9 seconds (matching the actual 17:08:18 → 17:08:27
        //     post-resolution kill phase).
        //
        // Note on evadeRecoveryActive: deliberately NOT a conjunct
        // here. evadeRecoveryActive is set only by GoapAgent.cs:2247
        // in the GhostCombat (guid==0) branch — a rare 15 s window
        // after CombatGoal's ghost-combat timer expires. Run-153's
        // case did not involve ghost combat. Symmetry with Fix 13 /
        // Fix L (both of which gate on !evadeRecoveryActive) would
        // be cleaner, but during the rare ghost-combat window this
        // suppression letting the target stick is acceptable behavior
        // (FRG handles blacklisted targets via FollowRouteGoal.cs:374);
        // can be revisited if a ghost-combat-window oscillation is
        // observed.
        bool declaredStuck = navigation.IsApproachEscapePhysicallyStuck
                           || navigation.IsApproachEscapeExhausted;
        bool engageAllowedForJoin = declaredStuck || !navigation.IsInBlacklistArea();

        int meleeProbe = playerReader.MaxRange();
        bool inMelee = meleeProbe > 0 && meleeProbe <= 5;
        bool targetingUs = playerReader.TargetTarget
            is UnitsTarget.Me
            or UnitsTarget.Pet
            or UnitsTarget.PartyOrPet;

        if (bits.Combat() && inMelee && targetingUs && engageAllowedForJoin)
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
