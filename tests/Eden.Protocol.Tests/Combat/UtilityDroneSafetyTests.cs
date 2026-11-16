using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Drones;
using Eden.Data.World.Mining;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Protocol.Drones;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Combat.CombatDroneTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class UtilityDroneSafetyTests
{
    [Theory]
    [InlineData("lock")]
    [InlineData("family")]
    [InlineData("mixed")]
    [InlineData("range")]
    [InlineData("ownership")]
    public void RejectedWorkOrdersLeaveTheEntireFlightAndResourceUnchanged(string failure)
    {
        using Fixture f = UtilityDroneTests.Prepare();
        PassiveCatalogFixture.LoadMining(f.Base.CatalogPath);
        long stack = f.Bay(10246, 1);
        long combat = f.Bay(2488, 1);
        f.Space();
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        long drone = f.Drones.Launch(f.Pilot, [new(stack, 1)], f.Now).Launches.Single().DroneIds.Single();
        long gun = f.Drones.Launch(f.Pilot, [new(combat, 1)], f.Now).Launches.Single().DroneIds.Single();
        const long rock = 8000000000400;
        using (var transaction = live.Economy.Begin())
        {
            var position = transaction.Pilots.Find(f.Pilot)!.ActiveShip.Position!;
            transaction.Add(
                new WorldAsteroid
                {
                    Id = rock,
                    SystemId = 30000144,
                    BeltId = 40009232,
                    TypeId = failure == "family" ? 16262 : 1230,
                    Quantity = 10000,
                    Radius = 30,
                    X = position.X + (failure == "range" ? 100000 : 100),
                    Y = position.Y,
                    Z = position.Z,
                }
            );
            if (failure == "ownership")
            {
                transaction.Set<WorldDrone>().Find(drone)!.ControllerShipId = 0;
            }
            transaction.Commit();
        }
        var before = f.Drones.List(30000144, f.Now).ToArray();
        Assert.Throws<InvalidDataException>(() =>
            f.Drones.Order(
                f.Pilot,
                failure == "mixed" ? [drone, gun] : [drone],
                DroneOrder.MineRepeatedly,
                rock,
                failure == "lock" ? new HashSet<long>() : new HashSet<long> { rock },
                f.Now
            )
        );
        Assert.Equal(before, f.Drones.List(30000144, f.Now));
        Assert.Equal(10000, live.Economy.Inspect(w => w.Set<WorldAsteroid>().Find(rock)!.Quantity));
        Assert.Empty(live.Economy.Inspect(w => w.Set<SavedPilotHostility>().ToArray()));
    }

    [Fact]
    public void IdleWorkerShieldsRechargeOnlyOncePerObservedInterval()
    {
        using Fixture f = UtilityDroneTests.Prepare();
        long stack = f.Bay(10246, 1);
        f.Space();
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        long id = f.Drones.Launch(f.Pilot, [new(stack, 1)], f.Now).Launches.Single().DroneIds.Single();
        using (var transaction = live.Economy.Begin())
        {
            transaction.Set<WorldDrone>().Find(id)!.Shield = 1;
            transaction.Commit();
        }
        var before = Assert.Single(f.Drones.List(30000144, f.Now));
        f.Tick();
        var after = Assert.Single(f.Drones.List(30000144, f.Now));
        double expected = ChargedResource.Recharged(
            before.Health.Remaining.Shield,
            before.Profile.Defense.Shield,
            before.Profile.Defense.ShieldRechargeMilliseconds,
            1000
        );
        Assert.Equal(expected, after.Health.Remaining.Shield, 8);
    }

    [Fact]
    public void MiningAndRepairRigsReachTheirActualUtilityProfiles()
    {
        using Fixture f = UtilityDroneTests.Prepare();
        PassiveCatalogFixture.LoadDroneEquipment(f.Base.CatalogPath);
        Eden.Protocol.Tests.Persistence.SmallHullTests.TrainCatalog(f.Base);
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        var ore = f.Drones.Profile(f.Pilot, 10246)!;
        var ice = f.Drones.Profile(f.Pilot, 43699)!;
        var repair = f.Drones.Profile(f.Pilot, 23711)!;
        _ = f.Base.Fit(32043, online: false);
        _ = f.Base.Fit(32051, online: false);
        Assert.Equal(ore.Utility!.Amount * 1.1, f.Drones.Profile(f.Pilot, 10246)!.Utility!.Amount, 8);
        Assert.Equal(ice.Utility!.DurationMilliseconds * .9, f.Drones.Profile(f.Pilot, 43699)!.Utility!.DurationMilliseconds, 8);
        Assert.True(f.Drones.Profile(f.Pilot, 23711)!.Utility!.Amount > repair.Utility!.Amount);
    }

    [Fact]
    public void EcmJamExpiresBeforeItsNextAttemptAndCannotBeReplayedByRepeatedTicks()
    {
        using Fixture f = UtilityDroneTests.Prepare();
        using (CatalogDbContext catalog = new(f.Base.CatalogPath, writable: true))
        {
            foreach (
                var sensor in new[]
                {
                    DogmaAttributeId.RadarStrength,
                    DogmaAttributeId.LadarStrength,
                    DogmaAttributeId.MagnetometricStrength,
                    DogmaAttributeId.GravimetricStrength,
                }
            )
            {
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)sensor},0.01)");
            }
        }
        long stack = f.Bay(23707, 1);
        UtilityDroneIntegrationTests.Space(f);
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        long drone = f.Drones.Launch(f.Pilot, [new(stack, 1)], f.Now).Launches.Single().DroneIds.Single();
        const long target = 1000000000002;
        f.Drones.Order(f.Pilot, [drone], DroneOrder.Engage, target, new HashSet<long> { target }, f.Now);
        f.Tick();
        var ewar = f.Base.Db.Services.GetRequiredService<IPilotElectronicWarfare>();
        var first = Assert.Single(ewar.Incoming(target));
        Assert.Equal(5000, (first.ExpiresAt - first.StartedAt) / TimeSpan.TicksPerMillisecond);
        f.Base.Clock.Seconds += 5;
        ewar.Sweep(f.Now);
        Assert.Empty(ewar.Incoming(target));
        f.Tick();
        Assert.Empty(ewar.Incoming(target));
        f.Tick(14);
        var next = Assert.Single(ewar.Incoming(target));
        Assert.Equal(20000, (next.StartedAt - first.StartedAt) / TimeSpan.TicksPerMillisecond);
        f.Drones.Advance(f.Pilot, f.Now, 0);
        Assert.Equal(next, Assert.Single(ewar.Incoming(target)));
    }

    [Fact]
    public void UtilityAttributesAndEffectCommandsEncodeFiniteNativeValues()
    {
        using Fixture f = UtilityDroneTests.Prepare();
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        var profile = f.Drones.Profile(f.Pilot, 10246)!;
        Assert.True(profile.Mass > 0 && double.IsFinite(profile.Mass));
        Assert.True(profile.Agility > 0 && float.IsFinite(profile.Agility));
        var salvage = f.Drones.Profile(f.Pilot, 32787)!;
        Assert.True(salvage.Mass > 0 && salvage.Agility > 0);
        using CatalogDbContext catalog = new(f.Base.CatalogPath);
        var basis = Eden.World.Fittings.PilotFittingStore.ReadAttributes(catalog, 10246);
        var attributes = DroneWire.Attributes(profile, basis);
        Assert.All(attributes.Values, value => Assert.True(double.IsFinite(value)));
        Assert.Equal(46.875, attributes[DogmaAttributeId.MiningAmount]);
        var effect = new DroneWorkEffect(UtilityDroneKind.OreMining, 8000000000400, f.Now, 60000);
        var contact = new DroneContact(
            2000000000900,
            f.Pilot,
            f.Ship,
            1000009,
            30000144,
            profile,
            DroneActivity.Mining,
            effect.TargetId,
            new(0, 0, 0),
            new(0, 0, 0),
            new(0, profile.Defense, profile.Defense.Undamaged)
        );
        Assert.NotNull(BlueMarshal.Decode(BlueMarshal.Encode(DroneWorkWire.Effect(contact, effect, true))));
    }

    [Theory]
    [InlineData("CmdMine")]
    [InlineData("CmdMineRepeatedly")]
    [InlineData("CmdSalvage")]
    public void NativeWorkCommandsRejectMalformedArgumentsThroughTheDroneOwner(string method)
    {
        using Fixture f = UtilityDroneTests.Prepare();
        using LiveWorld live = new(new(f.Base.Db.Path), f.Base.Clock, _ => { });
        f.Space();
        using var scope = f.Base.Db.Services.CreateScope();
        using var context = DroneSynchronizationTests.Context(f, scope.ServiceProvider, 1000001);
        context.Flight.Snapshot();
        var result = scope.ServiceProvider.GetRequiredService<DroneHandler>().Call(context, "entity", method, [new List<object?>()]);
        Assert.IsType<PilotCallResult.Failure>(result);
    }
}
