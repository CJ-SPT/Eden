using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Fittings;

public sealed record SavedFittingItem(int TypeId, InventoryFlag Flag, int Quantity);

public sealed record FittingPlan(string Name, string Description, int ShipTypeId, IReadOnlyList<SavedFittingItem> Items);

public sealed record SavedFitting(long Id, long OwnerId, FittingPlan Plan, long SavedAt);

public sealed record FittingImport(long TemporaryId, FittingPlan Plan);

public sealed record FittingImportResult(long TemporaryId, long FittingId);

public interface IPilotSavedFittings
{
    long Revision(long ownerId);
    IReadOnlyList<SavedFitting> Read(long pilotId, long? ownerId = null);
    long Save(long pilotId, FittingPlan plan, long? fittingId = null, long? ownerId = null);
    IReadOnlyList<FittingImportResult> SaveMany(long pilotId, long ownerId, IReadOnlyList<FittingImport> plans);
    void Rename(long pilotId, long fittingId, string name, string description, long? ownerId = null);
    void Delete(long pilotId, long fittingId, long? ownerId = null);
    IReadOnlyList<long> DeleteMany(long pilotId, long ownerId, IReadOnlyList<long> fittingIds);
}

public static class SavedFittingRules
{
    // Build 3662168 appConst and fittingSvc.VerifyFitting.
    public const int MaximumFittings = 500;
    public const int MaximumCorporationFittings = 600;
    public const int MaximumNameLength = 50;
    public const int MaximumDescriptionLength = 500;

    // Bound template storage without applying real hull capacity or fitting restrictions.
    public const int MaximumItems = 1024;

    public static void CheckBatch(IReadOnlyList<long> ids, bool temporary)
    {
        if (ids.Count > MaximumCorporationFittings || ids.Distinct().Count() != ids.Count || ids.Any(id => temporary ? id >= 0 : id <= 0))
        {
            throw new InvalidDataException("Choose distinct fitting IDs within the batch limit.");
        }
    }

    public static void CheckText(string name, string description)
    {
        if (
            string.IsNullOrWhiteSpace(name)
            || name.Length > MaximumNameLength
            || description.Length > MaximumDescriptionLength
            || name.Any(char.IsControl)
            || description.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
            || name.Contains("@@", StringComparison.Ordinal)
            || description.Contains("@@", StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException("Use a fitting name of 1–50 characters and a description of at most 500 characters.");
        }
    }

    public static void Check(FittingPlan plan)
    {
        CheckText(plan.Name, plan.Description);
        if (plan.ShipTypeId <= 0 || plan.Items.Count is < 1 or > MaximumItems)
        {
            throw new InvalidDataException("Choose a ship and between 1 and 1024 fitting entries.");
        }
        foreach (var item in plan.Items)
        {
            if (item.TypeId <= 0 || item.Quantity <= 0 || !AllowedFlag(item.Flag))
            {
                throw new InvalidDataException("Fitting entries need a known type, a fitting or cargo location, and a positive quantity.");
            }
        }
    }

    private static bool AllowedFlag(InventoryFlag flag) =>
        flag
            is >= InventoryFlag.LowSlot0
                and <= InventoryFlag.HighSlot7
                or >= InventoryFlag.RigSlot0
                and <= InventoryFlag.RigSlot2
                or >= InventoryFlag.SubsystemSlot0
                and <= InventoryFlag.SubsystemSlot3
                or >= InventoryFlag.ServiceSlot0
                and <= InventoryFlag.ServiceSlot7
                or InventoryFlag.Cargo
                or InventoryFlag.DroneBay
                or InventoryFlag.FighterBay
                or InventoryFlag.HiddenModifiers;
}
