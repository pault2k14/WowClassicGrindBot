using Game;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharedLib.NpcFinder;
using SharpDX.DirectWrite;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Formats.Tar;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Vortice.Direct3D11;
using static Core.AddonTicks;
using static System.Diagnostics.Stopwatch;
using static System.Math;

namespace Core.Goals;

public sealed partial class RestHandler
{
#if DEBUG
    private const bool DEBUG = true;
#else
    private const bool DEBUG = false;
#endif

    private readonly ILogger<RestHandler> logger;
    private readonly bool Log;
    private readonly ConfigurableInput input;

    private readonly Wait wait;
    private readonly PlayerReader playerReader;
    private readonly AddonReader addonReader;
    private readonly AddonBits bits;

    private readonly CombatLog combatLog;
    private readonly ClassConfiguration classConfig;
    private BuffStatus<IPlayer> playerBuffs;
    private readonly FrozenDictionary<string, Func<bool>> boolVariables;

    public RestHandler(
        ILogger<RestHandler> logger,
        ConfigurableInput input,
        ClassConfiguration classConfig,
        AddonBits bits,
        Wait wait,
        AddonReader addonReader,
        PlayerReader playerReader,
        CombatLog combatLog,
        IServiceProvider sp)
    {
        this.logger = logger;
        this.input = input;

        this.wait = wait;
        this.bits = bits;
        this.addonReader = addonReader;
        this.playerReader = playerReader;

        this.combatLog = combatLog;

        this.classConfig = classConfig;
        Log = classConfig.Log;
        this.playerBuffs = sp.GetRequiredService<BuffStatus<IPlayer>>();

        Dictionary<string, Func<bool>> boolVariables = new(StringComparer.InvariantCultureIgnoreCase);

        AddAura("", boolVariables, playerBuffs);
        this.boolVariables = boolVariables.ToFrozenDictionary();
    }

    public bool IsResting()
    {
        if((playerReader.HealthPercent() != 100 && IsEating() && (!bits.Combat() && !bits.Focus_Combat()) ) 
            || (playerReader.ManaPercent() != 100 && IsDrinking() && (!bits.Combat() && !bits.Focus_Combat())))
        {
            return true;
        }

        return false;
    }

    public bool IsEating()
    {
        return HasAura("Food");
    }

    public bool IsDrinking()
    {
        return HasAura("Drink");
    }

    private bool HasAura(string requirement)
    {
        var spanLookupBool = boolVariables.GetAlternateLookup<ReadOnlySpan<char>>();
        if (!spanLookupBool.TryGetValue(requirement, out Func<bool>? value))
        {
            return false;
        }

        return value();
    }

    private static void AddAura<T>(string prefix,
    Dictionary<string, Func<bool>> boolVariables, T t) where T : notnull
    {
        foreach (MethodInfo mInfo in t.GetType().GetMethods(
            BindingFlags.DeclaredOnly |
            BindingFlags.Public | BindingFlags.Instance))
        {
            if (mInfo.ReturnType != typeof(bool))
                continue;

            NamesAttribute? names =
                (NamesAttribute?)Attribute.GetCustomAttribute(
                    mInfo, typeof(NamesAttribute));

            if (names is not null)
            {
                foreach (string name in names.Values)
                {
                    boolVariables.Add($"{prefix}{name}",
                        mInfo.CreateDelegate<Func<bool>>(t));
                }
            }
            else
            {
                string name = $"{prefix}{mInfo.Name.Replace("_", " ")}";
                boolVariables.Add(name, mInfo.CreateDelegate<Func<bool>>(t));
            }
        }
    }



}