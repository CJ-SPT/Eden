using System.Buffers.Binary;
using Eden.Data.World.Economy;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Trading;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Protocol.Trading;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class PlayerTradeTests
{
    private const long Pilot = ContractAssetTestData.Pilot;
    private const long Partner = ContractAssetTestData.Other;
    private const long Station = ContractAssetTestData.Station;
    private const string Service = PlayerTradeHandler.Service;

    [Fact]
    public void NativeTradeSettlesItemsAndMoneyOnceAndSurvivesReopen()
    {
        TestClock clock = new();
        using TestWorld world = new(clock: clock, liveWorld: true);
        var live = LiveWorld.Find(world.Path)!;
        long stack = Stack(world);
        long hull = Hull(world);
        long module = Stack(world, 1, hull, flag: InventoryFlag.HighSlot0);
        long payment = Stack(world, 2, owner: Partner);
        SetBalance(world, Pilot, 50_000);
        SetBalance(world, Partner, 30_000);
        using Client first = new(world, Pilot);
        using Client second = new(world, Partner);
        var tradeStore = world.Services.GetRequiredService<IPlayerTradeStore>();
        // Warm the immutable catalog records for the assets and trade restrictions.
        tradeStore.Quote(Pilot, Station, new([stack, hull], Station, (int)InventoryFlag.Hangar, null));
        tradeStore.Quote(Partner, Station, new([payment], Station, (int)InventoryFlag.Hangar, null));
        first.Inventory.List(Pilot, Station);
        List<string> commands = [];
        long id;
        using (var metrics = SessionPerformance.Begin("resident player trade", _ => { }, force: true, command: commands.Add)!)
        {
            id = Start(first, second);
            Assert.True(first.Accept(first.List()));
            Assert.True(first.CallBound("Add", [stack, Station], new() { ["qty"] = 7L, ["flag"] = 0L }).Value());
            Assert.True(first.CallBound("MultiAdd", [new List<object?> { hull }, Station], new() { ["flag"] = 0L }).Value());
            Assert.True(second.CallBound("Add", [payment, Station]).Value());
            first.CallBound("OfferMoney", [123.45d]).Value();
            second.CallBound("OfferMoney", [20L]).Value();
            var terms = second.List();
            Assert.Equal(new TradeAmounts(12345, 2000), terms.Money);
            Assert.Equal(3, terms.Items.Rows.Count);
            Assert.Null(terms.State.Initiator);
            Assert.Equal(20, Assert.Single(first.Inventory.List(Pilot, Station), i => i.Id == stack).Quantity);
            Assert.Equal(50_000, first.Wallet.Balance(Pilot));

            Assert.True(second.Accept(terms));
            Assert.True(first.Accept(terms));
            second.Advance();
            Assert.Equal(id, Assert.Single(first.Context.Notifications.OfType<TradeCompleted>()).ContainerId);
            Assert.Equal(id, Assert.Single(second.Context.Notifications.OfType<TradeCompleted>()).ContainerId);
            Assert.Contains(first.Context.Notifications.OfType<ItemChanged>(), n => n.Item.Id == payment && n.Item.OwnerId == Pilot);
            Assert.Contains(second.Context.Notifications.OfType<ItemChanged>(), n => n.Item.Id == hull && n.Item.OwnerId == Partner);
            Assert.True(first.CallBound("Abort", []).Value());
            Assert.IsType<PilotCallResult.Failure>(second.CallBound("MakeOffer", [Manifest(terms)]));
            Assert.Equal(39_655, first.Wallet.Balance(Pilot));
            Assert.Equal(40_345, second.Wallet.Balance(Partner));
            Assert.Empty(commands);
            Assert.Equal(0, metrics.Connections);
        }
        live.Dispose();
        using LiveWorld reopened = new(new(world.Path), clock);
        var journal = reopened.Economy.Inspect(state =>
        {
            Assert.Equal(13, state.Set<OwnedItem>().Find(stack)!.Quantity);
            Assert.Equal(Pilot, state.Set<OwnedItem>().Find(payment)!.OwnerId);
            Assert.Equal(Partner, state.Ships.Find(hull)!.OwnerId);
            Assert.Equal(Partner, state.Set<OwnedItem>().Find(module)!.OwnerId);
            Assert.Equal(hull, state.Set<OwnedItem>().Find(module)!.LocationId);
            return state
                .Filter<SavedServicePayment>(entry => entry.PilotId == Pilot && entry.Kind == WalletEntryType.PlayerTrading)
                .OrderBy(entry => entry.Id)
                .ToArray();
        });
        var received = Assert.Single(second.Inventory.List(Partner, Station), i => i.Quantity == 7);
        Assert.Equal(Partner, received.OwnerId);
        Assert.Collection(
            journal,
            debit =>
            {
                Assert.Equal(-12345, debit.AmountCents);
                Assert.Equal(37_655, debit.BalanceCents);
                Assert.Equal(Partner, debit.CounterpartyId);
            },
            credit =>
            {
                Assert.Equal(2000, credit.AmountCents);
                Assert.Equal(39_655, credit.BalanceCents);
            }
        );
    }

    [Fact]
    public void StaleManifestAndUnavailableAssetsOrMoneyCannotSettle()
    {
        using TestWorld world = new(liveWorld: true);
        long stack = Stack(world);
        long foreign = Stack(world, owner: Partner);
        SetBalance(world, Partner, 10_000);
        using Client first = new(world, Pilot);
        using Client second = new(world, Partner);
        Start(first, second);
        Assert.IsType<PilotCallResult.Failure>(first.CallBound("MultiAdd", [new List<object?> { stack, foreign }, Station]));
        Assert.IsType<PilotCallResult.Failure>(first.CallBound("Add", [first.Context.Pilot.ShipId, Station]));
        Assert.Empty(first.List().Items.Rows);
        first.CallBound("Add", [stack, Station]).Value();
        var old = first.List();
        Assert.True(first.Accept(old));
        second.CallBound("OfferMoney", [30L]).Value();
        Assert.False(second.Accept(old));
        Assert.Contains(second.Context.Notifications, notification => notification is TradeOfferReset);
        var terms = second.List();
        Assert.Null(terms.State.Initiator);
        var item = Assert.Single(terms.Items.Rows);
        Assert.False(second.Accept(terms with { Items = new([item with { Quantity = item.Quantity + 1 }]) }));
        Assert.True(first.Accept(terms));
        SetBalance(world, Partner, 1);
        Assert.IsType<PilotCallResult.Failure>(second.CallBound("MakeOffer", [Manifest(terms)]));
        Assert.Null(first.List().State.Initiator);
        Assert.Equal(20, Assert.Single(first.Inventory.List(Pilot, Station), i => i.Id == stack).Quantity);
        Assert.Equal(1, second.Wallet.Balance(Partner));

        SetBalance(world, Partner, 10_000);
        first.Inventory.Move(Pilot, [stack], Station, first.Context.Pilot.ShipId, (int)InventoryFlag.Cargo, 1);
        Assert.True(first.Accept(terms));
        Assert.IsType<PilotCallResult.Failure>(second.CallBound("MakeOffer", [Manifest(terms)]));
        Assert.Equal(10_000, second.Wallet.Balance(Partner));
        Assert.Equal(0, first.Wallet.Balance(Pilot));
    }

    [Fact]
    public void DeparturesAndCancellationInvalidateOnlyTheOldTrade()
    {
        using TestWorld world = new(liveWorld: true);
        using Client first = new(world, Pilot);
        Assert.IsType<PilotCallResult.Failure>(first.Call(Service, "InitiateTrade", [Pilot]));
        Assert.IsType<PilotCallResult.Failure>(first.Call(Service, "InitiateTrade", [Partner]));
        using Client second = new(world, Partner);
        using Client outsider = new(world, Pilot);
        long id = Start(first, second);
        Assert.IsType<PilotCallResult.Failure>(outsider.Call(Service, "MachoBindObject", [id, null]));
        var previous = first.Bound;
        Assert.Null(second.Context.Flight.CallWire("ship", "Undock", [second.Context.Pilot.ShipId]));
        second.Advance();
        first.Advance();
        Assert.Contains(second.Context.Notifications.OfType<TradeCancelled>(), notice => notice.ContainerId == id);
        Assert.Contains(first.Context.Notifications.OfType<TradeCancelled>(), notice => notice.ContainerId == id);
        Assert.IsType<PilotCallResult.Failure>(first.CallBound("List", []));
        Assert.IsType<PilotCallResult.Failure>(first.Call(Service, "InitiateTrade", [Partner]));
        Assert.Null(second.Context.Flight.CallWire("beyonce", "CmdDock", [Station, second.Context.Pilot.ShipId]));
        second.Advance();

        long next = Start(first, second);
        Assert.NotEqual(id, next);
        Assert.Equal("unknown binding", first.Resolve(previous));
        Assert.True(second.CallBound("Abort", []).Value());
        first.Advance();
        Assert.Contains(first.Context.Notifications.OfType<TradeCancelled>(), notice => notice.ContainerId == next);
        long disconnected = Start(first, second);
        second.Dispose();
        first.Advance();
        Assert.Contains(first.Context.Notifications.OfType<TradeCancelled>(), notice => notice.ContainerId == disconnected);
        Assert.IsType<PilotCallResult.Failure>(first.CallBound("List", []));
    }

    private static void SetBalance(TestWorld world, long pilot, long cents)
    {
        using var action = LiveWorld.Find(world.Path)!.Economy.Begin(new(pilots: [pilot]));
        var wallet = action.Set<PilotWallet>().Find(pilot) ?? action.Add(new PilotWallet { PilotId = pilot }).Entity;
        wallet.Cents = cents;
        action.Commit();
    }

    private static long Stack(
        TestWorld world,
        int quantity = 20,
        long location = Station,
        long owner = Pilot,
        InventoryFlag flag = InventoryFlag.Hangar
    )
    {
        using var action = LiveWorld.Find(world.Path)!.Economy.Begin(new(pilots: [owner]));
        long id = action.NextItemId();
        action.Add(
            new OwnedItem
            {
                Id = id,
                OwnerId = owner,
                TypeId = InventoryDefinitions.CivilianMinerType,
                LocationId = location,
                FlagId = (int)flag,
                Quantity = quantity,
            }
        );
        action.Commit();
        return id;
    }

    private static long Hull(TestWorld world)
    {
        using var action = LiveWorld.Find(world.Path)!.Economy.Begin(new(pilots: [Pilot]));
        long id = action.NextItemId();
        action.Add(
            new Ship
            {
                Id = id,
                OwnerId = Pilot,
                TypeId = 601,
                StationId = (int)Station,
                Name = "Trade hull",
            }
        );
        action.Commit();
        return id;
    }

    private static long Start(Client first, Client second)
    {
        var moniker = Assert.IsType<TradeMoniker>(
            Assert.IsType<PilotCallResult.Success>(first.Call(Service, "InitiateTrade", [Partner])).Reply
        );
        var wire = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(moniker)));
        Assert.Equal("carbon.common.script.net.moniker.Moniker", wire.Name);
        Assert.Equal(
            new object?[] { Service, (long)PilotLogin.NodeId, moniker.ContainerId, null },
            BlueRead.Tuple(wire.State, 4).Select(value => value is byte[] ? BlueMarshal.Text(value) : value).ToArray()
        );
        second.Advance();
        var notice = Assert.Single(second.Context.Notifications.OfType<TradeInitiated>());
        Assert.Equal(new TradeInitiated(Pilot, moniker), notice);
        first.Bind(moniker.ContainerId);
        second.Bind(moniker.ContainerId);
        first.Context.Notifications.Clear();
        second.Context.Notifications.Clear();
        return moniker.ContainerId;
    }

    private static object? Manifest(TradeList terms) =>
        BlueMarshal.Decode(
            BlueMarshal.Encode(
                new BlueInstance(
                    "utillib.KeyVal",
                    new Dictionary<string, object?>
                    {
                        ["tradeContainerID"] = terms.ContainerId,
                        ["money"] = terms.Money,
                        ["tradeItems"] = new BlueConstructor(
                            "__builtin__.set",
                            [terms.Items.Rows.Select(row => (object?)Packed(row)).ToList()]
                        ),
                    }
                )
            )
        );

    private static BluePackedRow Packed(InventoryRow row)
    {
        var descriptor = Assert.IsType<BlueRowDescriptor>(BlueMarshal.Decode(BlueMarshal.Encode(InventoryWire.Descriptor())));
        byte[] data = new byte[40];
        BinaryPrimitives.WriteInt64LittleEndian(data, row.Id);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), row.LocationId);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), row.TypeId);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20), checked((int)row.OwnerId));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), row.Quantity);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), row.GroupId);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(32), row.CategoryId);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(36), checked((short)row.FlagId));
        // Five literal blocks of eight bytes with two zero nibbles per marker.
        return new(descriptor, [0, .. data[..16], 0, .. data[16..32], 0, .. data[32..]], [row.CustomInfo]);
    }

    private sealed class Client : IDisposable
    {
        private readonly IServiceScope _scope;
        private readonly PilotCallRouter _router;
        private readonly PlayerTradeHandler _trades;
        internal PilotCallContext Context { get; }
        internal IPilotInventory Inventory { get; }
        internal IPilotWalletReader Wallet { get; }
        internal BlueObjectReference Bound { get; private set; } = null!;

        internal Client(TestWorld world, long pilot)
        {
            _scope = world.Services.CreateScope();
            var services = _scope.ServiceProvider;
            _router = services.GetRequiredService<PilotCallRouter>();
            _trades = services.GetRequiredService<PlayerTradeHandler>();
            Inventory = services.GetRequiredService<IPilotInventory>();
            Wallet = services.GetRequiredService<IPilotWalletReader>();
            var account = world.Store.FindPilot(pilot == Pilot ? 1000001 : 1000002)!;
            Context = new(
                pilot,
                account.Account,
                [account],
                services.GetRequiredService<FlightSessionFactory>().Create,
                pilot,
                SignedSettings.Empty
            );
            Context.Select(account);
            Advance();
        }

        internal void Advance() => _trades.Advance(Context);

        internal string Resolve(BlueObjectReference reference) =>
            _router.ResolveService(Context, [1L, (long)PilotLogin.NodeId, null, null], reference.Id);

        internal PilotCallResult Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
        {
            var result = _router.Call(Context, service, method, args, _ => { }, keywords);
            Advance();
            return result;
        }

        internal PilotCallResult CallBound(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            Call(Resolve(Bound), method, args, keywords);

        internal void Bind(long id)
        {
            var bind = Assert.IsType<BoundPilotReply>(
                Assert
                    .IsType<PilotCallResult.Success>(
                        Call(Service, "MachoBindObject", [id, new object?[] { "List", Array.Empty<object?>() }])
                    )
                    .Reply
            );
            Assert.Equal(id, Assert.IsType<TradeList>(bind.Reply).ContainerId);
            Bound = bind.Reference;
        }

        internal TradeList List() => Assert.IsType<TradeList>(Assert.IsType<PilotCallResult.Success>(CallBound("List", [])).Reply);

        internal bool Accept(TradeList terms) => CallBound("MakeOffer", [Manifest(terms)]).Value();

        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Context.Dispose();
                _scope.Dispose();
            }
        }
    }
}

internal static class TradeTestReply
{
    internal static bool Value(this PilotCallResult result) =>
        Assert.IsType<bool>(Assert.IsType<PilotCallResult.Success>(result).Reply.ToBlue());
}
