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
        _ => unknown
    };

    public static string ToStringF(this GoapKey key, bool state)
    {
        return state ? ToStringTrue(key) : ToStringFalse(key);
    }
}
