using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Economy;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Industry;

internal sealed partial class PilotIndustryStore
{
    // Pinned evetypes.TYPE_LIST_LOWER_INVENTION_SKILL_PROBABILITY.
    private const int LowerInventionSkillProbabilityList = 799;

    private static ClientBlueprints BlueprintDefinition(CatalogDbContext catalog, int typeId) =>
        catalog.Definitions.Find(CatalogData.Client.Blueprints, typeId.ToString(CultureInfo.InvariantCulture))
        ?? throw new InvalidDataException("The blueprint definition is unavailable.");

    private IndustryResult InstallScience(long pilotId, IndustryRequest request)
    {
        _ = ScienceRules.TimeAttribute(request.Activity);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var station = CurrentStation(world, catalog, pilotId, request.FacilityId, StationService.Laboratory);
        var item =
            world.Set<OwnedItem>().SingleOrDefault(i => i.Id == request.BlueprintId && i.OwnerId == pilotId)
            ?? throw new InvalidDataException("The blueprint is unavailable or belongs to another pilot.");
        var blueprint = ReadBlueprint(world, catalog, item);
        if (blueprint.JobId.HasValue || blueprint.Facility.Id != request.FacilityId || item.TypeId != request.BlueprintTypeId)
        {
            throw new InvalidDataException("Choose an available blueprint at this station.");
        }
        var definition = BlueprintDefinition(catalog, item.TypeId);
        var activity = request.Activity switch
        {
            IndustryActivity.ResearchMaterial => definition.Activities?.ResearchMaterial,
            IndustryActivity.ResearchTime => definition.Activities?.ResearchTime,
            IndustryActivity.Copying => definition.Activities?.Copying,
            _ => null,
        };
        var invention = request.Activity == IndustryActivity.Invention ? definition.Activities?.Invention : null;
        long? baseSeconds = activity?.Time ?? invention?.Time;
        if (baseSeconds is not > 0)
        {
            throw new InvalidDataException("This blueprint does not support the selected science activity.");
        }
        foreach (var skill in activity?.Skills ?? invention?.Skills ?? [])
        {
            if (
                skill.TypeID is not > 0 or > int.MaxValue
                || skill.Level is not >= 1 or > 5
                || Level(world, catalog, pilotId, (int)skill.TypeID.Value) < skill.Level
            )
            {
                throw new InvalidDataException("Train the blueprint's required science skills first.");
            }
        }
        ClientBlueprintsActivitiesInventionProductsItem? product = null;
        int? decryptorType = null;
        InventionModifiers? decryptor = null;
        double skillProbability = 0;
        var materials = (activity?.Materials ?? invention?.Materials ?? [])
            .Select(m => new ManufacturingMaterial(m.TypeID, m.Quantity))
            .ToList();
        if (invention is not null)
        {
            var products = invention.Products ?? [];
            product =
                request.ProductTypeId is int selected ? products.SingleOrDefault(p => p.TypeID == selected)
                : products.Length == 1 ? products[0]
                : null;
            if (
                product?.TypeID is not > 0 or > int.MaxValue
                || product.Quantity is not > 0 or > int.MaxValue
                || product.Probability is not > 0 or > 1
            )
            {
                throw new InvalidDataException("Select an available invention output blueprint.");
            }
            var output = BlueprintDefinition(catalog, (int)product.TypeID.Value);
            if (output.Activities?.Manufacturing is null)
            {
                throw new InvalidDataException("The invention output has no manufacturing definition.");
            }
            var lower =
                catalog.Definitions.Find(CatalogData.Client.Typelist, LowerInventionSkillProbabilityList)?.IncludedTypeIDs
                ?? throw new InvalidDataException("The invention skill probability definition is unavailable.");
            skillProbability = (invention.Skills ?? []).Sum(s =>
                Level(world, catalog, pilotId, checked((int)s.TypeID!.Value))
                * (lower.Contains(s.TypeID.Value) ? ScienceRules.ReducedSkillProbability : ScienceRules.SkillProbability)
            );
            var extra = request.QuotedMaterials.Keys.Where(t => !materials.Any(m => m.TypeId == t)).ToArray();
            if (extra.Length > 1)
            {
                throw new InvalidDataException("Choose at most one invention decryptor.");
            }
            if (extra is [int typeId])
            {
                if (catalog.Definitions.Items.GetValueOrDefault(typeId)?.GroupId != IndustryDefinitions.GenericDecryptorGroup)
                {
                    throw new InvalidDataException("The optional invention material is not a generic decryptor.");
                }
                var attributes = Attributes(catalog, typeId);
                double probability = attributes.GetValueOrDefault(DogmaAttributeId.InventionProbabilityMultiplier);
                if (!double.IsFinite(probability) || probability <= 0)
                {
                    throw new InvalidDataException("The decryptor probability is invalid.");
                }
                decryptorType = typeId;
                decryptor = new(
                    probability,
                    checked((int)attributes.GetValueOrDefault(DogmaAttributeId.InventionMaterialModifier)),
                    checked((int)attributes.GetValueOrDefault(DogmaAttributeId.InventionTimeModifier)),
                    checked((int)attributes.GetValueOrDefault(DogmaAttributeId.InventionRunsModifier))
                );
                materials.Add(new(typeId, 1));
            }
        }
        else if (request.ProductTypeId is int selected && selected != item.TypeId)
        {
            throw new InvalidDataException("Research and copying output the source blueprint type.");
        }
        var plan = ScienceRules.Plan(
            request,
            new(
                new(blueprint.MaterialEfficiency, blueprint.TimeEfficiency, blueprint.RunsRemaining),
                baseSeconds.Value,
                checked((int)(definition.MaxProductionLimit ?? 0)),
                world
                    .Set<IndustryJob>()
                    .Count(j =>
                        j.PilotId == pilotId && j.Status == IndustryJobStatus.Installed && j.Activity != IndustryActivity.Manufacturing
                    ),
                Bonuses(world, catalog, pilotId),
                materials,
                product?.Probability ?? 1,
                skillProbability,
                checked((int)(product?.Quantity ?? 1)),
                decryptor
            )
        );
        List<InventoryChange> changes = [];
        ConsumeMaterials(world, catalog, pilotId, station.Id, plan.Materials, changes);
        if (blueprint.Item.CategoryId != (int)InventoryCategory.Relic)
        {
            item = ReserveBlueprint(world, catalog, item, changes);
        }
        long now = Now;
        var job = world
            .Add(
                new IndustryJob
                {
                    PilotId = pilotId,
                    Activity = request.Activity,
                    BlueprintId = item.Id,
                    BlueprintTypeId = item.TypeId,
                    BlueprintCopy = blueprint.RunsRemaining >= 0,
                    StationId = station.Id,
                    ProductTypeId = checked((int)(product?.TypeID ?? item.TypeId)),
                    Runs = request.Runs,
                    OutputQuantity = request.Activity is IndustryActivity.Invention or IndustryActivity.Copying ? request.Runs : 1,
                    LicensedRuns = request.LicensedRuns,
                    MaterialEfficiency = plan.MaterialEfficiency,
                    TimeEfficiency = plan.TimeEfficiency,
                    OutputRuns = plan.OutputRuns,
                    Probability = plan.Probability,
                    SuccessfulRuns = invention is null
                        ? request.Runs
                        : ScienceRules.SuccessfulRuns(plan.Probability, random.Rolls(request.Runs)),
                    OptionalTypeId = decryptorType,
                    StartTime = now,
                    EndTime = checked(now + plan.DurationSeconds * TimeSpan.TicksPerSecond),
                    DurationSeconds = plan.DurationSeconds,
                    Status = IndustryJobStatus.Installed,
                    MaterialsJson = JsonSerializer.Serialize(plan.Materials),
                }
            )
            .Entity;
        double value = EconomicStore.Enabled(world)
            ? BlueprintValue(catalog, EconomicStore.Prices(world), job.ProductTypeId) * plan.CostMultiplier
            : 0;
        double security = catalog.Definitions.Find(CatalogData.Client.Systems, (long)station.SolarSystemId)?.SecurityStatus ?? 0;
        var charge = EconomicStore.IndustryCharge(
            world,
            pilotId,
            station.SolarSystemId,
            value,
            request.Activity,
            security,
            Attributes(catalog, item.TypeId).GetValueOrDefault(DogmaAttributeId.IndustryJobCostMultiplier, 1)
        );
        if (request.QuotedCharge is { } quoted && (quoted.CostCents != charge.CostCents || quoted.TaxCents != charge.TaxCents))
        {
            throw new InvalidDataException("Science price changed; reopen the blueprint to refresh the quote.");
        }
        job.CostCents = charge.TotalCents;
        world.SaveChanges();
        long? balance =
            charge.TotalCents > 0
                ? EconomicStore.Pay(world, pilotId, station.OwnerId, job.Id, EntryType(job.Activity), charge.TotalCents, now)
                : null;
        world.SaveChanges();
        transaction.Commit();
        return new(ReadJob(catalog, job), changes, balance);
    }

    private static double BlueprintValue(CatalogDbContext catalog, IReadOnlyDictionary<int, decimal> prices, int typeId)
    {
        var recipe =
            BlueprintDefinition(catalog, typeId).Activities?.Manufacturing
            ?? throw new InvalidDataException("The blueprint has no manufacturing valuation.");
        return EconomicRules.ClientValue(
            IndustryMaterials.Calculate((recipe.Materials ?? []).Select(m => new ManufacturingMaterial(m.TypeID, m.Quantity)).ToArray(), 1),
            prices
        );
    }

    private static WalletEntryType EntryType(IndustryActivity activity) =>
        activity switch
        {
            IndustryActivity.ResearchTime => WalletEntryType.ResearchTime,
            IndustryActivity.ResearchMaterial => WalletEntryType.ResearchMaterial,
            IndustryActivity.Copying => WalletEntryType.Copying,
            IndustryActivity.Invention => WalletEntryType.Invention,
            _ => throw new InvalidDataException("Unsupported science charge."),
        };

    private static void ConsumeMaterials(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        int station,
        IReadOnlyDictionary<int, int> materials,
        List<InventoryChange> changes
    )
    {
        foreach (var material in materials)
        {
            var stacks = world
                .Set<OwnedItem>()
                .Where(i =>
                    i.OwnerId == pilotId
                    && i.LocationId == station
                    && i.FlagId == (int)InventoryFlag.Hangar
                    && i.TypeId == material.Key
                    && !i.Singleton
                )
                .OrderBy(i => i.Id)
                .ToArray();
            if (stacks.Sum(i => (long)i.Quantity) < material.Value)
            {
                throw new InvalidDataException("There are not enough industry materials in Item Hangar.");
            }
            int remaining = material.Value;
            foreach (var stack in stacks)
            {
                if (remaining == 0)
                {
                    break;
                }
                var before = PilotInventoryStore.Read(catalog, stack);
                int taken = Math.Min(stack.Quantity, remaining);
                stack.Quantity -= taken;
                remaining -= taken;
                changes.Add(new(before with { Quantity = stack.Quantity }, before));
                if (stack.Quantity == 0)
                {
                    world.Remove(stack);
                }
            }
        }
    }

    private static OwnedItem ReserveBlueprint(WorldDbContext world, CatalogDbContext catalog, OwnedItem item, List<InventoryChange> changes)
    {
        InventoryItem? before = PilotInventoryStore.Read(catalog, item);
        if (item.Quantity > 1)
        {
            item.Quantity--;
            changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
            item = world
                .Add(
                    new OwnedItem
                    {
                        Id = InventoryPersistence.NextId(world),
                        OwnerId = item.OwnerId,
                        TypeId = item.TypeId,
                        LocationId = item.LocationId,
                        FlagId = item.FlagId,
                        Quantity = 1,
                    }
                )
                .Entity;
            before = null;
        }
        item.Singleton = true;
        changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
        return item;
    }

    private static void DeliverScience(WorldDbContext world, CatalogDbContext catalog, IndustryJob job, List<InventoryChange> changes)
    {
        if (job.Activity is IndustryActivity.ResearchMaterial or IndustryActivity.ResearchTime)
        {
            var source = world.Set<OwnedItem>().Find(job.BlueprintId) ?? throw new InvalidDataException("Missing installed blueprint.");
            source.MaterialEfficiency = job.MaterialEfficiency;
            source.TimeEfficiency = job.TimeEfficiency;
            return;
        }
        int count = job.Activity == IndustryActivity.Invention ? job.SuccessfulRuns!.Value : job.Runs;
        long next = InventoryPersistence.NextId(world);
        for (int i = 0; i < count; i++)
        {
            var output = world
                .Add(
                    new OwnedItem
                    {
                        Id = checked(next + i),
                        OwnerId = job.PilotId,
                        TypeId = job.ProductTypeId,
                        LocationId = job.StationId,
                        FlagId = (int)InventoryFlag.Hangar,
                        Quantity = 1,
                        Singleton = true,
                        MaterialEfficiency = job.MaterialEfficiency,
                        TimeEfficiency = job.TimeEfficiency,
                        BlueprintRuns = job.OutputRuns,
                    }
                )
                .Entity;
            changes.Add(new(PilotInventoryStore.Read(catalog, output), null));
        }
    }
}
