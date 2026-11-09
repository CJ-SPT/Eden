using System.Diagnostics;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Repair;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerDefensePerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task WarmLocalRepairCyclesUseNoSqlAndCheckpointOnePaidCompletionAcrossRestart()
    {
        const int smallArmorRepairer = 523;
        using var f = CapsuleerDefenseModuleTests.Prepare(smallArmorRepairer, DogmaEffect.ArmorRepair);
        long moduleId = f.Fit(smallArmorRepairer);
        CapsuleerDefenseModuleTests.Space(f, DogmaEffect.ArmorRepair);
        using var live = new LiveWorld(new(f.Db.Path), f.Clock, _ => { });
        var runtime = live.Combat!;
        CapsuleerDefenseEquipment module;
        using (WorldDbContext world = new(f.Db.Path))
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            module = CapsuleerDefenseProfile.Read(world, catalog, f.PilotId).Modules.Single(value => value.Item.Id == moduleId);
        }
        var first = runtime.DefenseRepair(f.PilotId, module, f.Clock.Now)!;
        // Renewing a module already present in the previous projection must replace its saved completion.
        runtime.Flush();
        long now = first.ReadyAt;
        using (var metrics = SessionPerformance.Begin("warm local defense cycles", output.WriteLine, force: true)!)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            for (int cycle = 0; cycle < 10; cycle++)
            {
                first = runtime.DefenseRepair(f.PilotId, module, now)!;
                Assert.Throws<InvalidDataException>(() => runtime.DefenseRepair(f.PilotId, module, now));
                now = first.ReadyAt;
            }
            output.WriteLine(
                $"Ten paid repair cycles: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms, "
                    + $"{GC.GetAllocatedBytesForCurrentThread() - allocated} bytes."
            );
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        live.MeasureCheckpoints = true;
        await live.FlushAsync();
        var checkpoint = live.LastCheckpoint!;
        output.WriteLine(
            $"Repair checkpoint capture/projection/write: {checkpoint.Capture.TotalMilliseconds:F2}/"
                + $"{checkpoint.Projection.TotalMilliseconds:F2}/{checkpoint.Write.TotalMilliseconds:F2} ms."
        );
        live.Dispose();
        using (WorldDbContext saved = new(f.Db.Path))
        {
            var repair = saved.Set<PendingLocalRepair>().Single(value => value.ModuleId == moduleId);
            Assert.Equal(first.ReadyAt, repair.CompletesAt);
            Assert.Equal(first.StartedAt, repair.StartedAt);
            Assert.Equal(first.ReadyAt, saved.Set<ModuleCooldown>().Find(moduleId)!.ReadyAt);
        }
        using var reopened = new LiveWorld(new(f.Db.Path), f.Clock, _ => { });
        var completed = f.Db.Services.GetRequiredService<IPilotHealth>().Read(f.PilotId, first.ReadyAt);
        Assert.Equal(completed.Defense.Armor, completed.Remaining.Armor);
        Assert.Equal(completed.Remaining, f.Db.Services.GetRequiredService<IPilotHealth>().Read(f.PilotId, first.ReadyAt).Remaining);
        await reopened.FlushAsync();
        using WorldDbContext final = new(f.Db.Path);
        Assert.Empty(final.Set<PendingLocalRepair>());
    }
}
