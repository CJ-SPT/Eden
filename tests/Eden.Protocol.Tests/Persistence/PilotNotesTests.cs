using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Skills;
using Eden.Server;
using Eden.World.Characters;
using Eden.World.Docking;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class PilotNotesTests
{
    private const long Owner = 90000001;
    private const long Other = 90000002;

    [Fact]
    public void NotesArePrivatePerPilotAndTargetAndSurviveReopenUpdatesAndClearing()
    {
        using TestWorld db = new();
        // Characters on the same account must still have independent notes.
        using (WorldDbContext world = new(db.Path))
        {
            world.Pilots.Find(Other)!.AccountId = 1000001;
            world.SaveChanges();
        }
        var notes = db.Services.GetRequiredService<IPilotNotes>();
        const string text = "<b>星間旅行</b><br>пилот 🚀\nPrivate contact";
        Assert.Equal("", notes.Read(Owner, Other));
        Assert.Equal("", notes.Read(Other, Other));
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Empty(world.Set<SavedPilotNote>());
        }
        notes.Save(Owner, new(Other, text));
        notes.Save(Owner, new(Owner, "Self"));
        notes.Save(Other, new(Other, "Independent"));
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var saved = reopened.GetRequiredService<IPilotNotes>();
        Assert.Equal(text, saved.Read(Owner, Other));
        Assert.Equal("Self", saved.Read(Owner, Owner));
        Assert.Equal("Independent", saved.Read(Other, Other));
        Assert.Equal("", saved.Read(Other, Owner));
        Assert.Equal("", reopened.GetRequiredService<IPilotCharacterSheet>().ReadBiography(Other));
        saved.Save(Owner, new(Other, "Changed"));
        Assert.Equal("Changed", notes.Read(Owner, Other));
        saved.Save(Owner, new(Other, ""));
        saved.Save(Owner, new(Other, ""));
        saved.Save(Other, new(Owner, ""));
        WorldInitialization.Open(db.Path);
        Assert.Equal("", saved.Read(Owner, Other));
        Assert.Equal("Independent", notes.Read(Other, Other));
        using WorldDbContext final = new(db.Path);
        Assert.Equal(2, final.Set<SavedPilotNote>().Count());
    }

    [Fact]
    public void NotesEnforceTheNativeTextLimitAndRejectInvalidOwnersAndTargetsWithoutChangingSavedNotes()
    {
        using TestWorld db = new();
        AgentTestData.Prepare(db);
        var notes = db.Services.GetRequiredService<IPilotNotes>();
        string maximum = new('x', PilotNoteRules.MaximumLength);
        notes.Save(Owner, new(Other, maximum));
        notes.Save(Other, new(Other, "Other note"));
        notes.Save(Owner, new(AgentTestData.AgentId, "NPC contact"));
        Assert.Equal(maximum, notes.Read(Owner, Other));
        Assert.Equal("NPC contact", notes.Read(Owner, AgentTestData.AgentId));
        foreach (long target in new[] { 0L, -1L, 123L, long.MaxValue, AgentTestData.DeletedAgent })
        {
            Assert.Throws<InvalidDataException>(() => notes.Read(Owner, target));
            Assert.Throws<InvalidDataException>(() => notes.Save(Owner, new(target, "Rejected")));
        }
        Assert.Throws<InvalidDataException>(() => notes.Read(123, Other));
        Assert.Throws<InvalidDataException>(() => notes.Save(123, new(Other, "Unknown owner")));
        Assert.Throws<InvalidDataException>(() => notes.Save(Owner, new(Other, maximum + "x")));
        Assert.Throws<InvalidDataException>(() => notes.Save(Owner, new(Other, "Invalid\0note")));
        Assert.Equal(maximum, notes.Read(Owner, Other));
        Assert.Equal("Other note", notes.Read(Other, Other));
        using WorldDbContext world = new(db.Path);
        Assert.Equal(3, world.Set<SavedPilotNote>().Count());
    }

    [Fact]
    public void Version63UpgradeCreatesNotesWithoutChangingExistingProfilesOrProgress()
    {
        using TestWorld db = new();
        db.Services.GetRequiredService<IPilotCharacterSheet>().SaveBiography(Owner, "Public biography");
        var pilot = db.Store.FindPilot(1000001)!;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldPilotNote; PRAGMA user_version=63");
        }
        WorldInitialization.Open(db.Path);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var notes = reopened.GetRequiredService<IPilotNotes>();
        Assert.Equal("", notes.Read(Owner, Other));
        Assert.Equal(pilot, reopened.GetRequiredService<SqliteWorld>().FindPilot(1000001));
        Assert.Equal("Public biography", reopened.GetRequiredService<IPilotCharacterSheet>().ReadBiography(Owner));
        Assert.Equal(8000, Assert.Single(reopened.GetRequiredService<IPilotSkillReader>().ReadSkills(Owner)).Points);
        notes.Save(Owner, new(Other, "Migrated note"));
        WorldInitialization.Open(db.Path);
        Assert.Equal("Migrated note", notes.Read(Owner, Other));
        using WorldDbContext final = new(db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, final.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public void FailedWorldUpgradeRollsBackTheNewNotesTableAndVersion()
    {
        using TestWorld db = new();
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldPilotNote;
                PRAGMA user_version=63;
                UPDATE WorldPilotSkill SET TypeId=2147483647 WHERE PilotId=90000001;
                """
            );
        }
        Assert.Throws<InvalidDataException>(() => WorldInitialization.Open(db.Path));
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Equal(63, world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
            Assert.Equal(
                0,
                world
                    .Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name='WorldPilotNote'")
                    .AsEnumerable()
                    .Single()
            );
            world.Database.ExecuteSqlRaw("UPDATE WorldPilotSkill SET TypeId=3327 WHERE PilotId=90000001");
        }
        WorldInitialization.Open(db.Path);
        Assert.Equal("", db.Services.GetRequiredService<IPilotNotes>().Read(Owner, Other));
    }
}
