using Eden.Gameplay.Flight;
using Eden.Gameplay.Loot;

namespace Eden.Gameplay.Inventory;

public sealed record ShipInventoryAccess(long PilotId, long ActiveShipId, long StationId, bool Docked);

public sealed record ShipWreckAccess(WreckContact Wreck, SpacePosition ShipPosition, double ShipRadius);

public static class ShipStorageRules
{
    public static bool CanRead(ShipInventoryAccess access, InventoryItem ship, InventoryItem? parent, ShipWreckAccess? wreck = null)
    {
        if (ship.OwnerId != access.PilotId)
        {
            return false;
        }
        if (ship.Id == access.ActiveShipId)
        {
            return true;
        }
        if (!access.Docked && ship.FlagId == (int)InventoryFlag.Cargo && wreck is not null && ship.LocationId == wreck.Wreck.Id)
        {
            return wreck.Wreck.OwnerId == access.PilotId
                && wreck.ShipPosition.DistanceTo(wreck.Wreck.Position) - wreck.ShipRadius - wreck.Wreck.Radius
                    <= WreckDefinitions.TransferRange;
        }
        if (ShipHoldRules.Definition(ship.FlagId)?.AllowsAssembledShip == true)
        {
            return ship.LocationId == access.ActiveShipId
                || access.Docked
                    && parent?.OwnerId == access.PilotId
                    && parent.LocationId == access.StationId
                    && parent.FlagId == (int)InventoryFlag.Hangar;
        }
        return access.Docked && ship.LocationId == access.StationId && ship.FlagId == (int)InventoryFlag.Hangar;
    }

    public static void CheckContents(IReadOnlyList<InventoryItem> contents, bool containsShips)
    {
        if (
            containsShips
            || contents.Any(item =>
                ShipHoldRules.IsHold(item.FlagId)
                && item.FlagId is not ((int)InventoryFlag.DroneBay or (int)InventoryFlag.FighterBay)
                && !CanRetainCargo(item)
            )
        )
        {
            throw new InvalidDataException(
                "Only permitted cargo may remain aboard stored ships; remove other cargo and nested ships first."
            );
        }
    }

    // CCP's frigate escape-bay design uses the SMA/SMB cargo whitelist.
    // Nanite paste is already a charge in the pinned catalog (category 8).
    private static bool CanRetainCargo(InventoryItem item) =>
        item.CategoryId is (int)InventoryCategory.Charge or (int)InventoryCategory.Subsystem
        || item.GroupId == (int)HoldCargoGroup.Booster
        || item.TypeId is (int)HoldCargoType.LiquidOzone or (int)HoldCargoType.StrontiumClathrates;
}
