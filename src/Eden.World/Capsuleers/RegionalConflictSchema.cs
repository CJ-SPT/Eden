using System.Text.Json;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal static class RegionalConflictSchema
{
    internal static string Serialize(RegionalConflictRegion state) => JsonSerializer.Serialize(state);

    internal static RegionalConflictRegion Deserialize(string json) =>
        JsonSerializer.Deserialize<RegionalConflictRegion>(json) ?? throw new InvalidDataException("Invalid regional conflict checkpoint.");

    internal static void Configure(ModelBuilder model)
    {
        var row = model.Entity<SavedRegionalConflict>();
        row.ToTable("WorldRegionalConflict").HasKey(value => value.RegionId);
        row.Property(value => value.State).HasConversion(value => Serialize(value), json => Deserialize(json));
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldRegionalConflict(
                RegionId INTEGER PRIMARY KEY, State TEXT NOT NULL CHECK(json_valid(State)));
            """
        );
}
