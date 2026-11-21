using Eden.Data.World.Economy;
using Eden.Data.World.Industry;
using Eden.Gameplay.Industry;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Industry;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Maps;

public sealed class IndustryMapTests
{
    private const string Starts = "GetIndustryJobsOverLast24Hours";
    private const string Costs = "GetIndustryCostModifier";

    [Fact]
    public async Task InstalledJobCountsOnceAfterCommitAndSurvivesCancellationAndRestartUntilExpiry()
    {
        using IndustryTests.Fixture fixture = new();
        var pilot = fixture.Pilot;
        var request = fixture.Request(runs: 2);
        WorldDatabase database = new(fixture.Db.Path);
        var map = fixture.Db.Services.GetRequiredService<IIndustryMap>();
        using (LiveWorld live = new(database, fixture.Clock))
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            using (var aborted = live.Economy.Begin(new(pilots: [pilot.PilotId])))
            {
                fixture.Store.InstallManufacturing(aborted, catalog, pilot.PilotId, request);
                Assert.Empty(map.JobsStarted(null));
            }
            Assert.Empty(map.JobsStarted(null));

            var job = fixture.Store.Install(pilot.PilotId, request).Job;
            Assert.Equal(2, job.Runs);
            Assert.Equal(new IndustryMapJobCount(pilot.SystemId, 1), Assert.Single(map.JobsStarted(null)));
            Assert.Empty(map.JobsStarted(IndustryActivity.Copying));
            fixture.Store.Cancel(pilot.PilotId, job.Id, pilot.SystemId);
            using (var metrics = SessionPerformance.Begin("warm industry map", _ => { }, force: true)!)
            {
                var counts = map.JobsStarted(IndustryActivity.Manufacturing);
                Assert.Equal(new IndustryMapJobCount(pilot.SystemId, 1), Assert.Single(counts));
                Assert.Same(counts, map.JobsStarted(IndustryActivity.Manufacturing));
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            await live.FlushAsync();
        }

        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(1) - TimeSpan.FromMilliseconds(1));
        using var reopened = ServerServices.Build(database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<IIndustryMap>();
        Assert.Equal(new IndustryMapJobCount(pilot.SystemId, 1), Assert.Single(restored.JobsStarted(null)));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(1));
        Assert.Empty(restored.JobsStarted(null));
        Assert.Empty(restored.JobsStarted(IndustryActivity.Manufacturing));
    }

    [Fact]
    public void NativeIndustryFiltersUseTypedActivitySelectorsAndSavedCostIndices()
    {
        using IndustryTests.Fixture fixture = new();
        var pilot = fixture.Pilot;
        WorldDatabase database = new(fixture.Db.Path);
        const double systemOverride = 0.075;
        const double defaultIndex = 0.04;
        using (WorldDbContext saved = new(database.Path))
        {
            saved.Add(new SavedManufacturingIndex { SystemId = pilot.SystemId, Index = (decimal)systemOverride });
            saved.SaveChanges();
        }
        using var services = ServerServices.Build(database, fixture.Clock, liveWorld: true);
        using var scope = services.CreateScope();
        var live = services.GetRequiredService<LiveWorld>();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var store = services.GetRequiredService<Eden.World.Docking.SqliteWorld>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(store, p, fixture.Clock, new(), []),
            42,
            SignedSettings.Empty
        );
        Assert.Same(PilotCallResult.Unsupported, Call(Starts, [null]));
        context.Select(pilot);
        Assert.Empty(Jobs([null]));
        Assert.All(Indices(IndustryActivity.Manufacturing).Values, value => Assert.Equal(0d, value));

        using (var install = live.Economy.Begin(new(pilots: [pilot.PilotId])))
        {
            foreach (var activity in IndustryDefinitions.Activities)
            {
                int count = (int)activity;
                for (int i = 0; i < count; i++)
                {
                    AddJob(install, activity, pilot.StationId, fixture.Clock.Now);
                }
            }
            // One expired start, one future start, and one job without a known-space facility.
            AddJob(install, IndustryActivity.Copying, pilot.StationId, fixture.Clock.Now - TimeSpan.TicksPerDay);
            AddJob(install, IndustryActivity.Copying, pilot.StationId, fixture.Clock.Now + TimeSpan.TicksPerSecond);
            AddJob(install, IndustryActivity.Copying, long.MaxValue, fixture.Clock.Now);
            var policy = install.Set<SavedEconomicPolicy>().Single();
            policy.ChargeServiceCosts = true;
            policy.DefaultManufacturingIndex = (decimal)defaultIndex;
            install.Commit();
        }

        int total = IndustryDefinitions.Activities.Sum(activity => (int)activity);
        AssertJobs(Jobs([null]), total);
        AssertJobs(Jobs([0L]), total);
        foreach (var activity in IndustryDefinitions.Activities)
        {
            AssertJobs(Jobs([(long)activity]), (int)activity);
            var indices = Indices(activity);
            Assert.Equal(systemOverride, indices[pilot.SystemId]);
            Assert.All(indices.Where(system => system.Key != pilot.SystemId), system => Assert.Equal(defaultIndex, system.Value));
            Assert.Contains(indices.Keys, system => system != pilot.SystemId);
        }
        Assert.Equal(systemOverride, fixture.Store.Facility(pilot.PilotId, pilot.StationId).CostIndex);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        AssertJobs(Jobs([(long)IndustryActivity.Copying]), (int)IndustryActivity.Copying + 1);
        AssertJobs(Jobs([null]), total + 1);
        Assert.IsType<PilotCallResult.Failure>(Call(Costs, [null]));
        Assert.IsType<PilotCallResult.Failure>(Call(Costs, [0L]));
        Assert.IsType<PilotCallResult.Failure>(Call(Starts, [6L]));
        Assert.IsType<PilotCallResult.Failure>(Call(Starts, ["1"]));
        Assert.IsType<PilotCallResult.Failure>(Call(Starts, []));
        Assert.IsType<PilotCallResult.Failure>(Call(Starts, [1L, 1L]));
        Assert.IsType<PilotCallResult.Failure>(Call(Costs, [1L], new() { ["unsupported"] = true }));

        PilotCallResult Call(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            router.Call(context, "map", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        object? Reply(string method, object?[] args) =>
            BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(Call(method, args)).Reply));

        Dictionary<long, object?> Indices(IndustryActivity activity) =>
            Assert.IsType<Dictionary<long, object?>>(Reply(Costs, [(long)activity]));

        List<object?> Jobs(object?[] args)
        {
            var rowset = Assert.IsType<BlueInstance>(Reply(Starts, args));
            Assert.Equal("eve.common.script.sys.rowset.Rowset", rowset.Name);
            var fields = BlueRead.Fields(rowset.State);
            Assert.Equal(new[] { "solarSystemID", "noOfJobs" }, Assert.IsType<List<object?>>(fields["header"]).Select(BlueMarshal.Text));
            return Assert.IsType<List<object?>>(fields["lines"]);
        }

        void AssertJobs(List<object?> rows, int count) =>
            Assert.Equal(new object?[] { (long)pilot.SystemId, (long)count }, Assert.IsType<object?[]>(Assert.Single(rows)));

        void AddJob(WorldAction world, IndustryActivity activity, long facility, long start) =>
            world.Add(
                new IndustryJob
                {
                    PilotId = pilot.PilotId,
                    Activity = activity,
                    StationId = facility,
                    Runs = 1,
                    OutputQuantity = 1,
                    StartTime = start,
                    EndTime = start + TimeSpan.TicksPerSecond,
                    DurationSeconds = 1,
                    Status = IndustryJobStatus.Delivered,
                }
            );
    }
}
