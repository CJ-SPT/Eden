using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Fittings;

internal static class SavedFittingWire
{
    internal static IReadOnlyList<FittingImport> ReadMany(object? value, long owner)
    {
        if (value is Dictionary<string, object?> { Count: 0 })
        {
            return [];
        }
        if (value is not Dictionary<long, object?> plans || plans.Count > SavedFittingRules.MaximumCorporationFittings)
        {
            throw new InvalidDataException("Expected a fitting dictionary indexed by temporary IDs.");
        }
        SavedFittingRules.CheckBatch(plans.Keys.ToArray(), true);
        return plans.Select(pair => new FittingImport(pair.Key, Read(pair.Value, owner))).ToArray();
    }

    internal static IReadOnlyList<long> ReadIds(object? value)
    {
        if (value is not List<object?> ids || ids.Count > SavedFittingRules.MaximumCorporationFittings || ids.Any(id => id is not long))
        {
            throw new InvalidDataException("Expected a list of fitting IDs.");
        }
        var result = ids.Cast<long>().ToArray();
        SavedFittingRules.CheckBatch(result, false);
        return result;
    }

    internal static FittingPlan Read(object? value, long owner)
    {
        if (value is not BlueInstance { Name: "utillib.KeyVal", State: Dictionary<string, object?> fields })
        {
            throw new InvalidDataException("Expected native saved fitting data.");
        }
        if (fields.TryGetValue("ownerID", out var suppliedOwner) && !Equals(suppliedOwner, owner))
        {
            throw new InvalidDataException("Save fittings for the requested owner only.");
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

internal sealed record FittingImportReply(FittingImportResult Result) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?> { ["tempFittingID"] = Result.TemporaryId, ["realFittingID"] = Result.FittingId }
        );
}

internal sealed record FittingsUpdatedNotification(long OwnerId, IReadOnlyList<long> FittingIds) : PilotNotification("OnFittingAdded")
{
    // Native list handling invalidates existing entries; an integer only invalidates a new entry.
    public override object?[] Arguments => [OwnerId, new BlueList<long>(FittingIds)];
}

internal sealed record FittingDeletedNotification(long OwnerId, long FittingId) : PilotNotification("OnFittingDeleted")
{
    public override object?[] Arguments => [OwnerId, FittingId];
}

internal sealed record FittingsDeletedNotification(long OwnerId, IReadOnlyList<long> FittingIds)
    : PilotNotification("OnManyFittingsDeleted")
{
    public override object?[] Arguments => [OwnerId, new BlueList<long>(FittingIds)];
}
