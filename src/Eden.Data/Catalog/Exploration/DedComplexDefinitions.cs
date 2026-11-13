using Eden.Gameplay.Agents;

namespace Eden.Data.Catalog.Exploration;

public sealed record DedLootSource(int TypeId, int Quantity = 1, bool BlueprintCopy = false);

public sealed record DedRoomMember(
    long AnchorId,
    int TypeId,
    int Count = 1,
    int FactionId = 0,
    string? Name = null,
    bool Structure = false,
    int Stage = 0
);

public sealed record DedGateBinding(
    long ObjectId,
    bool RequiresClear = false,
    MissionGateKey? Key = null,
    int DestinationRoomId = 0,
    int RequiredSkill = 0,
    int RequiredLevel = 0
);

public sealed record DedRoomBinding(
    int RoomId,
    long EntryObjectId,
    DedGateBinding? Gate,
    IReadOnlyList<DedRoomMember> Members,
    IReadOnlyList<DedGateBinding>? Gates = null
);

public sealed record DedSecondaryReward(int TypeId, IReadOnlyList<DedLootSource> Loot);

public sealed record DedComplexBinding(
    int DungeonId,
    int Rating,
    int RewardStructureType,
    IReadOnlyList<DedRoomBinding> Rooms,
    int OverseerType,
    IReadOnlyList<DedLootSource> OverseerLoot,
    IReadOnlyList<DedLootSource> GuaranteedLoot,
    IReadOnlyList<int> DeadspaceModules,
    IReadOnlyList<int> FactionModules,
    int Blueprint,
    int SecurityBands = 3,
    int Revision = 0,
    IReadOnlyList<DedSecondaryReward>? SecondaryRewards = null,
    int ClearRoomId = 0,
    int FollowUp = 0
);
