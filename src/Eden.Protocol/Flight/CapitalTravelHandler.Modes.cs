using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Flight;

internal sealed partial class CapitalTravelHandler
{
    // Pinned godma effects 6581 and 6582.
    internal static DogmaEffect? ModeEffect(string name) =>
        name switch
        {
            "moduleBonusSiegeModule" => DogmaEffect.CapitalSiege,
            "moduleBonusTriageModule" => DogmaEffect.CapitalTriage,
            "industrialCoreEffect2" => DogmaEffect.CapitalIndustrialCore,
            _ => null,
        };

    internal PilotCallResult ActivateMode(PilotCallContext context, string method, object?[] args, DogmaEffect effect)
    {
        if (method == "Deactivate" && args is [long, _])
        {
            // The client may request deactivation while the paid single cycle runs.
            return PilotCallResult.None;
        }
        var flight = context.Flight;
        var pilot = context.Pilot;
        if (
            method != "Activate"
            || args is not [long module, _, var target, long repeat]
            || target is not (null or 0L) && !Equals(target, pilot.ShipId)
            || repeat is < 0 or > int.MaxValue
            || !flight.CanUseSelfModules
            || flight.InWarp
            || flight.PreparingWarp
            || flight.Cloaked
            || flight.MicroJumpSpooling
        )
        {
            return new PilotCallResult.Failure("Activate a capital mode on your active ship outside warp.");
        }
        try
        {
            flight.Checkpoint();
            var active = travel.ActivateMode(pilot.PilotId, pilot.ShipId, module, effect);
            foreach (var change in active.FuelChanges)
            {
                InventoryChangesWire.NotifyItemChange(context, change, beforeFlight: true);
            }
            flight.StopForCapitalModule();
            flight.RefreshFitting(active.ShipAttributes);
            Advance(context);
            return PilotCallResult.None;
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static DogmaActiveEffect Effect(CapitalModeCycle mode) =>
        new(mode.ModuleId, mode.PilotId, mode.ShipId, null, mode.Effect, mode.StartedAt, CapitalModeRules.CycleMilliseconds, 0);
}
