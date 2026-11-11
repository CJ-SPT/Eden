using Eden.Data.World.Corporations;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Protocol.Corporations;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class CorporationContactsTests
{
    [Theory]
    [InlineData("standing")]
    [InlineData("delete")]
    [InlineData("label")]
    public void DatabaseFailuresRollBackWholeContactSelectionsAndLabelMaskCleanup(string operation)
    {
        using TestWorld db = new();
        int corporation = CorporationAdministrationTests.CreateAndJoin(db);
        var store = db.Services.GetRequiredService<ICorporationContacts>();
        store.SetStanding(Ceo, [Navy, Member], 5);
        long label = store.CreateLabel(Ceo, "Trusted", null);
        store.SetLabels(Ceo, [Navy, Member], label, true);
        using CorporationTestClient observer = new(db, 1000002);
        Read(observer);
        string before = Rows(db);
        long revision = store.Revision(corporation);
        using (WorldDbContext world = new(db.Path))
        {
            string sql = operation switch
            {
                "standing" => """
                    CREATE TRIGGER FailCorporationContact BEFORE UPDATE ON WorldCorporationContact WHEN OLD.ContactId=90000002
                    BEGIN SELECT RAISE(ABORT,'forced contact failure'); END;
                    """,
                "delete" => """
                    CREATE TRIGGER FailCorporationContact BEFORE DELETE ON WorldCorporationContact WHEN OLD.ContactId=90000002
                    BEGIN SELECT RAISE(ABORT,'forced contact failure'); END;
                    """,
                _ => """
                    CREATE TRIGGER FailCorporationContact BEFORE DELETE ON WorldCorporationContactLabel
                    BEGIN SELECT RAISE(ABORT,'forced label failure'); END;
                    """,
            };
            world.Database.ExecuteSqlRaw(sql);
        }
        switch (operation)
        {
            case "standing":
                Assert.Throws<DbUpdateException>(() => store.SetStanding(Ceo, [Navy, Member], -5));
                break;
            case "delete":
                Assert.Throws<SqliteException>(() => store.Delete(Ceo, [Navy, Member]));
                break;
            default:
                Assert.Throws<DbUpdateException>(() => store.DeleteLabel(Ceo, label));
                break;
        }
        Assert.Equal(before, Rows(db));
        Assert.Equal(revision, store.Revision(corporation));
        observer.Services.GetRequiredService<CorporationContactsMonitor>().Advance(observer.Context);
        Assert.Empty(observer.Context.Notifications);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TRIGGER FailCorporationContact");
        }
        WorldInitialization.Open(db.Path);
        Assert.Equal(before, Rows(db));
        store.SetStanding(Ceo, [Navy, Member], -5);
        store.DeleteLabel(Ceo, label);
        Assert.All(
            store.Read(Ceo).Contacts,
            c =>
            {
                Assert.Equal(-5, c.Standing);
                Assert.Equal(0, c.LabelMask);
            }
        );
        store.Delete(Ceo, [Navy, Member]);
        WorldInitialization.Open(db.Path);
        Assert.Empty(store.Read(Ceo).Contacts);
    }

    [Fact]
    public void Version143UpgradePreservesPersonalContactsAndCreatesEmptyCorporationStorage()
    {
        using TestWorld db = new();
        int corporation = CorporationAdministrationTests.CreateAndJoin(db);
        var personal = db.Services.GetRequiredService<IPilotContacts>();
        personal.Add(Ceo, Member, 2.5, false);
        var pilot = db.Store.FindPilot(1000001);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "DROP TABLE WorldCorporationContact; DROP TABLE WorldCorporationContactLabel; PRAGMA user_version=143"
            );
        }
        WorldInitialization.Open(db.Path);
        WorldInitialization.Open(db.Path);
        var store = db.Services.GetRequiredService<ICorporationContacts>();
        Assert.Empty(store.Read(Ceo).Contacts);
        Assert.Empty(store.Read(Ceo).Labels);
        Assert.Equal(corporation, store.Read(Member).CorporationId);
        Assert.Equal(2.5, Assert.Single(personal.Read(Ceo).Addresses).Standing);
        Assert.Equal(pilot, db.Store.FindPilot(1000001));
        store.SetStanding(Ceo, [Navy], 5);
        long label = store.CreateLabel(Ceo, "Persistent", 0);
        store.SetLabels(Ceo, [Navy], label, true);
        string before = Rows(db);
        WorldInitialization.Open(db.Path);
        Assert.Equal(before, Rows(db));
        using WorldDbContext check = new(db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, check.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Equal(0, check.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").Single());
    }

    [Fact]
    public void NpcCorporationContactsStayEmptyAndCannotBeWritten()
    {
        using TestWorld db = new();
        var store = db.Services.GetRequiredService<ICorporationContacts>();
        Assert.Empty(store.Read(Ceo).Contacts);
        Assert.Empty(store.Read(Ceo).Labels);
        Assert.Throws<InvalidDataException>(() => store.SetStanding(Ceo, [Member], 5));
        Assert.Throws<InvalidDataException>(() => store.CreateLabel(Ceo, "Denied", null));
        Assert.Empty(store.Read(Ceo).Contacts);
    }
}
