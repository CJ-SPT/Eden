using System.Globalization;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Development;

[Injectable]
internal sealed class DeveloperTravelCommands(IPilotTranslocation travel, ISystemObjects objects, PilotUpdateCoordinator updates)
{
    internal PilotCallResult Execute(PilotCallContext context, string[] command)
    {
        if (context.ControlledStructureId.HasValue)
        {
            return new PilotCallResult.Failure("Release structure control before using developer travel.");
        }
        switch (command[0].ToLowerInvariant())
        {
            case "/getpos" when command.Length == 1:
                return new PositionReply(Position(context));
            case "/reportdesync" when command.Length == 1:
                return context.Flight.ReadDesyncReport();
            case "/stop" when command.Length == 1:
                return context.Flight.Execute(new FlightCommand.Stop());
            case "/warptopoint" when command is [_, var x, var y, var z]:
                _ = Position(context);
                return context.Flight.Execute(new FlightCommand.WarpToPoint(-context.Pilot.ShipId, Point([x, y, z]), 0));
            case "/tr":
                return Move(context, Resolve(context, command));
            default:
                throw new InvalidDataException("Use /getpos, /reportdesync, /stop, or /warptopoint x y z.");
        }
    }

    private TranslocationTarget Resolve(PilotCallContext context, string[] command) =>
        command switch
        {
            [_, var subject, var value] when Self(subject) => ResolveDestination(context, value),
            [_, var subject, var destination, var offset] when Self(subject) && Self(destination) && IsOffset(offset) => Coordinates(
                context,
                Position(context) + Point(offset[7..].Split(','))
            ),
            _ => throw new InvalidDataException("Use /tr me followed by a destination ID, pos=x,y,z, or offset=x,y,z."),
        };

    private TranslocationTarget ResolveDestination(PilotCallContext context, string value)
    {
        if (IsOffset(value))
        {
            return Coordinates(context, Position(context) + Point(value[7..].Split(',')));
        }
        if (value.StartsWith("pos=", StringComparison.OrdinalIgnoreCase))
        {
            return Coordinates(context, Point(value[4..].Split(',')));
        }
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long targetId) && targetId > 0)
        {
            return travel.Resolve(targetId);
        }
        throw new InvalidDataException("Supply a destination ID or three finite coordinates.");
    }

    internal PilotCallResult Move(PilotCallContext context, TranslocationTarget target)
    {
        var destinationObjects = objects.Load(target.System.Id);
        if (!target.Docked)
        {
            _ = TranslocationRules.Arrival(target, context.Pilot.Flight.ShipRadius, destinationObjects);
        }
        // Cancel old activities before committing the move. Their checkpoints must
        // never write a source-system position over the committed destination.
        updates.PrepareTranslocation(context);
        // A docked pilot enters the destination through StationToSpaceTransition.
        // A synthetic source undock followed by JumpDriveOut can strand CAM_JUMP,
        // whose locked camera rejects both mouse controls and camera shortcuts.
        if (context.Pilot.Position is not null && context.Pilot.SystemId != target.System.Id && !target.Docked)
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

    private static bool IsOffset(string value) => value.StartsWith("offset=", StringComparison.OrdinalIgnoreCase);

    private static bool Self(string value) => value.Equals("me", StringComparison.OrdinalIgnoreCase);

    private static SpacePosition Position(PilotCallContext context) =>
        context.Pilot.Position ?? throw new InvalidDataException("Undock to use your ship position.");

    private static TranslocationTarget Coordinates(PilotCallContext context, SpacePosition point) =>
        new(context.Pilot.ShipId, context.Pilot.CurrentSystem ?? context.Pilot.Home.System, null, point, 0) { Coordinate = true };

    private static SpacePosition Point(string[] values)
    {
        if (values is [var x, var y, var z] && Number(x, out double a) && Number(y, out double b) && Number(z, out double c))
        {
            return new(a, b, c);
        }
        throw new InvalidDataException("Supply three finite coordinates.");
    }

    private static bool Number(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private sealed record PositionReply(SpacePosition Position) : BlueModel
    {
        public override object ToBlue() => new object?[] { Position.X, Position.Y, Position.Z };
    }
}
