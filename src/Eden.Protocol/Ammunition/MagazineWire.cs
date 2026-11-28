using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Ammunition;

[Injectable(InjectionType.Scoped)]
internal sealed class MagazineWire(
    TimeProvider clock,
    IPilotFitting fitting,
    IPilotCombat combat,
    IPilotInventory inventory,
    IPilotAmmunition? ammunition = null,
    IPilotMining? mining = null,
    Eden.Gameplay.Structures.IPilotStructureAmmunition? structureAmmunition = null
)
{
    private sealed record BoosterReload(long PilotId, Magazine Current, Magazine? Visible);

    private readonly Dictionary<long, BoosterReload> _boosterReloads = [];

    internal bool IsMonitoring => _boosterReloads.Count > 0;

    internal static DogmaItemKey.Charge Key(Magazine magazine) => new(magazine.ShipId, magazine.FlagId, magazine.TypeId);

    internal DogmaItemInfo<double> Prime(long pilotId, Magazine magazine)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var attributes = (
            structureAmmunition?.Handles(magazine.ShipId) == true
                ? (structureAmmunition ?? throw new InvalidDataException("Structure ammunition is unavailable.")).ChargeAttributes(
                    pilotId,
                    magazine.ShipId,
                    magazine.ModuleId
                )
                : ammunition?.ChargeAttributes(pilotId, magazine.ShipId, magazine.ModuleId) ?? fitting.Attributes(magazine.TypeId)
        ).ToDictionary(a => a.Key, a => a.Value);
        attributes[DogmaAttributeId.Quantity] = magazine.Quantity;
        attributes[DogmaAttributeId.Damage] = magazine.Damage;
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

    internal static void NotifyState(PilotCallContext context, Magazine magazine, long now)
    {
        // Quantity zero removes the native sublocation. Deliver final crystal
        // damage before that removal so no update targets an unloaded charge.
        if (magazine.Quantity > 0)
        {
            NotifyQuantity(context, magazine, magazine.Quantity, now);
        }
        context.Notifications.Add(
            new ModuleAttributesChanged([new(context.Pilot.PilotId, Key(magazine), DogmaAttributeId.Damage, now, magazine.Damage)])
        );
        if (magazine.Quantity == 0)
        {
            NotifyQuantity(context, magazine, 0, now);
        }
    }

    internal void NotifyChanges(PilotCallContext context, IReadOnlyList<MagazineChange> changes, double reload = 0)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        foreach (var change in changes)
        {
            var magazine = change.Current ?? change.Previous!;
            bool structure = structureAmmunition?.Handles(magazine.ShipId) == true;
            var module = structure
                ? null
                : inventory
                    .List(context.Pilot.PilotId, context.Pilot.ShipId)
                    .SingleOrDefault(i => i.Id == magazine.ModuleId && InventoryDefinitions.IsFittedSlot(i.FlagId));
            if (
                reload > 0
                && context.Pilot.Position is not null
                && change.Current is { } loaded
                && loaded.ShipId == context.Pilot.ShipId
                && loaded.ReadyAt > now
                && module?.GroupId == CapacitorBoosterDefinitions.Group
            )
            {
                BeginBoosterReload(context, change, loaded, now);
                continue;
            }
            if (change.Previous is { } old && (change.Current is null || old.TypeId != change.Current.TypeId))
            {
                NotifyQuantity(context, old, 0, now);
            }
            if (change.Current is { } current)
            {
                NotifyLoaded(context, current, change.Previous, now);
                if (reload > 0)
                {
                    context.Notifications.Add(new ChargeLoading([current.ModuleId], current.TypeId, reload));
                }
            }
            if (module is null)
            {
                continue;
            }
            var attributes =
                InventoryDefinitions.IsOreMiningGroup(module.GroupId)
                    ? mining?.ModuleAttributes(context.Pilot.PilotId, module) ?? fitting.ModuleAttributes(context.Pilot.PilotId, module)
                : InventoryDefinitions.IsHighSlot(module.FlagId) && !MissileDefinitions.LauncherGroups.Contains(module.GroupId)
                    ? combat.ModuleAttributes(context.Pilot.PilotId, module)
                : fitting.ModuleAttributes(context.Pilot.PilotId, module);
            var values = new[]
            {
                DogmaAttributeId.MaxRange,
                DogmaAttributeId.AccuracyFalloff,
                DogmaAttributeId.TrackingSpeed,
                DogmaAttributeId.CapacitorNeed,
                DogmaAttributeId.NextActivationTime,
            }
                .Concat(
                    InventoryDefinitions.IsOreMiningGroup(module.GroupId)
                        ?
                        [
                            DogmaAttributeId.MiningAmount,
                            DogmaAttributeId.Duration,
                            DogmaAttributeId.MiningResidueChance,
                            DogmaAttributeId.MiningResidueMultiplier,
                        ]
                        : Array.Empty<DogmaAttributeId>()
                )
                .Concat(SupportModuleDefinitions.Effect(module.GroupId) is not null ? SupportModuleDefinitions.BonusAttributes : [])
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

    private void NotifyLoaded(PilotCallContext context, Magazine current, Magazine? previous, long now)
    {
        if (previous?.TypeId != current.TypeId)
        {
            context.Notifications.Add(new GodmaItemPrimed(current.ShipId, Prime(context.Pilot.PilotId, current)));
        }
        // The tuple sublocation attaches only when Godma receives attributeQuantity (build 3662168).
        NotifyState(context, current, now);
    }

    private void BeginBoosterReload(PilotCallContext context, MagazineChange change, Magazine loaded, long now)
    {
        var visible = change.Previous;
        if (visible is not null && visible.TypeId != loaded.TypeId)
        {
            NotifyQuantity(context, visible, 0, now);
            visible = null;
        }
        _boosterReloads[loaded.ModuleId] = new(context.Pilot.PilotId, loaded, visible);
        double duration = (loaded.ReadyAt - now) / (double)TimeSpan.TicksPerMillisecond;
        context.Notifications.Add(
            new ModuleAttributesChanged([
                new(context.Pilot.PilotId, new DogmaItemKey.Item(loaded.ModuleId), DogmaAttributeId.ReloadTime, now, duration),
                new(
                    context.Pilot.PilotId,
                    new DogmaItemKey.Item(loaded.ModuleId),
                    DogmaAttributeId.NextActivationTime,
                    now,
                    loaded.ReadyAt
                ),
            ])
        );
        context.Notifications.Add(new ChargeLoading([loaded.ModuleId], loaded.TypeId, duration));
    }

    internal IReadOnlyList<Magazine> Visible(
        DockedPilot pilot,
        IReadOnlyList<Magazine> magazines,
        IReadOnlyList<InventoryItem>? modules = null
    )
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (pilot.Position is null || magazines.All(magazine => magazine.ReadyAt <= now))
        {
            return magazines;
        }
        modules ??= inventory.List(pilot.PilotId, pilot.ShipId);
        List<Magazine> visible = [];
        foreach (var magazine in magazines)
        {
            if (
                magazine.ReadyAt <= now
                || !modules.Any(module => module.Id == magazine.ModuleId && module.GroupId == CapacitorBoosterDefinitions.Group)
            )
            {
                visible.Add(magazine);
                continue;
            }
            if (
                !_boosterReloads.TryGetValue(magazine.ModuleId, out var reload)
                || reload.PilotId != pilot.PilotId
                || reload.Current != magazine
            )
            {
                _boosterReloads[magazine.ModuleId] = reload = new(pilot.PilotId, magazine, null);
            }
            if (reload.Visible is { } prior)
            {
                visible.Add(prior);
            }
        }
        return visible;
    }

    internal void Advance(PilotCallContext context)
    {
        if (!IsMonitoring || !context.Selected)
        {
            return;
        }
        long pilot = context.Pilot.PilotId;
        long ship = context.Pilot.ShipId;
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        IReadOnlyList<Magazine>? current = null;
        foreach (var reload in _boosterReloads.Values.ToArray())
        {
            var magazine = reload.Current;
            if (reload.PilotId != pilot || magazine.ShipId != ship)
            {
                _boosterReloads.Remove(magazine.ModuleId);
            }
            else if (magazine.ReadyAt <= now)
            {
                _boosterReloads.Remove(magazine.ModuleId);
                current ??= ammunition?.Read(pilot, ship) ?? [magazine];
                if (current.Contains(magazine))
                {
                    NotifyLoaded(context, magazine, reload.Visible, now);
                    context.Notifications.Add(
                        new ModuleAttributesChanged([
                            new(pilot, new DogmaItemKey.Item(magazine.ModuleId), DogmaAttributeId.NextActivationTime, now, 0),
                        ])
                    );
                }
            }
        }
    }
}
