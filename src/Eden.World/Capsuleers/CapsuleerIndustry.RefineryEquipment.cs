using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private bool? ProduceRefineryEquipment(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot factory,
        SavedCorporation corporation,
        OwnedItem[] hangar,
        int type,
        int quantity,
        out FactorySupplyNeed[] needs,
        bool manufacture = false,
        IReadOnlySet<int>? harvest = null
    )
    {
        var recipes = catalog.Definitions.GlobalDefinition(s_recipesByProduct);
        var stock = hangar.GroupBy(item => item.TypeId).ToDictionary(group => group.Key, group => group.Sum(item => (long)item.Quantity));
        Dictionary<int, FactorySupplyNeed> missing = [];
        HashSet<int> ancestry = [];
        bool? action = Work(
            type,
            quantity,
            permitPurchase: !manufacture
                && type is not AthanorType and not MoonDrillType and not ReprocessingServiceType and not CompositeReactorType
        );
        needs = missing.Values.ToArray();
        return action;

        bool? Work(int product, int amount, bool permitPurchase)
        {
            int shortfall = (int)Math.Max(0, amount - stock.GetValueOrDefault(product));
            if (shortfall <= 0 || !ancestry.Add(product))
            {
                return null;
            }
            try
            {
                var recipe = recipes[product].FirstOrDefault();
                if (harvest?.Contains(product) == true)
                {
                    return null;
                }
                if (permitPurchase && Buy(product, shortfall))
                {
                    return true;
                }
                if (recipe is null)
                {
                    if (permitPurchase)
                    {
                        Request(product, amount, shortfall);
                    }
                    return null;
                }
                var sponsor = manufacture && product == type ? corporation : null;
                if (ProduceFactoryComponent(world, catalog, factory, corporation, hangar, product, shortfall, sponsor) is bool changed)
                {
                    return changed;
                }
                var original = hangar.FirstOrDefault(item => item.TypeId == recipe.BlueprintTypeId && item.BlueprintRuns != 0);
                if (original is null)
                {
                    if (Buy(recipe.BlueprintTypeId, 1))
                    {
                        return true;
                    }
                    Request(recipe.BlueprintTypeId, 1, 1);
                }
                int target = (int)Math.Min(int.MaxValue, (long)shortfall + recipe.OutputQuantity - 1);
                int runs = CapsuleerIndustryRules.DemandRuns(target, recipe.OutputQuantity, recipe.Seconds, original?.BlueprintRuns);
                if (runs > 0)
                {
                    foreach (
                        var (ingredient, needed) in IndustryMaterials.Calculate(recipe.Materials, runs, original?.MaterialEfficiency ?? 0)
                    )
                    {
                        if (Work(ingredient, needed, permitPurchase: true) is bool child)
                        {
                            return child;
                        }
                    }
                }
                return null;
            }
            finally
            {
                ancestry.Remove(product);
            }
        }

        bool Buy(int product, int amount)
        {
            long ceiling = Limit(product, amount);
            if (ceiling <= 0)
            {
                return false;
            }
            var asks = market.SupplyOrders(world, catalog, factory.StationId, [product], factory.PilotId, corporation.Id);
            var order = QuoteInput(asks.TakeWhile(offer => offer.PriceCents <= ceiling), product, amount);
            if (order is null)
            {
                return false;
            }
            market.BuyCorporationStock(world, catalog, factory.PilotId, factory.StationId, corporation, [order]);
            return true;
        }

        void Request(int product, int amount, int units)
        {
            long ceiling = Limit(product, units);
            if (ceiling > 0 && (!missing.TryGetValue(product, out var existing) || existing.HangarTarget < amount))
            {
                missing[product] = new(product, amount, amount, units, ceiling);
            }
        }

        long Limit(int product, int amount)
        {
            long available = corporation.BalanceCents / amount;
            long reference = ReferencePrice(catalog, product);
            return reference > 0 ? Math.Min(available, reference) : available;
        }
    }
}
