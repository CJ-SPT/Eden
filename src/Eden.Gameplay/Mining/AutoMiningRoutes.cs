using System.Collections.Immutable;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Mining;

public enum AutoMiningFleetMode
{
    Solo,
    Lead,
    Follow,
}

public sealed record AutoMiningRoute(ImmutableArray<int> Systems, AutoMiningFleetMode FleetMode)
{
    public const int MaximumSystems = 32;
    public const int MaximumTextLength = 2048;
    public static AutoMiningRoute Empty { get; } = new([], AutoMiningFleetMode.Solo);
    public bool Travels => Systems.Length > 0 || FleetMode == AutoMiningFleetMode.Follow;
}

public sealed record AutoMiningDestination(int SystemId, long Id, SpacePosition Position, string Name);

public sealed record AutoMiningGate(int SystemId, long Id, int DestinationSystemId, long ExitId);

public interface IPilotAutoMiningRoutes
{
    AutoMiningRoute ReadRoute(long pilotId);
    AutoMiningRoute SaveRoute(long pilotId, int origin, string systems, AutoMiningFleetMode mode);
    string Describe(AutoMiningRoute route);
    IReadOnlyList<AutoMiningGate> Path(int origin, int destination);
    IReadOnlyList<AutoMiningDestination> Belts(int systemId);
    AutoMiningDestination Station(long stationId);
}

public interface IAutoMiningFleets
{
    long RequireLeader(long pilotId);
    void Publish(long pilotId, AutoMiningDestination destination);
    AutoMiningDestination Follow(long pilotId, long leaderId);
    void Stop(long pilotId);
}

public static class AutoMiningRouteRules
{
    public static IReadOnlyList<AutoMiningGate> Path(int origin, int destination, IEnumerable<AutoMiningGate> gates)
    {
        if (origin == destination)
        {
            return [];
        }
        var links = gates.OrderBy(gate => gate.Id).ToLookup(gate => gate.SystemId);
        Dictionary<int, AutoMiningGate> previous = [];
        HashSet<int> visited = [origin];
        Queue<int> pending = new([origin]);
        while (pending.TryDequeue(out int system))
        {
            foreach (var gate in links[system])
            {
                if (!visited.Add(gate.DestinationSystemId))
                {
                    continue;
                }
                previous.Add(gate.DestinationSystemId, gate);
                if (gate.DestinationSystemId == destination)
                {
                    List<AutoMiningGate> path = [];
                    for (int next = destination; next != origin; next = previous[next].SystemId)
                    {
                        path.Add(previous[next]);
                    }
                    path.Reverse();
                    return path.AsReadOnly();
                }
                pending.Enqueue(gate.DestinationSystemId);
            }
        }
        throw new InvalidDataException("No connected stargate route exists between the selected systems.");
    }
}
