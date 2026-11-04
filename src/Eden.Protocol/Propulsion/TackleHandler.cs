using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Propulsion;

[Injectable(InjectionType.Scoped)]
internal sealed class TackleHandler(
    TimeProvider clock,
    IPilotTackle tackle,
    TargetingHandler targeting,
    IPilotInventory inventory,
    IPilotFitting fitting
) : IModuleEffectSource, IDisposable
{
    private sealed record Cycle(
        long PilotId,
        long ShipId,
        TackleModule Module,
        TackleTarget? Target,
        long Started,
        long StartTime,
        long Repeats,
        bool Stopping = false
    );

    private readonly Dictionary<long, Cycle> _cycles = [];
    private TackleEffect[] _received = [];

    private long Now() => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static DogmaActiveEffect Effect(Cycle cycle) =>
        new(
            cycle.Module.ItemId,
            cycle.PilotId,
            cycle.ShipId,
            cycle.Target?.Id,
            cycle.Module.Effect,
            cycle.StartTime,
            cycle.Module.DurationMilliseconds,
            cycle.Repeats
        );

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.TryGetValue(module, out var cycle) && cycle.ShipId == pilot.ShipId ? Effect(cycle) : null;

    private void Notify(PilotCallContext context, Cycle cycle, bool active) =>
        context.Notifications.Add(new ShipEffectChanged(Effect(cycle), Now(), active, active, active ? null : Now()));

    private static TackleContact Contact(Cycle cycle) => new(cycle.Module, cycle.Target?.Id ?? cycle.ShipId, cycle.StartTime);

    private void Start(PilotCallContext context, long module, DogmaEffect effect, long? targetId, long repeats, Cycle? previous = null)
    {
        bool core = effect == DogmaEffect.WarpCoreStabilizer;
        TackleTarget? target = null;
        if (!core)
        {
            if (targetId is not long id || context.Flight.FindTarget(id) is null)
            {
                throw new InvalidDataException("Lock a nearby ship or NPC before applying tackle.");
            }
            target = new(id, context.Flight.VisibleNpcs.Any(n => n.Id == id) ? TackleTargetKind.Npc : TackleTargetKind.Ship);
        }
        var locked = target is not null && targeting.IsLocked(target.Id) ? new HashSet<long> { target.Id } : [];
        long started = clock.GetTimestamp();
        long startTime = Now();
        var result = tackle.Activate(context.Pilot.PilotId, module, effect, target, locked, startTime);
        Cycle cycle = new(
            context.Pilot.PilotId,
            context.Pilot.ShipId,
            result.Module,
            result.Target,
            started,
            startTime,
            core ? 0 : repeats
        );
        _cycles[module] = cycle;
        if (previous is null)
        {
            Notify(context, cycle, true);
        }
        else
        {
            context.Flight.SetTackleEffect(Contact(previous), false);
        }
        context.Flight.SetTackleEffect(Contact(cycle), true);
        context.Flight.RefreshTackle(true);
        context.Notifications.Add(
            new ModuleAttributesChanged([
                new(cycle.PilotId, new DogmaItemKey.Item(cycle.ShipId), DogmaAttributeId.CapacitorCharge, Now(), result.Capacitor),
                new(cycle.PilotId, new DogmaItemKey.Item(module), DogmaAttributeId.NextActivationTime, Now(), core ? result.ReadyAt : 0),
            ])
        );
        if (core)
        {
            NotifyShipAttributes(context);
        }
    }

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        tackle.Stop(cycle.PilotId, cycle.ShipId, cycle.Module.ItemId);
        _cycles.Remove(cycle.Module.ItemId);
        Notify(context, cycle, false);
        if (cycle.ShipId == context.Pilot.ShipId)
        {
            context.Flight.SetTackleEffect(Contact(cycle), false);
            context.Flight.RefreshTackle(true);
            if (cycle.Module.Effect == DogmaEffect.WarpCoreStabilizer)
            {
                NotifyShipAttributes(context);
            }
        }
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        tackle.Sweep(Now());
        foreach (var cycle in _cycles.Values.ToArray())
        {
            bool targeted = cycle.Target is not null;
            bool usable = targeted ? context.Flight.CanTarget : context.Flight.CanUseSelfModules && !context.Flight.InWarp;
            bool online = inventory.List(cycle.PilotId, cycle.ShipId).Any(i => i.Id == cycle.Module.ItemId && i.Online);
            if (
                cycle.ShipId != context.Pilot.ShipId
                || !usable
                || !online
                || targeted && (!targeting.IsLocked(cycle.Target!.Id) || context.Flight.FindTarget(cycle.Target.Id) is null)
            )
            {
                Stop(context, cycle);
                continue;
            }
            if (clock.GetElapsedTime(cycle.Started).TotalMilliseconds < cycle.Module.DurationMilliseconds)
            {
                continue;
            }
            if (!cycle.Stopping && cycle.Repeats > 0 && targeted)
            {
                try
                {
                    Start(context, cycle.Module.ItemId, cycle.Module.Effect, cycle.Target!.Id, cycle.Repeats - 1, cycle);
                    continue;
                }
                catch (Exception error) when (error is InvalidDataException or OverflowException) { }
            }
            Stop(context, cycle);
        }
        var incoming = context.Flight.CanUseSelfModules ? tackle.Incoming(context.Pilot.ShipId).ToArray() : [];
        var ended = _received.Where(e => !incoming.Any(n => n.Module.ItemId == e.Module.ItemId)).ToArray();
        var started = incoming.Where(n => !_received.Any(e => e.Module.ItemId == n.Module.ItemId)).ToArray();
        foreach (var effect in ended)
        {
            context.Notifications.Add(new TackleEwarChanged(effect, false));
        }
        foreach (var effect in started)
        {
            context.Notifications.Add(new TackleEwarChanged(effect, true));
        }
        if (ended.Length > 0 || started.Length > 0)
        {
            context.Flight.RefreshTackle(true);
            NotifyShipAttributes(context);
        }
        _received = incoming;
    }

    private void NotifyShipAttributes(PilotCallContext context)
    {
        var attributes = tackle.ShipAttributes(context.Pilot.PilotId);
        long now = Now();
        List<ModuleAttributeChange> changes =
        [
            new(
                context.Pilot.PilotId,
                new DogmaItemKey.Item(context.Pilot.ShipId),
                DogmaAttributeId.MaxVelocity,
                now,
                context.Pilot.Flight.MaxSpeed
            ),
            new(
                context.Pilot.PilotId,
                new DogmaItemKey.Item(context.Pilot.ShipId),
                DogmaAttributeId.WarpScrambleStatus,
                now,
                attributes.GetValueOrDefault(DogmaAttributeId.WarpScrambleStatus)
            ),
        ];
        foreach (
            var item in inventory.List(context.Pilot.PilotId, context.Pilot.ShipId).Where(i => InventoryDefinitions.IsMediumSlot(i.FlagId))
        )
        {
            var module = fitting.ModuleAttributes(context.Pilot.PilotId, item);
            if (module.TryGetValue(DogmaAttributeId.ActivationBlocked, out double blocked))
            {
                changes.Add(new(context.Pilot.PilotId, new DogmaItemKey.Item(item.Id), DogmaAttributeId.ActivationBlocked, now, blocked));
            }
        }
        context.Notifications.Add(new ModuleAttributesChanged(changes));
    }

    public void Cancel(PilotCallContext context)
    {
        foreach (var cycle in _cycles.Values.ToArray())
        {
            Stop(context, cycle);
        }
    }

    public void Leave(PilotCallContext context)
    {
        Cancel(context);
        _received = [];
    }

    public PilotCallResult Call(PilotCallContext context, string service, string method, object?[] args)
    {
        if (service != "dogmaIM" || args.Length < 2 || TackleWire.Effect(BlueMarshal.Text(args[1])) is not { } effect)
        {
            return PilotCallResult.Unsupported;
        }
        bool valid = (method, args) switch
        {
            ("Activate", [long, _, null or long, long repeats]) when repeats is >= 0 and <= int.MaxValue => true,
            ("Deactivate", [long, _]) => true,
            _ => false,
        };
        if (!valid)
        {
            return PilotCallResult.Unsupported;
        }
        context.Flight.Checkpoint();
        targeting.Advance(context);
        Advance(context);
        try
        {
            long module = (long)args[0]!;
            if (_cycles.TryGetValue(module, out var cycle))
            {
                if (
                    cycle.Module.Effect != effect
                    || method == "Activate" && args[2] is long target && target != (cycle.Target?.Id ?? cycle.ShipId)
                )
                {
                    return new PilotCallResult.Failure("The module is already active with a different effect or target.");
                }
                if (method == "Deactivate")
                {
                    _cycles[module] = cycle with { Stopping = true };
                }
                return PilotCallResult.None;
            }
            if (method == "Deactivate")
            {
                return PilotCallResult.None;
            }
            bool core = effect == DogmaEffect.WarpCoreStabilizer;
            if (core ? !context.Flight.CanUseSelfModules || context.Flight.InWarp : !context.Flight.CanTarget)
            {
                return new PilotCallResult.Failure("Activate tackle modules while in space and outside warp.");
            }
            if (core && args[2] is long own && own != context.Pilot.ShipId)
            {
                return new PilotCallResult.Failure("Activate the warp core stabilizer on your own ship.");
            }
            Start(context, module, effect, args[2] as long?, (long)args[3]!);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("The tackle module exceeded the local cycle limit.");
        }
    }

    public void Dispose()
    {
        foreach (var cycle in _cycles.Values)
        {
            tackle.Stop(cycle.PilotId, cycle.ShipId, cycle.Module.ItemId);
        }
        _cycles.Clear();
    }
}
