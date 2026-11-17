using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.IO.Compression;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.Catalog.Flight;
using Eden.Gameplay.Flight;

namespace Eden.World.Catalog;

internal static class StaticCollisionCatalog
{
    private const int ClientBuild = 3662168;
    private const string ResourceName = "Eden.Data.StaticCollision.json.gz";

    private sealed record Payload(
        FrozenDictionary<int, StaticCollisionTypeBinding> Types,
        FrozenDictionary<string, StaticCollisionProfile> Profiles
    );

    private static readonly Lazy<Payload> s_payload = new(LoadPayload);
    private static readonly ConcurrentDictionary<string, Lazy<StaticCollisionGeometry>> s_models = new(StringComparer.Ordinal);
    private static readonly CatalogDefinition<StaticCollisionGeometry?> s_geometry = new("static-collision-geometry", Load);

    internal static StaticCollisionGeometry? Read(CatalogDbContext catalog, int typeId) => catalog.Definition(s_geometry, typeId);

    private static Payload LoadPayload()
    {
        using var source =
            typeof(StaticCollisionPayload).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The pinned static collision catalog is missing.");
        using GZipStream decoded = new(source, CompressionMode.Decompress);
        var data =
            JsonSerializer.Deserialize(decoded, StaticCollisionJsonContext.Default.StaticCollisionPayload)
            ?? throw new InvalidDataException("The pinned static collision catalog is empty.");
        if (data.Build != ClientBuild)
        {
            throw new InvalidDataException("The static collision catalog targets another client build.");
        }
        return new(data.Types.ToFrozenDictionary(binding => binding.TypeId), data.Profiles.ToFrozenDictionary(profile => profile.Key));
    }

    private static StaticCollisionGeometry? Load(CatalogDbContext catalog, int typeId)
    {
        var payload = s_payload.Value;
        if (
            !payload.Types.TryGetValue(typeId, out var binding)
            || catalog.Read(CatalogData.Client.Types, (long)typeId)?.GraphicID != binding.GraphicId
        )
        {
            // Legacy or modified graphics without a matching baked mesh retain the catalog sphere.
            return null;
        }
        var graphic = catalog.Read(CatalogData.Client.Graphicids, (long)binding.GraphicId);
        bool matches =
            binding.Hull is { } hull ? string.Equals(graphic?.SofHullName, hull, StringComparison.OrdinalIgnoreCase)
            : binding.GraphicFile is { } file ? string.Equals(graphic?.GraphicFile, file, StringComparison.OrdinalIgnoreCase)
            : false;
        if (!matches)
        {
            return null;
        }
        return s_models.GetOrAdd(binding.Profile, key => new(() => Build(payload.Profiles[key]))).Value;
    }

    internal static StaticCollisionGeometry Build(StaticCollisionProfile profile)
    {
        SpacePosition Position(CollisionGridPoint point) => new(point.X * profile.Pitch, point.Y * profile.Pitch, point.Z * profile.Pitch);

        var spheres = profile.Spheres.Select(cell =>
        {
            var position = Position(cell);
            return new StaticCollisionPrimitive(position, position, profile.Radius);
        });
        var capsules = profile.Capsules.Select(capsule => new StaticCollisionPrimitive(
            Position(capsule.Start),
            Position(capsule.End),
            profile.Radius
        ));
        return new(spheres.Concat(capsules).ToArray());
    }
}
