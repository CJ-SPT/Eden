using Eden.Gameplay.Reprocessing;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Reprocessing;

internal sealed record ReprocessingQuoteReply(ReprocessingQuotes Quote) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Quote.StationTax,
            new BlueDictionary([(null, Quote.StationEfficiency)]),
            new BlueMap<long, ReprocessingItemReply>(
                Quote.Items.ToDictionary(item => item.ItemId, item => new ReprocessingItemReply(item))
            ),
        };
}

internal sealed record ReprocessingItemReply(ReprocessingQuote Quote) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["itemID"] = Quote.ItemId,
                ["typeID"] = Quote.TypeId,
                ["quantityToProcess"] = Quote.QuantityToProcess,
                ["leftOvers"] = Quote.LeftOvers,
                ["portions"] = Quote.Portions,
                ["efficiency"] = Quote.Efficiency,
                ["totalISKCost"] = Quote.CostCents / 100d,
                ["recoverables"] = new BlueList<RecoveredMaterialReply>(
                    Quote.Recoverables.Select(item => new RecoveredMaterialReply(item)).ToArray()
                ),
            }
        );
}

internal sealed record RecoveredMaterialReply(RecoveredMaterial Material) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["typeID"] = Material.TypeId,
                ["client"] = Material.Quantity,
                ["unrecoverable"] = Material.Unrecoverable,
                ["iskCost"] = Material.CostCents / 100d,
            }
        );
}

internal sealed record ReprocessingCompleted(ReprocessingResult Result) : BlueModel
{
    public override object ToBlue() => new object?[] { new BlueList<long>(Result.ProcessedIds), new BlueMap<int, int>(Result.Output) };
}
