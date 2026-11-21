using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Corporations;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Economy;
using Eden.World.Industry;
using Eden.World.Market;
using Eden.World.Runtime.Economy;
using Rules = Eden.Gameplay.Capsuleers.CapsuleerMinerTwoRules;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private static readonly CatalogDefinition<MinerTwoRecipe?> s_minerTwo = new(
        "capsuleer-miner-two",
        (catalog, _) =>
        {
            var blueprint = catalog.Definitions.Find(CatalogData.Client.Blueprints, Rules.Original.ToString(CultureInfo.InvariantCulture));
            var copy = blueprint?.Activities?.Copying;
            var invention = blueprint?.Activities?.Invention;
            var output = invention?.Products?.SingleOrDefault(product => product.TypeID == Rules.InventedBlueprint);
            var manufacturingRecipes = ManufacturingCatalog.Recipes(catalog);
            var manufacturing = manufacturingRecipes.GetValueOrDefault(Rules.InventedBlueprint);
            var sourceManufacturing = manufacturingRecipes.GetValueOrDefault(Rules.Original);
            if (
                copy?.Time is not > 0
                || invention?.Time is not > 0
                || output is not { Quantity: > 0 and <= int.MaxValue, Probability: > 0 and <= 1 }
                || manufacturing?.ProductTypeId != Rules.Module
                || sourceManufacturing is null
            )
            {
                return null;
            }
            var skills = (copy.Skills ?? [])
                .Select(skill => new ManufacturingRequiredSkill(checked((int)skill.TypeID!.Value), checked((int)skill.Level!.Value)))
                .Concat(
                    (invention.Skills ?? []).Select(skill => new ManufacturingRequiredSkill(
                        checked((int)skill.TypeID!.Value),
                        checked((int)skill.Level!.Value)
                    ))
                )
                .Concat(manufacturing.Skills)
                .GroupBy(skill => skill.TypeId)
                .Select(group => group.MaxBy(skill => skill.Level)!)
                .ToArray();
            return new(
                (copy.Materials ?? []).Select(material => new ManufacturingMaterial(material.TypeID, material.Quantity)).ToArray(),
                (invention.Materials ?? []).Select(material => new ManufacturingMaterial(material.TypeID, material.Quantity)).ToArray(),
                manufacturing,
                skills,
                sourceManufacturing
                    .Materials.Concat(manufacturing.Materials)
                    .Select(material => (int)material.TypeId!.Value)
                    .Distinct()
                    .ToArray(),
                (int)output.Quantity.Value,
                output.Probability.Value
            );
        }
    );

    private bool? ProduceMinerTwo(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot operatorPilot,
        SavedCorporation corporation,
        OwnedItem[] hangar,
        IEnumerable<SavedPersonalOrder> bids,
        long now,
        out FactorySupplyRequest? supply
    )
    {
        supply = null;
        if (
            corporation.BalanceCents < PersonalMarketRules.MinimumBrokerFeeCents
            || !StationCapabilities.Has(catalog, operatorPilot.OperationId, StationService.Laboratory)
            || catalog.Definition(s_minerTwo, 0) is not { } recipe
            || recipe.Skills.Any(skill =>
                catalog.FittingAttributes(skill.TypeId).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant) <= 0
            )
            || !ManufacturingCatalog.Allowed(catalog, Rules.Module, ManufacturingCatalog.Security(catalog, operatorPilot.SystemId))
        )
        {
            return null;
        }

        long pilot = operatorPilot.PilotId;
        int station = operatorPilot.StationId;
        int system = operatorPilot.SystemId;
        if (
            EconomicStore.Enabled(world)
            && (
                catalog.Definitions.Find(CatalogData.Client.Systems, (long)system)?.SecurityStatus is null
                || recipe.FeeInputs.Any(type => EconomicStore.AdjustedPrice(world, type) <= 0)
            )
        )
        {
            return null;
        }
        var corporateStock = world.Filter<OwnedItem>(item => item.OwnerId == corporation.Id);
        if (corporateStock.Any(item => item.FlagId == CorporationStorageRules.MarketDeliveriesFlag && item.TypeId == Rules.Module))
        {
            return null;
        }
        var copies = corporateStock
            .Where(item =>
                item.LocationId == station
                && item.FlagId == CorporationStorageRules.MarketDeliveriesFlag
                && item.Singleton
                && item.BlueprintRuns > 0
                && (item.TypeId == Rules.Original || item.TypeId == Rules.InventedBlueprint)
            )
            .OrderBy(item => item.Id)
            .ToArray();
        var invented = hangar.Concat(copies).FirstOrDefault(item => item.TypeId == Rules.InventedBlueprint && item.BlueprintRuns > 0);
        var inputCopy = copies.FirstOrDefault(item => item.TypeId == Rules.Original);
        var original = hangar.FirstOrDefault(item => item.TypeId == Rules.Original && item.BlueprintRuns < 0);
        int runs = Math.Min(Rules.BatchSize, invented?.BlueprintRuns ?? recipe.InventedRuns);
        var bid = bids.FirstOrDefault();
        if (bid is not null)
        {
            runs = Math.Min(runs, (int)Math.Ceiling((double)bid.Remaining / recipe.Manufacturing.OutputQuantity));
        }
        int quantity = checked(runs * recipe.Manufacturing.OutputQuantity);
        long price = Math.Max(bid?.PriceCents ?? 0, ReferencePrice(catalog, Rules.Module));
        if (price <= 0)
        {
            return null;
        }
        price = Math.Min(price, long.MaxValue / quantity);

        var copyMaterials = IndustryMaterials.Calculate(recipe.CopyMaterials, 1);
        var inventionMaterials = IndustryMaterials.Calculate(recipe.InventionMaterials, 1);
        var manufacturingMaterials = IndustryMaterials.Calculate(
            recipe.Manufacturing.Materials,
            runs,
            invented?.MaterialEfficiency ?? IndustryDefinitions.InventionMaterialEfficiency
        );
        IndustryActivity activity;
        OwnedItem? source;
        var required = manufacturingMaterials.ToDictionary();
        if (invented is not null)
        {
            activity = IndustryActivity.Manufacturing;
            source = invented;
        }
        else
        {
            if (
                world
                    .Filter<IndustryJob>(job => job.OutputCorporationId == corporation.Id)
                    .Any(job =>
                        job.Activity == IndustryActivity.Invention
                        && job.ProductTypeId == Rules.InventedBlueprint
                        && job.Status == IndustryJobStatus.Delivered
                        && job.SuccessfulRuns == 0
                        && job.CompletedAt > now - Rules.FailureRetry.Ticks
                    )
            )
            {
                return null;
            }
            activity = inputCopy is null ? IndustryActivity.Copying : IndustryActivity.Invention;
            source = inputCopy ?? original;
            AddRequired(inventionMaterials);
            if (activity == IndustryActivity.Copying)
            {
                AddRequired(copyMaterials);
                if (original is null)
                {
                    required.Add(Rules.Original, 1);
                }
            }
        }

        var owned = hangar.Where(item => !item.Singleton).ToLookup(item => item.TypeId);
        var shortages = required
            .Select(pair => new ManufacturingMaterial(
                pair.Key,
                (int)Math.Max(0, pair.Value - owned[pair.Key].Sum(item => (long)item.Quantity))
            ))
            .Where(part => part.Quantity > 0)
            .ToArray();
        var offers = market
            .SupplyOrders(world, catalog, station, shortages.Select(part => (int)part.TypeId!.Value).ToArray(), pilot, corporation.Id)
            .ToLookup(order => order.TypeId);
        List<MarketPurchase> purchases = [];
        Dictionary<int, int> missing = [];
        List<FactorySupplyNeed> datacores = [];
        foreach (var part in shortages)
        {
            int type = (int)part.TypeId!.Value;
            int needed = checked((int)part.Quantity!.Value);
            var quote = QuoteInput(offers[type], type, needed);
            if (quote?.Quantity == needed)
            {
                purchases.Add(quote);
                continue;
            }
            missing.Add(type, needed - (quote?.Quantity ?? 0));
            if (inventionMaterials.ContainsKey(type) && catalog.FittingType(type)?.GroupId == ResearchRules.DatacoreGroup)
            {
                datacores.Add(new(type, required[type], required[type], needed, ResearchRules.CentsPerDatacore));
            }
        }
        if (missing.Count > 0)
        {
            var planetaryNeeds = missing
                .Where(part => PlanetaryCommodity(catalog, part.Key))
                .Select(part => new FactorySupplyNeed(
                    part.Key,
                    required[part.Key],
                    required[part.Key],
                    part.Value,
                    ReferencePrice(catalog, part.Key)
                ))
                .ToArray();
            if (datacores.Count > 0 || planetaryNeeds.Length > 0)
            {
                supply = new(corporation.Id, station, null, [.. datacores, .. planetaryNeeds]) { ResearchDatacores = datacores.Count > 0 };
            }
            if (RefineFactoryOre(world, catalog, operatorPilot, hangar, missing))
            {
                return false;
            }
            bool needsMorphite = missing.ContainsKey(CapsuleerIndustryRules.Morphite);
            var ore = needsMorphite ? MercoxitNeeds(catalog, operatorPilot, corporation, hangar) : [];
            if (ore.Length > 0)
            {
                if (BuyLocalMercoxit(world, catalog, operatorPilot, corporation, ore))
                {
                    return true;
                }
                supply = new(corporation.Id, station, null, [.. datacores, .. planetaryNeeds, .. ore])
                {
                    ResearchDatacores = datacores.Count > 0,
                    Mercoxit = true,
                };
            }
            foreach (var (type, quantityMissing) in missing.Where(part => manufacturingMaterials.ContainsKey(part.Key)))
            {
                if (ProduceFactoryComponent(world, catalog, operatorPilot, corporation, hangar, type, quantityMissing) is bool changed)
                {
                    return changed;
                }
            }
            if (s_minerTwoReactionComponents.Any(missing.ContainsKey))
            {
                if (bid is not null && FindMoonSupply(world, catalog, operatorPilot, hangar, missing) is { } moon)
                {
                    supply = (supply ?? new(corporation.Id, station, null, [])) with
                    {
                        MoonSupply = moon,
                        Demand = new(bid.Id, bid.PriceCents, Math.Min(bid.Remaining, quantity)),
                    };
                    return null;
                }
                var construction = BuildMoonRefinery(world, catalog, operatorPilot, corporation, hangar, now, out var equipment);
                if (equipment is not null)
                {
                    supply = supply is null
                        ? equipment
                        : supply with
                        {
                            Materials = supply.Materials.Concat(equipment.Materials).DistinctBy(need => need.TypeId).ToArray(),
                            Refinery = equipment.Refinery,
                        };
                }
                if (construction.HasValue)
                {
                    return construction;
                }
            }
            return null;
        }
        decimal purchasesCost = purchases.Sum(quote => (decimal)quote.Quantity * quote.LimitPriceCents);
        long manufacturingFee = EconomicStore
            .Manufacturing(
                world,
                pilot,
                system,
                IndustryMaterials.Calculate(recipe.Manufacturing.Materials, runs),
                ManufacturingCatalog.Security(catalog, system)
            )
            .TotalCents;
        long copyFee = activity == IndustryActivity.Copying ? ScienceFee(IndustryActivity.Copying, Rules.Original) : 0;
        long inventionFee = invented is null ? ScienceFee(IndustryActivity.Invention, Rules.InventedBlueprint) : 0;
        decimal expense = (decimal)manufacturingFee + copyFee + inventionFee;
        long broker = PilotMarketStore.SupplyBrokerFee(world, catalog, pilot, station, price, quantity);
        decimal budget = purchasesCost + expense + broker;
        if (budget > corporation.BalanceCents)
        {
            return null;
        }
        long net = PilotMarketStore.EstimatedSupplyProceeds(world, catalog, pilot, station, price, quantity);
        decimal expectedRetryCost =
            activity == IndustryActivity.Copying
                ? (
                    inventionFee
                    + copyFee
                    + inventionMaterials.Sum(pair =>
                        (decimal)pair.Value
                        * (
                            purchases.FirstOrDefault(purchase => purchase.TypeId == pair.Key)?.LimitPriceCents
                            ?? ReferencePrice(catalog, pair.Key)
                        )
                    )
                ) * (1 / (decimal)recipe.Probability - 1)
                : 0;
        if (net <= purchasesCost + expense + expectedRetryCost)
        {
            return null;
        }

        foreach (var skill in recipe.Skills)
        {
            CapsuleerCommunityStore.GrantRequirements(world, catalog, pilot, skill.TypeId, []);
            Qualify(world, catalog, pilot, skill);
        }
        if (purchases.Count > 0)
        {
            var bought = market.BuyCorporationStock(world, catalog, pilot, station, corporation, purchases);
            if (bought.Trades.Sum(trade => (long)trade.Quantity) != purchases.Sum(quote => (long)quote.Quantity))
            {
                throw new InvalidDataException("The quoted Miner II inputs are no longer available.");
            }
        }
        source ??= Hangar(world, pilot, station).First(item => item.TypeId == Rules.Original && item.BlueprintRuns < 0);
        if (source.OwnerId == corporation.Id)
        {
            source.OwnerId = pilot;
            source.FlagId = (int)InventoryFlag.Hangar;
            world.SaveChanges();
        }
        if (activity == IndustryActivity.Manufacturing)
        {
            industry.InstallManufacturing(
                world,
                catalog,
                pilot,
                new(source.Id, source.TypeId, station, runs, Rules.Module, 0, manufacturingMaterials),
                corporation.Id
            );
        }
        else
        {
            industry.InstallScience(
                world,
                catalog,
                pilot,
                new(
                    source.Id,
                    source.TypeId,
                    station,
                    1,
                    activity == IndustryActivity.Copying ? Rules.Original : Rules.InventedBlueprint,
                    0,
                    activity == IndustryActivity.Copying ? copyMaterials : inventionMaterials,
                    Activity: activity,
                    LicensedRuns: activity == IndustryActivity.Copying ? 1 : 0
                ),
                corporation.Id
            );
        }
        return purchases.Count > 0;

        long ScienceFee(IndustryActivity step, int result) =>
            PilotIndustryStore
                .ScienceCharge(world, catalog, pilot, system, Rules.Original, result, step, IndustryDefinitions.ScienceCostPercentage)
                .TotalCents;

        void AddRequired(IReadOnlyDictionary<int, int> materials)
        {
            foreach (var (type, amount) in materials)
            {
                required[type] = checked(required.GetValueOrDefault(type) + amount);
            }
        }
    }

    private sealed record MinerTwoRecipe(
        ManufacturingMaterial[] CopyMaterials,
        ManufacturingMaterial[] InventionMaterials,
        ManufacturingRecipeDefinition Manufacturing,
        ManufacturingRequiredSkill[] Skills,
        int[] FeeInputs,
        int InventedRuns,
        double Probability
    );
}
