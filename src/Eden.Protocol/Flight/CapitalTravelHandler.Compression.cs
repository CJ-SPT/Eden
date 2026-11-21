using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Structures;

namespace Eden.Protocol.Flight;

internal sealed partial class CapitalTravelHandler
{
    internal const string CompressionEffectName = "industrialItemCompression";

    private IReadOnlyList<ShipCompressorCycle> _compressors = [];

    internal PilotCallResult ActivateCompression(PilotCallContext context, string method, object?[] args)
    {
        if (compression is null)
        {
            return PilotCallResult.Unsupported;
        }
        var flight = context.Flight;
        var pilot = context.Pilot;
        try
        {
            if (method == "Deactivate" && args is [long stop, _])
            {
                compression.Stop(pilot.PilotId, pilot.ShipId, stop);
                AdvanceCompressors(context);
                return PilotCallResult.None;
            }
            if (
                method != "Activate"
                || args is not [long module, _, var target, long repeats]
                || target is not (null or 0L) && !Equals(target, pilot.ShipId)
                || repeats is < 0 or > int.MaxValue
                || !flight.CanUseSelfModules
                || flight.InWarp
                || flight.PreparingWarp
                || flight.Cloaked
                || flight.MicroJumpSpooling
            )
            {
                return new PilotCallResult.Failure("Activate a fitted compressor on your own Rorqual outside warp.");
            }
            flight.Checkpoint();
            var result = compression.Activate(pilot.PilotId, pilot.ShipId, module);
            context.NotificationsBeforeFlight.Add(
                new ModuleAttributesChanged([
                    new(pilot.PilotId, new DogmaItemKey.Item(pilot.ShipId), DogmaAttributeId.CapacitorCharge, Now, result.Capacitor),
                ])
            );
            AdvanceCompressors(context);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private PilotCallResult Compress(PilotCallContext context, object?[] args, bool plain)
    {
        if (compression is null)
        {
            return PilotCallResult.Unsupported;
        }
        var flight = context.Flight;
        if (
            !plain
            || args is not [long item, long facility]
            || item <= 0
            || facility <= 0
            || !flight.CanUseSelfModules
            || flight.InWarp
            || flight.PreparingWarp
            || flight.Cloaked
            || flight.MicroJumpSpooling
        )
        {
            return new PilotCallResult.Failure("Choose an owned resource stack and a nearby compressor while in space.");
        }
        try
        {
            flight.Checkpoint();
            var result = compression.Compress(context.Pilot.PilotId, context.Pilot.ShipId, item, facility);
            InventoryChangesWire.NotifyItemChange(context, result.Change, beforeFlight: true);
            return new StructureCompressionReply(result);
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private void AdvanceCompressors(PilotCallContext context)
    {
        if (!context.Selected)
        {
            _compressors = [];
            return;
        }
        IReadOnlyList<ShipCompressorCycle> current =
            context.Pilot.ShipTypeId == UtilityDroneRules.Rorqual ? travel.Compressors(context.Pilot.ShipId) : [];
        foreach (var ended in _compressors.Except(current))
        {
            context.NotificationsBeforeFlight.Add(new ShipEffectChanged(Effect(ended), Now, false, false, Now));
        }
        foreach (var started in current.Except(_compressors))
        {
            context.NotificationsBeforeFlight.Add(new ShipEffectChanged(Effect(started), Now, true, true));
        }
        _compressors = current;
        context.Flight.RefreshCompressionFacilities(
            current
                .Select(cycle => cycle.Facility)
                .GroupBy(facility => facility.TypeList)
                .Select(group => group.MaxBy(facility => facility.Range)!)
                .OrderBy(facility => facility.TypeList)
                .ToArray()
        );
    }

    private static DogmaActiveEffect Effect(ShipCompressorCycle cycle) =>
        new(
            cycle.ModuleId,
            cycle.PilotId,
            cycle.ShipId,
            null,
            DogmaEffect.IndustrialItemCompression,
            cycle.StartedAt,
            ShipCompressionRules.CycleMilliseconds,
            0
        );
}

internal sealed partial class FlightSession
{
    private IReadOnlyList<ShipCompressionFacility> _compressionFacilities = [];

    internal void RefreshCompressionFacilities(ShipCompressionFacility[] facilities)
    {
        if (_compressionFacilities.SequenceEqual(facilities))
        {
            return;
        }
        _compressionFacilities = facilities;
        if (InBallpark)
        {
            var contact = CreateContact();
            Update(new DestinyCommand.OnSlimItemChange(Pilot.ShipId, new ShipSlimItem(contact)));
            space.Publish(contact);
        }
    }
}
