using System.Collections.Frozen;
using System.Collections.Immutable;
using Eden.Data.Catalog;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;

namespace Eden.World.Industry;

internal sealed record ManufacturingRecipeDefinition(
    int BlueprintTypeId,
    int ProductTypeId,
    int OutputQuantity,
    long Seconds,
    ImmutableArray<ManufacturingMaterial> Materials,
    ImmutableArray<ManufacturingRequiredSkill> Skills
);

internal sealed record ManufacturingRequiredSkill(int TypeId, int Level);

internal static class ManufacturingCatalog
{
    private static readonly CatalogDefinition<FrozenDictionary<int, ManufacturingRecipeDefinition>> s_recipes = new(
        "manufacturing-recipes",
        Load
    );

    internal static IReadOnlyDictionary<int, ManufacturingRecipeDefinition> Recipes(CatalogDbContext catalog) =>
        catalog.Definitions.GlobalDefinition(s_recipes);

    internal static double Security(CatalogDbContext catalog, int systemId) =>
        catalog.Definitions.Find(CatalogData.Client.Systems, (long)systemId)?.SecurityStatus
        ?? catalog.Definitions.Find(CatalogData.Client.Solarsystemcontent, (long)systemId)?.Security
        ?? double.NaN;

    internal static bool Allowed(CatalogDbContext catalog, int productId, double security)
    {
        var product = catalog.Definitions.Items[productId];
        return ProductionFacilityRules.CanManufacture((InventoryCategory)product.CategoryId, product.GroupId, security);
    }

    private static FrozenDictionary<int, ManufacturingRecipeDefinition> Load(CatalogDbContext catalog, int unused)
    {
        Dictionary<int, ManufacturingRecipeDefinition> result = [];
        var definitions = catalog.Definitions.Pin(CatalogData.Client.Blueprints);
        var typeIds = definitions.Values.SelectMany(d =>
            new[] { d.BlueprintTypeID ?? 0 }.Concat((d.Activities?.Manufacturing?.Products ?? []).Select(p => p.TypeID ?? 0))
        );
        var types = catalog.Definitions.FindMany(CatalogData.Client.Types, typeIds.Distinct());
        foreach (var blueprint in definitions.Values)
        {
            var recipe = blueprint.Activities?.Manufacturing;
            if (
                blueprint.BlueprintTypeID is not > 0 or > int.MaxValue
                || recipe?.Products is not [var product]
                || product.TypeID is not > 0 or > int.MaxValue
                || product.Quantity is not > 0 or > int.MaxValue
                || recipe.Time is not > 0
                || types.GetValueOrDefault(blueprint.BlueprintTypeID.Value)?.Published != 1
                || types.GetValueOrDefault(product.TypeID.Value)?.Published != 1
                || !catalog.Definitions.Items.TryGetValue((int)blueprint.BlueprintTypeID.Value, out var source)
                || source.CategoryId != (int)InventoryCategory.Blueprint
                || !catalog.Definitions.Items.ContainsKey((int)product.TypeID.Value)
                || recipe.Materials is not { Length: > 0 }
                || (recipe.Materials ?? []).Any(m =>
                    m.TypeID is not > 0 or > int.MaxValue
                    || m.Quantity is not > 0 or > int.MaxValue
                    || !catalog.Definitions.Items.ContainsKey((int)m.TypeID.Value)
                )
                || (recipe.Materials ?? []).Select(m => m.TypeID).Distinct().Count() != (recipe.Materials ?? []).Length
                || (recipe.Skills ?? []).Any(s =>
                    s.TypeID is not > 0 or > int.MaxValue
                    || s.Level is not >= 1 or > 5
                    || !catalog.Definitions.Items.TryGetValue((int)s.TypeID.Value, out var skill)
                    || skill.CategoryId != (int)InventoryCategory.Skill
                )
            )
            {
                continue;
            }
            int blueprintId = (int)blueprint.BlueprintTypeID.Value;
            result.Add(
                blueprintId,
                new(
                    blueprintId,
                    (int)product.TypeID.Value,
                    (int)product.Quantity.Value,
                    recipe.Time.Value,
                    (recipe.Materials ?? []).Select(m => new ManufacturingMaterial(m.TypeID, m.Quantity)).ToImmutableArray(),
                    (recipe.Skills ?? [])
                        .Select(s => new ManufacturingRequiredSkill((int)s.TypeID!.Value, (int)s.Level!.Value))
                        .ToImmutableArray()
                )
            );
        }
        return result.ToFrozenDictionary();
    }
}
