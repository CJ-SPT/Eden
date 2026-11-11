using Eden.Data.World.Fittings;
using Eden.Gameplay.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Fittings;

public sealed partial class SavedFittingsTests
{
    [Fact]
    public void PilotOnlyOwnershipMigrationPreservesPlansSequenceIndexesAndTriggers()
    {
        using TestWorld db = new();
        var store = db.Services.GetRequiredService<IPilotSavedFittings>();
        long id = store.Save(Owner, Plan("Preserved"));
        string before = SavedRows(db);
        string assets = Assets(db);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP TRIGGER DeletePilotSavedFittings;
                DROP TRIGGER DeleteCorporationSavedFittings;
                CREATE TABLE LegacyFittings(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    OwnerId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    ShipTypeId INTEGER NOT NULL, Name TEXT NOT NULL, Description TEXT NOT NULL,
                    ItemsJson TEXT NOT NULL, SavedAt INTEGER NOT NULL);
                INSERT INTO LegacyFittings SELECT * FROM WorldSavedFitting;
                DROP TABLE WorldSavedFitting;
                ALTER TABLE LegacyFittings RENAME TO WorldSavedFitting;
                CREATE INDEX IX_WorldSavedFitting_OwnerId ON WorldSavedFitting(OwnerId);
                CREATE INDEX FittingNameTestIndex ON WorldSavedFitting(Name);
                CREATE TABLE FittingAudit(Entry INTEGER NOT NULL);
                CREATE TRIGGER FittingAuditTest AFTER INSERT ON WorldSavedFitting
                BEGIN INSERT INTO FittingAudit VALUES(NEW.Id); END;
                UPDATE sqlite_sequence SET seq=9000 WHERE name='WorldSavedFitting';
                PRAGMA user_version=141;
                """
            );
        }
        WorldInitialization.Open(db.Path);
        WorldInitialization.Open(db.Path);
        Assert.Equal(before, SavedRows(db));
        Assert.Equal(assets, Assets(db));
        Assert.Equal(id, Assert.Single(store.Read(Owner)).Id);
        long added = store.Save(Owner, Plan("After upgrade"));
        Assert.Equal(9001, added);
        using WorldDbContext check = new(db.Path);
        Assert.Equal(
            "WorldInventoryOwner",
            check.Database.SqlQueryRaw<string>("SELECT \"table\" AS Value FROM pragma_foreign_key_list('WorldSavedFitting')").Single()
        );
        Assert.Equal(
            1,
            check.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM sqlite_schema WHERE name='FittingNameTestIndex'").Single()
        );
        Assert.Equal(added, check.Database.SqlQueryRaw<long>("SELECT Entry AS Value FROM FittingAudit").Single());
        Assert.Equal(0, check.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").Single());
        Assert.Equal(WorldMigration.CurrentVersion, check.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        check.Add(
            new SavedFittingRecord
            {
                OwnerId = long.MaxValue,
                ShipTypeId = 601,
                Name = "Unknown owner",
            }
        );
        Assert.Throws<DbUpdateException>(() => check.SaveChanges());
    }

    [Fact]
    public void DeletingAPilotRemovesTheirPlansWithoutDeletingAnotherOwnersTemplates()
    {
        using TestWorld db = new();
        var store = db.Services.GetRequiredService<IPilotSavedFittings>();
        long retained = store.Save(Owner, Plan("Retain"));
        store.Save(Other, Plan("Remove"));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DELETE FROM WorldPilot WHERE Id=90000002");
        }
        using WorldDbContext check = new(db.Path);
        Assert.Equal(retained, Assert.Single(check.Set<SavedFittingRecord>()).Id);
        Assert.Equal(0, check.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").Single());
    }
}
