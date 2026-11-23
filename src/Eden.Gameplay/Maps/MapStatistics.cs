namespace Eden.Gameplay.Maps;

public enum MapHistoryStatistic
{
    Jumps,
    Kills,
}

public enum MapActivityKind
{
    Jump,
    ShipLoss,
    NpcShipLoss,
    CapsuleLoss,
}

public readonly record struct MapPilotCounts(int InSpace, int Docked)
{
    public MapPilotCounts Add(MapPilotCounts other) => new(InSpace + other.InSpace, Docked + other.Docked);
}

public readonly record struct MapActivityCounts(int Jumps, int Ships, int NpcShips, int Capsules)
{
    public MapActivityCounts Record(MapActivityKind kind) =>
        kind switch
        {
            MapActivityKind.Jump => this with { Jumps = Jumps + 1 },
            MapActivityKind.ShipLoss => this with { Ships = Ships + 1 },
            MapActivityKind.NpcShipLoss => this with { NpcShips = NpcShips + 1 },
            MapActivityKind.CapsuleLoss => this with { Capsules = Capsules + 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}

public readonly record struct MapHistoryRow(int SystemId, MapActivityCounts Counts);

public readonly record struct MapSystemVisit(int SystemId, int Visits, long LastVisitedAt);

public sealed record MapHistorySnapshot(long UpdatedAt, IReadOnlyList<MapHistoryRow> Rows)
{
    public static MapHistorySnapshot Empty { get; } = new(1, []);
}

public sealed record MapPilotPopulation(int Samples, IReadOnlyDictionary<int, MapPilotCounts> Systems)
{
    public static MapPilotPopulation Empty { get; } = new(1, new Dictionary<int, MapPilotCounts>());
}

public interface IMapStatistics
{
    MapHistorySnapshot History(MapHistoryStatistic statistic, int hours);
    MapPilotPopulation Population();
    IReadOnlyList<MapSystemVisit> SystemVisits(long characterId);
}
