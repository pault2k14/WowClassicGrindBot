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
    partymembercombat, // Complex check of precondition like values
    partyleadercombat, // Complex check of precondition like values
    partyincombat, // Very simple check of if the player is in combat or focus is in combat
    drinking,
    eating,
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
        _ => unknown
    };

    public static string ToStringF(this GoapKey key, bool state)
    {
        return state ? ToStringTrue(key) : ToStringFalse(key);
    }
}