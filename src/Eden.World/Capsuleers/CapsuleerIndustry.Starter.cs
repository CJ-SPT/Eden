using Eden.Data.Catalog;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Economy;
using Eden.World.Industry;
using Eden.World.Market;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    internal static bool StarterTradeSettled(WorldAction world, long pilot) =>
        world.Filter<MarketExecution>(trade => trade.PilotId == pilot).Any(trade => trade.TypeId == (int)MiningLaserType.MinerI);

    internal static bool HasStarterWorkDemand(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, long now)
    {
        if (CapsuleerProvisioningCatalog.StarterIndustry(catalog, pilot.SystemId).HomeStationId != pilot.StationId)
        {
            return false;
        }
        long id = pilot.PilotId;
        int station = pilot.StationId;
        if (world.PilotInstalledIndustryJobs(id).Any(job => job.StationId == station))
        {
            return true;
        }
        var bids = StarterBids(world, id, station, now).ToArray();
        if (bids.Length == 0)
        {
            return false;
        }
        var recipes = catalog.Definitions.GlobalDefinition(s_recipesByProduct);
        var products = catalog.Definition(s_starterFleetProducts, 0);
        var sources = catalog.Definition(CapsuleerProvisioningCatalog.StarterOreSources, pilot.SystemId);
        return bids.Any(bid =>
            (bid.TypeId == (int)MiningLaserType.MinerI || products.Contains(bid.TypeId))
            && recipes[bid.TypeId]
                .Any(recipe => recipe.Materials.All(material => material.TypeId is long type && sources.Contains((int)type)))
            && StarterOutputInProgress(world, station, bid.TypeId) < Math.Min(bid.Remaining, bid.EscrowCents / bid.PriceCents)
        );
    }

    private static IEnumerable<EconomySavedPersonalOrder> StarterBids(
        EconomyTransaction world,
        long pilot,
        int station,
        long now,
        int? product = null
    ) =>
        world
            .StationPersonalOrders(station, product)
            .Where(order =>
                order.Bid
                && order.PilotId != pilot
                && order.Status == PersonalOrderStatus.Open
                && order.Expires > now
                && order.Remaining > 0
                && order.PriceCents > 0
                && order.EscrowCents >= order.PriceCents
            );

    private static EconomySavedPersonalOrder? BestStarterBid(EconomyTransaction world, long pilot, int station, long now, int product) =>
        StarterBids(world, pilot, station, now, product).MinBy(order => (-order.PriceCents, order.Id));

    internal bool WorkStarterIndustry(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        long now,
        out int[] oreTypes,
        out bool factoryWork
    )
    {
        oreTypes = [];
        factoryWork = false;
        int station = pilot.StationId;
        if (CapsuleerProvisioningCatalog.StarterIndustry(catalog, pilot.SystemId).HomeStationId != station)
        {
            return false;
        }
        long id = pilot.PilotId;
        int product = (int)MiningLaserType.MinerI;
        var job = world
            .PilotInstalledIndustryJobs(id)
            .FirstOrDefault(job => job.StationId == station && job.OutputCorporationId == null && job.ProductTypeId == product);
        if (job is not null)
        {
            factoryWork = true;
            var installed = world.Set<IndustryJob>().Find(job.Id)!;
            return PilotIndustryStore.Ready(world, installed, now)
                && DeliverStarterJob(
                    world,
                    catalog,
                    pilot,
                    installed,
                    BestStarterBid(world, id, station, now, product),
                    legacyMarketJob: !StarterTradeSettled(world, id)
                );
        }
        var bid = BestStarterBid(world, id, station, now, product);
        var hangar = Hangar(world, id, station);
        if (bid is null)
        {
            bool sold = SellStarterOre(world, catalog, pilot, hangar);
            // Keep the existing blueprint working and let a funded buyer establish demand.
            return !hangar.Any(item => item.TypeId == StarterIndustryRules.MinerBlueprint || item.TypeId == product)
                    && PlaceStarterBid(world, catalog, pilot, now, product)
                || sold;
        }
        if (!ManufacturingCatalog.Recipes(catalog).TryGetValue(StarterIndustryRules.MinerBlueprint, out var recipe))
        {
            return SellStarterOre(world, catalog, pilot, hangar);
        }
        factoryWork = true;
        if (recipe.Skills.Any(skill => PilotSkillReader.Level(world, catalog, id, skill.TypeId) < skill.Level))
        {
            return SellStarterOre(world, catalog, pilot, hangar);
        }
        if (StarterOutputInProgress(world, station, product) >= bid.Remaining)
        {
            return SellStarterOre(world, catalog, pilot, hangar);
        }
        return PrepareStarterJob(world, catalog, pilot, hangar, recipe, out oreTypes, bid);
    }

    internal CareerShipTurn? WorkStarterFleetOrders(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, long now)
    {
        if (
            world.Set<SavedCapsuleerAgent>().Find(pilot.PilotId) is null
            || CapsuleerProvisioningCatalog.StarterIndustry(catalog, pilot.SystemId).HomeStationId != pilot.StationId
        )
        {
            return null;
        }
        var products = catalog.Definition(s_starterFleetProducts, 0);
        long id = pilot.PilotId;
        int station = pilot.StationId;
        var job = world.PilotInstalledIndustryJobs(id).FirstOrDefault(job => job.StationId == station);
        if (job is not null)
        {
            if (!products.Contains(job.ProductTypeId))
            {
                return null;
            }
            var installed = world.Set<IndustryJob>().Find(job.Id)!;
            if (!PilotIndustryStore.Ready(world, installed, now))
            {
                return new();
            }
            return new(
                MarketChanged: DeliverStarterJob(
                    world,
                    catalog,
                    pilot,
                    installed,
                    BestStarterBid(world, id, station, now, job.ProductTypeId)
                )
            );
        }
        var bids = StarterBids(world, id, station, now)
            .Where(order => products.Contains(order.TypeId))
            .OrderByDescending(order => order.PriceCents)
            .ThenBy(order => order.Id)
            .DistinctBy(order => order.TypeId)
            .ToArray();
        if (bids.Length == 0)
        {
            return null;
        }

        var sources = catalog.Definition(CapsuleerProvisioningCatalog.StarterOreSources, pilot.SystemId);
        var recipes = catalog.Definitions.GlobalDefinition(s_recipesByProduct);
        var hangar = Hangar(world, id, station);
        foreach (var bid in bids)
        {
            if (StarterOutputInProgress(world, station, bid.TypeId) >= bid.Remaining)
            {
                continue;
            }
            var recipe = recipes[bid.TypeId]
                .FirstOrDefault(recipe =>
                    recipe.Materials.All(material => material.TypeId is long type && sources.Contains((int)type))
                    && recipe.Skills.All(skill => PilotSkillReader.Level(world, catalog, id, skill.TypeId) >= skill.Level)
                );
            if (recipe is not null)
            {
                var changed = PrepareStarterJob(world, catalog, pilot, hangar, recipe, out var oreTypes, bid);
                return new(MarketChanged: changed, OreTypes: oreTypes);
            }
        }
        return null;
    }

    private bool DeliverStarterJob(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        IndustryJob job,
        EconomySavedPersonalOrder? bid,
        bool legacyMarketJob = false
    )
    {
        long id = pilot.PilotId;
        var organization = CapsuleerCorporations.Organization(world, id);
        bool marketJob = organization.StarterMarketJobId == job.Id;
        var delivered = industry.Finish(world, catalog, id, job.Id, pilot.SystemId, cancel: false);
        if (marketJob)
        {
            CapsuleerCorporations.SaveOrganization(world, id, organization with { StarterMarketJobId = null });
        }
        if (
            (!marketJob && !legacyMarketJob)
            || bid is null
            || delivered.Changes.Select(change => change.Item).SingleOrDefault(item => item.TypeId == job.ProductTypeId) is not { } output
        )
        {
            return false;
        }
        int quantity = (int)Math.Min(job.OutputQuantity, Math.Min(bid.Remaining, bid.EscrowCents / bid.PriceCents));
        return market
                .ExecuteSell(
                    world,
                    catalog,
                    id,
                    [new(output.Id, pilot.StationId, output.TypeId, bid.PriceCents, quantity)],
                    0,
                    skipUnmatched: true
                )
                .Trades.Count > 0;
    }

    private static readonly CatalogDefinition<HashSet<int>> s_starterFleetProducts = new(
        "starter-fleet-products",
        (catalog, _) =>
            new[] { HaulingRules.BadgerType, (int)TechOneHullType.Merlin }
                .Select(type => CareerKit(catalog, type))
                .OfType<ReplacementKit>()
                .SelectMany(kit => kit.Parts())
                .Select(part => part.TypeId)
                .ToHashSet()
    );

    private static long StarterOutputInProgress(EconomyTransaction world, int station, int type) =>
        world
            .StationInstalledIndustryJobs(station)
            .Where(job => job.SuspendedAt == null && job.ProductTypeId == type)
            .Sum(job => (long)job.OutputQuantity);

    private bool PlaceStarterBid(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, long now, int type)
    {
        long id = pilot.PilotId;
        long balance = StarterBalance(world, id);
        if (
            MarketSnapshotCatalog.Quotes.GetValueOrDefault(type)?.SellCents is not long price
            || balance < price
            || world.Filter<MarketExecution>(trade => trade.PilotId == id).Any(trade => trade.TypeId == type && trade.IsBuy)
            || world
                .Filter<SavedPersonalOrder>(order => order.PilotId == id)
                .Any(order => order.TypeId == type && order.Bid && order.Expires > now && order.Status == PersonalOrderStatus.Open)
            || !PilotMarketStore.HasSupplyListingSlot(world, catalog, id)
            || balance < price + PilotMarketStore.SupplyBrokerFee(world, catalog, id, pilot.StationId, price, 1)
        )
        {
            return false;
        }
        market.ExecuteBuy(world, catalog, id, pilot.StationId, [new(type, price, 1)], StarterIndustryRules.OrderDays);
        return true;
    }

    private bool PrepareStarterJob(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        OwnedItem[] hangar,
        ManufacturingRecipeDefinition recipe,
        out int[] oreTypes,
        EconomySavedPersonalOrder? funding = null
    )
    {
        oreTypes = [];
        long id = pilot.PilotId;
        int station = pilot.StationId;
        var blueprint = hangar.FirstOrDefault(item => item.TypeId == recipe.BlueprintTypeId && item.BlueprintRuns < 0);
        var materials = IndustryMaterials.Calculate(recipe.Materials, 1, blueprint?.MaterialEfficiency ?? 0);
        double security = ManufacturingCatalog.Security(catalog, pilot.SystemId);
        long jobCost = EconomicStore
            .Manufacturing(world, id, pilot.SystemId, IndustryMaterials.Calculate(recipe.Materials, 1), security)
            .TotalCents;
        if (
            funding is not null
            && PilotMarketStore.EstimatedSupplyProceeds(
                world,
                catalog,
                id,
                station,
                funding.PriceCents,
                Math.Min(funding.Remaining, recipe.OutputQuantity)
            ) <= jobCost
        )
        {
            return SellStarterOre(world, catalog, pilot, hangar);
        }
        if (blueprint is null)
        {
            long balance = StarterBalance(world, id);
            if (balance <= jobCost)
            {
                return SellStarterOre(world, catalog, pilot, hangar);
            }
            var ask = market.SupplyOrders(world, catalog, station, [recipe.BlueprintTypeId], id).MinBy(order => order.PriceCents);
            if (ask is null || balance < ask.PriceCents + jobCost)
            {
                return SellStarterOre(world, catalog, pilot, hangar);
            }
            market.ExecuteBuy(world, catalog, id, station, [new(recipe.BlueprintTypeId, ask.PriceCents, 1)], 0);
            return true;
        }
        var missing = MissingStarterMaterials(materials, hangar);
        for (int batch = 0; batch < materials.Count && missing.Count > 0; batch++)
        {
            if (!hangar.Any(item => refinery.RefineForProduction(world, catalog, id, station, item, missing)))
            {
                break;
            }
            hangar = Hangar(world, id, station);
            missing = MissingStarterMaterials(materials, hangar);
        }
        if (missing.Count > 0)
        {
            var sources = catalog.Definition(CapsuleerProvisioningCatalog.StarterOreSources, pilot.SystemId);
            var preferred =
                missing.Keys.Select(type => sources[type].ToArray()).Where(types => types.Length > 0).MinBy(types => types.Length) ?? [];
            oreTypes = preferred;
            if (
                StarterBalance(world, id) < jobCost
                || hangar.Any(item =>
                    preferred.Contains(item.TypeId)
                    && catalog.Read(CatalogData.Client.Types, (long)item.TypeId)?.PortionSize is long portion
                    && item.Quantity >= portion
                )
            )
            {
                return SellStarterOre(world, catalog, pilot, hangar);
            }
            return false;
        }
        if (StarterBalance(world, id) < jobCost)
        {
            return SellStarterOre(world, catalog, pilot, hangar);
        }
        var installed = industry.InstallManufacturing(
            world,
            catalog,
            id,
            new(blueprint.Id, recipe.BlueprintTypeId, station, 1, recipe.ProductTypeId, 0, materials),
            serverQuote: true
        );
        if (funding is not null)
        {
            var organization = CapsuleerCorporations.Organization(world, id);
            CapsuleerCorporations.SaveOrganization(world, id, organization with { StarterMarketJobId = installed.Job.Id });
        }
        return false;
    }

    private static Dictionary<int, int> MissingStarterMaterials(IReadOnlyDictionary<int, int> required, OwnedItem[] hangar) =>
        required
            .Select(material =>
                (
                    material.Key,
                    Quantity: material.Value - hangar.Where(item => item.TypeId == material.Key).Sum(item => (long)item.Quantity)
                )
            )
            .Where(material => material.Quantity > 0)
            .ToDictionary(material => material.Key, material => checked((int)material.Quantity));

    private bool SellStarterOre(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, OwnedItem[] hangar)
    {
        int station = pilot.StationId;
        bool starterMarket = world.Settings.UsesFreshUniverseMarket();
        foreach (var item in hangar.Where(item => CapsuleerProvisioningCatalog.SupportedOre(catalog, item.TypeId)))
        {
            var bid = starterMarket ? market.StarterOreBid(world, catalog, station, item.TypeId) : null;
            if (starterMarket && bid is null)
            {
                continue;
            }
            int quantity = bid is null ? item.Quantity : Math.Min(bid.Remaining, item.Quantity);
            var result = market.ExecuteSell(
                world,
                catalog,
                pilot.PilotId,
                [new(item.Id, station, item.TypeId, bid?.PriceCents ?? 1, quantity)],
                0,
                skipUnmatched: !starterMarket,
                starterOre: starterMarket
            );
            if (result.Trades.Count > 0)
            {
                return true;
            }
        }
        return false;
    }

    private static long StarterBalance(WorldAction world, long pilot) => world.Set<PilotWallet>().Find(pilot)?.Cents ?? 0;
}
