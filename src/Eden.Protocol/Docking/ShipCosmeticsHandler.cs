using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class ShipCosmeticsHandler : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        // The remote API spells these two methods "Licenced" in build 3662168;
        // the local cosmetics service exposes GetLicensedSkins instead.
        if (service != "shipCosmeticsMgr" || method is not ("GetLicencedSkins" or "GetLicencedSkinsForShipType" or "GetFirstPartySkinData"))
        {
            return PilotCallResult.Unsupported;
        }
        bool validArguments = method switch
        {
            "GetLicencedSkins" => args.Length == 0,
            "GetLicencedSkinsForShipType" => args is [long pilotId, long typeId]
                && pilotId == context.Pilot.PilotId
                && typeId is > 0 and <= int.MaxValue,
            // Applied-skin previews can ask about another licensee. There is no
            // licensed/applied skin to return for any licensee in the current world.
            _ => args is [long licenseeId, long skinId] && licenseeId > 0 && skinId is > 0 and <= int.MaxValue,
        };
        if (!validArguments || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Choose valid pilot and skin information.");
        }

        // No saved SKIN entitlements exist yet. The panel still builds its
        // unlicensed catalog from static client data when the license list is empty.
        return method == "GetFirstPartySkinData" ? PilotCallResult.None : BlueEmpty.List;
    }
}
