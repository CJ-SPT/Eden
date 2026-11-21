using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task OscillatorMovesSubcapitalsAndOnlyLocksCapitalWarpAfterItsWarning(bool capital, bool outside)
    {
        using Fixture fixture = new(structures: true);
        var source = fixture.Db.Store.FindPilot(1000001)!;
        var target = fixture.Db.Store.FindPilot(1000002)!;
        var origin = new SpacePosition(1_500_000_000, 0, 0);
        var station = origin - new SpacePosition(500_000, 0, 0);
        long keepstar = fixture.AddUpwell(Keepstar, NullSystem, station, source.CorpId);
        var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
        fixture.Db.Store.SetPosition(source.ShipId, station, NullSystem);
        long ship = fixture.AssembleAndBoard(docking.Dock(source.PilotId, source.ShipId, keepstar), Avatar);
        long module = fixture.FitCapitalModule(DoomsdayFieldRules.TransportationFieldOscillator);
        fixture.Grant(source.PilotId, ship, InventoryFlag.Cargo, Strontium, 2001);
        fixture.Db.Store.SetPosition(ship, origin, NullSystem);
        long targetShip = target.ShipId;
        if (capital)
        {
            fixture.Db.Store.SetPosition(targetShip, s_station, Jan);
            targetShip = fixture.AssembleAndBoard(docking.Dock(target.PilotId, targetShip, JanStation), Revelation);
        }
        var aim = origin + new SpacePosition(50_000, 0, 0);
        var targetStart = aim + new SpacePosition(0, outside ? 15_000 : 0, 0);
        fixture.Db.Store.SetPosition(targetShip, targetStart, NullSystem);
        using Client titan = fixture.Select(1000001);
        using Client victim = fixture.Select(1000002);
        var health = fixture.Db.Services.GetRequiredService<IPilotHealth>();
        var before = health.Read(target.PilotId, fixture.Clock.Now).Remaining;
        Assert.IsType<PilotCallResult.Failure>(Activate(origin + new SpacePosition(200_000, 0, 0)));
        Assert.Equal(2001, Quantity(fixture, titan, Strontium));
        var started = Activate(aim);
        Assert.True(started is PilotCallResult.Success, started.ToString());
        Assert.Equal(1, Quantity(fixture, titan, Strontium));

        fixture.Clock.Seconds += 14;
        await victim.TickAsync();
        Assert.Equal(targetStart, victim.Pilot.Position);
        Assert.Contains(victim.Packets, packet => ContainsText(packet, "effects.IndicatorBeamMoving"));
        fixture.Clock.Seconds++;
        long fired = fixture.Clock.Now;
        await titan.TickAsync();
        await victim.TickAsync();
        var landed = victim.Pilot.Position!;
        Assert.Equal(NullSystem, victim.Pilot.SystemId);
        Assert.Equal(origin, titan.Pilot.Position);
        Assert.Equal(before, health.Read(target.PilotId, fired).Remaining);
        Assert.Equal(capital || outside, landed == targetStart);
        if (!capital && !outside)
        {
            Assert.True(landed.DistanceTo(targetStart) >= WarpCapacitorRules.AstronomicalUnit);
            Assert.Contains(victim.Packets, packet => ContainsText(packet, "SetBallPosition"));
        }

        var warp = fixture.Db.Services.GetRequiredService<IPilotWarpDrive>();
        if (capital)
        {
            Assert.Contains("transportation field", Assert.Throws<InvalidDataException>(() => PayForWarp(fired)).Message);
        }
        else
        {
            Assert.True(PayForWarp(fired).Distance > 0);
        }
        titan.Dispose();
        victim.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal(landed, fixture.Db.Store.FindPilot(1000002)!.Position);
        if (capital)
        {
            long expires = fired + DoomsdayFieldRules.OscillatorWarpLock;
            Assert.Contains("transportation field", Assert.Throws<InvalidDataException>(() => PayForWarp(expires - 1)).Message);
            Assert.True(PayForWarp(expires).Distance > 0);
        }

        PilotCallResult Activate(SpacePosition point) =>
            titan.Call(
                "superWeaponMgr",
                "ActivateSinglePointTargetedModule",
                module,
                (long)DogmaEffect.DoomsdayOscillator,
                new object?[] { point.X, point.Y, point.Z }
            );

        WarpCapacitorPayment PayForWarp(long now) => warp.Activate(target.PilotId, targetShip, 10_000_000, now);
    }
}
