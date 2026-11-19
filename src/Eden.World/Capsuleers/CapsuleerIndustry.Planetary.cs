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
                CapsuleerIndustryRules.MaximumPlanetarySupplyJumps
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

    private sealed record PlanetarySiteChoice(PlanetaryPlanet Planet, int Station, int Center, int Machine, bool Owned);

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
        var owned = world.Filter<SavedPlanetaryColony>(row => row.PilotId == courier.PilotId).Select(row => row.PlanetId).ToHashSet();
        bool room = owned.Count < PlanetaryRules.PlanetLimit(world.Settings.PlanetaryIndustry, PlanetaryCommandCenterSkillLevel);
        int home = catalog.RequireStation(warehouse).SolarSystemId;
        var systems = NearbySystems(catalog, home, CapsuleerIndustryRules.MaximumPlanetarySupplyJumps);
        var machines = step.SchematicId is int schematic ? definitions.Schematics[schematic].PinTypes.ToHashSet() : null;
        var sites = new List<PlanetarySiteChoice>();
        foreach (int system in systems.Where(system => !avoided.Contains(system)))
        {
            var station = catalog
                .Definitions.StationsInSystem(system)
                .Where(station => StationCapabilities.Has(catalog, station.OperationId, StationService.Market))
                .OrderByDescending(station => station.Id == warehouse)
                .ThenBy(station => station.Id)
                .FirstOrDefault();
            if (station is null)
            {
                continue;
            }
            foreach (var planet in PlanetaryCatalogReader.ProductionSites(catalog, system))
            {
                if (
                    !room && !owned.Contains(planet.Id)
                    || machines is null && !PlanetaryResources.Types(planet.TypeId).Contains(step.TypeId)
                )
                {
                    continue;
                }
                var pins = definitions.Pins.Values.Where(pin => pin.PlanetTypeId == planet.TypeId).OrderBy(pin => pin.TypeId).ToArray();
                var center = pins.FirstOrDefault(pin => pin.Kind == PlanetaryPinKind.Command);
                var machine = pins.FirstOrDefault(pin =>
                    machines is null ? pin.Kind == PlanetaryPinKind.Extractor : machines.Contains(pin.TypeId)
                );
                if (center is not null && machine is not null && pins.Any(pin => pin.Kind == PlanetaryPinKind.Spaceport))
                {
                    sites.Add(new(planet, station.Id, center.TypeId, machine.TypeId, owned.Contains(planet.Id)));
                }
            }
        }
        var routes = sites
            .Select(site => site.Station)
            .Distinct()
            .ToDictionary(
                station => station,
                station =>
                    CapsuleerTrafficRoutes.ToStation(
                        catalog,
                        warehouse,
                        station,
                        HullClass.Hauler,
                        avoided,
                        CapsuleerIndustryRules.MaximumPlanetarySupplyJumps
                    )
            );
        return sites
            .Select(site => (Site: site, Route: routes[site.Station]))
            .Where(candidate => candidate.Route is not null)
            .OrderByDescending(candidate => candidate.Site.Owned)
            .ThenByDescending(candidate =>
                candidate.Site.Planet.TypeId
                    is PlanetaryResources.Barren
                        or PlanetaryResources.Lava
                        or PlanetaryResources.Gas
                        or PlanetaryResources.Oceanic
                        or PlanetaryResources.Temperate
            )
            .ThenBy(candidate => candidate.Route!.Systems.Length)
            .ThenBy(candidate => candidate.Site.Planet.Id)
            .Select(candidate => candidate.Site)
            .FirstOrDefault();
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
