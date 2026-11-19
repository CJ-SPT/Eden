using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Economy;
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Structures;
using Ammo = Eden.Gameplay.Capsuleers.CapsuleerAmmunitionSupplyRules;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    internal sealed record FactoryStagingPlan(SavedStructure Structure, CapsuleerFactoryCargo[] Load);

    private sealed record StructureProduction(
        ManufacturingRecipeDefinition Recipe,
        OwnedItem Blueprint,
        int Runs,
        IReadOnlyDictionary<int, int> Materials,
        CapsuleerFactoryCargo[] Load
    );

    internal static bool CanDockAtFactory(WorldAction world, CatalogDbContext catalog, DockedPilot pilot, SavedStructure structure) =>
        structure.SystemId == pilot.SystemId
        && StructureRules.CanDock(StructureCatalog.Require(catalog, structure.TypeId), structure.State, pilot.ShipGroupId)
        && PilotStructureStore.Access(world, structure, pilot.PilotId);

    internal FactoryStagingPlan? PlanFactoryStaging(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, long now)
    {
        if (pilot.StationId == 0 || Employer(world, pilot.PilotId, pilot.CorpId) is null)
        {
            return null;
        }
        var candidates = world
            .Filter<SavedStructure>(structure => structure.SystemId == pilot.SystemId && structure.State != StructureState.Destroyed)
            .AsEnumerable()
            .Where(structure => CanDockAtFactory(world, catalog, pilot, structure))
            .OrderByDescending(structure => structure.CorporationId == pilot.CorpId)
            .ThenBy(structure => structure.Id)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }
        PilotStructureStore.Advance(world, catalog, now, pilot.SystemId);
        var pending = InstalledFactoryJobs(world, pilot.PilotId).ToArray();
        var origin = Hangar(world, pilot.PilotId, pilot.StationId);
        foreach (var structure in candidates.Where(structure => CanDockAtFactory(world, catalog, pilot, structure)))
        {
            if (pending.Length > 0)
            {
                if (pending.Any(job => job.StationId == structure.Id && CanMake(world, catalog, pilot, structure, job.ProductTypeId)))
                {
                    return new(structure, []);
                }
                continue;
            }
            if (PlanStructureProduction(world, catalog, pilot, structure, origin) is not { } batch)
            {
                continue;
            }
            double capacity = PilotInventoryStore.HoldCapacity(world, catalog, pilot.PilotId, pilot.ShipId, (int)InventoryFlag.Cargo);
            double used = InventoryPersistence.UsedVolume(world, catalog, pilot.PilotId, pilot.ShipId, (int)InventoryFlag.Cargo);
            double load = batch.Load.Sum(stack =>
            {
                var item = origin.Single(item => item.Id == stack.ItemId);
                return (item.VolumeOverride ?? PilotInventoryStore.Volume(catalog, item.TypeId, item.Singleton)) * stack.Quantity;
            });
            if (load <= capacity - used)
            {
                return new(structure, batch.Load);
            }
        }
        return null;
    }

    private static bool CanMake(WorldAction world, CatalogDbContext catalog, DockedPilot pilot, SavedStructure structure, int product) =>
        StructureIndustry.CanUse(world, catalog, structure, pilot.PilotId, IndustryActivity.Manufacturing, product);

    internal static bool FactoryDeliveryReady(WorldAction world, CatalogDbContext catalog, DockedPilot pilot, long id, long now) =>
        world.Set<SavedStructure>().Find(id) is { } structure
        && world
            .Filter<IndustryJob>(job => job.PilotId == pilot.PilotId)
            .AsEnumerable()
            .Any(job =>
                job.StationId == id
                && job.OutputCorporationId.HasValue
                && job.Status == IndustryJobStatus.Installed
                && job.Activity == IndustryActivity.Manufacturing
                && PilotIndustryStore.Ready(world, job, now)
                && CanDockAtFactory(world, catalog, pilot, structure)
                && CanMake(world, catalog, pilot, structure, job.ProductTypeId)
            );

    private static StructureProduction? PlanStructureProduction(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        SavedStructure structure,
        OwnedItem[] origin
    )
    {
        var recipes = ManufacturingCatalog.Recipes(catalog);
        var local = Hangar(world, pilot.PilotId, structure.Id);
        double security = ManufacturingCatalog.Security(catalog, pilot.SystemId);
        double multiplier = catalog
            .FittingAttributes(structure.TypeId)
            .GetValueOrDefault((DogmaAttributeId)PilotIndustryStore.FacilityMaterialFactorAttribute, 1);
        foreach (var blueprint in local.Concat(origin))
        {
            if (!recipes.TryGetValue(blueprint.TypeId, out var recipe))
            {
                continue;
            }
            int runs = FactoryRuns(recipe.ProductTypeId);
            if (
                runs == 0
                || blueprint.BlueprintRuns >= 0 && blueprint.BlueprintRuns < runs
                || local.Any(item => item.TypeId == recipe.ProductTypeId)
                || PilotIndustryStore.IsInstalled(world, blueprint.Id)
                || !ManufacturingCatalog.Allowed(catalog, recipe.ProductTypeId, security)
                || !CanMake(world, catalog, pilot, structure, recipe.ProductTypeId)
                || recipe.Skills.Any(skill =>
                    PilotSkillReader.Level(world, catalog, pilot.PilotId, skill.TypeId) < skill.Level
                    && (
                        skill != s_industryQualification
                        || catalog.FittingAttributes(skill.TypeId).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant) <= 0
                    )
                )
            )
            {
                continue;
            }
            var materials = IndustryMaterials.Calculate(recipe.Materials, runs, blueprint.MaterialEfficiency, multiplier);
            List<CapsuleerFactoryCargo> load = [];
            if (blueprint.LocationId != structure.Id)
            {
                load.Add(new(blueprint.Id, 1));
            }
            bool stocked = true;
            foreach (var material in materials)
            {
                long missing = material.Value - local.Where(item => item.TypeId == material.Key).Sum(item => (long)item.Quantity);
                foreach (var item in origin.Where(item => item.TypeId == material.Key && !item.Singleton))
                {
                    if (missing <= 0)
                    {
                        break;
                    }
                    int quantity = (int)Math.Min(item.Quantity, missing);
                    load.Add(new(item.Id, quantity));
                    missing -= quantity;
                }
                if (missing > 0)
                {
                    stocked = false;
                    break;
                }
            }
            if (stocked)
            {
                return new(recipe, blueprint, runs, materials, load.ToArray());
            }
        }
        return null;
    }

    private static int FactoryRuns(int product) =>
        product == Ammo.ChargeType ? Ammo.ProductionRuns
        : product == CapsuleerMinerTwoRules.Module ? CapsuleerMinerTwoRules.BatchSize
        : CapsuleerReplacementRules.MerlinParts.Any(part => part.TypeId == product) ? 1
        : 0;

    private void ProduceAtStructure(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, long now)
    {
        PilotStructureStore.Advance(world, catalog, now, pilot.SystemId);
        if (
            world.Set<SavedStructure>().Find(pilot.DockedLocationId) is not { } structure
            || !CanDockAtFactory(world, catalog, pilot, structure)
            || Employer(world, pilot.PilotId, pilot.CorpId) is not { } corporation
        )
        {
            return;
        }
        var running = InstalledFactoryJobs(world, pilot.PilotId).ToArray();
        foreach (var job in running.Where(job => job.StationId == structure.Id && job.Activity == IndustryActivity.Manufacturing))
        {
            if (PilotIndustryStore.Ready(world, job, now) && CanMake(world, catalog, pilot, structure, job.ProductTypeId))
            {
                industry.Finish(world, catalog, pilot.PilotId, job.Id, pilot.SystemId, cancel: false);
            }
        }
        if (
            running.Any(job => job.Status == IndustryJobStatus.Installed)
            || PlanStructureProduction(world, catalog, pilot, structure, []) is not { } batch
        )
        {
            return;
        }
        var charge = EconomicStore.Manufacturing(
            world,
            pilot.PilotId,
            pilot.SystemId,
            IndustryMaterials.Calculate(batch.Recipe.Materials, batch.Runs),
            ManufacturingCatalog.Security(catalog, pilot.SystemId)
        );
        if (!Fund(world, corporation, pilot.PilotId, charge.TotalCents, now))
        {
            return;
        }
        foreach (var skill in batch.Recipe.Skills.Where(skill => skill == s_industryQualification))
        {
            Qualify(world, catalog, pilot.PilotId, skill);
        }
        industry.InstallManufacturing(
            world,
            catalog,
            pilot.PilotId,
            new(batch.Blueprint.Id, batch.Blueprint.TypeId, structure.Id, batch.Runs, batch.Recipe.ProductTypeId, 0, batch.Materials),
            corporation.Id,
            stockOperatorHangar: true
        );
    }
}
