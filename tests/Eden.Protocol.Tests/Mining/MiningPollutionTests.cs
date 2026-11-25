using Eden.Data.World.Mining;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.Protocol.Flight;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Mining;

public sealed class MiningPollutionTests
{
    private const int Mercoxit = 11396;
    private const int DeepCoreStripMiner = 87562;

    [Fact]
    public void PinnedMercoxitCloudProfileUsesTheTrainedSpawnChanceAndExactPulseBoundary()
    {
        using TestWorld fixture = new();
        string path = new WorldDatabase(fixture.Path).CatalogPath;
        PassiveCatalogFixture.LoadMining(path);
        using CatalogDbContext catalog = new(path);
        var profile = MiningPollutionStore.Profile(catalog);
        Assert.Equal(5000, profile.Radius);
        Assert.Equal(TimeSpan.FromSeconds(30).Ticks, profile.Interval);
        Assert.Equal(TimeSpan.FromMinutes(5).Ticks, profile.Lifetime);
        Assert.Equal(new DamageVector(50, 50, 50, 50), profile.Damage);
        double chance = catalog.FittingAttributes(Mercoxit)[DogmaAttributeId.MiningDamageCloudChance];
        Assert.Equal(.05, MiningPollutionRules.Chance(chance, -10, 0));
        Assert.Equal(.025, MiningPollutionRules.Chance(chance, -10, 5));
        Assert.True(MiningPollutionRules.Contains(SpacePosition.Zero, profile.Radius, new(5000, 0, 0)));
        Assert.False(MiningPollutionRules.Contains(SpacePosition.Zero, profile.Radius, new(5000.001, 0, 0)));
        Assert.Equal(4 * profile.Interval, MiningPollutionRules.NextPulse(0, profile.Interval, TimeSpan.FromSeconds(105).Ticks));
    }

    [Fact]
    public void PaidMercoxitMiningPublishesAndExpiresAResidentCloudWithScopedDamage()
    {
        using var fixture = HullHoldTests.Prepare(17478, HullClass.MiningBarge);
        PassiveCatalogFixture.LoadMining(fixture.CatalogPath);
        using (CatalogDbContext catalog = new(fixture.CatalogPath, writable: true))
        {
            // Make the spawn deterministic after trained skill scaling without replacing native mining activation.
            const int cloudChanceAttribute = (int)DogmaAttributeId.MiningDamageCloudChance;
            catalog.Database.ExecuteSql(
                $"UPDATE TypeAttribute SET Value=2 WHERE ItemTypeId={Mercoxit} AND AttributeId={cloudChanceAttribute}"
            );
        }
        long module = fixture.Fit(DeepCoreStripMiner);
        long miner = fixture.ShipId;
        var wing = fixture.Db.Store.FindPilot(1000002)!;
        int system = fixture.Db.Store.FindPilot(1000001)!.SystemId;
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        SpacePosition center = new(10_000_000_000, 50_000, 2_000_000_000);
        fixture.Db.Store.SetPosition(miner, center + new SpacePosition(1000, 0, 0), system);
        fixture.Db.Store.SetPosition(wing.ShipId, center + new SpacePosition(5001, 0, 0), system);
        using var scope = fixture.Db.Services.CreateScope();
        var flights = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        using var minerFlight = flights.Create(fixture.Db.Store.FindPilot(1000001)!);
        using var wingFlight = flights.Create(fixture.Db.Store.FindPilot(1000002)!);
        const long rockId = AsteroidDefinitions.FirstAsteroidId + 99;
        using (var setup = live.Economy.Begin(new(systems: [system])))
        {
            setup.Add(
                new WorldAsteroid
                {
                    Id = rockId,
                    SystemId = system,
                    BeltId = 40009087,
                    TypeId = Mercoxit,
                    Quantity = 10000,
                    Radius = 100,
                    X = center.X,
                    Y = center.Y,
                    Z = center.Z,
                }
            );
            setup.Commit();
        }
        var fields = fixture.Db.Services.GetRequiredService<IAsteroidField>();
        minerFlight.RefreshAsteroids(fields.List(system));
        minerFlight.Snapshot();
        wingFlight.Snapshot();
        Assert.Equal(1000, minerFlight.Pilot.Position!.DistanceTo(center));
        Assert.Equal(5001, wingFlight.Pilot.Position!.DistanceTo(center));
        minerFlight.Updates.Clear();
        var mining = fixture.Db.Services.GetRequiredService<IPilotMining>();
        long started = fixture.Clock.Now;
        var start = mining.Begin(fixture.PilotId, module, rockId, started);
        Assert.Equal(DeepCoreStripMiner, start.Laser.TypeId);
        var cloud = Assert.Single(fields.List(system), body => body.TypeId == MiningPollutionRules.TypeId);
        Assert.Equal(5000, cloud.Radius);
        Assert.False(cloud.Solid);
        Assert.False(cloud.IsGlobal);
        minerFlight.RefreshAsteroids(fields.List(system));
        Assert.Contains(minerFlight.WireUpdates().Cast<object?[]>(), update => Equals(Assert.IsType<object?[]>(update[1])[0], "AddBalls"));
        Assert.Null(minerFlight.FindTarget(cloud.Id));

        var health = fixture.Db.Services.GetRequiredService<IPilotHealth>();
        var authority = fixture.Db.Services.GetRequiredService<IMiningPollutionClouds>();
        var before = health.Read(fixture.PilotId, fixture.Clock.Now);
        var wingBefore = health.Read(wing.PilotId, fixture.Clock.Now);
        long firstPulse = started + TimeSpan.FromSeconds(30).Ticks;
        AdvanceTo(firstPulse - 1);
        authority.Advance(fixture.Clock.Now);
        Assert.Equal(before.Remaining, health.Read(fixture.PilotId, fixture.Clock.Now).Remaining);
        AdvanceTo(firstPulse);
        authority.Advance(fixture.Clock.Now);
        var after = health.Read(fixture.PilotId, fixture.Clock.Now);
        Assert.Equal(CombatDamage.Apply(before.Remaining, before.Defense, new(50, 50, 50, 50)).Remaining, after.Remaining);
        Assert.Equal(wingBefore.Remaining, health.Read(wing.PilotId, fixture.Clock.Now).Remaining);
        authority.Advance(fixture.Clock.Now);
        Assert.Equal(after.Remaining, health.Read(fixture.PilotId, fixture.Clock.Now).Remaining);

        using (var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot()))
        {
            checkpoint.Project();
            using WorldDbContext saved = new(checkpoint.Database);
            var persisted = Assert.Single(saved.Set<SavedMiningPollutionCloud>());
            Assert.Equal(cloud.Id, persisted.Id);
            Assert.Equal(firstPulse + TimeSpan.FromSeconds(30).Ticks, persisted.NextPulseAt);
        }
        long expires = started + TimeSpan.FromMinutes(5).Ticks;
        AdvanceTo(expires - 1);
        Assert.Contains(fields.List(system), body => body.Id == cloud.Id);
        AdvanceTo(expires);
        Assert.DoesNotContain(fields.List(system), body => body.Id == cloud.Id);
        var beforeExpiry = health.Read(fixture.PilotId, fixture.Clock.Now);
        authority.Advance(fixture.Clock.Now);
        Assert.Equal(beforeExpiry.Remaining, health.Read(fixture.PilotId, fixture.Clock.Now).Remaining);
        Assert.False(live.Economy.HasMiningPollution(system));
        minerFlight.Updates.Clear();
        minerFlight.RefreshAsteroids(fields.List(system));
        Assert.Contains(
            minerFlight.WireUpdates().Cast<object?[]>(),
            update => Equals(Assert.IsType<object?[]>(update[1])[0], "RemoveBalls")
        );

        void AdvanceTo(long deadline) => fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(deadline - fixture.Clock.Now));
    }
}
