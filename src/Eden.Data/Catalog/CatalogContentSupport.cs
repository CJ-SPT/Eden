using System.Collections.Immutable;

namespace Eden.Data.Catalog;

public sealed record ManufacturingContentSupport(ImmutableHashSet<int> HullTypes, ImmutableHashSet<int> UtilityTypes);

public sealed record InsuranceContentSupport(ImmutableHashSet<int> MaterialValuedHullTypes);

public sealed record CatalogContentSupport(int Build, ManufacturingContentSupport Manufacturing, InsuranceContentSupport Insurance);
