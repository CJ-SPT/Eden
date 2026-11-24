using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Economy;
using Eden.World.Industry;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private static void TakeFactoryBlueprint(EconomyTransaction world, SavedCorporation corporation, long pilot, OwnedItem blueprint)
    {
        if (blueprint.OwnerId == corporation.Id)
        {
            blueprint.OwnerId = pilot;
            blueprint.FlagId = (int)InventoryFlag.Hangar;
            world.SaveChanges();
        }
    }

    private bool? ProduceFleetReserve(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot factory,
        SavedCorporation corporation,
        ILookup<int, OwnedItem> blueprints,
        IReadOnlyDictionary<int, long> inventory,
        ILookup<int, ManufacturingRecipeDefinition> recipes,
        Func<int, int> level,
        IReadOnlySet<int> stocked,
        double security,
        long now
    )
    {
        if (
            factory.StationId != corporation.StationId
            || corporation.BalanceCents <= 0
            || !blueprints.Any(group => ManufacturingCatalog.Recipes(catalog).ContainsKey(group.Key))
        )
        {
            return null;
        }
        var members = world.Filter<Pilot>(pilot => pilot.CorporationId == corporation.Id).ToArray();
        if (LiveWorld.Find(world.WorldPath) is { } live && members.Any(member => ActiveSupplyRun(live, member) is not null))
        {
            return null;
        }
        var depot = DepotStacks(world, corporation.Id, factory.StationId).ToLookup(item => item.TypeId);
        var targets = FleetStockTargets(world, catalog, corporation.Id, factory.StationId, headquarters: true, members, now);
        foreach (var target in targets.Where(target => !stocked.Contains(target.TypeId)))
        {
            var quantity = (int)Math.Max(0, target.Quantity - depot[target.TypeId].Sum(item => (long)item.Quantity));
            if (quantity == 0)
            {
                continue;
            }
            long limit = Math.Min(
                ReferencePrice(catalog, target.TypeId),
                corporation.BalanceCents / CapsuleerRegionalStockRules.BudgetDivisor / quantity
            );
            if (limit > 0)
            {
                var request = new FactorySupplyRequest(
                    corporation.Id,
                    factory.StationId,
                    null,
                    [new(target.TypeId, quantity, quantity, quantity, limit)]
                );
                var started = BuildFactoryInput(
                    world,
                    catalog,
                    factory,
                    corporation,
                    blueprints,
                    inventory,
                    recipes,
                    request,
                    level,
                    security,
                    now,
                    finalProduct: null
                );
                if (started.HasValue)
                {
                    return started;
                }
            }
        }
        return null;
    }

    private bool? BuildFactoryInput(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot operatorPilot,
        SavedCorporation corporation,
        ILookup<int, OwnedItem> blueprints,
        IReadOnlyDictionary<int, long> inventory,
        ILookup<int, ManufacturingRecipeDefinition> recipes,
        FactorySupplyRequest request,
        Func<int, int> level,
        double security,
        long now,
        int? finalProduct
    )
    {
        long pilot = operatorPilot.PilotId;
        int station = operatorPilot.StationId;
        HashSet<int> ancestry = finalProduct is int rootProduct ? [rootProduct] : [];
        foreach (var need in request.Materials)
        {
            var started = Build(need.TypeId, need.PlannedUnits, (decimal)need.PlannedUnits * need.MaximumUnitPriceCents);
            if (started.HasValue)
            {
                return started;
            }
        }
        return null;

        bool? Build(int product, int demand, decimal ceiling)
        {
            if (!recipes.Contains(product) || !ManufacturingCatalog.Allowed(catalog, product, security) || !ancestry.Add(product))
            {
                return null;
            }
            try
            {
                foreach (var recipe in recipes[product].Where(recipe => recipe.Skills.All(skill => level(skill.TypeId) >= skill.Level)))
                {
                    foreach (
                        var blueprint in blueprints[recipe.BlueprintTypeId].Where(item => !PilotIndustryStore.IsInstalled(world, item.Id))
                    )
                    {
                        var started = Plan(recipe, blueprint, demand, ceiling);
                        if (started.HasValue)
                        {
                            return started;
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

        bool? Plan(ManufacturingRecipeDefinition recipe, OwnedItem blueprint, int demand, decimal ceiling)
        {
            long rounded = (long)Math.Ceiling((double)demand / recipe.OutputQuantity) * recipe.OutputQuantity;
            int runs = CapsuleerIndustryRules.DemandRuns(
                (int)Math.Min(rounded, int.MaxValue),
                recipe.OutputQuantity,
                recipe.Seconds,
                blueprint.BlueprintRuns
            );
            if (runs <= 0)
            {
                return null;
            }

            var materials = IndustryMaterials.Calculate(recipe.Materials, runs, blueprint.MaterialEfficiency);
            var missing = materials
                .Where(entry => entry.Value > inventory.GetValueOrDefault(entry.Key))
                .ToDictionary(entry => entry.Key, entry => entry.Value - (int)inventory.GetValueOrDefault(entry.Key));
            var asks = market
                .SupplyOrders(world, catalog, station, missing.Keys.ToArray(), pilot, corporation.Id)
                .ToLookup(order => order.TypeId);
            List<MarketPurchase> purchases = [];
            Dictionary<int, int> outstanding = [];
            foreach (var (type, quantity) in missing)
            {
                var quote = QuoteInput(asks[type], type, quantity);
                if (quote is not null)
                {
                    purchases.Add(quote);
                }
                if ((quote?.Quantity ?? 0) < quantity)
                {
                    outstanding[type] = quantity - (quote?.Quantity ?? 0);
                }
            }
            if (
                EconomicStore.Enabled(world)
                && recipe.Materials.Any(material => EconomicStore.AdjustedPrice(world, (int)material.TypeId!.Value) <= 0)
            )
            {
                return null;
            }
            long fee = EconomicStore
                .Manufacturing(world, pilot, operatorPilot.SystemId, IndustryMaterials.Calculate(recipe.Materials, runs), security)
                .TotalCents;
            decimal cash = fee + purchases.Sum(purchase => (decimal)purchase.LimitPriceCents * purchase.Quantity);
            decimal owned = materials.Sum(entry =>
                (decimal)Math.Min(entry.Value, inventory.GetValueOrDefault(entry.Key)) * ReferencePrice(catalog, entry.Key)
            );
            decimal remaining = ceiling - cash - owned;
            if (remaining < 0)
            {
                return null;
            }
            if (outstanding.Count > 0)
            {
                foreach (var (type, quantity) in outstanding)
                {
                    var started = Build(type, quantity, remaining / outstanding.Count);
                    if (started.HasValue)
                    {
                        return started;
                    }
                }
                return null;
            }

            if (cash > long.MaxValue || !Fund(world, corporation, pilot, (long)cash, now))
            {
                return null;
            }
            if (purchases.Count > 0)
            {
                BuyFactoryInputs(world, catalog, pilot, station, corporation, purchases);
            }
            TakeFactoryBlueprint(world, corporation, pilot, blueprint);
            if (recipe.Skills.Contains(s_industryQualification))
            {
                Qualify(world, catalog, pilot, s_industryQualification);
            }
            industry.InstallManufacturing(
                world,
                catalog,
                pilot,
                new(blueprint.Id, recipe.BlueprintTypeId, station, runs, recipe.ProductTypeId, 0, materials),
                corporation.Id,
                stockOperatorHangar: finalProduct.HasValue || ancestry.Count > 1
            );
            return purchases.Count != 0;
        }
    }

    private void BuyFactoryInputs(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long pilot,
        int station,
        SavedCorporation corporation,
        IReadOnlyList<MarketPurchase> purchases
    )
    {
        var result = market.BuyCorporationStock(world, catalog, pilot, station, corporation, purchases);
        if (result.Trades.Sum(trade => (long)trade.Quantity) != purchases.Sum(purchase => (long)purchase.Quantity))
        {
            throw new InvalidDataException("The quoted production inputs are no longer available.");
        }
    }
}
