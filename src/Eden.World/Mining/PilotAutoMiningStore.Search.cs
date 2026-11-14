using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;

namespace Eden.World.Mining;

internal sealed partial class PilotAutoMiningStore
{
    private const int SearchLimit = 30;
    private const int MaximumSearchLength = 100;

    private static string SearchQuery(string query)
    {
        if (query.Length > MaximumSearchLength)
        {
            throw new InvalidDataException("Search using at most 100 characters.");
        }
        return query.Trim();
    }

    public IReadOnlyList<AutoMiningSystem> SearchSystems(string query)
    {
        query = SearchQuery(query);
        if (query.Length < 2)
        {
            return [];
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var miningSystems = catalog.MiningSystems();
        return database
            .Catalog.Systems.Values.Where(system =>
                miningSystems.Contains(system.Id) && system.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            )
            .OrderByDescending(system => system.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(system => system.Name, StringComparer.OrdinalIgnoreCase)
            .Take(SearchLimit)
            .Select(system => new AutoMiningSystem(system.Id, system.Name))
            .ToArray();
    }

    public IReadOnlyList<AutoMiningStation> SearchStations(string query, int currentSystem)
    {
        query = SearchQuery(query);
        if (query.Length == 0)
        {
            return Stations(currentSystem);
        }
        if (query.Length < 2)
        {
            return [];
        }
        return database
            .Catalog.StationNames.Where(station => station.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(station => station.Value.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(station => station.Value, StringComparer.OrdinalIgnoreCase)
            .Take(SearchLimit)
            .Select(station => new AutoMiningStation(station.Key, station.Value))
            .ToArray();
    }

    public IReadOnlyList<AutoMiningSystem> DescribeSystems(AutoMiningRoute route) =>
        route
            .Systems.Select(id => new AutoMiningSystem(id, database.Catalog.Systems.GetValueOrDefault(id)?.Name ?? "Unavailable system"))
            .ToArray();

    public void SavePlan(long pilotId, int origin, AutoMiningPlan plan)
    {
        if (!Enum.IsDefined(plan.Route.FleetMode) || plan.Route.Systems.Length > AutoMiningRoute.MaximumSystems)
        {
            throw new InvalidDataException("Choose a fleet mode and at most 32 mining systems.");
        }
        if (plan.Route.Systems.Length > 0 && plan.Options.Family != MiningFamily.Ore && plan.Route.FleetMode != AutoMiningFleetMode.Follow)
        {
            throw new InvalidDataException(
                "Mining system routes require Ore. Clear the systems to harvest ice or gas on the current grid."
            );
        }
        int previous = origin;
        foreach (int system in plan.Route.Systems)
        {
            if (!database.Catalog.Systems.ContainsKey(system) || Belts(system).Count == 0)
            {
                throw new InvalidDataException("Choose mining systems from the search results.");
            }
            _ = Path(previous, system);
            previous = system;
        }
        if (plan.Hauling.Enabled)
        {
            var station = Station(plan.Hauling.StationId);
            _ = Path(origin, station.SystemId);
        }
        using var action = RequireResidentWorld().Economy.Begin(new(pilots: [pilotId]));
        SaveSetting(action, pilotId, FilterKey, plan.Filter.Text);
        SaveSetting(action, pilotId, OptionsKey, FormatOptions(plan.Options));
        SaveSetting(action, pilotId, HaulKey, FormatHauling(plan.Hauling));
        SaveSetting(action, pilotId, RouteKey, $"{plan.Route.FleetMode}:{string.Join(',', plan.Route.Systems)}");
        action.Commit();
    }

    public AutoMiningDestination? Refuge(int systemId, SpacePosition position)
    {
        var station = database
            .Catalog.StationsInSystem(systemId)
            .OrderBy(station => position.DistanceTo(new(station.X, station.Y, station.Z)))
            .ThenBy(station => station.Id)
            .FirstOrDefault();
        if (station is not null)
        {
            return Station(station.Id);
        }
        var gate = database
            .Catalog.GatesInSystem(systemId)
            .Select(gate => database.Catalog.FindCelestial(gate.Id))
            .Where(body => body is not null)
            .OrderBy(body => position.DistanceTo(new(body!.X, body.Y, body.Z)))
            .FirstOrDefault();
        return gate is null ? null : new(systemId, gate.Id, new(gate.X, gate.Y, gate.Z), "Stargate refuge");
    }
}
