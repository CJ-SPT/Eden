using System.Security.Cryptography;
using System.Text;
using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Characters;

[Injectable]
internal sealed class PersonalSettingsStore(WorldDatabase database) : IPilotNotepad, ISharedSettings
{
    public IReadOnlyList<NotepadEntry> List(long pilotId) =>
        WorldActionScope.Inspect(
            database,
            world =>
            {
                RequirePilot(world, pilotId);
                return world
                    .Set<SavedNotepadEntry>()
                    .Where(row => row.PilotId == pilotId)
                    .OrderBy(row => row.Id)
                    .Select(row => new NotepadEntry(row.Id, row.Label, row.Text))
                    .ToArray();
            }
        );

    public NotepadEntry? Read(long pilotId, long noteId) =>
        WorldActionScope.Inspect(
            database,
            world =>
            {
                RequirePilot(world, pilotId);
                var row = FindNote(world, pilotId, noteId);
                return row is null ? null : new NotepadEntry(row.Id, row.Label, row.Text);
            }
        );

    public long Add(long pilotId, string label, string text)
    {
        NotepadRules.Check(label, text);
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        RequirePilot(world, pilotId);
        long id = world.Next("WorldNotepadEntry");
        world.Add(
            new SavedNotepadEntry
            {
                Id = id,
                PilotId = pilotId,
                Label = label,
                Text = text,
            }
        );
        transaction.Commit();
        return id;
    }

    public void Edit(long pilotId, long noteId, string label, string? text)
    {
        NotepadRules.Check(label, text);
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        RequirePilot(world, pilotId);
        var row = FindNote(world, pilotId, noteId) ?? throw new InvalidDataException("Unknown notepad entry.");
        row.Label = label;
        if (text is not null)
        {
            row.Text = text;
        }
        transaction.Commit();
    }

    public void Remove(long pilotId, long noteId)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        RequirePilot(world, pilotId);
        if (FindNote(world, pilotId, noteId) is { } row)
        {
            world.Remove(row);
        }
        transaction.Commit();
    }

    public SharedSettingKey Store(long pilotId, SharedSettingKind kind, string payload)
    {
        SharedSettingRules.Check(kind, payload);
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        RequirePilot(world, pilotId);
        var row = world.Set<SavedSharedSetting>().FirstOrDefault(row => row.Kind == kind && row.Hash == hash && row.Payload == payload);
        if (row is null)
        {
            row = new()
            {
                Id = world.Next("WorldSharedSetting"),
                Kind = kind,
                Hash = hash,
                Payload = payload,
            };
            world.Add(row);
        }
        transaction.Commit();
        return new(row.Hash, row.Id, row.Kind);
    }

    public string? Read(long pilotId, SharedSettingKey key) =>
        WorldActionScope.Inspect(
            database,
            world =>
            {
                RequirePilot(world, pilotId);
                var row = world.Set<SavedSharedSetting>().Find(key.Id);
                return row is not null && row.Kind == key.Kind && row.Hash == key.Hash ? row.Payload : null;
            }
        );

    private static SavedNotepadEntry? FindNote(WorldAction world, long pilotId, long noteId)
    {
        if (noteId <= 0)
        {
            throw new InvalidDataException("Invalid notepad entry.");
        }
        var row = world.Set<SavedNotepadEntry>().Find(noteId);
        return row?.PilotId == pilotId ? row : null;
    }

    private static void RequirePilot(WorldAction world, long pilotId)
    {
        if (!world.PilotExists(pilotId))
        {
            throw new InvalidDataException("Unknown settings owner.");
        }
    }
}
