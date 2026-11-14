using Eden.Gameplay.Characters;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CharacterSettingsTests
{
    private const long PilotId = 90000001;
    private const long WingId = 90000002;

    [Fact]
    public void SettingsPreserveBinaryAndUnicodeAcrossRestartAndRemainPrivate()
    {
        using TestWorld db = new();
        var settings = db.Services.GetRequiredService<IPilotCharacterSettings>();
        Assert.Empty(settings.Read(PilotId));
        settings.Save(PilotId, new("overview", new CharacterSettingValue.Binary([0, 255, 128, 10])));
        settings.Save(PilotId, new("layout", new CharacterSettingValue.Text("星間旅行 🚀")));
        settings.Save(WingId, new("layout", new CharacterSettingValue.Text("Wing")));
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var saved = reopened.GetRequiredService<IPilotCharacterSettings>();
        var values = saved.Read(PilotId);
        Assert.Equal("星間旅行 🚀", Assert.IsType<CharacterSettingValue.Text>(values[0].Value).Value);
        Assert.Equal(new byte[] { 0, 255, 128, 10 }, Assert.IsType<CharacterSettingValue.Binary>(values[1].Value).Value);
        saved.Save(PilotId, new("layout", new CharacterSettingValue.Binary([1, 2])));
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<CharacterSettingValue.Binary>(saved.Read(PilotId)[0].Value).Value);
        saved.Delete(PilotId, "layout");
        saved.Delete(PilotId, "layout");
        Assert.Equal("overview", Assert.Single(settings.Read(PilotId)).Key);
        Assert.Equal("Wing", Assert.IsType<CharacterSettingValue.Text>(Assert.Single(saved.Read(WingId)).Value).Value);
        WorldInitialization.Open(db.Path);
        Assert.Equal("overview", Assert.Single(saved.Read(PilotId)).Key);
    }

    [Fact]
    public void ResidentSettingsCommitBinaryTextAndDeletionThroughCheckpoint()
    {
        using TestWorld db = new(liveWorld: true);
        var live = LiveWorld.Find(db.Path)!;
        live.Economy.Warm();
        var settings = db.Services.GetRequiredService<IPilotCharacterSettings>();
        using (var metrics = SessionPerformance.Begin("resident settings", _ => { }, force: true)!)
        {
            settings.Save(PilotId, new("text", new CharacterSettingValue.Text("検証")));
            settings.Save(PilotId, new("binary", new CharacterSettingValue.Binary([0, 255, 128])));
            settings.Save(PilotId, new("empty", new CharacterSettingValue.Binary([])));
            settings.Save(PilotId, new("removed", new CharacterSettingValue.Text("delete")));
            settings.Delete(PilotId, "removed");
            Assert.Equal(3, settings.Read(PilotId).Count);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
        }
        live.Dispose();

        using var reopened = new LiveWorld(new(db.Path), TimeProvider.System);
        Assert.Equal("検証", Assert.IsType<CharacterSettingValue.Text>(settings.Read(PilotId, "text")!.Value).Value);
        Assert.Equal(
            new byte[] { 0, 255, 128 },
            Assert.IsType<CharacterSettingValue.Binary>(settings.Read(PilotId, "binary")!.Value).Value
        );
        Assert.Empty(Assert.IsType<CharacterSettingValue.Binary>(settings.Read(PilotId, "empty")!.Value).Value);
        Assert.Null(settings.Read(PilotId, "removed"));
        Assert.Empty(settings.Read(WingId));
    }

    [Fact]
    public void InvalidWritesLeaveExistingValuesAndOtherPilotsUnchanged()
    {
        using TestWorld db = new();
        var settings = db.Services.GetRequiredService<IPilotCharacterSettings>();
        CharacterSetting original = new("layout", new CharacterSettingValue.Text("original"));
        settings.Save(PilotId, original);
        foreach (string key in new[] { "", " ", "bad\0key", new string('k', CharacterSettingsRules.MaximumKeyLength + 1) })
        {
            Assert.Throws<InvalidDataException>(() => settings.Save(PilotId, original with { Key = key }));
            Assert.Throws<InvalidDataException>(() => settings.Delete(PilotId, key));
        }
        Assert.Throws<InvalidDataException>(() =>
            settings.Save(
                PilotId,
                original with
                {
                    Value = new CharacterSettingValue.Binary(new byte[CharacterSettingsRules.MaximumValueLength + 1]),
                }
            )
        );
        Assert.Throws<InvalidDataException>(() =>
            settings.Save(
                PilotId,
                original with
                {
                    Value = new CharacterSettingValue.Text(new string('x', CharacterSettingsRules.MaximumValueLength + 1)),
                }
            )
        );
        Assert.Throws<InvalidDataException>(() => settings.Save(123, original));
        Assert.Throws<InvalidDataException>(() => settings.Delete(123, original.Key));
        Assert.Throws<InvalidDataException>(() => settings.Read(123));
        Assert.Equal(original, Assert.Single(settings.Read(PilotId)));
        Assert.Empty(settings.Read(WingId));
        settings.Save(
            PilotId,
            original with
            {
                Value = new CharacterSettingValue.Binary(new byte[CharacterSettingsRules.MaximumValueLength]),
            }
        );
        Assert.Equal(
            CharacterSettingsRules.MaximumValueLength,
            Assert.IsType<CharacterSettingValue.Binary>(Assert.Single(settings.Read(PilotId)).Value).Value.Length
        );
        settings.Save(
            PilotId,
            original with
            {
                Value = new CharacterSettingValue.Text(new string('x', CharacterSettingsRules.MaximumValueLength)),
            }
        );
        Assert.Equal(
            CharacterSettingsRules.MaximumValueLength,
            Assert.IsType<CharacterSettingValue.Text>(Assert.Single(settings.Read(PilotId)).Value).Value.Length
        );
    }

    [Fact]
    public void Version39MigrationCreatesEmptySettingsWithoutChangingExistingSheetOrSkills()
    {
        using TestWorld db = new();
        db.Services.GetRequiredService<IPilotCharacterSheet>().SaveBiography(PilotId, "Keep me");
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldCharacterSetting; PRAGMA user_version=39");
        }
        WorldInitialization.Open(db.Path);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var settings = reopened.GetRequiredService<IPilotCharacterSettings>();
        Assert.Empty(settings.Read(PilotId));
        Assert.Equal("Keep me", reopened.GetRequiredService<IPilotCharacterSheet>().ReadBiography(PilotId));
        Assert.Equal(8000, Assert.Single(reopened.GetRequiredService<IPilotSkillReader>().ReadSkills(PilotId)).Points);
        settings.Save(PilotId, new("layout", new CharacterSettingValue.Text("saved")));
        Assert.Single(settings.Read(PilotId));
    }
}
