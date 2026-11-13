using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;

namespace Eden.Data.Catalog.Mining;

public sealed record MiningDepositDefinition(int TypeId, SpacePosition Position, long Quantity, float Radius);

public sealed record MiningSiteDefinition(
    int DungeonId,
    int NameId,
    string Name,
    MiningFamily Family,
    int Difficulty,
    IReadOnlyList<int> Regions,
    IReadOnlyList<MiningDepositDefinition> Deposits,
    IReadOnlyList<int>? Systems = null,
    bool LowSecurityOnly = false
);

public sealed record MiningSiteDefinitions(int Build, IReadOnlyList<MiningSiteDefinition> Sites);
