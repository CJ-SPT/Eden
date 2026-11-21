using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;

namespace Eden.Protocol.Flight;

internal sealed partial class FlightSession
{
    internal void StartJumpBridge(IPilotJumpBridges bridges, long source, ISystemObjects systems)
    {
        Checkpoint();
        if (!CanUseSelfModules || InWarp || PreparingWarp || MicroJumpSpooling || Cloaked)
        {
            throw new InvalidDataException("Wait until your ship can use the Ansiblex jump bridge.");
        }
        var destination = bridges.CheckJump(Pilot.PilotId, Pilot.ShipId, source);
        CancelGateCloak();
        PilotNavigationRevision++;
        _travel.BeginGate(
            new(source, destination.Exit, destination.System, systems.Load(destination.System.Id), Bridge: bridges),
            Stamp,
            clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
        );
        missiles?.Cancel(Pilot.PilotId, Pilot.ShipId);
        drones?.Release(Pilot.PilotId, Pilot.ShipId);
        tackle?.Cancel(Pilot.PilotId, Pilot.ShipId);
        electronicWarfare?.Cancel(Pilot.PilotId, Pilot.ShipId, incoming: true);
        SetAtRest();
        Update(new DestinyCommand.Stop(Pilot.ShipId));
        DepartureEffect(Pilot.ShipId, source, destination.System.Id);
        space.Publish(CreateContact());
    }
}
