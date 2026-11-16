using Eden.Gameplay.Sovereignty;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Sovereignty;

internal sealed record SovereigntyLaunchReply(long ItemId, long HubId) : BlueModel
{
    public override object ToBlue() => new BlueMap<long, BlueList<long>>(new Dictionary<long, BlueList<long>> { [ItemId] = new([HubId]) });
}

internal sealed record SovereigntyStructuresReply(SovereigntyHub? Hub) : BlueModel
{
    public override object ToBlue() => Hub is null ? BlueEmpty.List : new BlueList<SovereigntyStructureReply>([new(Hub)]);
}

internal sealed record SovereigntyStructureReply(SovereigntyHub Hub) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["itemID"] = Hub.HubId,
                ["typeID"] = Hub.TypeId,
                ["ownerID"] = Hub.CorporationId,
                ["corporationID"] = Hub.CorporationId,
                ["allianceID"] = Hub.Claim?.AllianceId,
                ["solarSystemID"] = Hub.SystemId,
                ["campaignState"] = null,
                ["vulnerabilityState"] = null,
                ["defenseMultiplier"] = 1d,
                ["isCapital"] = false,
            }
        );
}

internal sealed record SovereigntyClaimReply(SovereigntyClaim Claim, bool HubInfo) : BlueModel
{
    public override object ToBlue() =>
        HubInfo
            ? new BlueConstructor(
                BlueConstructor.SovereigntyHubClass,
                [Claim.HubId, Claim.CorporationId, Claim.AllianceId, Claim.ClaimedAt]
            )
            : new BlueConstructor(BlueConstructor.SovereigntyClaimClass, [Claim.HubId, Claim.CorporationId, Claim.AllianceId]);
}

internal sealed record AllianceSystemsReply(IReadOnlyList<SovereigntyClaim> Claims) : BlueModel
{
    public override object ToBlue() => new BlueList<AllianceSystemReply>(Claims.Select(claim => new AllianceSystemReply(claim)).ToArray());
}

internal sealed record AllianceSystemReply(SovereigntyClaim Claim) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?> { ["solarSystemID"] = Claim.SystemId, ["allianceID"] = Claim.AllianceId }
        );
}

internal sealed record SovereigntyStructuresUpdated(int SystemId, SovereigntyHub? Hub)
    : PilotNotification("OnSolarSystemSovStructuresUpdated")
{
    public override object?[] Arguments => [SystemId, new SovereigntyStructuresReply(Hub), null];
}

internal sealed record SovereigntyStatusChanged(int SystemId, SovereigntyClaim? Claim) : PilotNotification("ProcessSovStatusChanged")
{
    public override object?[] Arguments => [SystemId, Claim is null ? null : new SovereigntyClaimReply(Claim, false)];
}
