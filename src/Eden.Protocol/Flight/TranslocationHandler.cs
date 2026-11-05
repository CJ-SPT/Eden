using System.Globalization;
using Eden.Gameplay.Flight;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Flight;

[Injectable]
internal sealed class TranslocationHandler(IPilotTranslocation travel, ISystemObjects objects, PilotUpdateCoordinator updates)
    : ISelectedPilotCallHandler
{
    // Match the stock GM menu's ROLE_GML | ROLE_WORLDMOD visibility gate.
    private const PilotRole AllowedRoles = PilotRole.GameMasterLow | PilotRole.WorldMod;
    private const int MaximumCommandLength = 128;

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "slash" || method != "SlashCmd")
        {
            return PilotCallResult.Unsupported;
        }
        if ((context.Roles & (long)AllowedRoles) == 0)
        {
            return new PilotCallResult.Failure("GM translocation requires a developer or game-master account.");
        }
        try
        {
            if (context.Flight.Translocating)
            {
                throw new InvalidDataException("Wait for the current translocation to finish.");
            }
            if (args is not [string or byte[]] || keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Use /tr me followed by a destination ID.");
            }
            string text = BlueMarshal.Text(args[0]);
            if (text.Length > MaximumCommandLength)
            {
                throw new InvalidDataException("The translocation command is too long.");
            }
            string[] command = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (
                command is not [var action, var subject, var destination]
                || !action.Equals("/tr", StringComparison.OrdinalIgnoreCase)
                || !subject.Equals("me", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(destination, NumberStyles.None, CultureInfo.InvariantCulture, out int targetId)
                || targetId <= 0
            )
            {
                throw new InvalidDataException("Use /tr me followed by a solar system, NPC station or static celestial ID.");
            }
            var target = travel.Resolve(targetId);
            var destinationObjects = objects.Load(target.System.Id);
            if (target.StationId is null)
            {
                _ = TranslocationRules.Arrival(target, context.Pilot.Flight.ShipRadius, destinationObjects);
            }
            // Cancel old activities before committing the move. Their checkpoints must
            // never write a source-system position over the committed destination.
            updates.Leave(context);
            if (context.Pilot.Position is null && context.Pilot.SystemId != target.System.Id && target.StationId is null)
            {
                var undock = context.Flight.Execute(new FlightCommand.Undock(context.Pilot.ShipId));
                if (undock is not PilotCallResult.Success)
                {
                    return undock;
                }
                context.Flight.QueueTranslocationAfterSourceState(travel, target, destinationObjects);
                context.ReleaseLocationReferences();
                updates.Enter(context);
                return PilotCallResult.None;
            }
            if (context.Pilot.SystemId != target.System.Id && target.StationId is null)
            {
                if (context.Flight.InBallpark)
                {
                    context.Flight.StartTranslocation(travel, target, destinationObjects);
                }
                else
                {
                    context.Flight.QueueTranslocationAfterSourceState(travel, target, destinationObjects);
                }
                return PilotCallResult.None;
            }
            var moved = travel.Move(context.Pilot.PilotId, context.Pilot.ShipId, targetId);
            bool locationChanged = moved.Pilot.LocationId != context.Pilot.LocationId;
            context.Flight.Relocate(moved.Pilot, destinationObjects);
            if (locationChanged)
            {
                context.ReleaseLocationReferences();
            }
            context.Roster = context.Roster.Select(p => p.PilotId == moved.Pilot.PilotId ? moved.Pilot : p).ToArray();
            InventoryChangesWire.NotifyItemChange(context, moved.ShipChange);
            updates.Enter(context);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
