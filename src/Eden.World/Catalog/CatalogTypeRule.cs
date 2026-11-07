namespace Eden.World.Catalog;

// Cache only canonical item/group inputs. Explicit variant probes retain their
// original validation path instead of sharing a result for a different group.
internal sealed class CatalogTypeRule(string name, Func<CatalogDbContext, int, int, bool> evaluate)
{
    private readonly CatalogDefinition<bool> _definition = new(
        name,
        (catalog, typeId) => evaluate(catalog, typeId, catalog.RequireItem(typeId).GroupId),
        128
    );

    internal bool Supports(CatalogDbContext catalog, int typeId, int groupId) =>
        catalog.FittingType(typeId)?.GroupId == groupId ? catalog.Definition(_definition, typeId) : evaluate(catalog, typeId, groupId);
}
