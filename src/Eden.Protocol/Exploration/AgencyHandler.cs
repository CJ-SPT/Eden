using Eden.Gameplay.Exploration;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Exploration;

[Injectable]
internal sealed class AgencyHandler(IAgencyDirectory directory, TimeProvider clock) : ISelectedPilotCallHandler
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
                    "dungeonInstanceCacheMgr",
                    "GetSignatureInstances"
                        or "GetIceBeltInstances"
                        or "GetIceBeltsCount"
                        or "GetOreAnomalyInstances"
                        or "GetOreAnomaliesCount"
                        or "GetOreAnomaliesCountInRange"
                        or "GetBulwarkMiningSiteCounts"
                        or "GetFactionWarfareInstances"
                        or "GetFactionWarfareBattlefieldInstances"
                        or "GetHomefrontSiteInstances"
                        or "GetPirateInsurgencyInstances"
                        or "GetPirateInsurgencyIceHeistInstances"
                        or "GetTriglavianSiteInstances"
                )
                or
                ("custom_agency_provider", "get_content_data")
                or
                ("map", "GetIncursionGlobalReport" or "GetSystemsInIncursions")
                or
                ("dynamicResourceCacheMgr", "GetESSAgencyData" or "GetDynamicResourceSettings" or "GetDBSMapData")
            )
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (args.Length != 0 || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Agency directory requests do not accept arguments.");
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        return method switch
        {
            "GetSignatureInstances" => new AgencyCounts(directory.Signatures(now)),
            "GetIceBeltInstances" => new AnomalyDirectory(directory.Ice(now).Known),
            "GetIceBeltsCount" => new AgencyCounts(directory.Ice(now).Available),
            "GetDynamicResourceSettings" => new AgencyEssSettings(),
            "get_content_data" or "GetIncursionGlobalReport" => BlueEmpty.List,
            "GetSystemsInIncursions" => new CompatibilityRowset(["locationID", "sceneType", "templateNameID"], []),
            // No corresponding shared sites, custom campaigns, incursions or ESS
            // banks exist in Eden. Preserve each native provider's collection shape.
            _ => BlueEmpty.IndexedDictionary,
        };
    }
}

internal sealed record AgencyCounts(IReadOnlyList<AgencySystemCount> Systems) : BlueModel
{
    public override object ToBlue() => Systems.ToDictionary(s => (long)s.SystemId, s => (object?)s.Count);
}
