using System.Collections.Immutable;
using Eden.Gameplay.FactionWarfare;

namespace Eden.Data.Catalog.FactionWarfare;

internal sealed record WarzoneSeed(int SystemId, MilitiaFaction OriginalOccupier, ImmutableArray<int> Neighbors);
