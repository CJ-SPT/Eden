using Eden.Gameplay.FactionWarfare;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.FactionWarfare;

[Injectable]
internal sealed class FactionWarzoneHandler(IFactionWarzones warzones) : ISelectedPilotCallHandler
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
                    "fwWarzoneSolarsystem",
                    "GetAllWarzones"
                        or "GetHQSystemIDs"
                        or "GetAllWarzonesOccupationStates"
                        or "GetAllWarzonesOccupationStatesUncached"
                        or "GetLocalOccupationState"
                )
                or
                ("fwVictoryPointMgr", "GetAllVictoryPointStates" or "GetLocalVictoryPointState")
            )
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (args.Length != 0 || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Faction warfare map reads do not accept arguments.");
        }
        int systemId = context.Pilot.SystemId;
        return method switch
        {
            "GetAllWarzones" => new WarzoneDirectoryReply(warzones.Warzones),
            "GetHQSystemIDs" => new MilitiaHeadquartersReply(warzones.Headquarters),
            "GetAllWarzonesOccupationStates" or "GetAllWarzonesOccupationStatesUncached" => new WarzoneOccupationsReply(
                warzones.Warzones,
                warzones.Systems()
            ),
            "GetAllVictoryPointStates" => new WarzoneVictoryPointsReply(warzones.Systems()),
            "GetLocalOccupationState" => new LocalOccupationReply(systemId, warzones.System(systemId)),
            "GetLocalVictoryPointState" => new LocalVictoryPointsReply(systemId, warzones.System(systemId)),
            _ => PilotCallResult.Unsupported,
        };
    }
}
