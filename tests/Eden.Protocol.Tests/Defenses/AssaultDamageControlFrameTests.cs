using System.Diagnostics;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Defenses;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Skills;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Defenses;

public sealed partial class AssaultDamageControlTests(ITestOutputHelper output)
{
    private sealed record FrameSample(double Milliseconds, long Commands, long Connections, long Fits);

    [Fact]
    public async Task CompleteOwnerAndObserverFramesKeepTheSameWarmSqlBudgetAndMeasureCheckpointSeparately()
    {
        using var f = Prepare();
        long module = f.Fit(Basic);
        Space(f);
        ObserverSpace(f);
        List<string> reports = [];
        using LiveWorld live = new(new(f.Db.Path), f.Clock, reports.Add) { MeasureCheckpoints = true };
        using var ownerScope = f.Db.Services.CreateScope();
        using var observerScope = f.Db.Services.CreateScope();
        var ownerServices = ownerScope.ServiceProvider;
        var observerServices = observerScope.ServiceProvider;
        using var owner = Client(f, 1000001);
        using var observer = Client(f, 1000002);
        var ownerUpdates = ownerServices.GetRequiredService<PilotUpdateCoordinator>();
        var observerUpdates = observerServices.GetRequiredService<PilotUpdateCoordinator>();
        ownerUpdates.Enter(owner);
        observerUpdates.Enter(observer);
        PilotSessionTick Tick(IServiceProvider services, PilotUpdateCoordinator updates) =>
            new(
                f.Clock,
                updates,
                services.GetRequiredService<SkillHandler>(),
                services.GetRequiredService<IAsteroidField>(),
                services.GetRequiredService<IPilotOperationScope>()
            );
        var ownerTick = Tick(ownerServices, ownerUpdates);
        var observerTick = Tick(observerServices, observerUpdates);
        PilotSessionOutput ownerOutput = new(owner, 1000001, 1000001, _ => Task.CompletedTask, _ => { });
        PilotSessionOutput observerOutput = new(observer, 1000002, 1000002, _ => Task.CompletedTask, _ => { });
        async Task Frame()
        {
            f.Clock.Milliseconds += 50;
            await ownerTick.AdvanceAsync(owner, ownerOutput, _ => { }, 1000001, 1000001);
            await observerTick.AdvanceAsync(observer, observerOutput, _ => { }, 1000002, 1000002);
        }
        async Task<FrameSample> Measure(string name)
        {
            for (int warm = 0; warm < 40; warm++)
            {
                await Frame();
            }
            using var metrics = SessionPerformance.Begin(name, _ => { }, force: true)!;
            var timer = Stopwatch.StartNew();
            for (int frame = 0; frame < 40; frame++)
            {
                await Frame();
            }
            timer.Stop();
            FrameSample sample = new(timer.Elapsed.TotalMilliseconds, metrics.Commands, metrics.Connections, metrics.FittingCalculations);
            output.WriteLine(
                $"{name}: {sample.Milliseconds:F2}ms, SQL={sample.Commands}, connections={sample.Connections}, fits={sample.Fits}"
            );
            return sample;
        }
        var baseline = await Measure("40 owner/observer frames without active assault damage control");
        var handler = ownerServices.GetRequiredService<AssaultDamageControlHandler>();
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(owner, "dogmaIM", "Activate", [module, AssaultDamageControlHandler.EffectName, null, 0L])
        );
        var active = await Measure("40 owner/observer frames with active assault damage control");
        Assert.True(active.Commands <= baseline.Commands);
        Assert.True(active.Connections <= baseline.Connections);
        Assert.Equal(0, active.Fits);
        Assert.NotNull(handler.ActiveEffect(owner.Pilot, module));
        var checkpoint = Stopwatch.StartNew();
        await live.FlushAsync();
        checkpoint.Stop();
        output.WriteLine($"Separate checkpoint: {checkpoint.Elapsed.TotalMilliseconds:F2}ms");
        foreach (string report in reports)
        {
            output.WriteLine(report);
        }
    }
}
