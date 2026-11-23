using System.Text.RegularExpressions;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerInspection
{
    private static MinerProgress? Miner(LiveWorld live, WorldAction world, long id) =>
        live.MinerCheckpoints.GetValueOrDefault(id)?.Work
        ?? MinerCheckpoint.Read(world.Filter<SavedCapsuleerCommunity>(row => row.PilotId == id).FirstOrDefault()?.Progress);

    private static HaulerProgress? Hauler(LiveWorld live, WorldAction world, long id) =>
        live.HaulerCheckpoints.GetValueOrDefault(id)?.Work
        ?? HaulerCheckpoint.Read(world.Filter<SavedCapsuleerHauler>(row => row.PilotId == id).FirstOrDefault()?.Progress);

    private static CapsuleerWorkInspection Work(MinerProgress? miner, HaulerProgress? hauler, CapsuleerOrganization organization)
    {
        string activity =
            miner is not null ? Words(miner.Activity)
            : hauler is not null ? Words(hauler.Activity)
            : "Awaiting work";
        return (miner, hauler, organization) switch
        {
            (_, { EscapingBubble: true } or { Retreating: true }, _) or ({ EscapingBubble: true }, _, _) => new(activity, "Escape danger"),
            (_, { SovereigntyExpedition: { } expedition }, _) => SovereigntyWork(activity, expedition),
            (_, { RegionalEscort.Operation: { } escort }, _) => new(activity, Words(escort.Objective), SystemId: escort.TargetSystemId),
            (_, { Gang: { Phase: not RoamingGangPhase.Complete } gang }, _) => new(
                $"Roaming gang · {Words(gang.Phase)}",
                gang.Reason,
                gang.DestinationStationId,
                gang.Regional?.TargetSystemId
            ),
            (_, { MilitiaPatrol: { } patrol }, _) => new(
                $"Militia patrol · {Words(patrol.Phase)}",
                $"Contest a {Words(patrol.Site.Family)} complex",
                patrol.StagingStationId > 0 ? patrol.StagingStationId : patrol.HomeStationId,
                patrol.SystemId
            ),
            (_, { Journey: { Kind: TrafficJourneyKind.NullsecExpedition } expedition } traveler, _) => new(
                traveler.RouteDirection < 0 ? "Returning from nullsec" : activity,
                expedition.NullsecWork == NullsecExpeditionWork.Ratting ? "Rat in nullsec" : "Roam in nullsec",
                expedition.DestinationStationId,
                traveler.RouteDirection > 0 && expedition.Systems.Length > 0 ? expedition.Systems[^1] : null
            ),
            (_, { Ratting: { } ratting }, _) => new($"Bounty hunting · {Words(ratting.Phase)}", StationId: ratting.StationId),
            (_, { Exploration: { } exploration }, _) => new($"Exploration · {Words(exploration.Phase)}", StationId: exploration.StationId),
            (_, { Resupply: { } resupply }, _) => new(activity, "Resupply ship", resupply.DestinationStationId ?? resupply.StationId),
            (_, _, { Relocation: { } relocation }) => new(activity, "Relocate for corporation", relocation.DestinationId),
            (_, { CorporationSupply.FactorySupply: { } shipment }, _) => new(activity, "Supply factory", shipment.FactoryStationId),
            (_, { CorporationSupply: { } supply } delivery, _) => new(
                activity,
                $"Fulfill corporation order {supply.OrderId}",
                delivery.DestinationStationId > 0 ? delivery.DestinationStationId : null
            ),
            (_, { Freight: { } freight }, _) => new(activity, "Deliver freight", freight.Destination),
            (_, { PlanetaryProduction: { } planet }, _) => new(activity, "Produce planetary materials", planet.FactoryStationId),
            ({ FactoryStage: { } stage }, _, _) => new(activity, "Stage factory materials", stage.StationId),
            ({ Mercoxit: { } mercoxit }, _, _) => new(activity, "Mercoxit expedition", mercoxit.StationId),
            ({ MoonExtractionId: long extraction }, _, _) => new(activity, $"Mine moon extraction {extraction}"),
            ({ FactorySupply: { } factory }, _, _) => new(activity, "Source factory materials", factory.StationId),
            (_, { FactorySupply: { } factory }, _) => new(activity, "Source factory materials", factory.StationId),
            (_, { Journey: { } journey }, _) => new(activity, Words(journey.Kind), journey.DestinationStationId),
            ({ ReplacementOrderDeadline: not null }, _, _) or (_, { ReplacementOrderDeadline: not null }, _) => new(
                activity,
                "Acquire replacement ship"
            ),
            ({ WorkingBelt: > 0 }, _, _) => new(activity, $"Work asteroid belt {miner.WorkingBelt}"),
            (_, { DestinationStationId: > 0 } route, _) => new(activity, StationId: route.DestinationStationId),
            _ => new(activity),
        };
    }

    private static CapsuleerWorkInspection SovereigntyWork(string activity, SovereigntyExpedition expedition)
    {
        string objective = expedition.Duty switch
        {
            SovereigntyDuty.Claim => "Claim sovereignty",
            SovereigntyDuty.Defend => "Defend sovereignty",
            SovereigntyDuty.ReagentDelivery => "Deliver sovereignty reagents",
            _ => Words(expedition.Duty),
        };
        return new(
            activity,
            expedition.Returning ? $"Returning from assignment · {objective}" : objective,
            SystemId: expedition.TargetSystemId
        );
    }

    private static string Words<T>(T value)
        where T : struct, Enum => WordBoundary().Replace(value.ToString(), " ");

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex WordBoundary();
}
