using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Fittings;

internal static class ApplyFittingWire
{
    internal static ApplyFittingRequest Read(object?[] args)
    {
        // Build 3662168 fittingSvc.LoadFitting calls the bound ship inventory.
        if (
            args is not [long ship, long hullType, var sources, long location, var plan, var cargo, bool fitRigs]
            || hullType is <= 0 or > int.MaxValue
            || plan is not BlueInstance { Name: "utillib.KeyVal", State: Dictionary<string, object?> fields }
        )
        {
            throw new InvalidDataException("Invalid ship fitting request.");
        }

        Dictionary<int, IReadOnlyList<long>> itemIds = [];
        foreach (var (type, value) in Map(sources))
        {
            if (value is not HashSet<long> ids || ids.Any(id => id <= 0))
            {
                throw new InvalidDataException("Choose a set of hangar item IDs for each fitting type.");
            }
            itemIds.Add(PositiveInt(type), ids.Order().ToArray());
        }

        var slots = Map(fields.GetValueOrDefault("modulesByFlag"))
            .Select(pair => new FittingSlot(PositiveInt(pair.Key), PositiveInt(pair.Value)))
            .OrderBy(slot => slot.FlagId)
            .ToArray();
        var supplies = Quantities(cargo, InventoryFlag.Cargo)
            .Concat(Quantities(fields.GetValueOrDefault("chargesByType"), InventoryFlag.Cargo))
            .Concat(Quantities(fields.GetValueOrDefault("iceByType"), InventoryFlag.Cargo))
            .Concat(Quantities(fields.GetValueOrDefault("implantsByTypeID"), InventoryFlag.Cargo))
            .Concat(Quantities(fields.GetValueOrDefault("dronesByType"), InventoryFlag.DroneBay))
            .Concat(Quantities(fields.GetValueOrDefault("fightersByTypeID"), InventoryFlag.FighterBay))
            .ToArray();
        if (slots.Length + supplies.Length > SavedFittingRules.MaximumItems)
        {
            throw new InvalidDataException("The fitting has too many entries.");
        }

        return new(ship, (int)hullType, location, itemIds, slots, supplies, fitRigs);
    }

    private static IEnumerable<SavedFittingItem> Quantities(object? value, InventoryFlag flag) =>
        Map(value).Select(pair => new SavedFittingItem(PositiveInt(pair.Key), flag, PositiveInt(pair.Value)));

    private static IReadOnlyDictionary<long, object?> Map(object? value) =>
        value switch
        {
            Dictionary<long, object?> indexed when indexed.Count <= SavedFittingRules.MaximumItems => indexed,
            Dictionary<string, object?> { Count: 0 } => new Dictionary<long, object?>(),
            _ => throw new InvalidDataException("Expected a fitting dictionary with integer keys."),
        };

    private static int PositiveInt(object? value) =>
        value is long number && number is > 0 and <= int.MaxValue
            ? (int)number
            : throw new InvalidDataException("Expected a positive fitting ID, flag or quantity.");
}

internal sealed record MissingFittingItemReply(MissingFittingItem Item) : BlueModel
{
    public override object ToBlue() => new object?[] { Item.TypeId, Item.Quantity };
}
