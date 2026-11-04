using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Contracts;

public sealed record CourierTerms(int DestinationStationId, int DurationDays, long CollateralCents);

public sealed record ContractDestination(int StationId, int SystemId, int ConstellationId, int RegionId);

public sealed record CourierContract(ContractDestination Destination, int DurationDays, long CollateralCents, long? PackageId);

public sealed record CourierPackageInfo(long ContractId, long PackageId, long AcceptorId, ContractStatus Status, InventoryItem Item);

public static class CourierRules
{
    // Build 3662168 inventorycommon.const and inventorycommon.util.GetItemVolume.
    public const int PackageType = 3468;
    public const int PackageGroup = 649;
    public const int PackageCategory = 2;
    public const int VolumeUnitsPerCubicMetre = 100;
    public const double MaximumVolume = 1_200_000;
    public const int MaximumDurationDays = 28;
    public const decimal FeeMultiplier = 0.1m;

    public static void Check(ContractDraft draft)
    {
        if (draft.Type == ContractType.ItemExchange && draft.Courier is null)
        {
            return;
        }
        if (
            draft.Type != ContractType.Courier
            || draft.Courier is not { } terms
            || terms.DestinationStationId <= 0
            || terms.DurationDays is < 1 or > MaximumDurationDays
            || terms.CollateralCents is < 0 or > ContractRules.MaximumAmountCents
            || draft.PriceCents != 0
            || draft.RequestedItems.Count > 0
            || draft.Items.Count == 0
        )
        {
            throw new InvalidDataException("Choose valid courier goods, destination, duration and collateral.");
        }
    }

    public static int VolumeUnits(double volume)
    {
        if (!double.IsFinite(volume) || volume < 0 || volume > MaximumVolume)
        {
            throw new InvalidDataException("Courier packages cannot exceed 1,200,000 cubic metres.");
        }
        // Native wraps require a positive centi-cubic-metre quantity. Round upward
        // so capacity checks never understate the enclosed goods' actual volume.
        return checked((int)Math.Max(1, decimal.Ceiling((decimal)volume * VolumeUnitsPerCubicMetre)));
    }

    public static double PackageVolume(double volume) => (double)VolumeUnits(volume) / VolumeUnitsPerCubicMetre;

    public static long Deadline(long accepted, int days) => checked(accepted + TimeSpan.FromDays(days).Ticks);
}
