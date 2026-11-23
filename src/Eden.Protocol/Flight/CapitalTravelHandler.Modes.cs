using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Flight;

internal sealed partial class CapitalTravelHandler
{
    private sealed record BastionRepeat(CapitalModeCycle Cycle, long Repeats, bool Stopping = false);

    private BastionRepeat? _bastionRepeat;

    // Pinned godma effects 6581, 6582 and 6658.
    internal static DogmaEffect? ModeEffect(string name) =>
        name switch
        {
            "moduleBonusSiegeModule" => DogmaEffect.CapitalSiege,
            "moduleBonusTriageModule" => DogmaEffect.CapitalTriage,
            "moduleBonusBastionModule" => DogmaEffect.Bastion,
            "industrialCoreEffect2" => DogmaEffect.CapitalIndustrialCore,
            "cloneJumpAccepting" => DogmaEffect.CloneJumpAccepting,
            _ => null,
        };

    internal PilotCallResult ActivateMode(PilotCallContext context, string method, object?[] args, DogmaEffect effect)
    {
        if (method == "Deactivate" && args is [long item, _])
        {
            if (effect == DogmaEffect.Bastion)
            {
                CancelBastionRepeat(context.Pilot, item);
            }
            // Deactivation prevents the next activation. The paid cycle ends at its deadline.
            return PilotCallResult.None;
        }
        var pilot = context.Pilot;
        if (
            method != "Activate"
            || args is not [long module, _, var target, long repeat]
            || target is not (null or 0L) && !Equals(target, pilot.ShipId)
            || repeat is < 0 or > int.MaxValue
            || !CanActivateMode(context.Flight)
        )
        {
            return new PilotCallResult.Failure("Activate the mode on your active ship outside warp.");
        }
        try
        {
            var cycle = StartMode(context, module, effect);
            if (effect == DogmaEffect.Bastion)
            {
                _bastionRepeat = repeat > 0 ? new(cycle, repeat) : null;
            }
            Advance(context);
            return PilotCallResult.None;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static bool CanActivateMode(FlightSession flight) =>
        flight.CanUseSelfModules && !flight.InWarp && !flight.PreparingWarp && !flight.Cloaked && !flight.MicroJumpSpooling;

    private CapitalModeCycle StartMode(PilotCallContext context, long module, DogmaEffect effect)
    {
        var flight = context.Flight;
        var pilot = context.Pilot;
        flight.Checkpoint();
        var active = travel.ActivateMode(pilot.PilotId, pilot.ShipId, module, effect);
        foreach (var change in active.FuelChanges)
        {
            InventoryChangesWire.NotifyItemChange(context, change, beforeFlight: true);
        }
        if (effect == DogmaEffect.Bastion)
        {
            propulsion?.ClearStoppedCycle(context);
            flight.StopForBastion();
        }
        else
        {
            flight.StopForCapitalModule();
        }
        RefreshModeFitting(context, active.ShipAttributes, effect);
        return active.Cycle;
    }

    private void RefreshModeFitting(PilotCallContext context, IReadOnlyDictionary<DogmaAttributeId, double> attributes, DogmaEffect effect)
    {
        context.Flight.RefreshFitting(attributes);
        if (effect == DogmaEffect.Bastion)
        {
            var key = new DogmaItemKey.Item(context.Pilot.ShipId);
            long now = Now;
            context.NotificationsBeforeFlight.Add(
                new ModuleAttributesChanged(
                    attributes.Select(pair => new ModuleAttributeChange(context.Pilot.PilotId, key, pair.Key, now, pair.Value)).ToArray()
                )
            );
        }
    }

    private CapitalModeCycle? RepeatBastion(PilotCallContext context, CapitalModeCycle? mode)
    {
        if (_bastionRepeat is not { } pending || mode == pending.Cycle)
        {
            return null;
        }
        var cycle = pending.Cycle;
        _bastionRepeat = null;
        if (
            pending.Stopping
            || mode is not null
            || !context.Selected
            || context.Pilot.PilotId != cycle.PilotId
            || context.Pilot.ShipId != cycle.ShipId
            || Now < cycle.EndsAt
            || !CanActivateMode(context.Flight)
        )
        {
            return null;
        }
        try
        {
            var next = StartMode(context, cycle.ModuleId, cycle.Effect);
            if (pending.Repeats > 1)
            {
                _bastionRepeat = new(next, pending.Repeats - 1);
            }
            return next;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    private void CancelBastionRepeat(DockedPilot pilot, long module)
    {
        if (
            _bastionRepeat is { Cycle: var cycle } pending
            && cycle.PilotId == pilot.PilotId
            && cycle.ShipId == pilot.ShipId
            && cycle.ModuleId == module
        )
        {
            _bastionRepeat = pending with { Stopping = true };
        }
    }

    private DogmaActiveEffect Effect(CapitalModeCycle mode) =>
        new(
            mode.ModuleId,
            mode.PilotId,
            mode.ShipId,
            null,
            mode.Effect,
            mode.StartedAt,
            CapitalModeRules.DurationMilliseconds(mode.Effect),
            _bastionRepeat is { } pending && pending.Cycle == mode ? pending.Repeats : 0
        );
}
