using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Drones;

[Injectable]
internal sealed class DroneAttributeUpdates(IPilotDrones drones, IPilotInventory inventory, IPilotFitting fitting, TimeProvider clock)
{
    internal IReadOnlyList<ModuleAttributeChange> Read(
        PilotCallContext context,
        bool includeControl,
        IReadOnlyList<InventoryItem>? items = null
    )
    {
        long pilot = context.Pilot.PilotId;
        long ship = context.Pilot.ShipId;
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        List<ModuleAttributeChange> changes = [];
        if (includeControl)
        {
            changes.AddRange(DroneWire.ControlChanges(context, drones.Control(pilot), now));
        }
        Dictionary<int, IReadOnlyDictionary<DogmaAttributeId, double>?> attributes = [];
        foreach (
            var item in (items ?? inventory.List(pilot, ship)).Where(item =>
                item.FlagId == (int)InventoryFlag.DroneBay && item.CategoryId == (int)InventoryCategory.Drone
            )
        )
        {
            if (!attributes.TryGetValue(item.TypeId, out var values))
            {
                if (drones.Profile(pilot, item.TypeId) is not { } profile)
                {
                    attributes[item.TypeId] = null;
                    continue;
                }
                values = DroneWire.Attributes(profile, fitting.Attributes(item.TypeId));
                attributes[item.TypeId] = values;
            }
            if (values is not null)
            {
                Add(item.Id, values);
            }
        }
        if (context.Pilot.Position is not null)
        {
            foreach (var contact in drones.List(context.Pilot.SystemId, now).Where(drone => drone.PilotId == pilot && drone.ShipId == ship))
            {
                if (!attributes.TryGetValue(contact.Profile.TypeId, out var values) || values is null)
                {
                    values = DroneWire.Attributes(contact.Profile, fitting.Attributes(contact.Profile.TypeId));
                    attributes[contact.Profile.TypeId] = values;
                }
                Add(contact.Id, values);
            }
            context.Flight.RefreshDrones();
        }
        return changes;

        void Add(long item, IReadOnlyDictionary<DogmaAttributeId, double> values) =>
            changes.AddRange(
                values.Select(value => new ModuleAttributeChange(pilot, new DogmaItemKey.Item(item), value.Key, now, value.Value))
            );
    }
}
