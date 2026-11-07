using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Persistence;

public sealed class BoosterMutationTests
{
    [Fact]
    public async Task CompetingConsumptionAndFailedWritesPreserveInventoryAndElapsedTraining()
    {
        var clock = CalendarTestData.Clock();
        using var db = BoosterTestData.Create(clock);
        long item = BoosterTestData.Item(db, quantity: 2);
        var initial = ImplantTestData.Training(db).Save(BoosterTestData.Pilot, [new(3327, 1)], true);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(60));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectBooster BEFORE INSERT ON WorldBooster BEGIN SELECT RAISE(ABORT,'test'); END;"
            );
        }
        Assert.Throws<DbUpdateException>(() => BoosterTestData.Consume(db, item));
        Assert.Equal(initial.Queue, ImplantTestData.Training(db).Advance(BoosterTestData.Pilot).Queue);
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Equal(2, world.Set<OwnedItem>().Find(item)!.Quantity);
            Assert.Equal(0, world.Set<PilotSkill>().Find(BoosterTestData.Pilot, 3327)!.Points);
            Assert.Empty(world.Set<SavedBoosterState>());
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectBooster");
        }
        var results = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => Task.Run(() => Record.Exception(() => BoosterTestData.Consume(db, item))))
        );
        Assert.Single(results, e => e is null);
        Assert.IsType<InvalidDataException>(Assert.Single(results, e => e is not null));
        using WorldDbContext saved = new(db.Path);
        Assert.Single(saved.Set<SavedBooster>());
        Assert.Equal(1, saved.Set<OwnedItem>().Find(item)!.Quantity);
        Assert.Equal(30, saved.Set<PilotSkill>().Find(BoosterTestData.Pilot, 3327)!.Points);
    }

    [Fact]
    public void ExpiredSlotsCanBeReusedFromSpaceCargoAndConsumedIdentitiesAreNeverReallocated()
    {
        var clock = CalendarTestData.Clock();
        using var db = BoosterTestData.Create(clock);
        BoosterTestData.ShortLifetime(db);
        long item = BoosterTestData.Item(db, quantity: 2);
        var first = Assert.Single(BoosterTestData.Consume(db, item).Snapshot.Boosters);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(120));
        var pilot = db.Store.FindPilot(1000001)!;
        db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero);
        using (WorldDbContext world = new(db.Path))
        {
            var cargo = world.Set<OwnedItem>().Find(item)!;
            cargo.LocationId = pilot.ShipId;
            cargo.FlagId = (int)InventoryFlag.Cargo;
            world.SaveChanges();
        }
        var second = BoosterTestData.Store(db).Consume(BoosterTestData.Pilot, item, pilot.ShipId);
        Assert.Equal(item, Assert.Single(second.Snapshot.Boosters).ItemId);
        Assert.Equal(2, second.Snapshot.Revision);
        using WorldDbContext saved = new(db.Path);
        Assert.True(InventoryPersistence.NextId(saved) > first.ItemId);
        Assert.Null(saved.Set<OwnedItem>().Find(item));
    }

    [Theory]
    [InlineData("calendar")]
    [InlineData("age")]
    [InlineData("effects")]
    [InlineData("unpublished")]
    public void ExpiredAndUnsupportedProfilesRejectWithoutConsumingAnything(string reason)
    {
        var clock = CalendarTestData.Clock();
        using var db = BoosterTestData.Create(clock);
        long item = BoosterTestData.Item(db);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            if (reason is "calendar" or "age")
            {
                var id = reason == "age" ? DogmaAttributeId.BoosterMaximumAgeHours : DogmaAttributeId.BoosterLastInjectionDate;
                var row = catalog
                    .Set<TypeAttribute>()
                    .AsTracking()
                    .Single(a => a.ItemTypeId == BoosterTestData.Master && a.AttributeId == (int)id);
                row.Value = reason == "age" ? 1 : (clock.UtcNow.UtcDateTime - DateTime.UnixEpoch).TotalDays;
            }
            else
            {
                string dataset = reason == "effects" ? "client/typedogma" : "client/types";
                catalog.Set<SourceRecord>().AsTracking().Single(r => r.Dataset == dataset && r.Key == "48582").Data =
                    reason == "effects"
                        ? "{\"dogmaEffects\":[{\"effectID\":302},{\"effectID\":304},{\"effectID\":306},"
                            + "{\"effectID\":308},{\"effectID\":310},{\"effectID\":999}]}"
                        : "{\"published\":0,\"volume\":1}";
            }
            catalog.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => BoosterTestData.Consume(db, item));
        using WorldDbContext saved = new(db.Path);
        Assert.NotNull(saved.Set<OwnedItem>().Find(item));
        Assert.Empty(saved.Set<SavedBooster>());
        Assert.Empty(saved.Set<SavedBoosterState>());
    }

    [Fact]
    public void Version58MigrationRetainsTheExistingActiveQueueAndInventory()
    {
        var clock = CalendarTestData.Clock();
        using var db = BoosterTestData.Create(clock);
        long item = BoosterTestData.Item(db);
        var initial = ImplantTestData.Training(db).Save(BoosterTestData.Pilot, [new(3327, 1)], true);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "DROP TABLE WorldBooster; DROP TABLE WorldBoosterState; "
                    + "ALTER TABLE WorldSkillQueue DROP COLUMN PeriodsJson; PRAGMA user_version=58;"
            );
        }
        WorldInitialization.Open(db.Path);
        Assert.Equal(initial.Queue, ImplantTestData.Training(db).Advance(BoosterTestData.Pilot).Queue);
        Assert.Empty(BoosterTestData.Store(db).Read(BoosterTestData.Pilot).Boosters);
        Assert.Single(BoosterTestData.Consume(db, item).Snapshot.Boosters);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }
}
