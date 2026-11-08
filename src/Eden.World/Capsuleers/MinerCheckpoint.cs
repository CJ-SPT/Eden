using System.Text.Json;
using Eden.Gameplay.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed record MinerCheckpoint(long PilotId, MinerProgress Work)
{
    private static readonly JsonSerializerOptions Options = new() { IgnoreReadOnlyProperties = true };

    internal static MinerProgress? Read(string? json) =>
        json is null
            ? null
            : JsonSerializer.Deserialize<MinerProgress>(json, Options) ?? throw new InvalidDataException("Invalid miner progress.");

    internal void Apply(WorldDbContext world)
    {
        string progress = JsonSerializer.Serialize(Work, Options);
        world.Database.ExecuteSql($"UPDATE WorldCapsuleerCommunity SET Progress={progress} WHERE PilotId={PilotId}");
    }
}
