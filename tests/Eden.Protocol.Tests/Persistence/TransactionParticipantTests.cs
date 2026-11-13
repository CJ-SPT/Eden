using Eden.Data.World.Inventory;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Information;
using Eden.World.Market;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Persistence;

public sealed class TransactionParticipantTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(1, 5)]
    [InlineData(2, 5)]
    [InlineData(4, 5)]
    [InlineData(1, 10)]
    [InlineData(2, 10)]
    [InlineData(4, 10)]
    public void ClassifiedProjectionDoesNotGrowWithRemotePopulationOrObserverCount(int observers, int density)
    {
        using TestWorld db = new(liveWorld: true);
        const long first = 1000000000001;
        const long remoteStart = 2000000000000;
        const int local = 30000142;
        const int remote = 30000144;
        const int basePopulation = 128;
        int population = density * basePopulation;
        using (WorldDbContext world = new(db.Path))
        {
            var template = world.Ships.AsNoTracking().Single(s => s.Id == first);
            world.Ships.AddRange(
                Enumerable
                    .Range(1, population)
                    .Select(index => new Ship
                    {
                        Id = remoteStart + index,
                        OwnerId = template.OwnerId,
                        TypeId = template.TypeId,
                        StationId = template.StationId,
                        Name = "Remote resident",
                    })
            );
            world.SaveChanges();
        }
        var live = LiveWorld.Find(db.Path)!;
        using var owner = live.Enter();
        int Measure(bool classified)
        {
            live.Combat!.PublishPilotPosition(first, local, new(classified ? 2 : 1, 0, 0));
            for (int index = 1; index <= population; index++)
            {
                live.Combat.PublishPilotPosition(remoteStart + index, remote, new(classified ? 2 : 1, index, 0));
            }
            using var metrics = SessionPerformance.Begin("matched projection workload", _ => { }, force: true)!;
            using (WorldDbContext world = new(db.Path) { Participants = classified ? new(ships: [first]) : null })
            using (var transaction = world.Database.BeginTransaction())
            {
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(classified ? 2 : 1, world.Positions.Single(position => position.ShipId == first).X);
                transaction.Commit();
            }
            int projectionCommands = metrics.Commands;
            int projectionConnections = metrics.Connections;
            for (int observer = 0; observer < observers; observer++)
            {
                Assert.Equal(new SpacePosition(classified ? 2 : 1, 0, 0), live.Combat.InformationPosition(first));
                Assert.Equal(new SpacePosition(classified ? 2 : 1, 1, 0), live.Combat.InformationPosition(remoteStart + 1));
            }
            Assert.Equal(projectionCommands, metrics.Commands);
            Assert.Equal(projectionConnections, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
            return metrics.Commands;
        }
        int fallbackCommands = Measure(false);
        int participantCommands = Measure(true);
        Assert.Equal(2, participantCommands);
        Assert.True(fallbackCommands > participantCommands);
        Assert.Equal(population, live.Combat!.CaptureCheckpoint().Positions.Count);
    }

    [Fact]
    public void TransferInvalidatesBothSystemsAndKeepsUnrelatedSharedObservationWarm()
    {
        using TestWorld db = new(liveWorld: true);
        const long ship = 1000000000001;
        const int source = 30000142;
        const int destination = 30000144;
        const int other = 30000141;
        db.Store.SetPosition(ship, SpacePosition.Zero, source);
        var reads = LiveWorld.Find(db.Path)!.Reads!;
        IReadOnlyList<DirectionalEntry> Read(int system) =>
            reads.DirectionalContacts(
                system,
                () =>
                {
                    using WorldDbContext world = new(db.Path) { ReadsDirectionalPositions = true };
                    return world
                        .Positions.Where(p => p.SystemId == system)
                        .AsEnumerable()
                        .Select(p => new DirectionalEntry(new(p.ShipId, 601, 25, "Ship", new(p.X, p.Y, p.Z)), Ship: true))
                        .ToArray();
                }
            );
        Assert.Single(Read(source));
        Assert.Empty(Read(destination));
        var unrelated = Read(other);
        db.Store.SetPosition(ship, new(200, 0, 0), destination);
        using (var metrics = SessionPerformance.Begin("unrelated system observation", _ => { }, force: true)!)
        {
            Assert.Same(unrelated, Read(other));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Assert.Empty(Read(source));
        Assert.Equal(ship, Assert.Single(Read(destination)).Contact.Id);
        Assert.Same(Read(destination), Read(destination));
    }

    [Fact]
    public void UnchangedResourceMaintenanceUsesNoSqlOrProjection()
    {
        using TestWorld db = new(liveWorld: true);
        IResourceRenewal renewal = new ResourceRenewalStore(new(db.Path), new TestClock());
        Assert.Equal(0, renewal.Renew());
        using var metrics = SessionPerformance.Begin("unchanged resource deadline", _ => { }, force: true)!;
        for (int repeat = 0; repeat < 20; repeat++)
        {
            Assert.Equal(0, renewal.Renew());
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public async Task ClassifiedTransactionProjectsOnlyItsSystemAndCheckpointRetainsTheOther()
    {
        using TestWorld db = new(liveWorld: true);
        const long first = 1000000000001;
        const long second = 1000000000002;
        const int local = 30000142;
        const int remote = 30000144;
        db.Store.SetPosition(first, SpacePosition.Zero, local);
        db.Store.SetPosition(second, SpacePosition.Zero, remote);
        var live = LiveWorld.Find(db.Path)!;
        var combat = live.Combat!;
        using (live.Enter())
        {
            combat.PublishPilotPosition(first, local, new(100, 0, 0));
            combat.PublishPilotPosition(second, remote, new(200, 0, 0));
            using (WorldDbContext world = new(db.Path) { Participants = new(systems: [local]) })
            using (var transaction = world.Database.BeginTransaction())
            {
                Assert.Equal(100, world.Positions.Single(p => p.ShipId == first).X);
                // Inspect the stored image without causing the legacy read fallback.
                Assert.Equal(0, world.Positions.Single(p => p.ShipId == second).X);
                world.Database.ExecuteSql($"UPDATE WorldPosition SET X=999 WHERE ShipId={first}");
                transaction.Rollback();
            }
            Assert.Equal(new SpacePosition(100, 0, 0), combat.InformationPosition(first));
            // Projection participates in the rolled-back transaction, so both positions remain dirty.
            Assert.Equal(new[] { first, second }, combat.CaptureCheckpoint().Positions.Select(position => position.ShipId).Order());
            using var metrics = SessionPerformance.Begin("unrelated empty transaction", _ => { }, force: true)!;
            using (WorldDbContext world = new(db.Path) { Participants = TransactionParticipants.None })
            using (var transaction = world.Database.BeginTransaction())
            {
                transaction.Commit();
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        await live.FlushAsync();
        string checkpoint = Path.Combine(Path.GetDirectoryName(db.Path)!, "participants-checkpoint.sqlite");
        File.Copy(db.Path, checkpoint);
        using WorldDbContext saved = new(checkpoint);
        Assert.Equal(100, saved.Positions.Single(p => p.ShipId == first).X);
        Assert.Equal(200, saved.Positions.Single(p => p.ShipId == second).X);
    }

    [Fact]
    public void LaterRollbackOnOneConnectionDoesNotDiscardAnEarlierCommitNotification()
    {
        using TestWorld db = new(liveWorld: true);
        const long pilot = 90000001;
        PilotWalletReader wallet = new(new(db.Path));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSql($"INSERT OR IGNORE INTO WorldWallet(PilotId,Cents) VALUES({pilot},0)");
        }
        Assert.Equal(0, wallet.Balance(pilot));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.OpenConnection();
            using (var transaction = world.Database.BeginTransaction())
            {
                world.Database.ExecuteSql($"UPDATE WorldWallet SET Cents=1 WHERE PilotId={pilot}");
                transaction.Commit();
            }
            using (var transaction = world.Database.BeginTransaction())
            {
                transaction.Rollback();
            }
        }
        Assert.Equal(1, wallet.Balance(pilot));
    }

    [Fact]
    public void RemoteWalletCommitAndLocalRollbackPreserveWarmPersonalReads()
    {
        using TestWorld db = new(liveWorld: true);
        PilotWalletReader wallet = new(new(db.Path));
        const long pilot = 90000001;
        const long other = 90000002;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSql($"INSERT OR IGNORE INTO WorldWallet(PilotId,Cents) VALUES({pilot},0),({other},0)");
        }
        Assert.Equal(0, wallet.Balance(pilot));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSql($"UPDATE WorldWallet SET Cents=Cents+1 WHERE PilotId={other}");
        }
        using (WorldDbContext world = new(db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            world.Database.ExecuteSql($"UPDATE WorldWallet SET Cents=Cents+1 WHERE PilotId={pilot}");
            transaction.Rollback();
        }
        Assert.Equal(0, wallet.Balance(pilot));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSql($"UPDATE WorldWallet SET Cents=Cents+1 WHERE PilotId={pilot}");
        }
        Assert.Equal(1, wallet.Balance(pilot));
    }
}
