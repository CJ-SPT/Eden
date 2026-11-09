using Eden.World.Capsuleers;

namespace Eden.World.Runtime;

internal sealed partial class LiveReadModels
{
    private readonly ResidentSnapshot<long, long> _gateWatcherShips = new("resident.gateWatcherShip");
    private readonly ResidentSnapshot<long, GateWatcherPosition?> _gateWatcherPositions = new("resident.gateWatcherPosition");

    internal long GateWatcherShip(long pilotId, Func<long> load) => Read(_gateWatcherShips, pilotId, load);

    internal GateWatcherPosition? GateWatcherPosition(long shipId, Func<GateWatcherPosition?> load) =>
        Read(_gateWatcherPositions, shipId, load);

    internal void GateWatcherPositionChanged(long shipId) => _gateWatcherPositions.Remove(shipId);
}
