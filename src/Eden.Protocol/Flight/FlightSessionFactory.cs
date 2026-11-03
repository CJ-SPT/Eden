using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Industry;
using Eden.Protocol.Loot;
using Eden.Protocol.Mining;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Targeting;
using static Eden.Protocol.PilotSessions.PilotSessionWire;

namespace Eden.Protocol.Flight;

[Injectable]
internal sealed class FlightSessionFactory(
    IShipPositionStore positions,
    TimeProvider clock,
    SharedSpace space,
    ISystemObjects objects,
    IPilotInventory inventory,
    IStationDocking docking,
    IPilotHealth health,
    INpcEncounters encounters,
    IWrecks wrecks
)
{
    public FlightSession Create(DockedPilot pilot) =>
        new FlightSession(
            positions,
            pilot,
            clock,
            space,
            objects.Load(pilot.SystemId),
            inventory
                .List(pilot.PilotId, pilot.ShipId)
                .Where(i => InventoryDefinitions.IsHighSlot(i.FlagId))
                .Select(i => new FittedSpaceModule(i.Id, i.TypeId, i.FlagId))
                .ToArray(),
            docking,
            health,
            encounters,
            wrecks
        );
}
