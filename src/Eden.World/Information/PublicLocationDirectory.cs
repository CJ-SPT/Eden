using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Infrastructure.Registration;
using Eden.World.Storage;

namespace Eden.World.Information;

[Injectable]
internal sealed class PublicLocationDirectory(WorldDatabase database) : IPublicLocations
{
    public IReadOnlyList<PublicLocation> Read(IReadOnlyList<long> ids)
    {
        var catalog = database.Catalog;
        List<PublicLocation> result = [];
        foreach (long id in ids.Distinct())
        {
            if (id is <= 0 or > int.MaxValue)
            {
                continue;
            }
            int key = (int)id;
            if (catalog.Gates.TryGetValue(key, out var gate) && catalog.FindCelestial(key) is { } body)
            {
                result.Add(
                    new(
                        id,
                        "Stargate (" + catalog.Systems[gate.DestinationSystemId].Name + ")",
                        gate.SolarSystemId,
                        new(body.X, body.Y, body.Z)
                    )
                );
            }
            else if (catalog.Stations.TryGetValue(key, out var station))
            {
                result.Add(new(id, catalog.StationNames[key], station.SolarSystemId, new(station.X, station.Y, station.Z)));
            }
            else if (catalog.Systems.TryGetValue(key, out var system))
            {
                result.Add(new(id, system.Name, system.Id, SpacePosition.Zero));
            }
            else if (catalog.Constellations.TryGetValue(key, out var constellation))
            {
                result.Add(new(id, constellation.Name, null, SpacePosition.Zero));
            }
            else if (catalog.Regions.TryGetValue(key, out string? region))
            {
                result.Add(new(id, region, null, SpacePosition.Zero));
            }
        }
        return result;
    }
}
