using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Docking;

public sealed record StationDirectoryEntry(
    int Id,
    int TypeId,
    string Name,
    int SystemId,
    int ConstellationId,
    int RegionId,
    int OwnerId,
    int OperationId,
    int OrbitId,
    SpacePosition Position,
    IReadOnlyList<int> Services
);

public sealed record StationDirectorySnapshot(IReadOnlyList<StationDirectoryEntry> Stations, IReadOnlyList<int> ServiceIds);

public interface IStationDirectory
{
    StationDirectorySnapshot Read();
}
