using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class MutaplasmidHandler(IMutaplasmids mutations) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "dynamicItemService")
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected mutation arguments.");
            }
            if (method == "GetDynamicItemInfo" && args is [long item])
            {
                return mutations.Read(item) is { } info ? new MutatedItemReply(info) : PilotCallResult.None;
            }
            if (method == "CreateDynamicItem" && args is [long mutator, long source])
            {
                var result = mutations.Create(context.Pilot.PilotId, mutator, source);
                foreach (var change in result.Changes)
                {
                    InventoryChangesWire.NotifyItemChange(context, change);
                }
                return result.Item.ItemId;
            }
            throw new InvalidDataException("Invalid mutation request.");
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record MutatedItemReply(MutatedItem Item) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["itemID"] = Item.ItemId,
                ["typeID"] = Item.TypeId,
                ["sourceTypeID"] = Item.SourceTypeId,
                ["sourceItemID"] = Item.SourceItemId,
                ["mutatorTypeID"] = Item.MutatorTypeId,
                ["mutatorItemID"] = Item.MutatorItemId,
                ["characterID"] = Item.CreatorId,
                ["attributes"] = Item.Attributes.ToDictionary(p => (long)p.Key, p => (object?)p.Value),
                ["modifiers"] = Item.Modifiers.ToDictionary(p => (long)p.Key, p => (object?)p.Value),
            }
        );
}
