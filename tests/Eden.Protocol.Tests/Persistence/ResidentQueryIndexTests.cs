using System.Diagnostics;
using System.Linq.Expressions;
using Eden.Data.World.Combat;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Persistence;

public sealed class ResidentQueryIndexTests(ITestOutputHelper output)
{
    private const long Pilot = 90000001;
    private const long OtherPilot = Pilot + 1;
    private const long EarlierPilot = Pilot - 1;
    private const long Deadline = 100;

    [Theory]
    [InlineData(0)]
    [InlineData(10000)]
    public void DisjunctionOnlyVisitsIndexedEngagementCandidates(int unrelated)
    {
        var table = Table();
        table.Publish(new(EarlierPilot, Pilot), new(EarlierPilot, Pilot, 0, Deadline));
        table.Publish(new(Pilot, OtherPilot), new(Pilot, OtherPilot, 0, Deadline));
        for (int i = 0; i < unrelated; i++)
        {
            long lower = OtherPilot + i + 1;
            table.Publish(new(lower, lower + 1), new(lower, lower + 1, 0, Deadline));
        }
        CandidateCounter counter = new();
        Expression<Func<SavedEngagement, bool>> predicate = row =>
            counter.Visit(row.ExpiresAt) && (row.LowerPilotId == Pilot || row.HigherPilotId == Pilot);
        table.Matching(predicate).ToArray();
        const int iterations = 20;
        counter.Visits = 0;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            Assert.Equal(
                new[] { new EconomyKey(EarlierPilot, Pilot), new EconomyKey(Pilot, OtherPilot) },
                table.Matching(predicate).OrderBy(key => key.Id).ToArray()
            );
        }
        timer.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine(
            $"Rows {unrelated + 2}; visits {counter.Visits / iterations}; "
                + $"mean {timer.Elapsed.TotalMilliseconds / iterations:F3} ms; allocated {allocated / iterations} bytes/query."
        );
        Assert.Equal(2 * iterations, counter.Visits);
    }

    [Fact]
    public void DisjunctionUnionsOverlapsAndFallsBackForUnindexedBranches()
    {
        var table = Table();
        table.Publish(new(Pilot, OtherPilot), new(Pilot, OtherPilot, 0, Deadline));
        table.Publish(new(EarlierPilot, Pilot), new(EarlierPilot, Pilot, 0, Deadline));
        table.Publish(new(OtherPilot, OtherPilot + 1), new(OtherPilot, OtherPilot + 1, 0, Deadline));
        Assert.Single(table.Matching(row => row.LowerPilotId == Pilot || row.HigherPilotId == OtherPilot));
        Assert.Single(table.Matching(row => row.LowerPilotId == Pilot || row.HigherPilotId == 0));
        Assert.Single(table.Matching(row => row.LowerPilotId == 0 || row.HigherPilotId == OtherPilot));
        Assert.Empty(table.Matching(row => row.LowerPilotId == 0 || row.HigherPilotId == 0));
        Assert.Equal(3, table.Matching(row => row.LowerPilotId == Pilot || row.ExpiresAt == Deadline).Count());
        Assert.Equal(3, table.Matching(row => row.ExpiresAt == Deadline || row.LowerPilotId == Pilot).Count());
        Assert.Single(table.Matching(row => (row.LowerPilotId == Pilot && row.ExpiresAt > Deadline) || row.HigherPilotId == Pilot));
        Assert.Single(table.Matching(row => row.LowerPilotId == Pilot && (row.HigherPilotId == OtherPilot || row.ExpiresAt > Deadline)));
        long[] pilots = [Pilot, EarlierPilot];
        Assert.Equal(2, table.Matching(row => Enumerable.Contains(pilots, row.LowerPilotId) || row.HigherPilotId == Pilot).Count());
        long selected = Pilot;
        Expression<Func<SavedEngagement, bool>> captured = row => row.LowerPilotId == selected || row.HigherPilotId == selected;
        Assert.Equal(2, table.Matching(captured).Count());
        selected = EarlierPilot;
        Assert.Single(table.Matching(captured));
        long? missing = null;
        Assert.Equal(3, table.Matching(row => missing == null || row.LowerPilotId == missing.Value).Count());
    }

    [Fact]
    public async Task EngagementQueriesSeeLocalChangesRollbackAndReopenedCheckpoints()
    {
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(Engagement(EarlierPilot, Pilot));
            action.Add(Engagement(Pilot, OtherPilot));
            action.Add(Engagement(OtherPilot, OtherPilot + 1));
            action.Commit();
        }
        Assert.Equal(new[] { EarlierPilot, Pilot }, Read());
        using (var measured = SessionPerformance.Begin("indexed engagement changes", _ => { }, force: true)!)
        {
            using (var action = live.Economy.Begin(TransactionParticipants.None))
            {
                action.Remove(action.Set<SavedEngagement>().Find(EarlierPilot, Pilot)!);
                action.Set<SavedEngagement>().Find(Pilot, OtherPilot)!.ExpiresAt = 0;
                action.Add(Engagement(Pilot - 2, Pilot));
                Assert.Equal(new[] { Pilot - 2 }, ReadAction(action));
                Assert.Single(
                    action
                        .Filter<SavedEngagement>(row => row.LowerPilotId == Pilot || row.HigherPilotId == Pilot)
                        .Where(row => row.ExpiresAt > 0)
                        .ToArray()
                );
            }
            Assert.Equal(new[] { EarlierPilot, Pilot }, Read());
            using (var action = live.Economy.Begin(TransactionParticipants.None))
            {
                action.Remove(action.Set<SavedEngagement>().Find(EarlierPilot, Pilot)!);
                action.Set<SavedEngagement>().Find(Pilot, OtherPilot)!.ExpiresAt = 0;
                action.Add(Engagement(Pilot - 2, Pilot));
                action.Commit();
            }
            Assert.Equal(new[] { Pilot - 2 }, Read());
            Assert.Equal(0, measured.Commands);
            Assert.Equal(0, measured.Connections);
            Assert.Equal(0, measured.FittingCalculations);
        }
        await live.FlushAsync();
        using var disk = WorldConnections.FileConnection(fixture.Path);
        using WorldDbContext reopened = new(disk);
        Assert.Null(reopened.Set<SavedEngagement>().Find(EarlierPilot, Pilot));
        Assert.Equal(0, reopened.Set<SavedEngagement>().Find(Pilot, OtherPilot)!.ExpiresAt);
        Assert.Equal(Deadline, reopened.Set<SavedEngagement>().Find(Pilot - 2, Pilot)!.ExpiresAt);

        long[] Read() => live.Economy.Inspect(ReadAction, TransactionParticipants.None);
    }

    private static long[] ReadAction(WorldAction action) =>
        action
            .Filter<SavedEngagement>(row => (row.LowerPilotId == Pilot || row.HigherPilotId == Pilot) && row.ExpiresAt > 0)
            .OrderBy(row => row.LowerPilotId)
            .Select(row => row.LowerPilotId)
            .ToArray();

    private static SavedEngagement Engagement(long lower, long higher) =>
        new()
        {
            LowerPilotId = lower,
            HigherPilotId = higher,
            ExpiresAt = Deadline,
        };

    private static EconomyTable<SavedEngagement, EconomySavedEngagement> Table() =>
        new(
            "WorldEngagement",
            row => new(row.LowerPilotId, row.HigherPilotId),
            row => new(row.LowerPilotId, row.HigherPilotId, row.ActiveUntil, row.ExpiresAt),
            state => new SavedEngagement
            {
                LowerPilotId = state.LowerPilotId,
                HigherPilotId = state.HigherPilotId,
                ActiveUntil = state.ActiveUntil,
                ExpiresAt = state.ExpiresAt,
            },
            (_, _) => throw new NotSupportedException(),
            "LowerPilotId,HigherPilotId,ActiveUntil,ExpiresAt",
            _ => throw new NotSupportedException(),
            state => new(state.LowerPilotId, state.HigherPilotId),
            indexes: [new("LowerPilotId", row => row.LowerPilotId), new("HigherPilotId", row => row.HigherPilotId)],
            primaryColumn: null
        );

    private sealed class CandidateCounter
    {
        internal int Visits { get; set; }

        public bool Visit(long expiresAt)
        {
            Visits++;
            return expiresAt > 0;
        }
    }
}
