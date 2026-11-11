using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Protocol.Flight;
using Eden.World.Catalog;
using Eden.World.Exploration;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Exploration;

public sealed class AnomalyWarpInTests
{
    [Fact]
    public void SerpentisRefugeAndEveryOtherLayoutSendAsteroidsWithPositiveNativeRotationPeriods()
    {
        using TestWorld test = new();
        var database = new WorldDatabase(test.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        const int serpentisRefugeDungeon = 2315;
        var definitions = CombatAnomalyCatalog.Read(catalog);
        var refuge = definitions.Single(d => d.DungeonId == serpentisRefugeDungeon);
        Assert.Equal("Serpentis Refuge", refuge.Name);
        Assert.Contains(refuge.Objects, o => o.CategoryId == AsteroidDefinitions.Category);
        foreach (var definition in definitions)
        {
            foreach (var body in definition.Objects.Where(o => o.CategoryId == AsteroidDefinitions.Category))
            {
                using BinaryReader reader = new(new MemoryStream(Ballpark.AddObjects([body], 7).State));
                Assert.Equal(0, reader.ReadByte());
                Assert.Equal(7, reader.ReadInt32());
                Assert.Equal(body.Id, reader.ReadInt64());
                Assert.Equal((byte)NavigationMode.Rigid, reader.ReadByte());
                float radius = reader.ReadSingle();
                double minimumPeriod = 50 * Math.Log(radius);
                Assert.True(
                    double.IsFinite(minimumPeriod) && minimumPeriod > 0,
                    $"Dungeon {definition.DungeonId} asteroid {body.Id} has unsafe native rotation radius {radius}."
                );
            }
        }
    }

    [Theory]
    [InlineData(2341)]
    [InlineData(2354)]
    [InlineData(2188)]
    [InlineData(2213)]
    [InlineData(14215)]
    [InlineData(14216)]
    public void DroneAssemblyAndClusterLandingsRemainStableFromEveryDirection(int dungeonId)
    {
        using TestWorld test = new();
        var database = new WorldDatabase(test.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var definition = CombatAnomalyCatalog.Find(catalog, dungeonId)!;
        FlightProperties hull = new(0, 500, 100_000_000, 0.1f, 100, 3);
        SpacePosition[] origins = [new(1e9, 0, 0), new(-1e9, 0, 0), new(0, 1e9, 0), new(0, -1e9, 0), new(0, 0, 1e9), new(0, 0, -1e9)];
        foreach (var origin in origins)
        {
            foreach (long shipId in new long[] { 1, 2 })
            {
                var landing = WarpCourse.LandingPoint(shipId, hull.ShipRadius, origin, SpacePosition.Zero, 0);
                CollisionMotion arrival = new(shipId, hull, landing, SpacePosition.Zero, landing, SpacePosition.Zero);
                for (int frame = 0; frame < 10; frame++)
                {
                    var result = ShipCollisions.ResolveStatic(arrival, definition.Objects);
                    Assert.False(result.Bumped, $"Dungeon {dungeonId} ejects a landed ship in frame {frame}.");
                    Assert.Equal(0, result.Position.DistanceTo(landing));
                    Assert.Equal(0, result.Velocity.Length);
                }
            }
        }
    }

    [Fact]
    public void DroneClusterDecorativeWreckageIsNotSentAsAMassiveBall()
    {
        using TestWorld test = new();
        var database = new WorldDatabase(test.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        const int droneClusterDungeon = 2188;
        const int decorativeShuttleWreckageType = 64550;
        var body = Assert.Single(
            CombatAnomalyCatalog.Find(catalog, droneClusterDungeon)!.Objects,
            o => o.TypeId == decorativeShuttleWreckageType
        );
        Assert.Equal(6000f, body.Radius);
        Assert.False(body.Solid);
        using BinaryReader reader = new(new MemoryStream(Ballpark.AddObjects([body], 7).State));
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(7, reader.ReadInt32());
        Assert.Equal(body.Id, reader.ReadInt64());
        Assert.Equal((byte)NavigationMode.Rigid, reader.ReadByte());
        Assert.Equal(body.Radius, reader.ReadSingle());
        reader.ReadDouble();
        reader.ReadDouble();
        reader.ReadDouble();
        var flags = (DestinyBallFlags)reader.ReadByte();
        Assert.False(flags.HasFlag(DestinyBallFlags.Massive));
    }

    [Fact]
    public void NativeAnomalyEntryPointsDoNotEjectStationaryBattleships()
    {
        using TestWorld test = new();
        var database = new WorldDatabase(test.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        FlightProperties hull = new(0, 500, 100_000_000, 0.1f, 100, 3);
        CollisionMotion arrival = new(1, hull, SpacePosition.Zero, SpacePosition.Zero, SpacePosition.Zero, SpacePosition.Zero);
        var definitions = CombatAnomalyCatalog.Read(catalog);
        Assert.Equal(199, definitions.Length);
        foreach (var definition in definitions)
        {
            var result = ShipCollisions.ResolveStatic(arrival, definition.Objects);
            Assert.False(result.Bumped, $"Dungeon {definition.DungeonId} ({definition.Name}) ejects a ship at its native entry.");
            Assert.Equal(0, result.Position.Length);
            Assert.Equal(0, result.Velocity.Length);
        }
    }

    [Fact]
    public void ActualLandingOffsetsClearSolidGeometryAcrossEveryExistingLayout()
    {
        using TestWorld test = new();
        var database = new WorldDatabase(test.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        FlightProperties hull = new(0, 500, 100_000_000, 0.1f, 100, 3);
        SpacePosition[] directions = [new(1e9, 0, 0), new(-1e9, 0, 0), new(0, 1e9, 0), new(0, -1e9, 0), new(0, 0, 1e9), new(0, 0, -1e9)];
        foreach (var definition in CombatAnomalyCatalog.Read(catalog))
        {
            foreach (var origin in directions)
            {
                foreach (long shipId in new long[] { 1, 2 })
                {
                    var anchor = SiteWarpInRules.ResolveAnchor(shipId, hull.ShipRadius, origin, SpacePosition.Zero, 0, definition.Objects);
                    var landing = WarpCourse.LandingPoint(shipId, hull.ShipRadius, origin, anchor, 0);
                    CollisionMotion arrival = new(shipId, hull, landing, SpacePosition.Zero, landing, SpacePosition.Zero);
                    var result = ShipCollisions.ResolveStatic(arrival, definition.Objects);
                    Assert.False(result.Bumped, $"Dungeon {definition.DungeonId} has an obstructed actual landing.");
                    Assert.Equal(0, result.Position.DistanceTo(landing));
                }
            }
        }
    }
}
