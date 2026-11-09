using Eden.Gameplay.Bookmarks;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Fleets;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Flight;

[Injectable]
internal sealed class FlightCallHandler(
    ISystemObjects systems,
    IPilotBookmarks bookmarks,
    CooperativeFleetHandler fleets,
    ICombatAnomalies anomalies,
    TimeProvider clock,
    IPilotProbeScanning? scanning = null
) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service == "beyonce" && method == "GetCynoJammerState")
        {
            if (args.Length != 0 || keywords is { Count: > 0 })
            {
                return new PilotCallResult.Failure("Cyno jammer state does not accept arguments.");
            }
            // Build 3662168 CynoJammerIcon uses None for no jammer; zero means online.
            // Eden has no deployed jammers. Handle this read before movement checkpointing.
            return PilotCallResult.None;
        }
        if (service == "beyonce" && method == "GetFleetLocationAndInfo")
        {
            return fleets.Locations(context, args, keywords);
        }
        var request = FlightRequestWire.Read(service, method, args, keywords);
        if (request.Command is not { } command)
        {
            if (request.Checkpoint)
            {
                context.Flight.Checkpoint();
            }
            return PilotCallResult.Unsupported;
        }
        if (
            command is FlightCommand.WarpToScan
            || command is FlightCommand.WarpTo { TargetId: >= CombatAnomalyRules.FirstSiteId and < CombatAnomalyRules.FirstObjectId }
        )
        {
            var site = anomalies
                .List(context.Pilot.SystemId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc())
                .Concat(anomalies.Signatures(context.Pilot.SystemId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()))
                .SingleOrDefault(s =>
                    command is FlightCommand.WarpToScan scan
                        ? scan.TargetId == s.TargetId
                        : command is FlightCommand.WarpTo item && item.TargetId == s.Id
                );
            if (site is null)
            {
                return new PilotCallResult.Failure("This combat anomaly is no longer available in this system.");
            }
            if (
                site.IsSignature
                && scanning?.Resolved(context.Pilot.PilotId, site.Id, context.Pilot.SystemId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc())
                    != true
            )
            {
                return new PilotCallResult.Failure("Resolve this cosmic signature to 100% before warping to it.");
            }
            double range = command is FlightCommand.WarpToScan scanWarp ? scanWarp.Range : ((FlightCommand.WarpTo)command).Range;
            var anchor = context.Pilot.Position is { } origin
                ? SiteWarpInRules.ResolveAnchor(
                    context.Pilot.ShipId,
                    context.Pilot.Flight.ShipRadius,
                    origin,
                    site.Position,
                    range,
                    site.Objects
                )
                : site.Position;
            command = new FlightCommand.WarpToPoint(site.Id, anchor, range);
        }
        if (command is FlightCommand.WarpToFleetMember memberWarp)
        {
            try
            {
                command = fleets.MemberWarp(context, memberWarp);
            }
            catch (InvalidDataException error)
            {
                return new PilotCallResult.Failure(error.Message);
            }
        }
        if (command is FlightCommand.WarpToBookmark warp)
        {
            try
            {
                if (keywords?.Keys.Any(key => key is not ("minRange" or "fleet" or "fleetFormationSettings")) == true)
                {
                    throw new InvalidDataException("Unexpected bookmark warp arguments.");
                }
                // Build 3662168's WarpToBookmark always forwards fleet=False for personal warps.
                if (keywords?.TryGetValue("fleet", out var fleet) == true)
                {
                    if (fleet is not (false or 0L or true or 1L))
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
        if (command is FlightCommand.WarpTo or FlightCommand.WarpToPoint)
        {
            if (keywords?.GetValueOrDefault("fleet") is true or 1L)
            {
                return fleets.Warp(context, command, keywords?.GetValueOrDefault("fleetFormationSettings"));
            }
            if (keywords?.GetValueOrDefault("fleet") is not (null or false or 0L))
            {
                return new PilotCallResult.Failure("Invalid fleet warp flag.");
            }
        }
        if (command is not FlightCommand.RefreshState)
        {
            fleets.CancelWarpOrders(context.Pilot.PilotId);
        }
        return command is FlightCommand.Jump jump ? context.Flight.StartJump(jump, systems) : context.Flight.Execute(command);
    }
}
