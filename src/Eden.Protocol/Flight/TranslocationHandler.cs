using System.Globalization;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
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
                || !long.TryParse(destination, NumberStyles.None, CultureInfo.InvariantCulture, out long targetId)
                || targetId <= 0
            )
            {
                throw new InvalidDataException("Use /tr me followed by a supported destination ID.");
            }
            var target = travel.Resolve(targetId);
            var destinationObjects = objects.Load(target.System.Id);
            if (target.StationId is null)
            {
                _ = TranslocationRules.Arrival(target, context.Pilot.Flight.ShipRadius, destinationObjects);
            }
            // Cancel old activities before committing the move. Their checkpoints must
            // never write a source-system position over the committed destination.
            updates.PrepareTranslocation(context);
            // A docked pilot enters the destination through StationToSpaceTransition.
            // A synthetic source undock followed by JumpDriveOut can strand CAM_JUMP,
            // whose locked camera rejects both mouse controls and camera shortcuts.
            if (context.Pilot.Position is not null && context.Pilot.SystemId != target.System.Id && target.StationId is null)
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
            var moved = travel.Move(context.Pilot.PilotId, context.Pilot.ShipId, target);
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
