using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed class PilotHealthTests
{
    private const long PilotId = 90000001;
    private const long ShipId = 1000000000001;
    private static readonly long s_now = DateTime.UtcNow.ToFileTimeUtc();

    private static DamageResult Hit(TestWorld db, DamageVector damage, long now)
    {
        var health = db.Services.GetRequiredService<IPilotHealth>().Read(PilotId, now);
        using WorldDbContext world = new(db.Path);
        using var transaction = world.Database.BeginTransaction();
        var result = PilotHealthStore.Apply(world, ShipId, health.Defense, damage, now);
        world.SaveChanges();
        transaction.Commit();
        return result;
    }

    [Fact]
    public void DamageAndRechargeSurviveRestartWithoutRepairingArmorOrStructure()
    {
        using TestWorld db = new();
        var health = db.Services.GetRequiredService<IPilotHealth>();
        Assert.Equal(new HullDamage(200, 125, 175), health.Read(PilotId, s_now).Remaining);
        var result = Hit(db, new(0, 0, 400, 0), s_now);
        Assert.Equal(new HullDamage(0, 0, 100), result.Remaining);
        Assert.Equal(result.Remaining, health.Read(PilotId, s_now).Remaining);
        _ = new SqliteWorld(db.Path);
        var later = health.Read(PilotId, s_now + TimeSpan.TicksPerMinute);
        Assert.InRange(later.Remaining.Shield, 1, 200);
        Assert.Equal(0, later.Remaining.Armor);
        Assert.Equal(100, later.Remaining.Structure);
        Assert.Equal(result.Remaining, health.Read(PilotId, s_now - TimeSpan.TicksPerSecond).Remaining);
        var foreign = health.Read(90000002, s_now);
        Assert.Equal(foreign.Defense.Undamaged, foreign.Remaining);
        Assert.Throws<ArgumentOutOfRangeException>(() => Hit(db, new(double.NaN, 0, 0, 0), s_now));
        Assert.Equal(result.Remaining, health.Read(PilotId, s_now).Remaining);
    }

    [Fact]
    public void HealthUsesCatalogResonancesAndTrainedDefenseSkillsWithoutForgettingExistingDamage()
    {
        using TestWorld db = new();
        var health = db.Services.GetRequiredService<IPilotHealth>();
        Hit(db, new(0, 0, 400, 0), s_now);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            foreach (var skill in Enum.GetValues<DefenseSkill>())
            {
                catalog.Database.ExecuteSql($"INSERT INTO ItemType(Id,Name,GroupId) VALUES ({(int)skill},{skill.ToString()},257)");
                catalog.Database.ExecuteSql($"INSERT INTO TypeAttribute VALUES ({(int)skill},275,1)");
            }
            // Omitted resonances default to one; explicit zero means immunity.
            catalog.Database.ExecuteSqlRaw(
                "INSERT INTO DogmaAttribute(Id,Name) VALUES (273,'shieldKineticDamageResonance'); INSERT INTO "
                    + "TypeAttribute VALUES (601,273,0)"
            );
        }
        using (WorldDbContext world = new(db.Path))
        {
            foreach (var skill in Enum.GetValues<DefenseSkill>())
            {
                world.Add(
                    new PilotSkill
                    {
                        PilotId = PilotId,
                        TypeId = (int)skill,
                        Points = 8000,
                    }
                );
            }
            world.SaveChanges();
        }
        var changed = health.Read(PilotId, s_now);
        Assert.Equal(230, changed.Defense.Shield, 8);
        Assert.Equal(143.75, changed.Defense.Armor, 8);
        Assert.Equal(201.25, changed.Defense.Structure, 8);
        Assert.Equal(860000 * 0.85, changed.Defense.ShieldRechargeMilliseconds, 8);
        Assert.Equal(18.75, changed.Remaining.Armor, 8);
        Assert.Equal(126.25, changed.Remaining.Structure, 8);
        Assert.Equal(0, changed.Defense.ShieldResonance.Kinetic);
        Assert.Equal(1, changed.Defense.StructureResonance.Kinetic);
    }

    [Fact]
    public async Task ConcurrentHitsDoNotLoseDamageOrRechargeTwice()
    {
        using TestWorld db = new();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Hit(db, new(0, 0, 10, 0), s_now))));
        Assert.Equal(new HullDamage(120, 125, 175), db.Services.GetRequiredService<IPilotHealth>().Read(PilotId, s_now).Remaining);
    }

    [Fact]
    public void NativeDogmaAndBallparkSnapshotsExposeTheSameSavedDamage()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        Hit(db, new(0, 0, 400, 0), s_now);
        using var scope = db.Services.CreateScope();
        var health = scope.ServiceProvider.GetRequiredService<IPilotHealth>();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => new(db.Store, selected, TimeProvider.System, new(), db.Objects),
            10,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var all = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<BlueInstance>(router.CallWire(context, "dogmaIM", "GetAllInfo", Array.Empty<object?>(), _ => { })).State
        );
        var shipInfo = Assert.IsType<Dictionary<long, object?>>(all["shipInfo"]);
        var ship = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(shipInfo[ShipId]).State);
        var attributes = Assert.IsType<Dictionary<long, object?>>(ship["attributes"]);
        Assert.Equal(75d, attributes[(long)DogmaAttributeId.Damage]);
        Assert.Equal(125d, attributes[(long)DogmaAttributeId.ArmorDamage]);
        var instances = Assert.IsType<Dictionary<long, object?>>(Assert.IsType<object?[]>(all["shipState"])[0]);
        var row = Assert.IsType<object?[]>(instances[ShipId]);
        Assert.Equal(75d, row[2]);
        Assert.Equal(125d, row[5]);
        var snapshot = Assert.IsType<BlueInstance>(
            WireAssertions.Materialize(
                Ballpark.Snapshot(
                    new(
                        pilot with
                        {
                            Position = SpacePosition.Zero,
                        },
                        SpacePosition.Zero,
                        null,
                        0,
                        null,
                        Health: health.Read(PilotId, s_now)
                    ),
                    42,
                    db.Objects
                )
            )
        );
        var state = Assert.IsType<Dictionary<string, object?>>(snapshot.State);
        var damage = Assert.IsType<Dictionary<long, object?>>(state["damageState"]);
        var value = Assert.IsType<object?[]>(damage[ShipId]);
        Assert.Equal(new object?[] { 0d, 860000d / 5 }, Assert.IsType<object?[]>(value[0]));
        Assert.Equal(0d, value[1]);
        Assert.Equal(100d / 175, value[2]);
        Assert.NotEmpty(BlueMarshal.Encode(all));
        Assert.NotEmpty(BlueMarshal.Encode(snapshot));
    }

    [Fact]
    public void BoardingAnotherHullDoesNotTransferOrEraseDamage()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        var health = db.Services.GetRequiredService<IPilotHealth>();
        var fleet = db.Services.GetRequiredService<IPilotFleet>();
        Hit(db, new(0, 0, 400, 0), s_now);
        const long spareShip = 2000000000500;
        using (WorldDbContext world = new(db.Path))
        {
            world.Ships.Add(
                new()
                {
                    Id = spareShip,
                    OwnerId = PilotId,
                    TypeId = pilot.ShipTypeId,
                    StationId = pilot.StationId,
                    Name = "Spare",
                }
            );
            world.SaveChanges();
        }
        fleet.Board(PilotId, spareShip, ShipId);
        var fresh = health.Read(PilotId, s_now);
        Assert.Equal(spareShip, fresh.ShipId);
        Assert.Equal(fresh.Defense.Undamaged, fresh.Remaining);
        fleet.Board(PilotId, ShipId, spareShip);
        Assert.Equal(new HullDamage(0, 0, 100), health.Read(PilotId, s_now).Remaining);
    }

    [Fact]
    public void VersionSeventeenUpgradePreservesShipsItemsWalletAndEnergyWithoutInventingDamage()
    {
        using TestWorld db = new();
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                INSERT INTO WorldWallet VALUES (90000001,12345);
                INSERT INTO WorldShipEnergy VALUES (1000000000001,17,134000000000000000);
                DROP TABLE WorldShipHealth; DROP TABLE WorldWreck; DROP TABLE WorldRepairPayment; DROP TABLE WorldNpcAggression; DROP
                    TABLE WorldShipLoss; DROP TABLE WorldNpc; DROP TABLE WorldNpcBounty; DROP TABLE WorldWeaponCooldown; DELETE FROM
                    WorldItem WHERE TypeId=3638;
                PRAGMA user_version=17;
                """
            );
        }
        var pilot = db.Store.FindPilot(1000001)!;
        _ = new SqliteWorld(db.Path);
        _ = new SqliteWorld(db.Path);
        Assert.Equal(pilot, db.Store.FindPilot(1000001));
        using WorldDbContext reopened = new(db.Path);
        Assert.Equal(12345, reopened.Set<PilotWallet>().Find(PilotId)!.Cents);
        Assert.Equal(17, reopened.Set<ShipEnergy>().Find(ShipId)!.Charge);
        Assert.Empty(reopened.Set<ShipHealthState>());
        Assert.Equal(4, reopened.Set<OwnedItem>().Count());
        var fresh = db.Services.GetRequiredService<IPilotHealth>().Read(PilotId, s_now);
        Assert.Equal(fresh.Defense.Undamaged, fresh.Remaining);
        Assert.Throws<SqliteException>(() =>
            reopened.Database.ExecuteSqlRaw("INSERT INTO WorldShipHealth VALUES (1000000000001,-1,0,0,0)")
        );
    }
}
