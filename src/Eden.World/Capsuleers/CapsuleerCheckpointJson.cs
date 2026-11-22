using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Eden.Gameplay.Flight;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal static class CapsuleerCheckpointJson
{
    internal const int ProjectionBatchSize = 128;

    internal static JsonSerializerOptions Options { get; } =
        new()
        {
            IgnoreReadOnlyProperties = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OmitFollowTargetCollision } },
        };

    // SQLite casts a blob to text using the database encoding. Keep text parameters for UTF-16 saves.
    internal static bool UsesUtf8(WorldDbContext world) =>
        world.Database.SqlQueryRaw<string>("PRAGMA encoding").AsEnumerable().Single() == "UTF-8";

    private static void OmitFollowTargetCollision(JsonTypeInfo type)
    {
        if (type.Type == typeof(StaticSpaceObject))
        {
            // A restored course resolves its target by ID. Collision obstacles come from the system view.
            var geometry = type.Properties.Single(property => property.Name == nameof(StaticSpaceObject.Collision));
            type.Properties.Remove(geometry);
        }
    }
}
