using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Sovereignty;
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
        if ((service, method) == ("stationSvc", "GetSystemsForAlliance"))
        {
            if (keywords is { Count: > 0 })
            {
                return new PilotCallResult.Failure("Unexpected alliance systems options.");
            }
            // Build 3662168 FormAlliancesSystems.Load passes session.allianceid to this lookup.
            return args switch
            {
                [] => new AllianceSystemsReply(
                    context.Pilot.Identity.AllianceId is > 0 and var alliance ? entosis.ClaimsForAlliance(alliance) : []
                ),
                [null or 0L] => new AllianceSystemsReply([]),
                [long id] when id is > 0 and <= int.MaxValue => new AllianceSystemsReply(entosis.ClaimsForAlliance((int)id)),
                _ => new PilotCallResult.Failure("Choose a valid alliance ID."),
            };
        }
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
            "stationSvc" => new AllianceSystemsReply(entosis.Claims()),
            _ => BlueEmpty.IndexedDictionary,
        };
    }
}
