using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Loot;

public interface ICargoJettison
{
    JettisonResult Jettison(long pilotId, long shipId, IReadOnlyList<long> itemIds, long now);
    IReadOnlyList<InventoryChange> Deposit(long pilotId, long canId, InventoryMoveRequest request, long now);
}

public sealed record JettisonResult(IReadOnlyList<InventoryChange> Changes, long RetryAfter = 0);

public static class JetcanRules
{
    public const int TypeId = 23; // Build 3662168 inventorycommon.const.typeCargoContainer.
    public const int GroupId = 12;
    public const long FirstId = 8_500_000_000_000;
    public const long EndId = 8_600_000_000_000;
    public const int MaximumItemsPerCall = 1000;
    public const long Cooldown = 3 * TimeSpan.TicksPerMinute;
    public const long Lifetime = 2 * TimeSpan.TicksPerHour;
    private const double LaunchClearance = 1000;

    public static bool IsJetcan(long id) => id is >= FirstId and < EndId;

    public static bool CanJettison(InventoryItem item) =>
        item.Quantity > 0
        && item.CategoryId != (int)InventoryCategory.Ship
        && (InventoryFlag)item.FlagId
            is InventoryFlag.Cargo
                or InventoryFlag.GeneralMiningHold
                or InventoryFlag.MineralHold
                or InventoryFlag.AmmoHold
                or InventoryFlag.PlanetaryCommoditiesHold
                or InventoryFlag.AsteroidHold
                or InventoryFlag.GasHold
                or InventoryFlag.IceHold
                or InventoryFlag.InfrastructureHold
                or InventoryFlag.ExpeditionHold;

    public static SpacePosition LaunchPosition(SpacePosition origin, double shipRadius, double canRadius) =>
        origin + new SpacePosition(shipRadius + canRadius + LaunchClearance, 0, 0);
}
