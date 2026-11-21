using System.Collections.Immutable;
using Eden.Data.Catalog;
using Eden.Data.World.Corporations;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Economy;
using Eden.World.Industry;
using Eden.World.Market;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private sealed record FactoryScienceStep(ManufacturingMaterial[] Materials, ManufacturingRequiredSkill[] Skills);

    private sealed record FactoryInvention(
        int SourceType,
        int OutputType,
        int LicensedRuns,
        decimal BaseProbability,
        bool Relic,
        FactoryScienceStep Invention,
        FactoryScienceStep? Copying
    );

    private static readonly CatalogDefinition<ILookup<int, FactoryInvention>> s_inventionByBlueprint = new(
        "capsuleer-invention-by-output-blueprint",
        (catalog, _) =>
        {
            var manufacturing = ManufacturingCatalog.Recipes(catalog);
            List<FactoryInvention> routes = [];
            foreach (var source in catalog.Definitions.Pin(CatalogData.Client.Blueprints).Values.OrderBy(source => source.BlueprintTypeID))
            {
                if (source.BlueprintTypeID is not > 0 or > int.MaxValue || source.Activities?.Invention is not { Time: > 0 } invention)
                {
                    continue;
                }

                int type = (int)source.BlueprintTypeID.Value;
                bool relic = catalog.FittingType(type)?.CategoryId == (int)InventoryCategory.Relic;
                if (!relic && catalog.FittingType(type)?.CategoryId != (int)InventoryCategory.Blueprint)
                {
                    continue;
                }
                var invent = ReadStep(invention.Materials, invention.Skills);
                if (invent is null)
                {
                    continue;
                }
                var copy =
                    source.Activities.Copying is { Time: > 0 } definition && source.MaxProductionLimit is > 0
                        ? ReadStep(definition.Materials, definition.Skills)
                        : null;
                foreach (var product in invention.Products ?? [])
                {
                    if (
                        product.TypeID is > 0 and <= int.MaxValue
                        && product.Quantity is > 0 and <= int.MaxValue
                        && product.Probability is > 0 and <= 1
                        && manufacturing.ContainsKey((int)product.TypeID.Value)
                    )
                    {
                        routes.Add(
                            new(
                                type,
                                (int)product.TypeID.Value,
                                (int)product.Quantity.Value,
                                (decimal)product.Probability.Value,
                                relic,
                                invent,
                                copy
                            )
                        );
                    }
                }
            }
            return routes.ToLookup(route => route.OutputType);
        }
    );

    private static FactoryScienceStep? ReadStep(
        ImmutableArray<ClientBlueprintsActivitiesCopyingMaterialsItem>? materials,
        ImmutableArray<ClientBlueprintsActivitiesCopyingSkillsItem>? skills
    )
    {
        if (
            (materials ?? []).Any(input => input.TypeID is not > 0 or > int.MaxValue || input.Quantity is not > 0 or > int.MaxValue)
            || (skills ?? []).Any(skill => skill.TypeID is not > 0 or > int.MaxValue || skill.Level is not >= 1 or > 5)
        )
        {
            return null;
        }
        return new(
            (materials ?? []).Select(input => new ManufacturingMaterial(input.TypeID, input.Quantity)).ToArray(),
            (skills ?? []).Select(skill => new ManufacturingRequiredSkill((int)skill.TypeID!.Value, (int)skill.Level!.Value)).ToArray()
        );
    }

    private bool? TryInventForDemand(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot operatorPilot,
        SavedCorporation corporation,
        IReadOnlyDictionary<int, long> inventory,
        ILookup<int, OwnedItem> blueprints,
        OwnedItem[] hangar,
        ManufacturingRecipeDefinition recipe,
        IEnumerable<SavedPersonalOrder> bids,
        Func<int, int> level,
        double security,
        long now
    )
    {
        if (!StationCapabilities.Has(catalog, operatorPilot.OperationId, StationService.Laboratory))
        {
            return null;
        }
        var routes = catalog.Definitions.GlobalDefinition(s_inventionByBlueprint)[recipe.BlueprintTypeId];
        long pilot = operatorPilot.PilotId;
        int station = operatorPilot.StationId;
        int system = operatorPilot.SystemId;
        var jobs = world
            .Filter<IndustryJob>(job => job.OutputCorporationId == corporation.Id)
            .Where(job =>
                job.StationId == station && (job.Activity == IndustryActivity.Copying || job.Activity == IndustryActivity.Invention)
            )
            .ToArray();
        if (
            jobs.Any(job =>
                job.ProductTypeId == recipe.BlueprintTypeId
                && (
                    job.Status == IndustryJobStatus.Installed
                    || job.Status == IndustryJobStatus.Delivered
                        && job.SuccessfulRuns == 0
                        && job.CompletedAt > now - CapsuleerMinerTwoRules.FailureRetry.Ticks
                )
            )
        )
        {
            return null;
        }

        foreach (var route in routes)
        {
            if (route.Invention.Skills.Any(skill => level(skill.TypeId) < skill.Level))
            {
                continue;
            }
            var source = route.Relic
                ? hangar.FirstOrDefault(item => item.TypeId == route.SourceType && item.Quantity > 0)
                : blueprints[route.SourceType]
                    .FirstOrDefault(item => item.BlueprintRuns > 0 && !PilotIndustryStore.IsInstalled(world, item.Id));
            IndustryActivity activity = IndustryActivity.Invention;
            var step = route.Invention;
            if (source is null && !route.Relic)
            {
                if (
                    route.Copying is not { } copying
                    || copying.Skills.Any(skill => level(skill.TypeId) < skill.Level)
                    || jobs.Any(job => job.Status == IndustryJobStatus.Installed && job.ProductTypeId == route.SourceType)
                )
                {
                    continue;
                }
                source = blueprints[route.SourceType]
                    .FirstOrDefault(item => item.BlueprintRuns < 0 && !PilotIndustryStore.IsInstalled(world, item.Id));
                activity = IndustryActivity.Copying;
                step = copying;
            }

            MarketPurchase? license = null;
            if (source is null)
            {
                if (!IsMarketSupply(catalog, route.SourceType))
                {
                    continue;
                }
                license = QuoteInput(
                    market.SupplyOrders(world, catalog, station, [route.SourceType], pilot, corporation.Id),
                    route.SourceType,
                    1
                );
                if (license?.Quantity != 1)
                {
                    continue;
                }
            }

            int runs = CapsuleerIndustryRules.DemandRuns(
                bids.Max(order => order.Remaining),
                recipe.OutputQuantity,
                recipe.Seconds,
                route.LicensedRuns
            );
            if (runs <= 0)
            {
                continue;
            }
            int quantity = checked(runs * recipe.OutputQuantity);
            var bid = bids.FirstOrDefault(order => order.Remaining >= quantity) ?? bids.First();
            int sale = Math.Min(quantity, bid.Remaining);
            long net = PilotMarketStore.EstimatedSupplyProceeds(
                world,
                catalog,
                pilot,
                station,
                Math.Min(bid.PriceCents, long.MaxValue / sale),
                sale
            );
            var material = IndustryMaterials.Calculate(step.Materials, 1);
            var finished = IndustryMaterials.Calculate(recipe.Materials, runs, IndustryDefinitions.InventionMaterialEfficiency);
            if (
                EconomicStore.Enabled(world)
                && (
                    finished.Keys.Any(type => EconomicStore.AdjustedPrice(world, type) <= 0)
                    || activity == IndustryActivity.Copying && !ManufacturingCatalog.Recipes(catalog).ContainsKey(route.SourceType)
                    || activity == IndustryActivity.Copying
                        && ManufacturingCatalog
                            .Recipes(catalog)[route.SourceType]
                            .Materials.Any(input => EconomicStore.AdjustedPrice(world, (int)input.TypeId!.Value) <= 0)
                )
            )
            {
                continue;
            }
            long Fee(IndustryActivity job, int output) =>
                PilotIndustryStore
                    .ScienceCharge(world, catalog, pilot, system, route.SourceType, output, job, IndustryDefinitions.ScienceCostPercentage)
                    .TotalCents;
            decimal manufacturingFee = EconomicStore
                .Manufacturing(world, pilot, system, IndustryMaterials.Calculate(recipe.Materials, runs), security)
                .TotalCents;
            long stageFee = Fee(activity, activity == IndustryActivity.Copying ? route.SourceType : route.OutputType);
            decimal Estimate(IReadOnlyDictionary<int, int> inputs) =>
                inputs.Sum(input => (decimal)input.Value * ReferencePrice(catalog, input.Key));
            decimal inventionCost =
                Estimate(IndustryMaterials.Calculate(route.Invention.Materials, 1)) + Fee(IndustryActivity.Invention, route.OutputType);
            decimal copyCost = activity == IndustryActivity.Copying ? Estimate(material) + stageFee : 0;
            decimal relicCost = route.Relic ? license?.LimitPriceCents ?? ReferencePrice(catalog, route.SourceType) : 0;
            long originalCost = route.Relic ? 0 : license?.LimitPriceCents ?? 0;
            if (
                net
                <= originalCost + manufacturingFee + Estimate(finished) + (copyCost + inventionCost + relicCost) / route.BaseProbability
            )
            {
                continue;
            }

            var missing = material.Where(input => input.Value > inventory.GetValueOrDefault(input.Key)).ToArray();
            var asks = market
                .SupplyOrders(world, catalog, station, missing.Select(input => input.Key).ToArray(), pilot, corporation.Id)
                .ToLookup(order => order.TypeId);
            List<MarketPurchase> purchases = [];
            foreach (var input in missing)
            {
                int required = input.Value - (int)inventory.GetValueOrDefault(input.Key);
                if (QuoteInput(asks[input.Key], input.Key, required) is { } quote && quote.Quantity == required)
                {
                    purchases.Add(quote);
                }
            }
            decimal cash =
                stageFee
                + (license?.LimitPriceCents ?? 0)
                + purchases.Sum(purchase => (decimal)purchase.Quantity * purchase.LimitPriceCents);
            decimal overpriced = purchases.Sum(purchase =>
                (decimal)purchase.Quantity * Math.Max(0, purchase.LimitPriceCents - ReferencePrice(catalog, purchase.TypeId))
            );
            if (
                purchases.Count != missing.Length
                || cash > corporation.BalanceCents
                || net
                    <= originalCost
                        + manufacturingFee
                        + Estimate(finished)
                        + (copyCost + inventionCost + relicCost + overpriced) / route.BaseProbability
            )
            {
                continue;
            }
            if (license is not null)
            {
                purchases.Add(license);
            }
            if (purchases.Count > 0)
            {
                BuyFactoryInputs(world, catalog, pilot, station, corporation, purchases);
            }
            source ??= Hangar(world, pilot, station)
                .First(item => item.TypeId == route.SourceType && (route.Relic ? item.Quantity > 0 : item.BlueprintRuns < 0));
            TakeFactoryBlueprint(world, corporation, pilot, source);
            industry.InstallScience(
                world,
                catalog,
                pilot,
                new(
                    source.Id,
                    source.TypeId,
                    station,
                    1,
                    activity == IndustryActivity.Copying ? route.SourceType : route.OutputType,
                    0,
                    material,
                    Activity: activity,
                    LicensedRuns: activity == IndustryActivity.Copying ? 1 : 0
                ),
                corporation.Id
            );
            return purchases.Count > 0;
        }
        return null;
    }
}
