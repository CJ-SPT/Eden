using System.Reflection;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Server.Launcher;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Configuration;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Server;

internal static class ServerServices
{
    private static readonly Assembly[] ImplementationAssemblies =
    [
        typeof(ServerServices).Assembly,
        typeof(IPilotSession).Assembly,
        typeof(SqliteWorld).Assembly,
    ];
    private static readonly HashSet<Assembly> ContractAssemblies =
    [
        .. ImplementationAssemblies,
        typeof(IPilotHealth).Assembly,
        typeof(IPilotOperationScope).Assembly,
    ];

    public static ServiceProvider Build(
        WorldDatabase database,
        TimeProvider? clock = null,
        LauncherOptions? options = null,
        INpcPopulationSeeder? population = null,
        bool liveWorld = false,
        bool combatObjects = true,
        bool residentReads = true,
        bool simulationObjects = true,
        bool loadConfiguration = true
    )
    {
        if (loadConfiguration)
        {
            WorldConfigurationStore.LoadActive(database);
        }
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton(database);
        services.AddSingleton(_ => CatalogProvider.Open(database.CatalogPath));
        services.AddSingleton(new SessionDiagnostics(ServerLog.Console.Information, ServerLog.Console.Warning, ServerLog.Console.Trace));
        if (liveWorld)
        {
            services.AddSingleton<LiveWorld>(_ =>
                new(
                    database,
                    clock ?? TimeProvider.System,
                    combatObjects: combatObjects,
                    residentReads: residentReads,
                    simulationObjects: simulationObjects
                )
            );
        }
        if (population is null)
        {
            services.AddSingleton<ResidentFitCache>(_ => new(database));
        }
        services.AddSingleton(new NpcProfileCatalog(cache: population is null));
        services.AddSingleton(clock ?? TimeProvider.System);
        services.AddSingleton(options ?? new LauncherOptions());
        foreach (Assembly assembly in ImplementationAssemblies)
        {
            foreach (Type implementation in assembly.GetTypes())
            {
                if (implementation.GetCustomAttribute<InjectableAttribute>() is not { } attribute)
                {
                    continue;
                }
                ServiceLifetime lifetime = attribute.Lifetime switch
                {
                    InjectionType.Transient => ServiceLifetime.Transient,
                    InjectionType.Scoped => ServiceLifetime.Scoped,
                    InjectionType.Singleton => ServiceLifetime.Singleton,
                    _ => throw new InvalidOperationException($"Unknown service lifetime on {implementation}."),
                };
                services.Add(new ServiceDescriptor(implementation, implementation, lifetime));
                foreach (Type contract in implementation.GetInterfaces().Where(i => ContractAssemblies.Contains(i.Assembly)))
                {
                    services.Add(new ServiceDescriptor(contract, provider => provider.GetRequiredService(implementation), lifetime));
                }
            }
        }

        if (population is not null)
        {
            services.AddSingleton(population);
        }
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _ = provider.GetRequiredService<CatalogLifetime>();
        _ = provider.GetService<LiveWorld>();
        _ = provider.GetService<ResidentFitCache>();
        if (liveWorld)
        {
            // Build the immutable directory before admitting clients, so their first
            // GetAgents/owner lookup never pays the catalog materialization cost.
            _ = provider.GetRequiredService<INpcDirectory>().Agents();
        }
        return provider;
    }
}
