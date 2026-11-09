using System.Diagnostics;
using Eden.Gameplay.Information;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Flight;
using Eden.Protocol.Information;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Information;

public sealed partial class InformationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DirectionalCompleteSessionFramesCompareTheSameBaselineAndResidentWorkload()
    {
        foreach (bool resident in new[] { false, true })
        {
            using Fixture f = Prepare();
            Space(f);
            using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }, residentReads: resident);
            using var scope = f.Db.Services.CreateScope();
            var provider = scope.ServiceProvider;
            var pilot = f.Db.Store.FindPilot(1000001)!;
            using PilotCallContext context = new(
                1000001,
                pilot.Account,
                [pilot],
                provider.GetRequiredService<FlightSessionFactory>().Create,
                42,
                SignedSettings.Empty
            );
            context.Select(pilot);
            var updates = provider.GetRequiredService<PilotUpdateCoordinator>();
            updates.Enter(context);
            provider.GetRequiredService<SkillHandler>().Advance(context);
            context.Flight.Snapshot();
            PilotSessionOutput sessionOutput = new(
                context,
                1000001,
                42,
                packet =>
                {
                    _ = BlueMarshal.Encode(packet);
                    return Task.CompletedTask;
                },
                _ => { }
            );
            PilotSessionTick tick = new(
                f.Clock,
                updates,
                provider.GetRequiredService<SkillHandler>(),
                provider.GetRequiredService<IAsteroidField>(),
                provider.GetRequiredService<IPilotOperationScope>()
            );
            var scanner = provider.GetRequiredService<DirectionalScanHandler>();
            List<double> frames = [];
            for (int index = 0; index < 8; index++)
            {
                f.Clock.Milliseconds += (long)InformationRules.DirectionalScanCooldown.TotalMilliseconds;
                long started = Stopwatch.GetTimestamp();
                await tick.AdvanceAsync(context, sessionOutput, _ => { }, 1000001, 42);
                var reply = Assert.IsType<PilotCallResult.Success>(scanner.Call(context, "@systemScan", "ConeScan", Cone()));
                _ = BlueMarshal.Encode(reply.Reply.ToBlue());
                if (index >= 3)
                {
                    frames.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }
            using var checkpoint = live.CaptureSnapshot();
            double projection = checkpoint?.Project().TotalMilliseconds ?? 0;
            output.WriteLine(
                $"Resident reads={resident}: complete frame mean={frames.Average():F2} ms, max={frames.Max():F2} ms; "
                    + $"checkpoint capture={checkpoint?.Capture.TotalMilliseconds ?? 0:F2} ms, projection={projection:F2} ms."
            );
            Assert.True(context.Flight.InBallpark);
            Assert.NotEmpty(frames);
        }
    }
}
