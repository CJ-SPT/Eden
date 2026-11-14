using System.Collections.Immutable;
using System.Globalization;
using Eden.Gameplay.Mining;

namespace Eden.World.Mining;

internal sealed partial class PilotAutoMiningStore : IPilotAutoMiningRoutes
{
    private const string RouteKey = "eden.automining.route";

    public AutoMiningRoute ReadRoute(long pilotId)
    {
        var saved = ReadSetting(pilotId, RouteKey);
        if (
            saved?.Split(':') is not [var mode, var systems]
            || !Enum.TryParse(mode, out AutoMiningFleetMode fleet)
            || !Enum.IsDefined(fleet)
        )
        {
            return AutoMiningRoute.Empty;
        }
        var values = systems.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (values.Length > AutoMiningRoute.MaximumSystems || values.Any(value => !int.TryParse(value, out int id) || id <= 0))
        {
            return AutoMiningRoute.Empty;
        }
        return new(values.Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToImmutableArray(), fleet);
    }

    public AutoMiningRoute SaveRoute(long pilotId, int origin, string systems, AutoMiningFleetMode mode)
    {
        if (!Enum.IsDefined(mode) || systems.Length > AutoMiningRoute.MaximumTextLength)
        {
            throw new InvalidDataException("Choose a fleet mode and at most 32 systems.");
        }
        var names = systems.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (names.Length > AutoMiningRoute.MaximumSystems)
        {
            throw new InvalidDataException("A mining route can contain at most 32 systems.");
        }
        var catalog = database.Catalog;
        List<int> ids = [];
        foreach (string name in names)
        {
            int id = int.TryParse(name, out int numeric)
                ? numeric
                : catalog.Systems.Values.FirstOrDefault(system => system.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Id ?? 0;
            if (!catalog.Systems.ContainsKey(id))
            {
                throw new InvalidDataException($"Unknown solar system: {name}.");
            }
            _ = Path(ids.Count == 0 ? origin : ids[^1], id);
            ids.Add(id);
        }
        var route = new AutoMiningRoute(ids.ToImmutableArray(), mode);
        SaveSetting(pilotId, RouteKey, $"{mode}:{string.Join(',', ids)}");
        return route;
    }

    public string Describe(AutoMiningRoute route) =>
        string.Join(
            ", ",
            route.Systems.Select(id => database.Catalog.Systems.GetValueOrDefault(id)?.Name ?? id.ToString(CultureInfo.InvariantCulture))
        );

    public IReadOnlyList<AutoMiningGate> Path(int origin, int destination)
    {
        var gates = database.Catalog.Gates;
        return AutoMiningRouteRules.Path(
            origin,
            destination,
            gates
                .Values.Where(gate => gates.TryGetValue(gate.DestinationId, out var exit) && exit.DestinationId == gate.Id)
                .Select(gate => new AutoMiningGate(gate.SolarSystemId, gate.Id, gate.DestinationSystemId, gate.DestinationId))
        );
    }

    public IReadOnlyList<AutoMiningDestination> Belts(int systemId) =>
        database
            .Catalog.CelestialsInSystem(systemId)
            .Where(body => body.GroupId == AsteroidDefinitions.BeltGroup)
            .OrderBy(body => body.Id)
            .Select(
                (body, index) =>
                    new AutoMiningDestination(
                        systemId,
                        body.Id,
                        new(body.X, body.Y, body.Z),
                        $"{database.Catalog.Systems[systemId].Name} belt {index + 1}"
                    )
            )
            .ToArray();

    public AutoMiningDestination Station(long stationId)
    {
        if (stationId is <= 0 or > int.MaxValue || !database.Catalog.Stations.TryGetValue((int)stationId, out var station))
        {
            throw new InvalidDataException("The unload station is unavailable.");
        }
        return new(
            station.SolarSystemId,
            station.Id,
            new(station.X, station.Y, station.Z),
            database.Catalog.StationNames.GetValueOrDefault(station.Id) ?? "Unload station"
        );
    }
}
