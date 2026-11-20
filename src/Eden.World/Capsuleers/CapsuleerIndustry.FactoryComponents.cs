using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Economy;
using Eden.World.Industry;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private bool? ProduceFactoryComponent(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot factory,
        SavedCorporation corporation,
        OwnedItem[] hangar,
        int product,
        int shortage,
        SavedCorporation? reprocessingSponsor = null
    )
    {
        double security = ManufacturingCatalog.Security(catalog, factory.SystemId);
        if (!ManufacturingCatalog.Allowed(catalog, product, security))
        {
            return null;
        }
        long pilot = factory.PilotId;
        int station = factory.StationId;
        var recipes = catalog.Definitions.GlobalDefinition(s_recipesByProduct);
        var stock = hangar
            .Where(item => !item.Singleton)
            .GroupBy(item => item.TypeId)
            .ToDictionary(items => items.Key, items => items.Sum(item => (long)item.Quantity));
        foreach (var recipe in recipes[product])
        {
            if (
                recipe.Skills.Any(skill =>
                    catalog.FittingAttributes(skill.TypeId).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant) <= 0
                )
            )
            {
                continue;
            }
            var blueprint = hangar
                .Where(item => item.TypeId == recipe.BlueprintTypeId && item.BlueprintRuns != 0)
                .OrderBy(item => item.BlueprintRuns >= 0)
                .ThenByDescending(item => item.MaterialEfficiency)
                .FirstOrDefault();
            int roundedDemand = (int)Math.Min(int.MaxValue, (long)shortage + recipe.OutputQuantity - 1);
            int runs = CapsuleerIndustryRules.DemandRuns(roundedDemand, recipe.OutputQuantity, recipe.Seconds, blueprint?.BlueprintRuns);
            if (runs <= 0)
            {
                continue;
            }
            var original = blueprint is null
                ? QuoteInput(
                    market.SupplyOrders(world, catalog, station, [recipe.BlueprintTypeId], pilot, corporation.Id),
                    recipe.BlueprintTypeId,
                    1
                )
                : null;
            if (blueprint is null && original is null)
            {
                continue;
            }
            var baseMaterials = IndustryMaterials.Calculate(recipe.Materials, runs);
            if (EconomicStore.Enabled(world) && baseMaterials.Any(part => EconomicStore.AdjustedPrice(world, part.Key) <= 0))
            {
                continue;
            }
            var charge = EconomicStore.Manufacturing(world, pilot, factory.SystemId, baseMaterials, security);
            if ((decimal)charge.TotalCents + (original?.LimitPriceCents ?? 0) > corporation.BalanceCents)
            {
                continue;
            }
            var materials = IndustryMaterials.Calculate(recipe.Materials, runs, blueprint?.MaterialEfficiency ?? 0);
            var missing = materials
                .Where(part => stock.GetValueOrDefault(part.Key) < part.Value)
                .ToDictionary(part => part.Key, part => part.Value - (int)stock.GetValueOrDefault(part.Key));
            if (missing.Count > 0)
            {
                if (RefineFactoryOre(world, catalog, factory, hangar, missing, reprocessingSponsor))
                {
                    return false;
                }
                continue;
            }
            if (original is not null)
            {
                market.BuyCorporationStock(world, catalog, pilot, station, corporation, [original]);
                blueprint = Hangar(world, pilot, station).First(item => item.TypeId == recipe.BlueprintTypeId && item.BlueprintRuns < 0);
            }
            foreach (var skill in recipe.Skills)
            {
                CapsuleerCommunityStore.GrantRequirements(world, catalog, pilot, skill.TypeId, []);
                Qualify(world, catalog, pilot, skill);
            }
            industry.InstallManufacturing(
                world,
                catalog,
                pilot,
                new(blueprint!.Id, recipe.BlueprintTypeId, station, runs, product, 0, materials),
                corporation.Id,
                stockOperatorHangar: true
            );
            return original is not null;
        }
        return null;
    }

    private bool RefineFactoryOre(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot factory,
        OwnedItem[] hangar,
        IReadOnlyDictionary<int, int> missing,
        SavedCorporation? sponsor = null
    ) =>
        hangar.Any(item =>
            catalog.Definitions.Items.GetValueOrDefault(item.TypeId)?.CategoryId == (int)InventoryCategory.Asteroid
            && refinery.RefineForProduction(world, catalog, factory.PilotId, factory.StationId, item, missing, sponsor)
        );
}
