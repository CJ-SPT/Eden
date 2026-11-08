using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Repair;

[Injectable(InjectionType.Scoped)]
internal sealed class RemoteAssistanceHandler(
    TimeProvider clock,
    IPilotRemoteAssistance supporting,
    TargetingHandler targeting,
    SupportModuleWire attributes
) : IModuleEffectSource, IDisposable
{
    private sealed record Cycle(RemoteAssistanceContact Contact, long Repeats, bool Stopping = false);

    private readonly Dictionary<long, Cycle> _cycles = [];
    private IReadOnlyList<RemoteAssistanceContact> _incoming = [];

    private long Now() => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static SupportContact SpaceContact(RemoteAssistanceContact contact) =>
        new(contact.Module.Cycle, contact.StartedAt, contact.TargetId);

    private static DogmaActiveEffect Active(Cycle cycle) =>
        new(
            cycle.Contact.Module.Cycle.ItemId,
            cycle.Contact.PilotId,
            cycle.Contact.SourceShipId,
            cycle.Contact.TargetId,
            cycle.Contact.Module.Cycle.Effect,
            cycle.Contact.StartedAt,
            cycle.Contact.Module.Cycle.DurationMilliseconds,
            cycle.Repeats
        );

    public void CancelModule(PilotCallContext context, long module)
    {
        if (_cycles.TryGetValue(module, out var cycle))
        {
            Stop(context, cycle);
        }
    }

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.TryGetValue(module, out var cycle) && cycle.Contact.SourceShipId == pilot.ShipId ? Active(cycle) : null;

    private void Start(PilotCallContext context, long module, DogmaEffect effect, long target, long repeats, Cycle? previous = null)
    {
        if (context.Flight.FindTarget(target) is null)
        {
            throw new InvalidDataException("Lock another pilot's nearby ship before applying remote assistance.");
        }
        var result = supporting.Activate(
            context.Pilot.PilotId,
            module,
            effect,
            target,
            targeting.IsLocked(target) ? new HashSet<long> { target } : [],
            Now()
        );
        Cycle cycle = new(result.Contact, repeats);
        if (previous is null)
        {
            context.Notifications.Add(new ShipEffectChanged(Active(cycle), Now(), true, true));
        }
        else
        {
            context.Flight.SetSupportEffect(SpaceContact(previous.Contact), false);
        }
        _cycles[module] = cycle;
        context.Flight.SetSupportEffect(SpaceContact(result.Contact), true);
        attributes.Refresh(context, result.Capacitor);
    }

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        var contact = cycle.Contact;
        supporting.Stop(contact.PilotId, contact.SourceShipId, contact.Module.Cycle.ItemId);
        _cycles.Remove(contact.Module.Cycle.ItemId);
        context.Notifications.Add(new ShipEffectChanged(Active(cycle), Now(), false, false, Now()));
        if (context.Pilot.ShipId == contact.SourceShipId)
        {
            context.Flight.SetSupportEffect(SpaceContact(contact), false);
        }
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        foreach (var cycle in _cycles.Values.ToArray())
        {
            var contact = cycle.Contact;
            if (
                contact.SourceShipId != context.Pilot.ShipId
                || !context.Flight.CanTarget
                || context.Flight.InWarp
                || !targeting.IsLocked(contact.TargetId)
                || context.Flight.FindTarget(contact.TargetId) is null
                || !supporting.IsActive(contact.PilotId, contact.SourceShipId, contact.Module.Cycle.ItemId)
            )
            {
                Stop(context, cycle);
                continue;
            }
            if (contact.ExpiresAt > Now())
            {
                continue;
            }
            if (!cycle.Stopping && cycle.Repeats > 0)
            {
                try
                {
                    Start(context, contact.Module.Cycle.ItemId, contact.Module.Cycle.Effect, contact.TargetId, cycle.Repeats - 1, cycle);
                    continue;
                }
                catch (Exception error) when (error is InvalidDataException or OverflowException) { }
            }
            Stop(context, cycle);
        }
        var incoming = context.Flight.CanTarget ? supporting.Incoming(context.Pilot.ShipId) : [];
        if (!ReferenceEquals(incoming, _incoming))
        {
            foreach (var ended in _incoming.Where(e => !incoming.Any(c => c.Module.Cycle.ItemId == e.Module.Cycle.ItemId)))
            {
                context.NotificationsBeforeFlight.Add(new RemoteAssistanceChanged(ended, false));
            }
            foreach (var added in incoming.Where(e => !_incoming.Any(c => c.Module.Cycle.ItemId == e.Module.Cycle.ItemId)))
            {
                context.NotificationsBeforeFlight.Add(new RemoteAssistanceChanged(added, true));
            }
            if (!_incoming.SequenceEqual(incoming))
            {
                attributes.Refresh(context);
            }
            _incoming = incoming;
        }
    }

    public void Cancel(PilotCallContext context)
    {
        foreach (var cycle in _cycles.Values.ToArray())
        {
            Stop(context, cycle);
        }
        supporting.Cancel(context.Pilot.PilotId, context.Pilot.ShipId);
        foreach (var contact in _incoming)
        {
            context.NotificationsBeforeFlight.Add(new RemoteAssistanceChanged(contact, false));
        }
        _incoming = [];
    }

    public PilotCallResult Call(PilotCallContext context, string service, string method, object?[] args)
    {
        if (service != "dogmaIM" || args.Length < 2 || RemoteAssistanceWire.Effect(BlueMarshal.Text(args[1])) is not { } effect)
        {
            return PilotCallResult.Unsupported;
        }
        bool valid = (method, args) switch
        {
            ("Activate", [long, _, long, long repeats]) when repeats is >= 0 and <= int.MaxValue => true,
            ("Deactivate", [long, _]) => true,
            _ => false,
        };
        if (!valid)
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            context.Flight.Checkpoint();
            targeting.Advance(context);
            Advance(context);
            long module = (long)args[0]!;
            if (_cycles.TryGetValue(module, out var cycle))
            {
                if (cycle.Contact.Module.Cycle.Effect != effect || method == "Activate" && (long)args[2]! != cycle.Contact.TargetId)
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
            if (!context.Flight.CanTarget || context.Flight.InWarp)
            {
                return new PilotCallResult.Failure("Activate remote assistance modules in space and outside warp.");
            }
            Start(context, module, effect, (long)args[2]!, (long)args[3]!);
            return PilotCallResult.None;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    public void Dispose()
    {
        foreach (var cycle in _cycles.Values)
        {
            var contact = cycle.Contact;
            supporting.Stop(contact.PilotId, contact.SourceShipId, contact.Module.Cycle.ItemId);
        }
        _cycles.Clear();
        _incoming = [];
    }
}
