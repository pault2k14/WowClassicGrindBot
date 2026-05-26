// Core/GOAP/Events/EvadeBlacklistEvent.cs
// (EXISTING file in this folder — modify in place; do not add a second copy.)
//
// Fired by CombatGoal / ApproachTargetGoal / PullTargetGoal whenever the leader
// calls playerReader.IgnoreTarget() on an evading or unreachable mob.
// GoapAgent.HandleGoapEvent() catches it locally; cross-process the GUID (and the
// in-rect subset) reach the partner via LeaderState.BlacklistedMobGuids /
// NoEngageMobGuids over the party API channel (not party chat).

namespace Core.GOAP;

/// <summary>
/// Why an <see cref="EvadeBlacklistEvent"/> was dispatched. Only
/// <see cref="RealEvade"/> arms the self-defense recovery window + leader pause in
/// GoapAgent; the others blacklist the GUID without those recovery semantics.
/// See CHANGESET2_E_DESIGN.
/// </summary>
public enum EvadeReason
{
    /// <summary>Game reported the mob evading/resetting (combatLog.EvadeMobs / NearestTarget evading).</summary>
    RealEvade,
    /// <summary>Bot could not reach/pull the mob (rect/terrain bail). Not a game evade.</summary>
    ReachabilityBail,
    /// <summary>Re-dispatch of an already-known / API / leader-relayed blacklist GUID.</summary>
    Propagation,
    /// <summary>Ghost combat escape (zero GUID); handler keys on GUID==0.</summary>
    GhostCombat,
}

public sealed class EvadeBlacklistEvent : GoapEventArgs
{
    /// <summary>The unit GUID that was ignored/blacklisted due to evade.</summary>
    public int TargetGuid { get; }

    /// <summary>Why this event was dispatched (gates recovery-window semantics).</summary>
    public EvadeReason Reason { get; }

    /// <summary>True when the mob was determined to be INSIDE a blacklist rect at
    /// blacklist time (one-shot, facing-valid read). The receiver records this via
    /// IgnoreTarget(guid, inRect:true), which makes playerReader.IsNoEngage(guid) true:
    /// self-defense is suppressed and the finder skips it even while it damages us.
    /// False = plain blacklist/evade (still fightable in self-defense).
    /// See CHANGESET2_E4_D_DESIGN §7.</summary>
    public bool InRect { get; }

    public EvadeBlacklistEvent(int targetGuid, EvadeReason reason = EvadeReason.RealEvade, bool inRect = false)
    {
        TargetGuid = targetGuid;
        Reason = reason;
        InRect = inRect;
    }
}
