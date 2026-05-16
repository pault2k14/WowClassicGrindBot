using System;

namespace Core.GOAP;

public enum GoapKey
{
    hastarget,
    dangercombat,
    damagetaken,
    damagedone,
    damagetakenordone,
    targetisalive,
    targettargetsus,
    incombat,
    pethastarget,
    ismounted,
    withinpullrange,
    incombatrange,
    pulled,
    isdead,
    shouldloot,
    shouldgather,
    producedcorpse,
    consumecorpse,
    isswimming,
    itemsbroken,
    gathering,
    targethostile,
    hasfocus,
    focushastarget,
    focuscombat,
    consumablecorpsenearby,
    forcedfollow,
    assistisfollowing,
    assistrequestreturn,
    assistrequestreturnorisfollowing,
    assistshouldfollow,
    partymembercombat,
    partyleadercombat,
    partyincombat,
    drinking,
    eating,
    inblacklistarea,
    focusconnected,
    party1connected,
    party2connected,
    party3connected,
    party4connected,
    leaderWaitingForAssist,

    /// <summary>
    /// True for ~10 seconds after an evading mob is detected.
    /// CombatGoal and ApproachTargetGoal both require this to be false,
    /// preventing both bots from re-entering combat while navigating away.
    /// Set by GoapAgent when it handles EvadeBlacklistEvent.
    /// Automatically cleared after the recovery timer elapses.
    /// </summary>
    evadeRecovery,

    /// <summary>
    /// Compound gate for PartyLeader FollowRouteGoal.
    /// True when the leader is permitted to follow the patrol route:
    ///   - During evade recovery (bypass all combat/corpse checks so the
    ///     leader can navigate toward the assist regardless of fight state), OR
    ///   - Outside evade recovery AND no ongoing combat indicators AND no
    ///     pending corpse/consume cycle that should be resolved first.
    /// Computed by GoapAgent.CanPartyLeaderFollowRoute().
    /// </summary>
    partyleadercanfollowroute,

    /// <summary>
    /// True while a pather-based approach escape is actively in progress.
    /// Set from Navigation.IsApproachEscapeActive each tick in UpdateWorldState.
    /// PullTargetGoal requires this to be false — preventing it from being
    /// selected while ApproachTargetGoal owns an in-progress escape route,
    /// eliminating the ATG/PTG thrashing that occurs during escape navigation.
    /// ApproachTargetGoal has no such precondition so it can run during both
    /// normal approach and escape ownership.
    /// </summary>
    approachEscapeActive,

    /// <summary>
    /// True when the player has no target, OR the current target's GUID is
    /// in <c>PlayerReader.IsIgnored</c> (recently blacklisted via evade).
    /// "Ignored" semantics: nothing fightable in this slot.
    /// </summary>
    targetIsIgnored,

    /// <summary>
    /// True when the player has no focus target, OR the focus target's GUID
    /// is in <c>PlayerReader.IsIgnored</c>. Same "nothing fightable here"
    /// semantics as <see cref="targetIsIgnored"/>.
    /// </summary>
    focusTargetIsIgnored,

    /// <summary>
    /// True when both <see cref="targetIsIgnored"/> and
    /// <see cref="focusTargetIsIgnored"/> are true — i.e. neither the bot's
    /// own target nor its focus's target offers anything fightable.
    /// CombatGoal in PartyLeader/AssistFocus modes requires this to be false,
    /// which lets Combat select either when the bot has its own fightable
    /// target OR when only its focus's target is fightable (in which case
    /// CombatGoal.Update swaps to it via PressTargetFocus + PressTargetOfTarget).
    /// During evade recovery, both slots typically reference the freshly
    /// blacklisted mob → both ignored → key true → Combat blocked → planner
    /// falls back to FRG/FFG. When a non-blacklisted aggressor appears in
    /// either slot the key flips false and Combat becomes selectable.
    /// </summary>
    allPartyTargetsIsIgnored,

    /// <summary>
    /// Fix AH (log-73 16:43:37 → 16:44:15, ~37 s assist wedge): true when the
    /// party is either already in combat (<c>PartyInCombat()</c>) OR the
    /// leader has published an approach-start anchor
    /// (<c>LeaderNavigationProvider.HasApproachStart</c>). Used as
    /// ApproachTargetGoal's AssistFocus precondition in place of the
    /// previous <see cref="partyincombat"/> requirement.
    /// <para>
    /// Rationale: the leader's ATG OnEnter publishes the approach anchor and
    /// begins pressing Interact on its hard-acquired target. Combat does not
    /// actually start until the leader reaches the mob (~8.5 s later in
    /// log-73). During that pre-combat window the assist's ATG was un-
    /// selectable (<c>partyincombat=false</c>), so the assist fell back to
    /// FollowFocusGoal's PositionChase — which the pather cannot route
    /// through terrain that the leader's Interact key auto-walks across
    /// (rocks at <c>Z=51.86</c> in log-73). By the time combat finally fired
    /// the assist was already 23 y behind, wedged at
    /// <c>&lt;-389.84, -4137.61&gt;</c>, and escalated to CantFollow.
    /// </para>
    /// <para>
    /// With <c>partyEngaging=true</c> during the leader's approach phase,
    /// ATG (cost 8) becomes selectable on the assist as soon as
    /// <c>hastarget=true</c> is satisfied — see the focus-chain target
    /// acquisition in <c>FollowFocusGoal</c> (Fix AH Part 1) which presses
    /// PressTargetFocus + PressTargetOfTarget at the approach anchor. Once
    /// ATG owns the goal slot, its AssistFocus branch presses
    /// Approach every tick on the leader's hard target — the same
    /// interact-key auto-walk mechanism the leader uses to traverse
    /// pather-unfriendly terrain.
    /// </para>
    /// <para>
    /// Note: this key does NOT replace <c>partyincombat</c> generally — only
    /// ATG's AssistFocus precondition is migrated. <c>partyincombat</c> is
    /// preserved verbatim because FRG and other goals depend on its
    /// "combat is actually active" semantics; broadening it would cause
    /// FRG to abort patrol the moment the leader publishes an anchor.
    /// </para>
    /// </summary>
    partyEngaging,

    LENGTH
}

public static class GoapKey_Extension
{
    private const string unknown = "Unknown";

    private static string ToStringTrue(GoapKey value) => value switch
    {
        GoapKey.hastarget => "Target",
        GoapKey.dangercombat => "Danger",
        GoapKey.damagetaken => "Damage Taken",
        GoapKey.damagedone => "Damage Done",
        GoapKey.targetisalive => "Target alive",
        GoapKey.targettargetsus => "Targets us",
        GoapKey.incombat => "Combat",
        GoapKey.pethastarget => "Pet target",
        GoapKey.ismounted => "Mounted",
        GoapKey.withinpullrange => "Pull range",
        GoapKey.incombatrange => "Combat range",
        GoapKey.pulled => "Pulled",
        GoapKey.isdead => "Dead",
        GoapKey.shouldloot => "Loot",
        GoapKey.shouldgather => "Gather",
        GoapKey.producedcorpse => "Killing blow",
        GoapKey.consumecorpse => "Consume Corpse",
        GoapKey.isswimming => "Swimming",
        GoapKey.itemsbroken => "Broken",
        GoapKey.gathering => "Gathering",
        GoapKey.hasfocus => "Focus",
        GoapKey.focushastarget => "Focus Target",
        GoapKey.focuscombat => "Focus Combat",
        GoapKey.targethostile => "Target Hostile",
        GoapKey.damagetakenordone => "Damage Taken or Done",
        GoapKey.consumablecorpsenearby => "Consume Corpse nearby",
        GoapKey.forcedfollow => "Forced Follow",
        GoapKey.assistisfollowing => "assist is following",
        GoapKey.assistrequestreturn => "assist request return",
        GoapKey.assistrequestreturnorisfollowing => "assist request return or is following",
        GoapKey.assistshouldfollow => "assist should follow",
        GoapKey.partymembercombat => "party member in combat",
        GoapKey.partyleadercombat => "party leader in combat",
        GoapKey.partyincombat => "party in combat",
        GoapKey.eating => "eating",
        GoapKey.drinking => "drinking",
        GoapKey.inblacklistarea => "in blacklist area",
        GoapKey.focusconnected => "focus connected",
        GoapKey.party1connected => "party member 1 connected",
        GoapKey.party2connected => "party member 2 connected",
        GoapKey.party3connected => "party member 3 connected",
        GoapKey.party4connected => "party member 4 connected",
        GoapKey.leaderWaitingForAssist => "leader waiting for assist",
        GoapKey.evadeRecovery => "evade recovery",
        GoapKey.partyleadercanfollowroute => "leader can follow route",
        GoapKey.approachEscapeActive => "approach escape active",
        GoapKey.targetIsIgnored => "target is ignored or absent",
        GoapKey.focusTargetIsIgnored => "focus target is ignored or absent",
        GoapKey.allPartyTargetsIsIgnored => "all party targets ignored or absent",
        GoapKey.partyEngaging => "party engaging (in combat or leader approaching)",
        _ => unknown
    };

    private static string ToStringFalse(GoapKey value) => value switch
    {
        GoapKey.hastarget => "!Target",
        GoapKey.dangercombat => "!Danger",
        GoapKey.damagetaken => "!Damage Taken",
        GoapKey.damagedone => "!Damage Done",
        GoapKey.targetisalive => "!Target alive",
        GoapKey.targettargetsus => "!Targets us",
        GoapKey.incombat => "!Combat",
        GoapKey.pethastarget => "!Pet target",
        GoapKey.ismounted => "!Mounted",
        GoapKey.withinpullrange => "!Pull range",
        GoapKey.incombatrange => "!Combat range",
        GoapKey.pulled => "!Pulled",
        GoapKey.isdead => "!Dead",
        GoapKey.shouldloot => "!Loot",
        GoapKey.shouldgather => "!Gather",
        GoapKey.producedcorpse => "!Killing blow",
        GoapKey.consumecorpse => "!Consume Corpse",
        GoapKey.isswimming => "!Swimming",
        GoapKey.itemsbroken => "!Broken",
        GoapKey.gathering => "!Gathering",
        GoapKey.hasfocus => "!Focus",
        GoapKey.focushastarget => "!Focus Target",
        GoapKey.focuscombat => "!Focus Combat",
        GoapKey.targethostile => "!Target Hostile",
        GoapKey.damagetakenordone => "!Damage Taken or Done",
        GoapKey.consumablecorpsenearby => "!Consume Corpse nearby",
        GoapKey.forcedfollow => "!Forced Follow",
        GoapKey.assistisfollowing => "!assist is following",
        GoapKey.assistrequestreturn => "!assist request return",
        GoapKey.assistrequestreturnorisfollowing => "!assist request return or is following",
        GoapKey.assistshouldfollow => "!assist should follow",
        GoapKey.partymembercombat => "!party member in combat",
        GoapKey.partyleadercombat => "!party leader in combat",
        GoapKey.partyincombat => "!party in combat",
        GoapKey.eating => "!eating",
        GoapKey.drinking => "!drinking",
        GoapKey.inblacklistarea => "!in blacklist area",
        GoapKey.focusconnected => "!focus connected",
        GoapKey.party1connected => "!party member 1 connected",
        GoapKey.party2connected => "!party member 2 connected",
        GoapKey.party3connected => "!party member 3 connected",
        GoapKey.party4connected => "!party member 4 connected",
        GoapKey.leaderWaitingForAssist => "!leader waiting for assist",
        GoapKey.evadeRecovery => "!evade recovery",
        GoapKey.partyleadercanfollowroute => "!leader can follow route",
        GoapKey.approachEscapeActive => "!approach escape active",
        GoapKey.targetIsIgnored => "!target is ignored or absent",
        GoapKey.focusTargetIsIgnored => "!focus target is ignored or absent",
        GoapKey.allPartyTargetsIsIgnored => "!all party targets ignored or absent",
        GoapKey.partyEngaging => "!party engaging (not in combat and leader not approaching)",
        _ => unknown
    };

    public static string ToStringF(this GoapKey key, bool state)
    {
        return state ? ToStringTrue(key) : ToStringFalse(key);
    }
}
