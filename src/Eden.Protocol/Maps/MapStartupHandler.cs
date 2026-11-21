using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Structures;

namespace Eden.Protocol.Maps;

[Injectable]
internal sealed class MapStartupHandler(Eden.Gameplay.Sovereignty.IPilotEntosis entosis, IPilotJumpBridges? bridges = null)
    : ISelectedPilotCallHandler
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

                ("structureDirectory", "GetJumpBridgesWithMyAccess")
                or
                ("stationSvc", "GetAllianceSystems")
                or
                ("solarsystemInterferenceMgr", "GetAllInterferenceBands" or "GetLocalInterferenceState")
            )
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (args.Length != 0 || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Map information does not accept arguments.");
        }

        // No CRAB interference is simulated. Build 3662168 uses an absent system
        // dictionary entry or a None local state as its no-interference fallback.
        return service switch
        {
            "solarsystemInterferenceMgr" when method == "GetLocalInterferenceState" => PilotCallResult.None,
            "structureDirectory" => new JumpBridgeNetworkReply(bridges?.Network(context.Pilot.PilotId) ?? new([], [], [])),
            "stationSvc" => new Eden.Protocol.Sovereignty.AllianceSystemsReply(entosis.Claims()),
            _ => BlueEmpty.IndexedDictionary,
        };
    }
}
