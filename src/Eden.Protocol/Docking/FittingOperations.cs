using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Combat;
using Eden.Protocol.Loot;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class FittingOperations(TimeProvider clock, IPilotFitting fitting, IPilotInventory inventory)
{
    internal void NotifyShipAttributes(PilotCallContext context)
    {
        context.Flight.RefreshModules(inventory.List(context.Pilot.PilotId, context.Pilot.ShipId));
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var attributes = fitting.ShipAttributes(context.Pilot.PilotId);
        var changes = new[] { DogmaAttributeId.CpuLoad, DogmaAttributeId.PowerLoad, DogmaAttributeId.TurretSlotsLeft }
            .Select(attribute => new ModuleAttributeChange(
                context.Pilot.PilotId,
                new DogmaItemKey.Item(context.Pilot.ShipId),
                attribute,
                now,
                attributes[attribute]
            ))
            .ToArray();
        context.Notifications.Add(new ModuleAttributesChanged(changes));
    }

    internal PilotCallResult SetOnline(PilotCallContext context, long ship, long module, bool online)
    {
        try
        {
            _ = fitting.SetOnline(context.Pilot.PilotId, ship, module, online);
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            context.Notifications.Add(
                new ShipEffectChanged(new(module, context.Pilot.PilotId, ship, null, DogmaEffect.Online, now, null, 0), now, online, online)
            );
            NotifyShipAttributes(context);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
