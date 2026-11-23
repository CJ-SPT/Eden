using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureCargoDropoffTests
{
    [Fact]
    public async Task NativeDropOffChecksSurfaceRangeAndPreservesPrivateDeliveriesAcrossRestart()
    {
        const long DepositorAccount = 1000002;
        const int DeliveriesFlag = 173;
        const int DepositRange = 10_000;
        using Fixture f = new();
        var structure = f.Online();
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var depositor = f.Db.Store.FindPilot(DepositorAccount)!;
        var manager = f.Db.Store.FindPilot(1000001)!;
        f.Structures.SetAccess(manager.PilotId, structure.Id, publicAccess: false, []);
        var docking = f.Db.Services.GetRequiredService<IStationDocking>();
        Place(1_000);
        Assert.Contains(
            "Docking access",
            Assert.Throws<InvalidDataException>(() => docking.Dock(depositor.PilotId, depositor.ShipId, structure.Id)).Message
        );

        long first,
            second,
            foreign,
            fitted,
            remote;
        using (var setup = live.Economy.Begin(new(pilots: [depositor.PilotId, manager.PilotId])))
        {
            first = Stock(depositor.PilotId, depositor.ShipId, InventoryFlag.Cargo, 34, 31);
            second = Stock(depositor.PilotId, depositor.ShipId, InventoryFlag.Cargo, 35, 51);
            foreign = Stock(manager.PilotId, manager.ShipId, InventoryFlag.Cargo, 35, 2);
            fitted = Stock(depositor.PilotId, depositor.ShipId, InventoryFlag.HighSlot1, 3638, 1, singleton: true);
            remote = Stock(depositor.PilotId, depositor.StationId, InventoryFlag.Hangar, 34, 5);
            setup.Commit();

            long Stock(long owner, long location, InventoryFlag flag, int type, int quantity, bool singleton = false)
            {
                long id = setup.NextItemId();
                setup.Add(
                    new OwnedItem
                    {
                        Id = id,
                        OwnerId = owner,
                        LocationId = location,
                        FlagId = (int)flag,
                        TypeId = type,
                        Quantity = quantity,
                        Singleton = singleton,
                    }
                );
                return id;
            }
        }
        var batch = new List<object?> { first, foreign, fitted, remote, long.MaxValue, second };
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var factory = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        var assets = f.Db.Services.GetRequiredService<IPilotAssets>();
        var inventory = f.Db.Services.GetRequiredService<IPilotInventory>();
        long session = 70;

        Place(DepositRange + 1);
        using (var beyond = Select())
        {
            Assert.IsType<PilotCallResult.Failure>(Deposit(beyond, depositor.ShipId));
            Assert.Empty(beyond.Notifications);
        }
        Place(DepositRange);
        using (var atEdge = Select())
        {
            Assert.IsType<PilotCallResult.Failure>(Deposit(atEdge, manager.ShipId));
            long until = f.Clock.Now + TimeSpan.FromSeconds(1).Ticks;
            using (var timer = live.Economy.Begin(new(pilots: [depositor.PilotId])))
            {
                var row =
                    timer.Set<SavedCrimewatch>().Find(depositor.PilotId)
                    ?? timer.Add(new SavedCrimewatch { PilotId = depositor.PilotId }).Entity;
                row.SystemId = Fixture.SystemId;
                row.WeaponsActive = f.Clock.Now;
                row.WeaponsUntil = until;
                timer.Commit();
            }
            Assert.IsType<PilotCallResult.Failure>(Deposit(atEdge, depositor.ShipId));
            Assert.DoesNotContain(assets.Locations(depositor.PilotId), location => location.StationId == structure.Id);
            Assert.Empty(atEdge.Notifications.Concat(atEdge.NotificationsBeforeFlight));

            f.Clock.AdvanceWallTime(TimeSpan.FromTicks(until - f.Clock.Now));
            Assert.Equal(new object?[] { first, second }, Moved(Deposit(atEdge, depositor.ShipId)));
            var changes = atEdge.Notifications.Concat(atEdge.NotificationsBeforeFlight).OfType<ItemChanged>().ToArray();
            Assert.Equal(new[] { first, second }, changes.Select(change => change.Item.Id).Order());
            Assert.All(changes, change => Assert.Equal(structure.Id, change.Item.LocationId));
            Assert.Empty(Moved(Deposit(atEdge, depositor.ShipId)));
            Assert.Equal(2, atEdge.Notifications.Concat(atEdge.NotificationsBeforeFlight).OfType<ItemChanged>().Count());
        }

        Assert.Equal(2, Assert.Single(assets.Locations(depositor.PilotId), location => location.StationId == structure.Id).ItemCount);
        Assert.Empty(assets.Items(manager.PilotId, new(structure.Id)));
        Assert.Equal(new[] { first, second }, assets.Items(depositor.PilotId, new(structure.Id)).Select(item => item.Id).Order());
        Assert.Equal(1, Assert.Single(inventory.List(depositor.PilotId, depositor.ShipId), item => item.Id == fitted).Quantity);
        Assert.Equal(2, Assert.Single(inventory.List(manager.PilotId, manager.ShipId), item => item.Id == foreign).Quantity);
        Assert.Equal(5, Assert.Single(inventory.List(depositor.PilotId, depositor.StationId), item => item.Id == remote).Quantity);

        await live.FlushAsync();
        live.Dispose();
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using LiveWorld reopened = new(new(f.Db.Path), f.Clock, _ => { });
        using (var replay = Select())
        {
            Assert.Empty(Moved(Deposit(replay, depositor.ShipId)));
        }
        var restored = inventory.List(depositor.PilotId, structure.Id);
        Assert.Equal(31, Assert.Single(restored, item => item.Id == first && item.FlagId == DeliveriesFlag).Quantity);
        Assert.Equal(51, Assert.Single(restored, item => item.Id == second && item.FlagId == DeliveriesFlag).Quantity);
        f.Structures.SetAccess(manager.PilotId, structure.Id, false, [new(depositor.PilotId, true)]);
        Place(1_000);
        docking.Dock(depositor.PilotId, depositor.ShipId, structure.Id);
        using var inside = Select();
        var reference = Assert.IsType<BlueObjectReference>(
            Success(Call(inside, "invbroker", "GetInventoryFromId", [structure.Id, false])).ToBlue()
        );
        string hangar = Assert.IsType<string>(inside.ResolveReference(reference.Id));
        Assert.Equal(new[] { first, second }, List(DeliveriesFlag).Select(item => item.Id).Order());
        Assert.Same(
            PilotCallResult.None,
            Call(inside, hangar, "MultiAdd", [new List<object?> { first, second }, structure.Id], new() { ["flag"] = 4L })
        );
        Assert.Empty(List(DeliveriesFlag));
        Assert.Equal(new[] { first, second }, List(4).Select(item => item.Id).Where(id => id == first || id == second).Order());

        IReadOnlyList<InventoryRow> List(int flag) =>
            Assert.IsType<BlueList<InventoryRow>>(Success(Call(inside, hangar, "List", [], new() { ["flag"] = (long)flag }))).Items;

        PilotCallResult Deposit(PilotCallContext context, long ship) =>
            Call(context, "structureCargoDelivery", "DropOffItems", [batch, ship, structure.Id]);

        void Place(double surfaceDistance) =>
            f.Db.Store.SetPosition(
                depositor.ShipId,
                structure.Position + new SpacePosition(structure.Profile.Radius + depositor.Flight.ShipRadius + surfaceDistance, 0, 0),
                Fixture.SystemId
            );

        PilotCallContext Select()
        {
            var pilot = f.Db.Store.FindPilot(DepositorAccount)!;
            PilotCallContext context = new(DepositorAccount, pilot.Account, [pilot], factory.Create, session++, SignedSettings.Empty);
            context.Select(pilot);
            if (pilot.Position is not null)
            {
                context.Flight.Snapshot();
            }
            return context;
        }

        PilotCallResult Call(
            PilotCallContext context,
            string service,
            string method,
            object?[] args,
            Dictionary<string, object?>? keywords = null
        ) => router.Call(context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        static List<object?> Moved(PilotCallResult result) =>
            Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(Success(result))));

        static BlueModel Success(PilotCallResult result) => Assert.IsType<PilotCallResult.Success>(result).Reply;
    }
}
