using Eden.Gameplay.Flight;
using Eden.Protocol.Gateway;
using Eden.Protocol.Gateway.FactionActivity;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed class FactionWarzoneProtocolTests
{
    private const int Frontline = 30002087;

    [Fact]
    public void MapReadsReturnNativeWarzonesHeadquartersOccupationAndVictoryPoints()
    {
        using TestWorld world = new(liveWorld: true);
        using var scope = world.Services.CreateScope();
        var pilot = world.Store.FindPilot(1000001)!;
        var visiting = pilot with { CurrentSystem = pilot.Home.System with { Id = Frontline }, Position = new SpacePosition(0, 0, 0) };
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            current => new(world.Store, current, TimeProvider.System, new(), world.Objects),
            42,
            SignedSettings.Empty
        );
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        const string map = "fwWarzoneSolarsystem";
        const string points = "fwVictoryPointMgr";
        Assert.Same(PilotCallResult.Unsupported, Call(map, "GetAllWarzones", []));
        context.Select(pilot);
        Assert.Equal(new object?[] { (long)pilot.SystemId, null }, BlueRead.Tuple(Reply(map, "GetLocalOccupationState"), 2));
        Assert.Equal(new object?[] { (long)pilot.SystemId, null }, BlueRead.Tuple(Reply(points, "GetLocalVictoryPointState"), 2));
        context.ChangeShip(visiting);

        var warzones = Index(Reply(map, "GetAllWarzones"));
        Assert.Equal(new object?[] { 500003L, 500002L }, Assert.IsType<List<object?>>(warzones[1]));
        Assert.Equal(new object?[] { 500001L, 500004L }, Assert.IsType<List<object?>>(warzones[2]));
        var headquarters = Index(Reply(map, "GetHQSystemIDs"));
        Assert.Equal(4, headquarters.Count);
        Assert.Equal(30045324L, headquarters[500001]);
        Assert.Equal(30003788L, headquarters[500004]);

        foreach (string method in new[] { "GetAllWarzonesOccupationStates", "GetAllWarzonesOccupationStatesUncached" })
        {
            var occupation = Index(Reply(map, method));
            Assert.Equal(70, Index(occupation[1]).Count);
            Assert.Equal(90, Index(occupation[2]).Count);
            AssertOccupation(Index(occupation[1])[Frontline]);
        }
        var local = BlueRead.Tuple(Reply(map, "GetLocalOccupationState"), 2);
        Assert.Equal((long)Frontline, local[0]);
        AssertOccupation(local[1]);
        var localPoints = BlueRead.Tuple(Reply(points, "GetLocalVictoryPointState"), 2);
        Assert.Equal((long)Frontline, localPoints[0]);
        AssertVictoryPoints(localPoints[1]);
        var allPoints = Index(Reply(points, "GetAllVictoryPointStates"));
        Assert.Equal(160, allPoints.Count);
        AssertVictoryPoints(allPoints[Frontline]);
        Assert.IsType<PilotCallResult.Failure>(Call(map, "GetLocalOccupationState", [Frontline]));
        Assert.IsType<PilotCallResult.Failure>(Call(points, "GetAllVictoryPointStates", [], new() { ["systemID"] = Frontline }));
        Assert.Same(PilotCallResult.Unsupported, Call(map, "UnknownWarzoneMethod", []));

        object? Reply(string service, string method)
        {
            var reply = Assert.IsType<PilotCallResult.Success>(Call(service, method, [], new() { ["machoVersion"] = 1L }));
            return BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply));
        }

        PilotCallResult Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            router.Call(context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        static Dictionary<long, object?> Index(object? value) => Assert.IsType<Dictionary<long, object?>>(value);

        static void AssertOccupation(object? value)
        {
            var constructor = Assert.IsType<BlueConstructor>(value);
            Assert.Equal("factionwarfare.occupationState.OccupationState", constructor.Name);
            Assert.Equal(5, constructor.Arguments.Length);
            Assert.Equal(500002L, constructor.Arguments[0]);
            Assert.Equal("FL", BlueMarshal.Text(constructor.Arguments[1]));
            Assert.Equal(new object?[] { 500003L, 500002L }, Assert.IsType<List<object?>>(constructor.Arguments[2]));
            Assert.Equal(500011L, constructor.Arguments[3]);
            Assert.Null(constructor.Arguments[4]);
        }

        static void AssertVictoryPoints(object? value)
        {
            var constructor = Assert.IsType<BlueConstructor>(value);
            Assert.Equal("factionwarfare.victoryPointState.VictoryPointState", constructor.Name);
            Assert.Equal(new object?[] { 0L, 75000L }, constructor.Arguments);
        }
    }

    [Fact]
    public void AdvantageReadEchoesSystemAndReturnsNeutralParticipantScores()
    {
        using TestWorld world = new(liveWorld: true);
        var gateway = world.Services.GetRequiredService<GatewayRequests>();
        var request = new GetSolarSystemScoresRequest { SolarSystem = new() { Sequential = Frontline } };
        var response = gateway.Handle(new() { Payload = Any.Pack(request) });
        Assert.Equal(200u, response.StatusCode);
        var scores = response.Payload.Unpack<GetSolarSystemScoresResponse>();
        Assert.Equal(request.SolarSystem, scores.SolarSystem);
        Assert.Equal(new[] { 500003ul, 500002ul }, scores.Scores.Select(score => score.Faction.Sequential));
        Assert.All(
            scores.Scores,
            score =>
            {
                Assert.Equal(0u, score.Contribution);
                Assert.Equal(0u, score.Floor);
            }
        );

        request.SolarSystem.Sequential = 30003788;
        response = gateway.Handle(new() { Payload = Any.Pack(request) });
        Assert.Equal(200u, response.StatusCode);
        Assert.Empty(response.Payload.Unpack<GetSolarSystemScoresResponse>().Scores);
        Assert.Equal(400u, gateway.Handle(new() { Payload = Any.Pack(new GetSolarSystemScoresRequest()) }).StatusCode);
        var malformed = Any.Pack(request);
        malformed.Value = ByteString.CopyFrom([0x0a]);
        Assert.Equal(400u, gateway.Handle(new() { Payload = malformed }).StatusCode);
    }
}
