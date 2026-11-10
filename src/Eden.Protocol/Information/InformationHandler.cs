using Eden.Gameplay.Docking;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Information;

[Injectable(InjectionType.Scoped)]
internal sealed class InformationHandler(
    IPilotInformation information,
    TargetingHandler targeting,
    TimeProvider clock,
    SupportModuleWire attributes
) : ISelectedPilotCallHandler, IModuleEffectSource, IDisposable
{
    private sealed record Cycle(InformationCycle Contact, long Repeats, bool Stopping = false);

    private readonly Dictionary<long, Cycle> _cycles = [];
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static DogmaActiveEffect Active(Cycle cycle) =>
        new(
            cycle.Contact.Module.ItemId,
            cycle.Contact.PilotId,
            cycle.Contact.ShipId,
            cycle.Contact.TargetId == 0 ? cycle.Contact.ShipId : cycle.Contact.TargetId,
            cycle.Contact.Module.Effect,
            cycle.Contact.StartedAt,
            cycle.Contact.Module.DurationMilliseconds,
            cycle.Repeats
        );

    private static SupportContact Space(Cycle cycle) =>
        new(
            new(
                cycle.Contact.Module.ItemId,
                cycle.Contact.Module.TypeId,
                cycle.Contact.Module.Effect,
                cycle.Contact.Module.DurationMilliseconds,
                cycle.Contact.Module.CapacitorNeed
            ),
            cycle.Contact.StartedAt,
            cycle.Contact.TargetId == 0 ? null : cycle.Contact.TargetId
        );

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.TryGetValue(module, out var cycle) && cycle.Contact.ShipId == pilot.ShipId ? Active(cycle) : null;

    public void CancelModule(PilotCallContext context, long module)
    {
        if (_cycles.TryGetValue(module, out var cycle))
        {
            Stop(context, cycle);
        }
    }

    private void Start(PilotCallContext context, long module, DogmaEffect effect, long target, long repeats, Cycle? previous = null)
    {
        bool passive = effect == DogmaEffect.PassiveTargeting;
        bool pending = passive && !targeting.IsLocked(target) && !targeting.IsPending(target);
        if (passive)
        {
            var result = targeting.LockPassively(context, target);
            if (result is PilotCallResult.Failure or PilotCallResult.NamedFailure)
            {
                throw new InvalidDataException("This target cannot be locked passively.");
            }
        }
        InformationActivation activation;
        try
        {
            activation = information.Begin(
                context.Pilot.PilotId,
                module,
                effect,
                target,
                targeting.IsLocked(target) ? new HashSet<long> { target } : [],
                Now
            );
        }
        catch
        {
            if (pending)
            {
                targeting.CancelPassive(context, target);
            }
            throw;
        }
        if (effect is DogmaEffect.ShipScan or DogmaEffect.CargoScan or DogmaEffect.PassiveTargeting)
        {
            repeats = 0;
        }
        Cycle cycle = new(activation.Cycle, repeats);
        _cycles[module] = cycle;
        if (previous is null)
        {
            context.Notifications.Add(new ShipEffectChanged(Active(cycle), Now, true, true));
        }
        else
        {
            context.Flight.SetSupportEffect(Space(previous), false);
        }
        context.Flight.SetSupportEffect(Space(cycle), true);
        attributes.Refresh(context, activation.Capacitor);
    }

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        information.Stop(cycle.Contact.PilotId, cycle.Contact.Module.ItemId, cycle.Contact.ReadyAt);
        _cycles.Remove(cycle.Contact.Module.ItemId);
        context.Notifications.Add(new ShipEffectChanged(Active(cycle), Now, false, false, Now));
        if (context.Pilot.ShipId == cycle.Contact.ShipId)
        {
            context.Flight.SetSupportEffect(Space(cycle), false);
        }
    }

    public void Advance(PilotCallContext context)
    {
        foreach (var cycle in _cycles.Values.ToArray())
        {
            var contact = cycle.Contact;
            bool targeted =
                contact.Module.Effect is DogmaEffect.CargoScan or DogmaEffect.ShipScan or DogmaEffect.Salvaging or DogmaEffect.TractorBeam;
            if (
                contact.ShipId != context.Pilot.ShipId
                || !context.Flight.CanTarget
                || !information.IsActive(contact)
                || targeted && (!targeting.IsLocked(contact.TargetId) || context.Flight.FindTarget(contact.TargetId) is null)
            )
            {
                Stop(context, cycle);
                continue;
            }
            if (contact.Module.Effect == DogmaEffect.TractorBeam)
            {
                try
                {
                    information.AdvanceTractor(contact, Now);
                }
                catch (InvalidDataException)
                {
                    Stop(context, cycle);
                    continue;
                }
            }
            if (contact.ReadyAt > Now)
            {
                continue;
            }
            bool completed = false;
            try
            {
                var result = information.Complete(contact.PilotId, contact.Module.ItemId, contact.ReadyAt, Now);
                completed = result.Salvage?.Success == true;
                if (result.Cargo is { } cargo)
                {
                    context.Notifications.Add(new CargoScanCompleted(cargo));
                }
                if (result.Ship is { } ship)
                {
                    context.Notifications.Add(new ShipScanCompleted(ship));
                }
                foreach (var change in result.Salvage?.Cargo ?? [])
                {
                    InventoryChangesWire.NotifyItemChange(context, change);
                }
                if (contact.Module.Effect == DogmaEffect.AutomaticTargeting)
                {
                    foreach (long hostile in information.Hostiles(contact.PilotId, Now))
                    {
                        if (
                            context.Flight.FindTarget(hostile) is { } target
                            && context.Pilot.Position!.DistanceTo(target.Position) - target.Radius - context.Pilot.Flight.ShipRadius
                                <= contact.Module.Range
                        )
                        {
                            _ = targeting.LockAutomatically(context, hostile);
                        }
                    }
                }
                if (!completed && !context.Flight.Cloaked && !cycle.Stopping && cycle.Repeats > 0)
                {
                    Start(context, contact.Module.ItemId, contact.Module.Effect, contact.TargetId, cycle.Repeats - 1, cycle);
                    continue;
                }
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException or CargoCapacityException) { }
            Stop(context, cycle);
        }
    }

    public void Cancel(PilotCallContext context)
    {
        foreach (var cycle in _cycles.Values.ToArray())
        {
            Stop(context, cycle);
        }
    }

    internal PilotCallResult CallModule(PilotCallContext context, string method, object?[] args)
    {
        if (args.Length < 2 || InformationWire.Effect(BlueMarshal.Text(args[1])) is not { } effect)
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            context.Flight.Checkpoint();
            targeting.Advance(context);
            Advance(context);
            if (method == "Deactivate" && args is [long module, _])
            {
                if (_cycles.TryGetValue(module, out var cycle))
                {
                    if (cycle.Contact.Module.Effect != effect)
                    {
                        return new PilotCallResult.Failure("The module is running a different information effect.");
                    }
                    _cycles[module] = cycle with { Stopping = true };
                }
                return PilotCallResult.None;
            }
            if (
                method != "Activate"
                || args is not [long item, _, var targetValue, long repeats]
                || repeats < 0
                || repeats > int.MaxValue
                || targetValue is not (null or long)
            )
            {
                return PilotCallResult.Unsupported;
            }
            long target = targetValue is long id ? id : 0;
            if (!context.Flight.CanTarget)
            {
                return new PilotCallResult.Failure("Activate information modules in space and outside warp.");
            }
            if (_cycles.TryGetValue(item, out var active))
            {
                return active.Contact.Module.Effect == effect && active.Contact.TargetId == target
                    ? PilotCallResult.None
                    : new PilotCallResult.Failure("The module is already active on a different target.");
            }
            Start(context, item, effect, target, repeats);
            return PilotCallResult.None;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
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
        bool survey = service == "miningScanMgr" && method == "perform_scan" && args.Length == 0;
        if (!survey)
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            context.Flight.Checkpoint();
            if (!context.Flight.CanTarget)
            {
                return new PilotCallResult.Failure("Use scanners in space and outside warp.");
            }
            return new MiningSurveyReply(information.Survey(context.Pilot.PilotId, Now));
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
            information.Stop(cycle.Contact.PilotId, cycle.Contact.Module.ItemId, cycle.Contact.ReadyAt);
        }
        _cycles.Clear();
    }
}
