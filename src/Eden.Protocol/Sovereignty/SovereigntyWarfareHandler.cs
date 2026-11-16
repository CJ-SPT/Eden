using Eden.Gameplay.Sovereignty;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Sovereignty;

[Injectable]
internal sealed class SovereigntyWarfareHandler(IPilotEntosis entosis) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "allianceRegistry" || method is not ("GetPrimeTimeInfo" or "SetPrimeHour" or "GetAllianceSovereigntyStructuresInfo"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected sovereignty schedule arguments.");
            }
            if (method == "SetPrimeHour" && args is [long hour] && hour is >= 0 and < 24)
            {
                entosis.SetPrimeHour(context.Pilot.PilotId, (int)hour);
                return PilotCallResult.None;
            }
            if (args.Length != 0)
            {
                throw new InvalidDataException("Invalid sovereignty schedule arguments.");
            }
            return method switch
            {
                "GetPrimeTimeInfo" => new SovereigntyPrimeReply(entosis.PrimeTime(context.Pilot.PilotId)),
                "GetAllianceSovereigntyStructuresInfo" => new AllianceSovereigntyReply(entosis.AllianceHubs(context.Pilot.PilotId)),
                _ => new PilotCallResult.Failure("Choose a UTC prime hour between zero and 23."),
            };
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record SovereigntyPrimeReply(AlliancePrimeTime Prime) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["currentPrimeHour"] = Prime.CurrentHour,
                ["newPrimeHour"] = Prime.NewHour,
                ["newPrimeHourValidAfter"] = Prime.ValidAfter,
            }
        );
}

internal sealed record AllianceSovereigntyReply(IReadOnlyList<SovereigntyHub> Hubs) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            BlueEmpty.List,
            new BlueList<AllianceSovereigntyRow>(Hubs.Select(hub => new AllianceSovereigntyRow(hub)).ToArray()),
            new BlueList<SovereigntyCampaignScore>(
                Hubs.Where(hub => hub.Conflict?.CampaignAt is not null)
                    .SelectMany(hub =>
                        new SovereigntyCampaignScore[]
                        {
                            new(hub.HubId, hub.Claim!.AllianceId, hub.Conflict!.DefenderPoints / 100d),
                            new(hub.HubId, SovereigntyWarfareRules.Attackers, 1 - hub.Conflict!.DefenderPoints / 100d),
                        }
                    )
                    .ToArray()
            ),
        };
}

internal sealed record AllianceSovereigntyRow(SovereigntyHub Hub) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["structureID"] = Hub.HubId,
                ["solarSystemID"] = Hub.SystemId,
                ["corporationID"] = Hub.CorporationId,
                ["campaignStartTime"] = Hub.Conflict?.CampaignAt,
                ["campaignEventType"] = Hub.Conflict?.CampaignAt is null ? null : SovereigntyWarfareRules.HubDefenseEvent,
                ["campaignOccupancyLevel"] = 1d,
                ["vulnerabilityOccupancyLevel"] = 1d,
                ["vulnerableStartTime"] = Hub.Conflict?.Window.StartsAt,
                ["vulnerableEndTime"] = Hub.Conflict?.Window.EndsAt,
            }
        );
}

internal sealed record SovereigntyCampaignScore(long HubId, int TeamId, double Score) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["sourceItemID"] = HubId,
                ["teamID"] = TeamId,
                ["score"] = Score,
            }
        );
}
