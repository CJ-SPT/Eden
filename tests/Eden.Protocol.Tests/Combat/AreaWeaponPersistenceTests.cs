using System.Text.Json;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class AreaWeaponTests
{
    [Theory]
    [InlineData(27922, true)]
    [InlineData(27924, false)]
    [InlineData(34264, false)]
    public void UtilityBombsApplyLockBreakingOrSignatureScaledCapacitorDrain(int charge, bool lockbreaker)
    {
        using var fixture = Prepare(Manticore);
        long module = fixture.Fit(BombLauncher);
        Load(fixture, module, charge);
        var target = Place(fixture, new(30000, 0, 0));
        using CatalogDbContext catalog = new(fixture.CatalogPath);
        var profile = AreaWeaponCatalog.Bomb(catalog, charge);
        using LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var locks = fixture.Db.Services.GetRequiredService<Eden.Gameplay.Targeting.IPilotTargetLocks>();
        locks.Add(target.ShipId, fixture.ShipId);
        var store = fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>();
        var attributes = fixture.Db.Services.GetRequiredService<IPilotFitting>().ShipAttributes(target.PilotId);
        _ = store.Activate(
            fixture.PilotId,
            fixture.ShipId,
            module,
            DogmaEffect.UseMissiles,
            new(1, 0, 0),
            new(100, 0, 0),
            null,
            fixture.Clock.Now
        );
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(profile.Lifetime));
        store.Advance(SystemId, fixture.Clock.Now);
        if (lockbreaker)
        {
            Assert.False(locks.Contains(target.ShipId, fixture.ShipId));
            Assert.Empty(fixture.Db.Services.GetRequiredService<IPilotElectronicWarfare>().Incoming(target.ShipId));
        }
        else
        {
            double expected = Math.Max(
                0,
                attributes[DogmaAttributeId.CapacitorCapacity]
                    - profile.Power.CapacitorDrain * Math.Min(1, attributes[DogmaAttributeId.SignatureRadius] / profile.SignatureResolution)
            );
            Assert.Equal(expected, live.Economy.Inspect(world => world.Set<ShipEnergy>().Find(target.ShipId)!.Charge), 6);
        }
    }

    [Fact]
    public async Task CheckpointRetainsTheFlightAndRestartCancelsItWithoutRefundOrOfflineDetonation()
    {
        using var fixture = Prepare(Manticore);
        long module = fixture.Fit(BombLauncher);
        Load(fixture, module, Concussion);
        var target = Place(fixture, new(30000, 0, 0));
        long ready;
        using (LiveWorld live = new(new(fixture.Db.Path), fixture.Clock, _ => { }))
        {
            var store = fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>();
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
            ready = paid.ReadyAt;
            using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
            snapshot.Project();
            using (WorldDbContext image = new(snapshot.Database))
            {
                var flight = Assert.Single(image.Set<SavedAreaWeapon>()).Flight();
                Assert.Equal(Concussion, flight.Bomb!.TypeId);
                Assert.Equal(1, image.Set<OwnedItem>().Single(item => item.LocationId == module).Quantity);
            }
            await live.FlushAsync();
        }
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(20));
        WorldInitialization.Initialize(new(fixture.Db.Path), fixture.Clock);
        using LiveWorld reopened = new(new(fixture.Db.Path), fixture.Clock, _ => { });
        var weapons = fixture.Db.Services.GetRequiredService<IPilotAreaWeapons>();
        weapons.Advance(SystemId, fixture.Clock.Now);
        ResidentMonitorAssertions.WithoutSql(() =>
        {
            weapons.Advance(SystemId, fixture.Clock.Now);
            Assert.Empty(weapons.Projectiles(SystemId, fixture.Clock.Now));
        });
        using WorldDbContext saved = new(fixture.Db.Path);
        Assert.Empty(saved.Set<SavedAreaWeapon>());
        Assert.Null(saved.Set<ShipHealthState>().Find(target.ShipId));
        Assert.Equal(1, saved.Set<OwnedItem>().Single(item => item.LocationId == module).Quantity);
        Assert.Equal(ready, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }

    [Fact]
    public void LegacySingleTargetEffectsMigrateWithoutLossAndAllowIndependentAreaRecipients()
    {
        using var fixture = Prepare();
        long module = fixture.Fit(BurstJammer);
        var victim = fixture.Db.Store.FindPilot(1000002)!;
        ElectronicWarfareModule profile = new(
            module,
            BurstJammer,
            DogmaEffect.ProjectorWeb,
            NpcEwarKind.Webifying,
            40000,
            0,
            10000,
            0,
            new(WebPercent: -55)
        );
        ElectronicWarfareContact contact = new(
            fixture.PilotId,
            fixture.ShipId,
            profile,
            new(victim.ShipId, TackleTargetKind.Ship),
            profile.Power,
            fixture.Clock.Now,
            fixture.Clock.Now + TimeSpan.FromSeconds(40).Ticks
        );
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldPlayerEwar;
                CREATE TABLE WorldPlayerEwar(
                    ModuleId INTEGER PRIMARY KEY REFERENCES WorldItem(Id),TargetId INTEGER NOT NULL,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id),SourceShipId INTEGER NOT NULL REFERENCES WorldShip(Id),
                    SystemId INTEGER NOT NULL,TargetKind INTEGER NOT NULL,ContactJson TEXT NOT NULL);
                """
            );
            string json = JsonSerializer.Serialize(contact);
            world.Database.ExecuteSql(
                $"INSERT INTO WorldPlayerEwar VALUES ({module},{victim.ShipId},{fixture.PilotId},{fixture.ShipId},{SystemId},0,{json})"
            );
            PlayerEwarSchema.Upgrade(world);
            PlayerEwarSchema.Upgrade(world);
            Assert.Equal(json, world.Set<WorldPlayerEwar>().Single().ContactJson);
            world.Add(
                new WorldPlayerEwar
                {
                    ModuleId = module,
                    TargetId = fixture.ShipId,
                    PilotId = fixture.PilotId,
                    SourceShipId = fixture.ShipId,
                    SystemId = SystemId,
                    ContactJson = JsonSerializer.Serialize(contact with { Target = new(fixture.ShipId, TackleTargetKind.Ship) }),
                }
            );
            world.SaveChanges();
        }
        using WorldDbContext reopened = new(fixture.Db.Path);
        Assert.Equal(2, reopened.Set<WorldPlayerEwar>().Count());
        Assert.NotNull(reopened.Set<WorldPlayerEwar>().Find(module, victim.ShipId));
        Assert.NotNull(reopened.Set<WorldPlayerEwar>().Find(module, fixture.ShipId));
    }
}
