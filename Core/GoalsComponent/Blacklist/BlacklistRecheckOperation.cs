// ── Fix FW-active (run-167) — Active position recheck operation ──
//
// Phase C implementation of the operator-locked design captured in
// run167-fix-plan.md / run167-fix-phase-c-kickoff.md.
//
// REPLACES the Phase A+B placeholder that read passive
// IsTargetLikelyInBlacklistRect() at FRG waypoint arrival. The
// passive read fails in run-167's specific failure mode:
//   - target oscillates (Combat ↔ BlacklistTarget plan flicker)
//   - target gets cleared by ClearTarget in cycle
//   - facing wrong, distance bracket wide, dead-reckoning noise
//
// THIS COMPONENT performs the multi-step physical operation:
//   1. Tab-cycle (max MaxTabAttemptsPerCycle presses,
//      InterTabSettleMs between) to acquire guid M.
//      - found M  → proceed to Step 2
//      - found other BL mob → skip past
//      - found non-BL aggressor we're in combat with → STOP,
//        hand off to caller for Combat engagement
//      - exhausted attempts → SearchFailed, enter wait-retry
//   2. PressInteract — face M (initiates click-to-move toward M)
//   3. StepBackwards — 100ms back tap cancels click-to-move and
//      stops player motion
//   4. Wait FacingSettleMs (~700ms) for facing/TargetMapPos addon
//      refresh
//   5. Read navigation.IsTargetLikelyInBlacklistRect() — single
//      sample (the function already does P1/P2/P3 multi-sample
//      internally, decision #13)
//   6. Write verdict to cache (InRect / NotInRect)
//
// WAIT-RETRY on SearchFailed:
//   - hold position
//   - retry the cycle every RetryIntervalMs (10s)
//   - after RetryExhaustionMs (30s) total → declare Resolved
//     (mob killed by someone else or leashed)
//   - timer continues across non-BL kill interruptions
//
// LIFECYCLE GUARDS (decision #14):
//   - if cache verdict for the in-flight guid becomes Unknown
//     mid-operation (combat exit invalidates the cache wholesale),
//     abort cleanly without writing a verdict.
//
// THREADING:
//   - Update(IAddonDataProvider) called from the addon read thread
//     each frame. State machine is single-threaded from this entry.
//   - BeginRecheck/IsInFlight/IsHandingOffToCombat/Abort can be
//     called from any thread (typically the GOAP thread). Internal
//     state mutations guarded by a lock to keep cross-thread reads
//     consistent.
//   - Cache (BlacklistRecheckCache) is already thread-safe via
//     its own lock.
//
// ARCHITECTURAL NOTE (operator decision #11):
//   - This is a STANDALONE component, not sub-states inside FRG.
//     It ticks every addon frame regardless of which GOAP plan is
//     active. That's required because engagement-time rechecks
//     fire from CombatGoal / GoapAgent.selfDefenseOverride — neither
//     of which is FRG. Putting the operation in FRG would couple it
//     to FRG being the active plan, which it isn't at engagement
//     time.

using Microsoft.Extensions.Logging;

using System;

namespace Core;

/// <summary>
/// Per-guid active position recheck operation. Performs the multi-step
/// physical operation (Tab/Interact/Stop/Read) and populates the
/// <see cref="BlacklistRecheckCache"/>. Singleton; one per bot.
/// </summary>
public sealed class BlacklistRecheckOperation : IReader
{
    private enum SubPhase
    {
        Idle,
        TabSearch,    // pressing Tab, waiting for new target each press
        Facing,       // PressInteract issued, single-tick transitional state
        Settling,     // waiting FacingSettleMs for addon TargetMapPos refresh
        Reading,      // about to invoke IsTargetLikelyInBlacklistRect() and cache
        Waiting,      // SearchFailed: paused until next retry interval
        HandingOff,   // non-BL aggressor found mid-cycle; paused for caller to engage
    }

    private readonly ILogger<BlacklistRecheckOperation> logger;
    private readonly ConfigurableInput input;
    private readonly PlayerReader playerReader;
    private readonly AddonBits bits;
    private readonly Core.Goals.Navigation navigation;
    private readonly BlacklistRecheckCache cache;
    private readonly CombatLog combatLog;

    private readonly object _lock = new();

    // Operation state (guarded by _lock for cross-thread reads)
    private SubPhase _phase = SubPhase.Idle;
    private int _opGuid;                       // guid being rechecked
    private DateTime _lastTabPressUtc = DateTime.MinValue;
    private DateTime _facingStartUtc = DateTime.MinValue;
    private DateTime _waitingStartUtc = DateTime.MinValue;
    private int _nonBlAggressorGuid;           // set when HandingOff fires

    public BlacklistRecheckOperation(
        ILogger<BlacklistRecheckOperation> logger,
        ConfigurableInput input,
        PlayerReader playerReader,
        AddonBits bits,
        Core.Goals.Navigation navigation,
        BlacklistRecheckCache cache,
        CombatLog combatLog)
    {
        this.logger = logger;
        this.input = input;
        this.playerReader = playerReader;
        this.bits = bits;
        this.navigation = navigation;
        this.cache = cache;
        this.combatLog = combatLog;
    }

    // ── Public API ─────────────────────────────────────────────────

    /// <summary>
    /// Begin (or no-op if already in-flight) an active recheck for the
    /// given guid. Sets the cache verdict to Pending so Fix FY's gate
    /// blocks engagement while the operation runs (decision #12: bot
    /// sits still and waits). Idempotent — if an operation for the same
    /// guid is already in flight, this is a no-op. If a DIFFERENT guid
    /// is in flight, the request is dropped (decision #5 multi-aggressor
    /// option (ii): track only original aggressor; the new guid keeps
    /// its existing verdict, which is typically Unknown — Fix FY's gate
    /// then defers as usual).
    /// </summary>
    public void BeginRecheck(int guid)
    {
        if (guid == 0) return;
        lock (_lock)
        {
            if (_phase != SubPhase.Idle && _opGuid != guid)
            {
                // Different guid already being rechecked; ignore the new one.
                return;
            }
            if (_phase != SubPhase.Idle && _opGuid == guid)
            {
                // Same guid; already in-flight. Idempotent.
                return;
            }
            _opGuid = guid;
            _phase = SubPhase.TabSearch;
            _lastTabPressUtc = DateTime.MinValue;
            _facingStartUtc = DateTime.MinValue;
            _waitingStartUtc = DateTime.MinValue;
            _nonBlAggressorGuid = 0;
            cache.SetVerdict(guid, RecheckVerdict.Pending);
            cache.ResetTabAttempts(guid);
        }
        logger.LogInformation(
            $"[Recheck] [FIX-FIRE] FW-active: BeginRecheck guid={guid}. " +
            $"Bot will sit still while operation runs (Tab cycle + Interact + " +
            $"Stop + read; ~300-700ms expected for a successful cycle).");
    }

    /// <summary>True iff this guid currently has an operation in flight
    /// (TabSearch / Facing / Settling / Reading / Waiting / HandingOff).</summary>
    public bool IsInFlight(int guid)
    {
        if (guid == 0) return false;
        lock (_lock)
        {
            return _opGuid == guid && _phase != SubPhase.Idle;
        }
    }

    /// <summary>True iff ANY operation is currently in flight.</summary>
    public bool AnyInFlight()
    {
        lock (_lock) { return _phase != SubPhase.Idle; }
    }

    /// <summary>
    /// True iff the Tab cycle landed on a non-BL aggressor and the
    /// operation has paused for the caller (typically GOAP / Combat
    /// plan) to engage that guid. The aggressor guid is returned via
    /// <paramref name="aggressorGuid"/>. After the caller has resolved
    /// the combat (mob killed / left combat), the operation will
    /// re-acquire the original target via a fresh cycle — but the
    /// caller can also call <see cref="Abort"/> if the original
    /// engagement is no longer relevant.
    /// </summary>
    public bool IsHandingOffToCombat(out int aggressorGuid)
    {
        lock (_lock)
        {
            if (_phase == SubPhase.HandingOff)
            {
                aggressorGuid = _nonBlAggressorGuid;
                return true;
            }
            aggressorGuid = 0;
            return false;
        }
    }

    /// <summary>
    /// Abort any in-flight operation. Does NOT write a verdict; the
    /// cache is left at whatever its current state is. Callers use
    /// this on combat exit (cache will be wiped wholesale anyway) or
    /// when the original engagement is no longer relevant.
    /// </summary>
    public void Abort(string reason)
    {
        int abortedGuid;
        SubPhase abortedPhase;
        lock (_lock)
        {
            if (_phase == SubPhase.Idle) return;
            abortedGuid = _opGuid;
            abortedPhase = _phase;
            ResetState_NoLock();
        }
        logger.LogInformation(
            $"[Recheck] FW-active: Abort guid={abortedGuid} fromPhase={abortedPhase} reason={reason}");
    }

    // ── IReader.Update — ticks every addon frame ──────────────────

    public void Update(IAddonDataProvider reader)
    {
        // Snapshot state under lock; advance the phase, then write back.
        SubPhase phase;
        int guid;
        lock (_lock)
        {
            phase = _phase;
            guid = _opGuid;
        }

        if (phase == SubPhase.Idle) return;

        // ── Cache-invalidation guard (decision #14) ──
        // If the cache verdict for the in-flight guid is Unknown, the
        // cache was wiped (combat exit) or the entry was explicitly
        // invalidated. Abort cleanly without writing a verdict.
        RecheckVerdict currentVerdict = cache.GetVerdict(guid);
        if (currentVerdict == RecheckVerdict.Unknown)
        {
            Abort("cache verdict became Unknown mid-operation (combat exit or explicit invalidation)");
            return;
        }

        switch (phase)
        {
            case SubPhase.TabSearch:
                TickTabSearch(guid);
                break;
            case SubPhase.Facing:
                TickFacing(guid);
                break;
            case SubPhase.Settling:
                TickSettling(guid);
                break;
            case SubPhase.Reading:
                TickReading(guid);
                break;
            case SubPhase.Waiting:
                TickWaiting(guid);
                break;
            case SubPhase.HandingOff:
                TickHandingOff(guid);
                break;
        }
    }

    public void Reset() { /* nothing — combat-exit invalidation handled by cache + guard */ }

    // ── Phase ticks ───────────────────────────────────────────────

    private void TickTabSearch(int guid)
    {
        // Inter-Tab settle: wait InterTabSettleMs since the last Tab
        // press before pressing again. Gives the WoW UI time to
        // publish the new target slot.
        DateTime lastPress;
        lock (_lock) { lastPress = _lastTabPressUtc; }
        if (lastPress != DateTime.MinValue &&
            (DateTime.UtcNow - lastPress).TotalMilliseconds < BlacklistRecheckCache.InterTabSettleMs)
        {
            // Still settling from previous press; wait.
            return;
        }

        // If the last press already landed us on the target guid, we
        // don't need another press. Skip straight to Facing.
        int curTarget = playerReader.TargetGuid;
        if (curTarget == guid)
        {
            lock (_lock) { _phase = SubPhase.Facing; }
            logger.LogInformation(
                $"[Recheck] FW-active: Tab landed on target guid={guid}, advancing to Facing.");
            return;
        }

        // If the last press landed on a DIFFERENT guid, classify it.
        if (curTarget != 0 && curTarget != guid && lastPress != DateTime.MinValue)
        {
            bool curIsBl = playerReader.IsNoEngage(curTarget);
            bool curIsHostileAndDamagingUs =
                bits.Target_Hostile() &&
                !bits.Target_Dead() &&
                combatLog.DamageTaken.Contains(curTarget);

            if (!curIsBl && curIsHostileAndDamagingUs)
            {
                // Non-BL aggressor discovered. STOP cycle, hand off to caller.
                lock (_lock)
                {
                    _phase = SubPhase.HandingOff;
                    _nonBlAggressorGuid = curTarget;
                }
                cache.RecordNonBlAggressorDiscovered(guid, curTarget);
                logger.LogWarning(
                    $"[Recheck] [FIX-FIRE] FW-active: Tab cycle for guid={guid} landed on " +
                    $"non-BL aggressor guid={curTarget} (in combat, damaging us, not on IsNoEngage). " +
                    $"STOPPING cycle and handing off to caller for Combat engagement. " +
                    $"Cycle will restart after the non-BL guid is resolved.");
                return;
            }

            // Other BL mob, or some unrelated target. Continue cycling past it.
            logger.LogDebug(
                $"[Recheck] FW-active: Tab landed on guid={curTarget} (isBl={curIsBl}, " +
                $"hostile={bits.Target_Hostile()}, dead={bits.Target_Dead()}) — skipping, " +
                $"continuing cycle for guid={guid}.");
        }

        // Have we hit the attempts cap?
        var entry = cache.TryGetEntry(guid);
        int attempts = entry?.TabAttemptsThisCycle ?? 0;
        if (attempts >= BlacklistRecheckCache.MaxTabAttemptsPerCycle)
        {
            // Tab cycle exhausted. Transition to wait-retry.
            cache.SetVerdict(guid, RecheckVerdict.SearchFailed);
            lock (_lock)
            {
                _phase = SubPhase.Waiting;
                _waitingStartUtc = DateTime.UtcNow;
            }
            logger.LogWarning(
                $"[Recheck] [FIX-FIRE] FW-active: Tab cycle exhausted ({attempts} attempts) " +
                $"for guid={guid}. Entering wait-retry: retry every {BlacklistRecheckCache.RetryIntervalMs}ms, " +
                $"give up at {BlacklistRecheckCache.RetryExhaustionMs}ms total.");
            return;
        }

        // Press Tab.
        if (!input.TargetNearestTarget.OnCooldown())
        {
            input.PressNearestTarget(System.Threading.CancellationToken.None);
            cache.RecordTabAttempt(guid);
            lock (_lock) { _lastTabPressUtc = DateTime.UtcNow; }
        }
        // (else: Tab on cooldown — try again next addon tick)
    }

    private void TickFacing(int guid)
    {
        // Stop motion + face the target.
        navigation.StopMovement();   // release ForwardKey if held
        input.PressInteract();       // start WoW click-to-move toward target
        input.StepBackwards();       // 100ms back tap cancels click-to-move + stops bot

        lock (_lock)
        {
            _facingStartUtc = DateTime.UtcNow;
            _phase = SubPhase.Settling;
        }
        logger.LogInformation(
            $"[Recheck] FW-active: Facing guid={guid} via Interact+StepBackwards. " +
            $"Settling for {BlacklistRecheckCache.FacingSettleMs}ms before reading verdict.");
    }

    private void TickSettling(int guid)
    {
        // Continuation guards: target may have dropped during the settle
        // (mob died or leashed). If so, abort.
        if (!bits.Target() || playerReader.TargetGuid != guid)
        {
            Abort($"target lost or changed during Settling (curTarget={playerReader.TargetGuid}, opGuid={guid})");
            return;
        }

        DateTime facingStart;
        lock (_lock) { facingStart = _facingStartUtc; }
        if ((DateTime.UtcNow - facingStart).TotalMilliseconds < BlacklistRecheckCache.FacingSettleMs)
            return; // still settling

        lock (_lock) { _phase = SubPhase.Reading; }
    }

    private void TickReading(int guid)
    {
        // Single read (decision #13). IsTargetLikelyInBlacklistRect()
        // itself is multi-sample (P1/P2/P3 fallback chain) so the
        // outer layer doesn't need additional sampling.
        if (!bits.Target() || playerReader.TargetGuid != guid)
        {
            Abort($"target lost or changed at Reading (curTarget={playerReader.TargetGuid}, opGuid={guid})");
            return;
        }

        bool inRect = navigation.IsTargetLikelyInBlacklistRect();
        RecheckVerdict verdict = inRect ? RecheckVerdict.InRect : RecheckVerdict.NotInRect;
        cache.SetVerdict(guid, verdict);

        logger.LogWarning(
            $"[Recheck] [FIX-FIRE] FW-active: Verdict for guid={guid} = {verdict} " +
            $"(passive read after Tab+Interact+Stop+700ms settle). Cache updated; " +
            $"caller's Fix FY gate will pick up the verdict on next planner tick.");

        // Operation complete — transition to Idle.
        lock (_lock) { ResetState_NoLock(); }
    }

    private void TickWaiting(int guid)
    {
        // Wait-retry: 10s interval, 30s total budget.
        if (cache.IsRetryExhausted(guid))
        {
            cache.SetVerdict(guid, RecheckVerdict.Resolved);
            logger.LogWarning(
                $"[Recheck] [FIX-FIRE] FW-active: Wait-retry exhausted ({BlacklistRecheckCache.RetryExhaustionMs}ms) " +
                $"for guid={guid}. Declaring Resolved (mob assumed killed by someone else or leashed). " +
                $"Caller's Fix FY gate will allow engagement of other guids; this guid will not " +
                $"re-trigger recheck unless re-cached.");
            lock (_lock) { ResetState_NoLock(); }
            return;
        }

        if (cache.IsRetryDue(guid))
        {
            // Reset attempt counter and restart cycle.
            cache.ResetTabAttempts(guid);
            cache.SetVerdict(guid, RecheckVerdict.Pending);
            lock (_lock)
            {
                _phase = SubPhase.TabSearch;
                _lastTabPressUtc = DateTime.MinValue;
            }
            logger.LogInformation(
                $"[Recheck] FW-active: Retry due for guid={guid} — restarting Tab cycle.");
        }
        // else: still waiting; do nothing this tick
    }

    private void TickHandingOff(int guid)
    {
        // Wait for the caller's Combat plan to resolve the non-BL aggressor.
        // Detection: bits.Combat() drops below "in combat with the aggressor"
        // OR the aggressor died (no longer hostile / no longer in combat with us).
        //
        // Simpler heuristic that's reliable: if our DamageTaken set no longer
        // contains _nonBlAggressorGuid (combatLog cleared on Left Combat), the
        // engagement is over. Restart the recheck cycle for the original guid.
        int agg;
        lock (_lock) { agg = _nonBlAggressorGuid; }

        bool aggressorResolved =
            !combatLog.DamageTaken.Contains(agg) ||
            (playerReader.TargetGuid == agg && bits.Target_Dead());

        if (aggressorResolved)
        {
            logger.LogInformation(
                $"[Recheck] FW-active: Non-BL aggressor guid={agg} resolved. " +
                $"Restarting recheck cycle for original guid={guid}.");
            cache.ResetTabAttempts(guid);
            cache.SetVerdict(guid, RecheckVerdict.Pending);
            lock (_lock)
            {
                _phase = SubPhase.TabSearch;
                _lastTabPressUtc = DateTime.MinValue;
                _nonBlAggressorGuid = 0;
            }
        }
        // else: still in combat with the aggressor; caller's Combat plan
        // is engaging. Wait.
    }

    private void ResetState_NoLock()
    {
        _phase = SubPhase.Idle;
        _opGuid = 0;
        _lastTabPressUtc = DateTime.MinValue;
        _facingStartUtc = DateTime.MinValue;
        _waitingStartUtc = DateTime.MinValue;
        _nonBlAggressorGuid = 0;
    }
}
