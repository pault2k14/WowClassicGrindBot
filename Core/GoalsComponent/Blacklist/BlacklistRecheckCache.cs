// ── Fix FW (run-167) — Active position recheck cache for BL aggressors ──
//
// Operator-locked design (see /mnt/user-data/outputs/run167-fix-plan.md):
//
//   • Position is only knowable when the mob is our current target.
//     Until then we have no MaxRange/Direction data for that guid.
//
//   • Therefore "recheck this guid's position" is a multi-step physical
//     operation, not a pure read:
//       1. Tab-cycle (up to 5 presses, T_settle=200ms between) to find
//          guid M.
//       2. PressInteract to face M (starts WoW click-to-move).
//       3. StepBackwards to cancel click-to-move and stop motion.
//       4. Wait for stationary state + addon TargetMapPos refresh.
//       5. Read multi-sample IsTargetLikelyInBlacklistRect verdict.
//
//   • Result is CACHED per-guid for the duration of the engagement.
//     Auto-retarget on damage during backtrack (where we keep the
//     target so WoW doesn't auto-retarget anyway) reads cache, does
//     NOT re-run the operation.
//
//   • Cache invalidates only on combat exit (mob death or leash) —
//     when bits.Combat() transitions true→false.
//
//   • Tab cycle behaviors:
//       - Found M → proceed to face/stop/read.
//       - Found a DIFFERENT guid that's on IsNoEngage map (other BL
//         mob) → skip past, continue cycling for M.
//       - Found a DIFFERENT guid that we're in combat with AND not on
//         IsNoEngage map (non-BL aggressor) → STOP recheck, signal
//         caller to engage this guid via normal Combat plan. After
//         that combat resolves, recheck restarts from Step 1.
//       - 5 presses exhausted without finding M → RECHECK_FAILED.
//
//   • Wait-and-retry protocol on RECHECK_FAILED:
//       - Stay put. Wait 10s. Retry.
//       - If still fails, wait 10s. Retry. (20s total)
//       - If still fails, wait 10s. Retry. (30s total)
//       - At 30s exhausted → declare aggressor RESOLVED (killed by
//         someone else or leashed). Exit backtrack. Resume forward
//         patrol.
//       - During the wait, bot defends against any aggressor that
//         attacks (non-BL via normal Combat plan; BL via own
//         recheck for that new guid).
//       - Timer continues across non-BL kill interruptions (do NOT
//         reset on Combat resume).
//
//   • Engagement-time recheck fires for ALL new aggros (first damage
//     from a guid not in cache), not just guids that are propagated
//     IsNoEngage. Per operator: until we target the mob we cannot
//     know its position, so we cannot know if it's in a rect.
//
// This file owns the cache state and the operation steps. The state
// machine driving them (per-tick advancement, transitions between
// "looking for M", "facing", "settling", "reading", "waiting") lives
// inside FollowRouteGoal.cs's UpdateBacktrackStateMachine (Fix FX)
// and FollowFocusGoal.cs (Fix GB consumer). This file exposes the
// primitives those consumers compose.

using System;
using System.Collections.Generic;

namespace Core;

/// <summary>
/// Per-guid result of an engagement-time / waypoint-arrival position recheck.
/// </summary>
public enum RecheckVerdict
{
    /// <summary>No recheck has been performed yet for this guid (or cache cleared).</summary>
    Unknown = 0,

    /// <summary>Recheck operation is in flight (Tab-cycling, facing, settling, reading).</summary>
    Pending,

    /// <summary>Recheck completed and confirmed mob's estimated position is inside a BL rect.</summary>
    InRect,

    /// <summary>Recheck completed and confirmed mob's estimated position is outside all BL rects.</summary>
    NotInRect,

    /// <summary>Tab cycle exhausted (5 presses) without finding the guid; wait-retry in progress.</summary>
    SearchFailed,

    /// <summary>Wait-retry exhausted (30s elapsed without finding); aggressor declared killed or leashed.</summary>
    Resolved,
}

/// <summary>
/// Reason the recheck cycle paused. Used by FRG state machine to decide what to do next.
/// </summary>
public enum RecheckSideEffect
{
    /// <summary>Recheck completed normally — read the cached verdict via TryGetVerdict.</summary>
    None = 0,

    /// <summary>During Tab cycling we landed on a non-BL aggressor (in combat with us, not on
    /// IsNoEngage map). Caller should engage this guid via Combat plan; after that combat
    /// resolves, restart the recheck from Step 1.</summary>
    NonBlAggressorDiscovered,
}

/// <summary>
/// Per-guid cache entry tracking the recheck state for one aggressor.
/// </summary>
public sealed class RecheckCacheEntry
{
    /// <summary>Guid this entry tracks.</summary>
    public int Guid;

    /// <summary>Current verdict for this guid.</summary>
    public RecheckVerdict Verdict;

    /// <summary>UTC time the cache entry was created or last updated.</summary>
    public DateTime LastUpdateUtc;

    /// <summary>For SearchFailed verdicts: UTC when wait-retry began. Used to compute
    /// elapsed wait time against the 30s exhaustion ceiling.</summary>
    public DateTime SearchFailedUtc;

    /// <summary>Count of Tab presses done in the current cycle attempt (0..5).</summary>
    public int TabAttemptsThisCycle;

    /// <summary>If NonBlAggressorDiscovered fired during the last cycle attempt, the
    /// guid the cycle landed on (caller engages this via Combat plan).</summary>
    public int LastDiscoveredNonBlAggressor;
}

/// <summary>
/// Owns the recheck cache and exposes primitives consumed by FRG (Fix FX) and FFG (Fix GB).
///
/// Lifecycle:
///   • Constructed once at startup (registered as a singleton via the bot's DI).
///   • Each tick where a recheck is in flight, FRG calls AdvanceRecheck(guid) which
///     performs one of: Tab press + read, face (Interact + StepBackwards), settle wait,
///     position read + cache store. Returns true when verdict is finalized.
///   • Cache invalidated by InvalidateOnCombatExit, called from FRG when bits.Combat()
///     transitions true → false.
/// </summary>
public sealed class BlacklistRecheckCache
{
    // ── Tunables ──

    /// <summary>Maximum Tab presses per cycle attempt. Per operator answer to question on
    /// cycle bound (5 was the suggested ceiling).</summary>
    public const int MaxTabAttemptsPerCycle = 5;

    /// <summary>Milliseconds to wait between successive Tab presses to let the WoW UI
    /// publish the new target. Pulled from TargetFinder.waitMs (existing inter-Tab
    /// cadence in the codebase).</summary>
    public const int InterTabSettleMs = 200;

    /// <summary>Milliseconds to wait after PressInteract/StepBackwards for player
    /// orientation to update and addon TargetMapPos to refresh. Same value as
    /// FollowRouteGoal.BtEvalDelayMs (~700ms) — already tuned for this case.</summary>
    public const int FacingSettleMs = 700;

    /// <summary>Interval between wait-retry attempts after SearchFailed. Operator
    /// answer: 10s.</summary>
    public const int RetryIntervalMs = 10_000;

    /// <summary>Total wait-retry budget before declaring Resolved. Operator answer:
    /// 30s upper bound.</summary>
    public const int RetryExhaustionMs = 30_000;

    private readonly Dictionary<int, RecheckCacheEntry> _entries = new();
    private readonly object _lock = new();

    /// <summary>
    /// Read the current verdict for a guid. Returns Unknown if guid not in cache.
    /// </summary>
    public RecheckVerdict GetVerdict(int guid)
    {
        if (guid == 0) return RecheckVerdict.Unknown;
        lock (_lock)
        {
            return _entries.TryGetValue(guid, out var entry)
                ? entry.Verdict
                : RecheckVerdict.Unknown;
        }
    }

    /// <summary>
    /// Try to retrieve the full cache entry for a guid (e.g., to inspect timing or
    /// discovered non-BL aggressor). Returns null if not present.
    /// </summary>
    public RecheckCacheEntry? TryGetEntry(int guid)
    {
        if (guid == 0) return null;
        lock (_lock)
        {
            return _entries.TryGetValue(guid, out var entry) ? entry : null;
        }
    }

    /// <summary>
    /// Set or update a verdict for a guid. Call sites:
    ///   • Engagement-time / waypoint-arrival recheck completion (InRect / NotInRect).
    ///   • Tab cycle exhaustion (SearchFailed — starts wait-retry timer).
    ///   • Wait-retry exhaustion (Resolved).
    ///   • Recheck operation start (Pending).
    /// </summary>
    public void SetVerdict(int guid, RecheckVerdict verdict)
    {
        if (guid == 0) return;
        lock (_lock)
        {
            if (!_entries.TryGetValue(guid, out var entry))
            {
                entry = new RecheckCacheEntry { Guid = guid };
                _entries[guid] = entry;
            }
            entry.Verdict = verdict;
            entry.LastUpdateUtc = DateTime.UtcNow;
            if (verdict == RecheckVerdict.SearchFailed)
            {
                // Start (or reset, if a new search attempt failed) the wait-retry timer.
                // Per operator: timer should NOT reset on non-BL kill interruption, but
                // a fresh SearchFailed after a successful retry attempt does reset.
                if (entry.SearchFailedUtc == default)
                    entry.SearchFailedUtc = DateTime.UtcNow;
            }
            else if (verdict == RecheckVerdict.InRect || verdict == RecheckVerdict.NotInRect
                                                       || verdict == RecheckVerdict.Resolved)
            {
                entry.SearchFailedUtc = default;
                entry.TabAttemptsThisCycle = 0;
                entry.LastDiscoveredNonBlAggressor = 0;
            }
        }
    }

    /// <summary>
    /// Record that the current cycle's Tab press count incremented (used by FRG's
    /// state machine to bound attempts at MaxTabAttemptsPerCycle).
    /// </summary>
    public void RecordTabAttempt(int guid)
    {
        if (guid == 0) return;
        lock (_lock)
        {
            if (_entries.TryGetValue(guid, out var entry))
                entry.TabAttemptsThisCycle++;
        }
    }

    /// <summary>
    /// Reset the per-cycle Tab attempt counter (e.g., when starting a fresh retry
    /// attempt after the RetryIntervalMs wait, or when restarting a cycle after a
    /// non-BL aggressor was killed).
    /// </summary>
    public void ResetTabAttempts(int guid)
    {
        if (guid == 0) return;
        lock (_lock)
        {
            if (_entries.TryGetValue(guid, out var entry))
            {
                entry.TabAttemptsThisCycle = 0;
                entry.LastDiscoveredNonBlAggressor = 0;
            }
        }
    }

    /// <summary>
    /// Record that the Tab cycle landed on a non-BL aggressor (in combat with us,
    /// not on IsNoEngage map). FRG reads this to engage the discovered guid
    /// via Combat plan.
    /// </summary>
    public void RecordNonBlAggressorDiscovered(int rechecGuid, int aggressorGuid)
    {
        if (rechecGuid == 0) return;
        lock (_lock)
        {
            if (_entries.TryGetValue(rechecGuid, out var entry))
                entry.LastDiscoveredNonBlAggressor = aggressorGuid;
        }
    }

    /// <summary>
    /// True if the wait-retry budget (30s) has elapsed since SearchFailed began
    /// for this guid. FRG calls this each tick during the SearchFailed state to
    /// know when to transition to Resolved and exit backtrack.
    /// </summary>
    public bool IsRetryExhausted(int guid)
    {
        if (guid == 0) return false;
        lock (_lock)
        {
            if (!_entries.TryGetValue(guid, out var entry))
                return false;
            if (entry.Verdict != RecheckVerdict.SearchFailed)
                return false;
            if (entry.SearchFailedUtc == default)
                return false;
            return (DateTime.UtcNow - entry.SearchFailedUtc).TotalMilliseconds >= RetryExhaustionMs;
        }
    }

    /// <summary>
    /// True if it's time to attempt the next retry (RetryIntervalMs has elapsed
    /// since the last attempt). FRG ticks this in the SearchFailed state.
    /// </summary>
    public bool IsRetryDue(int guid)
    {
        if (guid == 0) return false;
        lock (_lock)
        {
            if (!_entries.TryGetValue(guid, out var entry))
                return false;
            if (entry.Verdict != RecheckVerdict.SearchFailed)
                return false;
            return (DateTime.UtcNow - entry.LastUpdateUtc).TotalMilliseconds >= RetryIntervalMs;
        }
    }

    /// <summary>
    /// Invalidate (remove) cache entries for guids that combat has left. Called
    /// when bits.Combat() transitions true → false — at that point the mob has
    /// either been killed or leashed and the cached result is stale. Per
    /// operator: cache invalidates only on combat exit.
    /// </summary>
    public void InvalidateOnCombatExit()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    /// <summary>
    /// Drop a single guid from the cache. Used by FRG when the backtrack
    /// finishes (success or exit) for that specific aggressor.
    /// </summary>
    public void Invalidate(int guid)
    {
        if (guid == 0) return;
        lock (_lock)
        {
            _entries.Remove(guid);
        }
    }

    /// <summary>True if any guid currently has Pending or SearchFailed status
    /// — used by FRG to know that a recheck operation is in flight.</summary>
    public bool AnyRecheckInFlight()
    {
        lock (_lock)
        {
            foreach (var e in _entries.Values)
            {
                if (e.Verdict == RecheckVerdict.Pending || e.Verdict == RecheckVerdict.SearchFailed)
                    return true;
            }
            return false;
        }
    }
}
