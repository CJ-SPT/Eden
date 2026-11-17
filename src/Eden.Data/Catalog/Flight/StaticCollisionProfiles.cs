using System.Text.Json.Serialization;

namespace Eden.Data.Catalog.Flight;

internal sealed record StaticCollisionPayload(
    int Build,
    IReadOnlyList<StaticCollisionTypeBinding> Types,
    IReadOnlyList<StaticCollisionProfile> Profiles
);

internal sealed record StaticCollisionTypeBinding(int TypeId, int GraphicId, string Profile, string? Hull, string? GraphicFile);

internal readonly record struct CollisionGridPoint(int X, int Y, int Z);

internal sealed record CollisionGridCapsule(CollisionGridPoint Start, CollisionGridPoint End);

internal sealed record StaticCollisionProfile(
    string Key,
    double Pitch,
    float Radius,
    IReadOnlyList<CollisionGridPoint> Spheres,
    IReadOnlyList<CollisionGridCapsule> Capsules
);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StaticCollisionPayload))]
internal sealed partial class StaticCollisionJsonContext : JsonSerializerContext;
