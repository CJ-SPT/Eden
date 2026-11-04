using Eden.Gameplay.Contracts;
using Eden.Protocol.Contracts;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CourierServiceTests
{
    private static BlueNewObject Request(long item, bool isPrivate = true)
    {
        var request = ContractServiceTests.Request(item);
        var fields = Assert.IsType<Dictionary<string, object?>>(request.State);
        fields["contractType"] = 3L;
        fields["isPrivate"] = isPrivate;
        fields["assignedToID"] = isPrivate ? CourierTestData.Hauler : 0L;
        fields["destinationID"] = (long)ContractAssetTestData.RemoteStation;
        fields["numDays"] = 1L;
        fields["price"] = 0L;
        fields["reward"] = 250d;
        fields["collateral"] = 20000d;
        return request;
    }

    [Fact]
    public void NativeCourierCreationAcceptanceProgressLookupAndDeliveryUseThePersistentPackage()
    {
        using Fixture f = new();
        Assert.Same(PilotCallResult.Unsupported, f.Owner.Call("CreateContract", [Request(f.Item)]));
        f.Select();
        long id = Created(f.Owner.Call("CreateContract", [Request(f.Item)]));
        var details = Fields(Reply(f.Hauler.Call("GetContract", [id])));
        Assert.Empty(Assert.IsType<List<object?>>(details["items"]));
        var contract = Fields(details["contract"]);
        Assert.Equal(3L, contract["type"]);
        Assert.Equal(ContractAssetTestData.RemoteStation, Convert.ToInt32(contract["endStationID"]));
        Assert.Equal(20000d, contract["collateral"]);
        Assert.Equal(1L, contract["numDays"]);
        Assert.Single(Assert.IsType<List<object?>>(Fields(Reply(f.Owner.Call("GetContract", [id])))["items"]));
        f.Owner.Context.Notifications.Clear();
        var accepted = Fields(Reply(f.Hauler.Call("AcceptContract", [id, false])));
        Assert.Equal(1L, accepted["status"]);
        long package = Assert.IsType<long>(accepted["crateID"]);
        var wrap = Assert.Single(f.Hauler.Context.Notifications.OfType<ItemChanged>());
        Assert.Equal(CourierRules.PackageType, wrap.Item.TypeId);
        Assert.Equal(-3500, wrap.Item.Quantity);
        f.Owner.Advance();
        Assert.Equal("OnContractAccepted", Assert.Single(f.Owner.Context.Notifications.OfType<ContractEvent>()).Name);
        var lookup = Fields(Reply(f.Hauler.Call("GetCourierContractFromItemID", [package])));
        Assert.Equal(id, lookup["contractID"]);
        Assert.Null(Reply(f.Owner.Call("GetCourierContractFromItemID", [package])));
        var login = Fields(Reply(f.Hauler.Call("GetLoginInfo", [])));
        var progress = Fields(Assert.Single(Assert.IsType<List<object?>>(login["inProgress"])));
        Assert.Equal(id, progress["contractID"]);
        Assert.Equal(f.Clock.Now + TimeSpan.FromDays(1).Ticks, progress["expires"]);
        Assert.Equal(1L, Fields(Reply(f.Hauler.Call("CollectMyPageInfo", [])))["numInProgress"]);
        Assert.Single(Assert.IsType<List<object?>>(Fields(Reply(f.Hauler.Call("GetMyCurrentContractList", [true, false])))["contracts"]));
        Assert.Single(Assert.IsType<List<object?>>(Fields(Reply(f.Owner.Call("GetMyCurrentContractList", [false, false])))["contracts"]));
        Assert.Equal(20000d, Fields(Reply(f.Hauler.Call("GetMyContractEscrow", [])))["iskEscrow"]);
        Assert.IsType<PilotCallResult.Failure>(f.Hauler.Call("CompleteContract", [id, 4L]));
        CourierTestData.Load(f.Db, package);
        CourierTestData.Dock(f.Db, 1000002, ContractAssetTestData.RemoteStation);
        f.Hauler.Refresh();
        f.Hauler.Context.Notifications.Clear();
        Assert.Equal(true, Reply(f.Hauler.Call("CompleteContract", [id, 4L])));
        Assert.Equal("OnContractCompleted", Assert.Single(f.Hauler.Context.Notifications.OfType<ContractEvent>()).Name);
        Assert.Null(Reply(f.Hauler.Call("GetCourierContractFromItemID", [package])));
        Assert.Empty(Assert.IsType<List<object?>>(Fields(Reply(f.Hauler.Call("GetLoginInfo", [])))["inProgress"]));
    }

    [Fact]
    public void NativePackageBindingReturnsItsVolumeRowAndBreaksThroughTheBoundInventory()
    {
        using Fixture f = new();
        f.Select();
        long id = Created(f.Owner.Call("CreateContract", [Request(f.Item)]));
        long package = Assert.IsType<long>(Fields(Reply(f.Hauler.Call("AcceptContract", [id, false])))["crateID"]);
        var binding = Assert.IsType<BlueObjectReference>(
            WireAssertions.Materialize(f.Hauler.Call("GetInventoryFromId", [package], "invbroker"))
        );
        string service = Assert.IsType<string>(f.Hauler.Context.ResolveReference(binding.Id));
        Assert.StartsWith(CourierPackageHandler.InventoryPrefix, service);
        Assert.Null(f.Owner.Context.ResolveReference(binding.Id));
        var row = Assert.IsType<BlueConstructor>(WireAssertions.Materialize(f.Hauler.Call("GetSelfInvItem", [], service)));
        Assert.Equal(-3500, Assert.IsType<object?[]>(row.Arguments[1])[5]);
        Assert.Null(Reply(f.Hauler.Call("BreakPlasticWrap", [], service)));
        Assert.Equal(7L, Fields(Fields(Reply(f.Hauler.Call("GetContract", [id])))["contract"])["status"]);
        Assert.IsType<PilotCallResult.Failure>(f.Hauler.Call("BreakPlasticWrap", [], service));
        Assert.Null(Reply(f.Hauler.Call("GetCourierContractFromItemID", [package])));
    }

    [Fact]
    public void NativeCourierSearchHonorsDestinationCollateralAndContractTypeWithoutRevealingTheManifest()
    {
        using Fixture f = new();
        f.Select();
        var request = Request(f.Item, isPrivate: false);
        var fields = Assert.IsType<Dictionary<string, object?>>(request.State);
        fields["itemList"] = new List<object?>
        {
            new List<object?> { f.Item, 7L },
            new List<object?> { ContractAssetTestData.Stack(f.Db), 1L },
        };
        long id = Created(f.Owner.Call("CreateContract", [request]));
        var search = Fields(
            Reply(
                f.Hauler.Call(
                    "SearchContracts",
                    [],
                    keywords: new()
                    {
                        ["contractType"] = 3L,
                        ["endLocationID"] = (long)ContractAssetTestData.RemoteStation,
                        ["minCollateral"] = 20000d,
                        ["maxCollateral"] = 20000d,
                        ["minReward"] = 250d,
                        ["maxReward"] = 250d,
                        ["sortBy"] = 9L,
                        ["sortDir"] = 1L,
                        ["excludeMultiple"] = true,
                    }
                )
            )
        );
        Assert.Equal(1L, search["numFound"]);
        var bundle = Fields(Assert.Single(Assert.IsType<List<object?>>(search["contracts"])));
        Assert.Equal(id, Fields(bundle["contract"])["contractID"]);
        Assert.Empty(Assert.IsType<List<object?>>(bundle["items"]));
        var exchanges = Fields(Reply(f.Hauler.Call("SearchContracts", [], keywords: new() { ["contractType"] = 10L })));
        Assert.Empty(Assert.IsType<List<object?>>(exchanges["contracts"]));
    }

    private static long Created(PilotCallResult result) => Assert.IsType<long>(Assert.Single(Assert.IsType<List<object?>>(Reply(result))));

    private static object? Reply(PilotCallResult result)
    {
        Assert.True(result is PilotCallResult.Success, result.ToString());
        return BlueMarshal.Decode(BlueMarshal.Encode(((PilotCallResult.Success)result).Reply));
    }

    private static Dictionary<string, object?> Fields(object? value) =>
        Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(value).State);

    private sealed class Fixture : IDisposable
    {
        public TestClock Clock { get; } = CalendarTestData.Clock();
        public TestWorld Db { get; }
        public Client Owner { get; }
        public Client Hauler { get; }
        public long Item { get; }

        public Fixture()
        {
            Db = new(clock: Clock);
            CourierTestData.Prepare(Db);
            Item = ContractAssetTestData.Stack(Db);
            Owner = new(Db, Clock, 1000001);
            Hauler = new(Db, Clock, 1000002);
        }

        public void Select()
        {
            Owner.Refresh();
            Hauler.Refresh();
            Owner.Advance();
            Hauler.Advance();
            Owner.Context.Notifications.Clear();
            Hauler.Context.Notifications.Clear();
        }

        public void Dispose()
        {
            Owner.Dispose();
            Hauler.Dispose();
            Db.Dispose();
        }
    }

    internal sealed class Client(TestWorld db, TestClock clock, long account) : IDisposable
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

        public void Refresh()
        {
            if (Context.Selected)
            {
                Context.ChangeShip(db.Store.FindPilot(account)!);
            }
            else
            {
                Context.Select(db.Store.FindPilot(account)!);
            }
            _scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().Enter(Context);
        }

        public void Advance() => _scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>().Advance(Context);

        public PilotCallResult Call(
            string method,
            object?[] args,
            string service = "contractProxy",
            Dictionary<string, object?>? keywords = null
        )
        {
            var reply = _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);
            if (Context.Selected)
            {
                Advance();
            }
            return reply;
        }

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
        }
    }
}
