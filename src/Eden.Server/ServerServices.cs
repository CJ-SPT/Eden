using System.Reflection;
using Eden.Gameplay.Combat;
using Eden.Protocol;
using Eden.Server.Launcher;
using Eden.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Server;

internal static class ServerServices
{
    public static ServiceProvider Build(
        WorldDatabase database,
        TimeProvider? clock = null,
        LauncherOptions? options = null,
        INpcPopulationSeeder? population = null,
        bool liveWorld = false,
        bool combatObjects = true,
        bool residentReads = true
    )
    {
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton(database);
        if (liveWorld)
        {
            services.AddSingleton<LiveWorld>(_ =>
                new(database, clock ?? TimeProvider.System, combatObjects: combatObjects, residentReads: residentReads)
            );
        }
        if (population is null)
        {
            services.AddSingleton<ResidentFitCache>(_ => new(database));
        }
        services.AddSingleton(new NpcProfileCatalog(cache: population is null));
        services.AddSingleton(clock ?? TimeProvider.System);
        services.AddSingleton(options ?? new LauncherOptions());
        foreach (Assembly assembly in new[] { typeof(ServerServices).Assembly, typeof(InjectableAttribute).Assembly })
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
                foreach (
                    Type contract in implementation
                        .GetInterfaces()
                        .Where(i =>
                            i.Assembly == typeof(InjectableAttribute).Assembly
                            || i.Assembly == typeof(ServerServices).Assembly
                            || i.Assembly == typeof(IPilotHealth).Assembly
                        )
                )
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
        _ = provider.GetService<LiveWorld>();
        _ = provider.GetService<ResidentFitCache>();
        return provider;
    }
}
