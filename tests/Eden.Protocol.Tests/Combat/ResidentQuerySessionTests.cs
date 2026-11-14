using System.Diagnostics;
using Eden.Data.World.Combat;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Skills;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Combat;

public sealed class ResidentQuerySessionTests(ITestOutputHelper output)
{
    private const long Pilot = 90000001;
    private const long OtherPilot = Pilot + 1;
    private const long Account = 1000001;

    [Theory]
    [InlineData(0)]
    [InlineData(10000)]
    public async Task EngagementRefreshPreservesCompleteSessionFrames(int unrelated)
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            for (int index = 0; index <= unrelated; index++)
            {
                long lower = index == 0 ? Pilot : OtherPilot + index;
                action.Add(
                    new SavedEngagement
                    {
                        LowerPilotId = lower,
                        HigherPilotId = lower + 1,
                        ExpiresAt = clock.Now + TimeSpan.TicksPerMinute,
                    }
                );
            }
            action.Commit();
        }
        using var firstScope = fixture.Services.CreateScope();
        using var secondScope = fixture.Services.CreateScope();
        using var first = Create(firstScope.ServiceProvider, Account);
        using var second = Create(secondScope.ServiceProvider, Account + 1);
        var firstTick = Tick(firstScope.ServiceProvider);
        var secondTick = Tick(secondScope.ServiceProvider);
        PilotSessionOutput firstOutput = new(first, Account, Account, _ => Task.CompletedTask, _ => { });
        PilotSessionOutput secondOutput = new(second, Account + 1, Account + 1, _ => Task.CompletedTask, _ => { });
        List<double> frames = [];
        for (int sample = 0; sample < 13; sample++)
        {
            clock.Seconds++;
            long expiry = clock.Now + TimeSpan.TicksPerMinute;
            using (var action = live.Economy.Begin(TransactionParticipants.None))
            {
                action.Set<SavedEngagement>().Find(Pilot, OtherPilot)!.ExpiresAt = expiry;
                action.Commit();
            }
            var timer = Stopwatch.StartNew();
            using var measured = SessionPerformance.Begin("engagement session pair", _ => { }, force: true)!;
            await firstTick.AdvanceAsync(first, firstOutput, _ => { }, Account, Account);
            await secondTick.AdvanceAsync(second, secondOutput, _ => { }, Account + 1, Account + 1);
            timer.Stop();
            if (sample >= 3)
            {
                frames.Add(timer.Elapsed.TotalMilliseconds);
                Assert.Equal(0, measured.Commands);
                Assert.Equal(0, measured.Connections);
                Assert.Equal(0, measured.FittingCalculations);
            }
            var crimewatch = fixture.Services.GetRequiredService<IPilotCrimewatch>();
            Assert.Equal(expiry, Assert.Single(crimewatch.Read(Pilot, first.Pilot.SystemId).Engagements).ExpiresAt);
            Assert.Equal(expiry, Assert.Single(crimewatch.Read(OtherPilot, second.Pilot.SystemId).Engagements).ExpiresAt);
        }
        var checkpoint = Stopwatch.StartNew();
        await live.FlushAsync();
        checkpoint.Stop();
        output.WriteLine(
            $"Unrelated {unrelated}; two-session frames mean {frames.Average():F3} ms, max {frames.Max():F3} ms; "
                + $"checkpoint {checkpoint.Elapsed.TotalMilliseconds:F3} ms."
        );

        PilotCallContext Create(IServiceProvider provider, long account)
        {
            var pilot = fixture.Store.FindPilot(account)!;
            PilotCallContext context = new(
                account,
                pilot.Account,
                [pilot],
                provider.GetRequiredService<FlightSessionFactory>().Create,
                account,
                SignedSettings.Empty
            );
            context.Select(pilot);
            provider.GetRequiredService<PilotUpdateCoordinator>().Enter(context);
            return context;
        }

        PilotSessionTick Tick(IServiceProvider provider) =>
            new(
                clock,
                provider.GetRequiredService<PilotUpdateCoordinator>(),
                provider.GetRequiredService<SkillHandler>(),
                provider.GetRequiredService<IAsteroidField>(),
                provider.GetRequiredService<IPilotOperationScope>()
            );
    }
}
