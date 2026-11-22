using System.Text.Json;
using Eden.Gameplay.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial record MinerCheckpoint(long PilotId, MinerProgress Work)
{
    private static JsonSerializerOptions Options => CapsuleerCheckpointJson.Options;

    internal static MinerProgress? Read(string? json) =>
        json is null
            ? null
            : JsonSerializer.Deserialize<MinerProgress>(json, Options) ?? throw new InvalidDataException("Invalid miner progress.");

    internal static void Apply(WorldDbContext world, IReadOnlyList<MinerCheckpoint> checkpoints)
    {
        bool utf8 = checkpoints.Count != 0 && CapsuleerCheckpointJson.UsesUtf8(world);
        foreach (var batch in checkpoints.Chunk(CapsuleerCheckpointJson.ProjectionBatchSize))
        {
            object rows = utf8 ? JsonSerializer.SerializeToUtf8Bytes(batch, Options) : JsonSerializer.Serialize(batch, Options);
            world.Database.ExecuteSql(
                $"""
                UPDATE WorldCapsuleerCommunity SET Progress=json_extract(saved.value,'$.Work')
                FROM json_each(CAST({rows} AS TEXT)) AS saved
                WHERE PilotId=json_extract(saved.value,'$.PilotId')
                """
            );
        }
    }

    internal void Apply(WorldDbContext world)
    {
        string progress = JsonSerializer.Serialize(Work, Options);
        world.Database.ExecuteSql($"UPDATE WorldCapsuleerCommunity SET Progress={progress} WHERE PilotId={PilotId}");
    }
}
