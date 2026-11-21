using Eden.Gameplay.Docking;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Protocol.Fighters;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Theory]
    [InlineData(Templar, 18, 14000, 4000)]
    [InlineData(Cyclops, 9, 40000, 6000)]
    public async Task FighterSalvoDamagesLockedTargetAndRearmsAfterCheckpoint(int type, int charges, int duration, int rearm)
    {
        using Fixture fixture = new(Archon, Helium, structures: true);
        var source = fixture.Db.Store.FindPilot(1000001)!;
        var origin = new SpacePosition(1_000_000_000, 0, 0);
        long ship = source.ShipId;
        if (type == Cyclops)
        {
            var point = origin - new SpacePosition(500_000, 0, 0);
            long keepstar = fixture.AddUpwell(Keepstar, NullSystem, point, source.CorpId);
            fixture.Db.Store.SetPosition(ship, point, NullSystem);
            var docked = fixture.Db.Services.GetRequiredService<IStationDocking>().Dock(source.PilotId, ship, keepstar);
            ship = fixture.AssembleAndBoard(docked, Aeon);
        }
        fixture.Db.Store.SetPosition(ship, origin, NullSystem);
        long target = fixture.AddUpwell(35832, NullSystem, origin + new SpacePosition(9_000, 0, 0), source.CorpId);
        long fighter = fixture.PutInFighterBay(type, 3);
        int tube = FighterDeckRules.FirstTube;
        using Client carrier = fixture.Select(1000001);
        Assert.IsType<PilotCallResult.Success>(carrier.Call("fighterMgr", "LoadFightersToTube", fighter, tube));
        Assert.IsType<PilotCallResult.Success>(carrier.Call("fighterMgr", "LaunchFightersFromTubes", new List<object?> { tube }));
        Assert.Equal(charges, State().Salvo!.Charges);
        Assert.IsType<PilotCallResult.Failure>(Fire());
        Assert.Equal(charges, State().Salvo!.Charges);
        Assert.IsType<PilotCallResult.Success>(carrier.Call("dogmaIM", "AddTarget", target));
        fixture.Clock.Seconds += 20;
        await carrier.TickAsync();
        Assert.IsType<PilotCallResult.Success>(
            carrier.Call("fighterMgr", "CmdActivateAbilitySlots", new List<object?> { fighter }, 0L, target)
        );
        var fired = Fire();
        Assert.True(fired is PilotCallResult.Success, fired.ToString());
        var active = State();
        Assert.Equal(DroneActivity.Fighting, active.Activity);
        Assert.True(active.SalvoActive);
        var cycle = active.Salvo!;
        Assert.Equal(charges - 1, cycle.Charges);
        Assert.Equal(duration * TimeSpan.TicksPerMillisecond, cycle.EndsAt - cycle.StartedAt);
        Assert.Equal(rearm * TimeSpan.TicksPerMillisecond, cycle.RearmAt - cycle.EndsAt);
        Assert.NotNull(fixture.Structures.Read(source.PilotId, target)!.Battle!.RepairAt);
        var notification = Assert.Single(carrier.Context.NotificationsBeforeFlight.OfType<FighterSalvoStarted>());
        Assert.Equal(
            new object?[]
            {
                fighter,
                FighterSalvoCycle.Slot,
                charges - 1,
                cycle.StartedAt,
                (long)duration,
                new List<object?> { cycle.EndsAt, cycle.RearmAt },
            },
            notification.Arguments
        );
        Assert.IsType<PilotCallResult.Failure>(Fire());
        Assert.IsType<PilotCallResult.Success>(carrier.Call("fighterMgr", "CmdDeactivateAbilitySlots", new List<object?> { fighter }, 2L));
        Assert.False(State().SalvoActive);
        Assert.IsType<PilotCallResult.Failure>(Fire());
        cycle = cycle with { StartedAt = 0 };
        carrier.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal(cycle, State().Salvo);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(cycle.RearmAt - fixture.Clock.Now - 1));
        Assert.Equal(charges - 1, State().Salvo!.Charges);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Equal(charges, State().Salvo!.Charges);

        FighterInSpace State() =>
            Assert.Single(fixture.Db.Services.GetRequiredService<IPilotFighterDeck>().Read(source.PilotId, ship).Squadrons);
        PilotCallResult Fire() => carrier.Call("fighterMgr", "CmdActivateAbilitySlots", new List<object?> { fighter }, 2L, target);
    }
}
