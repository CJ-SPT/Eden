using System.Reflection;
using Eden.Protocol;
using Eden.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Server;

internal static class ServerServices
{
    public static ServiceProvider Build(WorldDatabase database)
    {
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton(database);
        services.AddSingleton(TimeProvider.System);
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
                        .Where(i => i.Assembly == typeof(InjectableAttribute).Assembly || i.Assembly == typeof(ServerServices).Assembly)
                )
                {
                    services.Add(new ServiceDescriptor(contract, provider => provider.GetRequiredService(implementation), lifetime));
                }
            }
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
