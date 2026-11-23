using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Inventory;

namespace Eden.Data.Catalog.Agents;

public sealed record MissionSpawnGroup(
    ImmutableArray<int> Types,
    int Count,
    double Range,
    bool Trigger = false,
    bool CompletesMission = false,
    bool Required = true,
    int EnemyFaction = 0,
    int LootTypeId = 0,
    int LootQuantity = 0,
    bool LootToPilotCargo = false,
    bool CareerPassive = false,
    string? CareerName = null
);

[JsonConverter(typeof(JsonStringEnumConverter<MissionWaveTrigger>))]
public enum MissionWaveTrigger
{
    PreviousWaveCleared,
    Visit,
    StructureDamaged,
    StructureDestroyed,
    Mining,
    HackResolved,
    Delivery,
    RoomArrivalDelay,
    NpcHealthThreshold,
}

public sealed record MissionNpcHealthTrigger(int SourceWave, int NpcTypeId, MissionHealthLayer Layer, double RemainingFraction);

public sealed record MissionWave(
    ImmutableArray<MissionSpawnGroup> Groups,
    int RoomId = 0,
    MissionWaveTrigger Trigger = MissionWaveTrigger.PreviousWaveCleared,
    long TriggerObjectId = 0,
    long DelayTicks = 0,
    MissionNpcHealthTrigger? HealthTrigger = null
);

public sealed record MissionCareerVisitDefinition(long ObjectId, double Range, bool RemoveObject = false, double ThermalDamage = 0)
{
    public bool RequiresClear { get; init; }
    public int? RequiredCargoTypeId { get; init; }
}

public sealed record MissionItemRewardDefinition(ImmutableArray<int> Types, int Quantity);

public sealed record MissionRoomDefinition(int RoomId, long EntryObjectId);

public sealed record CareerMissionDeposit(long ObjectId, int Quantity);

public sealed record MissionContainerDefinition(long ObjectId, int TypeId, int Quantity, bool RequiresClear = false)
{
    public ExplorationKind? Analyzer { get; init; }
    public bool CompletesOnHack { get; init; }
    public bool RequiresMiningDepletion { get; init; }
    public bool CompletesOnTake { get; init; }
    public ImmutableArray<MissionItemReward> AdditionalItems { get; init; } = [];
    public int DepositTypeId { get; init; }
    public int DepositQuantity { get; init; }
    public long? DepositRequiresDestroyedObjectId { get; init; }
    public bool DepositCompletesMission { get; init; } = true;
}

public sealed record MissionStructureDrop(int TypeId, int Quantity);

public sealed record MissionStructureDefinition(
    long ObjectId,
    bool Required = false,
    bool CompletesMission = false,
    MissionStructureDrop? Drop = null
);

public sealed record MissionGateDefinition(long FromObjectId, int DestinationRoomId, bool RequiresClear = false, MissionGateKey? Key = null)
{
    public long? RequiresDeliveryObjectId { get; init; }
    public bool RequiresMiningSurvey { get; init; }
}

public sealed record MissionDefinition(
    int MissionId,
    int Level,
    int Difficulty,
    bool Storyline,
    string Source,
    string SourceRevision,
    ImmutableArray<MissionWave> Waves
)
{
    public MissionScanKind? Scan { get; init; }
    public ImmutableArray<MissionRoomDefinition> Rooms { get; init; } = [];
    public ImmutableArray<MissionGateDefinition> Gates { get; init; } = [];
    public ImmutableArray<MissionContainerDefinition> Containers { get; init; } = [];
    public ImmutableArray<MissionStructureDefinition> Structures { get; init; } = [];
    public MissionCareerVisitDefinition? CareerVisit { get; init; }
    public bool Deadspace { get; init; } = true;
    public int OfferOrder { get; init; }
    public bool Mining { get; init; }
    public ImmutableArray<CareerMissionDeposit> CareerDeposits { get; init; } = [];
    public long? SalvageObjectId { get; init; }
    public MissionChainPart? Chain { get; init; }
    public bool Trade { get; init; }
    public MissionItemRewardDefinition? ItemReward { get; init; }
    public ImmutableArray<long> FixedAgentIds { get; init; } = [];
    public bool Career { get; init; }
    public int? EpicArcId { get; init; }
    public int? SiteSystemId { get; init; }
    public int? DeliveryStationId { get; init; }
    public int? PickupStationId { get; init; }
    public bool PreserveObjectiveCargo { get; init; }
    public bool FailOnCargoLoss { get; init; }
    public int? CargoLossRetryMissionId { get; init; }
    public bool DeliverToAgent { get; init; }
    public bool Scripted => Career || EpicArcId.HasValue;
    public int? DungeonId { get; init; }
    public int? ObjectiveTypeId { get; init; }
    public int? ObjectiveQuantity { get; init; }
    public int? GiftBlueprintRuns { get; init; }
    public int? GiftTypeId { get; init; }
    public int? GiftQuantity { get; init; }
    public ImmutableArray<MissionItemReward> AdditionalGifts { get; init; } = [];
    public bool RequiresAllHacks { get; init; }
    public MissionCareerEffect? CareerEffect { get; init; }
    public MissionCareerHazard? CareerHazard { get; init; }
    public bool CareerSacrifice { get; init; }
    public bool CareerShipLoss { get; init; }
    public bool CareerRescue { get; init; }
}

public sealed record MissionCareerEffect(int TypeId, DogmaEffect Effect);

public sealed record MissionCareerHazard(long ObjectId, double Range, double ElectromagneticDamage, int IntervalSeconds);

public sealed record MissionContent(int Build, ImmutableArray<MissionDefinition> Missions);
