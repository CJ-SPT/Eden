using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Targeting;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Propulsion;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class AreaWeaponTests
{
    private const int SystemId = 30000142;
    private const int Scorpion = 640;
    private const int Manticore = 12032;
    private const int BurstJammer = 580;
    private const int Grappler = 41040;
    private const int BombLauncher = 27914;
    private const int Concussion = 27912;
    private const int WebProjector = 40635;

    [Fact]
    public void PublishedAreaWeaponAndGrapplerProfilesHaveProductionFittingAndAmmunitionOwners()
    {
        using Fixture fixture = new();
        PassiveCatalogFixture.LoadAreaWeapons(fixture.CatalogPath);
        using CatalogDbContext catalog = new(fixture.CatalogPath);
        var types = catalog.Set<ItemType>().ToArray();
        int modules = 0;
        int charges = 0;
        foreach (var type in types)
        {
            if (catalog.Read(CatalogData.Client.Types, (long)type.Id)?.Published != 1)
            {
                continue;
            }
            if (AreaWeaponRules.IsModule(type.GroupId) || type.GroupId == TackleDefinitions.GrapplerGroup)
            {
                Assert.True(FittingCatalog.Definition(catalog, type.Id) is not null, type.Name);
                if (type.GroupId is AreaWeaponRules.BurstJammerGroup or TackleDefinitions.GrapplerGroup)
                {
                    Assert.True(OverloadCatalog.Definition(catalog, type.Id) is not null, type.Name + " overload");
                }
                modules++;
            }
            if (AreaWeaponRules.IsBomb(type.GroupId) || type.GroupId == AreaWeaponRules.DefenderChargeGroup)
            {
                Assert.True(AreaWeaponCatalog.Charge(catalog, type.Id, type.GroupId), type.Name);
                charges++;
            }
        }
        Assert.Equal(29, modules);
        Assert.Equal(8, charges);
    }

    [Fact]
    public void BurstBreaksMultipleOutgoingLocksWithoutBlockingRelockAndRejectsDuplicatePayment()
    {
        using var fixture = Prepare();
        long module = fixture.Fit(BurstJammer);
        var target = Place(fixture, new(1000, 0, 0));
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var locks = fixture.Db.Services.GetRequiredService<IPilotTargetLocks>();
        var breaks = fixture.Db.Services.GetRequiredService<IPilotLockBreaks>();
        locks.Add(target.ShipId, fixture.ShipId);
        locks.Add(target.ShipId, 123);
        var store = fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>();
        var paid = store.Activate(
            fixture.PilotId,
            fixture.ShipId,
            module,
            DogmaEffect.BurstJammer,
            new(1, 0, 0),
            SpacePosition.Zero,
            null,
            fixture.Clock.Now
        );
        Assert.True(paid.Capacitor < 10000);
        Assert.False(locks.Contains(target.ShipId, fixture.ShipId));
        Assert.False(locks.Contains(target.ShipId, 123));
        Assert.Equal(1, breaks.BreakRevision(target.ShipId));
        Assert.Empty(fixture.Db.Services.GetRequiredService<IPilotElectronicWarfare>().Incoming(target.ShipId));
        locks.Add(target.ShipId, fixture.ShipId);
        Assert.True(locks.Contains(target.ShipId, fixture.ShipId));
        Assert.Throws<InvalidDataException>(() =>
            store.Activate(
                fixture.PilotId,
                fixture.ShipId,
                module,
                DogmaEffect.BurstJammer,
                new(1, 0, 0),
                SpacePosition.Zero,
                null,
                fixture.Clock.Now
            )
        );
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        snapshot.Project();
        using WorldDbContext saved = new(snapshot.Database);
        Assert.Equal(paid.Capacitor, saved.Set<ShipEnergy>().Find(fixture.ShipId)!.Charge);
        Assert.Equal(paid.ReadyAt, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Fact]
    public void GrapplerUsesFalloffAndRestoresSpeedWhenStopped()
    {
        using var fixture = Prepare();
        long module = fixture.Fit(Grappler);
        var source = fixture.Db.Store.FindPilot(1000001)!;
        var victim = fixture.Db.Store.FindPilot(1000002)!;
        double distance = 9000 + source.Flight.ShipRadius + victim.Flight.ShipRadius;
        victim = Place(fixture, new(distance, 0, 0));
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var store = fixture.Db.Services.GetRequiredService<IPilotTackle>();
        double speed = fixture.Db.Services.GetRequiredService<IPilotFitting>().ShipAttributes(victim.PilotId)[DogmaAttributeId.MaxVelocity];
        var paid = store.Activate(
            fixture.PilotId,
            module,
            DogmaEffect.StasisWebifier,
            new(victim.ShipId, TackleTargetKind.Ship),
            new HashSet<long> { victim.ShipId },
            fixture.Clock.Now
        );
        Assert.Equal(-40, paid.Module.WebPercent, 6);
        Assert.Equal(
            speed * .6,
            fixture.Db.Services.GetRequiredService<IPilotFitting>().ShipAttributes(victim.PilotId)[DogmaAttributeId.MaxVelocity],
            6
        );
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(paid.ReadyAt - fixture.Clock.Now));
        store.Stop(fixture.PilotId, fixture.ShipId, module);
        Assert.Equal(
            speed,
            fixture.Db.Services.GetRequiredService<IPilotFitting>().ShipAttributes(victim.PilotId)[DogmaAttributeId.MaxVelocity],
            6
        );
    }

    [Fact]
    public void BombTravelsWithoutATargetAndDetonatesOnlyOnceWithSignatureScaledDamage()
    {
        using var fixture = Prepare(Manticore);
        long module = fixture.Fit(BombLauncher);
        Load(fixture, module, Concussion);
        var victim = Place(fixture, new(30000, 0, 0));
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var store = fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>();
        var targetAttributes = fixture.Db.Services.GetRequiredService<IPilotFitting>().ShipAttributes(victim.PilotId);
        var initial = fixture.Db.Services.GetRequiredService<IPilotHealth>().Read(victim.PilotId, fixture.Clock.Now);
        var paid = store.Activate(
            fixture.PilotId,
            fixture.ShipId,
            module,
            DogmaEffect.UseMissiles,
            new(1, 0, 0),
            new(100, 0, 0),
            null,
            fixture.Clock.Now
        );
        Assert.NotNull(paid.FlightId);
        Assert.Equal(1, paid.Charge!.Current!.Quantity);
        var payload = live.Economy.Inspect(world => world.Set<SavedAreaWeapon>().Find(paid.FlightId.Value)!.Flight().Bomb!);
        double signatureFactor = Math.Min(1, targetAttributes[DogmaAttributeId.SignatureRadius] / payload.SignatureResolution);
        var expected = CombatDamage.Apply(
            initial.Remaining,
            initial.Defense,
            payload.Damage.Scale(signatureFactor),
            victim.ShipId,
            fixture.Clock.Now + TimeSpan.FromSeconds(12).Ticks
        );
        var launched = Assert.Single(store.Projectiles(SystemId, fixture.Clock.Now));
        Assert.Equal(SpacePosition.Zero, launched.Flight.Position);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(6));
        var halfway = Assert.Single(store.Projectiles(SystemId, fixture.Clock.Now));
        Assert.Equal(new(15000, 0, 0), halfway.Flight.Position);
        Assert.NotEmpty(Ballpark.AddMissiles([halfway], 0).State);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(6));
        store.Advance(SystemId, fixture.Clock.Now);
        var damaged = fixture.Db.Services.GetRequiredService<IPilotHealth>().Read(victim.PilotId, fixture.Clock.Now);
        Assert.Equal(expected.Remaining, damaged.Remaining);
        Assert.True(damaged.Remaining.Shield < damaged.Defense.Shield);
        Assert.Empty(store.Projectiles(SystemId, fixture.Clock.Now));
        Assert.True(store.Impacted(paid.FlightId.Value));
        store.Advance(SystemId, fixture.Clock.Now);
        Assert.Equal(damaged, fixture.Db.Services.GetRequiredService<IPilotHealth>().Read(victim.PilotId, fixture.Clock.Now));
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        snapshot.Project();
        using WorldDbContext saved = new(snapshot.Database);
        Assert.Empty(saved.Set<SavedAreaWeapon>());
        Assert.Equal(paid.ReadyAt, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Fact]
    public void ProjectorPaysFuelSpoolsTracksOccupantsAndExpiresAtTheExactDeadline()
    {
        using var fixture = Prepare(projectors: true);
        long module = fixture.Fit(WebProjector);
        long fuel = fixture.Grant(16272, 300, cargo: true);
        var victim = Place(fixture, new(50000, 0, 0));
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var store = fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>();
        var fitting = fixture.Db.Services.GetRequiredService<IPilotFitting>();
        double speed = fitting.ShipAttributes(victim.PilotId)[DogmaAttributeId.MaxVelocity];
        var paid = store.Activate(
            fixture.PilotId,
            fixture.ShipId,
            module,
            DogmaEffect.ProjectorWeb,
            new(1, 0, 0),
            SpacePosition.Zero,
            new(50000, 0, 0),
            fixture.Clock.Now
        );
        Assert.Equal(50, Assert.Single(paid.InventoryChanges).Item.Quantity);
        Assert.Equal(speed, fitting.ShipAttributes(victim.PilotId)[DogmaAttributeId.MaxVelocity]);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(10));
        store.Advance(SystemId, fixture.Clock.Now);
        Assert.Equal(speed * .45, fitting.ShipAttributes(victim.PilotId)[DogmaAttributeId.MaxVelocity], 6);
        Assert.Single(fixture.Db.Services.GetRequiredService<IPilotElectronicWarfare>().Incoming(victim.ShipId));
        fixture.Db.Store.SetPosition(victim.ShipId, new(100000, 0, 0), SystemId);
        store.Advance(SystemId, fixture.Clock.Now);
        Assert.Equal(speed, fitting.ShipAttributes(victim.PilotId)[DogmaAttributeId.MaxVelocity]);
        fixture.Db.Store.SetPosition(victim.ShipId, new(50000, 0, 0), SystemId);
        store.Advance(SystemId, fixture.Clock.Now);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(40) - TimeSpan.FromTicks(1));
        Assert.Equal(speed * .45, fitting.ShipAttributes(victim.PilotId)[DogmaAttributeId.MaxVelocity], 6);
        using (live.Enter(systemId: SystemId))
        {
            LiveTimedModules timers = new(live, SystemId);
            long now = fixture.Clock.Now;
            timers.Expire(now);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int check = 0; check < 1024; check++)
            {
                timers.Expire(now);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.InRange(allocated, 0, 1024);
        }
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Equal(speed, fitting.ShipAttributes(victim.PilotId)[DogmaAttributeId.MaxVelocity]);
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        snapshot.Project();
        using WorldDbContext saved = new(snapshot.Database);
        Assert.Equal(50, saved.Set<OwnedItem>().Find(fuel)!.Quantity);
        Assert.Empty(saved.Set<WorldPlayerEwar>());
    }

    private static Fixture Prepare(int hull = Scorpion, bool projectors = false)
    {
        Fixture fixture = new();
        PassiveCatalogFixture.LoadAreaWeapons(fixture.CatalogPath);
        using CatalogDbContext catalog = new(fixture.CatalogPath, writable: true);
        string system = "{\"securityStatus\":0}";
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142',{system})");
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)DogmaAttributeId.GravimetricStrength},1)");
        if (projectors)
        {
            // Explicit artificial budgets exercise module operation while real
            // supercarrier hull admission remains a separate milestone.
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO TypeAttribute VALUES ({hull},{(int)DogmaAttributeId.PowerOutput},1000000)"
            );
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO TypeAttribute VALUES ({hull},{(int)DogmaAttributeId.CapacitorCapacity},1000000)"
            );
            catalog.Database.ExecuteSql($"UPDATE ItemType SET Capacity=1000000 WHERE Id={hull}");
            foreach (int type in new[] { 27678, 40635, 40636, 40696, 40697, 40698, 40699 })
            {
                catalog.Database.ExecuteSql(
                    $"INSERT OR REPLACE INTO TypeAttribute VALUES ({type},{(int)DogmaAttributeId.CanFitShipGroup1},27)"
                );
            }
        }
        using WorldDbContext world = new(fixture.Db.Path);
        world.Ships.Find(fixture.ShipId)!.TypeId = hull;
        world.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Omega;
        world.Pilots.Find(fixture.PilotId)!.SafetyLevel = ShipSafetyLevel.Disabled;
        world.SaveChanges();
        return fixture;
    }

    private static DockedPilot Place(Fixture fixture, SpacePosition target)
    {
        var victim = fixture.Db.Store.FindPilot(1000002)!;
        fixture.Db.Store.SetPosition(fixture.ShipId, SpacePosition.Zero, SystemId);
        fixture.Db.Store.SetPosition(victim.ShipId, target, SystemId);
        return fixture.Db.Store.FindPilot(1000002)!;
    }

    private static void Load(Fixture fixture, long module, int charge)
    {
        fixture.Train(AreaWeaponRules.BombDeploymentSkill, 5);
        fixture.Train(AreaWeaponRules.DefenderSkill, 5);
        long stack = fixture.Grant(charge, 2);
        fixture
            .Db.Services.GetRequiredService<IPilotAmmunition>()
            .Load(fixture.PilotId, fixture.ShipId, [module], [stack], fixture.StationId, fixture.Clock.Now);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(10));
    }
}
