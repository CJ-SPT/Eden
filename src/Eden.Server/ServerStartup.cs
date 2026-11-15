using System.Diagnostics;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Server;

internal static class ServerStartup
{
    internal static T Measure<T>(string name, Func<T> start)
    {
        long started = Stopwatch.GetTimestamp();
        using var performance = SessionPerformance.Begin($"startup.{name}", ServerLog.Console.Trace);
        T result = start();
        ServerLog.Console.Information($"Startup {name} completed in {Stopwatch.GetElapsedTime(started).TotalSeconds:F2}s.");
        return result;
    }

    internal static void Measure(string name, Action start) =>
        Measure(
            name,
            () =>
            {
                start();
                return true;
            }
        );

    internal static void WarmCatalogs(IServiceProvider services, WorldDatabase database)
    {
        using var reads = new CatalogReadScope().Begin();
        Measure("core catalog", () => services.GetRequiredService<CatalogProvider>().PreloadCore());
        Measure("market catalog", () => services.GetRequiredService<PilotMarketStore>().WarmMarketCatalog());
        Measure("price catalog", () => services.GetRequiredService<Eden.Protocol.Industry.EconomicPriceCache>().Warm());
        Measure("NPC catalog", () => services.GetRequiredService<NpcPopulationSeeder>().WarmCatalog(database));
    }
}
