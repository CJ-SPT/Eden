using Eden.Gameplay.Contracts;
using Eden.Gameplay.Market;
using Eden.Protocol.Contracts;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class ContractServiceTests
{
    [Fact]
    public void DelayedContractEventsCannotRewindANewerWalletTransaction()
    {
        using Fixture f = new();
        f.Select();
        long id = Created(f.Owner.Call("CreateContract", [Request(f.Item)]));
        f.Owner.Context.Notifications.Clear();
        Reply(f.Guest.Call("AcceptContract", [id, false]));
        ContractTests.Fund(f.Db, ContractAssetTestData.Pilot, 90_000_000);
        f.Owner.Advance();
        Assert.Equal(900000d, Assert.Single(f.Owner.Context.Notifications.OfType<AccountBalanceChanged>()).Balance);
    }

    [Fact]
    public void CancelledAssignmentsAreSkippedAndTheCursorStillAdvancesToNewActiveContracts()
    {
        using Fixture f = new();
        f.Select();
        long old = Created(f.Owner.Call("CreateContract", [Request(f.Item)]));
        Reply(f.Owner.Call("DeleteContract", [old]));
        f.Guest.Advance();
        Assert.Empty(f.Guest.Context.Notifications.OfType<ContractEvent>());
        long next = Created(f.Owner.Call("CreateContract", [Request(f.Item)]));
        f.Guest.Advance();
        Assert.Equal(next, Assert.Single(f.Guest.Context.Notifications.OfType<ContractEvent>()).ContractId);
    }

    internal static BlueNewObject Request(long item) =>
        new(
            BlueNewObject.ContractClass,
            new Dictionary<string, object?>
            {
                ["contractType"] = 1L,
                ["isPrivate"] = true,
                ["assignedToID"] = ContractAssetTestData.Other,
                ["minutesExpire"] = 1440L,
                ["numDays"] = 0L,
                ["startStationID"] = 60003760L,
                ["destinationID"] = null,
                ["price"] = 123.45,
                ["reward"] = 0L,
                ["collateral"] = 0L,
                ["title"] = new BlueUnicode("Étoile 装備"),
                ["description"] = new BlueUnicode("<b>Mining equipment</b> 🚀"),
                ["itemList"] = new List<object?>
                {
                    new List<object?> { item, 7L },
                },
                ["startStationDivision"] = 4L,
                ["requestItemTypeList"] = new List<object?>(),
                ["forCorp"] = false,
                ["multiContract"] = false,
            }
        );

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(0L)]
    [InlineData(1L)]
    public void NativeCreateAssignedDetailsAcceptLoginAndJournalUseTheSameAtomicContract(object? confirm)
    {
        using Fixture f = new();
        Assert.Same(PilotCallResult.Unsupported, f.Owner.Call("CreateContract", [Request(f.Item)]));
        f.Select();
        // Reproduce the native wizard's global station lookup before its item picker.
        object?[] binding = [ContractAssetTestData.Pilot, 10002L];
        Assert.Equal((long)PilotLogin.NodeId, Reply(f.Owner.Call("MachoResolveObject", [binding], service: "charMgr")));
        var bound = Assert.IsType<BoundPilotReply>(
            Assert
                .IsType<PilotCallResult.Success>(
                    f.Owner.Call("MachoBindObject", [binding, new object?[] { "ListStations", Array.Empty<object?>() }], service: "charMgr")
                )
                .Reply
        );
        Assert.NotEmpty(Assert.IsType<List<object?>>(Fields(BlueMarshal.Decode(BlueMarshal.Encode(bound.Reply)))["lines"]));
        Assert.NotEmpty(
            Assert.IsType<List<object?>>(WireAssertions.Materialize(f.Owner.Call("GetItemsInDockableLocation", [60003760L, false])))
        );
        long id = Created(f.Owner.Call("CreateContract", [Request(f.Item)], new() { ["confirm"] = confirm }));
        Assert.Equal("OnContractCreated", Assert.Single(f.Owner.Context.Notifications.OfType<ContractEvent>()).Name);
        var changed = Assert.Single(f.Owner.Context.Notifications.OfType<ItemChanged>());
        Assert.Equal(13, changed.Item.Quantity);
        Assert.Equal(20, changed.Previous[InventoryColumn.Quantity]);
        f.Guest.Advance();
        Assert.Equal("OnContractAssigned", Assert.Single(f.Guest.Context.Notifications.OfType<ContractEvent>()).Name);
        var login = Fields(Reply(f.Guest.Call("GetLoginInfo", [])));
        Assert.Equal(id, Fields(Assert.Single(Assert.IsType<List<object?>>(login["assignedToMe"])))["contractID"]);
        var details = Fields(Reply(f.Guest.Call("GetContract", [id])));
        var row = Fields(details["contract"]);
        Assert.Equal(1L, row["type"]);
        Assert.Equal(0L, row["status"]);
        Assert.Equal(ContractAssetTestData.Pilot, row["issuerID"]);
        Assert.Equal(ContractAssetTestData.Other, row["assigneeID"]);
        Assert.Equal("Étoile 装備", row["title"]);
        Assert.Equal("<b>Mining equipment</b> 🚀", row["description"]);
        Assert.Equal(123.45d, row["price"]);
        var item = Fields(Assert.Single(Assert.IsType<List<object?>>(details["items"])));
        Assert.Equal(true, item["inCrate"]);
        Assert.Equal(7L, item["quantity"]);
        Assert.Null(item["parentID"]);
        var limits = Fields(Reply(f.Owner.Call("NumOutstandingContracts", [])));
        Assert.Equal(1L, limits["myCharTotal"]);
        Assert.Equal(1L, limits["nonCorpForMyChar"]);
        var page = Fields(Reply(f.Guest.Call("CollectMyPageInfo", [])));
        Assert.Equal(
            new object?[] { ContractAssetTestData.Pilot, 1000044L, ContractAssetTestData.Other, 1L },
            Assert.Single(Assert.IsType<List<object?>>(page["outstandingContracts"]))
        );
        Assert.Equal(1000044L, Reply(f.Owner.Call("GetCorporationIDForCharacter", [ContractAssetTestData.Other], service: "corpmgr")));
        f.Owner.Context.Notifications.Clear();
        f.Guest.Context.Notifications.Clear();
        var accepted = Fields(Reply(f.Guest.Call("AcceptContract", [id, false])));
        Assert.Equal(4L, accepted["status"]);
        Assert.Equal(f.Clock.Now, accepted["dateCompleted"]);
        Assert.Equal("OnContractCompleted", Assert.Single(f.Guest.Context.Notifications.OfType<ContractEvent>()).Name);
        Assert.Equal(7, Assert.Single(f.Guest.Context.Notifications.OfType<ItemChanged>()).Item.Quantity);
        f.Owner.Advance();
        Assert.Equal(
            new[] { "OnContractAccepted", "OnContractCompleted" },
            f.Owner.Context.Notifications.OfType<ContractEvent>().Select(e => e.Name)
        );
        Assert.Equal(990123.45d, Assert.Single(f.Owner.Context.Notifications.OfType<AccountBalanceChanged>()).Balance);
        var journal = Assert.IsType<List<object?>>(Reply(f.Owner.Call("GetTransactions", [1000L, null, null, false], service: "account")));
        Assert.Equal(new object?[] { 72L, 71L }, journal.Select(e => Fields(e)["entryTypeID"]));
        Assert.Equal(new object?[] { -10000d, 123.45d }, journal.Select(e => Fields(e)["amount"]));
        Assert.All(journal, entry => Assert.Equal(id, Fields(entry)["referenceID"]));
        f.Owner.Context.Notifications.Clear();
        f.Owner.Advance();
        Assert.Empty(f.Owner.Context.Notifications);
        var attention = Fields(WireAssertions.Materialize(Assert.IsType<PilotCallResult.Success>(f.Owner.Call("GetLoginInfo", [])).Reply));
        var attentionRow = Assert.IsType<BlueConstructor>(Assert.Single(Assert.IsType<List<object?>>(attention["needsAttention"])));
        Assert.Equal("blue.DBRow", attentionRow.Name);
        Assert.Equal(new object?[] { id, false }, attentionRow.Arguments[1]);
    }

    [Fact]
    public void FetchingLoginAssignmentsBeforeTheFeedTickDoesNotAnnounceTheAssignmentTwice()
    {
        using Fixture f = new();
        f.Select();
        Reply(f.Owner.Call("CreateContract", [Request(f.Item)]));
        Assert.Single(Assert.IsType<List<object?>>(Fields(Reply(f.Guest.Call("GetLoginInfo", [])))["assignedToMe"]));
        Assert.Empty(f.Guest.Context.Notifications.OfType<ContractEvent>());
        f.Guest.Advance();
        Assert.Empty(f.Guest.Context.Notifications.OfType<ContractEvent>());
    }

    [Fact]
    public void NativeRejectReclaimAndOwnerListsRetainTheExpectedForms()
    {
        using Fixture f = new();
        f.Select();
        long id = Created(f.Owner.Call("CreateContract", [Request(f.Item)]));
        Assert.Equal(true, Reply(f.Guest.Call("CompleteContract", [id, 6L])));
        var owned = Fields(
            Reply(
                f.Owner.Call(
                    "GetContractListForOwner",
                    [ContractAssetTestData.Pilot, null, null, false],
                    new() { ["num"] = 100L, ["startContractID"] = null }
                )
            )
        );
        Assert.Equal(id, Fields(Assert.Single(Assert.IsType<List<object?>>(owned["contracts"])))["contractID"]);
        Assert.Single(Assert.IsType<List<object?>>(Assert.IsType<Dictionary<long, object?>>(owned["items"])[id]));
        Assert.IsType<PilotCallResult.Failure>(f.Guest.Call("DeleteContract", [id]));
        Assert.Equal(true, Reply(f.Owner.Call("DeleteContract", [id])));
        Assert.Null(Reply(f.Owner.Call("DeleteNotification", [id, false])));
        Assert.Equal(0L, Fields(Reply(f.Owner.Call("CollectMyPageInfo", [])))["numRequiresAttention"]);
        Assert.Empty(Assert.IsType<List<object?>>(Fields(Reply(f.Owner.Call("GetMyBids", [false])))["contracts"]));
        Assert.Empty(Assert.IsType<List<object?>>(Fields(Reply(f.Owner.Call("GetMyCurrentContractList", [true, false])))["contracts"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeSearchKeywordsReturnDetailsBundlesWithTypedItemSelections(bool set)
    {
        using Fixture f = new();
        f.Select();
        var request = Request(f.Item);
        var values = Assert.IsType<Dictionary<string, object?>>(request.State);
        values["isPrivate"] = false;
        values["assignedToID"] = null;
        // BuildPage3's public branch calls cfg.evelocations.Get(startStation).Station().regionID.
        var station = Fields(Reply(f.Owner.Call("GetStation", [60003760L], service: "stationSvc")));
        Assert.Equal(60003760L, station["stationID"]);
        Assert.Equal(10000002L, station["regionID"]);
        Assert.Equal(30000142L, station["solarSystemID"]);
        Assert.Equal(52678L, station["stationTypeID"]);
        long id = Created(f.Owner.Call("CreateContract", [request], new() { ["confirm"] = null }));
        object selection = set ? new BlueConstructor("__builtin__.set", [new List<object?> { 3651L }]) : new List<object?> { 3651L };
        var found = Fields(
            Reply(
                f.Guest.Call(
                    "SearchContracts",
                    [],
                    new()
                    {
                        ["itemTypes"] = selection,
                        ["itemTypeName"] = null,
                        ["itemCategoryID"] = null,
                        ["itemGroupID"] = null,
                        ["contractType"] = 10L,
                        ["securityClasses"] = null,
                        ["locationID"] = 30000142L,
                        ["endLocationID"] = null,
                        ["issuerID"] = ContractAssetTestData.Pilot,
                        ["minPrice"] = 123.45d,
                        ["maxPrice"] = 123.45d,
                        ["minReward"] = null,
                        ["maxReward"] = null,
                        ["minCollateral"] = null,
                        ["maxCollateral"] = null,
                        ["minVolume"] = 0L,
                        ["maxVolume"] = null,
                        ["excludeTrade"] = true,
                        ["excludeMultiple"] = true,
                        ["excludeNoBuyout"] = true,
                        ["availability"] = 0L,
                        ["description"] = null,
                        ["searchHint"] = null,
                        ["sortBy"] = 1L,
                        ["sortDir"] = 0L,
                        ["startNum"] = 0L,
                    }
                )
            )
        );
        Assert.Equal(1L, found["numFound"]);
        Assert.Equal(1000L, found["maxResults"]);
        Assert.True(Assert.IsType<double>(found["searchTime"]) >= 0);
        var bundle = Fields(Assert.Single(Assert.IsType<List<object?>>(found["contracts"])));
        Assert.Equal(id, Fields(bundle["contract"])["contractID"]);
        Assert.Single(Assert.IsType<List<object?>>(bundle["items"]));
        Assert.Empty(Assert.IsType<List<object?>>(bundle["bids"]));
    }

    [Fact]
    public void NativeCourierStillRequiresADestinationBeforeChangingState()
    {
        using Fixture f = new();
        f.Select();
        var request = Request(f.Item);
        var fields = Assert.IsType<Dictionary<string, object?>>(request.State);
        fields["contractType"] = (long)ContractType.Courier;
        fields["price"] = 0L;
        fields["reward"] = 250d;
        fields["collateral"] = 20000d;
        fields["numDays"] = 1L;
        Assert.IsType<PilotCallResult.Failure>(f.Owner.Call("CreateContract", [request], new() { ["confirm"] = null }));
        Assert.Equal(100_000_000, f.Db.Services.GetRequiredService<IPilotWalletReader>().Balance(ContractAssetTestData.Pilot));
        Assert.Empty(f.Owner.Context.Notifications);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<SavedContract>());
        Assert.Equal(20, world.Set<OwnedItem>().Find(f.Item)!.Quantity);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NativePublicWizardDefaultsCreateAndSettleWithOptionalNoneOrLegacyZeroIdentifiers(bool zeroAssignee, bool zeroDestination)
    {
        using Fixture f = new();
        f.Select();
        var request = Request(f.Item);
        var fields = Assert.IsType<Dictionary<string, object?>>(request.State);
        fields["isPrivate"] = false;
        fields["assignedToID"] = zeroAssignee ? (object)0L : null;
        fields["destinationID"] = zeroDestination ? (object)0L : null;
        fields["title"] = new BlueUnicode("");
        fields["description"] = new BlueUnicode("");
        fields["price"] = 1000d;
        fields["itemList"] = new List<object?>
        {
            new List<object?> { f.Item, 5L },
        };
        long id = Created(f.Owner.Call("CreateContract", [request], new() { ["confirm"] = null }));
        var details = Fields(Reply(f.Guest.Call("GetContract", [id])));
        var contract = Fields(details["contract"]);
        Assert.Equal(0L, contract["availability"]);
        Assert.Equal(0L, contract["assigneeID"]);
        Assert.Equal(0L, contract["endStationID"]);
        Assert.Equal(0L, contract["numDays"]);
        Assert.Equal(1000d, contract["price"]);
        Assert.Equal("", contract["title"]);
        Assert.Equal("", contract["description"]);
        Assert.Equal(5L, Fields(Assert.Single(Assert.IsType<List<object?>>(details["items"])))["quantity"]);
        Reply(f.Guest.Call("AcceptContract", [id, false]));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(15, world.Set<OwnedItem>().Find(f.Item)!.Quantity);
        Assert.Contains(world.Set<OwnedItem>(), item => item.OwnerId == ContractAssetTestData.Other && item.Quantity == 5);
    }

    [Fact]
    public void InvalidNativeRequestsRejectBeforeChangingAssetsWalletOrNotifications()
    {
        using Fixture f = new();
        f.Select();
        foreach (
            var (key, invalid) in new (string, object?)[]
            {
                ("forCorp", true),
                ("forCorp", null),
                ("isPrivate", null),
                ("multiContract", null),
                ("multiContract", true),
                ("assignedToID", null),
                ("contractType", null),
                ("startStationID", null),
                ("startStationDivision", null),
                ("minutesExpire", null),
                ("numDays", null),
                ("contractType", 3L),
                ("collateral", 1L),
                ("numDays", 1L),
                ("destinationID", 60003760L),
                ("startStationDivision", 5L),
                ("price", double.NaN),
                ("isPrivate", false),
                ("ownerID", ContractAssetTestData.Other),
                ("itemList", new object?[] { new object?[] { f.Item, 7L } }),
                (
                    "itemList",
                    new List<object?>
                    {
                        new List<object?> { null, 7L },
                    }
                ),
                (
                    "itemList",
                    new List<object?>
                    {
                        new List<object?> { f.Item, null },
                    }
                ),
            }
        )
        {
            var request = Request(f.Item);
            Assert.IsType<Dictionary<string, object?>>(request.State)[key] = invalid;
            Assert.IsType<PilotCallResult.Failure>(f.Owner.Call("CreateContract", [request]));
        }
        var incomplete = Request(f.Item);
        Assert.IsType<Dictionary<string, object?>>(incomplete.State).Remove("description");
        Assert.IsType<PilotCallResult.Failure>(f.Owner.Call("CreateContract", [incomplete]));
        Assert.IsType<PilotCallResult.Failure>(f.Owner.Call("CreateContract", [Request(f.Item)], new() { ["other"] = true }));
        foreach (object invalid in new object[] { -1L, 2L, 0d, "false" })
        {
            var failure = Assert.IsType<PilotCallResult.Failure>(
                f.Owner.Call("CreateContract", [Request(f.Item)], new() { ["confirm"] = invalid })
            );
            Assert.Equal("Invalid contract flag.", failure.Message);
        }
        Assert.IsType<PilotCallResult.Failure>(f.Owner.Call("SearchContracts", [], new() { ["sortBy"] = 999L }));
        Assert.IsType<PilotCallResult.Failure>(f.Owner.Call("SearchContracts", [1L]));
        Assert.IsType<PilotCallResult.Failure>(f.Owner.Call("GetCorporationIDForCharacter", [1L], service: "corpmgr"));
        Assert.Equal(100_000_000, f.Db.Services.GetRequiredService<IPilotWalletReader>().Balance(ContractAssetTestData.Pilot));
        Assert.Empty(f.Owner.Context.Notifications);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<SavedContract>());
        Assert.Equal(20, world.Set<OwnedItem>().Find(f.Item)!.Quantity);
    }

    [Fact]
    public void ChangeFeedDrainsBatchesInOrderWithoutRepeatingOrRequiringAnotherWrite()
    {
        using Fixture f = new();
        f.Select();
        var contracts = f.Db.Services.GetRequiredService<IPilotContracts>();
        for (int i = 0; i < 18; i++)
        {
            long id = contracts.Create(ContractAssetTestData.Pilot, ContractTests.Draft(f.Item) with { Items = [new(f.Item, 1)] });
            contracts.Delete(ContractAssetTestData.Pilot, id);
        }
        f.Owner.Advance();
        Assert.Equal(ContractRules.ChangeBatchSize, f.Owner.Context.Notifications.OfType<ContractEvent>().Count());
        Assert.Equal(
            new[] { ContractChangeKind.Created, ContractChangeKind.Reclaimed },
            f.Owner.Context.Notifications.OfType<ContractEvent>().Take(2).Select(e => e.Kind)
        );
        f.Owner.Context.Notifications.Clear();
        f.Owner.Advance();
        Assert.Equal(4, f.Owner.Context.Notifications.OfType<ContractEvent>().Count());
        f.Owner.Context.Notifications.Clear();
        f.Owner.Advance();
        Assert.Empty(f.Owner.Context.Notifications);
    }

    private static long Created(PilotCallResult result) => Assert.IsType<long>(Assert.Single(Assert.IsType<List<object?>>(Reply(result))));

    private static object? Reply(PilotCallResult result)
    {
        var success = Assert.IsType<PilotCallResult.Success>(result);
        return BlueMarshal.Decode(BlueMarshal.Encode(success.Reply));
    }

    private static Dictionary<string, object?> Fields(object? value) =>
        Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(value).State);

    private sealed class Fixture : IDisposable
    {
        public TestClock Clock { get; } = CalendarTestData.Clock();
        public TestWorld Db { get; }
        public Client Owner { get; }
        public Client Guest { get; }
        public long Item { get; }

        public Fixture()
        {
            Db = new(clock: Clock);
            ContractTests.Fund(Db, ContractAssetTestData.Pilot);
            ContractTests.Fund(Db, ContractAssetTestData.Other);
            Item = ContractAssetTestData.Stack(Db);
            Owner = new(Db, Clock, 1000001);
            Guest = new(Db, Clock, 1000002);
        }

        public void Select()
        {
            Owner.Select();
            Guest.Select();
            Owner.Advance();
            Guest.Advance();
            Owner.Context.Notifications.Clear();
            Guest.Context.Notifications.Clear();
        }

        public void Dispose()
        {
            Owner.Dispose();
            Guest.Dispose();
            Db.Dispose();
        }
    }

    private sealed class Client(TestWorld db, TestClock clock, long account) : IDisposable
    {
        private readonly IServiceScope _scope = db.Services.CreateScope();
        public PilotCallContext Context { get; } =
            new(
                account,
                db.Store.ReadAccount(account),
                db.Store.ListPilots(account),
                p => new(db.Store, p, clock, new(), db.Objects),
                account,
                SignedSettings.Empty
            );

        public void Select()
        {
            Context.Select(db.Store.FindPilot(account)!);
            _scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().Enter(Context);
            Context.Notifications.Clear();
        }

        public void Advance() => _scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().Advance(Context);

        public PilotCallResult Call(
            string method,
            object?[] args,
            Dictionary<string, object?>? keywords = null,
            string service = "contractProxy"
        )
        {
            var result = _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(
                    Context,
                    service,
                    method,
                    BlueMarshal.Decode(BlueMarshal.Encode(args)),
                    _ => { },
                    keywords is null ? null : BlueRead.Fields(BlueMarshal.Decode(BlueMarshal.Encode(keywords)))
                );
            if (Context.Selected)
            {
                Advance();
            }
            return result;
        }

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
        }
    }
}
