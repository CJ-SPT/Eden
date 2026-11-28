using Eden.Data.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Persistence;

public sealed class IncrementalCheckpointTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WritersRunBetweenPageBatchesAndFinalImageMatchesResidentGeneration(bool rollback, bool diskCapture)
    {
        using TestWorld fixture = new();
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TABLE CheckpointPadding(Value BLOB); INSERT INTO CheckpointPadding VALUES(zeroblob(4194304))"
            );
        }
        using LiveWorld live = new(new(fixture.Path), new TestClock(), Console.WriteLine, diskCheckpointCapture: diskCapture);
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
        if (diskCapture)
        {
            string scratch = Assert.Single(CaptureFiles(fixture.Path));
            Assert.Equal(scratch, snapshot.Database.DataSource);
            snapshot.Dispose();
            Assert.Empty(CaptureFiles(fixture.Path));
        }
        live.CheckpointStepCompleted = null;
    }

    [Fact]
    public async Task DiskCaptureReclaimsAbandonedImagesAndCleansUpAfterCaptureAndWriteFailures()
    {
        const long pilot = 90000001;
        using TestWorld fixture = new();
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TABLE CheckpointPadding(Value BLOB); INSERT INTO CheckpointPadding VALUES(zeroblob(4194304))"
            );
            world.Add(new PilotWallet { PilotId = pilot, Cents = 100 });
            world.SaveChanges();
        }
        string abandoned = fixture.Path + ".checkpoint-capture-" + Guid.NewGuid().ToString("N");
        string unrelated = fixture.Path + ".checkpoint-capture-notes";
        File.WriteAllText(abandoned, "interrupted capture");
        File.WriteAllText(abandoned + "-journal", "interrupted journal");
        File.WriteAllText(unrelated, "retained");
        int attempts = 0;
        using LiveWorld live = new(
            new(fixture.Path),
            new CheckpointClock(new TestClock()),
            _ => { },
            (snapshot, path) =>
            {
                if (++attempts == 1)
                {
                    throw new IOException("Injected durable write failure.");
                }
                LiveWorld.PersistSnapshot(snapshot, path);
            },
            diskCheckpointCapture: true
        );
        Assert.False(File.Exists(abandoned));
        Assert.False(File.Exists(abandoned + "-journal"));
        Assert.Equal("retained", File.ReadAllText(unrelated));
        using (var action = live.Economy.Begin())
        {
            action.Set<PilotWallet>().Find(pilot)!.Cents = 200;
            action.Commit();
        }

        live.CheckpointStepCompleted = _ => throw new IOException("Injected capture failure.");
        await Assert.ThrowsAsync<IOException>(live.FlushAsync);
        Assert.Equal([unrelated], CaptureFiles(fixture.Path));
        Assert.Equal(0, attempts);
        Assert.False(live.CheckpointCopying);
        live.CheckpointStepCompleted = null;

        await Assert.ThrowsAsync<IOException>(live.FlushAsync);
        Assert.Equal([unrelated], CaptureFiles(fixture.Path));
        Assert.Equal(100, CentsIn(fixture.Path));
        Assert.Equal(1, attempts);

        await live.FlushAsync();
        Assert.Equal([unrelated], CaptureFiles(fixture.Path));
        Assert.Equal(200, CentsIn(fixture.Path));
        Assert.Equal(100, CentsIn(fixture.Path + ".previous"));
        Assert.Equal(2, attempts);

        static long CentsIn(string path)
        {
            using var connection = WorldConnections.FileConnection(path);
            using WorldDbContext saved = new(connection);
            return saved.Set<PilotWallet>().Find(pilot)!.Cents;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupReclaimsAbandonedCheckpointFilesRegardlessOfCaptureMode(bool diskCapture)
    {
        using TestWorld fixture = new();
        string identity = Guid.NewGuid().ToString("N");
        string capture = fixture.Path + ".checkpoint-capture-" + identity;
        string staging = fixture.Path + ".save-" + identity;
        string other = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "other.sqlite.save-" + identity);
        string notes = fixture.Path + ".save-notes";
        string previous = fixture.Path + ".previous";
        File.Copy(fixture.Path, previous);
        string[] abandoned = [capture, capture + "-journal", staging, staging + "-journal"];
        foreach (string path in abandoned.Concat([other, notes]))
        {
            File.WriteAllText(path, "checkpoint fixture");
        }

        using LiveWorld live = new(new(fixture.Path), new TestClock(), _ => { }, diskCheckpointCapture: diskCapture);

        Assert.All(abandoned, path => Assert.False(File.Exists(path)));
        Assert.True(File.Exists(previous));
        Assert.True(File.Exists(fixture.Path));
        Assert.Equal("checkpoint fixture", File.ReadAllText(other));
        Assert.Equal("checkpoint fixture", File.ReadAllText(notes));
    }

    private static string[] CaptureFiles(string world) =>
        Directory.GetFiles(Path.GetDirectoryName(world)!, Path.GetFileName(world) + ".checkpoint-capture-*");
}
