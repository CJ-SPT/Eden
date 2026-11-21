using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Mining;
using Eden.World.Reprocessing;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Structures;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private static readonly CatalogDefinition<ILookup<(StructureService, int), ReactionRecipe>> s_reactionProducts = new(
        "capsuleer-reactions-by-service-and-product",
        (catalog, _) =>
            ReactionCatalog.Recipes(catalog).Values.OrderBy(recipe => recipe.Formula).ToLookup(recipe => (recipe.Service, recipe.Output))
    );

    private static FactoryMoonSupply? FindReactionSupply(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot factory,
        FactorySupplyRequest request
    )
    {
        var recipes = catalog.Definitions.GlobalDefinition(s_reactionProducts);
        foreach (var service in recipes.Select(group => group.Key.Item1).Distinct())
        {
            var materials = request
                .Materials.Where(need =>
                    recipes.Contains((service, need.TypeId))
                    && ReferencePrice(catalog, need.TypeId) is > 0 and long price
                    && price <= need.MaximumUnitPriceCents
                )
                .Select(need => new FactoryMoonMaterial(need.TypeId, need.PlannedUnits))
                .ToArray();
            if (materials.Length > 0 && LocateReactionSupply(world, catalog, factory, materials, service, true) is { } supply)
            {
                return supply;
            }
        }
        return null;
    }

    private static FactoryMoonSupply? FindMoonSupply(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot factory,
        OwnedItem[] hangar,
        IReadOnlyDictionary<int, int> missing
    )
    {
        var recipes = catalog.Definitions.GlobalDefinition(s_recipesByProduct);
        var reactionProducts = catalog.Definitions.GlobalDefinition(s_reactionProducts);
        var materials = s_minerTwoReactionComponents
            .Where(missing.ContainsKey)
            .SelectMany(type =>
                recipes[type]
                    .Take(1)
                    .SelectMany(recipe =>
                        IndustryMaterials.Calculate(recipe.Materials, (int)Math.Ceiling((double)missing[type] / recipe.OutputQuantity))
                    )
            )
            .Where(material => reactionProducts.Contains((StructureService.CompositeReactions, material.Key)))
            .GroupBy(material => material.Key)
            .Select(group => new FactoryMoonMaterial(
                group.Key,
                (int)Math.Max(0, group.Sum(material => (long)material.Value) - Quantity(hangar, group.Key))
            ))
            .Where(material => material.Quantity > 0)
            .ToArray();
        return materials.Length == 0
            ? null
            : LocateReactionSupply(world, catalog, factory, materials, StructureService.CompositeReactions, false);
    }

    private static FactoryMoonSupply? LocateReactionSupply(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot factory,
        FactoryMoonMaterial[] materials,
        StructureService reactor,
        bool existingServices
    )
    {
        foreach (
            var structure in world
                .Filter<SavedStructure>(structure => structure.CorporationId == factory.CorpId)
                .AsEnumerable()
                .Where(structure =>
                    OwnedMoonRefinery(structure)
                    && structure.HasCore
                    && StructureRules.Operational(structure.State)
                    && !structure.UnanchorAt.HasValue
                )
                .OrderBy(structure => structure.Id)
        )
        {
            var services = world
                .Filter<SavedStructureService>(service => service.StructureId == structure.Id)
                .AsEnumerable()
                .Select(service => world.Set<OwnedItem>().Find(service.ItemId)?.TypeId)
                .ToHashSet();
            if (
                existingServices
                    ? !PilotStructureStore.Snapshot(world, catalog, structure).Services.Contains(reactor)
                    : s_refineryServices.Any(type => !services.Contains(type))
            )
            {
                continue;
            }
            var stations = catalog.Definitions.StationsInSystem(structure.SystemId);
            foreach (
                var member in world
                    .Filter<Pilot>(pilot => pilot.CorporationId == factory.CorpId)
                    .Where(pilot => pilot.Id != factory.PilotId)
                    .OrderBy(pilot => pilot.Id)
            )
            {
                var home = world.Filter<SavedCapsuleerCommunity>(row => row.PilotId == member.Id).SingleOrDefault();
                if (home is not { Career: CapsuleerCareer.Mining } || Employer(world, member.Id, factory.CorpId) is null)
                {
                    continue;
                }
                foreach (
                    var station in stations.OrderByDescending(station => station.Id == home.HomeStationId).ThenBy(station => station.Id)
                )
                {
                    if (
                        PilotReprocessingStore.SupportsNpcReprocessing(catalog, station.Id)
                        && StationCapabilities.Has(catalog, station.OperationId, StationService.Manufacturing)
                        && StationCapabilities.Has(catalog, station.OperationId, StationService.Market)
                        && MoonRoute(home.HomeStationId, station.Id) is not null
                        && MoonRoute(station.Id, factory.StationId) is not null
                    )
                    {
                        var supply = new FactoryMoonSupply(structure.Id, member.Id, station.Id, materials)
                        {
                            Service = reactor,
                            UseExistingServices = existingServices,
                        };
                        if (
                            !existingServices
                            || PlanMoonInputs(catalog, supply, Hangar(world, member.Id, station.Id), Hangar(world, member.Id, structure.Id))
                                .Formulas.All(formula =>
                                    ReactionCatalog
                                        .Recipes(catalog)[formula]
                                        .Skills.All(skill => PilotSkillReader.Level(world, catalog, member.Id, skill.TypeId) >= skill.Level)
                                )
                        )
                        {
                            return supply;
                        }
                    }
                }
            }
        }
        return null;

        CapsuleerTrafficRoute? MoonRoute(int start, int end) =>
            CapsuleerTrafficRoutes.ToStation(
                catalog,
                start,
                end,
                HullClass.Hauler,
                maximumJumps: CapsuleerIndustryRules.MaximumRefinerySupplyJumps,
                policy: world.Settings.Capsuleers
            );
    }

    private static long Quantity(IEnumerable<OwnedItem> stock, int type) =>
        stock.Where(item => item.TypeId == type && !item.Singleton).Sum(item => (long)item.Quantity);

    internal static bool HasPendingMoonJob(EconomyTransaction world, long pilot) => InstalledFactoryJobs(world, pilot).Any();

    internal static CapsuleerFactoryCargo[] MoonCargoForStation(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        FactoryMoonSupply supply
    )
    {
        if (pilot.DockedLocationId != supply.StructureId)
        {
            return [];
        }
        var depot = Hangar(world, pilot.PilotId, supply.StationId);
        var output = Hangar(world, pilot.PilotId, supply.StructureId);
        double free = FreeFreightVolume(world, catalog, pilot);
        return MoonCargo(catalog, supply.Materials, depot, output, free);
    }

    private static CapsuleerFactoryCargo[] MoonCargo(
        CatalogDbContext catalog,
        FactoryMoonMaterial[] demand,
        OwnedItem[] destination,
        OwnedItem[] origin,
        double free
    )
    {
        List<CapsuleerFactoryCargo> cargo = [];
        foreach (var material in demand)
        {
            long remaining = material.Quantity - Quantity(destination, material.TypeId);
            foreach (var item in origin.Where(item => item.TypeId == material.TypeId && !item.Singleton))
            {
                double volume = item.VolumeOverride ?? PilotInventoryStore.Volume(catalog, item.TypeId);
                if (remaining <= 0 || volume <= 0 || free < volume)
                {
                    break;
                }
                int units = (int)Math.Min(Math.Floor(free / volume), Math.Min(remaining, item.Quantity));
                cargo.Add(new(item.Id, units));
                remaining -= units;
                free -= units * volume;
            }
        }
        return cargo.ToArray();
    }

    internal static FactoryMoonSupply? AssignedMoonSupply(EconomyTransaction world, DockedPilot pilot, long now)
    {
        if (LiveWorld.Find(world.WorldPath) is not { } live || Employer(world, pilot.PilotId, pilot.CorpId) is null)
        {
            return null;
        }
        var members = world.Filter<Pilot>(member => member.CorporationId == pilot.CorpId).OrderBy(member => member.Id).ToArray();
        foreach (var member in members.Where(member => member.Id != pilot.PilotId))
        {
            if (
                CurrentFactoryRequest(world, live, member.Id, pilot.CorpId, now)?.MoonSupply is not { } supply
                || supply.OperatorId != pilot.PilotId
            )
            {
                continue;
            }
            var loaded = members
                .Select(courier => (Courier: courier, Work: live.HaulerCheckpoints.GetValueOrDefault(courier.Id)?.Work))
                .Where(entry =>
                    entry.Work?.CorporationSupply?.FactorySupply is { MoonSupply: { } pickup } shipment
                    && pickup.OperatorId == pilot.PilotId
                    && pickup.StructureId == supply.StructureId
                    && shipment.ManufacturerId == member.Id
                    && entry.Work.ShipId == entry.Courier.ActiveShipId
                )
                .SelectMany(entry => entry.Work!.Freight?.Stacks ?? [])
                .ToLookup(stack => stack.TypeId);
            return supply with
            {
                Materials = supply
                    .Materials.Select(part =>
                        part with
                        {
                            Quantity = (int)Math.Max(0, part.Quantity - loaded[part.TypeId].Sum(stack => (long)stack.Quantity)),
                        }
                    )
                    .Where(part => part.Quantity > 0)
                    .ToArray(),
            };
        }
        return null;
    }

    internal void SettleMoonStationJobs(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, long now)
    {
        foreach (var job in InstalledFactoryJobs(world, pilot.PilotId).ToArray())
        {
            if (job.StationId == pilot.StationId && PilotIndustryStore.Ready(world, job, now))
            {
                industry.Finish(world, catalog, pilot.PilotId, job.Id, pilot.SystemId, cancel: false);
            }
        }
    }

    private sealed record MoonSupplyPlan(int[] Formulas, FactoryMoonMaterial[] Inputs);

    private static MoonSupplyPlan PlanMoonInputs(
        CatalogDbContext catalog,
        FactoryMoonSupply supply,
        OwnedItem[] station,
        OwnedItem[] structure
    )
    {
        var reactions = catalog.Definitions.GlobalDefinition(s_reactionProducts);
        var stock = station
            .Concat(structure)
            .Where(item => !item.Singleton)
            .GroupBy(item => item.TypeId)
            .ToDictionary(group => group.Key, group => group.Sum(item => (long)item.Quantity));
        List<int> formulas = [];
        Dictionary<int, int> inputs = [];
        HashSet<int> ancestry = [];
        foreach (var material in supply.Materials)
        {
            Require(material.TypeId, material.Quantity);
        }
        return new([.. formulas.Distinct()], inputs.Select(pair => new FactoryMoonMaterial(pair.Key, pair.Value)).ToArray());

        void Require(int type, int quantity)
        {
            long owned = stock.GetValueOrDefault(type);
            stock[type] = Math.Max(0, owned - quantity);
            int missing = (int)Math.Max(0, quantity - owned);
            if (missing == 0)
            {
                return;
            }
            if (reactions[(supply.Service, type)].FirstOrDefault() is not { } recipe || !ancestry.Add(type))
            {
                inputs[type] = checked(inputs.GetValueOrDefault(type) + missing);
                return;
            }
            int runs = (int)Math.Ceiling((double)missing / recipe.Quantity);
            foreach (var material in IndustryMaterials.Calculate(recipe.Materials, runs))
            {
                Require(material.Key, material.Value);
            }
            formulas.Add(recipe.Formula);
            stock[type] = (long)runs * recipe.Quantity - missing;
            ancestry.Remove(type);
        }
    }

    internal bool PrepareMoonSupply(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        FactoryMoonSupply supply,
        long now,
        out FactorySupplyRequest? purchases,
        out int[] iceProducts
    )
    {
        purchases = null;
        iceProducts = [];
        if (
            world.Set<SavedStructure>().Find(supply.StructureId) is not { } structure
            || !CanDockAtFactory(world, catalog, pilot, structure)
            || pilot.StationId != supply.StationId
            || Employer(world, pilot.PilotId, pilot.CorpId) is not { } corporation
            || InstalledFactoryJobs(world, pilot.PilotId).Any()
        )
        {
            return false;
        }

        var stationStock = Hangar(world, pilot.PilotId, pilot.StationId);
        var reactorStock = Hangar(world, pilot.PilotId, structure.Id);
        var plan = PlanMoonInputs(catalog, supply, stationStock, reactorStock);
        if (plan.Formulas.Length == 0)
        {
            return false;
        }
        var profile = StructureCatalog.Require(catalog, structure.TypeId);
        var site = new MoonRefinerySite(supply.StationId, structure.MoonId!.Value);
        var service = supply.UseExistingServices
            ? null
            : NextRefineryWork(
                world,
                catalog,
                pilot,
                site,
                structure,
                profile,
                profile.CoreTypeId!.Value,
                now,
                restockFracturedField: !CanRunReaction(world, catalog, pilot, structure, StructureService.CompositeReactions)
            );
        if (
            service is { Work: RefineryWork.StartExtraction or RefineryWork.OnlineServices }
            || service is { Work: RefineryWork.Fuel } && Quantity(stationStock, service.ItemTypeId) >= service.Quantity
        )
        {
            purchases = new(corporation.Id, pilot.StationId, null, []) { Refinery = service };
            return false;
        }
        foreach (int formula in supply.UseExistingServices ? [] : plan.Formulas)
        {
            foreach (var skill in ReactionCatalog.Recipes(catalog)[formula].Skills)
            {
                CapsuleerCommunityStore.GrantRequirements(world, catalog, pilot.PilotId, skill.TypeId, []);
                Qualify(world, catalog, pilot.PilotId, skill);
            }
        }
        List<FactorySupplyNeed> needs = [];
        var moonInputs = MoonMiningRules
            .Composition(structure.MoonId!.Value, ManufacturingCatalog.Security(catalog, pilot.SystemId))
            .SelectMany(entry => catalog.Read(CatalogData.Client.Typematerials, (long)entry.TypeId)?.Materials ?? [])
            .Select(material => material.MaterialTypeID)
            .ToHashSet();
        var inputs = plan.Inputs.Where(material =>
            supply.Service != StructureService.CompositeReactions || !moonInputs.Contains(material.TypeId)
        );
        if (service is { Work: RefineryWork.Fuel })
        {
            int missing = service.Quantity - (int)Quantity(stationStock, service.ItemTypeId);
            inputs = inputs.Prepend(new(service.ItemTypeId, missing));
        }
        var harvestable = RefineryIceProducts(world, catalog, pilot);
        foreach (
            var needed in inputs.Concat(
                plan.Formulas.Where(type => !stationStock.Concat(reactorStock).Any(item => item.TypeId == type))
                    .Select(type => new FactoryMoonMaterial(type, 1))
            )
        )
        {
            int target = checked(needed.Quantity + (int)Quantity(stationStock, needed.TypeId));
            bool fuel = catalog.FittingType(needed.TypeId)?.GroupId == StructureRules.FuelBlockGroup;
            if (supply.UseExistingServices)
            {
                long limit = CapsuleerReplacementRules.LimitPrice(ReferencePrice(catalog, needed.TypeId));
                if (limit > 0)
                {
                    var offers = market.SupplyOrders(world, catalog, pilot.StationId, [needed.TypeId], pilot.PilotId, corporation.Id);
                    var quote = QuoteInput(offers.Where(order => order.PriceCents <= limit), needed.TypeId, needed.Quantity);
                    if (quote is not null && (decimal)quote.LimitPriceCents * quote.Quantity <= corporation.BalanceCents)
                    {
                        BuyFactoryInputs(world, catalog, pilot.PilotId, pilot.StationId, corporation, [quote]);
                        return true;
                    }
                    needs.Add(new(needed.TypeId, target, target, needed.Quantity, limit));
                }
                continue;
            }
            var harvest = fuel ? MissingIceIngredients(catalog, stationStock, needed.TypeId, target, harvestable) : [];
            if (
                harvest.Count > 0
                && RefineFactoryOre(world, catalog, pilot, stationStock, harvest.ToDictionary(type => type, _ => 1), corporation)
            )
            {
                return false;
            }
            var changed = ProduceRefineryEquipment(
                world,
                catalog,
                pilot,
                corporation,
                stationStock,
                needed.TypeId,
                target,
                out var missing,
                fuel,
                harvest
            );
            needs.AddRange(missing);
            if (changed.HasValue)
            {
                foreach (var job in InstalledFactoryJobs(world, pilot.PilotId))
                {
                    job.CapsuleerEndTicks ??= checked(world.ActiveTicks + job.EndTime - job.StartTime);
                }
                return changed.Value;
            }
            iceProducts = iceProducts.Concat(harvest).Distinct().ToArray();
        }
        if (needs.Count > 0)
        {
            purchases = new(corporation.Id, pilot.StationId, null, needs.DistinctBy(need => need.TypeId).ToArray());
        }
        return false;
    }

    private static HashSet<int> MissingIceIngredients(
        CatalogDbContext catalog,
        OwnedItem[] stock,
        int fuel,
        int target,
        HashSet<int> harvestable
    )
    {
        if (catalog.Definitions.GlobalDefinition(s_recipesByProduct)[fuel].FirstOrDefault() is not { } recipe)
        {
            return [];
        }
        int deficit = (int)Math.Max(0, target - Quantity(stock, fuel));
        int runs = (int)Math.Ceiling((double)deficit / recipe.OutputQuantity);
        return IndustryMaterials
            .Calculate(recipe.Materials, runs)
            .Where(part => harvestable.Contains(part.Key) && Quantity(stock, part.Key) < part.Value)
            .Select(part => part.Key)
            .ToHashSet();
    }

    private static HashSet<int> RefineryIceProducts(WorldAction world, CatalogDbContext catalog, DockedPilot pilot)
    {
        if (
            !PilotReprocessingStore.SupportsNpcReprocessing(catalog, pilot.StationId)
            || !PilotInventoryStore.List(world, catalog, pilot.PilotId, pilot.ShipId).Any(item => IsIceHarvester(catalog, item))
        )
        {
            return [];
        }
        return MiningSiteCatalog
            .Sites.Where(site => site.Family == MiningFamily.Ice && site.Systems!.Contains(pilot.SystemId))
            .SelectMany(site => site.Deposits)
            .SelectMany(deposit => catalog.Read(CatalogData.Client.Typematerials, (long)deposit.TypeId)?.Materials ?? [])
            .Select(material => (int)material.MaterialTypeID!.Value)
            .ToHashSet();
    }

    internal static bool IsIceHarvester(CatalogDbContext catalog, InventoryItem item) =>
        InventoryDefinitions.IsHighSlot(item.FlagId)
        && MiningDefinitions.Family(item.GroupId, catalog.FittingAttributes(item.TypeId)) == MiningFamily.Ice;
}
