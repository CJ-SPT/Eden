using Eden.Gameplay.Characters;
using Eden.Gameplay.Market;
using Eden.Protocol.Characters;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class HomeStationServiceTests
{
    [Fact]
    public void NativeHomeCandidatesFeeNotificationsAndJournalUsePersistedStateAndRealSchoolHeadquarters()
    {
        using Fixture fixture = new();
        Assert.Same(PilotCallResult.Unsupported, fixture.Call("get_home_station", []));
        fixture.Select();
        var home = Fields(Reply(fixture.Call("get_home_station", [])));
        Assert.Equal((long)HomeStationTestData.Origin, home["id"]);
        Assert.Equal(false, home["is_fallback"]);
        var candidates = Assert.IsType<List<object?>>(Reply(fixture.Call("get_home_station_candidates", []))).Select(Fields).ToArray();
        var school = Assert.Single(candidates, candidate => Equals(candidate["is_school_hq"], true));
        Assert.Equal((long)HomeStationTestData.School, school["id"]);
        Assert.Empty(Assert.IsType<List<object?>>(school["errors"]));
        Assert.Null(Reply(fixture.Call("get_next_remote_change_time", [])));
        Assert.Null(Reply(fixture.Call("set_home_station", [(long)HomeStationTestData.School, false])));
        Assert.Equal(400000d, Assert.Single(fixture.Context.Notifications.OfType<AccountBalanceChanged>()).Balance);
        Assert.Equal(HomeStationTestData.School, Assert.Single(fixture.Context.Notifications.OfType<HomeStationChanged>()).Id);
        Assert.Equal((long)HomeStationTestData.School, Fields(Reply(fixture.Call("get_home_station", [])))["id"]);
        Assert.Equal(HomeStationTestData.Origin, fixture.Context.Pilot.StationId);
        var journal = Assert
            .IsType<PilotCallResult.Success>(
                fixture.Router.Call(fixture.Context, "account", "GetTransactions", new object?[] { 1000L, null, null, false }, _ => { })
            )
            .Reply;
        var payment = Assert.Single(Assert.IsType<BlueList<WalletJournalEntry>>(journal).Items);
        Assert.Equal(WalletEntryType.CloneTransfer, payment.Type);
        Assert.Equal(-10_000_000, payment.AmountCents);
        Assert.Equal(40_000_000, payment.BalanceCents);
        Assert.Equal(HomeStationTestData.School, payment.ReferenceId);
        Assert.NotEmpty(BlueMarshal.Encode(payment));
        var corporation = Assert.IsType<BlueConstructor>(
            Assert
                .IsType<PilotCallResult.Success>(
                    fixture.Router.Call(fixture.Context, "corpRegistry", "GetCorporation", Array.Empty<object?>(), _ => { })
                )
                .Reply.ToBlue()
        );
        Assert.Equal("blue.DBRow", corporation.Name);
        var values = Assert.IsType<object?[]>(corporation.Arguments[1]);
        Assert.Equal(HomeStationTestData.School, values[3]);
        Assert.Equal(HomeStationTestData.System, values[4]);
        Assert.NotEmpty(BlueMarshal.Encode(corporation));
        Assert.Equal(1000044, fixture.Db.Services.GetRequiredService<INpcCorporationDirectory>().Read(1000044).Id);
    }

    [Fact]
    public void InvalidRequestsCannotSpendMoneyChangeHomeOrForgeAnotherPilotsDestination()
    {
        using Fixture fixture = new();
        fixture.Select();
        foreach (
            var (method, args) in new (string, object?[])[]
            {
                ("get_home_station", [90000002L]),
                ("get_home_station_candidates", [90000002L]),
                ("get_next_remote_change_time", [90000002L]),
                ("self_destruct_clone", [90000002L]),
                ("set_home_station", [HomeStationTestData.School]),
                ("set_home_station", [(long)HomeStationTestData.School, "true"]),
                ("set_home_station", [(long)HomeStationTestData.Other, true]),
                ("set_home_station", [long.MaxValue, true]),
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(fixture.Call(method, args));
        }
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("get_home_station", [], new() { ["pilotID"] = 90000002L }));
        Assert.Same(PilotCallResult.Unsupported, fixture.Call("unknown_home_method", []));
        Assert.Empty(fixture.Context.Notifications);
        Assert.Equal(
            HomeStationTestData.Origin,
            fixture.Db.Services.GetRequiredService<IPilotHomeStation>().Read(HomeStationTestData.PilotId).Id
        );
        Assert.Equal(50_000_000, fixture.Db.Services.GetRequiredService<IPilotWalletReader>().Balance(HomeStationTestData.PilotId));
    }

    [Fact]
    public async Task CapsuleTransferReplacesTheSessionAndInvalidatesOldStationBindingsBeforeAnnouncingIt()
    {
        using Fixture fixture = new();
        long oldShip = HomeStationTestData.MakeActiveCapsule(fixture.Db);
        fixture.Select();
        var previous = fixture.Context.Pilot;
        var oldBinding = fixture.Context.Reference("invbroker", PilotBindingLifetime.Location);
        Assert.Null(Reply(fixture.Call("self_destruct_clone", [])));
        Assert.NotEqual(oldShip, fixture.Context.Pilot.ShipId);
        Assert.Equal(HomeStationTestData.Origin, fixture.Context.Pilot.StationId);
        Assert.Null(fixture.Context.ResolveReference(oldBinding.Id));
        Assert.Equal(2, fixture.Context.Notifications.OfType<ItemChanged>().Count());
        List<object?> output = [];
        PilotSessionOutput writer = new(
            fixture.Context,
            1000001,
            1,
            packet =>
            {
                output.Add(packet);
                return Task.CompletedTask;
            },
            _ => { }
        );
        await writer.AnnounceTransitionAsync(previous);
        var transition = Assert.IsType<BlueInstance>(output[^1]);
        Assert.EndsWith(".SessionChangeNotification", transition.Name);
        var changes = BlueRead.Fields(BlueRead.Tuple(BlueRead.Tuple(BlueRead.Tuple(transition.State, 14)[4], 3)[1], 2)[1]);
        Assert.Equal(new object?[] { oldShip, fixture.Context.Pilot.ShipId }, changes["shipid"]);
        Assert.Equal(new object?[] { HomeStationTestData.Other, HomeStationTestData.Origin }, changes["stationid"]);
        Assert.Empty(fixture.Context.Notifications);
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("self_destruct_clone", []));
    }

    private static Dictionary<string, object?> Fields(object? value) => BlueRead.Fields(Assert.IsType<BlueInstance>(value).State);

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));

    private sealed class Fixture : IDisposable
    {
        public TestClock Clock { get; } = new();
        public TestWorld Db { get; }
        public PilotCallContext Context { get; }
        private readonly IServiceScope _scope;
        public PilotCallRouter Router => _scope.ServiceProvider.GetRequiredService<PilotCallRouter>();

        public Fixture()
        {
            Db = new(clock: Clock);
            HomeStationTestData.Prepare(Db);
            _scope = Db.Services.CreateScope();
            var pilot = Db.Store.FindPilot(1000001)!;
            Context = new(1000001, pilot.Account, [pilot], p => new(Db.Store, p, Clock, new(), Db.Objects), 42, SignedSettings.Empty);
        }

        public void Select() => Context.Select(Db.Store.FindPilot(1000001)!);

        public PilotCallResult Call(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            Router.Call(Context, "home_station", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
            Db.Dispose();
        }
    }
}
