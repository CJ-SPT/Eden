using Eden.Data.World.Characters;
using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.FactionWarfare;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Gateway;
using Eden.Protocol.Gateway.FactionActivity;
using Eden.Server;
using Eden.World.FactionWarfare;
using Eden.World.Runtime;
using Eden.World.Storage;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.FactionWarfare;

public sealed class MilitiaAdvantageTests
{
    private const int System = 30002087;
    private const long Account = 1000001;
    private const long Observer = 1000002;

    [Theory]
    [InlineData(50, 0, 50)]
    [InlineData(50, -2000, 50)]
    [InlineData(50, 1, 51)]
    [InlineData(-50, 30000, -65)]
    [InlineData(350, 100000, 700)]
    public void AppliesOnlyPositiveNetAdvantageToVictoryPoints(int points, int net, int expected) =>
        Assert.Equal(expected, MilitiaAdvantageRules.AdjustedVictoryPoints(points, net));

    [Fact]
    public async Task ContributionCapsDecaysAndPublishesPrivateAndPublicNotices()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(fixture.Path);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var gateway = fixture.Services.GetRequiredService<GatewayRequests>();
        var warzones = fixture.Services.GetRequiredService<IFactionWarzones>();
        long pilotId = fixture.Store.FindPilot(Account)!.PilotId;
        var request = new Request { Payload = Any.Pack(new GetSolarSystemScoresRequest { SolarSystem = new() { Sequential = System } }) };
        Assert.Equal(200u, gateway.Handle(request, Account).StatusCode);
        Assert.Equal(200u, gateway.Handle(request, Observer).StatusCode);
        int floor = warzones.Advantage(System, clock.Now)!.Second.Floor;
        int cap = MilitiaAdvantageRules.Maximum - floor;

        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            var pilot = action.Set<Pilot>().Find(pilotId)!;
            var zone = FactionWarzoneStore.System(action, System)!;
            Assert.Equal(cap, MilitiaAdvantageStore.Apply(action, zone, MilitiaFaction.Minmatar, 100_000, clock.Now, pilot));
            Assert.Equal(0, MilitiaAdvantageStore.Apply(action, zone, MilitiaFaction.Minmatar, 50, clock.Now, pilot));
            action.Commit();
        }

        var owner = gateway.FactionActivityNotices(Account);
        var contribution = Assert
            .Single(owner, row => row.Payload.Is(ScoreContributionNotice.Descriptor))
            .Payload.Unpack<ScoreContributionNotice>();
        Assert.Equal((ulong)pilotId, contribution.Contributor.Sequential);
        Assert.Equal(cap, contribution.Contribution);
        var publicNotice = Assert.Single(gateway.FactionActivityNotices(Observer)).Payload.Unpack<SolarSystemScoresNotice>();
        Assert.Equal((uint)cap, publicNotice.Scores[1].Contribution);
        Assert.Empty(gateway.FactionActivityNotices(Account));
        Assert.Empty(gateway.FactionActivityNotices(Observer));

        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            var zone = FactionWarzoneStore.System(action, System)!;
            Assert.Equal(-2_000, MilitiaAdvantageStore.Apply(action, zone, MilitiaFaction.Minmatar, -2_000, clock.Now));
        }
        Assert.Equal(cap, warzones.Advantage(System, clock.Now)!.Second.Contribution);
        using (var metrics = SessionPerformance.Begin("warmed Advantage", _ => { }, force: true)!)
        {
            Assert.Equal(cap, warzones.Advantage(System, clock.Now + TimeSpan.TicksPerDay - 1)!.Second.Contribution);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }

        await live.FlushAsync();
        fixture.Services.Dispose();
        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        var restored = reopened.GetRequiredService<IFactionWarzones>();
        Assert.Equal(
            cap - MilitiaAdvantageRules.DailyDecay,
            restored.Advantage(System, clock.Now + TimeSpan.TicksPerDay)!.Second.Contribution
        );
        Assert.Null(restored.Advantage(30000142, clock.Now));
    }
}
