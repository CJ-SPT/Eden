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
        if (service == "devIndexManager" && method == "GetAllDevelopmentIndices")
        {
            return args.Length == 0 && keywords is not { Count: > 0 }
                ? new AllSovereigntyDevelopmentReply(entosis.AllDevelopment())
                : new PilotCallResult.Failure("Unexpected development-index arguments.");
        }
        if (
            service != "allianceRegistry"
            || method
                is not (
                    "GetPrimeTimeInfo"
                    or "SetPrimeHour"
                    or "GetAllianceSovereigntyStructuresInfo"
                    or "GetCapitalSystemInfo"
                    or "SetCapitalSystem"
                    or "CancelCapitalSystemTransition"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected sovereignty schedule arguments.");
            }
            if (method == "SetCapitalSystem" && args is [long system] && system is > 0 and <= int.MaxValue)
            {
                entosis.SetCapital(context.Pilot.PilotId, (int)system);
                context.Notifications.Add(new SovereigntyCapitalInfoUpdated());
                return PilotCallResult.None;
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
                "GetCapitalSystemInfo" => new SovereigntyCapitalReply(entosis.Capital(context.Pilot.PilotId)),
                "CancelCapitalSystemTransition" => CancelCapital(context),
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

    private PilotCallResult CancelCapital(PilotCallContext context)
    {
        entosis.CancelCapital(context.Pilot.PilotId);
        context.Notifications.Add(new SovereigntyCapitalInfoUpdated());
        return PilotCallResult.None;
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
                ["campaignOccupancyLevel"] = (double)(Hub.Conflict?.CampaignMultiplier ?? 1m),
                ["vulnerabilityOccupancyLevel"] = (double)(Hub.Conflict?.WindowMultiplier ?? 1m),
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

internal sealed record SovereigntyCapitalReply(SovereigntyCapital Capital) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["currentCapitalSystem"] = Capital.CurrentSystemId,
                ["newCapitalSystem"] = Capital.NewSystemId,
                ["newCapitalSystemValidAfter"] = Capital.ValidAfter,
            }
        );
}

internal sealed record AllSovereigntyDevelopmentReply(IReadOnlyList<SovereigntyDevelopment> Systems) : BlueModel
{
    public override object ToBlue() =>
        new BlueList<SovereigntyDevelopmentRow>(Systems.Select(value => new SovereigntyDevelopmentRow(value)).ToArray());
}

internal sealed record SovereigntyDevelopmentRow(SovereigntyDevelopment Development) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["solarSystemID"] = Development.SystemId,
                ["militaryPoints"] = (long)Development.MilitaryPoints,
                ["industrialPoints"] = (long)Development.IndustrialPoints,
                ["claimedFor"] = Development.ClaimedDays,
            }
        );
}

internal sealed record SovereigntyCapitalInfoUpdated() : PilotNotification("OnUpdateCapitalInfo")
{
    public override object?[] Arguments => [];
}

internal sealed record SovereigntyCapitalChanged(int AllianceId, int? Before, int? After) : PilotNotification("OnCapitalSystemChanged")
{
    public override object?[] Arguments => [new object?[] { AllianceId, Before, After }];
}

internal sealed record SovereigntyDevelopmentChanged(int SystemId) : PilotNotification("OnSolarSystemDevIndexChanged")
{
    public override object?[] Arguments => [SystemId];
}
