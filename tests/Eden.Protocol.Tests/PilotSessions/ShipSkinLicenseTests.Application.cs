using Eden.Data.World.Inventory;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Gateway;
using Eden.Protocol.Gateway.ShipSkins;
using Eden.Protocol.Gateway.ShipSkins.Api;
using Eden.Protocol.PilotSessions;
using Eden.Server;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Storage;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class ShipSkinLicenseTests
{
    [Fact]
    public async Task NativeApplicationAndGatewayReflectOwnershipVisibilityAndExpiryAcrossCheckpoint()
    {
        TestClock clock = new(new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero));
        using TestWorld world = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(world.Path);
        LoadSkins(database.CatalogPath);
        var live = world.Services.GetRequiredService<LiveWorld>();
        var owner = world.Store.FindPilot(OwnerAccount)!;
        var other = world.Store.FindPilot(OtherAccount)!;
        var gateway = world.Services.GetRequiredService<GatewayRequests>();
        SkinGatewayClient pilotGateway = new(gateway, OwnerAccount, owner.PilotId);
        long ship,
            spare,
            timed,
            permanent;
        using (var setup = live.Economy.Begin(new(pilots: [owner.PilotId])))
        {
            ship = AddHull("Active Apocalypse");
            spare = AddHull("Spare Apocalypse");
            setup.Pilots.Find(owner.PilotId)!.ActiveShipId = ship;
            timed = AddLicense(TimedLicense, ship, InventoryFlag.Cargo, 2);
            permanent = AddLicense(PermanentLicense, owner.StationId, InventoryFlag.Hangar, 1);
            setup.Commit();

            long AddHull(string name)
            {
                long id = setup.NextItemId();
                setup.Add(
                    new Ship
                    {
                        Id = id,
                        OwnerId = owner.PilotId,
                        StationId = owner.StationId,
                        TypeId = Apocalypse,
                        Name = name,
                    }
                );
                return id;
            }

            long AddLicense(int type, long location, InventoryFlag flag, int quantity)
            {
                long id = setup.NextItemId();
                setup.Add(
                    new OwnedItem
                    {
                        Id = id,
                        OwnerId = owner.PilotId,
                        LocationId = location,
                        FlagId = (int)flag,
                        TypeId = type,
                        Quantity = quantity,
                    }
                );
                return id;
            }
        }

        var get = new GetRequest { Ship = new() { Sequential = (ulong)ship } };
        Assert.Equal(401u, gateway.Handle(pilotGateway.Envelope(get)).StatusCode);
        Assert.Equal(401u, gateway.Handle(pilotGateway.Envelope(get), OtherAccount).StatusCode);
        Assert.Equal(400u, pilotGateway.Call(new GetRequest()).StatusCode);
        using (Client docked = new(world.Services, OwnerAccount))
        {
            Assert.IsType<PilotCallResult.Failure>(docked.Apply(ship, ApocalypseSkin));
            AssertState(pilotGateway.Get(ship), owner.PilotId, ship, null);
            Assert.Same(PilotCallResult.None, docked.Activate(timed, permanent));
            Assert.IsType<PilotCallResult.Failure>(docked.Apply(ship, MerlinSkin));
            Assert.IsType<PilotCallResult.Failure>(docked.Apply(other.ShipId, ApocalypseSkin));
            AssertState(pilotGateway.Get(ship), owner.PilotId, ship, null);
            Assert.Same(PilotCallResult.None, docked.Apply(spare, ApocalypseSkin));
            AssertState(pilotGateway.Get(spare), owner.PilotId, spare, ApocalypseSkin);
        }

        SpacePosition point = new(10_000_000, 0, 0);
        world.Store.SetPosition(ship, point, owner.SystemId);
        using (Client actor = new(world.Services, OwnerAccount))
        {
            actor.EnterGrid();
            Assert.IsType<PilotCallResult.Failure>(actor.Apply(spare, null));
            world.Store.SetPosition(other.ShipId, point + new SpacePosition(1_000_000_000, 0, 0), owner.SystemId);
            using (Client distant = new(world.Services, OtherAccount))
            {
                distant.EnterGrid();
                SkinGatewayClient offGrid = new(gateway, OtherAccount, other.PilotId);
                Assert.Equal(404u, offGrid.Call(get).StatusCode);
                Assert.DoesNotContain(offGrid.Bubble(), state => state.Ship.Sequential == (ulong)ship);
                Assert.Empty(offGrid.Notices());
                _ = pilotGateway.Notices();

                Assert.Same(PilotCallResult.None, actor.Apply(ship, ApocalypseSkin));
                AssertSetNotice(pilotGateway, owner.PilotId, ship, ApocalypseSkin);
                Assert.Empty(offGrid.Notices());
                Assert.Equal(404u, offGrid.Call(get).StatusCode);
                AssertState(
                    Assert.Single(pilotGateway.Bubble(), state => state.Ship.Sequential == (ulong)ship),
                    owner.PilotId,
                    ship,
                    ApocalypseSkin
                );

                Assert.Equal("SkinAlreadyApplied", Assert.IsType<PilotCallResult.NamedFailure>(actor.Apply(ship, ApocalypseSkin)).Name);
                Assert.Empty(pilotGateway.Notices());
            }

            world.Store.SetPosition(other.ShipId, point + new SpacePosition(5_000, 0, 0), owner.SystemId);
            using Client neighbor = new(world.Services, OtherAccount);
            neighbor.EnterGrid();
            SkinGatewayClient nearby = new(gateway, OtherAccount, other.PilotId);
            AssertState(nearby.Get(ship), owner.PilotId, ship, ApocalypseSkin);
            AssertState(Assert.Single(nearby.Bubble(), state => state.Ship.Sequential == (ulong)ship), owner.PilotId, ship, ApocalypseSkin);
            Assert.Equal(404u, nearby.Call(new GetRequest { Ship = new() { Sequential = (ulong)spare } }).StatusCode);
            Assert.Empty(nearby.Notices());

            Assert.Same(PilotCallResult.None, actor.Apply(ship, null));
            AssertSetNotice(pilotGateway, owner.PilotId, ship, null);
            AssertSetNotice(nearby, owner.PilotId, ship, null);
            AssertState(nearby.Get(ship), owner.PilotId, ship, null);
            Assert.Equal("SkinAlreadyApplied", Assert.IsType<PilotCallResult.NamedFailure>(actor.Apply(ship, null)).Name);
            Assert.Empty(pilotGateway.Notices());
            Assert.Empty(nearby.Notices());

            Assert.Same(PilotCallResult.None, actor.Apply(ship, ApocalypseSkin));
            AssertSetNotice(pilotGateway, owner.PilotId, ship, ApocalypseSkin);
            AssertSetNotice(nearby, owner.PilotId, ship, ApocalypseSkin);
        }

        await live.FlushAsync();
        world.Services.Dispose();
        WorldInitialization.Initialize(database, clock);
        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        reopened.GetRequiredService<SqliteWorld>().SetPosition(ship, null);
        using Client restored = new(reopened, OwnerAccount);
        SkinGatewayClient restoredGateway = new(reopened.GetRequiredService<GatewayRequests>(), OwnerAccount, owner.PilotId);
        long expires = Assert.IsType<long>(restored.Detail(owner.PilotId, ApocalypseSkin)!.Expires);
        clock.AdvanceWallTime(TimeSpan.FromTicks(expires - clock.Now - 1));
        AssertState(restoredGateway.Get(ship), owner.PilotId, ship, ApocalypseSkin);
        Assert.Empty(restoredGateway.Notices());
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        AssertSetNotice(restoredGateway, owner.PilotId, ship, null);
        AssertState(restoredGateway.Get(ship), owner.PilotId, ship, null);
        Assert.IsType<PilotCallResult.Failure>(restored.Apply(ship, ApocalypseSkin));

        Assert.Same(PilotCallResult.None, restored.Activate(timed));
        AssertState(restoredGateway.Get(ship), owner.PilotId, ship, null);
        AssertState(restoredGateway.Get(spare), owner.PilotId, spare, null);
        Assert.Empty(restoredGateway.Notices());
        Assert.Same(PilotCallResult.None, restored.Apply(ship, ApocalypseSkin));
        AssertSetNotice(restoredGateway, owner.PilotId, ship, ApocalypseSkin);

        Assert.Same(PilotCallResult.None, restored.Apply(spare, ApocalypseSkin));
        AssertState(restoredGateway.Get(spare), owner.PilotId, spare, ApocalypseSkin);
        reopened.GetRequiredService<SqliteWorld>().SetPosition(other.ShipId, null);
        using (
            var transfer = reopened
                .GetRequiredService<LiveWorld>()
                .Economy.Begin(new(pilots: [owner.PilotId, other.PilotId], ships: [spare]))
        )
        {
            var delivered = transfer.Ships.Find(spare)!;
            delivered.OwnerId = other.PilotId;
            delivered.StationId = transfer.Pilots.Find(other.PilotId)!.ActiveShip.StationId;
            transfer.Commit();
        }
        Assert.Equal(404u, restoredGateway.Call(new GetRequest { Ship = new() { Sequential = (ulong)spare } }).StatusCode);
        SkinGatewayClient recipient = new(reopened.GetRequiredService<GatewayRequests>(), OtherAccount, other.PilotId);
        AssertState(recipient.Get(spare), other.PilotId, spare, null);
    }

    private static void AssertState(State state, long owner, long ship, int? skin)
    {
        Assert.Equal((uint)owner, state.Character.Sequential);
        Assert.Equal((ulong)ship, state.Ship.Sequential);
        if (skin is int skinId)
        {
            Assert.False(state.Skin.NoSkin);
            Assert.Equal((ulong)skinId, state.Skin.Firstparty.Identifier.Sequential);
        }
        else
        {
            Assert.True(state.Skin.NoSkin);
            Assert.Null(state.Skin.Firstparty);
        }
    }

    private static void AssertSetNotice(SkinGatewayClient gateway, long owner, long ship, int? skin)
    {
        var notice = Assert.Single(gateway.Notices());
        Assert.Equal(gateway.PilotId, notice.TargetGroup.Character);
        Assert.Equal("type.googleapis.com/eve_public.cosmetic.ship.api.SetNotice", notice.Payload.TypeUrl);
        AssertState(notice.Payload.Unpack<SetNotice>().State, owner, ship, skin);
    }

    private sealed class SkinGatewayClient(GatewayRequests gateway, long account, long pilot)
    {
        private readonly ShipSkinNoticeCursor _cursor = new();

        public uint PilotId => checked((uint)pilot);

        public Request Envelope(IMessage payload) =>
            new()
            {
                CorrelationUuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                AuthoritativeContext = new() { Identity = new() { Character = new() { Sequential = PilotId } } },
                Payload = Any.Pack(payload),
            };

        public Response Call(IMessage payload) =>
            Response.Parser.ParseFrom(gateway.Handle(Request.Parser.ParseFrom(Envelope(payload).ToByteArray()), account).ToByteArray());

        public State Get(long ship) => Read<GetResponse>(new GetRequest { Ship = new() { Sequential = (ulong)ship } }).State;

        public IReadOnlyList<State> Bubble() => Read<GetAllInBubbleResponse>(new GetAllInBubbleRequest()).States;

        public IReadOnlyList<Notice> Notices() =>
            gateway.ShipSkinNotices(account, _cursor).Select(notice => Notice.Parser.ParseFrom(notice.ToByteArray())).ToArray();

        private T Read<T>(IMessage payload)
            where T : class, IMessage<T>, new()
        {
            var response = Call(payload);
            Assert.Equal(200u, response.StatusCode);
            return response.Payload.Unpack<T>();
        }
    }
}
