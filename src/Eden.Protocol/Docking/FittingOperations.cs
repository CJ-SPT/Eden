using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;
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
internal sealed class FittingOperations(
    TimeProvider clock,
    IPilotFitting fitting,
    IPilotInventory inventory,
    IPilotShipResources resources,
    ShipDogmaBuilder dogma
)
{
    internal void NotifyShipAttributes(PilotCallContext context)
    {
        var modules = inventory.List(context.Pilot.PilotId, context.Pilot.ShipId);
        context.Flight.RefreshModules(modules);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var snapshot = resources.Read(context.Pilot.PilotId, context.Pilot.ShipId, now);
        var attributes = snapshot.Attributes.ToDictionary();
        context.Flight.RefreshFitting(attributes);
        attributes[DogmaAttributeId.CapacitorCharge] = snapshot.Capacitor;
        attributes[DogmaAttributeId.ShieldCharge] = snapshot.Health.Remaining.Shield;
        attributes[DogmaAttributeId.ArmorDamage] = snapshot.Health.Defense.Armor - snapshot.Health.Remaining.Armor;
        attributes[DogmaAttributeId.Damage] = snapshot.Health.Defense.Structure - snapshot.Health.Remaining.Structure;
        var changes = attributes
            .Select(a => new ModuleAttributeChange(context.Pilot.PilotId, new DogmaItemKey.Item(context.Pilot.ShipId), a.Key, now, a.Value))
            .ToList();
        // Passive equipment changes existing modules as well as the hull. The
        // native client keeps its primed values until it receives these updates.
        foreach (var module in modules.Where(m => InventoryDefinitions.IsFittedSlot(m.FlagId)))
        {
            var info = dogma.PrimeModule(context.Pilot, module);
            changes.AddRange(info.Attributes.Select(a => new ModuleAttributeChange(context.Pilot.PilotId, info.Key, a.Key, now, a.Value)));
        }
        context.Notifications.Add(new ModuleAttributesChanged(changes));
        context.Flight.NotifyHealth(snapshot.Health);
    }

    internal void NotifyOnlineChanges(PilotCallContext context, IReadOnlyList<InventoryChange> changes)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        foreach (var change in changes.Where(c => c.Item.Online != (c.Previous?.Online ?? false)))
        {
            context.Notifications.Add(
                new ShipEffectChanged(
                    new(change.Item.Id, context.Pilot.PilotId, context.Pilot.ShipId, null, DogmaEffect.Online, now, null, 0),
                    now,
                    change.Item.Online,
                    change.Item.Online
                )
            );
        }
    }

    internal PilotCallResult SetOnline(PilotCallContext context, long ship, long module, bool online)
    {
        try
        {
            var changes = fitting.SetOnline(context.Pilot.PilotId, ship, module, online);
            NotifyOnlineChanges(context, changes);
            NotifyShipAttributes(context);
            return PilotCallResult.None;
        }
        catch (CargoCapacityException error)
        {
            return new PilotCallResult.NamedFailure("NotEnoughCargoSpace", new CargoSpaceErrorValues(error.Available, error.Required));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
