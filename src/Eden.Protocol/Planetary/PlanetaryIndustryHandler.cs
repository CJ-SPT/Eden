using System.Globalization;
using Eden.Gameplay.Planetary;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Planetary;

[Injectable]
internal sealed class PlanetaryIndustryHandler(IPilotPlanetaryIndustry industry) : ISelectedPilotCallHandler
{
    internal const string BindingPrefix = "@planet:";

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool bound =
            service.StartsWith(BindingPrefix, StringComparison.Ordinal)
            && int.TryParse(service.AsSpan(BindingPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out _);
        if (service != "planetMgr" && !bound)
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Unexpected planetary options.");
        }
        long pilot = context.Pilot.PilotId;
        int boundPlanet = bound ? int.Parse(service.AsSpan(BindingPrefix.Length), CultureInfo.InvariantCulture) : 0;
        try
        {
            if (method == "GetPlanetsForChar" && args.Length == 0)
            {
                return new PlanetaryPlanetsReply(industry.List(pilot));
            }
            if (method == "GetMyLaunchesDetails" && args.Length == 0)
            {
                return new PlanetaryLaunchesReply(industry.Launches(pilot));
            }
            if (method == "DeleteLaunch" && args is [long launchId])
            {
                industry.DeleteLaunch(pilot, checked((int)launchId));
                context.Notifications.Add(new PlanetaryLaunchesChanged());
                return PilotCallResult.None;
            }
            if (method is "GetPlanetInfo" or "GetPlanetResourceInfo" or "GetCommandPinsForPlanet" or "GetExtractorsForPlanet")
            {
                int planet =
                    args.Length == 0 && bound ? boundPlanet
                    : args is [long id] ? checked((int)id)
                    : throw new InvalidDataException("Choose one planet.");
                var snapshot = industry.Read(pilot, planet);
                return method switch
                {
                    "GetPlanetInfo" => new PlanetaryPlanetReply(snapshot),
                    "GetPlanetResourceInfo" => new PlanetaryResourceInfoReply(snapshot.Planet),
                    _ => new PlanetaryPublicPinsReply(industry.Networks(planet), method == "GetExtractorsForPlanet"),
                };
            }
            if (method == "GetFullNetworkForOwner" && args is [long planetId, long owner])
            {
                return new PlanetaryPublicNetworkReply(industry.Networks(checked((int)planetId)).SingleOrDefault(c => c.OwnerId == owner));
            }
            if (!bound)
            {
                return PilotCallResult.Unsupported;
            }
            switch (method)
            {
                case "GetResourceData" when args.Length == 1:
                {
                    var fields = PlanetaryRequestWire.Fields(args[0]);
                    int resource = PlanetaryRequestWire.Int32(fields.GetValueOrDefault("resourceTypeID"));
                    int bands = PlanetaryRequestWire.Int32(fields.GetValueOrDefault("newBand"));
                    // Proximity is an inert native cache marker; it cannot select a pilot or change yield.
                    object? proximity = fields.GetValueOrDefault("proximity");
                    if (proximity is not null and not long and not double and not string and not byte[])
                    {
                        throw new InvalidDataException("Invalid planetary scan cache marker.");
                    }
                    return new PlanetaryResourceReply(industry.ResourceField(pilot, boundPlanet, resource, bands));
                }
                case "UserUpdateNetwork" when args.Length == 1:
                {
                    var change = industry.Update(pilot, boundPlanet, PlanetaryRequestWire.Commands(args[0]));
                    Notify(context, change);
                    return new PlanetaryColonyReply(change.Colony);
                }
                case "UserAbandonPlanet" when args.Length == 0:
                    industry.Abandon(pilot, boundPlanet);
                    context.Notifications.Add(new PlanetaryStateChanged(boundPlanet));
                    return PilotCallResult.None;
                case "GetProgramResultInfo" when args.Length == 4:
                    return new PlanetaryQuoteReply(
                        industry.Quote(
                            pilot,
                            boundPlanet,
                            PlanetaryRequestWire.Id(args[0]),
                            PlanetaryRequestWire.Int32(args[1]),
                            PlanetaryRequestWire.Heads(args[2]),
                            PlanetaryRequestWire.Number(args[3])
                        )
                    );
                case "UserTransferCommodities" when args.Length == 2:
                    return new PlanetaryTransferReply(
                        industry.Transfer(pilot, boundPlanet, PlanetaryRequestWire.Path(args[0]), PlanetaryRequestWire.Commodities(args[1]))
                    );
                case "UserLaunchCommodities" when args.Length == 2:
                {
                    var change = industry.Launch(
                        pilot,
                        boundPlanet,
                        PlanetaryRequestWire.Integer(args[0]),
                        PlanetaryRequestWire.Commodities(args[1])
                    );
                    Notify(context, change);
                    context.Notifications.Add(new PlanetaryLaunchesChanged());
                    return change.Colony.SimulatedAt;
                }
                default:
                    return PilotCallResult.Unsupported;
            }
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    internal static void Notify(PilotCallContext context, PlanetaryChange change)
    {
        foreach (var item in change.Items)
        {
            InventoryChangesWire.NotifyItemChange(context, item);
        }
        if (change.BalanceCents is long balance)
        {
            context.Notifications.Add(new AccountBalanceChanged(context.Pilot.PilotId, balance / 100d));
        }
    }
}

internal sealed record PlanetaryStateChanged(int PlanetId) : PilotNotification("OnMajorPlanetStateUpdate")
{
    public override object?[] Arguments => [PlanetId, true];
}

internal sealed record PlanetaryLaunchesChanged() : PilotNotification("OnPILaunchesChange")
{
    public override object?[] Arguments => [];
}
