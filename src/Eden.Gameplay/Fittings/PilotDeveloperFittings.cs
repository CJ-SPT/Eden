using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Fittings;

public sealed record DeveloperFitRequest(string Type, int Quantity, int? Flag);

public sealed record DeveloperFitChange(IReadOnlyList<InventoryChange> Items, IReadOnlyList<MagazineChange> Magazines, double Reload = 0);

public sealed record DeveloperShipSetup(int ShipTypeId, IReadOnlyDictionary<int, int> SlotTypes);

public sealed record DeveloperShipCopy(long ShipId, string Name, IReadOnlyList<InventoryChange> Changes);

public interface IPilotDeveloperFittings
{
    DeveloperFitChange Fit(long pilot, long ship, DeveloperFitRequest request);
    DeveloperFitChange Unload(long pilot, long ship, string selector);
    DeveloperFitChange Online(long pilot, long ship);
    DeveloperShipSetup Setup(long ship);
    DeveloperShipCopy CopyDocked(long pilot, long ship);
}
