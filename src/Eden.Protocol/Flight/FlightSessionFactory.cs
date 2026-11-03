using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Protocol.Combat;
using Eden.Protocol.Loot;

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
    IWrecks wrecks,
    IPilotMissiles? missiles = null
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
            wrecks,
            missiles
        );
}
