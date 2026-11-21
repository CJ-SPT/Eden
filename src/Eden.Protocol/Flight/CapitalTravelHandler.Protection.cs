using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Flight;

internal sealed partial class CapitalTravelHandler
{
    internal const string ProtectionEffectName = "moduleBonusIndustrialInvulnerability";
    private IndustrialProtectionActivation? _protection;

    internal PilotCallResult ActivateProtection(PilotCallContext context, string method, object?[] args)
    {
        if (method == "Deactivate" && args is [long, _])
        {
            return PilotCallResult.None;
        }
        var pilot = context.Pilot;
        var flight = context.Flight;
        if (
            method != "Activate"
            || args is not [long module, _, var target, long repeats]
            || repeats is < 0 or > int.MaxValue
            || !flight.CanTarget
            || flight.PreparingWarp
            || flight.MicroJumpSpooling
            || targeting is null
        )
        {
            return new PilotCallResult.Failure("Activate P.A.N.I.C. while controlling a Rorqual outside warp.");
        }
        try
        {
            targeting.Advance(context);
            var asteroid = flight.VisibleAsteroids.FirstOrDefault(body =>
                targeting.IsLocked(body.Id) && (target is null or 0L || Equals(target, body.Id))
            );
            if (asteroid is null)
            {
                return new PilotCallResult.Failure("Lock an ore or ice asteroid before activating P.A.N.I.C.");
            }
            flight.Checkpoint();
            var activation = travel.ProtectIndustry(pilot.PilotId, pilot.ShipId, module, asteroid.Id);
            _protection = activation;
            InventoryChangesWire.NotifyItemChange(context, activation.Burnout, beforeFlight: true);
            context.NotificationsBeforeFlight.Add(
                new ModuleAttributesChanged([
                    new(pilot.PilotId, new DogmaItemKey.Item(module), DogmaAttributeId.Damage, Now, activation.Damage),
                ])
            );
            context.NotificationsBeforeFlight.Add(new ShipEffectChanged(Effect(activation), Now, true, true));
            return PilotCallResult.None;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private void AdvanceProtection(PilotCallContext context)
    {
        if (_protection is not { } active || Now < active.EndsAt)
        {
            return;
        }
        _protection = null;
        if (context.Selected && context.Pilot.ShipId == active.ShipId)
        {
            context.NotificationsBeforeFlight.Add(new ShipEffectChanged(Effect(active), Now, false, false, active.EndsAt));
        }
    }

    private static DogmaActiveEffect Effect(IndustrialProtectionActivation cycle) =>
        new(
            cycle.ModuleId,
            cycle.PilotId,
            cycle.ShipId,
            null,
            DogmaEffect.IndustrialInvulnerability,
            cycle.StartedAt,
            (cycle.EndsAt - cycle.StartedAt) / (double)TimeSpan.TicksPerMillisecond,
            0
        );
}
