using Eden.Protocol.Docking;

namespace Eden.Protocol.Reprocessing;

public sealed record RecoveredMaterial(int TypeId, int Quantity, long Unrecoverable);

public sealed record ReprocessingQuote(
    long ItemId,
    int TypeId,
    int QuantityToProcess,
    int LeftOvers,
    int Portions,
    double Efficiency,
    IReadOnlyList<RecoveredMaterial> Recoverables
);

public sealed record ReprocessingQuotes(double StationTax, double StationEfficiency, IReadOnlyList<ReprocessingQuote> Items);

public sealed record ReprocessingResult(
    IReadOnlyList<long> ProcessedIds,
    IReadOnlyDictionary<int, int> Output,
    IReadOnlyList<InventoryChange> Changes
);

public interface IPilotReprocessing
{
    void CheckStation(long pilotId, int stationId);
    ReprocessingQuotes Quote(long pilotId, int stationId, long activeShipId, IReadOnlyList<long> itemIds);
    ReprocessingResult Reprocess(
        long pilotId,
        int stationId,
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
