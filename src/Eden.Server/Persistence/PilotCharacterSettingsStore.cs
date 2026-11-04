using Eden.Gameplay.Characters;
using Eden.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class SavedCharacterSetting
{
    public long PilotId { get; set; }
    public string Key { get; set; } = "";
    public string? Text { get; set; }
    public byte[]? Binary { get; set; }
}

[Injectable]
internal sealed class PilotCharacterSettingsStore(WorldDatabase database) : IPilotCharacterSettings
{
    public IReadOnlyList<CharacterSetting> Read(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        CheckPilot(world, pilotId);
        return world
            .Set<SavedCharacterSetting>()
            .AsNoTracking()
            .Where(s => s.PilotId == pilotId)
            .OrderBy(s => s.Key)
            .AsEnumerable()
            .Select(s => new CharacterSetting(
                s.Key,
                s.Text is { } text
                    ? new CharacterSettingValue.Text(text)
                    : new CharacterSettingValue.Binary(s.Binary ?? throw new InvalidDataException("Invalid saved character setting."))
            ))
            .ToArray();
    }

    public void Save(long pilotId, CharacterSetting setting)
    {
        CharacterSettingsRules.Check(setting);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        CheckPilot(world, pilotId);
        var saved = world.Set<SavedCharacterSetting>().Find(pilotId, setting.Key);
        if (saved is null)
        {
            saved = new SavedCharacterSetting { PilotId = pilotId, Key = setting.Key };
            world.Add(saved);
        }
        saved.Text = (setting.Value as CharacterSettingValue.Text)?.Value;
        saved.Binary = (setting.Value as CharacterSettingValue.Binary)?.Value;
        world.SaveChanges();
        transaction.Commit();
    }

    public void Delete(long pilotId, string key)
    {
        CharacterSettingsRules.CheckKey(key);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        CheckPilot(world, pilotId);
        world.Set<SavedCharacterSetting>().Where(s => s.PilotId == pilotId && s.Key == key).ExecuteDelete();
        transaction.Commit();
    }

    private static void CheckPilot(WorldDbContext world, long pilotId)
    {
        if (!world.Pilots.Any(p => p.Id == pilotId))
        {
            throw new InvalidDataException("Unknown character.");
        }
    }
}
