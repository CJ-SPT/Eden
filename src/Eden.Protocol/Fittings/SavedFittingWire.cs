using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Fittings;

internal static class SavedFittingWire
{
    internal static FittingPlan Read(object? value, long owner)
    {
        if (value is not BlueInstance { Name: "utillib.KeyVal", State: Dictionary<string, object?> fields })
        {
            throw new InvalidDataException("Expected native saved fitting data.");
        }
        if (fields.TryGetValue("ownerID", out var suppliedOwner) && !Equals(suppliedOwner, owner))
        {
            throw new InvalidDataException("Save fittings for the selected pilot only.");
        }
        if (
            fields.GetValueOrDefault("shipTypeID") is not long type
            || type is <= 0 or > int.MaxValue
            || fields.GetValueOrDefault("fitData") is not List<object?> entries
            || entries.Count > SavedFittingRules.MaximumItems
        )
        {
            throw new InvalidDataException("Invalid fitting hull or entry list.");
        }
        List<SavedFittingItem> items = [];
        foreach (var entry in entries)
        {
            if (
                entry is not object?[] { Length: 3 } tuple
                || tuple[0] is not long itemType
                || itemType is <= 0 or > int.MaxValue
                || tuple[1] is not long flag
                || flag is < 0 or > int.MaxValue
                || tuple[2] is not long quantity
                || quantity is <= 0 or > int.MaxValue
            )
            {
                throw new InvalidDataException("Expected fitting entries as (typeID, flag, quantity) tuples.");
            }
            items.Add(new((int)itemType, (InventoryFlag)flag, (int)quantity));
        }
        return new(
            BlueMarshal.Text(fields.GetValueOrDefault("name")),
            BlueMarshal.Text(fields.GetValueOrDefault("description")),
            (int)type,
            items
        );
    }
}

internal sealed record SavedFittingReply(SavedFitting Fitting) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["fittingID"] = Fitting.Id,
                ["ownerID"] = Fitting.OwnerId,
                ["name"] = new BlueUnicode(Fitting.Plan.Name),
                ["description"] = new BlueUnicode(Fitting.Plan.Description),
                ["shipTypeID"] = Fitting.Plan.ShipTypeId,
                ["fitData"] = new BlueList<SavedFittingItemReply>(Fitting.Plan.Items.Select(i => new SavedFittingItemReply(i)).ToArray()),
                ["savedDate"] = Fitting.SavedAt,
            }
        );
}

internal sealed record SavedFittingItemReply(SavedFittingItem Item) : BlueModel
{
    public override object ToBlue() => new object?[] { Item.TypeId, (int)Item.Flag, Item.Quantity };
}

internal sealed record FittingAddedNotification(long OwnerId, long FittingId) : PilotNotification("OnFittingAdded")
{
    public override object?[] Arguments => [OwnerId, FittingId];
}
