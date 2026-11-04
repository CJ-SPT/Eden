using Eden.Gameplay.Insurance;
using Eden.Gameplay.Market;
using Eden.Protocol.Insurance;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class InsuranceServiceTests
{
    [Fact]
    public void NativeQuotePurchaseConfirmationContractsCancellationAndJournalUseRealPolicyState()
    {
        using Fixture fixture = new();
        Assert.IsType<PilotCallResult.UnsupportedCall>(fixture.Call("GetContracts", []));
        fixture.Select();
        Assert.Equal(1000d, Reply(fixture.Call("GetInsurancePrice", [601L])));
        var prices = Assert.IsType<Dictionary<long, object?>>(
            Reply(fixture.Call("GetInsurancePrices", [new List<object?> { 601L, 3638L, 601L }]))
        );
        Assert.Equal(1000d, prices[601]);
        Assert.Equal(0d, prices[3638]);
        Assert.Null(
            Reply(
                fixture.Router.Call(fixture.Context, "corpStationMgr", "DoStandingCheckForStationService", new object?[] { 21L }, _ => { })
            )
        );
        Assert.Empty(Assert.IsType<List<object?>>(Reply(fixture.Call("GetContracts", []))));
        Assert.Null(Reply(fixture.Call("GetContractForShip", [fixture.Context.Pilot.ShipId])));
        Assert.Null(Reply(fixture.Call("InsureShip", [fixture.Context.Pilot.ShipId, 50d, false])));
        var balance = Assert.IsType<AccountBalanceChanged>(Assert.Single(fixture.Context.Notifications));
        Assert.Equal(new object?[] { "cash", 90000001L, 9950d }, balance.Arguments);
        fixture.Context.Notifications.Clear();
        var contract = Fields(Assert.Single(Assert.IsType<List<object?>>(Reply(fixture.Call("GetContracts", [])))));
        Assert.Equal(fixture.Context.Pilot.ShipId, contract["shipID"]);
        Assert.Equal(90000001L, contract["ownerID"]);
        Assert.Equal(0.5d, contract["fraction"]);
        Assert.Equal(fixture.Clock.Now, contract["startDate"]);
        Assert.Equal(fixture.Clock.Now + TimeSpan.FromDays(84).Ticks, contract["endDate"]);
        Assert.Equal(contract, Fields(Reply(fixture.Call("GetContractForShip", [fixture.Context.Pilot.ShipId]))));
        var existing = Assert.IsType<PilotCallResult.NamedFailure>(fixture.Call("InsureShip", [fixture.Context.Pilot.ShipId, 300d, false]));
        Assert.Equal("InsureShipFailedSingleContract", existing.Name);
        Assert.Equal(90000001L, BlueRead.Fields(existing.Values.ToBlue())["ownerName"]);
        Assert.Empty(fixture.Context.Notifications);
        Assert.Null(Reply(fixture.Call("InsureShip", [fixture.Context.Pilot.ShipId, 300d, false], new() { ["voidOld"] = true })));
        Assert.Equal(9650d, Assert.IsType<AccountBalanceChanged>(Assert.Single(fixture.Context.Notifications)).Balance);
        fixture.Context.Notifications.Clear();
        var journal = Assert.IsType<List<object?>>(
            Reply(fixture.Router.Call(fixture.Context, "account", "GetTransactions", new object?[] { 1000L, null, null, false }, _ => { }))
        );
        Assert.Equal(2, journal.Count);
        var entries = journal.Select(Fields).ToArray();
        Assert.All(
            entries,
            entry =>
            {
                Assert.Equal(19L, entry["entryTypeID"]);
                Assert.Equal(90000001L, entry["ownerID1"]);
                Assert.Equal(InsuranceRules.InsurerCorporationId, entry["ownerID2"]);
                Assert.Equal(-fixture.Context.Pilot.ShipId, entry["referenceID"]);
            }
        );
        Assert.Equal(new object?[] { -50d, -300d }, entries.Select(entry => entry["amount"]));
        Assert.Equal(2, entries.Select(entry => entry["transactionID"]).Distinct().Count());
        Assert.Null(Reply(fixture.Call("UnInsureShip", [fixture.Context.Pilot.ShipId])));
        Assert.Empty(Assert.IsType<List<object?>>(Reply(fixture.Call("GetContracts", []))));
        Assert.Empty(Assert.IsType<List<object?>>(Reply(fixture.Call("GetContracts", [1L]))));
        Assert.Equal(965000, fixture.Db.Services.GetRequiredService<IPilotWalletReader>().Balance(90000001));
        Assert.Empty(fixture.Context.Notifications);
    }

    [Fact]
    public void NativeThreeFieldStationBindingWorksAndRejectsRemoteOrSpaceBindings()
    {
        using Fixture fixture = new();
        fixture.Select();
        long station = fixture.Context.Pilot.StationId;
        var bound = Assert.IsType<BoundPilotReply>(
            Assert
                .IsType<PilotCallResult.Success>(
                    fixture.Call(
                        "MachoBindObject",
                        [new object?[] { station, station, 15L }, new object?[] { "GetInsurancePrice", new object?[] { 601L } }]
                    )
                )
                .Reply
        );
        Assert.Equal(1000d, bound.Reply.ToBlue());
        Assert.Equal("insuranceSvc", fixture.Context.ResolveReference(bound.Reference.Id));
        Assert.NotEmpty(BlueMarshal.Encode(bound));
        Assert.IsType<PilotCallResult.UnsupportedCall>(
            fixture.Call("MachoBindObject", [new object?[] { station + 1, station, 15L }, null])
        );
        Assert.IsType<PilotCallResult.UnsupportedCall>(fixture.Call("MachoBindObject", [new object?[] { station, station, 5L }, null]));
        fixture.Context.Flight.CallWire("ship", "Undock", [fixture.Context.Pilot.ShipId]);
        Assert.IsType<PilotCallResult.UnsupportedCall>(fixture.Call("MachoBindObject", [new object?[] { station, station, 15L }, null]));
        Assert.Equal(1000d, Reply(fixture.Call("GetInsurancePrice", [601L])));
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("InsureShip", [fixture.Context.Pilot.ShipId, 50d, false]));
        Assert.Empty(fixture.Db.Services.GetRequiredService<IPilotInsurance>().Contracts(90000001));
    }

    [Fact]
    public void MalformedForgedAndCorporationRequestsCannotSpendMoneyOrChangePolicies()
    {
        using Fixture fixture = new();
        fixture.Select();
        foreach (
            (string method, object?[] args) in new (string, object?[])[]
            {
                ("InsureShip", [fixture.Context.Pilot.ShipId]),
                ("InsureShip", [1000000000002L, 50d, false]),
                ("InsureShip", [fixture.Context.Pilot.ShipId, 50d, true]),
                ("InsureShip", [fixture.Context.Pilot.ShipId, 50.01d, false]),
                ("InsureShip", [fixture.Context.Pilot.ShipId, double.NaN, false]),
                ("InsureShip", [fixture.Context.Pilot.ShipId, double.PositiveInfinity, false]),
                ("InsureShip", [fixture.Context.Pilot.ShipId, double.MaxValue, false]),
                ("GetInsurancePrices", [new List<object?> { "invalid" }]),
                ("GetInsurancePrice", [long.MaxValue]),
                ("GetContracts", [90000002L]),
                ("UnInsureShip", [1000000000002L]),
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(fixture.Call(method, args));
        }
        Assert.IsType<PilotCallResult.Failure>(
            fixture.Call("InsureShip", [fixture.Context.Pilot.ShipId, 50d, false], new() { ["pilotId"] = 90000002L })
        );
        Assert.IsType<PilotCallResult.Failure>(
            fixture.Call("InsureShip", [fixture.Context.Pilot.ShipId, 50d, false], new() { ["voidOld"] = "bad" })
        );
        Assert.Null(Reply(fixture.Call("GetContractForShip", [1000000000002L])));
        Assert.Equal(1_000_000, fixture.Db.Services.GetRequiredService<IPilotWalletReader>().Balance(90000001));
        Assert.Empty(fixture.Db.Services.GetRequiredService<IPilotWalletReader>().Journal(90000001).Insurance);
        Assert.Empty(fixture.Context.Notifications);
    }

    private static Dictionary<string, object?> Fields(object? value) => BlueRead.Fields(Assert.IsType<BlueInstance>(value).State);

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));

    private sealed class Fixture : IDisposable
    {
        public TestClock Clock { get; } = new();
        public TestWorld Db { get; }
        private readonly IServiceScope _scope;
        public PilotCallContext Context { get; }
        public PilotCallRouter Router => _scope.ServiceProvider.GetRequiredService<PilotCallRouter>();

        public Fixture()
        {
            Db = new(clock: Clock);
            InsuranceTestData.Prepare(Db);
            InsuranceTestData.Fund(Db);
            _scope = Db.Services.CreateScope();
            var pilot = Db.Store.FindPilot(1000001)!;
            Context = new(1000001, pilot.Account, [pilot], p => new(Db.Store, p, Clock, new(), Db.Objects), 42, SignedSettings.Empty);
        }

        public void Select() => Context.Select(Db.Store.FindPilot(1000001)!);

        public PilotCallResult Call(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            Router.Call(Context, "insuranceSvc", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
            Db.Dispose();
        }
    }
}
