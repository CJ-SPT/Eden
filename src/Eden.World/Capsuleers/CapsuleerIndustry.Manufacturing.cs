using Eden.Data.Catalog;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Economy;
using Eden.World.Industry;
using Eden.World.Market;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Rules = Eden.Gameplay.Capsuleers.CapsuleerAmmunitionSupplyRules;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private static readonly ManufacturingRequiredSkill s_industryQualification = new((int)IndustrySkill.Industry, 1);
    private static readonly CatalogDefinition<ILookup<int, ManufacturingRecipeDefinition>> s_recipesByProduct = new(
        "capsuleer-manufacturing-products",
        (catalog, _) =>
            ManufacturingCatalog.Recipes(catalog).Values.OrderBy(recipe => recipe.BlueprintTypeId).ToLookup(recipe => recipe.ProductTypeId)
    );

    private static ILookup<int, SavedPersonalOrder> LocalProductionBids(
        WorldAction world,
        long pilot,
        int corporation,
        int station,
        long now
    ) =>
        world
            .Filter<SavedPersonalOrder>(order => order.StationId == station)
            .Where(order =>
                order.Bid
                && order.Status == PersonalOrderStatus.Open
                && order.Expires > now
                && order.Remaining > 0
                && order.PilotId != pilot
                && order.CorporationId != corporation
            )
            .OrderByDescending(order => order.PriceCents)
            .ThenBy(order => order.Id)
            .AsEnumerable()
            .ToLookup(order => order.TypeId);

    // Null means no demand work was done. A non-null result tells the scheduler whether to publish a market change.
    private bool? TryProduceForDemand(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot operatorPilot,
        SavedCorporation corporation,
        OwnedItem[] hangar,
        ILookup<int, SavedPersonalOrder> bids,
        IReadOnlySet<int> stocked,
        long now,
        out FactorySupplyRequest? supply
    )
    {
        supply = null;
        if (bids.Count == 0)
        {
            return null;
        }
        long pilot = operatorPilot.PilotId;
        double? security = catalog.Definitions.Find(CatalogData.Client.Systems, (long)operatorPilot.SystemId)?.SecurityStatus;
        if (security is null && EconomicStore.Enabled(world))
        {
            return null;
        }

        var recipes = catalog.Definitions.GlobalDefinition(s_recipesByProduct);
        var inventory = hangar
            .GroupBy(item => item.TypeId)
            .ToDictionary(group => group.Key, group => group.Sum(item => (long)item.Quantity));
        var qualifications = new Dictionary<int, int>();
        int Level(int skill)
        {
            if (!qualifications.TryGetValue(skill, out int level))
            {
                level = PilotSkillReader.Level(world, catalog, pilot, skill);
                if (
                    skill == s_industryQualification.TypeId
                    && catalog.FittingAttributes(skill).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant) > 0
                )
                {
                    level = Math.Max(level, s_industryQualification.Level);
                }
                qualifications.Add(skill, level);
            }
            return level;
        }

        foreach (var group in bids)
        {
            int product = group.Key;
            if (
                product == Rules.ChargeType
                || stocked.Contains(product)
                || !recipes.Contains(product)
                || !ManufacturingCatalog.Allowed(
                    catalog,
                    product,
                    security ?? ManufacturingCatalog.Security(catalog, operatorPilot.SystemId)
                )
            )
            {
                continue;
            }
            foreach (var recipe in recipes[product].Where(recipe => recipe.Skills.All(skill => Level(skill.TypeId) >= skill.Level)))
            {
                var blueprint = hangar
                    .Where(item => item.TypeId == recipe.BlueprintTypeId && item.BlueprintRuns != 0)
                    .OrderBy(item => item.BlueprintRuns >= 0)
                    .ThenByDescending(item => item.MaterialEfficiency)
                    .FirstOrDefault();
                int maxRuns = CapsuleerIndustryRules.DemandRuns(
                    group.Max(order => order.Remaining),
                    recipe.OutputQuantity,
                    recipe.Seconds,
                    blueprint?.BlueprintRuns
                );
                if (maxRuns > 0)
                {
                    var changed = TryStartDemandJob(
                        world,
                        catalog,
                        operatorPilot,
                        corporation,
                        hangar,
                        inventory,
                        group,
                        recipe,
                        blueprint,
                        maxRuns,
                        security ?? 0,
                        now,
                        out var request
                    );
                    if (changed.HasValue)
                    {
                        supply = null;
                        return changed;
                    }
                    supply ??= request;
                }
            }
        }
        return null;
    }

    private bool? TryStartDemandJob(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot operatorPilot,
        SavedCorporation corporation,
        OwnedItem[] hangar,
        IReadOnlyDictionary<int, long> inventory,
        IEnumerable<SavedPersonalOrder> bids,
        ManufacturingRecipeDefinition recipe,
        OwnedItem? blueprint,
        int maxRuns,
        double security,
        long now,
        out FactorySupplyRequest? supply
    )
    {
        supply = null;
        long pilot = operatorPilot.PilotId;
        int station = operatorPilot.StationId;
        var maximumMaterials = IndustryMaterials.Calculate(recipe.Materials, maxRuns, blueprint?.MaterialEfficiency ?? 0);
        int[] shoppingTypes = maximumMaterials
            .Where(material => material.Value > inventory.GetValueOrDefault(material.Key))
            .Select(material => material.Key)
            .Concat(blueprint is null ? [recipe.BlueprintTypeId] : Array.Empty<int>())
            .Distinct()
            .ToArray();
        var offers = shoppingTypes.Length == 0 ? [] : market.SupplyOrders(world, catalog, station, shoppingTypes, pilot, corporation.Id);
        var asks = offers.ToLookup(order => order.TypeId);
        var blueprintPurchase = blueprint is null ? QuoteInput(asks[recipe.BlueprintTypeId], recipe.BlueprintTypeId, 1) : null;
        if (blueprint is null && blueprintPurchase is null)
        {
            return null;
        }
        for (int runs = maxRuns; runs > 0; runs--)
        {
            int quantity = runs * recipe.OutputQuantity;
            var bid = bids.First(order => order.Remaining >= quantity);
            long maxPrice = long.MaxValue / quantity;
            long price = Math.Min(bid.PriceCents, maxPrice);
            long net = PilotMarketStore.EstimatedSupplyProceeds(world, catalog, pilot, station, price, quantity);
            var charge = EconomicStore.Manufacturing(
                world,
                pilot,
                operatorPilot.SystemId,
                IndustryMaterials.Calculate(recipe.Materials, runs),
                security
            );
            if (net <= charge.TotalCents)
            {
                continue;
            }
            var materials = IndustryMaterials.Calculate(recipe.Materials, runs, blueprint?.MaterialEfficiency ?? 0);
            var missing = materials
                .Where(material => material.Value > inventory.GetValueOrDefault(material.Key))
                .ToDictionary(material => material.Key, material => material.Value - (int)inventory.GetValueOrDefault(material.Key));
            List<MarketPurchase> purchases = blueprintPurchase is null ? [] : [blueprintPurchase];
            Dictionary<int, int> shortages = [];
            foreach (var (type, required) in missing)
            {
                var purchase = QuoteInput(asks[type], type, required);
                if (purchase is not null)
                {
                    purchases.Add(purchase);
                }
                if (required > (purchase?.Quantity ?? 0))
                {
                    shortages.Add(type, required - (purchase?.Quantity ?? 0));
                }
            }
            decimal inputBudget = purchases.Sum(purchase => (decimal)purchase.LimitPriceCents * purchase.Quantity);
            decimal ownedValue = materials.Sum(material =>
                (decimal)Math.Min(material.Value, inventory.GetValueOrDefault(material.Key)) * ReferencePrice(catalog, material.Key)
            );
            if (net <= charge.TotalCents + inputBudget + ownedValue)
            {
                continue;
            }
            if (
                blueprint is not null
                && missing.Count != 0
                && hangar.Any(item =>
                    catalog.Definitions.Items.GetValueOrDefault(item.TypeId)?.CategoryId == (int)InventoryCategory.Asteroid
                    && refinery.RefineForProduction(world, catalog, pilot, station, item, missing)
                )
            )
            {
                return false;
            }
            long broker = PilotMarketStore.SupplyBrokerFee(world, catalog, pilot, station, price, quantity);
            long localCost = (long)inputBudget + charge.TotalCents + broker;
            if (shortages.Count != 0)
            {
                decimal available = Math.Min(net - charge.TotalCents - inputBudget - ownedValue - 1, corporation.BalanceCents - localCost);
                var demand = new FactorySupplyDemand(bid.Id, bid.PriceCents, quantity);
                supply = FactoryRequest(catalog, corporation.Id, station, demand, shortages, inventory, materials, available) ?? supply;
                continue;
            }
            if (!Fund(world, corporation, pilot, localCost, now))
            {
                continue;
            }
            if (purchases.Count != 0)
            {
                var purchase = market.BuyCorporationStock(world, catalog, pilot, station, corporation, purchases);
                if (purchase.Trades.Sum(trade => (long)trade.Quantity) != purchases.Sum(request => (long)request.Quantity))
                {
                    throw new InvalidDataException("The quoted manufacturing input quantities are no longer available.");
                }
            }
            blueprint ??= Hangar(world, pilot, station).First(item => item.TypeId == recipe.BlueprintTypeId && item.BlueprintRuns < 0);
            if (recipe.Skills.Contains(s_industryQualification))
            {
                Qualify(world, catalog, pilot, s_industryQualification);
            }
            industry.InstallManufacturing(
                world,
                catalog,
                pilot,
                new(blueprint.Id, recipe.BlueprintTypeId, station, runs, recipe.ProductTypeId, 0, materials),
                corporation.Id
            );
            return purchases.Count != 0;
        }
        return null;
    }

    private static FactorySupplyRequest? FactoryRequest(
        CatalogDbContext catalog,
        int corporation,
        int station,
        FactorySupplyDemand demand,
        IReadOnlyDictionary<int, int> shortages,
        IReadOnlyDictionary<int, long> inventory,
        IReadOnlyDictionary<int, int> materials,
        decimal available
    )
    {
        if (available <= 0)
        {
            return null;
        }
        // Apportion the batch's spending ceiling by each shortage's catalog value.
        // Unpriced inputs receive equal shares of the budget as a reference.
        var weights = shortages.ToDictionary(
            item => item.Key,
            item => ReferencePrice(catalog, item.Key) is > 0 and long price ? (decimal)price * item.Value : available / shortages.Count
        );
        decimal scale = available / weights.Values.Sum();
        var needs = shortages
            .Select(item => new FactorySupplyNeed(
                item.Key,
                inventory.GetValueOrDefault(item.Key) + item.Value,
                materials[item.Key],
                item.Value,
                (long)
                    Math.Min(
                        MarketDefinitions.MaximumOrderPrice * MarketDefinitions.CentsPerIsk,
                        decimal.Floor(weights[item.Key] * scale / item.Value)
                    )
            ))
            .ToArray();
        return needs.All(need => need.MaximumUnitPriceCents > 0) ? new(corporation, station, demand, needs) : null;
    }

    private static MarketPurchase? QuoteInput(IEnumerable<MarketOrder> asks, int type, int quantity)
    {
        int remaining = quantity;
        long price = 0;
        foreach (var order in asks)
        {
            if (!StandardMarketRules.CanFill(remaining, order.Remaining, order.MinimumVolume))
            {
                continue;
            }
            remaining -= Math.Min(remaining, order.Remaining);
            price = order.PriceCents;
            if (remaining == 0)
            {
                break;
            }
        }
        return remaining < quantity ? new(type, price, quantity - remaining) : null;
    }
}
