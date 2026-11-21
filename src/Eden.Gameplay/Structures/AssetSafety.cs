using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;

namespace Eden.Gameplay.Structures;

public sealed record AssetSafetyDestination(long Id, int TypeId, int SystemId, string Name);

public sealed record AssetSafetyWrap(
    long Id,
    long OwnerId,
    long StructureId,
    int SystemId,
    string Name,
    long CreatedAt,
    long ManualAt,
    long AutomaticAt,
    long? DestinationId,
    AssetSafetyDestination NearestStation
);

public sealed record AssetSafetyTargets(IReadOnlyList<AssetSafetyDestination> Structures, AssetSafetyDestination? NearestStation);

public sealed record AssetSafetyContents(InventoryItem Wrap, IReadOnlyList<InventoryItem> Items);

public sealed record AssetSafetyRecovery(IReadOnlyList<InventoryChange> Changes, long OwnerId, long BalanceCents);

public interface IPilotAssetSafety
{
    bool Available { get; }
    long Revision(long ownerId);
    bool Handles(long inventoryId);
    IReadOnlyList<AssetSafetyWrap> List(long pilotId, bool corporation);
    AssetSafetyWrap? Move(long pilotId, int systemId, long structureId, bool corporation);
    AssetSafetyWrap Deliver(long pilotId, long wrapId, int systemId, long destinationId);
    AssetSafetyTargets Destinations(long pilotId, int systemId);
    IReadOnlyDictionary<long, string?> Names(long pilotId, IReadOnlyList<long> ids);
    AssetSafetyContents Inventory(long pilotId, long wrapId);
    AssetSafetyRecovery Recover(long pilotId, long destinationId, InventoryMoveRequest request);
}

public static class AssetSafetyRules
{
    // Build 3662168 invconst.typeAssetSafetyWrap and appConst.refAssetSafetyRecoveryTax.
    public const int WrapTypeId = 60;
    public const int WrapGroupId = 1319;
    public const int ManualDays = 5;
    public const int AutomaticDays = 20;
    public const int MaximumItemsPerCall = 1000;
    public const long ManualDelay = ManualDays * TimeSpan.TicksPerDay;
    public const long AutomaticDelay = AutomaticDays * TimeSpan.TicksPerDay;
    public const decimal SameSystemFee = 0.005m;
    public const decimal RemoteFee = 0.15m;
    public const long FirstLootContainerId = 13_000_000_000_000;
    public const long LootContainerLimit = 13_100_000_000_000;

    public static bool IsLootContainer(long id) => id is >= FirstLootContainerId and < LootContainerLimit;

    public static bool CanAccessLoot(
        int system,
        int containerSystem,
        SpacePosition position,
        SpacePosition container,
        double shipRadius,
        double containerRadius
    ) => system == containerSystem && position.DistanceTo(container) - shipRadius - containerRadius <= WreckDefinitions.TransferRange;

    public static long RecoveryFee(decimal valueCents, bool sameSystem)
    {
        if (valueCents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(valueCents));
        }
        return checked((long)decimal.Ceiling(valueCents * (sameSystem ? SameSystemFee : RemoteFee)));
    }
}
