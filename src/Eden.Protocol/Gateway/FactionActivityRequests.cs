using System.Net;
using Eden.Gameplay.FactionWarfare;
using Eden.Protocol.Gateway.FactionActivity;
using Google.Protobuf;

namespace Eden.Protocol.Gateway;

internal static class FactionActivityRequests
{
    internal static GatewayReply? Read(
        string type,
        ByteString payload,
        IFactionWarzones warzones,
        long now,
        Action<MilitiaAdvantageState> observe
    )
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
            if (warzones.Advantage((int)system.Sequential, now) is { } scores)
            {
                response.Scores.Add(Wire(scores.First));
                response.Scores.Add(Wire(scores.Second));
                observe(scores);
            }
            return new(HttpStatusCode.OK, response);
        }
        catch (InvalidProtocolBufferException)
        {
            return new(HttpStatusCode.BadRequest);
        }
    }

    internal static SolarSystemScoresNotice Changed(MilitiaAdvantageState state) =>
        new()
        {
            SolarSystem = new() { Sequential = (ulong)state.SystemId },
            Scores = { Wire(state.First), Wire(state.Second) },
        };

    internal static ScoreContributionNotice Contribution(MilitiaAdvantageContribution contribution) =>
        new()
        {
            SolarSystem = new() { Sequential = (ulong)contribution.SystemId },
            Contributor = new() { Sequential = (ulong)contribution.PilotId },
            Faction = new() { Sequential = (ulong)contribution.Faction },
            Contribution = contribution.Amount,
        };

    private static Score Wire(MilitiaAdvantageScore score) =>
        new()
        {
            Faction = new() { Sequential = (ulong)score.Faction },
            Contribution = (uint)score.Contribution,
            Floor = (uint)score.Floor,
        };
}
