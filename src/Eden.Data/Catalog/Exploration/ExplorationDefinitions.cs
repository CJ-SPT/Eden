using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;

namespace Eden.Data.Catalog.Exploration;

public sealed record ExplorationContainerDefinition(int TypeId, int Difficulty, SpacePosition Position);

public sealed record ExplorationDefinition(
    int DungeonId,
    int NameId,
    string Name,
    int FactionId,
    ExplorationKind Kind,
    int Band,
    int Difficulty,
    IReadOnlyList<ExplorationContainerDefinition> Containers
);

public sealed record ExplorationDefinitions(int Build, IReadOnlyList<ExplorationDefinition> Sites);
