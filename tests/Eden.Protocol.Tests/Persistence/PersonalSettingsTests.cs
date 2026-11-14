using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class PersonalSettingsTests
{
    private const long Owner = 90000001;
    private const long Other = 90000002;

    [Fact]
    public void NotepadRenameFoldersPrivacyAndDeletionSurviveReopen()
    {
        using TestWorld db = new();
        var notes = db.Services.GetRequiredService<IPilotNotepad>();
        using (WorldDbContext world = new(db.Path))
        {
            world.Pilots.Find(Other)!.AccountId = 1000001;
            world.SaveChanges();
        }
        const string body = "<b>星間</b> 🚀 <a href=showinfo:1373//90000002>pilot</a>";
        long id = notes.Add(Owner, "N:Travel", body);
        long folder = notes.Add(Owner, "S:folders", new string('f', NotepadRules.MaximumTextLength));
        notes.Edit(Owner, id, "N:Renamed", null);
        Assert.Equal(body, notes.Read(Owner, id)!.Text);
        Assert.Throws<InvalidDataException>(() => notes.Edit(Owner, id, "N:Rejected", new string('x', NotepadRules.MaximumTextLength + 1)));
        Assert.Equal("N:Renamed", notes.Read(Owner, id)!.Label);
        Assert.Empty(notes.List(Other));
        Assert.Null(notes.Read(Other, id));
        Assert.Throws<InvalidDataException>(() => notes.Edit(Other, id, "N:Stolen", "changed"));
        notes.Remove(Other, id);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var saved = reopened.GetRequiredService<IPilotNotepad>();
        Assert.Equal(new NotepadEntry(id, "N:Renamed", body), saved.Read(Owner, id));
        Assert.Equal(NotepadRules.MaximumTextLength, saved.Read(Owner, folder)!.Text.Length);
        saved.Edit(Owner, id, "N:Renamed", "");
        Assert.Equal("", saved.Read(Owner, id)!.Text);
        saved.Remove(Owner, id);
        saved.Remove(Owner, id);
        Assert.Null(saved.Read(Owner, id));
        long next = saved.Add(Owner, "N:New", "new");
        Assert.True(next > folder);
        WorldInitialization.Open(db.Path);
        Assert.Equal(new[] { folder, next }, saved.List(Owner).Select(n => n.Id));
        Assert.Equal("", db.Services.GetRequiredService<IPilotNotes>().Read(Owner, Other));
    }

    [Fact]
    public void SharedSettingsAreImmutableDeduplicatedAndKeyedByHashIdAndKindAcrossReopen()
    {
        using TestWorld db = new();
        var settings = db.Services.GetRequiredService<ISharedSettings>();
        var key = settings.Store(Owner, SharedSettingKind.Overview, "[[\"name\", \"Travel\"]]");
        Assert.Equal(key, settings.Store(Other, key.Kind, settings.Read(Other, key)!));
        var filter = settings.Store(Owner, SharedSettingKind.InventoryFilter, settings.Read(Owner, key)!);
        var changed = settings.Store(Owner, key.Kind, "[[\"name\", \"Combat\"]]");
        Assert.NotEqual(key.Id, changed.Id);
        Assert.NotEqual(key.Id, filter.Id);
        Assert.Null(settings.Read(Other, key with { Hash = "wrong" }));
        Assert.Null(settings.Read(Other, key with { Kind = SharedSettingKind.FleetBroadcast }));
        Assert.Null(settings.Read(Other, key with { Id = long.MaxValue }));
        Assert.Throws<InvalidDataException>(() => settings.Store(123, key.Kind, "[]"));
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        Assert.Equal("[[\"name\", \"Travel\"]]", reopened.GetRequiredService<ISharedSettings>().Read(Other, key));
        using WorldDbContext world = new(db.Path);
        Assert.Equal(3, world.Set<SavedSharedSetting>().Count());
    }

    [Fact]
    public void Version155UpgradePreservesExistingNotesAndProfiles()
    {
        using TestWorld db = new();
        db.Services.GetRequiredService<IPilotNotes>().Save(Owner, new(Other, "Existing contact"));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldNotepadEntry; DROP TABLE WorldSharedSetting; PRAGMA user_version=155;");
        }
        WorldInitialization.Open(db.Path);
        WorldInitialization.Open(db.Path);
        Assert.Equal("Existing contact", db.Services.GetRequiredService<IPilotNotes>().Read(Owner, Other));
        Assert.Empty(db.Services.GetRequiredService<IPilotNotepad>().List(Owner));
        var key = db.Services.GetRequiredService<ISharedSettings>().Store(Owner, SharedSettingKind.Overview, "[]");
        Assert.Equal("[]", db.Services.GetRequiredService<ISharedSettings>().Read(Other, key));
        using WorldDbContext final = new(db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, final.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public void ResidentSettingsPublishImmediatelyAndCheckpointPreservesImmutableSnapshots()
    {
        using TestWorld db = new();
        using LiveWorld live = new(new(db.Path), TimeProvider.System, _ => { });
        live.Economy.Warm();
        var notes = db.Services.GetRequiredService<IPilotNotepad>();
        var settings = db.Services.GetRequiredService<ISharedSettings>();
        long id = notes.Add(Owner, "N:Before", "original");
        var key = settings.Store(Owner, SharedSettingKind.FleetBroadcast, "[[\"enabled\", true]]");
        using (var metrics = SessionPerformance.Begin("resident personal settings", _ => { }, force: true)!)
        {
            Assert.Equal("original", notes.Read(Owner, id)!.Text);
            Assert.Single(notes.List(Owner));
            Assert.Equal("[[\"enabled\", true]]", settings.Read(Other, key));
            Assert.Equal(key, settings.Store(Other, key.Kind, "[[\"enabled\", true]]"));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using var first = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        notes.Edit(Owner, id, "N:After", "updated");
        Assert.Equal("updated", notes.Read(Owner, id)!.Text);
        Assert.Equal("[[\"enabled\", true]]", settings.Read(Other, key));
        Assert.Throws<InvalidDataException>(() => notes.Edit(Other, id, "N:Denied", "bad"));
        using (WorldDbContext baseline = new(first.Database))
        {
            baseline.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectSetting BEFORE INSERT ON WorldSharedSetting BEGIN SELECT RAISE(ABORT,'failed checkpoint'); END;"
            );
        }
        Assert.Throws<SqliteException>(() => first.Project());
        using (WorldDbContext failed = new(first.Database))
        {
            Assert.Empty(failed.Set<SavedNotepadEntry>());
            Assert.Empty(failed.Set<SavedSharedSetting>());
            failed.Database.ExecuteSqlRaw("DROP TRIGGER RejectSetting");
        }
        Assert.Equal("updated", notes.Read(Owner, id)!.Text);
        first.Project();
        using (WorldDbContext saved = new(first.Database))
        {
            Assert.Equal("original", saved.Set<SavedNotepadEntry>().Find(id)!.Text);
            Assert.Equal(key.Hash, saved.Set<SavedSharedSetting>().Find(key.Id)!.Hash);
        }
        notes.Remove(Owner, id);
        long next = notes.Add(Owner, "N:Next", "new");
        Assert.True(next > id);
        using var second = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        second.Project();
        using WorldDbContext after = new(second.Database);
        Assert.Null(after.Set<SavedNotepadEntry>().Find(id));
        Assert.Equal("new", after.Set<SavedNotepadEntry>().Find(next)!.Text);
    }

    [Theory]
    [InlineData("", "ok")]
    [InlineData("N:bad\0name", "ok")]
    [InlineData("N:good", "bad\0body")]
    public void NotepadRulesRejectInvalidText(string label, string text) =>
        Assert.Throws<InvalidDataException>(() => NotepadRules.Check(label, text));

    [Fact]
    public void TextLimitsAcceptBoundariesAndRejectOversizedInputs()
    {
        string label = new('x', NotepadRules.MaximumLabelLength);
        string text = new('x', NotepadRules.MaximumTextLength);
        NotepadRules.Check(label, text);
        Assert.Throws<InvalidDataException>(() => NotepadRules.Check(label + "x", text));
        Assert.Throws<InvalidDataException>(() => NotepadRules.Check(label, text + "x"));
        SharedSettingRules.Check(SharedSettingKind.Overview, new string('x', SharedSettingRules.MaximumPayloadBytes));
        Assert.Throws<InvalidDataException>(() => SharedSettingRules.Check((SharedSettingKind)1, "[]"));
        Assert.Throws<InvalidDataException>(() => SharedSettingRules.Check(SharedSettingKind.Overview, ""));
        Assert.Throws<InvalidDataException>(() =>
            SharedSettingRules.Check(SharedSettingKind.Overview, new string('星', SharedSettingRules.MaximumPayloadBytes / 3 + 1))
        );
    }
}
