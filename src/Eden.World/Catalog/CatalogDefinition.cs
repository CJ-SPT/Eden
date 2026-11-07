namespace Eden.World.Catalog;

// A definition loader receives static catalog inputs only. Personal fitting
// results remain in the transactionally versioned resident world cache.
internal sealed record CatalogDefinition<T>(string Name, Func<CatalogDbContext, int, T> Load, long EstimatedBytes = 1024);
