using System.Text.Json;
using Eden.Gameplay.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed record HaulerCheckpoint(long PilotId, HaulerProgress Work)
{
    private static readonly JsonSerializerOptions Options = new() { IgnoreReadOnlyProperties = true };

    internal static HaulerProgress? Read(string? json) =>
        json is null
            ? null
            : JsonSerializer.Deserialize<HaulerProgress>(json, Options) ?? throw new InvalidDataException("Invalid hauler progress.");

    internal void Apply(WorldDbContext world)
    {
        string progress = JsonSerializer.Serialize(Work, Options);
        world.Database.ExecuteSql($"UPDATE WorldCapsuleerHauler SET Progress={progress} WHERE PilotId={PilotId}");
    }
}
