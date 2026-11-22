using System.Text.Json;
using Eden.Gameplay.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial record HaulerCheckpoint(long PilotId, HaulerProgress Work)
{
    private static JsonSerializerOptions Options => CapsuleerCheckpointJson.Options;

    internal static HaulerProgress? Read(string? json) =>
        json is null
            ? null
            : JsonSerializer.Deserialize<HaulerProgress>(json, Options) ?? throw new InvalidDataException("Invalid hauler progress.");

    internal static void Apply(WorldDbContext world, IReadOnlyList<HaulerCheckpoint> checkpoints)
    {
        bool utf8 = checkpoints.Count != 0 && CapsuleerCheckpointJson.UsesUtf8(world);
        foreach (var batch in checkpoints.Chunk(CapsuleerCheckpointJson.ProjectionBatchSize))
        {
            object rows = utf8 ? JsonSerializer.SerializeToUtf8Bytes(batch, Options) : JsonSerializer.Serialize(batch, Options);
            world.Database.ExecuteSql(
                $"""
                UPDATE WorldCapsuleerHauler SET Progress=json_extract(saved.value,'$.Work')
                FROM json_each(CAST({rows} AS TEXT)) AS saved
                WHERE PilotId=json_extract(saved.value,'$.PilotId')
                """
            );
        }
    }

    internal void Apply(WorldDbContext world)
    {
        string progress = JsonSerializer.Serialize(Work, Options);
        world.Database.ExecuteSql($"UPDATE WorldCapsuleerHauler SET Progress={progress} WHERE PilotId={PilotId}");
    }
}
