using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Combat;

[Injectable(InjectionType.Scoped)]
internal sealed class AreaWeaponHandler(IPilotAreaWeapons weapons, TimeProvider clock) : IModuleEffectSource, ISelectedPilotCallHandler
{
    internal const string BurstEffect = "ECMBurstJammer";

    private sealed record Cycle(DogmaActiveEffect Effect, WeaponContact Contact, long End, bool Stopping = false);

    private readonly Dictionary<long, Cycle> _cycles = [];
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.TryGetValue(module, out var cycle) && cycle.Effect.ShipId == pilot.ShipId ? cycle.Effect : null;

    public void CancelModule(PilotCallContext context, long module)
    {
        if (_cycles.Remove(module, out var cycle))
        {
            if (AreaWeaponRules.IsProjector(cycle.Effect.Effect))
            {
                weapons.CancelModule(cycle.Effect.PilotId, cycle.Effect.ShipId, module);
            }
            context.Notifications.Add(new ShipEffectChanged(cycle.Effect, Now, false, false, Now));
            if (context.Pilot.ShipId == cycle.Effect.ShipId)
            {
                context.Flight.SetWeaponEffect(cycle.Contact, false);
            }
        }
    }

    internal void Cancel(PilotCallContext context)
    {
        if (context.Selected)
        {
            weapons.Cancel(context.Pilot.PilotId, context.Pilot.ShipId, Now);
        }
        foreach (long module in _cycles.Keys.ToArray())
        {
            CancelModule(context, module);
        }
    }

    internal void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        weapons.Advance(context.Pilot.SystemId, Now);
        foreach (var (module, cycle) in _cycles.ToArray())
        {
            if (cycle.Effect.ShipId != context.Pilot.ShipId || !context.Flight.CanTarget)
            {
                CancelModule(context, module);
                continue;
            }
            if (Now < cycle.End)
            {
                continue;
            }
            if (!cycle.Stopping && cycle.Effect.Repeats > 0 && cycle.Effect.Effect == DogmaEffect.BurstJammer)
            {
                try
                {
                    Start(context, module, DogmaEffect.BurstJammer, cycle.Effect.Repeats - 1, null, cycle);
                    continue;
                }
                catch (Exception error) when (error is InvalidDataException or OverflowException) { }
            }
            CancelModule(context, module);
        }
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "superWeaponMgr" || method != "ActivateSinglePointTargetedModule")
        {
            return PilotCallResult.Unsupported;
        }
        if (
            keywords is { Count: > 0 }
            || args is not [long module, long effect, var value]
            || effect is < int.MinValue or > int.MaxValue
            || !AreaWeaponRules.IsProjector((DogmaEffect)effect)
        )
        {
            return new PilotCallResult.Failure("Invalid point-targeted module activation.");
        }
        try
        {
            var coordinates = BlueRead.Tuple(value, 3);
            SpacePosition point = new(Number(coordinates[0]), Number(coordinates[1]), Number(coordinates[2]));
            return Activate(context, module, (DogmaEffect)effect, 0, point);
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static double Number(object? value) =>
        value switch
        {
            double number when double.IsFinite(number) => number,
            long number => number,
            _ => throw new InvalidDataException("Expected finite point coordinates."),
        };

    internal PilotCallResult? CallLauncher(PilotCallContext context, string method, object?[] args) =>
        args.FirstOrDefault() is long module && weapons.IsLauncher(context.Pilot.PilotId, module)
            ? CallModule(context, method, args, DogmaEffect.UseMissiles)
            : null;

    internal PilotCallResult CallModule(PilotCallContext context, string method, object?[] args, DogmaEffect effect)
    {
        if (method == "Deactivate" && args is [long module, _])
        {
            if (_cycles.TryGetValue(module, out var cycle))
            {
                if (AreaWeaponRules.IsProjector(cycle.Effect.Effect) || DoomsdayRules.IsTargeted(cycle.Effect.Effect))
                {
                    return new PilotCallResult.Failure("A paid superweapon cycle cannot be stopped early.");
                }
                _cycles[module] = cycle with { Stopping = true };
            }
            return PilotCallResult.None;
        }
        if (method != "Activate" || args is not [long item, _, var target, long repeats])
        {
            return PilotCallResult.Unsupported;
        }
        if (DoomsdayRules.IsTargeted(effect))
        {
            return target is long id && id > 0
                ? Activate(context, item, effect, repeats, null, id)
                : new PilotCallResult.Failure("Select a locked capital ship.");
        }
        if (target is not null && !Equals(target, context.Pilot.ShipId))
        {
            return new PilotCallResult.Failure("Activate this area weapon without a locked target.");
        }
        return Activate(context, item, effect, repeats, null);
    }

    private PilotCallResult Activate(
        PilotCallContext context,
        long module,
        DogmaEffect effect,
        long repeats,
        SpacePosition? point,
        long? target = null
    )
    {
        if (!context.Flight.CanTarget || context.Flight.Cloaked || repeats is < 0 or > int.MaxValue)
        {
            return new PilotCallResult.Failure("Activate area weapons in space outside warp and cloak.");
        }
        if (_cycles.ContainsKey(module))
        {
            return PilotCallResult.None;
        }
        try
        {
            context.Flight.Checkpoint();
            Start(context, module, effect, effect == DogmaEffect.BurstJammer ? repeats : 0, point, target: target);
            return PilotCallResult.None;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private void Start(
        PilotCallContext context,
        long item,
        DogmaEffect effect,
        long repeats,
        SpacePosition? point,
        Cycle? previous = null,
        long? target = null
    )
    {
        long now = Now;
        var result = weapons.Activate(
            context.Pilot.PilotId,
            context.Pilot.ShipId,
            item,
            effect,
            context.Flight.Heading,
            context.Flight.Velocity,
            point,
            now,
            target
        );
        var module = result.Module;
        bool doomsday = module.Kind == AreaWeaponKind.Doomsday;
        var notifications = doomsday ? context.NotificationsBeforeFlight : context.Notifications;
        DogmaActiveEffect active = new(item, context.Pilot.PilotId, context.Pilot.ShipId, target, effect, now, module.Duration, repeats);
        WeaponContact contact = new(
            item,
            module.TypeId,
            0,
            target,
            now,
            module.Duration,
            doomsday ? DoomsdayRules.Graphic(effect)
                : effect == DogmaEffect.BurstJammer ? "effects.ECMBurst"
                : "effects.MissileDeployment"
        );
        _cycles[item] = new(active, contact, AreaWeaponRules.Deadline(now, module.Duration));
        if (previous is null)
        {
            notifications.Add(new ShipEffectChanged(active, now, true, true));
            if (doomsday)
            {
                context.Flight.StopForCapitalModule();
            }
            if (doomsday || effect == DogmaEffect.BurstJammer)
            {
                context.Flight.SetWeaponEffect(contact, true);
            }
        }
        else
        {
            context.Flight.RepeatWeaponEffect(previous.Contact, contact);
        }
        if (result.Charge?.Current is { } magazine)
        {
            MagazineWire.NotifyQuantity(context, magazine, magazine.Quantity, now);
        }
        foreach (var change in result.InventoryChanges)
        {
            InventoryChangesWire.NotifyItemChange(context, change, beforeFlight: doomsday);
        }
        notifications.Add(
            new ModuleAttributesChanged([
                new(
                    context.Pilot.PilotId,
                    new DogmaItemKey.Item(context.Pilot.ShipId),
                    DogmaAttributeId.CapacitorCharge,
                    now,
                    result.Capacitor
                ),
                new(context.Pilot.PilotId, new DogmaItemKey.Item(item), DogmaAttributeId.NextActivationTime, now, result.ReadyAt),
            ])
        );
        context.Flight.RefreshMissiles();
    }
}
