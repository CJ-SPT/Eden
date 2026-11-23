using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Planetary;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Planetary;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Planetary;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private const int PlanetaryCommandCenterSkillLevel = 5;
    private static readonly CatalogDefinition<IReadOnlySet<int>> s_planetaryCommodities = new(
        "capsuleers/planetary-commodities",
        (catalog, _) => PlanetaryCatalogReader.Read(catalog).CommodityTiers.Keys.ToHashSet()
    );

    private static bool PlanetaryCommodity(CatalogDbContext catalog, int type) =>
        catalog.FittingType(type)?.CategoryId is (int)InventoryCategory.PlanetaryCommodity or (int)InventoryCategory.PlanetaryResource
        && catalog.Definitions.GlobalDefinition(s_planetaryCommodities).Contains(type);

    private Collection? PlanPlanetaryCollection(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        SavedCorporation employer,
        HaulerProgress work,
        (long Id, FactorySupplyRequest? Request)[] requests,
        IReadOnlySet<int> avoided,
        long now
    )
    {
        if (work.PlanetaryRetryAt > now)
        {
            return null;
        }
        if (work.PlanetaryProduction is { } pending)
        {
            return pending.CorporationId == employer.Id && pending.ReadyAt <= now ? Trip(pending, PlanetaryVisitPhase.Warehouse) : null;
        }
        foreach (var (manufacturer, request) in requests)
        {
            if (request is null || !request.Materials.Any(need => PlanetaryCommodity(catalog, need.TypeId)))
            {
                continue;
            }
            var definitions = PlanetaryCatalogReader.Read(catalog);
            var inventory = Hangar(world, courier.PilotId, request.StationId)
                .Where(item => !item.Singleton)
                .GroupBy(item => item.TypeId)
                .ToDictionary(group => group.Key, group => group.Sum(item => (long)item.Quantity));
            foreach (
                var demand in FactoryShortages(world, catalog, manufacturer, request)
                    .Where(shortage => definitions.CommodityTiers.ContainsKey(shortage.Need.TypeId))
            )
            {
                int root = demand.Need.TypeId;
                var step = PlanetaryProductionPlan.Next(
                    definitions,
                    root,
                    demand.Quantity,
                    inventory,
                    world.Settings.PlanetaryIndustry.FactoryOutputMultiplier
                );
                if (step is null && inventory.GetValueOrDefault(root) > 0)
                {
                    var ready = new PlanetaryProductionStep(root, demand.Quantity, null, []);
                    return Trip(
                        new(employer.Id, manufacturer, request.StationId, request.StationId, 0, root, demand.Quantity, ready, 0, 0),
                        PlanetaryVisitPhase.DeliverStock
                    );
                }
                if (step is not null && PlanetarySite(world, catalog, courier, request.StationId, step, definitions, avoided) is { } site)
                {
                    var production = new FactoryPlanetaryProduction(
                        employer.Id,
                        manufacturer,
                        request.StationId,
                        site.Station,
                        site.Planet.Id,
                        root,
                        demand.Quantity,
                        step,
                        site.Center,
                        site.Machine
                    );
                    if (Trip(production, PlanetaryVisitPhase.Warehouse) is { } route)
                    {
                        return route;
                    }
                }
            }
        }
        return null;

        Collection? Trip(FactoryPlanetaryProduction production, PlanetaryVisitPhase phase)
        {
            var route = CapsuleerTrafficRoutes.ToStation(
                catalog,
                courier.StationId,
                production.FactoryStationId,
                HullClass.Hauler,
                avoided,
                CapsuleerIndustryRules.MaximumPlanetarySupplyJumps,
                policy: world.Settings.Capsuleers
            );
            return route is null
                ? null
                : new(
                    new(employer.Id, 0)
                    {
                        FactorySupply = new(production.ManufacturerId, production.FactoryStationId, production.StagingStationId, null)
                        {
                            Planetary = new(production, phase),
                        },
                    },
                    route
                );
        }
    }

    private sealed record PlanetarySiteChoice(PlanetaryPlanet Planet, int Station, int Center, int Machine);

    private static PlanetarySiteChoice? PlanetarySite(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        int warehouse,
        PlanetaryProductionStep step,
        PlanetaryCatalog definitions,
        IReadOnlySet<int> avoided
    )
    {
        if (catalog.FittingType(PlanetaryRules.CustomsOfficeType) is null)
        {
            return null;
        }
        using var phase = SessionPerformance.Phase("traffic.findPlanetarySite");
        var owned = world.Filter<SavedPlanetaryColony>(row => row.PilotId == courier.PilotId).Select(row => row.PlanetId).ToHashSet();
        bool room = owned.Count < PlanetaryRules.PlanetLimit(world.Settings.PlanetaryIndustry, PlanetaryCommandCenterSkillLevel);
        int home = catalog.RequireStation(warehouse).SolarSystemId;
        var policy = world.Settings.Capsuleers;
        var geography = catalog.Definitions;
        if (!policy.CanWork(geography, home))
        {
            return null;
        }
        var machines = step.SchematicId is int schematic ? definitions.Schematics[schematic].PinTypes.ToHashSet() : null;
        var groups = definitions.Pins.Values.OrderBy(pin => pin.TypeId).ToLookup(pin => pin.PlanetTypeId);
        Dictionary<int, (int Center, int Machine)> equipment = [];
        foreach (var pins in groups)
        {
            if (machines is null && !PlanetaryResources.Types(pins.Key).Contains(step.TypeId))
            {
                continue;
            }
            var center = pins.FirstOrDefault(pin => pin.Kind == PlanetaryPinKind.Command);
            var machine = pins.FirstOrDefault(pin =>
                machines is null ? pin.Kind == PlanetaryPinKind.Extractor : machines.Contains(pin.TypeId)
            );
            if (center is not null && machine is not null && pins.Any(pin => pin.Kind == PlanetaryPinKind.Spaceport))
            {
                equipment.Add(pins.Key, (center.TypeId, machine.TypeId));
            }
        }
        if (equipment.Count == 0)
        {
            return null;
        }

        // All staging stations share the same warehouse, hull and avoidance rules.
        var nearby = CapsuleerTrafficRoutes.Nearby(
            catalog,
            home,
            HullClass.Hauler,
            CapsuleerIndustryRules.MaximumPlanetarySupplyJumps,
            avoided
        );
        List<(int System, int Jumps, int Station)> staging = [];
        foreach (var (system, jumps) in nearby.Systems)
        {
            if (avoided.Contains(system) || !policy.CanWork(geography, system))
            {
                continue;
            }
            var station = geography
                .StationsInSystem(system)
                .Where(station => StationCapabilities.Has(catalog, station.OperationId, StationService.Market))
                .OrderByDescending(station => station.Id == warehouse)
                .ThenBy(station => station.Id)
                .FirstOrDefault();
            if (station is not null)
            {
                staging.Add((system, jumps, station.Id));
            }
        }

        var sites = PlanetaryCatalogReader.ProductionSites(catalog, staging.Select(stop => stop.System).ToArray());
        PlanetarySiteChoice? best = null;
        (bool NewColony, bool OtherPlanetType, int Jumps, int PlanetId) bestPriority = default;
        foreach (var (system, jumps, station) in staging)
        {
            foreach (var planet in sites[system])
            {
                bool existing = owned.Contains(planet.Id);
                if ((!room && !existing) || !equipment.TryGetValue(planet.TypeId, out var pins))
                {
                    continue;
                }
                bool preferred =
                    planet.TypeId
                    is PlanetaryResources.Barren
                        or PlanetaryResources.Lava
                        or PlanetaryResources.Gas
                        or PlanetaryResources.Oceanic
                        or PlanetaryResources.Temperate;
                var priority = (!existing, !preferred, jumps, planet.Id);
                if (best is null || priority.CompareTo(bestPriority) < 0)
                {
                    best = new(planet, station, pins.Center, pins.Machine);
                    bestPriority = priority;
                }
            }
        }
        return best;
    }

    internal FreightStack[]? LoadPlanetaryInputs(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        FactoryPlanetaryProduction production
    )
    {
        if (production.ReadyAt > 0 || production.Step.Inputs.Length == 0)
        {
            return [];
        }
        var stock = Hangar(world, courier.PilotId, courier.StationId).Where(item => !item.Singleton).ToLookup(item => item.TypeId);
        if (
            production.Step.Inputs.Any(input =>
                stock[input.TypeId].FirstOrDefault()?.Quantity < input.Quantity || !stock.Contains(input.TypeId)
            )
            || production.Step.Inputs.Sum(input => input.Quantity * PilotInventoryStore.Volume(catalog, input.TypeId))
                > FreeFreightVolume(world, catalog, courier)
        )
        {
            return null;
        }
        return production.Step.Inputs.Select(input => StowPurchased(world, courier, input.TypeId, input.Quantity)).ToArray();
    }

    internal bool SupplyPlanetaryCenter(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        FactoryPlanetaryProduction production,
        long now
    )
    {
        if (world.Set<SavedPlanetaryColony>().Find(courier.PilotId, production.PlanetId) is not null)
        {
            return true;
        }
        if (PlanetaryCenter(world, courier, production.CenterType) is not null)
        {
            return true;
        }
        if (
            Employer(world, courier.PilotId, production.CorporationId) is not { } employer
            || PilotInventoryStore.Volume(catalog, production.CenterType) > FreeFreightVolume(world, catalog, courier)
        )
        {
            return false;
        }
        var quote = QuoteInput(
            market.SupplyOrders(world, catalog, courier.StationId, [production.CenterType], courier.PilotId, employer.Id),
            production.CenterType,
            1
        );
        if (quote is not null && quote.LimitPriceCents <= employer.BalanceCents)
        {
            market.BuyCorporationStock(world, catalog, courier.PilotId, courier.StationId, employer, [quote]);
            StowPurchased(world, courier, production.CenterType, 1);
            return true;
        }
        // NPC command-center service at the staging station uses the published catalog price.
        long cost = ReferencePrice(catalog, production.CenterType);
        if (cost <= 0 || cost > employer.BalanceCents)
        {
            return false;
        }
        var station = catalog.RequireStation(courier.StationId);
        CorporationWalletStore.Pay(
            world,
            employer.Id,
            station.OwnerId,
            production.CenterType,
            WalletEntryType.MarketTransaction,
            -cost,
            now
        );
        InventoryPersistence.MergeOutput(
            world,
            catalog,
            courier.PilotId,
            courier.ShipId,
            (int)InventoryFlag.Cargo,
            production.CenterType,
            1
        );
        return true;
    }

    private static OwnedItem? PlanetaryCenter(WorldAction world, DockedPilot courier, int type) =>
        world
            .Filter<OwnedItem>(item => item.LocationId == courier.ShipId)
            .AsEnumerable()
            .FirstOrDefault(item =>
                item.OwnerId == courier.PilotId && item.TypeId == type && !item.Singleton && item.FlagId == (int)InventoryFlag.Cargo
            );

    internal static void DeliverPlanetaryStock(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        FactoryPlanetaryProduction production
    )
    {
        if (
            courier.PilotId == production.ManufacturerId
            || courier.StationId != production.FactoryStationId
            || Employer(world, courier.PilotId, production.CorporationId) is null
            || Employer(world, production.ManufacturerId, production.CorporationId) is null
        )
        {
            return;
        }
        int remaining = production.RootQuantity;
        foreach (
            var stock in Hangar(world, courier.PilotId, courier.StationId)
                .Where(item => item.TypeId == production.RootProduct && !item.Singleton)
        )
        {
            int quantity = Math.Min(stock.Quantity, remaining);
            InventoryPersistence.MergeOutput(
                world,
                catalog,
                production.ManufacturerId,
                courier.StationId,
                (int)InventoryFlag.Hangar,
                stock.TypeId,
                quantity
            );
            stock.Quantity -= quantity;
            if (stock.Quantity == 0)
            {
                world.Remove(stock);
            }
            remaining -= quantity;
            if (remaining == 0)
            {
                break;
            }
        }
    }
}
