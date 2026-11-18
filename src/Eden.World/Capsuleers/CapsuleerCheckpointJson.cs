using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Eden.Gameplay.Flight;

namespace Eden.World.Capsuleers;

internal static class CapsuleerCheckpointJson
{
    internal static JsonSerializerOptions Options { get; } =
        new()
        {
            IgnoreReadOnlyProperties = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OmitFollowTargetCollision } },
        };

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
