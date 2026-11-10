using System.Diagnostics;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.Capsuleers;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Persistence;

public sealed class MotionProjectionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1)]
    [InlineData(512)]
    [InlineData(2048)]
    public async Task TransactionProjectsMovingTrafficInBoundedBatchesAndCheckpointsLatestPositions(int count)
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        const long firstShip = 4_000_000_000_000;
        const int system = 30000141;
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            world.AddRange(
                Enumerable
                    .Range(0, count)
                    .Select(index => new Ship
                    {
                        Id = firstShip + index,
                        OwnerId = 90000001,
                        TypeId = 601,
                        StationId = 60015157,
                        Name = "Moving traffic",
                    })
            );
            world.SaveChanges();
        }
        using var live = new LiveWorld(fixture.Database, fixture.Clock, _ => { });
        void Move(int step)
        {
            using var owner = live.Enter();
            for (int index = 0; index < count; index++)
            {
                live.Combat!.PublishPilotPosition(
                    firstShip + index,
                    system,
                    new(1_000_000_000_000.125 + index * 0.125 + step, step, -step * 1e-50)
                );
            }
        }
        // Warm the insert path and measure subsequent ordinary coordinate updates.
        Move(0);
        using (WorldDbContext warm = new(fixture.Database.Path))
        using (warm.Database.BeginTransaction()) { }
        Move(1);
        using (var metrics = SessionPerformance.Begin("moving traffic transaction boundary", output.WriteLine, force: true)!)
        {
            long started = Stopwatch.GetTimestamp();
            using WorldDbContext world = new(fixture.Database.Path);
            using var transaction = world.Database.BeginTransaction();
            output.WriteLine($"Positions={count}, elapsed={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms.");
            Assert.Equal(count, world.Set<FlightPosition>().Count(p => p.ShipId >= firstShip && p.Y == 1));
            transaction.Commit();
            Assert.InRange(metrics.Commands, 2, (count + 127) / 128 + 1);
            Assert.Equal(2, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Move(2);
        using (var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot()))
        {
            using var command = snapshot.Database.CreateCommand();
            command.CommandText = $"""
                CREATE TRIGGER RejectPosition BEFORE UPDATE ON WorldPosition
                WHEN NEW.ShipId={firstShip + count - 1}
                BEGIN SELECT RAISE(ABORT,'reject last position'); END
                """;
            command.ExecuteNonQuery();
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => snapshot.Project());
            using WorldDbContext captured = new(snapshot.Database);
            Assert.Equal(count, captured.Set<FlightPosition>().Count(p => p.ShipId >= firstShip && p.Y == 1));
            command.CommandText = "DROP TRIGGER RejectPosition";
            command.ExecuteNonQuery();
            snapshot.Project();
            Assert.Equal(count, captured.Set<FlightPosition>().Count(p => p.ShipId >= firstShip && p.Y == 2));
        }
        live.MeasureCheckpoints = true;
        await live.FlushAsync();
        output.WriteLine($"Checkpoint={live.LastCheckpoint}");
        live.Dispose();
        using WorldDbContext reopened = new(fixture.Database.Path);
        Assert.Equal(count, reopened.Set<FlightPosition>().Count(p => p.ShipId >= firstShip && p.Y == 2 && p.SystemId == system));
        foreach (var position in reopened.Set<FlightPosition>().Where(p => p.ShipId >= firstShip).AsNoTracking())
        {
            Assert.Equal(1_000_000_000_002.125 + (position.ShipId - firstShip) * 0.125, position.X);
            Assert.Equal(-2e-50, position.Z);
        }
    }
}
