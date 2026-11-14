using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed record EconomyNotepadEntry(long Id, long PilotId, string Label, string Text);

internal sealed record EconomySharedSetting(long Id, SharedSettingKind Kind, string Hash, string Payload);

internal sealed partial class ResidentEconomy
{
    private void RegisterPersonalSettings()
    {
        Register(
            new EconomyTable<SavedNotepadEntry, EconomyNotepadEntry>(
                "WorldNotepadEntry",
                row => new(row.Id),
                row => new(row.Id, row.PilotId, row.Label, row.Text),
                state => new SavedNotepadEntry
                {
                    Id = state.Id,
                    PilotId = state.PilotId,
                    Label = state.Label,
                    Text = state.Text,
                },
                ProjectNotepadEntry,
                "Id,PilotId,Label,Text",
                reader => new(reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4)),
                state => new(state.Id),
                indexes: [new("PilotId", row => row.PilotId)],
                rowIdIsKey: true
            )
        );
        Register(
            new EconomyTable<SavedSharedSetting, EconomySharedSetting>(
                "WorldSharedSetting",
                row => new(row.Id),
                row => new(row.Id, row.Kind, row.Hash, row.Payload),
                state => new SavedSharedSetting
                {
                    Id = state.Id,
                    Kind = state.Kind,
                    Hash = state.Hash,
                    Payload = state.Payload,
                },
                ProjectSharedSetting,
                "Id,Kind,Hash,Payload",
                reader => new(reader.GetInt64(1), (SharedSettingKind)reader.GetInt32(2), reader.GetString(3), reader.GetString(4)),
                state => new(state.Id),
                indexes: [new("Hash", row => row.Hash)],
                rowIdIsKey: true
            )
        );
    }

    private static void ProjectNotepadEntry(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyNotepadEntry>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldNotepadEntry WHERE Id IN (SELECT value FROM json_each({keys}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldNotepadEntry(Id,PilotId,Label,Text)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.PilotId'),
                    json_extract(value,'$.Label'), json_extract(value,'$.Text')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET PilotId=excluded.PilotId, Label=excluded.Label, Text=excluded.Text
                """
            );
        }
    }

    private static void ProjectSharedSetting(WorldDbContext world, IReadOnlyList<EconomyChange<EconomySharedSetting>> changes)
    {
        foreach (var batch in changes.Where(change => change.Value is null).Chunk(ProjectionBatchSize))
        {
            string keys = JsonSerializer.Serialize(batch.Select(change => change.Key.Id));
            world.Database.ExecuteSql($"DELETE FROM WorldSharedSetting WHERE Id IN (SELECT value FROM json_each({keys}))");
        }
        foreach (var batch in changes.Where(change => change.Value is not null).Select(change => change.Value).Chunk(ProjectionBatchSize))
        {
            string values = JsonSerializer.Serialize(batch);
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldSharedSetting(Id,Kind,Hash,Payload)
                SELECT json_extract(value,'$.Id'), json_extract(value,'$.Kind'),
                    json_extract(value,'$.Hash'), json_extract(value,'$.Payload')
                FROM json_each({values}) WHERE 1
                ON CONFLICT(Id) DO UPDATE SET Kind=excluded.Kind, Hash=excluded.Hash, Payload=excluded.Payload
                """
            );
        }
    }
}
