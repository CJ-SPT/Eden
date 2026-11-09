namespace Eden.Gameplay.Capsuleers;

public enum TrafficJourneyKind
{
    Local,
    Regional,
    LongDistance,
}

public sealed record TrafficJourney(
    TrafficJourneyKind Kind,
    int HomeStationId,
    int SourceStationId,
    int DestinationStationId,
    int[] Systems,
    long Sequence
);

public static class TrafficJourneyRules
{
    public const int MaximumJumps = 20;
    public const int GateVisitorCount = 4;
    public const int HomeRosterSlots = 6;

    // Build 3662168 gate IDs fit below this stride, keeping each visitor reservation disjoint.
    public const int VisitorSlotStride = 100_000_000;
    public const int RegionalMinimumJumps = 3;
    public const int LongDistanceMinimumJumps = 8;
    private const int LocalLayoverSeconds = 120;
    private const int VisitorLayoverSeconds = 600;

    public static TrafficJourneyKind Kind(int slot, bool busy) =>
        (slot % 3) switch
        {
            0 => TrafficJourneyKind.Local,
            1 => busy ? TrafficJourneyKind.Regional : TrafficJourneyKind.Local,
            _ => busy ? TrafficJourneyKind.LongDistance : TrafficJourneyKind.Regional,
        };

    public static int MaximumDistance(TrafficJourneyKind kind) =>
        kind switch
        {
            TrafficJourneyKind.Local => 2,
            TrafficJourneyKind.Regional => LongDistanceMinimumJumps - 1,
            _ => MaximumJumps,
        };

    public static int MinimumDistance(TrafficJourneyKind kind) =>
        kind switch
        {
            TrafficJourneyKind.Local => 0,
            TrafficJourneyKind.Regional => RegionalMinimumJumps,
            _ => LongDistanceMinimumJumps,
        };

    public static int LayoverSeconds(TrafficJourneyKind kind, long pilotId, long sequence) =>
        (kind == TrafficJourneyKind.Local ? LocalLayoverSeconds : VisitorLayoverSeconds)
        + HaulingRules.StationDwellSeconds(pilotId, sequence);
}
