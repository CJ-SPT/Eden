using System.Diagnostics;
using Eden.Data.Catalog;
using Eden.Data.World.Market;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationShowInfoTests(ITestOutputHelper output)
{
    private const long Viewer = 90000002;
    private const int NpcCorporation = 1000044;
    private const int SystemId = 30000142;
    private const int StationId = 60003760;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeStartupAndAllTabReadsReturnExpectedShapesWithoutUnsupportedCalls(bool playerCorporation)
    {
        using TestWorld db = new(liveWorld: true);
        int corporation = CorporationPublicInfoTests.CreateCorporation(db, playerCorporation);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                UPDATE Station SET OwnerId=1000044 WHERE Id=60003760;
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                    SELECT 60003761,TypeId,1000044,SolarSystemId,OperationId,OrbitId,X,Y,Z FROM Station WHERE Id=60003760;
                """
            );
        }
        using var scope = db.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IPilotSession>();
        Queue<object?> requests = new([
            PilotServicesTests.Request("charUnboundMgr", "SelectCharacterID", [Viewer]),
            PilotServicesTests.Request("corpmgr", "GetPublicInfo", [(long)corporation]),
            PilotServicesTests.Request("config", "GetStationSolarSystemsByOwner", [(long)corporation]),
            PilotServicesTests.Request("corporationSvc", "GetCorpInfo", [(long)corporation]),
            PilotServicesTests.Request("facWarMgr", "GetCorporationWarFactionID", [(long)corporation]),
            PilotServicesTests.Request("corpmgr", "GetAggressionSettings", [(long)corporation]),
            PilotServicesTests.Request("fwCharacterEnlistmentMgr", "GetCorpAllowedEnlistmentFactions", [(long)corporation]),
            PilotServicesTests.Request("corpRegistry", "GetEveOwners", []),
            PilotServicesTests.Request("agentMgr", "GetAgents", []),
            PilotServicesTests.Request("standingMgr", "GetCharStandings", []),
            PilotServicesTests.Request("standingMgr", "GetCorpStandings", []),
            PilotServicesTests.Request("standingMgr", "GetNPCNPCStandings", []),
            PilotServicesTests.Request("corpRegistry", "GetCorporateContacts", []),
            PilotServicesTests.Request("charMgr", "GetContactList", []),
            PilotServicesTests.Request("allianceRegistry", "GetEmploymentRecord", [(long)corporation]),
            PilotServicesTests.Request("warsInfoMgr", "GetWarsByOwnerID", [(long)corporation]),
            PilotServicesTests.Request("warsInfoMgr", "GetWarsByOwners", [OwnerSet(corporation, NpcCorporation)]),
        ]);
        List<BlueInstance> packets = [];
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            session.RunAsync(
                () => requests.TryDequeue(out var next) ? Task.FromResult(next) : throw new EndOfStreamException(),
                packet =>
                {
                    packets.Add(Assert.IsType<BlueInstance>(packet));
                    return Task.CompletedTask;
                },
                _ => { },
                1000002,
                42,
                SignedSettings.Empty,
                CancellationToken.None
            )
        );
        Assert.DoesNotContain(packets, p => p.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
        var replies = packets.Where(p => p.Name.EndsWith(".CallRsp", StringComparison.Ordinal)).Select(Decode).ToArray();
        Assert.Equal(17, replies.Length);
        var info = Fields(replies[1]);
        Assert.Equal(playerCorporation ? 90000001L : 3004181L, info["creatorID"]);
        Assert.Equal(playerCorporation ? 0.05 : 0, info["loyaltyPointTaxRate"]);
        var systems = Rows(replies[2]);
        Assert.Equal(playerCorporation ? 0 : 1, systems.Count);
        if (!playerCorporation)
        {
            Assert.Equal(new object?[] { (long)SystemId }, Assert.Single(systems));
        }
        Assert.Empty(Rows(replies[3]));
        Assert.Null(replies[4]);
        Assert.Equal("crimewatch.corp_aggression.settings.AggressionSettings", Assert.IsType<BlueInstance>(replies[5]).Name);
        Assert.Empty(Assert.IsType<List<object?>>(replies[6]));
        Assert.Equal("eve.common.script.sys.rowset.Rowset", Assert.IsType<BlueInstance>(replies[7]).Name);
        Assert.Equal("eve.common.script.sys.rowset.Rowset", Assert.IsType<BlueInstance>(replies[8]).Name);
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(replies[9]));
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(replies[10]));
        Assert.Empty(Rows(replies[11]));
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(replies[12]));
        Assert.Contains("addresses", Fields(replies[13]).Keys);
        var history = Assert.IsType<List<object?>>(replies[14]);
        Assert.Equal(playerCorporation ? 1 : 0, history.Count);
        if (playerCorporation)
        {
            var founded = Fields(Assert.Single(history));
            Assert.Null(founded["allianceID"]);
            Assert.Equal(db.Services.GetRequiredService<IPlayerCorporations>().Read(corporation)!.CreatedAt, founded["startDate"]);
        }
        Assert.Empty(Assert.IsType<List<object?>>(replies[15]));
        var wars = Assert.IsType<Dictionary<long, object?>>(replies[16]);
        Assert.Equal(playerCorporation ? 2 : 1, wars.Count);
        Assert.All(wars.Values, value => Assert.Empty(Assert.IsType<List<object?>>(value)));
        Assert.Equal(NpcCorporation, db.Store.FindPilot(1000002)!.CorpId);
        using WorldDbContext saved = new(db.Path);
        Assert.Empty(saved.Set<MarketExecution>());
        Assert.Empty(saved.Set<NpcMarketOrder>());
    }

    [Theory]
    [InlineData("corporationSvc", "GetCorpInfo")]
    [InlineData("allianceRegistry", "GetEmploymentRecord")]
    [InlineData("warsInfoMgr", "GetWarsByOwnerID")]
    [InlineData("facWarMgr", "GetCorporationWarFactionID")]
    [InlineData("config", "GetStationSolarSystemsByOwner")]
    public void InformationReadsRequireSelectionAndRejectMalformedArguments(string service, string method)
    {
        using Fixture fixture = new();
        Assert.Same(PilotCallResult.Unsupported, fixture.Call(service, method, [NpcCorporation]));
        fixture.Select();
        foreach (
            object?[] args in new object?[][] { [], [null], [false], ["1000044"], [0L], [-1L], [long.MaxValue], [NpcCorporation, true] }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(fixture.Call(service, method, args));
        }
        Assert.IsType<PilotCallResult.Failure>(fixture.Call(service, method, [NpcCorporation], new() { ["pilotID"] = Viewer }));
        var result = fixture.Call(service, method, [NpcCorporation], new() { ["machoVersion"] = 1L });
        Assert.IsType<PilotCallResult.Success>(result);
        if (service != "config")
        {
            Assert.IsType<PilotCallResult.Failure>(fixture.Call(service, method, [int.MaxValue]));
        }
        Assert.Empty(fixture.Context.Notifications);
        Assert.Equal(fixture.Context.Pilot, fixture.Db.Store.FindPilot(1000002));
    }

    [Fact]
    public void WarOwnerCacheValidatesBoundedSetsAndIncludesEveryOwnerWithoutEnablingMutations()
    {
        using Fixture fixture = new();
        fixture.Select();
        foreach (object?[] args in new object?[][] { [], [null], [new List<object?> { NpcCorporation }], [OwnerSet(0)] })
        {
            Assert.IsType<PilotCallResult.Failure>(fixture.Call("warsInfoMgr", "GetWarsByOwners", args));
        }
        Assert.IsType<PilotCallResult.Failure>(
            fixture.Call("warsInfoMgr", "GetWarsByOwners", [OwnerSet(Enumerable.Range(1, 1001).Select(id => (long)id).ToArray())])
        );
        var result = Assert.IsType<PilotCallResult.Success>(
            fixture.Call("warsInfoMgr", "GetWarsByOwners", [OwnerSet(NpcCorporation, 1000035)])
        );
        var wars = Assert.IsType<Dictionary<long, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(result.Reply)));
        Assert.Equal(new long[] { 1000035, NpcCorporation }, wars.Keys);
        Assert.All(wars.Values, value => Assert.Empty(Assert.IsType<List<object?>>(value)));
        Assert.Same(PilotCallResult.Unsupported, fixture.Call("warsInfoMgr", "DeclareWar", [NpcCorporation]));
        Assert.Empty(fixture.Context.Notifications);
    }

    [Fact]
    public void MarketActivityReadDoesNotScaleWithUnrelatedResidentExecutions()
    {
        using TestWorld db = new(liveWorld: true);
        var live = db.Services.GetRequiredService<LiveWorld>();
        var market = db.Services.GetRequiredService<ICorporationMarketInformation>();
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(Order(9000001, NpcCorporation, 601, false));
            action.Add(Order(9000002, 1000035, 601, false));
            action.Add(Trade(1, 9000001, NpcCorporation, true, 345, 7));
            action.Add(Trade(2, 9000002, 1000035, true, 345, 7));
            action.Commit();
        }
        Assert.Single(market.Read(NpcCorporation));
        var small = Read();

        const int unrelatedExecutions = 2_000;
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            for (int i = 0; i < unrelatedExecutions; i++)
            {
                action.Add(Trade(3 + i, 9000002, 1000035, true, 345, 7));
            }
            action.Commit();
        }
        var populated = Read();
        output.WriteLine($"Small ledger {small.Elapsed.TotalMilliseconds:F2} ms, {small.Allocated:N0} bytes.");
        output.WriteLine($"Populated ledger {populated.Elapsed.TotalMilliseconds:F2} ms, {populated.Allocated:N0} bytes.");
        Assert.InRange(populated.Allocated, 0, small.Allocated + 64 * 1024);

        (long Allocated, TimeSpan Elapsed) Read()
        {
            using var measured = SessionPerformance.Begin("corporation market activity", _ => { }, force: true)!;
            long start = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var activity = market.Read(NpcCorporation);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            var elapsed = Stopwatch.GetElapsedTime(start);
            Assert.Equal(new CorporationMarketActivity(601, new(345, 7, 1, StationId), null), Assert.Single(activity));
            Assert.Equal(0, measured.Commands);
            Assert.Equal(0, measured.Connections);
            Assert.Equal(0, measured.FittingCalculations);
            return (allocated, elapsed);
        }
    }

    [Fact]
    public async Task MarketActivityReturnsLatestPublicNpcTradesWithNativeUnitsAndSurvivesReopen()
    {
        using Fixture fixture = new();
        var live = fixture.Db.Services.GetRequiredService<LiveWorld>();
        fixture.Select();
        using (var action = live.Economy.Begin(TransactionParticipants.None))
        {
            action.Add(Order(9000001, NpcCorporation, 601, false));
            action.Add(Order(9000002, NpcCorporation, 601, true));
            action.Add(Order(9000003, 1000035, 601, false));
            action.Add(Order(9000004, NpcCorporation, 3651, false));
            action.Add(Trade(1, 9000001, NpcCorporation, true, 200, 1));
            action.Add(Trade(2, 9000001, NpcCorporation, true, 345, 7));
            action.Add(Trade(3, 9000002, NpcCorporation, false, 123, 4));
            action.Add(Trade(4, 9000003, 1000035, true, 999, 8));
            action.Add(Trade(5, 9999999, NpcCorporation, true, 888, 9));
            action.Add(Trade(6, 9000004, NpcCorporation, true, 12345, 2, 3651));
            action.Add(Trade(7, 9000003, NpcCorporation, true, 666, 7));
            action.Add(Trade(8, 9000001, 1000035, true, 777, 8));
            action.Commit();
        }
        var result = Assert.IsType<PilotCallResult.Success>(fixture.Call("corporationSvc", "GetCorpInfo", [NpcCorporation]));
        var rows = Rows(BlueMarshal.Decode(BlueMarshal.Encode(result.Reply)));
        Assert.Equal(2, rows.Count);
        var row = Assert.IsType<object?[]>(rows[0]);
        Assert.Equal(new object?[] { 601L, 3.45, 7L, 2L, (long)StationId, 1.23, 4L, 3L, (long)StationId }, row);
        Assert.Equal(new object?[] { 3651L, 123.45, 2L, 6L, (long)StationId, null, null, null, null }, rows[1]);
        Assert.Empty(fixture.Context.Notifications);
        await live.FlushAsync();
        fixture.Db.Services.Dispose();
        using var reopened = ServerServices.Build(new(fixture.Db.Path), liveWorld: true);
        var activity = reopened.GetRequiredService<ICorporationMarketInformation>().Read(NpcCorporation).First();
        Assert.Equal(601, activity.TypeId);
        Assert.Equal(new(345, 7, 2, StationId), activity.Sell);
        Assert.Equal(new(123, 4, 3, StationId), activity.Buy);
        using WorldDbContext saved = new(fixture.Db.Path);
        Assert.Equal(8, saved.Set<MarketExecution>().Count());
        Assert.Equal(4, saved.Set<NpcMarketOrder>().Count());
    }

    private static NpcMarketOrder Order(long id, int owner, int type, bool bid) =>
        new()
        {
            Id = id,
            OwnerId = owner,
            TypeId = type,
            Bid = bid,
            PriceCents = 100,
            Entered = 10,
            Remaining = 10,
            StationId = owner == NpcCorporation ? StationId : StationId + 1,
            SystemId = SystemId,
            RegionId = 10000002,
        };

    private static MarketExecution Trade(long id, long order, long owner, bool isBuy, long cents, int quantity, int type = 601) =>
        new()
        {
            Id = id,
            PilotId = Viewer,
            OrderId = order,
            CounterpartyId = owner,
            IsBuy = isBuy,
            TypeId = type,
            PriceCents = cents,
            Quantity = quantity,
            Time = id,
            StationId = StationId,
        };

    private static object? Decode(BlueInstance packet) =>
        BlueMarshal.Decode(BlueRead.Bytes(BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 1)[0]));

    private static BlueConstructor OwnerSet(params long[] owners) =>
        new("__builtin__.set", [owners.Select(owner => (object?)owner).ToList()]);

    private static Dictionary<string, object?> Fields(object? reply) => BlueRead.Fields(Assert.IsType<BlueInstance>(reply).State);

    private static List<object?> Rows(object? reply) => Assert.IsType<List<object?>>(Fields(reply)["lines"]);

    private sealed class Fixture : IDisposable
    {
        private readonly IServiceScope _scope;
        public TestWorld Db { get; } = new(liveWorld: true);
        public PilotCallContext Context { get; }
        private PilotCallRouter Router { get; }

        public Fixture()
        {
            CorporationPublicInfoTests.CreateCorporation(Db, false);
            _scope = Db.Services.CreateScope();
            var pilot = Db.Store.FindPilot(1000002)!;
            Context = new(
                1000002,
                pilot.Account,
                [pilot],
                _scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
                42,
                SignedSettings.Empty
            );
            Router = _scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        }

        public void Select() => Context.Select(Db.Store.FindPilot(1000002)!);

        public PilotCallResult Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            Router.Call(Context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
            Db.Dispose();
        }
    }
}
