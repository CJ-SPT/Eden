using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Ammunition;

[Injectable]
internal sealed class MagazineWire(TimeProvider clock, IPilotFitting fitting, IPilotCombat combat, IPilotInventory inventory)
{
    internal static DogmaItemKey.Charge Key(Magazine magazine) => new(magazine.ShipId, magazine.FlagId, magazine.TypeId);

    internal DogmaItemInfo<double> Prime(Magazine magazine)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var attributes = fitting.Attributes(magazine.TypeId).ToDictionary(a => a.Key, a => a.Value);
        attributes[DogmaAttributeId.Quantity] = magazine.Quantity;
        return new(
            new DogmaItemKey.Charge(magazine.ShipId, magazine.FlagId, magazine.TypeId),
            null,
            attributes,
            new Dictionary<DogmaEffect, DogmaActiveEffect>(),
            now
        );
    }

    internal static void NotifyQuantity(PilotCallContext context, Magazine magazine, int quantity, long now) =>
        context.Notifications.Add(
            new ModuleAttributesChanged([
                new(
                    context.Pilot.PilotId,
                    new DogmaItemKey.Charge(magazine.ShipId, magazine.FlagId, magazine.TypeId),
                    DogmaAttributeId.Quantity,
                    now,
                    quantity
                ),
            ])
        );

    internal void NotifyChanges(PilotCallContext context, IReadOnlyList<MagazineChange> changes, double reload = 0)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        foreach (var change in changes)
        {
            if (change.Previous is { } old && (change.Current is null || old.TypeId != change.Current.TypeId))
            {
                NotifyQuantity(context, old, 0, now);
            }
            if (change.Current is { } current)
            {
                if (change.Previous?.TypeId != current.TypeId)
                {
                    context.Notifications.Add(new GodmaItemPrimed(current.ShipId, Prime(current)));
                }
                // Godma primes its row, but clientDogmaLocation fits a tuple
                // sublocation only on attributeQuantity (build 3662168).
                NotifyQuantity(context, current, current.Quantity, now);
                if (reload > 0)
                {
                    context.Notifications.Add(new ChargeLoading([current.ModuleId], current.TypeId, reload));
                }
            }
            var magazine = change.Current ?? change.Previous!;
            var module = inventory
                .List(context.Pilot.PilotId, context.Pilot.ShipId)
                .SingleOrDefault(i => i.Id == magazine.ModuleId && InventoryDefinitions.IsHighSlot(i.FlagId));
            if (module is null)
            {
                continue;
            }
            var attributes = combat.ModuleAttributes(context.Pilot.PilotId, module);
            var values = new[] { DogmaAttributeId.MaxRange, DogmaAttributeId.CapacitorNeed, DogmaAttributeId.NextActivationTime }
                .Select(attribute => new ModuleAttributeChange(
                    context.Pilot.PilotId,
                    new DogmaItemKey.Item(magazine.ModuleId),
                    attribute,
                    now,
                    attributes.GetValueOrDefault(attribute)
                ))
                .ToArray();
            context.Notifications.Add(new ModuleAttributesChanged(values));
        }
    }
}
