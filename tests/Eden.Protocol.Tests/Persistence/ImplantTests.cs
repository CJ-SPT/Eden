using System.Text.Json;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class ImplantTestData
{
    internal const long Pilot = 90000001;
    internal const int Cybernetics = 3411;
    internal const int BasicOcular = 9899;
    internal const int StandardOcular = 10216;

    // Pinned build 3662168 client/types and typedogma: all 25 published Cyber Learning types.
    internal static readonly (int Type, int Slot, int Bonus, int Required)[] Profiles =
    [
        (9899, 1, 3, 1),
        (9941, 2, 3, 1),
        (9942, 3, 3, 1),
        (9943, 4, 3, 1),
        (9956, 5, 3, 1),
        (10208, 2, 4, 4),
        (10209, 2, 5, 5),
        (10212, 3, 4, 4),
        (10213, 3, 5, 5),
        (10216, 1, 4, 4),
        (10217, 1, 5, 5),
        (10221, 4, 4, 4),
        (10222, 4, 5, 5),
        (10225, 5, 4, 4),
        (10226, 5, 5, 5),
        (13283, 1, 1, 1),
        (13284, 2, 1, 1),
        (13285, 3, 1, 1),
        (13286, 5, 1, 1),
        (13287, 4, 1, 1),
        (14295, 1, 2, 1),
        (14296, 3, 2, 1),
        (14297, 2, 2, 1),
        (14298, 4, 2, 1),
        (14299, 5, 2, 1),
    ];

    internal static TestWorld Create(TestClock clock, bool clones = false)
    {
        TestWorld db = new(clock: clock);
        if (clones)
        {
            JumpCloneTestData.Prepare(db);
        }
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Add(new ItemCategory { Id = (int)InventoryCategory.Implant, Name = "Implant" });
        catalog.Add(
            new ItemGroup
            {
                Id = ImplantRules.LearningGroup,
                CategoryId = (int)InventoryCategory.Implant,
                Name = "Cyber Learning",
            }
        );
        catalog.Add(
            new ItemType
            {
                Id = Cybernetics,
                GroupId = 257,
                Name = "Cybernetics",
            }
        );
        var ids = new[] { 175, 176, 177, 178, 179, 182, 277, 331 };
        foreach (int id in ids.Where(id => catalog.Set<DogmaAttribute>().Find(id) is null))
        {
            catalog.Add(new DogmaAttribute { Id = id, Name = $"implant-{id}" });
        }
        catalog.Add(
            new TypeAttribute
            {
                ItemTypeId = Cybernetics,
                AttributeId = (int)DogmaAttributeId.SkillTimeConstant,
                Value = 3,
            }
        );
        foreach (var profile in Profiles)
        {
            catalog.Add(
                new ItemType
                {
                    Id = profile.Type,
                    GroupId = ImplantRules.LearningGroup,
                    Name = $"Learning implant {profile.Type}",
                }
            );
            int bonus = profile.Slot switch
            {
                1 => 178,
                2 => 177,
                3 => 179,
                4 => 176,
                _ => 175,
            };
            foreach (
                var (attribute, value) in new[] { (331, profile.Slot), (bonus, profile.Bonus), (182, Cybernetics), (277, profile.Required) }
            )
            {
                catalog.Add(
                    new TypeAttribute
                    {
                        ItemTypeId = profile.Type,
                        AttributeId = attribute,
                        Value = value,
                    }
                );
            }
            Add("client/types", profile.Type.ToString(), new { published = 1, volume = 1 });
            Add(
                "client/typedogma",
                profile.Type.ToString(),
                new
                {
                    dogmaEffects = new[] { 302, 304, 306, 308, 310 }.Select(effect => new { effectID = effect, isDefault = 0 }).ToArray(),
                }
            );
        }
        var alpha = catalog.Set<SourceRecord>().AsTracking().Single(r => r.Dataset == "client/clonegrades" && r.Key == "\"1\"");
        alpha.Data = JsonSerializer.Serialize(
            new { skills = new[] { new { typeID = 3327, level = 4 }, new { typeID = Cybernetics, level = 3 } } }
        );
        catalog.SaveChanges();
        using WorldDbContext world = new(db.Path);
        world.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Omega;
        world.Set<PilotSkill>().Find(Pilot, 3327)!.Points = 0;
        world.Add(
            new PilotSkill
            {
                PilotId = Pilot,
                TypeId = Cybernetics,
                Points = TrainedSkill.PointsForLevel(3, 5),
            }
        );
        world.SaveChanges();
        return db;

        void Add(string dataset, string key, object data) =>
            catalog.Add(
                new SourceRecord
                {
                    Dataset = dataset,
                    Key = key,
                    Data = JsonSerializer.Serialize(data),
                }
            );
    }

    internal static long Item(TestWorld db, int type = BasicOcular, int quantity = 1)
    {
        using WorldDbContext world = new(db.Path);
        long id = InventoryPersistence.NextId(world);
        world.Add(
            new OwnedItem
            {
                Id = id,
                TypeId = type,
                OwnerId = Pilot,
                LocationId = HomeStationTestData.Origin,
                FlagId = (int)InventoryFlag.Hangar,
                Quantity = quantity,
            }
        );
        world.SaveChanges();
        return id;
    }

    internal static IPilotImplants Store(TestWorld db) => db.Services.GetRequiredService<IPilotImplants>();

    internal static IPilotTraining Training(TestWorld db) => db.Services.GetRequiredService<IPilotTraining>();

    internal static void Alpha(TestWorld db)
    {
        using WorldDbContext world = new(db.Path);
        world.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Alpha;
        world.SaveChanges();
    }
}

public sealed class ImplantTests
{
    public static IEnumerable<object[]> Profiles => ImplantTestData.Profiles.Select(p => new object[] { p.Type, p.Slot, p.Bonus });

    [Theory]
    [MemberData(nameof(Profiles))]
    public void PublishedProfilesConsumeOneItemApplyTheirAttributeAndSurviveRestart(int type, int slot, int bonus)
    {
        var clock = CalendarTestData.Clock();
        using var db = ImplantTestData.Create(clock);
        using LiveWorld live = new(new(db.Path), clock, _ => { });
        Assert.Empty(ImplantTestData.Store(db).Read(ImplantTestData.Pilot).Implants);
        long item = ImplantTestData.Item(db, type, 2);
        var result = ImplantTestData.Store(db).Install(ImplantTestData.Pilot, item);
        var installed = Assert.Single(result.Snapshot.Implants);
        Assert.Equal(type, installed.Profile.TypeId);
        Assert.Equal(slot, installed.Profile.Slot);
        Assert.Equal(bonus, installed.Profile.Bonus);
        Assert.NotEqual(item, installed.ItemId);
        var attribute = ImplantRules.AttributeForSlot(slot);
        Assert.Equal(CharacterAttributes.Default.ToDogma()[attribute] + bonus, result.Snapshot.Attributes.ToDogma()[attribute]);
        Assert.Equal(1, Assert.Single(result.Changes).Item.Quantity);
        Assert.Throws<InvalidDataException>(() => ImplantTestData.Store(db).Install(ImplantTestData.Pilot, item));
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        var implants = reopened.GetRequiredService<IPilotImplants>();
        Assert.Equal(installed, Assert.Single(implants.Read(ImplantTestData.Pilot).Implants));
        var sheet = reopened.GetRequiredService<IPilotCharacterSheet>();
        Assert.Equal(CharacterAttributes.Default, sheet.ReadAttributes(ImplantTestData.Pilot).Attributes);
        Assert.Equal(result.Snapshot.Attributes, sheet.ReadEffectiveAttributes(ImplantTestData.Pilot));
        Assert.Empty(implants.Destroy(ImplantTestData.Pilot, installed.ItemId).Snapshot.Implants);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(1, saved.Set<OwnedItem>().Find(item)!.Quantity);
        Assert.Null(saved.Set<OwnedItem>().Find(installed.ItemId));
        Assert.True(InventoryPersistence.NextId(saved) > installed.ItemId);
    }

    [Fact]
    public void ActiveTrainingUsesOldProgressAndNewRateWithoutDuplicateStartHistory()
    {
        var clock = CalendarTestData.Clock();
        using var db = ImplantTestData.Create(clock);
        ImplantTestData.Alpha(db);
        var training = ImplantTestData.Training(db);
        training.Save(ImplantTestData.Pilot, [new(3327, 1), new(3327, 2)], true);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(100));
        var installed = ImplantTestData.Store(db).Install(ImplantTestData.Pilot, ImplantTestData.Item(db));
        Assert.Equal(25, installed.Training.Queue[0].StartPoints);
        Assert.Equal(clock.Now + (long)Math.Ceiling(225d * TimeSpan.TicksPerMinute / 16.5), installed.Training.Queue[0].EndTime);
        Assert.Equal(installed.Training.Queue[0].EndTime, installed.Training.Queue[1].StartTime);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(120));
        var removed = ImplantTestData.Store(db).Destroy(ImplantTestData.Pilot, Assert.Single(installed.Snapshot.Implants).ItemId);
        // Saved queue durations round up to ticks; partial SP rounds down.
        Assert.Equal(57, removed.Training.Queue[0].StartPoints);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(772).Ticks, removed.Training.Queue[0].EndTime);
        Assert.Single(training.History(ImplantTestData.Pilot, 100));
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        Assert.Empty(training.Advance(ImplantTestData.Pilot).Queue);
        Assert.Empty(training.Advance(ImplantTestData.Pilot).ChangedSkills);
        Assert.Equal(2, training.History(ImplantTestData.Pilot, 100).Count(e => e.EventId == 53));
    }

    [Fact]
    public void PausedQueuesStayPausedAndRemappingKeepsBasePointsSeparateFromBonuses()
    {
        var clock = CalendarTestData.Clock();
        using var db = ImplantTestData.Create(clock);
        var training = ImplantTestData.Training(db);
        training.Save(ImplantTestData.Pilot, [new(3327, 1)], false);
        var installed = ImplantTestData.Store(db).Install(ImplantTestData.Pilot, ImplantTestData.Item(db));
        Assert.Null(Assert.Single(installed.Training.Queue).StartTime);
        var sheet = db.Services.GetRequiredService<IPilotCharacterSheet>();
        var remap = new CharacterAttributes(17, 17, 17, 27, 21);
        var remapped = sheet.Remap(ImplantTestData.Pilot, remap);
        Assert.Null(Assert.Single(remapped.Training.Queue).StartTime);
        Assert.Equal(remap, sheet.ReadAttributes(ImplantTestData.Pilot).Attributes);
        Assert.Equal(30, sheet.ReadEffectiveAttributes(ImplantTestData.Pilot).Perception);
        clock.AdvanceWallTime(TimeSpan.FromDays(3));
        Assert.Equal(0, Assert.Single(training.Advance(ImplantTestData.Pilot).Queue).StartPoints);
    }

    [Fact]
    public void AlphaRequirementsRejectNewHighGradeImplantsButKeepAlreadyInstalledBonuses()
    {
        var clock = CalendarTestData.Clock();
        using var db = ImplantTestData.Create(clock);
        long item = ImplantTestData.Item(db, ImplantTestData.StandardOcular);
        ImplantTestData.Alpha(db);
        Assert.Throws<InvalidDataException>(() => ImplantTestData.Store(db).Install(ImplantTestData.Pilot, item));
        using (WorldDbContext world = new(db.Path))
        {
            Assert.NotNull(world.Set<OwnedItem>().Find(item));
            world.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Omega;
            world.SaveChanges();
        }
        var installed = ImplantTestData.Store(db).Install(ImplantTestData.Pilot, item);
        ImplantTestData.Alpha(db);
        Assert.Equal(installed.Snapshot.Implants, ImplantTestData.Store(db).Read(ImplantTestData.Pilot).Implants);
        Assert.Equal(24, ImplantTestData.Store(db).Read(ImplantTestData.Pilot).Attributes.Perception);
    }

    [Fact]
    public void InstallationInSpaceUsesCurrentCargoAndKeepsTheImplantWithThePilot()
    {
        var clock = CalendarTestData.Clock();
        using var db = ImplantTestData.Create(clock);
        long item = ImplantTestData.Item(db);
        var pilot = db.Store.FindPilot(1000001)!;
        db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero);
        Assert.Throws<InvalidDataException>(() => ImplantTestData.Store(db).Install(ImplantTestData.Pilot, item));
        using (WorldDbContext world = new(db.Path))
        {
            var cargo = world.Set<OwnedItem>().Find(item)!;
            cargo.LocationId = pilot.ShipId;
            cargo.FlagId = (int)InventoryFlag.Cargo;
            world.SaveChanges();
        }
        var installed = Assert.Single(ImplantTestData.Store(db).Install(ImplantTestData.Pilot, item).Snapshot.Implants);
        Assert.Equal(item, installed.ItemId);
        Assert.Equal(23, ImplantTestData.Store(db).Read(ImplantTestData.Pilot).Attributes.Perception);
        Assert.Empty(ImplantTestData.Store(db).Destroy(ImplantTestData.Pilot, item).Snapshot.Implants);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("remote")]
    [InlineData("otherShip")]
    [InlineData("missing")]
    [InlineData("singleton")]
    [InlineData("skill")]
    [InlineData("unpublished")]
    [InlineData("extraEffect")]
    public void RejectionsLeaveInventoryTrainingAndCloneStateUnchanged(string reason)
    {
        var clock = CalendarTestData.Clock();
        using var db = ImplantTestData.Create(clock);
        long id = ImplantTestData.Item(db, quantity: 2);
        using (WorldDbContext world = new(db.Path))
        {
            var item = world.Set<OwnedItem>().Find(id)!;
            switch (reason)
            {
                case "foreign":
                    item.OwnerId = 90000002;
                    break;
                case "remote":
                    item.LocationId = HomeStationTestData.Other;
                    break;
                case "otherShip":
                    item.LocationId = 1000000000002;
                    item.FlagId = (int)InventoryFlag.Cargo;
                    break;
                case "missing":
                    world.Remove(item);
                    break;
                case "singleton":
                    item.Singleton = true;
                    break;
                case "skill":
                    world.Set<PilotSkill>().Find(ImplantTestData.Pilot, ImplantTestData.Cybernetics)!.Points = 0;
                    break;
            }
            world.SaveChanges();
        }
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            if (reason is "unpublished" or "extraEffect")
            {
                string dataset = reason == "unpublished" ? "client/types" : "client/typedogma";
                catalog.Set<SourceRecord>().AsTracking().Single(r => r.Dataset == dataset && r.Key == "9899").Data =
                    reason == "unpublished" ? "{\"published\":0,\"volume\":1}" : "{\"dogmaEffects\":[{\"effectID\":999}]}";
                catalog.SaveChanges();
            }
        }
        var before = ImplantTestData.Training(db).Save(ImplantTestData.Pilot, [new(3327, 1)], true);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(100));
        Assert.Throws<InvalidDataException>(() => ImplantTestData.Store(db).Install(ImplantTestData.Pilot, id));
        Assert.Equal(before.Queue, ImplantTestData.Training(db).Advance(ImplantTestData.Pilot).Queue);
        using WorldDbContext saved = new(db.Path);
        if (reason == "missing")
        {
            Assert.Null(saved.Set<OwnedItem>().Find(id));
        }
        else
        {
            Assert.Equal(2, saved.Set<OwnedItem>().Find(id)!.Quantity);
        }
        Assert.Equal(0, saved.Set<PilotSkill>().Find(ImplantTestData.Pilot, 3327)!.Points);
        Assert.Empty(saved.Set<SavedJumpClone>());
        Assert.Empty(saved.Set<SavedPilotImplant>());
        Assert.Empty(saved.Set<SavedImplantState>());
    }

    [Fact]
    public async Task CompetingInstallsConsumeOneUnitAndFailedPersistenceRollsBackProgress()
    {
        var clock = CalendarTestData.Clock();
        using var db = ImplantTestData.Create(clock);
        long item = ImplantTestData.Item(db, quantity: 2);
        var training = ImplantTestData.Training(db);
        var before = training.Save(ImplantTestData.Pilot, [new(3327, 1)], true);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(100));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectImplant BEFORE INSERT ON WorldPilotImplant BEGIN SELECT RAISE(ABORT,'test'); END;"
            );
        }
        Assert.Throws<DbUpdateException>(() => ImplantTestData.Store(db).Install(ImplantTestData.Pilot, item));
        Assert.Equal(before.Queue, training.Advance(ImplantTestData.Pilot).Queue);
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Equal(0, world.Set<PilotSkill>().Find(ImplantTestData.Pilot, 3327)!.Points);
            Assert.Equal(2, world.Set<OwnedItem>().Find(item)!.Quantity);
            Assert.Empty(world.Set<SavedJumpClone>());
            Assert.Empty(world.Set<SavedImplantState>());
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectImplant");
        }
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ => Task.Run(() => Record.Exception(() => ImplantTestData.Store(db).Install(ImplantTestData.Pilot, item))))
        );
        Assert.Single(results, r => r is null);
        Assert.IsType<InvalidDataException>(Assert.Single(results, r => r is not null));
        Assert.Single(ImplantTestData.Store(db).Read(ImplantTestData.Pilot).Implants);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(1, saved.Set<OwnedItem>().Find(item)!.Quantity);
    }

    [Fact]
    public void CloneSwitchesRetainImplantsWithTheirBodyAndBodyDestructionDeletesThem()
    {
        var clock = CalendarTestData.Clock();
        using var db = ImplantTestData.Create(clock, clones: true);
        var store = ImplantTestData.Store(db);
        var installed = Assert.Single(store.Install(ImplantTestData.Pilot, ImplantTestData.Item(db)).Snapshot.Implants);
        var clones = db.Services.GetRequiredService<IPilotJumpClones>();
        clones.Install(ImplantTestData.Pilot);
        var empty = Assert.Single(clones.Read(ImplantTestData.Pilot).Clones);
        var pod = HomeStationTestData.MakeActiveCapsule(db, HomeStationTestData.Origin);
        var training = ImplantTestData.Training(db);
        training.Save(ImplantTestData.Pilot, [new(3327, 1)], true);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(100));
        var jumped = clones.Jump(ImplantTestData.Pilot, pod, empty.StationId, empty.Id, JumpCloneRules.FeeCents);
        Assert.Empty(store.Read(ImplantTestData.Pilot).Implants);
        Assert.Equal(CharacterAttributes.Default, store.Read(ImplantTestData.Pilot).Attributes);
        Assert.Equal(installed, Assert.Single(clones.Read(ImplantTestData.Pilot).Implants));
        Assert.Throws<InvalidDataException>(() => store.Destroy(ImplantTestData.Pilot, installed.ItemId));
        Assert.Throws<InvalidDataException>(() => store.Destroy(90000002, installed.ItemId));
        Assert.Equal(54, Assert.Single(training.Advance(ImplantTestData.Pilot).Queue).StartPoints);
        var back = clones.Jump(ImplantTestData.Pilot, jumped.Pilot.ShipId, empty.StationId, installed.CloneId, JumpCloneRules.FeeCents);
        Assert.Equal(installed, Assert.Single(store.Read(ImplantTestData.Pilot).Implants));
        Assert.Equal(23, store.Read(ImplantTestData.Pilot).Attributes.Perception);
        var stored = Assert.Single(clones.Read(ImplantTestData.Pilot).Clones);
        HomeStationTestData.Travel(db, HomeStationTestData.Other);
        db.Services.GetRequiredService<IPilotHomeStation>().SelfDestruct(ImplantTestData.Pilot, back.Pilot.ShipId);
        Assert.Empty(store.Read(ImplantTestData.Pilot).Implants);
        Assert.Equal(stored, Assert.Single(clones.Read(ImplantTestData.Pilot).Clones));
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(392).Ticks, Assert.Single(training.Advance(ImplantTestData.Pilot).Queue).EndTime);
        using WorldDbContext saved = new(db.Path);
        Assert.Empty(saved.Set<SavedPilotImplant>());
        Assert.Empty(saved.Set<SavedJumpClone>().Where(c => c.Active));
    }

    [Fact]
    public void MigrationFrom57AddsEmptyImplantStorageWithoutChangingExistingInventory()
    {
        var clock = CalendarTestData.Clock();
        using var db = ImplantTestData.Create(clock);
        long item = ImplantTestData.Item(db);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldPilotImplant; DROP TABLE WorldImplantState; PRAGMA user_version=57;");
        }
        WorldInitialization.Open(db.Path);
        Assert.Empty(ImplantTestData.Store(db).Read(ImplantTestData.Pilot).Implants);
        Assert.Single(ImplantTestData.Store(db).Install(ImplantTestData.Pilot, item).Snapshot.Implants);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }
}
