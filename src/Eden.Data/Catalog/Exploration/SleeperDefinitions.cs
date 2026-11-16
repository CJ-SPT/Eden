using System.Collections.Immutable;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;

namespace Eden.Data.Catalog.Exploration;

public sealed record SleeperEncounterDefinitions(int Build, ImmutableArray<AnomalyEncounterReport> Encounters);

public sealed record SleeperNpcLoot(int TypeId, string Source, IReadOnlyList<NpcLootDrop> Drops);

public sealed record SleeperLootDefinitions(IReadOnlyList<SleeperNpcLoot> Npcs);
