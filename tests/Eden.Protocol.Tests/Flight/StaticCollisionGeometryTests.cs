using System.IO.Compression;
using System.Text.Json;
using Eden.Data.Catalog.Flight;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.World.Catalog;

namespace Eden.Protocol.Tests.Flight;

public sealed class StaticCollisionGeometryTests
{
    private static readonly FlightProperties Hull = new(1000, 10, 1_000_000, 1, 100, 3);

    private static StaticCollisionGeometry Frame() =>
        new([
            new(new(0, -100, 200), new(0, 100, 200), 20),
            new(new(0, -100, -200), new(0, 100, -200), 20),
            new(new(0, 200, -100), new(0, 200, 100), 20),
            new(new(0, -200, -100), new(0, -200, 100), 20),
            new(new(400, 200, 200), new(400, 200, 200), 20),
        ]);

    [Fact]
    public void EmbeddedClientProfilesHaveValidSharedBindingsAndNativeCollisionChildren()
    {
        using var source = typeof(StaticCollisionPayload).Assembly.GetManifestResourceStream("Eden.Data.StaticCollision.json.gz")!;
        using GZipStream decoded = new(source, CompressionMode.Decompress);
        var payload = JsonSerializer.Deserialize(decoded, StaticCollisionJsonContext.Default.StaticCollisionPayload)!;
        Assert.Equal(3662168, payload.Build);
        Assert.Equal(payload.Types.Count, payload.Types.Select(binding => binding.TypeId).Distinct().Count());
        Assert.Equal(payload.Profiles.Count, payload.Types.Select(binding => binding.Profile).Distinct().Count());
        var keys = payload.Profiles.Select(profile => profile.Key).ToHashSet();
        foreach (var binding in payload.Types)
        {
            Assert.Contains(binding.Profile, keys);
            Assert.True(binding.Hull is not null || binding.GraphicFile is not null);
        }
        foreach (var profile in payload.Profiles)
        {
            Assert.True(double.IsFinite(profile.Pitch) && profile.Pitch > 0);
            var geometry = StaticCollisionCatalog.Build(profile);
            Assert.InRange(geometry.Primitives.Count, 1, 2000);
            Assert.True(double.IsFinite(geometry.BoundingRadius));
            StaticSpaceObject body = new(2, 35835, 1406, 65, 1, SpacePosition.Zero, 45_000) { Collision = geometry };
            var state = Ballpark.AddObjects([body], 5).State;
            long length =
                5
                + 39
                + (geometry.Spheres.Count > 0 ? 2 + 28 * geometry.Spheres.Count : 0)
                + (geometry.Capsules.Count > 0 ? 2 + 52 * geometry.Capsules.Count : 0);
            Assert.Equal(length, state.LongLength);
        }
    }

    [Fact]
    public void CompoundHullPreservesPassagesAndRepelsCapsuleSidesAndEndcaps()
    {
        var geometry = Frame();
        StaticSpaceObject body = new(2, 35835, 1406, 65, 1, SpacePosition.Zero, 1000) { Collision = geometry };
        CollisionMotion clear = new(1, Hull, new(-100, 0, 0), new(200, 0, 0), new(100, 0, 0), new(200, 0, 0));
        Assert.Equal(ShipCollisions.ResolveStatic(clear, []), ShipCollisions.ResolveStatic(clear, [body]));
        var side = clear with { Start = new(-100, 0, 200), Position = new(100, 0, 200) };
        Assert.True(ShipCollisions.ResolveStatic(side, [body]).Bumped);
        var sideImpact = Assert.IsType<StaticCollisionImpact>(geometry.Primitives[0].Sweep(side.Start, side.Position, Hull.ShipRadius));
        Assert.Equal(0.35, sideImpact.Time, 8);
        Assert.Equal(new(0, 0, 200), sideImpact.Centre);
        var endcap = Assert.IsType<StaticCollisionImpact>(
            geometry.Primitives[0].Sweep(new(-100, 120, 200), new(100, 120, 200), Hull.ShipRadius)
        );
        Assert.Equal(new(0, 100, 200), endcap.Centre);
        Assert.InRange(endcap.Time, 0.38, 0.40);
        Assert.Null(geometry.Primitives[0].Sweep(new(-100, 131, 200), new(100, 131, 200), Hull.ShipRadius));
        Assert.Equal(SpacePosition.Zero, geometry.Unstick(SpacePosition.Zero, Hull.ShipRadius));
        Assert.Equal(
            SpacePosition.Zero,
            SiteWarpInRules.ResolveAnchor(1, Hull.ShipRadius, new(10_000, 0, 0), SpacePosition.Zero, 0, [body])
        );
        Assert.False(geometry.Intersects(geometry.Unstick(new(0, 0, 200), Hull.ShipRadius), Hull.ShipRadius));
        PilotStation station = new(
            2,
            35835,
            1,
            new(30001392, 200002, 100002),
            0,
            0,
            SpacePosition.Zero,
            new("Compound hull", 1406, 65),
            new(UndockCategory.Small, new(0, 0, 200), new(1, 0, 0))
        )
        {
            Collision = geometry,
        };
        var launch = StationUndocking.Launch(1, station, Hull);
        Assert.InRange(launch.Position.X, 531, 999);
        Assert.False(geometry.Intersects(launch.Position, Hull.ShipRadius));
        Assert.Equal(200, launch.Position.Z);
    }

    [Fact]
    public void WarmedCompoundQueriesStayLocalAndShareRotatedGeometry()
    {
        var geometry = new StaticCollisionGeometry(
            Enumerable
                .Range(0, 20_000)
                .Select(index => new StaticCollisionPrimitive(new(index * 1000, -100, 0), new(index * 1000, 100, 0), 20))
                .ToArray()
        );
        var rotated = geometry.Rotate(Math.PI / 2);
        Assert.Same(rotated, geometry.Rotate(Math.PI / 2));
        Assert.Same(geometry, rotated.Rotate(-Math.PI / 2));
        SpacePosition start = new(-100, 0, 0);
        SpacePosition end = new(100, 0, 0);
        List<int> candidates = [];
        for (int repeat = 0; repeat < 100; repeat++)
        {
            geometry.Find(start, end, 10, candidates);
        }
        Assert.Equal(new[] { 0 }, candidates);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int maximum = 0;
        for (int repeat = 0; repeat < 1000; repeat++)
        {
            geometry.Find(start, end, 10, candidates);
            maximum = Math.Max(maximum, candidates.Count);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1, maximum);
        Assert.InRange(allocated, 0, 1024);
        Assert.Throws<ArgumentException>(() => new StaticCollisionGeometry([new(SpacePosition.Zero, SpacePosition.Zero, float.NaN)]));
    }

    [Fact]
    public void NativeRigidStateUsesLocalChildrenWithoutTheEnclosingSolidSphere()
    {
        var geometry = Frame();
        StaticSpaceObject body = new(2, 35835, 1406, 65, 1, new(1_000_000_000_000, 0, 0), 1000) { Collision = geometry };
        var addition = Ballpark.AddObjects([body], 5);
        using MemoryStream bytes = new(addition.State);
        using BinaryReader reader = new(bytes);
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(5, reader.ReadInt32());
        Assert.Equal(body.Id, reader.ReadInt64());
        Assert.Equal((byte)NavigationMode.Rigid, reader.ReadByte());
        Assert.Equal(body.Radius, reader.ReadSingle());
        Assert.Equal(body.Position, Position());
        var flags = (DestinyBallFlags)reader.ReadByte();
        Assert.False(flags.HasFlag(DestinyBallFlags.Massive));
        Assert.True(flags.HasFlag(DestinyBallFlags.HasMiniBalls));
        Assert.True(flags.HasFlag(DestinyBallFlags.HasMiniCapsules));
        Assert.Equal(-1, reader.ReadSByte());
        Assert.Equal(geometry.Spheres.Count, reader.ReadUInt16());
        foreach (var sphere in geometry.Spheres)
        {
            Assert.Equal(sphere.Start, Position());
            Assert.Equal(sphere.Radius, reader.ReadSingle());
        }
        Assert.Equal(geometry.Capsules.Count, reader.ReadUInt16());
        foreach (var capsule in geometry.Capsules)
        {
            Assert.Equal(capsule.Start, Position());
            Assert.Equal(capsule.End, Position());
            Assert.Equal(capsule.Radius, reader.ReadSingle());
        }
        Assert.Equal(bytes.Length, bytes.Position);

        SpacePosition Position() => new(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
    }
}
