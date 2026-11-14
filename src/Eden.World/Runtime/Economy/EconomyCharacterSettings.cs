using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Economy;

internal sealed partial class ResidentEconomy
{
    private sealed record EconomyCharacterSetting(long PilotId, string Key, string? Text, string? BinaryHex);

    private void RegisterCharacterSettings() =>
        Register(
            new EconomyTable<SavedCharacterSetting, EconomyCharacterSetting>(
                "WorldCharacterSetting",
                row => new(row.PilotId, Name: row.Key),
                row => new(row.PilotId, row.Key, row.Text, row.Binary is { } bytes ? Convert.ToHexString(bytes) : null),
                state =>
                    new()
                    {
                        PilotId = state.PilotId,
                        Key = state.Key,
                        Text = state.Text,
                        Binary = state.BinaryHex is { } hex ? Convert.FromHexString(hex) : null,
                    },
                ProjectCharacterSettings,
                "PilotId,Key,Text,hex(Binary),Binary IS NULL",
                reader =>
                    new(
                        reader.GetInt64(1),
                        reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.GetBoolean(5) ? null : reader.GetString(4)
                    ),
                state => new(state.PilotId, Name: state.Key),
                indexes: [new(nameof(SavedCharacterSetting.PilotId), state => state.PilotId)],
                primaryColumn: null
            )
        );

    private static void ProjectCharacterSettings(WorldDbContext world, IReadOnlyList<EconomyChange<EconomyCharacterSetting>> changes)
    {
        foreach (var batch in changes.Chunk(ProjectionBatchSize))
        {
            var removed = batch.Where(change => change.Value is null).Select(change => change.Key).ToArray();
            if (removed.Length > 0)
            {
                string deleted = JsonSerializer.Serialize(removed);
                world.Database.ExecuteSql(
                    $"""
                    DELETE FROM WorldCharacterSetting
                    WHERE EXISTS (SELECT 1 FROM json_each({deleted}) WHERE
                        WorldCharacterSetting.PilotId=json_extract(value,'$.Id')
                        AND WorldCharacterSetting.Key=json_extract(value,'$.Name')
                    )
                    """
                );
            }
            var rows = batch.Select(change => change.Value).OfType<EconomyCharacterSetting>().ToArray();
            if (rows.Length > 0)
            {
                string values = JsonSerializer.Serialize(rows);
                world.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldCharacterSetting(PilotId,Key,Text,Binary)
                    SELECT json_extract(value,'$.PilotId'),json_extract(value,'$.Key'),json_extract(value,'$.Text'),
                        unhex(json_extract(value,'$.BinaryHex'))
                    FROM json_each({values}) WHERE 1
                    ON CONFLICT(PilotId,Key) DO UPDATE SET Text=excluded.Text,Binary=excluded.Binary
                    """
                );
            }
        }
    }
}
