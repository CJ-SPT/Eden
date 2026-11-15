using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Exploration;

public sealed class AbyssalEnvironmentTests
{
    [Fact]
    public async Task SuppressorHitsRogueDronesPlayerDronesAndMissilesButLeavesCacheIntact()
    {
        using Fixture f = new();
        AbyssalTests.Prepare(f);
        PassiveCatalogFixture.LoadCombatDrones(f.CatalogPath);
        long ship = f.ShipId;
        long filament = f.Grant(AbyssalRules.TranquilDarkFilament, cargo: true);
        long droneId = f.Grant(2488);
        const long MissileId = 9_800_000_002;
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var abyss = f.Db.Services.GetRequiredService<IAbyssalDeadspace>();
        var run = abyss.Deploy(f.PilotId, ship, filament, AbyssalMode.Cruiser).Run;
        long targetId;
        long cacheId;
        double cacheShield;
        using (var transaction = live.Economy.Begin(new(pilots: [f.PilotId], systems: [run.SystemId])))
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            var saved = transaction.Set<SavedAbyssalRun>().Find(run.Id)!;
            var field = AbyssalEnvironmentRules.Layout(0, saved.Seed)[2];
            var target = transaction.Filter<WorldNpc>(n => n.EncounterId == run.Id && n.Role == NpcRole.Pirate).First();
            targetId = target.Id;
            target.X = field.Position.X;
            target.Y = field.Position.Y;
            target.Z = field.Position.Z;
            target.HomeX = field.Position.X;
            target.HomeY = field.Position.Y;
            target.HomeZ = field.Position.Z;
            var attributes = catalog.FittingAttributes(target.TypeId);
            target.ShieldCharge = 0;
            target.ArmorDamage = attributes[DogmaAttributeId.ArmorHitPoints];
            target.StructureDamage = attributes[DogmaAttributeId.StructureHitPoints] - 1;
            var cache = transaction.Filter<WorldNpc>(n => n.EncounterId == run.Id && n.TypeId == AbyssalRules.CacheType).Single();
            cache.X = field.Position.X;
            cache.Y = field.Position.Y;
            cache.Z = field.Position.Z;
            cacheId = cache.Id;
            cacheShield = cache.ShieldCharge;
            var item = transaction.Set<OwnedItem>().Find(droneId)!;
            item.Singleton = true;
            item.LocationId = run.SystemId;
            item.FlagId = (int)InventoryFlag.AutoFit;
            var droneAttributes = catalog.FittingAttributes(item.TypeId);
            transaction.Add(
                new WorldDrone
                {
                    ItemId = droneId,
                    PilotId = f.PilotId,
                    ControllerShipId = ship,
                    SystemId = run.SystemId,
                    X = field.Position.X,
                    Y = field.Position.Y,
                    Z = field.Position.Z,
                    UpdatedAt = f.Clock.Now,
                    ArmorDamage = droneAttributes[DogmaAttributeId.ArmorHitPoints],
                    StructureDamage = droneAttributes[DogmaAttributeId.StructureHitPoints] - 1,
                }
            );
            var missile = new WorldMissile
            {
                Id = MissileId,
                PilotId = f.PilotId,
                SourceShipId = ship,
                SourceTypeId = 621,
                ChargeTypeId = 210,
                TargetId = targetId,
                SystemId = run.SystemId,
                UpdatedAt = f.Clock.Now,
                LaunchedAt = f.Clock.Now,
                ExpiresAt = f.Clock.Now + TimeSpan.TicksPerMinute,
                Snapshot = "",
            };
            missile.Save(
                new(new(new(1, 2, 1000, 1, 60000), field.Position, SpacePosition.Zero, field.Position), new(new(0, 0, 100, 0), 10, 1000, 1))
            );
            transaction.Add(missile);
            transaction.Commit();
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(2));
        abyss.Advance();
        for (int pulse = 0; pulse < 2; pulse++)
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(2));
            abyss.Advance();
        }
        live.Economy.Inspect(world =>
        {
            Assert.NotNull(world.Set<WorldNpc>().Find(targetId)!.DestroyedAt);
            Assert.Equal(cacheShield, world.Set<WorldNpc>().Find(cacheId)!.ShieldCharge);
            Assert.Null(world.Set<WorldDrone>().Find(droneId)!.SystemId);
            Assert.Equal(MissileFlightStatus.Expired, world.Set<WorldMissile>().Find(MissileId)!.Status);
            return true;
        });
        await live.FlushAsync();
    }

    [Theory]
    [InlineData(47847, 1997, 11, true)]
    [InlineData(48262, 1997, 11, false)]
    [InlineData(621, 26, 6, false)]
    [InlineData(47951, 2009, 11, false)]
    [InlineData(2488, 100, 18, true)]
    public void SuppressorUsesPinnedTargetClasses(int type, int group, int category, bool expected) =>
        Assert.Equal(expected, AbyssalEnvironmentRules.SuppressorTarget(type, group, category));

    [Theory]
    [InlineData(AbyssalWeather.Dark, DogmaAttributeId.MaxVelocity, 150)]
    [InlineData(AbyssalWeather.Electrical, DogmaAttributeId.CapacitorRechargeTime, 50)]
    [InlineData(AbyssalWeather.Exotic, DogmaAttributeId.ScanResolution, 150)]
    [InlineData(AbyssalWeather.Firestorm, DogmaAttributeId.ArmorHitPoints, 150)]
    [InlineData(AbyssalWeather.Gamma, DogmaAttributeId.ShieldCapacity, 150)]
    public void WeatherAppliesTypedBonuses(AbyssalWeather weather, DogmaAttributeId attribute, double expected)
    {
        var result = AbyssalEnvironmentRules.Apply(
            new Dictionary<DogmaAttributeId, double> { [attribute] = 100 },
            new(weather, 30, AbyssalFieldMask.None)
        );
        Assert.Equal(expected, result[attribute]);
    }

    [Fact]
    public void CloudsAndWeatherHaveInclusiveEdgesAndDoNotStackByObserver()
    {
        var field = AbyssalEnvironmentRules.Layout(0, 2)[0];
        Assert.Equal(AbyssalFieldMask.Tachyon, AbyssalEnvironmentRules.Fields(0, 2, field.Position));
        Assert.True(
            AbyssalEnvironmentRules.Fields(0, 2, field.Position + new SpacePosition(field.Range, 0, 0)).HasFlag(AbyssalFieldMask.Tachyon)
        );
        Assert.False(
            AbyssalEnvironmentRules
                .Fields(0, 2, field.Position + new SpacePosition(field.Range + 1, 0, 0))
                .HasFlag(AbyssalFieldMask.Tachyon)
        );
        var values = new Dictionary<DogmaAttributeId, double>
        {
            [DogmaAttributeId.MaxVelocity] = 100,
            [DogmaAttributeId.Agility] = 2,
            [DogmaAttributeId.MaxRange] = 1000,
        };
        var result = AbyssalEnvironmentRules.Apply(values, new(AbyssalWeather.Dark, 30, AbyssalFieldMask.Tachyon));
        Assert.Equal(600, result[DogmaAttributeId.MaxVelocity]);
        Assert.Equal(1, result[DogmaAttributeId.Agility]);
        Assert.Equal(700, result[DogmaAttributeId.MaxRange]);
        Assert.Equal(100, values[DogmaAttributeId.MaxVelocity]);
        Assert.Equal(0, AbyssalEnvironmentRules.BoundaryDamage(75_000));
        Assert.True(AbyssalEnvironmentRules.BoundaryDamage(75_001) > 0);
    }

    [Fact]
    public void ResidentFitChangesOnCloudCrossingAndStaysWarmWithinCloud()
    {
        using Fixture f = new();
        AbyssalTests.Prepare(f);
        long filament = f.Grant(AbyssalRules.TranquilDarkFilament, cargo: true);
        long ship = f.ShipId;
        using ResidentFitCache fits = new(new(f.Db.Path));
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var abyss = f.Db.Services.GetRequiredService<IAbyssalDeadspace>();
        double baseline = Speed();
        var run = abyss.Deploy(f.PilotId, f.ShipId, filament, AbyssalMode.Cruiser).Run;
        using (var change = live.Economy.Begin(new(pilots: [f.PilotId])))
        {
            change.Set<SavedAbyssalRun>().Find(run.Id)!.Seed = 2;
            change.Commit();
        }
        Assert.Equal(baseline * 1.5, Speed(), 6);
        var cloud = AbyssalEnvironmentRules.Layout(0, 2)[0];
        f.Db.Store.SetPosition(f.ShipId, cloud.Position, run.SystemId);
        Assert.Equal(baseline * 6, Speed(), 6);
        _ = Speed();
        using (var measured = SessionPerformance.Begin("Abyssal cloud fitting", _ => { }, force: true)!)
        {
            for (int i = 0; i < 3; i++)
            {
                f.Db.Store.SetPosition(ship, cloud.Position + new SpacePosition(i, 0, 0), run.SystemId);
                Assert.Equal(baseline * 6, Speed(), 6);
            }
            Assert.Equal(0, measured.Commands);
            Assert.Equal(0, measured.Connections);
            Assert.Equal(0, measured.FittingCalculations);
        }
        f.Db.Store.SetPosition(f.ShipId, AbyssalRules.Center(0), run.SystemId);
        Assert.Equal(baseline * 1.5, Speed(), 6);

        double Speed() =>
            live.Economy.Read(
                world =>
                {
                    using CatalogDbContext catalog = new(f.CatalogPath);
                    return PilotFittingStore.ShipAttributes(world, catalog, f.PilotId)[DogmaAttributeId.MaxVelocity];
                },
                new(pilots: [f.PilotId])
            );
    }

    [Fact]
    public async Task BoundaryExposureSurvivesCheckpointAndCannotReplayDamageAtSameTime()
    {
        using Fixture f = new();
        AbyssalTests.Prepare(f);
        long filament = f.Grant(AbyssalRules.TranquilDarkFilament, cargo: true);
        long original = f.ShipId;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var abyss = f.Db.Services.GetRequiredService<IAbyssalDeadspace>();
            var run = abyss.Deploy(f.PilotId, original, filament, AbyssalMode.Cruiser).Run;
            AbyssalTests.KillRoom(f, live, run.Id);
            f.Db.Store.SetPosition(original, new SpacePosition(76_000, 0, 0), run.SystemId);
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            abyss.Advance();
            var health = f.Db.Services.GetRequiredService<Eden.Gameplay.Combat.IPilotHealth>();
            var damaged = health.Read(f.PilotId, f.Clock.Now);
            Assert.True(damaged.Remaining.Shield < damaged.Defense.Shield);
            abyss.Advance();
            Assert.Equal(damaged, health.Read(f.PilotId, f.Clock.Now));
            await live.FlushAsync();
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(15));
        using LiveWorld reopened = new(new(f.Db.Path), f.Clock, _ => { });
        f.Db.Services.GetRequiredService<IAbyssalDeadspace>().Advance();
        Assert.NotEqual(original, f.Db.Store.FindPilot(1000001)!.ShipId);
        Assert.Null(f.Db.Services.GetRequiredService<IAbyssalDeadspace>().Read(f.PilotId));
    }
}
