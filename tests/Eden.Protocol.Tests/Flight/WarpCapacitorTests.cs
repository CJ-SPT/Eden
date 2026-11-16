using Eden.Data.World.Fittings;
using Eden.Data.World.Skills;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed class WarpCapacitorTests
{
    [Theory]
    [InlineData(1, 1_000_000, 0.000001, 100, 1, 1, 99)]
    [InlineData(10, 1_000_000, 0.000001, 100, 10, 10, 90)]
    [InlineData(10, 2_000_000, 0.000001, 100, 10, 20, 80)]
    [InlineData(10, 1_000_000, 0.0000005, 100, 10, 5, 95)]
    [InlineData(10, 1_000_000, 0.000001, 5, 5, 5, 0)]
    [InlineData(10, 1_000_000, 0, 0, 10, 0, 0)]
    public void WarpPaymentUsesMassDistanceAndEffectiveNeed(
        double distanceAu,
        double mass,
        double need,
        double capacitor,
        double expectedAu,
        double expectedCost,
        double expectedCharge
    )
    {
        var result = WarpCapacitorRules.Pay(distanceAu * WarpCapacitorRules.AstronomicalUnit, mass, need, capacitor);
        Assert.Equal(expectedAu * WarpCapacitorRules.AstronomicalUnit, result.Distance, 5);
        Assert.Equal(expectedCost, result.Cost, 8);
        Assert.Equal(expectedCharge, result.Capacitor, 8);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0.0000001)]
    public void EmptyOrUnusableCapacitorRejectsWarp(double charge)
    {
        Assert.Throws<InvalidDataException>(() =>
            WarpCapacitorRules.Pay(10 * WarpCapacitorRules.AstronomicalUnit, 1_000_000, 0.000001, charge)
        );
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8000, 3)]
    [InlineData(256000, 5)]
    public async Task ResidentWarpPaysOnceNotifiesAndCheckpointsWithWarpDriveSkill(long points, int level)
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        string catalogPath = new WorldDatabase(db.Path).CatalogPath;
        PassiveCatalogFixture.LoadCapacitorModules(catalogPath);
        var pilot = db.Store.FindPilot(1000001)!;
        var origin = pilot.Home.Position + new SpacePosition(pilot.Flight.StationRadius + 50000, 0, 0);
        db.Store.SetPosition(pilot.ShipId, origin);
        using (WorldDbContext saved = new(db.Path))
        {
            saved.Database.ExecuteSql(
                $"DELETE FROM WorldPilotSkill WHERE PilotId={pilot.PilotId} AND TypeId={WarpCapacitorRules.WarpDriveOperationSkill}"
            );
            saved.Add(
                new PilotSkill
                {
                    PilotId = pilot.PilotId,
                    TypeId = WarpCapacitorRules.WarpDriveOperationSkill,
                    Points = points,
                }
            );
            saved.SaveChanges();
        }
        using LiveWorld live = new(new(db.Path), clock, _ => { });
        using var system = live.UseSystem(pilot.SystemId);
        var selected = db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            selected.Account,
            [selected],
            db.Services.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        context.Select(selected);
        var flight = context.Flight;
        flight.Snapshot();
        flight.Updates.Clear();
        var resources = db.Services.GetRequiredService<IPilotShipResources>();
        var before = resources.Read(pilot.PilotId, pilot.ShipId, clock.Now);
        // Pinned Ibis profile: 8.66e-7 GJ/AU/kg. The dogma graph applies effect 494 once.
        Assert.Equal(8.66e-7 * (1 - level * 0.1), before.Attributes[DogmaAttributeId.WarpCapacitorNeed], 14);
        var target = origin + new SpacePosition(10 * WarpCapacitorRules.AstronomicalUnit, 0, 0);
        FlightCommand.WarpToPoint order = new(-1, target, 0);
        Assert.Same(PilotCallResult.None, live.Run(() => flight.Execute(order)));
        var change = Assert.Single(Assert.IsType<ModuleAttributesChanged>(Assert.Single(flight.NotificationsBeforeFlight)).Changes);
        double distance = origin.DistanceTo(WarpCourse.LandingPoint(pilot.ShipId, pilot.Flight.ShipRadius, origin, target, 0));
        double cost =
            distance
            / WarpCapacitorRules.AstronomicalUnit
            * before.Attributes[DogmaAttributeId.Mass]
            * before.Attributes[DogmaAttributeId.WarpCapacitorNeed];
        Assert.Equal(before.Capacitor - cost, resources.Capacitor(pilot.PilotId, clock.Now), 8);
        Assert.Equal(DogmaAttributeId.CapacitorCharge, change.Attribute);
        Assert.Equal(new DogmaItemKey.Item(pilot.ShipId), change.Item);
        Assert.Equal(before.Capacitor - cost, change.Value, 8);
        Assert.Same(PilotCallResult.None, live.Run(() => flight.Execute(order)));
        Assert.Single(flight.NotificationsBeforeFlight);
        Assert.Equal(change.Value, resources.Capacitor(pilot.PilotId, clock.Now), 8);
        Assert.Same(PilotCallResult.None, live.Run(() => flight.Execute(new FlightCommand.Stop())));
        Assert.False(flight.PreparingWarp);
        Assert.Equal(change.Value, resources.Capacitor(pilot.PilotId, clock.Now), 8);
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        Assert.Equal(change.Value, reopened.Set<ShipEnergy>().Find(pilot.ShipId)!.Charge, 8);
        List<BlueInstance> packets = [];
        PilotSessionOutput output = new(
            context,
            1000001,
            42,
            packet =>
            {
                packets.Add(Assert.IsType<BlueInstance>(packet));
                return Task.CompletedTask;
            },
            _ => { }
        );
        await output.FlushFlightUpdatesAsync();
        string[] names = packets
            .Select(packet =>
            {
                var destination = Assert.IsType<BlueInstance>(BlueRead.Tuple(packet.State, 14)[2]);
                return Assert.IsType<string>(BlueRead.Tuple(destination.State, 4)[1]);
            })
            .ToArray();
        Assert.Equal("OnModuleAttributeChanges", names[0]);
        Assert.Contains("DoDestinyUpdate", names.Skip(1));
        Assert.Empty(flight.NotificationsBeforeFlight);
        int count = packets.Count;
        await output.FlushFlightUpdatesAsync();
        Assert.Equal(count, packets.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void LowCapacitorRejectsOrLandsAtThePaidDistanceWithoutChangingRejectedState(double charge)
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        PassiveCatalogFixture.LoadCapacitorModules(new WorldDatabase(db.Path).CatalogPath);
        var pilot = db.Store.FindPilot(1000001)!;
        var origin = pilot.Home.Position + new SpacePosition(pilot.Flight.StationRadius + 50000, 0, 0);
        db.Store.SetPosition(pilot.ShipId, origin);
        using (WorldDbContext saved = new(db.Path))
        {
            saved.Add(
                new ShipEnergy
                {
                    ShipId = pilot.ShipId,
                    Charge = charge,
                    UpdatedAt = clock.Now,
                }
            );
            saved.SaveChanges();
        }
        using LiveWorld live = new(new(db.Path), clock, _ => { });
        using var system = live.UseSystem(pilot.SystemId);
        using var flight = db.Services.GetRequiredService<FlightSessionFactory>().Create(db.Store.FindPilot(1000001)!);
        flight.Snapshot();
        flight.Updates.Clear();
        var resources = db.Services.GetRequiredService<IPilotShipResources>();
        var before = resources.Read(pilot.PilotId, pilot.ShipId, clock.Now);
        // Invalid coordinates and local targets must never spend capacitor.
        Assert.Same(PilotCallResult.Unsupported, live.Run(() => flight.Execute(new FlightCommand.WarpToPoint(-1, origin, 0))));
        Assert.Same(
            PilotCallResult.Unsupported,
            live.Run(() => flight.Execute(new FlightCommand.WarpToPoint(-1, new(double.NaN, 0, 0), 0)))
        );
        Assert.Equal(charge, resources.Capacitor(pilot.PilotId, clock.Now), 8);
        Assert.Empty(flight.NotificationsBeforeFlight);
        var target = origin + new SpacePosition(10 * WarpCapacitorRules.AstronomicalUnit, 0, 0);
        var result = live.Run(() => flight.Execute(new FlightCommand.WarpToPoint(-1, target, 0)));
        if (charge == 0)
        {
            Assert.IsType<PilotCallResult.Failure>(result);
            Assert.False(flight.PreparingWarp);
            Assert.Equal(origin, flight.Pilot.Position);
            Assert.Empty(flight.NotificationsBeforeFlight);
            Assert.DoesNotContain(flight.Updates, update => update.Command is DestinyCommand.WarpTo);
            Assert.Equal(charge, resources.Capacitor(pilot.PilotId, clock.Now), 8);
            return;
        }
        Assert.Same(PilotCallResult.None, result);
        Assert.Equal(0, resources.Capacitor(pilot.PilotId, clock.Now), 8);
        double paidDistance =
            charge
            / (before.Attributes[DogmaAttributeId.Mass] * before.Attributes[DogmaAttributeId.WarpCapacitorNeed])
            * WarpCapacitorRules.AstronomicalUnit;
        for (int tick = 0; tick < 240 && flight.Moving; tick++)
        {
            clock.Seconds++;
            live.Run(() => flight.Checkpoint());
        }
        Assert.False(flight.Moving);
        var warp = Assert.Single(flight.Updates.Select(update => update.Command).OfType<DestinyCommand.WarpTo>());
        Assert.InRange(Math.Abs(origin.DistanceTo(new(warp.X, warp.Y, warp.Z)) - paidDistance), 0, 0.001);
        // The existing warp exit retains subwarp velocity until the following motion step.
        Assert.InRange(Math.Abs(origin.DistanceTo(flight.Pilot.Position!) - paidDistance), 0, 1000);
        Assert.True(flight.Pilot.Position!.DistanceTo(target) > WarpCapacitorRules.AstronomicalUnit);
        using var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        checkpoint.Project();
        using WorldDbContext reopened = new(checkpoint.Database);
        Assert.Equal(0, reopened.Set<ShipEnergy>().Find(pilot.ShipId)!.Charge, 8);
    }
}
