using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Gameplay.Flight;
using Eden.Modding;
using Eden.World.Catalog;
using Eden.World.Storage;

namespace Eden.World.Modding;

public static class RegionalExpressPackage
{
    public const string ModId = "eden.regional-express";
    private const double GateSpacing = 1_000_000_000;
    private const double MinimumHighSecurity = 0.45;
    private static readonly string[] s_hubs = ["Jita", "Amarr", "Dodixie", "Rens", "Hek"];

    public static void Write(WorldDatabase database, string path)
    {
        var routes = Create(database);
        ModPackages.Write(
            path,
            new ModManifest
            {
                Id = ModId,
                Name = "Regional Express",
                ApiVersion = ModFormat.TravelApiVersion,
                CatalogFingerprint = database.Catalog.Identity.SourceFingerprint,
            },
            new Dictionary<string, byte[]> { [ModTravelRoutes.PackagePath] = JsonSerializer.SerializeToUtf8Bytes(routes, ModFormat.Json) }
        );
    }

    public static IReadOnlyList<ModTravelRoute> Create(WorldDatabase database)
    {
        var catalog = CatalogProvider.Open(database.OriginalCatalogPath);
        var hubs = s_hubs.Select(name => catalog.Systems.Values.Single(system => system.Name == name)).ToArray();
        var hubRegions = hubs.Select(hub => hub.RegionId).ToHashSet();
        var stations = catalog
            .Stations.Values.GroupBy(station => station.SolarSystemId)
            .ToDictionary(group => group.Key, group => group.Count());
        var gates = catalog.Gates.Values.GroupBy(gate => gate.SolarSystemId).ToDictionary(group => group.Key, group => group.Count());
        var styles = catalog.RegionalGateStyles();
        List<(CatalogSystemDefinition A, CatalogSystemDefinition B)> pairs = [];
        for (int first = 0; first < hubs.Length; first++)
        {
            for (int second = first + 1; second < hubs.Length; second++)
            {
                pairs.Add((hubs[first], hubs[second]));
            }
        }
        // Ordinary known-space regions only: preserve Jove, Pochven, Zarzakh, starter and wormhole access.
        foreach (
            var region in catalog
                .Systems.Values.Where(system =>
                    system.RegionId is >= 10000001 and <= 10000069 && system.RegionId is not (10000004 or 10000017 or 10000019)
                )
                .Where(system => gates.ContainsKey(system.Id))
                .GroupBy(system => system.RegionId)
                .Where(region => !hubRegions.Contains(region.Key))
                .OrderBy(region => region.Key)
        )
        {
            var anchor = region
                .OrderByDescending(system => catalog.SystemSecurity.GetValueOrDefault(system.Id) >= MinimumHighSecurity)
                .ThenByDescending(system => stations.GetValueOrDefault(system.Id))
                .ThenByDescending(system => gates[system.Id])
                .ThenBy(system => system.Id)
                .First();
            var nearest = hubs.OrderBy(hub => Center(hub.Id).DistanceTo(Center(anchor.Id))).ThenBy(hub => hub.Id).First();
            pairs.Add((nearest, anchor));
        }
        var originalPairs = catalog.Gates.Values.Select(gate => (gate.SolarSystemId, gate.DestinationSystemId)).ToHashSet();
        Dictionary<int, int> slots = [];
        List<ModTravelRoute> routes = [];
        foreach (var pair in pairs.Where(pair => !originalPairs.Contains((pair.A.Id, pair.B.Id))))
        {
            string id = $"system-{Math.Min(pair.A.Id, pair.B.Id)}-{Math.Max(pair.A.Id, pair.B.Id)}";
            routes.Add(new(id, Endpoint(pair.A, pair.B, id + ":a"), Endpoint(pair.B, pair.A, id + ":b")));
        }
        ModTravelRoutes.Validate(routes);
        return routes;

        SpacePosition Center(int systemId)
        {
            var value = catalog.Find(CatalogData.Client.Systems, (long)systemId)!.Center!;
            return new(value.X!.Value, value.Y!.Value, value.Z!.Value);
        }

        ModTravelEndpoint Endpoint(CatalogSystemDefinition system, CatalogSystemDefinition destination, string identity)
        {
            var template = catalog.FindCelestial(catalog.GatesInSystem(system.Id)[0].Id)!;
            int slot = slots.GetValueOrDefault(system.Id) + 1;
            slots[system.Id] = slot;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ModId + ":" + identity));
            int gateId =
                ModTravelRoutes.FirstGateId
                + (int)(
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash)
                    % (ModTravelRoutes.GateEndId - ModTravelRoutes.FirstGateId)
                );
            var position = new ModTravelPosition(template.X + slot * GateSpacing, template.Y + GateSpacing, template.Z + GateSpacing);
            int type = RegionalGateRules.Type(styles[system.RegionId], styles[destination.RegionId]);
            return new(gateId, system.Id, type, position);
        }
    }
}
