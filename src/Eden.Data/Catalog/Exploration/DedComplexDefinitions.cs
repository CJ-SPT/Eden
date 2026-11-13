using Eden.Gameplay.Agents;

namespace Eden.Data.Catalog.Exploration;

public sealed record DedLootSource(int TypeId, int Quantity = 1, bool BlueprintCopy = false);

public sealed record DedRoomMember(
    long AnchorId,
    int TypeId,
    int Count = 1,
    int FactionId = 0,
    string? Name = null,
    bool Structure = false
);

public sealed record DedGateBinding(long ObjectId, bool RequiresClear = false, MissionGateKey? Key = null);

public sealed record DedRoomBinding(int RoomId, long EntryObjectId, DedGateBinding? Gate, IReadOnlyList<DedRoomMember> Members);

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
    int Blueprint
);
