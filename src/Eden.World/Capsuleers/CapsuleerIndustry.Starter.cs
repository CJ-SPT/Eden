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
using Eden.World.Catalog;
using Eden.World.Economy;
using Eden.World.Industry;
using Eden.World.Market;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    internal bool WorkStarterIndustry(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, long now, out int[] oreTypes)
    {
        oreTypes = [];
        int station = pilot.StationId;
        if (CapsuleerProvisioningCatalog.StarterIndustry(catalog, pilot.SystemId).HomeStationId != station)
        {
            return false;
        }
        long id = pilot.PilotId;
        int product = (int)MiningLaserType.MinerI;
        var hangar = Hangar(world, id, station);
        var bid = world
            .Filter<SavedPersonalOrder>(order => order.StationId == station)
            .Where(order =>
                order.TypeId == product
                && order.Bid
                && order.PilotId != id
                && order.Status == PersonalOrderStatus.Open
                && order.Expires > now
                && order.Remaining > 0
                && order.EscrowCents >= order.PriceCents
            )
            .OrderByDescending(order => order.PriceCents)
            .ThenBy(order => order.Id)
            .FirstOrDefault();
        if (world.Set<SavedCapsuleerAgent>().Find(id)?.Slot == StarterIndustryRules.BuyerSlot)
        {
            bool sold = SellStarterOre(world, catalog, pilot, hangar);
            return PlaceStarterBid(world, catalog, pilot, now, product) || sold;
        }

        var job = world
            .Filter<IndustryJob>(job => job.PilotId == id)
            .SingleOrDefault(job => job.Status == IndustryJobStatus.Installed && job.ProductTypeId == product);
        if (job is not null)
        {
            if (job.EndTime <= now && job.SuspendedAt is null)
            {
                var delivered = industry.Finish(world, catalog, id, job.Id, pilot.SystemId, cancel: false);
                var item = delivered.Changes.Select(change => change.Item).SingleOrDefault(item => item.TypeId == product);
                if (bid is not null && item is not null)
                {
                    market.ExecuteSell(world, catalog, id, [new(item.Id, station, product, bid.PriceCents, 1)], 0);
                    return true;
                }
            }
            return false;
        }
        if (hangar.FirstOrDefault(item => item.TypeId == product) is { } available)
        {
            if (bid is null)
            {
                return SellStarterOre(world, catalog, pilot, hangar);
            }
            market.ExecuteSell(world, catalog, id, [new(available.Id, station, product, bid.PriceCents, 1)], 0);
            return true;
        }
        if (
            bid is null
            || !ManufacturingCatalog.Recipes(catalog).TryGetValue(StarterIndustryRules.MinerBlueprint, out var recipe)
            || recipe.Skills.Any(skill => PilotSkillReader.Level(world, catalog, id, skill.TypeId) < skill.Level)
        )
        {
            return SellStarterOre(world, catalog, pilot, hangar);
        }
        return PrepareStarterJob(world, catalog, pilot, hangar, recipe, out oreTypes);
    }

    private bool PlaceStarterBid(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, long now, int type)
    {
        long id = pilot.PilotId;
        if (
            MarketSnapshotCatalog.Quotes.GetValueOrDefault(type)?.SellCents is not long price
            || world.Filter<MarketExecution>(trade => trade.PilotId == id).Any(trade => trade.TypeId == type && trade.IsBuy)
            || world
                .Filter<SavedPersonalOrder>(order => order.PilotId == id)
                .Any(order => order.TypeId == type && order.Bid && order.Expires > now && order.Status == PersonalOrderStatus.Open)
            || !PilotMarketStore.HasSupplyListingSlot(world, catalog, id)
            || StarterBalance(world, id) < price + PilotMarketStore.SupplyBrokerFee(world, catalog, id, pilot.StationId, price, 1)
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
        out int[] oreTypes
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
        if (blueprint is null)
        {
            var ask = market.SupplyOrders(world, catalog, station, [recipe.BlueprintTypeId], id).MinBy(order => order.PriceCents);
            if (ask is null || StarterBalance(world, id) < ask.PriceCents + jobCost)
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
        industry.InstallManufacturing(
            world,
            catalog,
            id,
            new(blueprint.Id, recipe.BlueprintTypeId, station, 1, recipe.ProductTypeId, 0, materials),
            serverQuote: true
        );
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
        foreach (var item in hangar.Where(item => CapsuleerProvisioningCatalog.SupportedOre(catalog, item.TypeId)))
        {
            var bid = market.StarterOreBid(world, catalog, station, item.TypeId);
            if (bid is not null)
            {
                market.ExecuteSell(
                    world,
                    catalog,
                    pilot.PilotId,
                    [new(item.Id, station, item.TypeId, bid.PriceCents, Math.Min(bid.Remaining, item.Quantity))],
                    0
                );
                return true;
            }
        }
        return false;
    }

    private static long StarterBalance(WorldAction world, long pilot) => world.Set<PilotWallet>().Find(pilot)?.Cents ?? 0;
}
