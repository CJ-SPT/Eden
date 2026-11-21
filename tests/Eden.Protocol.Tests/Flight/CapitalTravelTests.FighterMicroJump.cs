using Eden.Gameplay.Docking;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Theory]
    [InlineData(40362, 10000)]
    [InlineData(40560, 8000)]
    public async Task LongRangeHeavyFighterCompletesPinnedMicroJumpAfterRestart(int type, int spool)
    {
        using Fixture fixture = new(Archon, Helium, structures: true);
        var source = fixture.Db.Store.FindPilot(1000001)!;
        var keepstarPoint = new SpacePosition(1_000_000_000, 0, 0);
        long keepstar = fixture.AddUpwell(Keepstar, NullSystem, keepstarPoint, source.CorpId);
        fixture.Db.Store.SetPosition(source.ShipId, keepstarPoint, NullSystem);
        var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
        long ship = fixture.AssembleAndBoard(docking.Dock(source.PilotId, source.ShipId, keepstar), Aeon);
        fixture.Db.Store.SetPosition(ship, keepstarPoint + new SpacePosition(500_000, 0, 0), NullSystem);
        long fighter = fixture.PutInFighterBay(type, 3);
        using Client carrier = fixture.Select(1000001);
        int tube = FighterDeckRules.FirstTube;
        Assert.IsType<PilotCallResult.Success>(carrier.Call("fighterMgr", "LoadFightersToTube", fighter, tube));
        Assert.IsType<PilotCallResult.Success>(carrier.Call("fighterMgr", "LaunchFightersFromTubes", new List<object?> { tube }));
        var origin = Contact().Position;
        var destination = origin + new SpacePosition(0, 90_000, 0);
        Assert.IsType<PilotCallResult.Failure>(Jump(carrier, origin + new SpacePosition(0, 100_001, 0)));
        var started = Jump(carrier, destination);
        Assert.True(started is PilotCallResult.Success, started.ToString());
        var cycle = Contact().Mobility!;
        Assert.Equal(TimeSpan.FromMilliseconds(spool).Ticks, cycle.EndsAt - cycle.StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(90).Ticks, cycle.ReadyAt - cycle.EndsAt);
        Assert.Equal(origin, Contact().Position);
        Assert.Equal(2.5, Contact().MobilitySignatureMultiplier);
        carrier.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal(new FighterJumpDestination(destination.X, destination.Y, destination.Z), Contact().Mobility!.Destination);
        using Client resumed = fixture.Select(1000001);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(cycle.EndsAt - fixture.Clock.Now - 1));
        await resumed.TickAsync();
        Assert.NotEqual(destination, Contact().Position);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        await resumed.TickAsync();
        var moved = Contact();
        Assert.Equal(0, moved.Position.DistanceTo(destination));
        Assert.Equal(1, moved.MobilitySignatureMultiplier);
        Assert.Null(moved.Mobility!.Destination);
        Assert.IsType<PilotCallResult.Failure>(Jump(resumed, destination + new SpacePosition(0, 100, 0)));
        Assert.Contains(resumed.Packets, packet => ContainsText(packet, "SetBallPosition"));

        DroneContact Contact() =>
            Assert.Single(
                fixture.Db.Services.GetRequiredService<IPilotDrones>().List(NullSystem, fixture.Clock.Now),
                drone => drone.Id == fighter
            );
        PilotCallResult Jump(Client pilot, SpacePosition point) =>
            pilot.Call(
                "fighterMgr",
                "CmdActivateAbilitySlots",
                new List<object?> { fighter },
                1L,
                new object?[] { point.X, point.Y, point.Z }
            );
    }
}
