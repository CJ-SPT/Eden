using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Characters;

[Injectable]
internal sealed class PilotCharacterSettingsStore(WorldDatabase database) : IPilotCharacterSettings
{
    public IReadOnlyList<CharacterSetting> Read(long pilotId)
    {
        return WorldActionScope.Inspect(
            database,
            world =>
            {
                CheckPilot(world, pilotId);
                return world
                    .Filter<SavedCharacterSetting>(s => s.PilotId == pilotId)
                    .OrderBy(s => s.Key)
                    .AsEnumerable()
                    .Select(Read)
                    .ToArray();
            }
        );
    }

    public CharacterSetting? Read(long pilotId, string key)
    {
        return WorldActionScope.Inspect(database, world => Read(world, pilotId, key));
    }

    internal static CharacterSetting? Read(WorldAction world, long pilotId, string key) =>
        world.Set<SavedCharacterSetting>().Find(pilotId, key) is { } saved ? Read(saved) : null;

    private static CharacterSetting Read(SavedCharacterSetting setting) =>
        new(
            setting.Key,
            setting.Text is { } text
                ? new CharacterSettingValue.Text(text)
                : new CharacterSettingValue.Binary(
                    setting.Binary?.ToArray() ?? throw new InvalidDataException("Invalid saved character setting.")
                )
        );

    public void Save(long pilotId, CharacterSetting setting)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        Save(transaction.World, pilotId, setting);
        transaction.Commit();
    }

    internal static void Save(WorldAction world, long pilotId, CharacterSetting setting)
    {
        CharacterSettingsRules.Check(setting);
        CheckPilot(world, pilotId);
        var saved = world.Set<SavedCharacterSetting>().Find(pilotId, setting.Key);
        if (saved is null)
        {
            saved = new SavedCharacterSetting { PilotId = pilotId, Key = setting.Key };
            world.Add(saved);
        }
        saved.Text = (setting.Value as CharacterSettingValue.Text)?.Value;
        saved.Binary = (setting.Value as CharacterSettingValue.Binary)?.Value.ToArray();
    }

    public void Delete(long pilotId, string key)
    {
        CharacterSettingsRules.CheckKey(key);
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        CheckPilot(world, pilotId);
        if (world.Set<SavedCharacterSetting>().Find(pilotId, key) is { } saved)
        {
            world.Remove(saved);
        }
        transaction.Commit();
    }

    private static void CheckPilot(WorldAction world, long pilotId)
    {
        if (world.Pilots.Find(pilotId) is null)
        {
            throw new InvalidDataException("Unknown character.");
        }
    }
}
