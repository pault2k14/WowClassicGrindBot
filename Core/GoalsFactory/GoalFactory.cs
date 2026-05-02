using Core.AreaBlacklist;
using Core.Database;
using Core.Goals;
using Core.GOAP;
using Core.Party;
using Core.Session;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SharedLib;

using System;
using System.Linq;
using System.Numerics;
using System.Threading;

using static Core.BlacklistSourceType;
using static Newtonsoft.Json.JsonConvert;
using static System.IO.File;
using static System.IO.Path;

namespace Core;

public static class GoalFactory
{
    public static IServiceProvider Create(
        IServiceCollection services,
        IServiceProvider sp, ClassConfiguration classConfig)
    {
        services.AddStartupIoC(sp);

        // session scoped services

        services.AddScoped<ConfigurableInput>();
        services.AddScoped<GoapAgentState>();

        services.AddScoped<CancellationTokenSource<GoapAgent>>();
        services.AddScoped<IGrindSessionHandler, GrindSessionHandler>();

        if (classConfig.LogBagChanges)
            services.AddScoped<IBagChangeTracker, BagChangeTracker>();
        else
            services.AddScoped<IBagChangeTracker, NoBagChangeTracker>();


        if (classConfig.Mode != Mode.Grind && classConfig.Mode != Mode.PartyLeader)
        {
            services.AddScoped<IBlacklist, NoBlacklist>();

            services.AddKeyedScoped<IBlacklist, NoBlacklist>(TARGET);
            services.AddKeyedScoped<IBlacklist, NoBlacklist>(MOUSE_OVER);
        }
        else
        {
            services.AddScoped<IBlacklistSource, BlacklistMouseOver>();
            services.AddScoped<IBlacklistSource, BlacklistTarget>();

            services.AddScoped<BlacklistMouseOver>();
            services.AddScoped<BlacklistTarget>();

            services.AddKeyedScoped<IBlacklist, Blacklist<BlacklistMouseOver>>(MOUSE_OVER);
            services.AddKeyedScoped<IBlacklist, Blacklist<BlacklistTarget>>(TARGET);

            services.AddScoped<IBlacklist>(x => x.GetRequiredKeyedService<IBlacklist>(TARGET));

            services.AddScoped<GoapGoal, BlacklistTargetGoal>();
        }

        services.AddScoped<NpcNameTargeting>();

        // Goals components
        services.AddScoped<PlayerDirection>();
        services.AddScoped<StopMoving>();
        services.AddScoped<ReactCastError>();
        services.AddScoped<CastingHandlerInterruptWatchdog>();
        services.AddScoped<CastingHandler>();
        services.AddScoped<StuckDetector>();
        services.AddScoped<CombatTracker>();
        services.AddScoped<SafeSpotCollector>();
        services.AddScoped<RestHandler>();

        var playerReader = sp.GetRequiredService<PlayerReader>();

        if (playerReader.Class is UnitClass.Druid)
        {
            services.AddScoped<IMountHandler, DruidMountHandler>();
            services.AddScoped<MountHandler>();
        }
        else
        {
            services.AddScoped<IMountHandler, MountHandler>();
        }

        services.AddScoped<TargetFinder>();

        if (classConfig.Mode == Mode.CorpseRun)
        {
            services.AddScoped<GoapGoal, WalkToCorpseGoal>();
        }
        else if (classConfig.Mode == Mode.AttendedGather)
        {
            services.AddScoped<GoapGoal, WalkToCorpseGoal>();
            services.AddScoped<GoapGoal, CombatGoal>();
            services.AddScoped<GoapGoal, ApproachTargetGoal>();
            services.AddScoped<GoapGoal, WaitForGatheringGoal>();
            ResolveFollowRouteGoal(services, classConfig);

            ResolveLootAndSkin(services, classConfig);

            ResolvePetClass(services, playerReader.Class);

            if (classConfig.Parallel.Sequence.Length > 0)
            {
                services.AddScoped<GoapGoal, ParallelGoal>();
            }

            ResolveAdhocGoals(services, classConfig);

            ResolveAdhocNPCGoal(services, classConfig,
                sp.GetRequiredService<DataConfig>());

            ResolveWaitGoal(services, classConfig);
        }
        else if (classConfig.Mode == Mode.AssistFocus)
        {
            if(classConfig.AssistPull)
            {
                services.AddScoped<GoapGoal, PullTargetGoal>();
            }

            services.AddScoped<GoapGoal, ApproachTargetGoal>();
            services.AddScoped<GoapGoal, AssistFocusGoal>();
            services.AddScoped<GoapGoal, PartyMember1Goal>();
            services.AddScoped<GoapGoal, PartyMember2Goal>();
            services.AddScoped<GoapGoal, PartyMember3Goal>();
            services.AddScoped<GoapGoal, PartyMember4Goal>();
            services.AddScoped<GoapGoal, CombatGoal>();

            ResolveLootAndSkin(services, classConfig);

            services.AddScoped<GoapGoal, TargetFocusTargetGoal>();
            services.AddScoped<GoapGoal, FollowFocusGoal>();
            services.AddScoped<GoapGoal, ForcedFollowGoal>();

            if (classConfig.Parallel.Sequence.Length > 0)
            {
                services.AddScoped<GoapGoal, ParallelGoal>();
            }

            ResolveAdhocGoals(services, classConfig);
        }
        else if (classConfig.Mode is Mode.Grind or Mode.AttendedGrind or Mode.PartyLeader)
        {
            if (classConfig.Mode == Mode.AttendedGrind)
            {
                services.AddScoped<GoapGoal, WaitGoal>();
            }
            else
            {
                ResolveFollowRouteGoal(services, classConfig);
            }

            if (classConfig.Mode is Mode.PartyLeader)
            {
                services.AddScoped<GoapGoal, PartyMember1Goal>();
                services.AddScoped<GoapGoal, PartyMember2Goal>();
                services.AddScoped<GoapGoal, PartyMember3Goal>();
                services.AddScoped<GoapGoal, PartyMember4Goal>();
            }

            services.AddScoped<GoapGoal, WalkToCorpseGoal>();
            services.AddScoped<GoapGoal, PullTargetGoal>();
            services.AddScoped<GoapGoal, ApproachTargetGoal>();
            AddFleeGoal(services, classConfig);
            services.AddScoped<GoapGoal, CombatGoal>();

            if (classConfig.WrongZone.ZoneId > 0)
            {
                services.AddScoped<GoapGoal, WrongZoneGoal>();
            }

            ResolveLootAndSkin(services, classConfig);

            ResolvePetClass(services, playerReader.Class);

            if (classConfig.Parallel.Sequence.Length > 0)
            {
                services.AddScoped<GoapGoal, ParallelGoal>();
            }

            ResolveAdhocGoals(services, classConfig);

            ResolveAdhocNPCGoal(services, classConfig,
                sp.GetRequiredService<DataConfig>());

            ResolveWaitGoal(services, classConfig);
        }

        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
    }

    private static void ResolveLootAndSkin(IServiceCollection services,
        ClassConfiguration classConfig)
    {

        if (classConfig.Loot)
        {
            services.AddScoped<GoapGoal, ConsumeCorpseGoal>();
            services.AddScoped<GoapGoal, CorpseConsumedGoal>();

            services.AddScoped<GoapGoal, LootGoal>();

            if (classConfig.GatherCorpse)
            {
                services.AddScoped<GoapGoal, SkinningGoal>();
            }
        }
    }

    private static void ResolveAdhocGoals(IServiceCollection services,
        ClassConfiguration classConfig)
    {
        for (int i = 0; i < classConfig.Adhoc.Sequence.Length; i++)
        {
            KeyAction keyAction = classConfig.Adhoc.Sequence[i];
            services.AddScoped<GoapGoal, AdhocGoal>(x => new(keyAction,
                x.GetRequiredService<Microsoft.Extensions.Logging.ILogger>(),
                x.GetRequiredService<ConfigurableInput>(),
                x.GetRequiredService<Wait>(),
                x.GetRequiredService<PlayerReader>(),
                x.GetRequiredService<StopMoving>(),
                x.GetRequiredService<CastingHandler>(),
                x.GetRequiredService<IMountHandler>(),
                x.GetRequiredService<AddonBits>(),
                x.GetRequiredService<CombatLog>(),
                x.GetRequiredService<RestHandler>(),
                x.GetRequiredService<ChatReader>()));
        }
    }

    private static void ResolveAdhocNPCGoal(IServiceCollection services,
        ClassConfiguration classConfig, DataConfig dataConfig)
    {
        for (int i = 0; i < classConfig.NPC.Sequence.Length; i++)
        {
            KeyAction keyAction = classConfig.NPC.Sequence[i];
            keyAction.Path = GetPath(keyAction, dataConfig);

            services.AddScoped<GoapGoal, AdhocNPCGoal>(x => new(keyAction,
                x.GetRequiredService<ILogger<AdhocNPCGoal>>(),
                x.GetRequiredService<ConfigurableInput>(),
                x.GetRequiredService<Wait>(),
                x.GetRequiredService<PlayerReader>(),
                x.GetRequiredService<GossipReader>(),
                x.GetRequiredService<AddonBits>(),
                x.GetRequiredService<Navigation>(),
                x.GetRequiredService<StopMoving>(),
                x.GetRequiredService<AreaDB>(),
                x.GetRequiredService<NpcNameTargeting>(),
                x.GetRequiredService<ClassConfiguration>(),
                x.GetRequiredService<BagReader>(),
                x.GetRequiredService<SessionStat>(),
                x.GetRequiredService<IMountHandler>(),
                x.GetRequiredService<ExecGameCommand>(),
                x.GetRequiredService<CancellationTokenSource>()));
        }
    }

    private static void ResolveWaitGoal(IServiceCollection services,
        ClassConfiguration classConfig)
    {
        for (int i = 0; i < classConfig.Wait.Sequence.Length; i++)
        {
            KeyAction keyAction = classConfig.Wait.Sequence[i];

            services.AddScoped<GoapGoal, ConditionalWaitGoal>(x => new(
                keyAction,
                x.GetRequiredService<ILogger<ConditionalWaitGoal>>(),
                x.GetRequiredService<Wait>()));
        }
    }

    private static void ResolvePetClass(IServiceCollection services,
        UnitClass @class)
    {
        if (@class is
            UnitClass.Hunter or
            UnitClass.Warlock or
            UnitClass.Mage or
            UnitClass.DeathKnight)
        {
            services.AddScoped<GoapGoal, TargetPetTargetGoal>();
        }
    }


    public static void ResolveFollowRouteGoal(IServiceCollection services,
        ClassConfiguration classConfig)
    {
        float baseCost = FollowRouteGoal.DEFAULT_COST;

        for (int i = 0; i < classConfig.Paths.Length; i++)
        {
            int index = i;
            float cost = baseCost + (index * FollowRouteGoal.COST_OFFSET);

            services.AddKeyedScoped<PathSettings>(i,
                (IServiceProvider sp, object? key) =>
                GetPathSettings(
                    sp.GetRequiredService<ClassConfiguration>().Paths[(int)key!],
                    sp.GetRequiredService<DataConfig>()));

            services.AddScoped<GoapGoal, FollowRouteGoal>(x => new(
                cost,
                x.GetRequiredKeyedService<PathSettings>(index),
                x.GetRequiredService<ILogger<FollowRouteGoal>>(),
                x.GetRequiredService<ConfigurableInput>(),
                x.GetRequiredService<Wait>(),
                x.GetRequiredService<PlayerReader>(),
                x.GetRequiredService<AddonBits>(),
                x.GetRequiredService<ClassConfiguration>(),
                x.GetRequiredService<Navigation>(),
                x.GetRequiredService<IMountHandler>(),
                x.GetRequiredService<TargetFinder>(),
                x.GetRequiredService<IBlacklist>(),
                x.GetRequiredService<RestHandler>(),
                x.GetRequiredService<ChatReader>(),
                x.GetRequiredService<AssistStateStore>(),
                x.GetRequiredService<LeaderNavigationProvider>()  // waypoint + blacklist sharing
                ));
        }
    }

    public static void AddFleeGoal(IServiceCollection services, ClassConfiguration classConfig)
    {
        if (classConfig.Flee.Sequence.Length == 0)
            return;

        services.AddScoped<GoapGoal, FleeGoal>();
    }

    private static string RelativeFilePath(DataConfig dataConfig, string path)
    {
        return !path.Contains(dataConfig.Path)
            ? Join(dataConfig.Path, path)
            : path;
    }

    public static PathSettings GetPathSettings(PathSettings setting, DataConfig dataConfig)
    {
        setting.PathFilename = RelativeFilePath(dataConfig, setting.PathFilename);

        string json = ReadAllText(setting.PathFilename);

        Vector3[] waypoints;
        BlacklistRect[] mapRects;

        // 1) Try new format
        // 2) Fallback to old format
        try
        {
            var route = DeserializeObject<RouteFile>(json);
            if (route?.Waypoints is { Length: > 0 })
            {
                waypoints = route.Waypoints;

                mapRects = (route.Blacklists ?? Array.Empty<BlacklistRectDto>())
                    .Select(r => new BlacklistRect(r.MinX, r.MinY, r.MaxX, r.MaxY).Normalized())
                    .ToArray();
            }
            else
            {
                waypoints = DeserializeObject<Vector3[]>(json)!;
                mapRects = Array.Empty<BlacklistRect>();
            }
        }
        catch
        {
            waypoints = DeserializeObject<Vector3[]>(json)!;
            mapRects = Array.Empty<BlacklistRect>();
        }

        setting.Path = waypoints ?? Array.Empty<Vector3>();
        setting.MapBlacklistRects = mapRects ?? Array.Empty<BlacklistRect>();

        Console.WriteLine($"Loaded path: {setting.Path.Length} points, blacklists: {setting.MapBlacklistRects.Length} rects, file: {setting.PathFilename}");

        // Your existing Z normalization behavior
        for (int i = 0; i < setting.Path.Length; i++)
        {
            if (setting.Path[i].Z != 0)
                setting.Path[i].Z = 0;
        }

        if (!setting.PathReduceSteps)
            return setting;

        int step = 2;
        int reducedLength = setting.Path.Length % step == 0
            ? setting.Path.Length / step
            : (setting.Path.Length / step) + 1;

        Vector3[] path = new Vector3[reducedLength];
        for (int i = 0; i < path.Length; i++)
        {
            path[i] = setting.Path[i * step];
        }

        setting.Path = path;
        return setting;
    }

    public static Vector3[] GetPath(KeyAction keyAction, DataConfig dataConfig)
    {
        return string.IsNullOrEmpty(keyAction.PathFilename)
            ? Array.Empty<Vector3>()
            : DeserializeObject<Vector3[]>(
            ReadAllText(RelativeFilePath(dataConfig, keyAction.PathFilename)))!;
    }

    private sealed class RouteFile
    {
        public Vector3[]? Waypoints { get; set; }
        public BlacklistRectDto[]? Blacklists { get; set; }
    }

    private sealed class BlacklistRectDto
    {
        public float MinX { get; set; }
        public float MinY { get; set; }
        public float MaxX { get; set; }
        public float MaxY { get; set; }
    }
}
