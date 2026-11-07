using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.Ammunition;
using Eden.World.Combat.Npcs;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class DetachedCheckpointTests
{
    [Fact]
    public void ColdCrimewatchReadsDoNotProjectUnrelatedResidentCombat()
    {
        using AmmunitionTests.Fixture f = new(liveWorld: true);
        f.Load(quantity: 400);
        f.Clock.Milliseconds += 5000;
        f.Space();
        f.Fire();
        var crimewatch = f.Db.Services.GetRequiredService<IPilotCrimewatch>();
        List<string> reports = [];
        using (SessionPerformance.Begin("cold second pilot", reports.Add, force: true))
        {
            Assert.Equal(0, crimewatch.Read(90000002, 30000144).Timers.Npc.ActiveUntil);
        }
        Assert.DoesNotContain(reports, line => line.StartsWith("Performance combat.project:", StringComparison.Ordinal));
        using var snapshot = Assert.IsType<WorldSnapshot>(LiveWorld.Find(f.Db.Path)!.CaptureSnapshot());
        using WorldDbContext captured = new(snapshot.Database);
        Assert.Equal(200, captured.Set<OwnedItem>().AsNoTracking().Single(i => i.LocationId == f.Gun).Quantity);
        snapshot.Project();
        Assert.Equal(199, captured.Set<OwnedItem>().AsNoTracking().Single(i => i.LocationId == f.Gun).Quantity);
    }

    [Fact]
    public async Task CaptureUsesNoProjectionSqlAndAnAdvancingWorldCannotChangeTheDetachedGeneration()
    {
        using AmmunitionTests.Fixture f = new(liveWorld: true);
        f.Load(quantity: 400);
        f.Clock.Milliseconds += 5000;
        f.Space();
        var pilot = f.Pilot;
        CombatShot Fire() => f.Combat.Fire(pilot.PilotId, f.Gun, f.Npc.Id, SpacePosition.Zero, f.Clock.Now, 0.5, WeaponActivation.AmmoFed);
        var first = Fire();
        var live = LiveWorld.Find(f.Db.Path)!;
        WorldSnapshot snapshot;
        using (var metrics = SessionPerformance.Begin("capture", _ => { }, force: true)!)
        {
            snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using (snapshot)
        using (ManualResetEventSlim projecting = new())
        using (ManualResetEventSlim release = new())
        {
            int blocked = 0;
            SQLitePCL.raw.sqlite3_progress_handler(
                snapshot.Database.Handle!,
                1,
                _ =>
                {
                    if (Interlocked.Exchange(ref blocked, 1) == 0)
                    {
                        projecting.Set();
                        if (!release.Wait(TimeSpan.FromSeconds(10)))
                        {
                            return 1;
                        }
                    }
                    return 0;
                },
                null
            );
            var projection = Task.Run(snapshot.Project);
            CombatShot latest;
            try
            {
                Assert.True(projecting.Wait(TimeSpan.FromSeconds(10)));
                latest = await Task.Run(() =>
                    {
                        f.Clock.Milliseconds += 3000;
                        Fire();
                        using (WorldDbContext world = new(f.Db.Path))
                        {
                            world.Database.ExecuteSqlRaw("UPDATE WorldPilot SET Name='Changed after capture' WHERE Id=90000001");
                        }
                        f.Clock.Milliseconds += 3000;
                        return Fire();
                    })
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                release.Set();
            }
            await projection;
            using (WorldDbContext captured = new(snapshot.Database))
            {
                Assert.Equal(first.Health.Remaining.Shield, captured.Set<WorldNpc>().Find(f.Npc.Id)!.ShieldCharge);
                Assert.Equal(199, captured.Set<OwnedItem>().Single(i => i.LocationId == f.Gun).Quantity);
                Assert.NotEqual("Changed after capture", captured.Pilots.Find(pilot.PilotId)!.Name);
            }
            await live.FlushAsync();
            string checkpoint = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "detached-reopened.sqlite");
            File.Copy(f.Db.Path, checkpoint);
            using WorldDbContext reopened = new(checkpoint);
            Assert.Equal(latest.Health.Remaining.Shield, reopened.Set<WorldNpc>().Find(f.Npc.Id)!.ShieldCharge);
            Assert.Equal(197, reopened.Set<OwnedItem>().Single(i => i.LocationId == f.Gun).Quantity);
            Assert.Equal("Changed after capture", reopened.Pilots.Find(pilot.PilotId)!.Name);
            Assert.Equal(0, reopened.Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").Single());
        }
    }

    [Fact]
    public async Task FailedDetachedProjectionRollsBackItsPaymentsAndLeavesResidentStatePending()
    {
        using AmmunitionTests.Fixture f = new(liveWorld: true);
        f.Load(quantity: 400);
        f.Clock.Milliseconds += 5000;
        f.Space();
        var first = f.Fire();
        var live = LiveWorld.Find(f.Db.Path)!;
        using var snapshot = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        using (var command = snapshot.Database.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER RejectProjection BEFORE UPDATE ON WorldNpc BEGIN SELECT RAISE(ABORT,'test'); END";
            command.ExecuteNonQuery();
        }
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => snapshot.Project());
        using (WorldDbContext captured = new(snapshot.Database))
        {
            Assert.Equal(200, captured.Set<OwnedItem>().Single(i => i.LocationId == f.Gun).Quantity);
        }
        using (var command = snapshot.Database.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER RejectProjection";
            command.ExecuteNonQuery();
        }
        snapshot.Project();
        using (WorldDbContext captured = new(snapshot.Database))
        {
            Assert.Equal(199, captured.Set<OwnedItem>().Single(i => i.LocationId == f.Gun).Quantity);
            Assert.Equal(first.Health.Remaining.Shield, captured.Set<WorldNpc>().Find(f.Npc.Id)!.ShieldCharge);
        }
        await live.FlushAsync();
        string checkpoint = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "retry-reopened.sqlite");
        File.Copy(f.Db.Path, checkpoint);
        using WorldDbContext reopened = new(checkpoint);
        Assert.Equal(199, reopened.Set<OwnedItem>().Single(i => i.LocationId == f.Gun).Quantity);
    }
}
