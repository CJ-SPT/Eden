using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Reprocessing;

public sealed record RecoveredMaterial(int TypeId, int Quantity, long Unrecoverable, long CostCents = 0);

public sealed record ReprocessingQuote(
    long ItemId,
    int TypeId,
    int QuantityToProcess,
    int LeftOvers,
    int Portions,
    double Efficiency,
    IReadOnlyList<RecoveredMaterial> Recoverables
)
{
    public long CostCents => Recoverables.Sum(m => m.CostCents);
}

public sealed record ReprocessingQuotes(double StationTax, double StationEfficiency, IReadOnlyList<ReprocessingQuote> Items);

public sealed record ReprocessingResult(
    IReadOnlyList<long> ProcessedIds,
    IReadOnlyDictionary<int, int> Output,
    IReadOnlyList<InventoryChange> Changes,
    long? BalanceCents = null
);

public interface IPilotReprocessing
{
    void CheckStation(long pilotId, long stationId);
    ReprocessingQuotes Quote(long pilotId, long stationId, long activeShipId, IReadOnlyList<long> itemIds);
    ReprocessingResult Reprocess(
        long pilotId,
        long stationId,
        IReadOnlyList<long> itemIds,
        long sourceId,
        long? ownerId,
        long outputLocationId,
        int outputFlagId
    );
}

public enum ReprocessingSkill
{
    Reprocessing = 3385,
    ReprocessingEfficiency = 3389,
}
