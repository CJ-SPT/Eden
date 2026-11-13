using Eden.Gameplay.Agents;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Registration;

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
    IPilotShipAppearance appearance,
    IPilotMissiles? missiles = null,
    IPilotTackle? tackle = null,
    IPilotDrones? drones = null,
    IPilotCrimewatch? crimewatch = null,
    IPilotElectronicWarfare? electronicWarfare = null,
    IPilotGateTravel? gates = null,
    IHumanPresence? presence = null,
    ICapsuleerPresentation? presentation = null,
    IPilotMissions? missions = null,
    IPilotCommandBursts? commandBursts = null,
    IDedComplexTravel? dedComplexes = null
)
{
    public FlightSession Create(DockedPilot pilot)
    {
        var selected = presence?.Select(pilot.PilotId, pilot.SystemId);
        try
        {
            presentation?.Refresh();
            crimewatch?.Connect(pilot.PilotId);
            return new FlightSession(
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
                missiles,
                tackle,
                appearance,
                drones,
                crimewatch,
                electronicWarfare,
                gates,
                humanPresenceLease: selected,
                presentation: presentation,
                missions: missions,
                commandBursts: commandBursts,
                dedComplexes: dedComplexes
            );
        }
        catch
        {
            try
            {
                selected?.Dispose();
            }
            finally
            {
                presentation?.Refresh();
            }
            throw;
        }
    }
}
