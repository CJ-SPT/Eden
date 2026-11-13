using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Loot;

public sealed record WreckContact(
    long Id,
    long SourceId,
    long OwnerId,
    int CorporationId,
    int TypeId,
    string Name,
    int SystemId,
    SpacePosition Position,
    double Mass,
    float Radius,
    bool Empty,
    int GroupId = WreckDefinitions.Group,
    int CategoryId = WreckDefinitions.Category,
    int? PlanetId = null
);

public sealed record WreckInventory(InventoryItem Container, IReadOnlyList<InventoryItem> Items, double? Capacity = null);

public interface IWrecks
{
    IReadOnlyList<WreckContact> List(int systemId, long now);
    WreckInventory Read(long pilotId, long wreckId, long now);
    IReadOnlyList<InventoryChange> Take(
        long pilotId,
        long shipId,
        long wreckId,
        IReadOnlyList<long> itemIds,
        int? quantity,
        long now,
        InventoryFlag destinationFlag = InventoryFlag.Cargo
    );
}

public static class WreckDefinitions
{
    public const int Group = 186;
    public const int Category = 2;
    public const long FirstWreckId = 8_000_000_000_000;

    // Player wreck identities use the lost hull's persistent ID in a separate
    // range from NPC wrecks; a retry cannot create another container.
    public const long FirstPlayerWreckId = 10_000_000_000_000;

    // Pinned appConst.maxCargoContainerTransferDistance. Visibility follows
    // Eden's local grid policy; the two-hour lifetime and loot are Eden policy.
    public const double TransferRange = 2500;
    public const double VisibilityRange = 250_000;
    public const int LifetimeSeconds = 7200;
    public const float StationaryAgility = 1;
    public const int StarterLootType = 34; // Catalog Tritanium.
    public const int StarterLootQuantity = 100;
}
