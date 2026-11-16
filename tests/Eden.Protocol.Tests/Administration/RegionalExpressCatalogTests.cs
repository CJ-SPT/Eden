using System.Text.Json;
using Eden.Gameplay.Flight;
using Eden.Modding;
using Eden.World.Catalog;
using Eden.World.Modding;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Administration;

internal sealed class TravelCatalogFactAttribute : FactAttribute
{
    public TravelCatalogFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EDEN_TEST_TRAVEL_CATALOG")))
        {
            Skip = "Set EDEN_TEST_TRAVEL_CATALOG to the pinned original catalog for complete network validation.";
        }
    }
}

public sealed class RegionalExpressCatalogTests
{
    [TravelCatalogFact]
    public void EntirePinnedNetworkComposesWithRegionalModelsAndAtMostThreeInterchangeJumps()
    {
        string original = Path.GetFullPath(Environment.GetEnvironmentVariable("EDEN_TEST_TRAVEL_CATALOG")!);
        WorldDatabase database = new(Path.Combine(Path.GetDirectoryName(original)!, "unused-express-validation-world.sqlite"));
        var routes = RegionalExpressPackage.Create(database);
        Assert.Equal(
            JsonSerializer.Serialize(routes, ModFormat.Json),
            JsonSerializer.Serialize(RegionalExpressPackage.Create(database), ModFormat.Json)
        );
        var originalCatalog = CatalogProvider.Open(original);
        Assert.Equal(69, routes.Count);
        var endpoints = routes.SelectMany(route => new[] { route.A, route.B }).ToArray();
        Assert.Equal(endpoints.Length, endpoints.Select(endpoint => endpoint.GateId).Distinct().Count());
        var regions = endpoints.Select(endpoint => originalCatalog.Systems[endpoint.SystemId].RegionId).Distinct().ToArray();
        Assert.Equal(64, regions.Length);
        Assert.DoesNotContain(10000004, regions);
        Assert.DoesNotContain(10000070, regions);
        Assert.DoesNotContain(10001000, regions);
        var hubStyles = new Dictionary<string, RegionalGateStyle>
        {
            ["Jita"] = RegionalGateStyle.Caldari,
            ["Amarr"] = RegionalGateStyle.Amarr,
            ["Dodixie"] = RegionalGateStyle.Gallente,
            ["Rens"] = RegionalGateStyle.Minmatar,
            ["Hek"] = RegionalGateStyle.Minmatar,
        };
        foreach (var endpoint in endpoints)
        {
            var system = originalCatalog.Systems[endpoint.SystemId];
            var style = RegionalGateRules.Style(endpoint.TypeId);
            Assert.NotNull(style);
            if (hubStyles.TryGetValue(system.Name, out var expected))
            {
                Assert.Equal(expected, style);
            }
            Assert.Contains(
                originalCatalog.GatesInSystem(system.Id),
                gate => RegionalGateRules.Style(originalCatalog.FindCelestial(gate.Id)!.TypeId) == style
            );
        }
        var graph = routes
            .SelectMany(route => new[] { (From: route.A.SystemId, To: route.B.SystemId), (From: route.B.SystemId, To: route.A.SystemId) })
            .GroupBy(edge => edge.From)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.To).ToArray());
        foreach (int origin in graph.Keys)
        {
            HashSet<int> reached = [origin];
            HashSet<int> frontier = [origin];
            for (int jump = 0; jump < 3; jump++)
            {
                frontier = frontier.SelectMany(system => graph[system]).Where(reached.Add).ToHashSet();
            }
            Assert.Equal(graph.Count, reached.Count);
        }
        string directory = Directory.CreateTempSubdirectory("eden-express-catalog-").FullName;
        try
        {
            string target = Path.Combine(directory, "catalog.sqlite");
            File.Copy(original, target);
            var package = new ModPackage(
                new ModManifest { Id = RegionalExpressPackage.ModId, ApiVersion = ModFormat.TravelApiVersion },
                "validation",
                new Dictionary<string, byte[]>
                {
                    [ModTravelRoutes.PackagePath] = JsonSerializer.SerializeToUtf8Bytes(routes, ModFormat.Json),
                }
            );
            string overlay = TravelRouteProjection.Apply(target, "{}", [package]);
            using var document = JsonDocument.Parse(overlay);
            Assert.Equal(endpoints.Length, document.RootElement.GetProperty("travel").GetProperty("locations").EnumerateObject().Count());
            var projected = CatalogProvider.Open(target);
            Assert.Equal(originalCatalog.Gates.Count + endpoints.Length, projected.Gates.Count);
            foreach (var endpoint in endpoints)
            {
                var body = projected.FindCelestial(endpoint.GateId)!;
                Assert.Equal(endpoint.TypeId, body.TypeId);
                Assert.Equal(endpoint.Position.ToSpacePosition(), new(body.X, body.Y, body.Z));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
