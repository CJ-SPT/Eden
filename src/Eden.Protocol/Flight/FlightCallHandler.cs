using Eden.Gameplay.Bookmarks;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Flight;

[Injectable]
internal sealed class FlightCallHandler(ISystemObjects systems, IPilotBookmarks bookmarks) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        var request = FlightRequestWire.Read(service, method, args, keywords);
        if (request.Command is not { } command)
        {
            if (request.Checkpoint)
            {
                context.Flight.Checkpoint();
            }
            return PilotCallResult.Unsupported;
        }
        if (command is FlightCommand.WarpToBookmark warp)
        {
            try
            {
                if (keywords?.Keys.Any(key => key is not ("minRange" or "fleet")) == true)
                {
                    throw new InvalidDataException("Unexpected bookmark warp arguments.");
                }
                // Build 3662168's WarpToBookmark always forwards fleet=False for personal warps.
                if (keywords?.TryGetValue("fleet", out var fleet) == true)
                {
                    if (fleet is true or 1L)
                    {
                        throw new InvalidDataException("Fleet bookmark warp is not supported.");
                    }
                    if (fleet is not (false or 0L))
                    {
                        throw new InvalidDataException("Invalid bookmark warp fleet flag.");
                    }
                }
                var bookmark = bookmarks.Find(context.Pilot.PilotId, warp.BookmarkId);
                if (bookmark.Target.LocationId != context.Pilot.SystemId)
                {
                    throw new InvalidDataException("Travel to the bookmarked solar system before warping to this location.");
                }
                if (bookmark.Target.ItemId is { } item)
                {
                    if (!systems.Load(context.Pilot.SystemId).Any(body => body.Id == item))
                    {
                        throw new InvalidDataException("This bookmark is a map destination rather than a warp target.");
                    }
                    command = new FlightCommand.WarpTo(item, warp.Range);
                }
                else
                {
                    // Negative order IDs keep point bookmarks distinct from item
                    // IDs when the docking navigator repeats an active warp request.
                    command = new FlightCommand.WarpToPoint(-bookmark.Id, bookmark.Target.Position, warp.Range);
                }
            }
            catch (InvalidDataException error)
            {
                return new PilotCallResult.Failure(error.Message);
            }
        }
        return command is FlightCommand.Jump jump ? context.Flight.StartJump(jump, systems) : context.Flight.Execute(command);
    }
}
