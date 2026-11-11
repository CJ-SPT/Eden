using System.Text;
using System.Text.Json;
using Eden.Gameplay.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial record HaulerCheckpoint(long PilotId, HaulerProgress Work)
{
    private static readonly JsonSerializerOptions Options = new() { IgnoreReadOnlyProperties = true };

    internal static HaulerProgress? Read(string? json) =>
        json is null
            ? null
            : JsonSerializer.Deserialize<HaulerProgress>(json, Options) ?? throw new InvalidDataException("Invalid hauler progress.");

    internal static void Apply(WorldDbContext world, IReadOnlyList<HaulerCheckpoint> checkpoints)
    {
        const int rowsPerBatch = 128;
        foreach (var batch in checkpoints.Chunk(rowsPerBatch))
        {
            StringBuilder sql = new();
            List<object> parameters = [];
            foreach (var checkpoint in batch)
            {
                int offset = parameters.Count;
                sql.Append($"UPDATE WorldCapsuleerHauler SET Progress=@p{offset} WHERE PilotId=@p{offset + 1};");
                parameters.Add(JsonSerializer.Serialize(checkpoint.Work, Options));
                parameters.Add(checkpoint.PilotId);
            }
            world.Database.ExecuteSqlRaw(sql.ToString(), parameters.ToArray());
        }
    }

    internal void Apply(WorldDbContext world)
    {
        string progress = JsonSerializer.Serialize(Work, Options);
        world.Database.ExecuteSql($"UPDATE WorldCapsuleerHauler SET Progress={progress} WHERE PilotId={PilotId}");
    }
}
