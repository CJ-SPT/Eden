using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Flight;

public sealed class StationUndockTests
{
    private sealed record StationCase(int TypeId, float Radius, int PointCount);

    private sealed record CatalogFixture(StationCase[] Stations, SourceRecord[] Records);

    private static readonly CatalogFixture Catalog = ReadCatalog();
    private static readonly SpacePosition Amsen = new(274073054690, -12959337432.6, -50994703838.8);
    private const int AirStationType = 59956;

    private static CatalogFixture ReadCatalog()
    {
        using var stream = typeof(StationUndockTests).Assembly.GetManifestResourceStream(
            "Eden.Protocol.Tests.Fixtures.StationUndockCatalog.json"
        )!;
        return JsonSerializer.Deserialize<CatalogFixture>(stream, JsonSerializerOptions.Web)!;
    }

    private static void AddCatalog(TestWorld db)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        foreach (var record in Catalog.Records)
        {
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data) VALUES ({record.Dataset},{record.Key},{record.Data})"
            );
        }
    }

    public static IEnumerable<object[]> StationTypes() => Catalog.Stations.Select(station => new object[] { station.TypeId });

    [Theory]
    [InlineData(100, 175)]
    [InlineData(400, 700)]
    [InlineData(1000, 700)]
    public void MissingMetadataUsesASeparatedForwardLaneAndTheNativeSpeedCap(float speed, double expected)
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        var hull = pilot.Flight with { MaxSpeed = speed };
        var first = StationUndocking.Launch(pilot.ShipId, pilot.Home, hull);
        var second = StationUndocking.Launch(pilot.ShipId + 1, pilot.Home, hull);
        Assert.Equal(new(0, 0, 1), first.Direction);
        Assert.Equal(expected, first.Velocity.Z);
        Assert.True(first.Position.DistanceTo(second.Position) > 2 * hull.ShipRadius);
    }

    [Theory]
    [MemberData(nameof(StationTypes))]
    public void EveryNpcStationTypeLoadsItsPinnedExitAndLeavesRoomForBothSmallAndLargeHulls(int typeId)
    {
        using TestWorld db = new();
        AddCatalog(db);
        var expected = Catalog.Stations.Single(station => station.TypeId == typeId);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        var points = StationUndockCatalog.Read(catalog, typeId);
        Assert.Equal(expected.PointCount, points.Count);
        var pilot = db.Store.FindPilot(1000001)!;
        var station = pilot.Home with { TypeId = typeId, Position = Amsen };
        foreach (var group in new[] { HullClass.Corvette, HullClass.Battleship, HullClass.Carrier, HullClass.Titan })
        {
            var hull = pilot.Flight with { StationRadius = expected.Radius, ShipRadius = group == HullClass.Titan ? 6000 : 47 };
            station = station with { UndockPoint = StationUndocking.SelectPoint(pilot.ShipId, (int)group, points) };
            var launch = StationUndocking.Launch(pilot.ShipId, station, hull);
            Assert.True(launch.Position.DistanceTo(Amsen) - expected.Radius - hull.ShipRadius >= 499.99);
            Assert.Equal(1, launch.Direction.Length, 8);
            Assert.Equal(Math.Min(1.75 * hull.MaxSpeed, 700), launch.Velocity.Length, 7);
            var matching = points.Where(point => point.Category == StationUndocking.Category((int)group)).ToArray();
            var candidates = matching.Length > 0 ? matching : points.ToArray();
            if (candidates.Length > 0)
            {
                var selected = candidates[(int)((ulong)pilot.ShipId % (uint)candidates.Length)];
                Assert.InRange(selected.Direction.DistanceTo(launch.Direction), 0, 1e-12);
                Assert.InRange((launch.Position - Amsen - selected.Position).Cross(launch.Direction).Length, 0, 0.0001);
            }
        }
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(-1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 0, -1)]
    public void AirUndockSnapshotAndImmediateWarpKeepTheNativeLaunchMotionAndPersistArrival(double x, double y, double z)
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        AddCatalog(db);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog
                .Set<ItemType>()
                .Add(
                    new()
                    {
                        Id = AirStationType,
                        GroupId = 15,
                        Name = "AIR Laboratories",
                        Radius = 17876,
                    }
                );
            var station = catalog.Set<Station>().Single(station => station.Id == 60003760);
            station.TypeId = AirStationType;
            (station.X, station.Y, station.Z) = (Amsen.X, Amsen.Y, Amsen.Z);
            catalog.SaveChanges();
        }
        var pilot = db.Store.FindPilot(1000001)!;
        SharedSpace space = new();
        using FlightSession session = new(db.Store, pilot, clock, space, db.Objects);
        Assert.Same(PilotCallResult.None, session.Execute(new FlightCommand.Undock(pilot.ShipId)));
        var launch = session.Pilot.Position!;
        Assert.True(session.Moving);
        Assert.InRange(session.Velocity.Length, 1.75 * pilot.Flight.MaxSpeed - 0.001, 700);
        Assert.InRange(session.Velocity.Z / session.Velocity.Length, 0.79, 1);
        session.Snapshot();
        var state = Assert.IsType<DestinyCommand.SetState>(Assert.Single(session.Updates).Command).Snapshot;
        using (BinaryReader reader = new(new MemoryStream(state.State)))
        {
            // Each rigid static ball occupies 39 bytes before the free ship.
            reader.BaseStream.Position = 5 + db.Objects.Count * 39 + 8;
            Assert.Equal((byte)NavigationMode.Goto, reader.ReadByte());
            reader.BaseStream.Position += 4 + 24 + 1 + 8 + 1 + 8 + 4 + 4 + 4;
            Assert.Equal(session.Velocity, new(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()));
        }
        var target = launch + new SpacePosition(x, y, z) * 1_000_000_000;
        Assert.Same(PilotCallResult.None, session.Execute(new FlightCommand.WarpToPoint(-1, target, 0)));
        bool enteredWarp = false;
        for (int tick = 0; tick < 240; tick++)
        {
            clock.Seconds++;
            session.Checkpoint();
            enteredWarp |= session.InWarp;
            if (!session.Moving)
            {
                break;
            }
        }
        Assert.True(enteredWarp);
        Assert.False(session.Moving);
        Assert.InRange(session.Pilot.Position!.DistanceTo(target), pilot.Flight.ShipRadius, pilot.Flight.ShipRadius + 2000);
        Assert.Equal(session.Pilot.Position, WorldInitialization.Open(db.Path).FindPilot(1000001)!.Position);
        Assert.DoesNotContain(session.Updates, update => update.Command is DestinyCommand.Stop);
    }
}
