using System.Net;
using Eden.Gameplay.FactionWarfare;
using Eden.Protocol.Gateway.FactionActivity;
using Google.Protobuf;

namespace Eden.Protocol.Gateway;

internal static class FactionActivityRequests
{
    internal static GatewayReply? Read(string type, ByteString payload, IFactionWarzones warzones)
    {
        if (type != "eve_public.faction.activity.GetSolarSystemScoresRequest")
        {
            return null;
        }
        try
        {
            var request = GetSolarSystemScoresRequest.Parser.ParseFrom(payload);
            if (request.SolarSystem is not { Sequential: > 0 and <= int.MaxValue } system)
            {
                return new(HttpStatusCode.BadRequest);
            }
            var response = new GetSolarSystemScoresResponse { SolarSystem = system };
            if (warzones.System((int)system.Sequential) is { Warzone: var zone })
            {
                // Until Advantage has a gameplay owner, both participants start with zero score and floor.
                response.Scores.Add(NeutralScore(zone.FirstFaction));
                response.Scores.Add(NeutralScore(zone.SecondFaction));
            }
            return new(HttpStatusCode.OK, response);
        }
        catch (InvalidProtocolBufferException)
        {
            return new(HttpStatusCode.BadRequest);
        }
    }

    private static Score NeutralScore(MilitiaFaction faction) => new() { Faction = new() { Sequential = (ulong)faction } };
}
