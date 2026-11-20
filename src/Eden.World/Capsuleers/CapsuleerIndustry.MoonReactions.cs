using Eden.Data.Catalog;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Industry;
using Eden.World.Mining;
using Eden.World.Reprocessing;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Structures;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private const int MoonReactionRuns = 1;

    private static readonly CatalogDefinition<IReadOnlyDictionary<int, int>> s_moonReactionFeed = new(
        "capsuleers/moon-reaction-feed",
        (catalog, _) =>
        {
            var inputs = ReactionCatalog
                .Recipes(catalog)
                .Values.Where(recipe => recipe.Service == StructureService.CompositeReactions)
                .SelectMany(recipe => recipe.Materials.Select(material => (int)material.TypeId!.Value))
                .ToHashSet();
            return MoonMiningRules
                .OreTypes.SelectMany(ore => catalog.Read(CatalogData.Client.Typematerials, (long)ore)?.Materials ?? [])
                .Where(material =>
                    material.MaterialTypeID is > 0 and <= int.MaxValue && inputs.Contains((int)material.MaterialTypeID.Value)
                )
                .Select(material => (int)material.MaterialTypeID!.Value)
                .Distinct()
                .ToDictionary(type => type, _ => 1);
        }
    );

    internal sealed record MoonMiningTrip(long ExtractionId, int MoonId, StaticSpaceObject Target);

    internal static MoonMiningTrip? PlanMoonMining(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        long now,
        FactoryMoonSupply? supply = null
    )
    {
        if (
            pilot.StationId == 0
            || !PilotReprocessingStore.SupportsNpcReprocessing(catalog, pilot.StationId)
            || Employer(world, pilot.PilotId, pilot.CorpId) is null
        )
        {
            return null;
        }
        HashSet<int>? required = supply is null
            ? null
            : PlanMoonInputs(
                catalog,
                supply,
                Hangar(world, pilot.PilotId, pilot.StationId),
                Hangar(world, pilot.PilotId, supply.StructureId)
            )
                .Inputs.Select(input => input.TypeId)
                .ToHashSet();
        var fields = world
            .Filter<SavedMoonExtraction>(e => e.SystemId == pilot.SystemId)
            .Where(e => e.CorporationId == pilot.CorpId && e.FracturedAt.HasValue && e.FieldExpiresAt > now)
            .OrderBy(e => e.FieldExpiresAt)
            .ThenBy(e => e.Id)
            .AsEnumerable()
            .Where(e =>
                world.Set<SavedStructure>().Find(e.StructureId) is { } structure
                && OwnedMoonRefinery(structure)
                && structure.CorporationId == pilot.CorpId
                && PilotStructureStore.Access(world, structure, pilot.PilotId)
            )
            .ToArray();
        if (fields.Length == 0)
        {
            return null;
        }
        var rocks = AsteroidFieldStore
            .List(world, catalog, pilot.SystemId)
            .Where(rock =>
                MoonMiningRules.IsMoonRock(rock.Id)
                && (
                    required is null
                    || catalog
                        .Read(CatalogData.Client.Typematerials, (long)rock.TypeId)
                        ?.Materials?.Any(material =>
                            material.MaterialTypeID.HasValue && required.Contains((int)material.MaterialTypeID.Value)
                        ) == true
                )
            )
            .ToLookup(rock => MoonMiningRules.ExtractionId(rock.Id));
        foreach (var field in fields)
        {
            if (rocks[field.Id].FirstOrDefault() is { } target)
            {
                return new(field.Id, field.MoonId, target);
            }
        }
        return null;
    }

    internal void RefineMoonFeed(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, bool sponsored)
    {
        if (
            pilot.StationId == 0
            || Employer(world, pilot.PilotId, pilot.CorpId) is not { } corporation
            || !world
                .Filter<SavedStructure>(structure => structure.CorporationId == pilot.CorpId)
                .AsEnumerable()
                .Any(structure => structure.SystemId == pilot.SystemId && OwnedMoonRefinery(structure))
        )
        {
            return;
        }
        var needed = catalog.Definitions.GlobalDefinition(s_moonReactionFeed);
        var grades = MoonMiningRules.OreTypes.ToHashSet();
        foreach (var ore in Hangar(world, pilot.PilotId, pilot.StationId).Where(item => grades.Contains(item.TypeId)))
        {
            refinery.RefineForProduction(world, catalog, pilot.PilotId, pilot.StationId, ore, needed, sponsored ? corporation : null);
        }
    }

    private sealed record MoonReactionBatch(ReactionRecipe Recipe, OwnedItem Formula, CapsuleerFactoryCargo[] Load, bool Assigned);

    private static bool CanRunComposite(
        WorldAction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        SavedStructure structure,
        StructureService service
    ) =>
        structure.CorporationId == pilot.CorpId
        && OwnedMoonRefinery(structure)
        && service == StructureService.CompositeReactions
        && ManufacturingCatalog.Security(catalog, structure.SystemId) < StructureRules.HighSecurityBoundary
        && PilotStructureStore.Snapshot(world, catalog, structure).Services.Contains(service);

    private static MoonReactionBatch? PlanMoonReaction(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        SavedStructure structure,
        OwnedItem[] origin,
        long now
    )
    {
        if (!CanRunComposite(world, catalog, pilot, structure, StructureService.CompositeReactions))
        {
            return null;
        }
        var feed = catalog.Definitions.GlobalDefinition(s_moonReactionFeed);
        var recipes = ReactionCatalog.Recipes(catalog);
        var local = Hangar(world, pilot.PilotId, structure.Id);
        var supply = AssignedMoonSupply(world, pilot, now);
        var station = supply is not null && pilot.InStructure ? Hangar(world, pilot.PilotId, supply.StationId) : origin;
        var formulas = supply?.StructureId == structure.Id ? PlanMoonInputs(catalog, supply, station, local).Formulas : null;
        var items = local.Concat(origin).ToLookup(item => item.TypeId);
        foreach (var formula in formulas is null ? local.Concat(origin) : formulas.SelectMany(type => items[type]))
        {
            if (
                formula.BlueprintRuns >= 0
                || !recipes.TryGetValue(formula.TypeId, out var recipe)
                || recipe.Service != StructureService.CompositeReactions
                || formulas is null
                    && (
                        !recipe.Materials.Any(material => feed.ContainsKey((int)material.TypeId!.Value))
                        || local.Any(item => item.TypeId == recipe.Output)
                    )
                || PilotIndustryStore.IsInstalled(world, formula.Id)
                || recipe.Skills.Any(skill => PilotSkillReader.Level(world, catalog, pilot.PilotId, skill.TypeId) < skill.Level)
            )
            {
                continue;
            }
            var materials = IndustryMaterials.Calculate(recipe.Materials, MoonReactionRuns);
            if (FactoryCargo(structure.Id, formula, materials, local, origin) is { } load)
            {
                return new(recipe, formula, load, formulas is not null);
            }
        }
        return null;
    }

    private bool TryRunMoonReaction(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        SavedCorporation corporation,
        SavedStructure structure,
        long now
    )
    {
        if (PlanMoonReaction(world, catalog, pilot, structure, [], now) is not { } batch)
        {
            return false;
        }
        int formula = batch.Formula.TypeId;
        var charge = PilotIndustryStore.ReactionCharge(
            world,
            pilot.PilotId,
            pilot.SystemId,
            formula,
            MoonReactionRuns,
            ManufacturingCatalog.Security(catalog, pilot.SystemId)
        );
        if (!Fund(world, corporation, pilot.PilotId, charge.TotalCents, now))
        {
            return false;
        }
        industry.InstallReaction(
            world,
            catalog,
            pilot.PilotId,
            new(
                batch.Formula.Id,
                formula,
                structure.Id,
                MoonReactionRuns,
                batch.Recipe.Output,
                0,
                IndustryMaterials.Calculate(batch.Recipe.Materials, MoonReactionRuns),
                Activity: IndustryActivity.Reaction
            ),
            corporation.Id
        );
        return true;
    }
}
