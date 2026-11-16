using Eden.Gameplay.Dogma;
using Eden.Gameplay.Sovereignty;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Sovereignty;

namespace Eden.Protocol.Maps;

[Injectable]
internal sealed class SovereigntyHandler(IPilotEntosis entosis) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            (service, method)
            is not (

                (
                    "sovMgr",
                    "GetSovStructuresInfoForSolarSystem"
                        or "GetSovStructuresInfoForLocalSolarSystem"
                        or "GetInfrastructureHubInfo"
                        or "GetSystemSovereigntyInfo"
                )
                or
                ("devIndexManager", "GetDevelopmentIndicesForSystem")
            )
        )
        {
            return PilotCallResult.Unsupported;
        }

        bool local = method == "GetSovStructuresInfoForLocalSolarSystem";
        bool validArguments = local ? args.Length == 0 : args is [long systemId] && systemId is > 0 and <= int.MaxValue;
        if (!validArguments || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Choose a valid solar system for sovereignty information.");
        }

        int requested = local ? context.Pilot.SystemId : checked((int)(long)args[0]!);
        var hub = entosis.Hub(requested);
        return method switch
        {
            "GetSovStructuresInfoForSolarSystem" or "GetSovStructuresInfoForLocalSolarSystem" => new SovereigntyStructuresReply(hub),
            "GetInfrastructureHubInfo" or "GetSystemSovereigntyInfo" when hub?.Claim is { } claim => new SovereigntyClaimReply(
                claim,
                method == "GetInfrastructureHubInfo"
            ),
            "GetDevelopmentIndicesForSystem" => new BlueMap<DogmaAttributeId, SovereigntyDevelopmentIndexReply>(
                new Dictionary<DogmaAttributeId, SovereigntyDevelopmentIndexReply>
                {
                    [DogmaAttributeId.DevelopmentIndexMilitary] = new(0, false),
                    [DogmaAttributeId.DevelopmentIndexIndustrial] = new(0, false),
                }
            ),
            _ => PilotCallResult.None,
        };
    }
}

internal sealed record SovereigntyDevelopmentIndexReply(long Points, bool Increasing) : BlueModel
{
    // sovSvc.GetLevelInfoForIndex reads these KeyVal attributes, not a positional row.
    public override object ToBlue() =>
        new BlueInstance("utillib.KeyVal", new Dictionary<string, object?> { ["points"] = Points, ["increasing"] = Increasing });
}
