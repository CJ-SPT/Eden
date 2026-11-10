using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Combat;
using Eden.Protocol.Drones;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class FittingOperations(
    TimeProvider clock,
    IPilotFitting fitting,
    IPilotInventory inventory,
    IPilotShipResources resources,
    ShipDogmaBuilder dogma,
    IPilotWeaponBanks banks,
    DroneAttributeUpdates droneUpdates,
    Eden.Gameplay.Structures.IPilotStructures structures
)
{
    internal IReadOnlyList<WeaponBank> ReadBanks(PilotCallContext context) => banks.Read(context.Pilot.PilotId, context.Pilot.ShipId);

    internal void NotifyShipAttributes(PilotCallContext context, IReadOnlyList<WeaponBank>? previousBanks = null)
    {
        if (previousBanks is not null)
        {
            var current = ReadBanks(context);
            if (
                previousBanks.Count != current.Count
                || previousBanks.Any(b => !current.Any(c => c.MasterId == b.MasterId && c.SlaveIds.SequenceEqual(b.SlaveIds)))
            )
            {
                context.Notifications.Add(new WeaponBanksChanged(context.Pilot.ShipId, current));
            }
        }
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
        changes.AddRange(droneUpdates.Read(context, includeControl: true, modules));
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
            if (
                ship != context.Pilot.ShipId
                && ship >= Eden.Gameplay.Structures.StructureRules.FirstStructureId
                && structures.Read(context.Pilot.PilotId, ship) is not null
            )
            {
                Eden.Protocol.Structures.StructureHandler.Notify(
                    context,
                    structures.SetServiceOnline(context.Pilot.PilotId, ship, module, online)
                );
                return PilotCallResult.None;
            }
            var previousBanks = ReadBanks(context);
            var changes = fitting.SetOnline(context.Pilot.PilotId, ship, module, online);
            NotifyOnlineChanges(context, changes);
            NotifyShipAttributes(context, previousBanks);
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
