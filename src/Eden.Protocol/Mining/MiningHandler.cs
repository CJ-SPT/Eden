using System.Security.Cryptography;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Mining;

[Injectable(InjectionType.Scoped)]
internal sealed class MiningHandler(TimeProvider clock, IPilotMining mining, TargetingHandler targeting) : IModuleEffectSource
{
    private sealed record Cycle(MiningLaser Laser, long Target, long Started, long StartTime, long Repeats);

    private readonly Dictionary<long, Cycle> _cycles = [];
    private const int ProbabilityResolution = 1_000_000;

    public void Cancel(PilotCallContext context)
    {
        foreach (var cycle in _cycles.Values.ToArray())
        {
            Stop(context, cycle);
        }
    }

    public bool IsActive(long module) => _cycles.ContainsKey(module);

    public void CancelModule(PilotCallContext context, long module)
    {
        if (_cycles.TryGetValue(module, out var cycle))
        {
            Stop(context, cycle);
        }
    }

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.TryGetValue(module, out var cycle)
            ? new(
                module,
                pilot.PilotId,
                pilot.ShipId,
                cycle.Target,
                cycle.Laser.Effect,
                cycle.StartTime,
                cycle.Laser.DurationMilliseconds,
                cycle.Repeats
            )
            : null;

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        foreach (var (module, cycle) in _cycles.ToArray())
        {
            var target = context.Flight.FindTarget(cycle.Target);
            if (
                !targeting.IsLocked(cycle.Target)
                || target is null
                || context.Pilot.Position!.DistanceTo(target.Position) - context.Pilot.Flight.ShipRadius - target.Radius > cycle.Laser.Range
            )
            {
                Stop(context, cycle);
                continue;
            }
            if (clock.GetElapsedTime(cycle.Started).TotalMilliseconds < cycle.Laser.DurationMilliseconds)
            {
                continue;
            }
            try
            {
                var result = Collect(context, cycle, 1, true);
                if (cycle.Repeats > 0 && !result.Depleted && !result.CargoFull)
                {
                    Start(context, module, cycle.Target, cycle.Repeats - 1, cycle);
                }
                else
                {
                    Stop(context, cycle);
                }
            }
            catch (Exception error) when (error is InvalidDataException or CargoCapacityException)
            {
                Stop(context, cycle);
            }
        }
    }

    private static double Roll() => (double)RandomNumberGenerator.GetInt32(ProbabilityResolution) / ProbabilityResolution;

    private MiningYield Collect(PilotCallContext context, Cycle cycle, double fraction, bool completed)
    {
        var result = mining.Collect(
            context.Pilot.PilotId,
            cycle.Laser.ItemId,
            cycle.Target,
            new(fraction, completed, new(Roll(), Roll(), Roll(), Roll(), Roll(), Roll()))
        );
        if (result.Cargo is { } change)
        {
            InventoryChangesWire.NotifyItemChange(context, change);
        }
        if (result.Crystal is { } crystal)
        {
            MagazineWire.NotifyState(context, crystal, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        }
        if (result.Extracted > 0 || result.Residue > 0)
        {
            // oreMinedNotificationAdapter in build 3662168 uses these exact keys.
            context.Notifications.Add(new OreMined(result, cycle.Target, cycle.Laser.ItemId));
        }
        return result;
    }

    private void Start(PilotCallContext context, long module, long target, long repeats, Cycle? previous = null, DogmaEffect? effect = null)
    {
        long started = clock.GetTimestamp();
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var start = mining.Begin(context.Pilot.PilotId, module, target, now, effect ?? previous?.Laser.Effect);
        Cycle cycle = new(start.Laser, target, started, now, repeats);
        _cycles[module] = cycle;
        if (previous is null)
        {
            Effect(context, cycle, true);
        }
        else
        {
            context.Flight.SetMiningEffect(Contact(previous), false);
        }
        context.Flight.SetMiningEffect(Contact(cycle), true);
        context.Notifications.Add(
            new ModuleAttributesChanged([
                new ModuleAttributeChange(
                    context.Pilot.PilotId,
                    new DogmaItemKey.Item(context.Pilot.ShipId),
                    DogmaAttributeId.CapacitorCharge,
                    now,
                    start.Capacitor
                ),
            ])
        );
    }

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        if (!_cycles.TryGetValue(cycle.Laser.ItemId, out var active) || !ReferenceEquals(active, cycle))
        {
            return;
        }
        long completesAt = checked(cycle.StartTime + (long)Math.Ceiling(cycle.Laser.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        mining.Stop(context.Pilot.PilotId, cycle.Laser.ItemId, completesAt);
        if (!_cycles.Remove(cycle.Laser.ItemId))
        {
            return;
        }
        Effect(context, cycle, false);
        context.Flight.SetMiningEffect(Contact(cycle), false);
    }

    private void Effect(PilotCallContext context, Cycle cycle, bool active)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        context.Notifications.Add(
            new ShipEffectChanged(
                new DogmaActiveEffect(
                    cycle.Laser.ItemId,
                    context.Pilot.PilotId,
                    context.Pilot.ShipId,
                    cycle.Target,
                    cycle.Laser.Effect,
                    cycle.StartTime,
                    cycle.Laser.DurationMilliseconds,
                    cycle.Repeats
                ),
                now,
                active,
                active,
                active ? null : now
            )
        );
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "dogmaIM" || method is not ("Activate" or "Deactivate"))
        {
            return PilotCallResult.Unsupported;
        }
        context.Flight.Checkpoint();
        targeting.Advance(context);
        Advance(context);
        try
        {
            if (
                method == "Activate"
                && args is [long module, var effect, long target, long repeats]
                && MiningWireDefinitions.Effect(BlueMarshal.Text(effect)) is { } requested
                && repeats >= 0
                && repeats <= int.MaxValue
            )
            {
                if (!context.Flight.CanTarget || !targeting.IsLocked(target))
                {
                    return new PilotCallResult.Failure("Lock an asteroid before activating the mining laser.");
                }
                if (_cycles.TryGetValue(module, out var existing))
                {
                    return existing.Target == target && existing.Laser.Effect == requested
                        ? PilotCallResult.None
                        : new PilotCallResult.Failure("The mining laser is already active.");
                }
                Start(context, module, target, repeats, effect: requested);
                return PilotCallResult.None;
            }
            if (
                method == "Deactivate"
                && args is [long item, var effectName]
                && MiningWireDefinitions.Effect(BlueMarshal.Text(effectName)) is { } requestedEffect
            )
            {
                if (_cycles.TryGetValue(item, out var cycle))
                {
                    if (cycle.Laser.Effect != requestedEffect)
                    {
                        return new PilotCallResult.Failure("The mining effect does not match the active module.");
                    }
                    // Ore/gas can return elapsed yield; ice needs a complete cycle.
                    // Only completed cycles can roll a critical success.
                    double fraction = Math.Clamp(
                        clock.GetElapsedTime(cycle.Started).TotalMilliseconds / cycle.Laser.DurationMilliseconds,
                        0,
                        1
                    );
                    bool completed = cycle.Laser.Family == MiningFamily.Ice && fraction == 1;
                    _ = Collect(context, cycle, fraction, completed);
                    Stop(context, cycle);
                }
                return PilotCallResult.None;
            }
            return PilotCallResult.Unsupported;
        }
        catch (Exception error) when (error is InvalidDataException or CargoCapacityException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static Eden.Protocol.Flight.MiningContact Contact(Cycle cycle) =>
        new(
            cycle.Laser.ItemId,
            cycle.Laser.TypeId,
            cycle.Target,
            cycle.StartTime,
            cycle.Laser.DurationMilliseconds,
            MiningWireDefinitions.Graphic(cycle.Laser.Family)
        );
}
