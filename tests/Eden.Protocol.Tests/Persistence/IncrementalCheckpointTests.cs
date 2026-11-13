using Eden.Data.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Persistence;

public sealed class IncrementalCheckpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WritersRunBetweenPageBatchesAndFinalImageMatchesResidentGeneration(bool rollback)
    {
        using TestWorld fixture = new();
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TABLE CheckpointPadding(Value BLOB); INSERT INTO CheckpointPadding VALUES(zeroblob(4194304))"
            );
        }
        using LiveWorld live = new(new(fixture.Path), new TestClock(), Console.WriteLine);
        using (var action = live.Economy.Begin())
        {
            action.Add(new PilotWallet { PilotId = 90000001, Cents = 100 });
            action.Commit();
        }
        using ManualResetEventSlim copied = new();
        using ManualResetEventSlim resume = new();
        live.CheckpointStepCompleted = step =>
        {
            if (step == 1)
            {
                Assert.False(live.HasPersistenceOwnership);
                copied.Set();
                Assert.True(resume.Wait(TimeSpan.FromSeconds(20)));
            }
        };
        var capturing = Task.Run(live.CaptureSnapshot);
        try
        {
            Assert.True(copied.Wait(TimeSpan.FromSeconds(20)));
            await Task.Run(() =>
                {
                    using (WorldDbContext world = new(fixture.Path))
                    {
                        using var transaction = world.Database.BeginTransaction();
                        world.Database.ExecuteSqlRaw("UPDATE WorldPilot SET Name='Between batches' WHERE Id=90000001");
                        if (rollback)
                        {
                            transaction.Rollback();
                        }
                        else
                        {
                            transaction.Commit();
                        }
                    }
                    using var action = live.Economy.Begin();
                    action.Set<PilotWallet>().Find(90000001)!.Cents = 200;
                    action.Commit();
                })
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            resume.Set();
            live.CheckpointStepCompleted = null;
        }
        using var snapshot = Assert.IsType<WorldSnapshot>(await capturing.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.True(snapshot.CopySteps > 1);
        Assert.True(snapshot.MaximumCopyPause > TimeSpan.Zero);
        snapshot.Project();
        using (WorldDbContext saved = new(snapshot.Database))
        {
            Assert.Equal(200, saved.Set<PilotWallet>().Find(90000001L)!.Cents);
            Assert.Equal(!rollback, saved.Pilots.Find(90000001L)!.Name == "Between batches");
        }
        live.CheckpointStepCompleted = null;
    }
}
