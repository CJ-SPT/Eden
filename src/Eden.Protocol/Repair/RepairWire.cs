using Eden.Protocol.Serialization;

namespace Eden.Protocol.Repair;

internal sealed record RepairQuoteReply(RepairQuote Quote) : IBlueWritable
{
    public object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["itemID"] = Quote.ItemId,
                ["typeID"] = Quote.TypeId,
                ["groupID"] = Quote.GroupId,
                ["damage"] = Quote.Damage,
                ["maxHealth"] = Quote.MaxHealth,
                ["costToRepairOneUnitOfDamage"] = (double)RepairDefinitions.IskPerDamagePoint,
            }
        );
}
