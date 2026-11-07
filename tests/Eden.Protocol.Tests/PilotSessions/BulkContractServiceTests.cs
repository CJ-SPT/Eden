using Eden.Gameplay.Inventory;
using Eden.Protocol.Contracts;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class BulkContractServiceTests
{
    private static BlueNewObject Request(IReadOnlyList<long> hulls, bool isPrivate)
    {
        var request = ContractServiceTests.Request(hulls[0]);
        var fields = Assert.IsType<Dictionary<string, object?>>(request.State);
        fields["isPrivate"] = isPrivate;
        fields["assignedToID"] = isPrivate ? (object)BulkContractTestData.Buyer : null;
        fields["price"] = 1234.56;
        fields["itemList"] = hulls.Select(id => (object?)new List<object?> { id, 1L }).ToList();
        fields["multiContract"] = true;
        return request;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeBulkCreationReturnsEveryContractIdWithSeparateManifestsPricesAndLaterSettlement(bool isPrivate)
    {
        using BulkContractTestData.Fixture f = new();
        using CourierServiceTests.Client owner = new(f.Db, f.Clock, 1000001);
        using CourierServiceTests.Client buyer = new(f.Db, f.Clock, 1000002);
        Assert.Same(PilotCallResult.Unsupported, owner.Call("CreateContract", [Request(f.Hulls, isPrivate)]));
        owner.Refresh();
        buyer.Refresh();
        owner.Advance();
        buyer.Advance();
        owner.Context.Notifications.Clear();
        buyer.Context.Notifications.Clear();
        var ids = Assert
            .IsType<List<object?>>(
                Reply(owner.Call("CreateContract", [Request(f.Hulls, isPrivate)], keywords: new() { ["confirm"] = null }))
            )
            .Select(Assert.IsType<long>)
            .ToArray();
        Assert.Equal(2, ids.Length);
        Assert.Equal(2, owner.Context.Notifications.OfType<ContractEvent>().Count(e => e.Name == "OnContractCreated"));
        var removals = owner.Context.Notifications.OfType<ItemChanged>().ToArray();
        Assert.Equal(f.Hulls, removals.Select(change => change.Item.Id));
        Assert.All(
            removals,
            change =>
            {
                Assert.Equal((int)InventoryLocation.Graveyard, change.Item.LocationId);
                Assert.Equal(ContractAssetTestData.Station, change.Previous[InventoryColumn.Location]);
                Assert.NotEmpty(BlueMarshal.Encode(change.Arguments));
            }
        );
        for (int i = 0; i < ids.Length; i++)
        {
            var details = Fields(Reply(buyer.Call("GetContract", [ids[i]])));
            var contract = Fields(details["contract"]);
            Assert.Equal(1L, contract["type"]);
            Assert.Equal(1234.56, contract["price"]);
            Assert.Equal("Étoile 装備", contract["title"]);
            var item = Fields(Assert.Single(Assert.IsType<List<object?>>(details["items"])));
            Assert.Equal(f.Hulls[i], item["itemID"]);
            Assert.Equal(1L, item["quantity"]);
        }
        if (isPrivate)
        {
            Assert.Equal(
                ids,
                buyer.Context.Notifications.OfType<ContractEvent>().Where(e => e.Name == "OnContractAssigned").Select(e => e.ContractId)
            );
            var login = Fields(Reply(buyer.Call("GetLoginInfo", [])));
            Assert.Equal(2, Assert.IsType<List<object?>>(login["assignedToMe"]).Count);
        }
        var list = Fields(Reply(owner.Call("GetContractListForOwner", [BulkContractTestData.Issuer, null, 1L, false])));
        Assert.Equal(2, Assert.IsType<List<object?>>(list["contracts"]).Count);
        Reply(buyer.Call("AcceptContract", [ids[0], false]));
        Assert.Equal(true, Reply(owner.Call("DeleteContract", [ids[1]])));
        Assert.Equal(BulkContractTestData.Funds - BulkContractTestData.Price, f.Balance(BulkContractTestData.Buyer));
        Assert.Equal(4L, Fields(Fields(Reply(owner.Call("GetContract", [ids[0]])))["contract"])["status"]);
        Assert.Equal(5L, Fields(Fields(Reply(owner.Call("GetContract", [ids[1]])))["contract"])["status"]);
    }

    [Fact]
    public void NativeInvalidBulkRequestsDoNotFallBackToOneCombinedContract()
    {
        using BulkContractTestData.Fixture f = new();
        using CourierServiceTests.Client owner = new(f.Db, f.Clock, 1000001);
        owner.Refresh();
        var request = Request(f.Hulls, true);
        var fields = Assert.IsType<Dictionary<string, object?>>(request.State);
        long revision = f.Contracts.Revision;
        foreach (
            var (key, value) in new (string, object)[]
            {
                ("reward", 1d),
                (
                    "requestItemTypeList",
                    new List<object?>
                    {
                        new List<object?> { 3651L, 1L },
                    }
                ),
                ("forCorp", true),
                ("multiContract", "true"),
                ("contractType", 2L),
            }
        )
        {
            object? original = fields[key];
            fields[key] = value;
            Assert.IsType<PilotCallResult.Failure>(owner.Call("CreateContract", [request]));
            fields[key] = original;
        }
        f.Unchanged(revision);
    }

    private static object? Reply(PilotCallResult result)
    {
        Assert.True(result is PilotCallResult.Success, result.ToString());
        return BlueMarshal.Decode(BlueMarshal.Encode(((PilotCallResult.Success)result).Reply));
    }

    private static Dictionary<string, object?> Fields(object? value) =>
        Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(value).State);
}
