using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Fittings;

public sealed record MultifitRequest(ApplyFittingRequest Fitting, int Count, string? Name, long? ContainerId);

public sealed record MultifitResult(
    IReadOnlyList<long> ShipIds,
    IReadOnlyList<InventoryChange> Changes,
    IReadOnlyList<MissingFittingItem> Missing
);

public interface IPilotMultifit
{
    MultifitResult FitShips(long pilotId, MultifitRequest request);
}

public static class MultifitRules
{
    // Eden bounds one atomic batch independently of the client's availability calculation.
    public const int MaximumShips = 100;

    public static IReadOnlyList<MissingFittingItem> RequiredItems(MultifitRequest request)
    {
        Check(request);
        var fitting = request.Fitting;
        return fitting
            .Slots.Where(slot => fitting.FitRigs || !InventoryDefinitions.IsRigSlot(slot.FlagId))
            .Select(slot => new MissingFittingItem(slot.TypeId, 1))
            .Concat(fitting.Supplies.Select(item => new MissingFittingItem(item.TypeId, item.Quantity)))
            .Append(new(fitting.ShipTypeId, 1))
            .GroupBy(item => item.TypeId)
            .Select(group => new MissingFittingItem(group.Key, checked(group.Sum(item => item.Quantity) * request.Count)))
            .OrderBy(item => item.TypeId)
            .ToArray();
    }

    public static void Check(MultifitRequest request)
    {
        if (
            request.Count is < 1 or > MaximumShips
            || request.Name?.Length > FleetDefinitions.MaximumShipNameLength
            || request.Name?.Any(char.IsControl) == true
            || request.Fitting.ShipTypeId <= 0
            || request.Fitting.SourceId <= 0
            || request.ContainerId is < 0
            || request.ContainerId is > 0 && request.ContainerId != request.Fitting.SourceId
        )
        {
            throw new InvalidDataException("Multifit 1–100 packaged ships from your current item hangar with a valid ship name.");
        }
    }
}
