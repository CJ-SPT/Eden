using System.Text.Json;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Maps;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Capsuleers;
using Eden.Protocol.Tests.Persistence;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Maps;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Maps;

public sealed class MapNavigationTests
{
    [Fact]
    public void StructureMapCacheUsesCurrentFileTimeOnEveryRefresh()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld world = new(clock: clock, liveWorld: true);
        using var scope = world.Services.CreateScope();
        var pilot = world.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(world.Store, p, clock, new(), world.Objects),
            42,
            SignedSettings.Empty
        );
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.Same(PilotCallResult.Unsupported, Call([30000142L]));
        context.Select(pilot);

        var first = Assert.IsType<PilotCallResult.Success>(Call([30000142L]));
        AssertStructureMap(BlueMarshal.Decode(BlueMarshal.Encode(first.Reply)), clock.Now);
        clock.AdvanceWallTime(TimeSpan.FromMinutes(5));
        using (var metrics = SessionPerformance.Begin("structure-map-cache-refresh", _ => { }, force: true)!)
        {
            long firstVersion = clock.Now - TimeSpan.FromMinutes(5).Ticks;
            var refreshed = Assert.IsType<PilotCallResult.Success>(
                Call([30000142L], new() { ["machoVersion"] = new object?[] { firstVersion, 0L } })
            );
            AssertStructureMap(BlueMarshal.Decode(BlueMarshal.Encode(refreshed.Reply)), clock.Now);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }

        Assert.IsType<PilotCallResult.Failure>(Call([]));
        Assert.IsType<PilotCallResult.Failure>(Call([0L]));
        Assert.IsType<PilotCallResult.Failure>(Call([long.MaxValue]));
        Assert.Equal(pilot, world.Store.FindPilot(1000001));

        PilotCallResult Call(object?[] args, Dictionary<string, object?>? keywords = null) =>
            router.Call(context, "structureDirectory", "GetStructureMapData", args, _ => { }, keywords);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoryHasStableCacheVersionAndNativeRowsWithoutChangingPilot(bool inSpace)
    {
        using TestWorld world = new();
        using var scope = world.Services.CreateScope();
        var saved = world.Store.FindPilot(1000001)!;
        var pilot = inSpace ? saved with { Position = new SpacePosition(1_000_000, 0, 0) } : saved;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(world.Store, p, TimeProvider.System, new(), world.Objects),
            42,
            SignedSettings.Empty
        );
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Assert.Same(PilotCallResult.Unsupported, Call([3L, 24L]));
        context.Select(pilot);
        byte[]? baseline = null;
        foreach (long statistic in new long[] { 1, 3, 5 })
        {
            foreach (long hours in new long[] { 1, 24 })
            {
                for (int cycle = 0; cycle < 2; cycle++)
                {
                    var reply = Assert.IsType<PilotCallResult.Success>(Call([statistic, hours], new() { ["machoVersion"] = 1L }));
                    byte[] bytes = BlueMarshal.Encode(reply.Reply);
                    AssertHistory(BlueMarshal.Decode(bytes));
                    baseline ??= bytes;
                    Assert.Equal(baseline, bytes);
                }
            }
        }
        foreach (
            object?[] invalid in new object?[][]
            {
                [],
                [3L],
                [3L, 24L, 1L],
                [null, 24L],
                [true, 24L],
                ["3", 24L],
                [0L, 24L],
                [2L, 24L],
                [long.MaxValue, 24L],
                [long.MinValue, 24L],
                [3L, 0L],
                [3L, -1L],
                [3L, long.MaxValue],
                [3L, true],
                [3L, null],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(Call(invalid));
        }
        Assert.IsType<PilotCallResult.Failure>(Call([3L, 24L], new() { ["characterID"] = 90000002L }));
        Assert.Same(PilotCallResult.Unsupported, router.Call(context, "map", "UnknownMapMethod", Array.Empty<object?>(), _ => { }));
        Assert.Equal(pilot, context.Pilot);
        Assert.Equal(saved, world.Store.FindPilot(1000001));
        Assert.Empty(context.Notifications);
        Assert.Empty(context.NotificationsBeforeFlight);

        PilotCallResult Call(object?[] args, Dictionary<string, object?>? keywords = null) =>
            router.Call(context, "map", "GetHistory", BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeMapSearchAndRoutePreparationPacketsHaveResolvableResults(bool inSpace)
    {
        var clock = CalendarTestData.Clock();
        using TestWorld world = new(clock: clock);
        if (inSpace)
        {
            world.Store.SetPosition(world.Store.FindPilot(1000001)!.ShipId, new SpacePosition(1_000_000, 0, 0));
        }
        using var resident = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var before = world.Store.FindPilot(1000001)!;
        object?[] requests =
        [
            PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [90000001L]),
            PilotServicesTests.Request("map", "GetStationInfo", []),
            PilotServicesTests.Request("structureDirectory", "GetJumpBridgesWithMyAccess", []),
            PilotServicesTests.Request("charMgr", "GetHomeStationRow", []),
            PilotServicesTests.Request("map", "GetAllRoamingWeatherSystems", []),
            PilotServicesTests.Request("map", "GetHistory", [3L, 24L]),
            PilotServicesTests.Request("map", "GetHistory", [3L, 24L]),
            PilotServicesTests.Request("search", "Query", ["Jita", new List<object?> { 6L, 7L, 8L, 9L }]),
            PilotServicesTests.Request("structureDirectory", "GetStructureMapData", [30000142L]),
            PilotServicesTests.Request("stationSvc", "GetStation", [60003760L]),
            PilotServicesTests.Request("machoNet", "GetClusterGameStatisticsForClient", ClusterArguments()),
            PilotServicesTests.Request("map", "GetIndustryJobsOverLast24Hours", [null]),
            PilotServicesTests.Request("map", "GetIndustryCostModifier", [(long)Eden.Gameplay.Industry.IndustryActivity.Reaction]),
        ];
        Queue<object?> input = new(requests.Select(packet => BlueMarshal.Decode(BlueMarshal.Encode(packet))));
        List<BlueInstance> output = [];
        using var scope = resident.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            session.RunAsync(
                () => input.TryDequeue(out var packet) ? Task.FromResult(packet) : throw new EndOfStreamException(),
                packet =>
                {
                    output.Add(Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(packet))));
                    return Task.CompletedTask;
                },
                _ => { },
                1000001,
                42,
                SignedSettings.Empty,
                CancellationToken.None
            )
        );
        Assert.DoesNotContain(output, packet => packet.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        var replies = output
            .Where(packet => packet.Name.EndsWith(".CallRsp", StringComparison.Ordinal))
            .Select(packet =>
            {
                var body = BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 1);
                return BlueMarshal.Decode(BlueRead.Bytes(body[0]));
            })
            .ToArray();
        Assert.Equal(requests.Length, replies.Length);
        var stations = BlueRead.Fields(Assert.IsType<BlueInstance>(replies[1]).State);
        Assert.Contains("solarSystemID", Assert.IsType<List<object?>>(stations["header"]).Select(BlueMarshal.Text));
        Assert.NotEmpty(Assert.IsType<List<object?>>(stations["lines"]));
        AssertHistory(replies[5]);
        AssertHistory(replies[6]);
        Assert.Equal(BlueMarshal.Encode(replies[5]), BlueMarshal.Encode(replies[6]));
        var groups = Assert.IsType<Dictionary<long, object?>>(replies[7]);
        Assert.Equal(new List<object?> { 30000142L }, groups[7]);
        Assert.Contains(60003760L, Assert.IsType<List<object?>>(groups[9]));
        AssertStructureMap(replies[8], clock.Now);
        var station = BlueRead.Fields(Assert.IsType<BlueInstance>(replies[9]).State);
        Assert.Equal(60003760L, station["stationID"]);
        Assert.Equal(30000142L, station["solarSystemID"]);
        var population = BlueRead.Tuple(replies[10], 3);
        Assert.Equal(1L, population[2]);
        var jobs = BlueRead.Fields(Assert.IsType<BlueInstance>(replies[11]).State);
        Assert.Empty(Assert.IsType<List<object?>>(jobs["lines"]));
        Assert.Contains(30000142L, Assert.IsType<Dictionary<long, object?>>(replies[12]).Keys);
        Assert.Equal(before, world.Store.FindPilot(1000001));
    }

    [Fact]
    public async Task CheckpointBatchesMapActivityAndRestoresLegacyHistoryWithoutDuplicates()
    {
        const int system = 30000142;
        const int newJumps = 10_000;
        var clock = CalendarTestData.Clock();
        using TestWorld fixture = new(clock: clock);
        long cutoff = clock.Now - TimeSpan.TicksPerDay;
        using (WorldDbContext legacy = new(fixture.Path))
        {
            legacy.Database.ExecuteSqlRaw("DROP TABLE WorldMapActivity; PRAGMA user_version=220;");
            var payload = new MapStatisticsPayload(
                [new(cutoff, system, MapActivityKind.Jump), new(cutoff + 1, system, MapActivityKind.Jump)],
                []
            );
            string json = JsonSerializer.Serialize(payload);
            legacy.Database.ExecuteSql($"INSERT INTO WorldMapStatistics(Id,Payload) VALUES(1,{json})");
        }

        WorldDatabase database = new(fixture.Path);
        WorldInitialization.Initialize(database);
        using (var host = ServerServices.Build(database, clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            var statistics = host.GetRequiredService<IMapStatistics>();
            Assert.Equal(1, Assert.Single(statistics.History(MapHistoryStatistic.Jumps, 24).Rows).Counts.Jumps);
            using (var action = live.Economy.Begin())
            {
                for (int index = 0; index < newJumps; index++)
                {
                    MapStatisticsStore.Record(action, system, clock.Now, MapActivityKind.Jump);
                }
                action.Commit();
            }
            await live.FlushAsync();
            using (var action = live.Economy.Begin())
            {
                MapStatisticsStore.Record(action, system, clock.Now, MapActivityKind.NpcShipLoss);
                action.Commit();
            }
            await live.FlushAsync();
        }

        using (WorldDbContext checkpoint = new(fixture.Path))
        {
            int[] batchSizes = checkpoint.Database.SqlQueryRaw<int>("SELECT length(Payload) AS Value FROM WorldMapActivity").ToArray();
            Assert.True(batchSizes.Length > 1);
            Assert.All(batchSizes, size => Assert.InRange(size, 1, 64 * 1024));
        }
        using var reopened = ServerServices.Build(new(fixture.Path), clock, liveWorld: true);
        var restored = reopened.GetRequiredService<IMapStatistics>();
        Assert.Equal(newJumps + 1, Assert.Single(restored.History(MapHistoryStatistic.Jumps, 24).Rows).Counts.Jumps);
        Assert.Equal(newJumps, Assert.Single(restored.History(MapHistoryStatistic.Jumps, 1).Rows).Counts.Jumps);
        Assert.Equal(1, Assert.Single(restored.History(MapHistoryStatistic.Kills, 24).Rows).Counts.NpcShips);
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Equal(newJumps, Assert.Single(restored.History(MapHistoryStatistic.Jumps, 24).Rows).Counts.Jumps);
    }

    [Fact]
    public async Task GateHistoryPublishesCommittedArrivalsAndExpiresAfterCheckpointReopen()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld world = new(clock: clock, liveWorld: true);
        using var scope = world.Services.CreateScope();
        var pilot = world.Store.FindPilot(1000001)!;
        var gate = world.Objects.Single(body => body.DestinationGateId.HasValue);
        world.Store.SetPosition(pilot.ShipId, gate.Position);
        var statistics = world.Services.GetRequiredService<IMapStatistics>();
        var live = world.Services.GetRequiredService<LiveWorld>();
        using (CatalogDbContext catalog = new(new Eden.World.Storage.WorldDatabase(world.Path).CatalogPath))
        using (var action = live.Economy.Begin(new(systems: [gate.DestinationSystemId!.Value], pilots: [pilot.PilotId])))
        {
            PilotGateTravelStore.TransferDetailed(
                action,
                catalog,
                pilot.PilotId,
                pilot.ShipId,
                gate.Id,
                gate.DestinationGateId!.Value,
                clock.Now
            );
            Assert.Empty(statistics.History(MapHistoryStatistic.Jumps, 1).Rows);
        }
        Assert.Empty(statistics.History(MapHistoryStatistic.Jumps, 1).Rows);
        var travel = world.Services.GetRequiredService<IPilotGateTravel>();
        travel.Transfer(pilot.PilotId, pilot.ShipId, gate.Id, gate.DestinationGateId!.Value);
        Assert.Throws<InvalidDataException>(() => travel.Transfer(pilot.PilotId, pilot.ShipId, gate.Id, gate.DestinationGateId.Value));
        Assert.Equal(1, Assert.Single(statistics.History(MapHistoryStatistic.Jumps, 1).Rows).Counts.Jumps);
        var arrived = world.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            arrived.Account,
            [arrived],
            p => new(world.Store, p, clock, new(), []),
            42,
            SignedSettings.Empty
        );
        context.Select(arrived);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using (var metrics = SessionPerformance.Begin("map jump history", _ => { }, force: true)!)
        {
            var reply = Assert.IsType<PilotCallResult.Success>(
                router.Call(context, "map", "GetHistory", new object?[] { 1L, 1L }, _ => { })
            );
            var lines = ReadHistory(BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply))).Lines;
            Assert.Equal(new object?[] { (long)arrived.SystemId, 1L, 0L, 0L }, Assert.IsType<object?[]>(Assert.Single(lines)));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        await live.FlushAsync();
        scope.Dispose();
        world.Services.Dispose();
        clock.AdvanceWallTime(TimeSpan.FromHours(1) - TimeSpan.FromMilliseconds(1));
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var restored = reopened.GetRequiredService<IMapStatistics>();
        Assert.Equal(arrived.SystemId, Assert.Single(restored.History(MapHistoryStatistic.Jumps, 1).Rows).SystemId);
        clock.AdvanceWallTime(TimeSpan.FromMilliseconds(1));
        Assert.Empty(restored.History(MapHistoryStatistic.Jumps, 1).Rows);
        Assert.Single(restored.History(MapHistoryStatistic.Jumps, 24).Rows);
        clock.AdvanceWallTime(TimeSpan.FromHours(23));
        Assert.Empty(restored.History(MapHistoryStatistic.Jumps, 24).Rows);
    }

    [Fact]
    public async Task PersonalVisitsFollowSelectedPilotArrivalsAndSurviveCheckpointReopen()
    {
        const long Account = 1000001;
        var clock = CalendarTestData.Clock();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var pilot = world.Store.FindPilot(Account)!;
        var gate = world.Objects.Single(body => body.DestinationGateId.HasValue);
        var statistics = world.Services.GetRequiredService<IMapStatistics>();
        Assert.Empty(statistics.SystemVisits(pilot.PilotId));
        long firstArrival = clock.Now;
        long returnAt;
        using (var scope = world.Services.CreateScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
            using PilotCallContext context = new(Account, pilot.Account, [pilot], factory.Create, 42, SignedSettings.Empty);
            var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            Assert.Same(PilotCallResult.Unsupported, Call([]));
            context.Select(pilot);
            Assert.Equal(new MapSystemVisit(pilot.SystemId, 1, firstArrival), Assert.Single(statistics.SystemVisits(pilot.PilotId)));

            world.Store.SetPosition(pilot.ShipId, gate.Position);
            var travel = scope.ServiceProvider.GetRequiredService<IPilotGateTravel>();
            Assert.Throws<InvalidDataException>(() => travel.Transfer(pilot.PilotId, pilot.ShipId, gate.Id, gate.Id));
            Assert.Single(statistics.SystemVisits(pilot.PilotId));

            clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
            var arrived = travel.Transfer(pilot.PilotId, pilot.ShipId, gate.Id, gate.DestinationGateId!.Value);
            var systems = scope.ServiceProvider.GetRequiredService<ISystemObjects>();
            var destinationObjects = systems.Load(arrived.SystemId);
            context.Flight.Relocate(arrived, destinationObjects);
            long visitedAt = clock.Now;
            var returnGate = destinationObjects.Single(body => body.Id == gate.DestinationGateId);
            world.Store.SetPosition(pilot.ShipId, returnGate.Position);
            clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
            var returned = travel.Transfer(pilot.PilotId, pilot.ShipId, returnGate.Id, gate.Id);
            context.Flight.Relocate(returned, world.Objects);
            returnAt = clock.Now;

            Assert.IsType<PilotCallResult.Failure>(Call([pilot.PilotId]));
            Assert.IsType<PilotCallResult.Failure>(Call([], new() { ["characterID"] = CalendarTestData.Guest }));
            using var metrics = SessionPerformance.Begin("personal map visits", _ => { }, force: true)!;
            var reply = Assert.IsType<PilotCallResult.Success>(Call([]));
            var rows = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply)));
            Assert.Equal("eve.common.script.sys.rowset.Rowset", rows.Name);
            var fields = BlueRead.Fields(rows.State);
            Assert.Equal(
                new[] { "lastDateTime", "solarSystemID", "visits" },
                Assert.IsType<List<object?>>(fields["header"]).Select(BlueMarshal.Text)
            );
            var lines = Assert.IsType<List<object?>>(fields["lines"]);
            Assert.Equal(new object?[] { returnAt, (long)pilot.SystemId, 2L }, BlueRead.Tuple(lines[0], 3));
            Assert.Equal(new object?[] { visitedAt, (long)arrived.SystemId, 1L }, BlueRead.Tuple(lines[1], 3));
            Assert.Equal(2, lines.Count);
            Assert.Empty(statistics.SystemVisits(CalendarTestData.Guest));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);

            PilotCallResult Call(object?[] args, Dictionary<string, object?>? keywords = null) =>
                router.Call(context, "map", "GetSolarSystemVisits", args, _ => { }, keywords);
        }
        await world.Services.GetRequiredService<LiveWorld>().FlushAsync();
        world.Services.Dispose();
        using var reopened = ServerServices.Build(new(world.Path), clock, liveWorld: true);
        var restored = reopened.GetRequiredService<IMapStatistics>();
        Assert.Equal(new MapSystemVisit(pilot.SystemId, 2, returnAt), restored.SystemVisits(pilot.PilotId)[0]);
        clock.AdvanceWallTime(TimeSpan.FromDays(2));
        using var selected = reopened.GetRequiredService<IHumanPresence>().Select(pilot.PilotId, pilot.SystemId);
        Assert.Equal(new MapSystemVisit(pilot.SystemId, 2, clock.Now), restored.SystemVisits(pilot.PilotId)[0]);
        selected.Transfer(AbyssalRules.FirstSystem);
        Assert.Equal(2, restored.SystemVisits(pilot.PilotId).Count);
        selected.Transfer(pilot.SystemId);
        Assert.Equal(new MapSystemVisit(pilot.SystemId, 3, clock.Now), restored.SystemVisits(pilot.PilotId)[0]);
        selected.Dispose();
        var deletion = reopened.GetRequiredService<ICharacterDeletion>();
        long deletionReady = deletion.Prepare(Account, pilot.PilotId);
        clock.AdvanceWallTime(TimeSpan.FromTicks(deletionReady - clock.Now));
        deletion.Delete(Account, pilot.PilotId);
        Assert.Empty(restored.SystemVisits(pilot.PilotId));
    }

    [Fact]
    public async Task PopulationAveragesSelectedPlayersAndOffscreenBotsAcrossSamplesAndRestart()
    {
        const int Perimeter = 30000144;
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        var founder = fixture.Founder();
        var clock = fixture.Clock;
        using (var host = ServerServices.Build(fixture.Database, clock, liveWorld: true))
        {
            var ships = host.GetRequiredService<SqliteWorld>();
            var player = ships.FindPilot(1000001)!;
            int homeSystem = player.HomeSystemId;
            ships.SetPosition(founder.FounderShipId, SpacePosition.Zero, Perimeter);
            ships.SetPosition(player.ShipId, SpacePosition.Zero, Perimeter);
            var presence = host.GetRequiredService<IHumanPresence>();
            using var selected = presence.Select(player.PilotId, Perimeter);
            using var duplicate = presence.Select(player.PilotId, Perimeter);
            var live = host.GetRequiredService<LiveWorld>();
            var statistics = host.GetRequiredService<IMapStatistics>();
            using (var metrics = SessionPerformance.Begin("map population sample", _ => { }, force: true)!)
            {
                live.SampleMapPilots();
                Assert.Equal(new(2, 0), statistics.Population().Systems[Perimeter]);
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            clock.Seconds += 60;
            ships.SetPosition(player.ShipId, null);
            selected.Transfer(homeSystem);
            duplicate.Dispose();
            live.SampleMapPilots();
            var counts = statistics.Population();
            Assert.Equal(2, counts.Samples);
            Assert.Equal(new(3, 0), counts.Systems[Perimeter]);
            Assert.True(counts.Systems[homeSystem].Docked > 0);

            using var scope = host.CreateScope();
            player = ships.FindPilot(1000001)!;
            using PilotCallContext context = new(
                1000001,
                player.Account,
                [player],
                p => new(ships, p, clock, new(), []),
                42,
                SignedSettings.Empty
            );
            var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            Assert.Same(PilotCallResult.Unsupported, Call(ClusterArguments()));
            context.Select(player);
            Assert.IsType<PilotCallResult.Failure>(Call([]));
            var reply = Assert.IsType<PilotCallResult.Success>(Call(ClusterArguments()));
            var state = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply)), 3);
            Assert.Equal(2L, state[2]);
            var totals = Assert.IsType<Dictionary<long, object?>>(state[0]);
            var docked = Assert.IsType<Dictionary<long, object?>>(state[1]);
            Assert.Equal(3L, totals[Perimeter - 30_000_000L]);
            Assert.False(docked.ContainsKey(Perimeter - 30_000_000L));
            Assert.Equal((long)counts.Systems[homeSystem].Docked, docked[homeSystem - 30_000_000L]);
            await live.FlushAsync();

            PilotCallResult Call(object?[] args) =>
                router.Call(
                    context,
                    "machoNet",
                    "GetClusterGameStatisticsForClient",
                    BlueMarshal.Decode(BlueMarshal.Encode(args)),
                    _ => { }
                );
        }
        using var reopened = ServerServices.Build(fixture.Database, clock, liveWorld: true);
        var restored = reopened.GetRequiredService<IMapStatistics>();
        Assert.Equal(new(3, 0), restored.Population().Systems[Perimeter]);
        clock.Seconds += 29 * 60;
        Assert.Equal(new(1, 0), restored.Population().Systems[Perimeter]);
        clock.Seconds += 60;
        Assert.Empty(restored.Population().Systems);
    }

    private static object?[] ClusterArguments() => ["EVE", new object?[] { BlueEmpty.Dictionary, BlueEmpty.Dictionary, 0L }];

    [Fact]
    public void KillHistoryUsesNativeShipNpcAndPodFieldsForBothWindows()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld world = new(clock: clock, liveWorld: true);
        using var scope = world.Services.CreateScope();
        var pilot = world.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(world.Store, p, clock, new(), []),
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var live = world.Services.GetRequiredService<LiveWorld>();
        using (var action = live.Economy.Begin(new(pilots: [pilot.PilotId])))
        {
            MapStatisticsStore.Record(action, pilot.SystemId, clock.Now, MapActivityKind.ShipLoss);
            MapStatisticsStore.Record(action, pilot.SystemId, clock.Now, MapActivityKind.NpcShipLoss);
            MapStatisticsStore.Record(action, pilot.SystemId, clock.Now, MapActivityKind.CapsuleLoss);
            MapStatisticsStore.Record(action, 31_000_005, clock.Now, MapActivityKind.CapsuleLoss);
            action.Commit();
        }
        var (version, lines) = ReadHistory(Reply(1));
        Assert.Equal(new object?[] { (long)pilot.SystemId, 2L, 1L, 1L }, Assert.IsType<object?[]>(Assert.Single(lines)));
        clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
        Assert.Equal(version, ReadHistory(Reply(1)).Version);
        Assert.Equal(lines, ReadHistory(Reply(24)).Lines);
        clock.AdvanceWallTime(TimeSpan.FromMinutes(59));
        var expired = ReadHistory(Reply(1));
        Assert.Empty(expired.Lines);
        Assert.NotEqual(version[1], expired.Version[1]);
        Assert.Single(ReadHistory(Reply(24)).Lines);

        object? Reply(long hours)
        {
            var reply = Assert.IsType<PilotCallResult.Success>(
                router.Call(context, "map", "GetHistory", new object?[] { 3L, hours }, _ => { })
            );
            return BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply));
        }
    }

    private static void AssertStructureMap(object? reply, long timestamp)
    {
        var cached = Assert.IsType<BlueInstance>(reply);
        Assert.Equal("carbon.common.script.net.objectCaching.CachedMethodCallResult", cached.Name);
        var state = BlueRead.Tuple(cached.State, 3);
        var details = BlueRead.Fields(state[0]);
        Assert.Equal("5 minutes", BlueMarshal.Text(details["versionCheck"]));
        Assert.Equal("charid", BlueMarshal.Text(details["sessionInfo"]));
        Assert.Equal(new object?[] { timestamp, 0L }, BlueRead.Tuple(state[2], 2));
        Assert.Empty(Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueRead.Bytes(state[1]))));
    }

    private static void AssertHistory(object? reply)
    {
        var (version, lines) = ReadHistory(reply);
        Assert.Equal(1L, version[0]);
        Assert.Empty(lines);
    }

    private static (object?[] Version, List<object?> Lines) ReadHistory(object? reply)
    {
        var cached = Assert.IsType<BlueInstance>(reply);
        Assert.Equal("carbon.common.script.net.objectCaching.CachedMethodCallResult", cached.Name);
        var state = BlueRead.Tuple(cached.State, 3);
        var details = BlueRead.Fields(state[0]);
        Assert.Equal("1 minute", BlueMarshal.Text(details["versionCheck"]));
        Assert.False(details.ContainsKey("sessionInfo"));
        var version = BlueRead.Tuple(state[2], 2);
        Assert.Equal((long)CachedMethodPayload.Checksum(BlueRead.Bytes(state[1])), version[1]);
        var rows = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueRead.Bytes(state[1])));
        Assert.Equal("eve.common.script.sys.rowset.Rowset", rows.Name);
        var fields = BlueRead.Fields(rows.State);
        Assert.Equal(
            new[] { "solarSystemID", "value1", "value2", "value3" },
            Assert.IsType<List<object?>>(fields["header"]).Select(BlueMarshal.Text)
        );
        return (version, Assert.IsType<List<object?>>(fields["lines"]));
    }
}
