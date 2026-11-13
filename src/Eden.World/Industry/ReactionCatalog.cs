using System.Collections.Frozen;
using Eden.Data.Catalog;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;

namespace Eden.World.Industry;

internal sealed record ReactionRecipe(
    int Formula,
    StructureService Service,
    int Output,
    int Quantity,
    long Seconds,
    IReadOnlyList<ManufacturingMaterial> Materials,
    IReadOnlyList<ManufacturingRequiredSkill> Skills
);

internal static class ReactionCatalog
{
    private const int CompositeFormulaGroup = 1888;
    private const int PolymerFormulaGroup = 1889;
    private const int MolecularForgedFormulaGroup = 4097;
    private const int FirstRacialEnhancerFormula = 57496;
    private const int LastRacialEnhancerFormula = 57499;
    private const int FirstRacialStabilizerFormula = 57503;
    private const int LastRacialStabilizerFormula = 57506;
    private const int UnrefinedMoonMaterialGroup = 428;
    private const int CompressedPrismaticite = 90307;

    private static readonly CatalogDefinition<FrozenDictionary<int, ReactionRecipe>> s_recipes = new("reaction-recipes", Load);

    internal static IReadOnlyDictionary<int, ReactionRecipe> Recipes(CatalogDbContext catalog) =>
        catalog.Definitions.GlobalDefinition(s_recipes);

    private static readonly CatalogDefinition<FrozenSet<int>> s_alchemyOutputs = new(
        "moon-alchemy-outputs",
        (catalog, _) =>
            Recipes(catalog)
                .Values.Where(recipe =>
                    catalog.FittingType(recipe.Output) is { GroupId: UnrefinedMoonMaterialGroup } type
                    && type.Name.StartsWith("Unrefined ", StringComparison.Ordinal)
                )
                .Select(recipe => recipe.Output)
                .ToFrozenSet()
    );

    internal static bool IsMoonAlchemyOutput(CatalogDbContext catalog, int typeId) =>
        catalog.Definitions.GlobalDefinition(s_alchemyOutputs).Contains(typeId);

    internal static StructureService? Service(int formula, int group) =>
        group switch
        {
            CompositeFormulaGroup => StructureService.CompositeReactions,
            PolymerFormulaGroup => StructureService.HybridReactions,
            MolecularForgedFormulaGroup
                when formula
                    is >= FirstRacialEnhancerFormula
                        and <= LastRacialEnhancerFormula
                        or >= FirstRacialStabilizerFormula
                        and <= LastRacialStabilizerFormula => StructureService.BiochemicalReactions,
            _ => null,
        };

    private static FrozenDictionary<int, ReactionRecipe> Load(CatalogDbContext catalog, int unused)
    {
        var definitions = catalog.Definitions;
        var formulas = new[] { CompositeFormulaGroup, PolymerFormulaGroup, MolecularForgedFormulaGroup }
            .SelectMany(group => definitions.TypesInGroup(group).Select(id => (Id: id, Service: Service(id, group))))
            .Where(formula => formula.Service.HasValue)
            .ToDictionary(formula => formula.Id, formula => formula.Service!.Value);
        var types = definitions.FindMany(CatalogData.Client.Types, formulas.Keys.Select(id => (long)id));
        var activities = definitions.Pin(CatalogData.Client.Blueprints);
        return activities
            .Where(pair => pair.Value.BlueprintTypeID is > 0 and <= int.MaxValue)
            .Select(pair => (Id: (int)pair.Value.BlueprintTypeID!.Value, Reaction: pair.Value.Activities?.Reaction))
            .Where(entry => types.GetValueOrDefault(entry.Id)?.Published == 1 && entry.Reaction is not null)
            .Select(entry => Validate(catalog, entry.Id, formulas[entry.Id], entry.Reaction!))
            .OfType<ReactionRecipe>()
            .ToFrozenDictionary(recipe => recipe.Formula);
    }

    private static ReactionRecipe? Validate(
        CatalogDbContext catalog,
        int formula,
        StructureService service,
        ClientBlueprintsActivitiesManufacturing recipe
    )
    {
        if (
            recipe.Products is not [{ TypeID: > 0 and <= int.MaxValue, Quantity: > 0 and <= int.MaxValue } output]
            || recipe.Time is not > 0
            || recipe.Materials is not { Length: > 0 } materials
            || materials.Any(m =>
                m.TypeID is not (> 0 and <= int.MaxValue)
                || m.TypeID == CompressedPrismaticite
                || m.Quantity is not (> 0 and <= int.MaxValue)
                || catalog.FittingType(m.TypeID.Value) is null
            )
            || materials.Select(m => m.TypeID).Distinct().Count() != materials.Length
            || catalog.Read(CatalogData.Client.Types, output.TypeID!.Value)?.Published != 1
            || (recipe.Skills ?? []).Any(s =>
                s.TypeID is not (> 0 and <= int.MaxValue)
                || s.Level is not (>= 1 and <= 5)
                || catalog.FittingType(s.TypeID.Value)?.CategoryId != (int)InventoryCategory.Skill
            )
        )
        {
            return null;
        }
        return new(
            formula,
            service,
            (int)output.TypeID!.Value,
            (int)output.Quantity!.Value,
            recipe.Time.Value,
            materials.Select(m => new ManufacturingMaterial(m.TypeID, m.Quantity)).ToArray(),
            (recipe.Skills ?? []).Select(s => new ManufacturingRequiredSkill((int)s.TypeID!.Value, (int)s.Level!.Value)).ToArray()
        );
    }
}
