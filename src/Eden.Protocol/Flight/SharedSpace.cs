using System.Collections.Concurrent;
using Eden.Protocol.Docking;

namespace Eden.Protocol.Flight;

internal sealed record ShipContact(DockedPilot Pilot, SpacePosition Velocity, SpacePosition? Heading, double SpeedFraction, double? FollowRange);

[Injectable(InjectionType.Singleton)]
internal sealed class SharedSpace
{
    // Only immutable samples cross connection boundaries. Each pilot owns its simulation and socket.
    private readonly ConcurrentDictionary<long, ShipContact> _ships = new();

    public void Publish(ShipContact ship) => _ships[ship.Pilot.ShipId] = ship;
    public void Leave(long shipId) => _ships.TryRemove(shipId, out _);

    public ShipContact[] Peers(DockedPilot pilot) => _ships.Values
        .Where(ship => ship.Pilot.SystemId == pilot.SystemId && ship.Pilot.ShipId != pilot.ShipId)
        .OrderBy(ship => ship.Pilot.ShipId).ToArray();
}
