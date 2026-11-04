using Eden.Protocol.Contracts;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class AuctionServiceTests
{
    [Fact]
    public void NativeAuctionCreationAndBuyoutPreserveFractionalIskAndRejectTheOmitted28DayOption()
    {
        using AuctionTestData.Fixture f = new();
        using CourierServiceTests.Client owner = new(f.Db, f.Clock, 1000001);
        using CourierServiceTests.Client bidder = new(f.Db, f.Clock, 1000002);
        owner.Refresh();
        bidder.Refresh();
        var request = Request(f.Item, 2_000_000.99);
        var fields = Assert.IsType<Dictionary<string, object?>>(request.State);
        fields["price"] = 1_000_000.01;
        fields["minutesExpire"] = 40320L;
        Assert.IsType<PilotCallResult.Failure>(owner.Call("CreateContract", [request]));
        fields["minutesExpire"] = 1440L;
        long id = Created(owner.Call("CreateContract", [request], keywords: new() { ["confirm"] = null }));
        Assert.Equal(true, Reply(bidder.Call("PlaceBid", [id, 2_000_000.99, false])));
        var details = Fields(Reply(bidder.Call("GetContract", [id])));
        Assert.Equal(1_000_000.01, Fields(details["contract"])["price"]);
        Assert.Equal(2_000_000.99, Fields(details["contract"])["collateral"]);
        Assert.Equal(2_000_000.99, Fields(Assert.Single(Assert.IsType<List<object?>>(details["bids"])))["amount"]);
        Assert.Equal(AuctionTestData.Funds - 200_000_099, f.Balance(AuctionTestData.Bidder));
    }

    [Fact]
    public void NativeOutbidNotificationRefundsTheWalletAndRetainsTheLosingPilotsBidList()
    {
        using AuctionTestData.Fixture f = new();
        using CourierServiceTests.Client bidder = new(f.Db, f.Clock, 1000002);
        bidder.Refresh();
        long id = f.Create();
        Reply(bidder.Call("PlaceBid", [id, 1000000d, false]));
        bidder.Context.Notifications.Clear();
        long other = Assert.Single(CalendarTestData.AddPilots(f.Db, 1));
        ContractTests.Fund(f.Db, other, AuctionTestData.Funds);
        f.Contracts.Bid(other, id, 110_000_000);
        bidder.Advance();
        var notification = Assert.Single(bidder.Context.Notifications.OfType<ContractEvent>());
        Assert.Equal("OnContractOutbid", notification.Name);
        Assert.Equal(id, Assert.Single(notification.Arguments));
        Assert.Equal(10_000_000d, Assert.Single(bidder.Context.Notifications.OfType<AccountBalanceChanged>()).Balance);
        Assert.Single(Assert.IsType<List<object?>>(Fields(Reply(bidder.Call("GetMyBids", [false])))["contracts"]));
        Assert.Null(Reply(bidder.Call("DeleteContractNotification", [id, false])));
        Assert.Empty(Assert.IsType<List<object?>>(Fields(Reply(bidder.Call("GetMyBids", [false])))["contracts"]));
    }

    private static BlueNewObject Request(long item, double buyout = 0)
    {
        var request = ContractServiceTests.Request(item);
        var fields = Assert.IsType<Dictionary<string, object?>>(request.State);
        fields["contractType"] = 2L;
        fields["isPrivate"] = false;
        fields["assignedToID"] = 0L;
        fields["price"] = 1000000d;
        fields["collateral"] = buyout;
        return request;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeBidDetailsMyBidsAttentionAndIndependentClaimsExposeActualSavedState(bool buyout)
    {
        using AuctionTestData.Fixture f = new();
        using CourierServiceTests.Client owner = new(f.Db, f.Clock, 1000001);
        using CourierServiceTests.Client bidder = new(f.Db, f.Clock, 1000002);
        Assert.Same(PilotCallResult.Unsupported, bidder.Call("PlaceBid", [1L, 1000000d, false]));
        owner.Refresh();
        bidder.Refresh();
        owner.Advance();
        bidder.Advance();
        long id = Created(owner.Call("CreateContract", [Request(f.Item, buyout ? 2000000d : 0)]));
        Assert.IsType<PilotCallResult.Failure>(bidder.Call("PlaceBid", [id, 2000000d, true]));
        Assert.IsType<PilotCallResult.Failure>(bidder.Call("PlaceBid", [id, 999999d, false]));
        Assert.Equal(true, Reply(bidder.Call("PlaceBid", [id, 2000000d, false])));
        var details = Fields(Reply(bidder.Call("GetContract", [id])));
        var contract = Fields(details["contract"]);
        Assert.Equal(2L, contract["type"]);
        Assert.Equal(buyout ? 2000000d : 0d, contract["collateral"]);
        var bid = Fields(Assert.Single(Assert.IsType<List<object?>>(details["bids"])));
        Assert.Equal(AuctionTestData.Bidder, bid["bidderID"]);
        Assert.Equal(2000000d, bid["amount"]);
        Assert.Equal(f.Clock.Now, bid["dateBid"]);
        Assert.True(Assert.IsType<long>(bid["bidID"]) > 0);
        Assert.Single(Assert.IsType<List<object?>>(Fields(Reply(bidder.Call("GetMyBids", [false])))["contracts"]));
        Assert.Empty(Assert.IsType<List<object?>>(Fields(Reply(bidder.Call("GetMyBids", [true])))["contracts"]));
        Assert.Equal(1L, Fields(Reply(bidder.Call("CollectMyPageInfo", [])))["numBiddingOn"]);
        if (!buyout)
        {
            Assert.IsType<PilotCallResult.Failure>(bidder.Call("FinishAuction", [id, false]));
            f.End(id);
            Assert.Equal(1L, Fields(Reply(bidder.Call("CollectMyPageInfo", [])))["numRequiresAttention"]);
            Assert.Single(Assert.IsType<List<object?>>(Fields(Reply(bidder.Call("GetMyExpiredContractList", [false])))["contracts"]));
            Assert.Equal(true, Reply(bidder.Call("FinishAuction", [id, false])));
        }
        Assert.Empty(Assert.IsType<List<object?>>(Fields(Reply(bidder.Call("GetMyExpiredContractList", [false])))["contracts"]));
        Assert.Single(Assert.IsType<List<object?>>(Fields(Reply(owner.Call("GetMyExpiredContractList", [false])))["contracts"]));
        owner.Context.Notifications.Clear();
        Assert.Equal(true, Reply(owner.Call("FinishAuction", [id, true])));
        Assert.Contains(owner.Context.Notifications.OfType<ContractEvent>(), e => e.Name == "OnContractCompleted");
        Assert.Equal(4L, Fields(Fields(Reply(owner.Call("GetContract", [id])))["contract"])["status"]);
        Assert.Equal(0L, Fields(Reply(bidder.Call("CollectMyPageInfo", [])))["numRequiresAttention"]);
        Assert.Null(Reply(bidder.Call("DeleteContractNotification", [id, false])));
        Assert.Empty(Assert.IsType<List<object?>>(Fields(Reply(bidder.Call("GetMyBids", [false])))["contracts"]));
    }

    [Fact]
    public void NativeAuctionSearchAndBidOnByFilterIncludeAuctionsAndReturnTheirManifest()
    {
        using AuctionTestData.Fixture f = new();
        using CourierServiceTests.Client owner = new(f.Db, f.Clock, 1000001);
        using CourierServiceTests.Client bidder = new(f.Db, f.Clock, 1000002);
        owner.Refresh();
        bidder.Refresh();
        long id = Created(owner.Call("CreateContract", [Request(f.Item, 2000000d)]));
        foreach (long type in new long[] { 2, 10, 11 })
        {
            var search = Fields(
                Reply(
                    bidder.Call(
                        "SearchContracts",
                        [],
                        keywords: new()
                        {
                            ["contractType"] = type,
                            ["minCollateral"] = 2000000d,
                            ["maxCollateral"] = 2000000d,
                        }
                    )
                )
            );
            var details = Fields(Assert.Single(Assert.IsType<List<object?>>(search["contracts"])));
            Assert.Equal(id, Fields(details["contract"])["contractID"]);
            Assert.Single(Assert.IsType<List<object?>>(details["items"]));
        }
        Reply(bidder.Call("PlaceBid", [id, 1000000d, false]));
        var list = Fields(Reply(bidder.Call("GetContractListForOwner", [AuctionTestData.Bidder, 10L, 2L, false])));
        Assert.Single(Assert.IsType<List<object?>>(list["contracts"]));
    }

    private static long Created(PilotCallResult result) => Assert.IsType<long>(Assert.Single(Assert.IsType<List<object?>>(Reply(result))));

    private static object? Reply(PilotCallResult result)
    {
        Assert.True(result is PilotCallResult.Success, result.ToString());
        return BlueMarshal.Decode(BlueMarshal.Encode(((PilotCallResult.Success)result).Reply));
    }

    private static Dictionary<string, object?> Fields(object? value) =>
        Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(value).State);
}
