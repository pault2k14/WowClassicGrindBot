using Core.GOAP;
using Core.Party;

using Microsoft.Extensions.Logging;

using SharedLib.NpcFinder;

using System;
using System.Threading;

using static System.Diagnostics.Stopwatch;

namespace Core.Goals;

public sealed class PullTargetGoal : GoapGoal, IGoapEventListener
{
    public override float Cost => 7f;

    private const int AcquireTargetTimeMs = 5000;
    private const int MAX_PULL_DURATION = 15_000;
    private const double RangeStuckIntervalMs = 3000;

    private readonly ILogger<PullTargetGoal> logger;
    private readonly ConfigurableInput input;
    private readonly ClassConfiguration classConfig;
    private readonly Wait wait;
    private readonly CombatLog combatLog;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly StopMoving stopMoving;
    private readonly StuckDetector stuckDetector;
    private readonly NpcNameTargeting npcNameTargeting;
    private readonly CastingHandler castingHandler;
    private readonly IMountHandler mountHandler;
    private readonly CombatTracker combatTracker;
    private readonly IBlacklist targetBlacklist;
    private readonly ChatReader chatReader;
    private readonly Navigation navigation;
    private readonly AssistStateStore assistStateStore;
    private readonly AssistStatusProvider assistStatusProvider;

    private readonly KeyAction? approachKey;
    private readonly Action approachAction;
    
    private readonly bool requiresNpcNameFinder;

    private long pullStart;
    private float _rangeStuckLastMinRange;
    private double _rangeStuckCheckAtMs;

    private bool _evadeRecoveryActive;

    private double PullDurationMs => GetElapsedTime(pullStart).TotalMilliseconds;

    public PullTargetGoal(ILogger<PullTargetGoal> logger, ConfigurableInput input,
        Wait wait, CombatLog combatlog, PlayerReader playerReader,
        AddonBits bits,
        IBlacklist targetBlacklist,
        StopMoving stopMoving, CastingHandler castingHandler,
        IMountHandler mountHandler, NpcNameTargeting npcNameTargeting,
        StuckDetector stuckDetector, CombatTracker combatTracker,
        ClassConfiguration classConfig, ChatReader chatReader,
        Navigation navigation,
        AssistStateStore assistStateStore,
        AssistStatusProvider assistStatusProvider)
        : base(nameof(PullTargetGoal))
    {
        this.logger = logger;
        this.input = input;
        this.wait = wait;
        this.combatLog = combatlog;
        this.playerReader = playerReader;
        this.bits = bits;
        this.stopMoving = stopMoving;
        this.castingHandler = castingHandler;
        this.mountHandler = mountHandler;
        this.npcNameTargeting = npcNameTargeting;
        this.stuckDetector = stuckDetector;
        this.combatTracker = combatTracker;
        this.targetBlacklist = targetBlacklist;
        this.classConfig = classConfig;
        this.chatReader = chatReader;
        this.navigation = navigation;
        this.assistStateStore = assistStateStore;
        this.assistStatusProvider = assistStatusProvider;

        Keys = classConfig.Pull.Sequence;

        approachAction = DefaultApproach;

        for (int i = 0; i < Keys.Length; i++)
        {
            KeyAction keyAction = Keys[i];

            if (keyAction.Name.Equals(input.Approach.Name, StringComparison.OrdinalIgnoreCase))
            {
                approachAction = ConditionalApproach;
                approachKey = keyAction;
            }

            if (keyAction.Requirements.Contains(RequirementFactory.AddVisible))
            {
                requiresNpcNameFinder = true;
            }
        }

        if (classConfig.Mode != Mode.AssistFocus)
        {
            AddPrecondition(GoapKey.targettargetsus, false);
        }

        AddPrecondition(GoapKey.forcedfollow, false);
        AddPrecondition(GoapKey.hastarget, true);
        AddPrecondition(GoapKey.targetisalive, true);
        AddPrecondition(GoapKey.targethostile, true);
        AddPrecondition(GoapKey.withinpullrange, true);
        AddPrecondition(GoapKey.inblacklistarea, false);
        AddPrecondition(GoapKey.assistrequestreturn, false);
        AddPrecondition(GoapKey.evadeRecovery, false);
        AddPrecondition(GoapKey.approachEscapeActive, false);

        AddEffect(GoapKey.pulled, true);
    }

    public override bool CanRun()
    {
        // Fix FU (run-166): yield to Fix FN/FT backtrack. Mirrors the
        // ATG.CanRun and BlacklistTargetGoal.CanRun gates — when the
        // FollowRouteGoal backtrack state machine is driving us back
        // along prior route waypoints (IsBacktrackingActive=true), PTG
        // must not preempt. WoW Classic auto-retargets the leader to
        // any mob that hits us when the target slot empties; the BL
        // caster fills it repeatedly, and PTG's hasTarget precondition
        // is satisfied each tick. Without this gate, PTG would fire,
        // call RATF, and either approach-pull into the rect (defeating
        // the backtrack) or run its in-rect bail (creating another
        // FRG.Abort cycle).
        if (navigation.IsBacktrackingActive)
            return false;

        return true;
    }

    public override void OnEnter()
    {
        // Target-tracking diagnostic (grep TARGET-GUID): live target guid + escape's
        // locked guid at goal entry, unconditional + Info-level.
        logger.LogInformation($"[PTG] OnEnter TARGET-GUID={playerReader.TargetGuid} escapeGuid={navigation.ApproachEscapeTargetGuid}");

        // E5: approach-entry gate (re-evaluation + fresh-pull) — see ApproachTargetGoal
        // OnEnter. We only reach here for a fightable (non-ignored) target; if it's
        // inside a blacklist rect, blacklist it in-rect and bail rather than pulling
        // into the rect. The IsIgnored abort at the top of Update exits next tick.
        if (playerReader.TargetGuid != 0 && navigation.IsTargetLikelyInBlacklistRect())
        {
            logger.LogInformation(
                $"[PTG] E5 approach gate: target guid={playerReader.TargetGuid} is inside a " +
                $"blacklist rect — blacklisting (in-rect) instead of pulling.");
            if (classConfig.Mode == Mode.PartyLeader)
                SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid, EvadeReason.ReachabilityBail, true));
            playerReader.IgnoreTarget(playerReader.TargetGuid, true);
            return;
        }

        wait.Update();
        stuckDetector.Reset();

        if (navigation.IsApproachEscapeActive &&
            playerReader.TargetGuid != 0 &&
            navigation.ApproachEscapeTargetGuid != 0 &&
            navigation.ApproachEscapeTargetGuid != playerReader.TargetGuid)
        {
            logger.LogWarning($"[PTG] OnEnter: stopping wrong-mob escape " +
                $"(escapeGuid={navigation.ApproachEscapeTargetGuid} ≠ target={playerReader.TargetGuid}) — calling Stop().");
            navigation.Stop();
        }

        if (!navigation.IsApproachEscapeActive)
        {
            logger.LogDebug($"[PTG] OnEnter: calling RATF for target={playerReader.TargetGuid} — " +
                $"pre-RATF state: escapeGuid={navigation.ApproachEscapeTargetGuid} " +
                $"yards={navigation.ApproachEscapeCurrentYards:0} active={navigation.IsApproachEscapeActive} " +
                $"exhausted={navigation.IsApproachEscapeExhausted} escalating={navigation.IsApproachEscapeEscalating}");
            navigation.ResetApproachEscapeForTarget(playerReader.TargetGuid);
        }
        else
        {
            logger.LogInformation($"[PTG] OnEnter: escape ACTIVE — skipping RATF. " +
                $"yards={navigation.ApproachEscapeCurrentYards:0} " +
                $"startUtc={navigation.ApproachEscapeStartUtc:HH:mm:ss.fff} " +
                $"lastAttemptAge={(DateTime.UtcNow - navigation.ApproachEscapeLastAttemptUtc).TotalMilliseconds:0}ms " +
                $"guid={navigation.ApproachEscapeTargetGuid} target={playerReader.TargetGuid} " +
                $"exhausted={navigation.IsApproachEscapeExhausted} escalating={navigation.IsApproachEscapeEscalating}");
        }
        _rangeStuckLastMinRange = float.MaxValue;

        if (mountHandler.IsMounted())
        {
            mountHandler.Dismount();
        }

        if (Keys.Length != 0 && !input.StopAttack.OnCooldown())
        {
            Log("Stop auto interact!");
            input.PressStopAttack();
            wait.Update();
            stopMoving.StopForward();
            wait.Update(playerReader.DoubleNetworkLatency);
            wait.Update();
        }

        if (requiresNpcNameFinder)
        {
            npcNameTargeting.ChangeNpcType(NpcNames.Enemy);
        }

        pullStart = GetTimestamp();
        _rangeStuckCheckAtMs = PullDurationMs + RangeStuckIntervalMs;
        input.PressDisableSoftInteract();
        wait.Update();
    }

    public override void OnExit()
    {
        if (requiresNpcNameFinder)
        {
            npcNameTargeting.ChangeNpcType(NpcNames.None);
        }

        // ── Fix CD (audit finding from run-155 follow-up) ──
        //
        // Was conditional: `if(bits.Target() && bits.Target_Alive() &&
        // bits.Combat()) input.StopForward(false)`. That only covered the
        // intended "pull succeeded, in combat" case. Other PTG exits left
        // Forward held:
        //   - Target died before pull completed (assist/leader finished
        //     the kill while PTG was still in the wind-up).
        //   - Target lost (out of LOS, despawned, distance).
        //   - Plan transition (Combat goal preempts before PTG's expected
        //     handoff path).
        //
        // PTG can hold Forward via ReactCastError.cs:159 (StartForward when
        // target is outside pull range and ERR_BADATTACKFACING fires).
        // None of those failure paths pair with a Stop().
        //
        // Unconditional release closes all of them. IsKeyDown guard inside
        // ConfigurableInput.StopForward makes it a no-op when Forward isn't
        // down, so the intended "pull succeeded" path costs nothing extra.
        input.StopForward(false);
    }

    public void OnGoapEvent(GoapEventArgs e)
    {
        if (e.GetType() == typeof(ResumeEvent))
        {
            pullStart = GetTimestamp();
            _rangeStuckLastMinRange = float.MaxValue;
            _rangeStuckCheckAtMs = PullDurationMs + RangeStuckIntervalMs;
        }
        else if (e is GoapStateEvent s && s.Key == GoapKey.evadeRecovery)
        {
            _evadeRecoveryActive = s.Value;
        }
    }

    public override void Update()
    {
        wait.Update();

        if (bits.Drowning())
        {
            input.PressJump();
        }

        if (chatReader.ForcedFollow)
        {
            AddEffect(GoapKey.forcedfollow, true);
            return;
        }

        // See ApproachTargetGoal.cs Update() for the rationale: also abort when
        // the current target is in playerReader.IsIgnored, in case the broadcast
        // of GoapKey.evadeRecovery hasn't reached us yet on this update tick.
        bool currentTargetIsIgnored =
            bits.Target() && playerReader.TargetGuid != 0 &&
            playerReader.IsIgnored(playerReader.TargetGuid);

        if (_evadeRecoveryActive || currentTargetIsIgnored)
        {
            string reason = _evadeRecoveryActive
                ? "evade recovery active"
                : $"current target guid={playerReader.TargetGuid} is in IsIgnored (broadcast not yet seen)";
            logger.LogInformation($"[PullTargetGoal] Aborting pull — {reason}.");
            input.PressStopAttack();
            wait.Update();
            input.PressClearTarget();
            wait.Update();
            stopMoving.Stop();
            return;
        }

        if (!navigation.IsApproachEscapeActive &&
            bits.Target() && !bits.Combat() &&
            (navigation.IsApproachEscapeExhausted ||
             (!navigation.IsApproachEscapeEscalating &&
              (targetBlacklist.Is() || navigation.IsInBlacklistArea()))))
        {
            string ptgBailReason = navigation.IsApproachEscapeExhausted
                ? "all escape levels exhausted (10y/20y/30y failed)"
                : targetBlacklist.Is() ? "target in blacklist" : "player inside blacklist area";
            logger.LogWarning($"[PTG] Bail-out: blacklisting target guid={playerReader.TargetGuid} — reason: {ptgBailReason}.");
            bool inRect = navigation.IsTargetLikelyInBlacklistRect();
            if (classConfig.Mode == Mode.PartyLeader && playerReader.TargetGuid != 0)
                SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid, EvadeReason.ReachabilityBail, inRect));
            playerReader.IgnoreTarget(playerReader.TargetGuid, inRect);
            input.PressStopAttack();
            input.PressClearTarget();
            wait.Update();
            stopMoving.StopForward();
            navigation.Stop();
            navigation.ResetApproachEscape();
            navigation.ClearStuckRects();
            wait.Update(playerReader.DoubleNetworkLatency);
            wait.Update();
            return;
        }

        // Block pull on the leader if the assist is genuinely unavailable (not following
        // and not actively navigating toward us). Using the API store mirrors ATG's check.
        if (!bits.Combat() && classConfig.Mode == Mode.PartyLeader &&
            !assistStateStore.AnyAssistIsFollowing() &&
            !assistStateStore.AnyAssistNavigating())
        {
            logger.LogInformation("PullTargetGoal: Not pulling — assist is not following or navigating.");
            return;
        }

        // ── Fix CF Q1 (run-157 01:54:18:524 redundant StuckRect) ──
        //
        // Gate the "Pull taking too long" path on bits.Target(). A pull
        // cannot be "taking too long" if there is no target — the bot
        // isn't pulling anything. Without this gate, the path fires
        // spuriously in scenarios where:
        //
        //   1. OnEnter's E5 early-return (line 137-143) bypasses the
        //      `pullStart = GetTimestamp()` at line 198, so `pullStart`
        //      keeps a stale value (from a previous PTG cycle or 0-init).
        //   2. The Evade event's handler in GoapAgent (line 2402-2403)
        //      then PressStopAttack + PressClearTarget, dropping the
        //      target slot.
        //   3. PTG.Update's next tick fires:
        //      - currentTargetIsIgnored guard (line 269): doesn't fire
        //        because bits.Target() is already false.
        //      - Bail-out (line 283): requires bits.Target() → skipped.
        //      - Assist-block (line 311): assist is following → skipped.
        //      - "Pull taking too long" (this block): stale pullStart
        //        makes PullDurationMs > MAX_PULL_DURATION true →
        //        TryUnstuck() drops a redundant dynamic StuckRect on
        //        top of the static blacklist that already covers the
        //        same area.
        //
        // Run-157 evidence (leader clock):
        //   01:54:18:260  PTG.OnEnter E5 fires for 2092382 (in static
        //                 rect), returns at line 143 without setting
        //                 pullStart.
        //   01:54:18:306  GoapAgent Evade handler: PressStopAttack.
        //   01:54:18:384  GoapAgent Evade handler: PressClearTarget.
        //   01:54:18:524  PTG.Update tick: "Pull taking too long" fires
        //                 → TryUnstuck → "StuckRect added: center=
        //                 <23.734722, -1563.7275>" — redundant: the
        //                 static rect [-116,-1672..21,-1545] inflated
        //                 already contains this position.
        //
        // The other in-Update checks at 283-307 and 311-317 properly
        // require bits.Target() or bits.Combat(); this block was the
        // outlier. The fix aligns behavior across all paths.
        if (bits.Target() &&
            PullDurationMs > MAX_PULL_DURATION &&
            !navigation.IsApproachEscapeActive &&
            !navigation.IsApproachEscapeEscalating)
        {
            input.PressStopAttack();
            input.PressClearTarget();
            navigation.TryUnstuck();
            Log("Pull taking too long. Clear target and attempting unstuck.");
            return;
        }

        if (classConfig.AutoPetAttack &&
            bits.Pet() &&
            (!playerReader.PetTarget() ||
            playerReader.TargetGuid != playerReader.PetTargetGuid) &&
            !input.PetAttack.OnCooldown())
        {
            input.PressStopAttack();
            input.PressPetAttack();
        }

        bool castAny = false;
        bool spellInQueue = false;

        ReadOnlySpan<KeyAction> keys = Keys;
        for (int i = 0; i < keys.Length; i++)
        {
            KeyAction keyAction = keys[i];

            if (classConfig.RaidIconsToSkipInCombat.IndexOf(playerReader
                .TargetRaidIcon()) != -1 && !keyAction.CrowdControl)
            {
                if (i + 1 == keys.Length)
                {
                    return;
                }
                continue;
            }

            if (keyAction.Name.Equals(input.Approach.Name,
                StringComparison.OrdinalIgnoreCase))
                continue;

            if (!keyAction.CanRun())
                continue;

            spellInQueue = castingHandler.SpellInQueue();
            if (spellInQueue)
            {
                break;
            }

            bool interrupt() => keyAction.CanBeInterrupted() || PullPrevention();

            if (castAny = castingHandler.Cast(keyAction, interrupt))
            {
                castAny = !keyAction.BaseAction;
            }
            else if (PullPrevention() &&
                (playerReader.IsCasting() || bits.Any_AutoAttack()))
            {
                Log("Preventing pulling possible tagged target!");
                input.PressStopAttack();
                input.PressClearTarget();
                wait.Update();
                return;
            }
        }

        if (bits.Target() && combatLog.EvadeMobs.Contains(playerReader.TargetGuid))
        {
            Log("Evading mob");

            if (classConfig.Mode == Mode.PartyLeader && playerReader.TargetGuid != 0)
                SendGoapEvent(new EvadeBlacklistEvent(playerReader.TargetGuid, EvadeReason.RealEvade));
            playerReader.IgnoreTarget(playerReader.TargetGuid);
            input.PressStopAttack();
            input.PressClearTarget();
            wait.Update();
            return;
        }
        else if (bits.Target())
        {
            combatLog.ToPull.Add(playerReader.TargetGuid);
        }

        if (castAny || spellInQueue || playerReader.IsCasting() || (bits.AutoShot() && !playerReader.IsInMeleeRange()))
            return;

        approachAction();
    }

    private void DefaultApproach()
    {
        if (input.Approach.OnCooldown())
            return;

        if (navigation.IsApproachEscapeActive)
        {
            navigation.Update(CancellationToken.None);
            if (navigation.IsApproachEscapeActive)
            {
                if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
                {
                    double escSec = (DateTime.UtcNow - navigation.ApproachEscapeStartUtc).TotalSeconds;
                    logger.LogDebug($"[PTG] DefaultApproach: escape heartbeat — " +
                        $"yards={navigation.ApproachEscapeCurrentYards:0} escapeSec={escSec:0.0}s " +
                        $"range={playerReader.MinRange():0.0}y guid={navigation.ApproachEscapeTargetGuid}");
                }
                navigation.TryUnstuck();
            }
            else
            {
                logger.LogInformation(
                    $"[PTG] Escape complete — resetting range-stuck tracker. " +
                    $"range={playerReader.MinRange():0.0}y exhausted={navigation.IsApproachEscapeExhausted} escalating={navigation.IsApproachEscapeEscalating}");
                _rangeStuckLastMinRange = playerReader.MinRange();
                _rangeStuckCheckAtMs = PullDurationMs + RangeStuckIntervalMs;
            }
            return;
        }

        if (!bits.SoftInteract() || EligibleEnemySoftTargetExists())
        {
            navigation.RecordApproachPosition(playerReader.WorldPos);
            input.PressApproach();
            wait.Update();
        }
        else
        {
            logger.LogInformation("Can't input.PressApproach()");
            logger.LogInformation("!bits.SoftInteract(): " + !bits.SoftInteract());
            logger.LogInformation("EligibleEnemySoftTargetExists(): " + EligibleEnemySoftTargetExists());
        }

        if (PullDurationMs >= _rangeStuckCheckAtMs && !navigation.IsApproachEscapeActive)
        {
            float currentRange = playerReader.MinRange();
            // ── Fix DS (log-131 13:42:03 — leader "ran away and came back") ──
            // MinRange()==0 is the sentinel for "target not in any detectable
            // range bracket" (out of every range check, or LOS-blocked), NOT a
            // real 0y distance — incombatrange would be true if it were melee.
            // The detector seeded _rangeStuckLastMinRange with that 0 (see the
            // seed block below) and then read 0 again 3s later, so 0 >= 0 gave a
            // false "No range progress (0.0 -> 0.0y)" and fired a pointless
            // ApproachEscape: the leader detoured ~18y away from mob 1094502 and
            // returned to the SAME spot, where combat started 12s later (via the
            // mob aggroing the party). It was the ONLY escape in the entire run.
            // Skip the progress comparison on invalid (0) readings and defer to
            // the next interval. Genuine physical stuck is still caught by the
            // !IsMoving() escape below; genuine no-progress across VALID readings
            // still escapes.
            if (currentRange <= 0f)
            {
                _rangeStuckCheckAtMs = PullDurationMs + RangeStuckIntervalMs;
            }
            else if (currentRange >= _rangeStuckLastMinRange)
            {
                logger.LogInformation(
                    $"[PTG] No range progress after {RangeStuckIntervalMs:0}ms " +
                    $"({_rangeStuckLastMinRange:0.0} -> {currentRange:0.0}y) — attempting pather escape. " +
                    $"escalating={navigation.IsApproachEscapeEscalating} exhausted={navigation.IsApproachEscapeExhausted}");
                _rangeStuckCheckAtMs = PullDurationMs + RangeStuckIntervalMs;
                navigation.TryUnstuck();
            }
            else
            {
                _rangeStuckLastMinRange = currentRange;
                _rangeStuckCheckAtMs = PullDurationMs + RangeStuckIntervalMs;
            }
        }
        else if (_rangeStuckLastMinRange == float.MaxValue)
        {
            // Fix DS: only seed the tracker with a VALID (non-zero) reading so a
            // transient 0 sentinel can't become the baseline that yields 0 >= 0.
            float seed = playerReader.MinRange();
            if (seed > 0f)
                _rangeStuckLastMinRange = seed;
        }

        if (!stuckDetector.IsMoving())
        {
            // ── Fix FF Q1-A (audit followup to Fix CF Q1) ──
            // "Not moving" only diagnoses a stuck approach when there's
            // actually a target to be approaching. Without bits.Target()
            // this fires after any path that clears the target mid-Update
            // (E5, target death, GoapAgent ClearTarget, etc.) while
            // pullStart timers are stale from the previous attempt —
            // navigation.TryUnstuck() then runs ApproachEscape against
            // either a stale anchor direction or the player's own position,
            // accumulating redundant stuck rects with no real obstacle.
            //
            // Pre-Fix-FE this was catastrophic (SetSingleWaypoint clobbered
            // the patrol stack on an unreachable projection → run-158-class
            // deadlock). Post-Fix-FE the patrol stack is preserved and the
            // escape exhausts in ~24-30 s, but the redundant rects persist
            // across episodes and degrade future pathfinding. Same
            // bits.Target() gate as Fix CF Q1 added to the "Pull taking
            // too long" check at line 366 — symmetric defense.
            if (!bits.Target())
                return;

            logger.LogInformation(
                $"[PTG] Not moving — calling TryUnstuck(). " +
                $"escalating={navigation.IsApproachEscapeEscalating} exhausted={navigation.IsApproachEscapeExhausted} " +
                $"yards={navigation.ApproachEscapeCurrentYards:0} range={playerReader.MinRange():0.0}y " +
                $"lastAttemptAge={(DateTime.UtcNow - navigation.ApproachEscapeLastAttemptUtc).TotalMilliseconds:0}ms " +
                $"stuckRects={navigation.StuckRectCount}");
            navigation.TryUnstuck();
        }
    }

    private void ConditionalApproach()
    {
        if (approachKey == null ||
            (!approachKey.CanRun() && !approachKey.OnCooldown()))
        {
            logger.LogInformation("ConditionalApproach: stopMoving.Stop()");
            logger.LogInformation("approachKey: " + approachKey);
            logger.LogInformation("!approachKey.CanRun(): " + !approachKey.CanRun());
            logger.LogInformation("!approachKey.OnCooldown(): " + !approachKey.OnCooldown());

            stopMoving.Stop();
            return;
        }

        DefaultApproach();
    }

    private bool PullPrevention()
    {
        return !targetBlacklist.Is() ||
            playerReader.TargetTarget is
            UnitsTarget.None or
            UnitsTarget.Me or
            UnitsTarget.Pet or
            UnitsTarget.PartyOrPet;
    }

    private bool EligibleEnemySoftTargetExists() =>
        bits.SoftInteract() &&
        bits.SoftInteract_Hostile() &&
        !bits.SoftInteract_Dead() &&
        !bits.SoftInteract_Tagged() &&
        playerReader.SoftInteract_Type == GuidType.Creature;

    private void Log(string text)
    {
        logger.LogInformation(text);
    }
}
