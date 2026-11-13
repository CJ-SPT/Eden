using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Fittings;

public sealed record FittingSlot(int FlagId, int TypeId);

public sealed record ApplyFittingRequest(
    long ShipId,
    int ShipTypeId,
    long SourceId,
    IReadOnlyDictionary<int, IReadOnlyList<long>> SourceItems,
    IReadOnlyList<FittingSlot> Slots,
    IReadOnlyList<SavedFittingItem> Supplies,
    bool FitRigs
);

public sealed record MissingFittingItem(int TypeId, int Quantity);

public sealed record AppliedFitting(IReadOnlyList<InventoryChange> Changes, IReadOnlyList<MissingFittingItem> Missing);

public interface IPilotApplyFitting
{
    AppliedFitting Apply(long pilotId, ApplyFittingRequest request);
}
