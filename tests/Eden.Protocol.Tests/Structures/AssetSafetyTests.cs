using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Structures;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Structures;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class AssetSafetyTests
{
    private const int StationId = 60015159;
    private const long Budget = 1_000_000_000;

    [Fact]
    public async Task NativeSafetyPreservesNestedAssetsAndShipsThenChargesAtomicPartialRecoveryAfterReopen()
    {
        using Fixture f = Prepare();
        var structure = f.Online();
        long ore = Item(f, structure.Id, 34, 100);
        long bag = Item(f, structure.Id, 23, 1, singleton: true);
        long nested = Item(f, bag, 35, 40);
        long blueprint = Item(f, structure.Id, 1130, 1, singleton: true);
        long hull;
        using (WorldActionScope action = Scope(f))
        {
            var bp = action.World.Set<OwnedItem>().Find(blueprint)!;
            bp.MaterialEfficiency = 10;
            bp.TimeEfficiency = 20;
            bp.BlueprintRuns = 7;
            hull = InventoryPersistence.NextId(action.World);
            action.World.Add(
                new Ship
                {
                    Id = hull,
                    TypeId = 601,
                    OwnerId = f.PilotId,
                    StationId = StationId,
                    StructureId = structure.Id,
                    Name = "Saved fitted hull",
                }
            );
            action.Commit();
        }
        long fitting = Item(f, hull, 399, 1, singleton: true, flag: (int)InventoryFlag.MediumSlot0);
        long charge = Item(f, fitting, 215, 20);
        var safety = f.Db.Services.GetRequiredService<IPilotAssetSafety>();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using var services = f.Db.Services.CreateScope();
        var factory = services.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        using PilotCallContext context = new(1000001, pilot.Account, [pilot], factory.Create, 91, SignedSettings.Empty);
        context.Select(pilot);
        var router = services.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var monitor = services.ServiceProvider.GetRequiredService<AssetSafetyHandler>();
        monitor.Advance(context);
        Assert.IsType<PilotCallResult.Success>(
            router.Call(
                context,
                "structureAssetSafety",
                "MovePersonalAssetsToSafety",
                new object?[] { (long)Fixture.SystemId, structure.Id },
                _ => { }
            )
        );
        Assert.Contains(context.Notifications, n => n is AssetSafetyCreated);
        var wrap = Assert.Single(safety.List(f.PilotId, false));
        monitor.Advance(context);
        Assert.Single(context.Notifications.OfType<AssetSafetyCreated>());
        using (var metrics = SessionPerformance.Begin("unchanged asset safety monitor", _ => { }, force: true)!)
        {
            for (int frame = 0; frame < 10; frame++)
            {
                monitor.Advance(context);
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        Assert.Equal(structure.Id, wrap.StructureId);
        Assert.Equal(StationId, wrap.NearestStation.Id);
        Assert.Throws<InvalidDataException>(() => safety.Inventory(f.PilotId, wrap.Id));
        using (WorldActionScope read = Scope(f))
        {
            Assert.Equal(wrap.Id, read.World.Ships.Find(hull)!.StoredInId);
            Assert.Equal(bag, read.World.Set<OwnedItem>().Find(nested)!.LocationId);
            Assert.Equal(hull, read.World.Set<OwnedItem>().Find(fitting)!.LocationId);
            Assert.Equal(7, read.World.Set<OwnedItem>().Find(blueprint)!.BlueprintRuns);
        }
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        string copy = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "safety-copy.sqlite");
        File.Copy(f.Db.Path, copy);
        WorldInitialization.Initialize(new(copy));
        using LiveWorld reopened = new(new(copy), f.Clock);
        IPilotAssetSafety restored = new AssetSafetyStore(new(copy), f.Clock);
        Assert.Equal(wrap, Assert.Single(restored.List(f.PilotId, false)));
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(5) - TimeSpan.FromTicks(1));
        Assert.Throws<InvalidDataException>(() => safety.Deliver(f.PilotId, wrap.Id, Fixture.SystemId, StationId));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        safety.Deliver(f.PilotId, wrap.Id, Fixture.SystemId, StationId);
        monitor.Advance(context);
        Assert.Contains(context.Notifications, n => n is AssetSafetyDelivered { OwnerId: 90000001 });
        Assert.Throws<InvalidDataException>(() => safety.Deliver(f.PilotId, wrap.Id, Fixture.SystemId, StationId));
        Dock(f, StationId);
        context.Select(f.Db.Store.FindPilot(1000001)!);
        var contents = safety.Inventory(f.PilotId, wrap.Id);
        Assert.Equal(new[] { ore, bag, blueprint, hull }.Order(), contents.Items.Select(i => i.Id).Order());
        Assert.Single(safety.Inventory(f.PilotId, bag).Items);
        var bound = Assert.IsType<PilotCallResult.Success>(
            router.Call(context, "invbroker", "GetInventoryFromId", new object?[] { wrap.Id }, _ => { })
        );
        string binding = context.ResolveReference(Assert.IsType<BlueObjectReference>(bound.Reply.ToBlue()).Id)!;
        Assert.StartsWith(AssetSafetyHandler.InventoryPrefix, binding);
        var listed = Assert.IsType<PilotCallResult.Success>(router.Call(context, binding, "List", Array.Empty<object?>(), _ => { }));
        Assert.Equal(4, Assert.IsType<BlueList<InventoryRow>>(listed.Reply).Items.Count);
        var inventory = f.Db.Services.GetRequiredService<IPilotInventory>();
        Assert.Throws<InvalidDataException>(() =>
            inventory.Move(f.PilotId, f.ShipId, new([wrap.Id], StationId, (int)InventoryFlag.Cargo, null))
        );
        var partial = safety.Recover(f.PilotId, StationId, new([ore], wrap.Id, (int)InventoryFlag.Hangar, 25));
        Assert.True(partial.BalanceCents < Budget);
        Assert.Contains(partial.Changes, c => c.Item.LocationId == StationId && c.Item.TypeId == 34 && c.Item.Quantity == 25);
        Assert.Equal(75, safety.Inventory(f.PilotId, wrap.Id).Items.Single(i => i.Id == ore).Quantity);
        Assert.IsType<PilotCallResult.Success>(router.Call(context, "@hangar", "Add", new object?[] { ore, wrap.Id }, _ => { }));
        long paid;
        using (WorldActionScope balance = Scope(f))
        {
            paid = balance.World.Set<PilotWallet>().Find(f.PilotId)!.Cents;
        }
        Assert.Throws<InvalidDataException>(() =>
            safety.Recover(f.PilotId, StationId, new([ore], wrap.Id, (int)InventoryFlag.Hangar, null))
        );
        using (WorldActionScope read = Scope(f))
        {
            Assert.Equal(paid, read.World.Set<PilotWallet>().Find(f.PilotId)!.Cents);
        }
        safety.Recover(f.PilotId, StationId, new([bag, blueprint, hull], wrap.Id, (int)InventoryFlag.Hangar, null));
        Assert.False(safety.Handles(wrap.Id));
        using WorldActionScope final = Scope(f);
        Assert.Equal(bag, final.World.Set<OwnedItem>().Find(nested)!.LocationId);
        Assert.Equal(hull, final.World.Set<OwnedItem>().Find(fitting)!.LocationId);
        Assert.Equal((int)InventoryFlag.MediumSlot0, final.World.Set<OwnedItem>().Find(fitting)!.FlagId);
        Assert.Equal(fitting, final.World.Set<OwnedItem>().Find(charge)!.LocationId);
        Assert.Null(final.World.Ships.Find(hull)!.StoredInId);
        Assert.Equal(StationId, final.World.Ships.Find(hull)!.StationId);
        Assert.Equal(7, final.World.Set<OwnedItem>().Find(blueprint)!.BlueprintRuns);
        Assert.Equal(10, final.World.Set<OwnedItem>().Find(blueprint)!.MaterialEfficiency);
        Assert.Equal(20, final.World.Set<OwnedItem>().Find(blueprint)!.TimeEfficiency);
    }

    [Theory]
    [InlineData(10000, true, 50)]
    [InlineData(10000, false, 1500)]
    [InlineData(1, true, 1)]
    [InlineData(0, false, 0)]
    public void RecoveryFeesUseThePublishedRatesAndRoundUpToOneCent(int value, bool local, long expected) =>
        Assert.Equal(expected, AssetSafetyRules.RecoveryFee(value, local));

    [Fact]
    public void RecoveryRejectsForeignAccessInvalidBatchesAndInsufficientFundsWithoutChangingCustody()
    {
        using Fixture f = Prepare();
        var structure = f.Online();
        long ore = Item(f, structure.Id, 34, 100);
        var safety = f.Db.Services.GetRequiredService<IPilotAssetSafety>();
        var wrap = safety.Move(f.PilotId, Fixture.SystemId, structure.Id, false)!;
        Assert.Throws<InvalidDataException>(() => safety.Move(f.PilotId, Fixture.SystemId, structure.Id, false));
        Assert.Null(safety.Names(90000002, [wrap.Id])[wrap.Id]);
        Assert.Throws<InvalidDataException>(() => safety.Inventory(90000002, wrap.Id));
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(5));
        safety.Deliver(f.PilotId, wrap.Id, Fixture.SystemId, StationId);
        Dock(f, StationId);
        var contractError = Assert.Throws<InvalidDataException>(() =>
            f
                .Db.Services.GetRequiredService<IPilotContracts>()
                .Create(f.PilotId, new(90000002, StationId, 1440, 0, 0, "Safety wrap", "", [new(wrap.Id, 1)], []))
        );
        Assert.Contains("safety", contractError.Message, StringComparison.OrdinalIgnoreCase);
        using (WorldActionScope emptyWallet = Scope(f))
        {
            emptyWallet.World.Set<PilotWallet>().Find(f.PilotId)!.Cents = 0;
            emptyWallet.Commit();
        }
        Assert.Throws<InvalidDataException>(() =>
            safety.Recover(f.PilotId, StationId, new([ore], wrap.Id, (int)InventoryFlag.Hangar, null))
        );
        Assert.Throws<InvalidDataException>(() =>
            safety.Recover(f.PilotId, StationId, new([ore, ore], wrap.Id, (int)InventoryFlag.Hangar, null))
        );
        Assert.Throws<InvalidDataException>(() =>
            safety.Recover(f.PilotId, StationId, new([ore], wrap.Id, (int)InventoryFlag.Hangar, 101))
        );
        Assert.Throws<InvalidDataException>(() =>
            f.Db.Services.GetRequiredService<IPilotItemTrash>().Trash(f.PilotId, StationId, [wrap.Id])
        );
        Assert.Equal(100, Assert.Single(safety.Inventory(f.PilotId, wrap.Id).Items).Quantity);
        using WorldActionScope unchanged = Scope(f);
        Assert.Equal(0, unchanged.World.Set<PilotWallet>().Find(f.PilotId)!.Cents);
        Assert.NotNull(unchanged.World.Set<SavedAssetSafety>().Find(wrap.Id));
    }

    [Fact]
    public void CorporationRecoveryPreservesDivisionsAndUsesOnlyItsMasterWallet()
    {
        using Fixture f = Prepare();
        var structure = f.Online();
        int division = CorporationStorageRules.FirstFlag + 2;
        long ore = Item(f, structure.Id, 34, 100, flag: division);
        using (WorldActionScope corporation = Scope(f))
        {
            corporation.World.Set<OwnedItem>().Find(ore)!.OwnerId = f.CorporationId;
            corporation.World.Set<SavedCorporation>().Find(f.CorporationId)!.BalanceCents = Budget;
            corporation.Commit();
        }
        var safety = f.Db.Services.GetRequiredService<IPilotAssetSafety>();
        var wrap = safety.Move(f.PilotId, Fixture.SystemId, structure.Id, true)!;
        Assert.Equal(f.CorporationId, wrap.OwnerId);
        Assert.Single(safety.List(f.PilotId, true));
        Assert.Throws<InvalidDataException>(() => safety.List(90000002, true));
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(5));
        safety.Deliver(f.PilotId, wrap.Id, Fixture.SystemId, StationId);
        Dock(f, StationId);
        Assert.Equal(division, Assert.Single(safety.Inventory(f.PilotId, wrap.Id).Items).FlagId);
        var storage = f.Db.Services.GetRequiredService<ICorporationStorage>();
        var office = storage.Rent(f.PilotId, storage.Quote(f.PilotId));
        long balance;
        using (var before = Scope(f))
        {
            balance = before.World.Set<SavedCorporation>().Find(f.CorporationId)!.BalanceCents;
        }
        var changes = storage.Move(f.PilotId, office.Id, new([ore], wrap.Id, division, null));
        Assert.Contains(changes, c => c.Item.Id == ore && c.Item.OwnerId == f.CorporationId && c.Item.FlagId == division);
        using WorldActionScope result = Scope(f);
        Assert.Equal(office.Id, result.World.Set<OwnedItem>().Find(ore)!.LocationId);
        Assert.True(result.World.Set<SavedCorporation>().Find(f.CorporationId)!.BalanceCents < balance);
        Assert.Equal(Budget, result.World.Set<PilotWallet>().Find(f.PilotId)!.Cents);
    }

    [Fact]
    public void UndeliveredWrapAutomaticallyRelocatesOnceAtTheSavedTwentyDayDeadline()
    {
        using Fixture f = Prepare();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            const string origin = "{\"security\":0.0}";
            const string destination = "{\"security\":0.4}";
            const string firstCenter = "{\"center\":{\"x\":0,\"y\":0,\"z\":0}}";
            const string secondCenter = "{\"center\":{\"x\":1000,\"y\":0,\"z\":0}}";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent','30001392',{origin})");
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent','30000142',{destination})"
            );
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30001392',{firstCenter})");
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142',{secondCenter})");
        }
        var structure = f.Online();
        _ = Item(f, structure.Id, 34, 100);
        var safety = f.Db.Services.GetRequiredService<IPilotAssetSafety>();
        var wrap = safety.Move(f.PilotId, Fixture.SystemId, structure.Id, false)!;
        Assert.Equal(60003760, wrap.NearestStation.Id);
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(20) - TimeSpan.FromTicks(1));
        Assert.Single(safety.List(f.PilotId, false));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Empty(safety.List(f.PilotId, false));
        Assert.Empty(safety.List(f.PilotId, false));
        using WorldActionScope result = Scope(f);
        var delivered = result.World.Set<SavedAssetSafety>().Find(wrap.Id)!;
        Assert.Equal(wrap.AutomaticAt, delivered.DeliveredAt);
        Assert.Equal(60003760, result.World.Set<OwnedItem>().Find(wrap.Id)!.LocationId);
        Assert.Equal(Budget, result.World.Set<PilotWallet>().Find(f.PilotId)!.Cents);
    }

    [Fact]
    public void WormholeSafetyEjectsLawfullyLootableContentsInsteadOfCreatingAProtectedWrap()
    {
        using Fixture f = Prepare();
        const int wormhole = 31000001;
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql($"INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES ({wormhole},20000020,'J100001')");
        }
        f.Db.Store.SetPosition(f.ShipId, Fixture.Point, wormhole);
        var structure = f.Online();
        long ore = Item(f, structure.Id, 34, 10);
        long corporateOre = Item(f, structure.Id, 35, 20);
        using (var corporation = Scope(f))
        {
            corporation.World.Set<OwnedItem>().Find(corporateOre)!.OwnerId = f.CorporationId;
            corporation.Commit();
        }
        var safety = f.Db.Services.GetRequiredService<IPilotAssetSafety>();
        Assert.Null(safety.Move(f.PilotId, wormhole, structure.Id, false));
        Assert.Null(safety.Move(f.PilotId, wormhole, structure.Id, true));
        Assert.Empty(safety.List(f.PilotId, false));
        var wrecks = f.Db.Services.GetRequiredService<IWrecks>();
        var contacts = wrecks.List(wormhole, f.Clock.Now);
        Assert.Equal(2, contacts.Count);
        var can = contacts.Single(c => c.OwnerId == f.PilotId);
        Assert.True(can.Abandoned);
        Assert.Equal(AssetSafetyRules.FirstLootContainerId, can.Id);
        using (var metrics = SessionPerformance.Begin("warm structure loot", _ => { }, force: true)!)
        {
            Assert.Same(contacts, wrecks.List(wormhole, f.Clock.Now));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        f.Db.Store.SetPosition(f.ShipId, can.Position, wormhole);
        Assert.Single(wrecks.Read(f.PilotId, can.Id, f.Clock.Now).Items);
        var visitor = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(visitor.ShipId, can.Position, wormhole);
        var taken = wrecks.Take(visitor.PilotId, visitor.ShipId, can.Id, [ore], null, f.Clock.Now);
        Assert.Contains(
            taken,
            change => change.Item.Id == ore && change.Item.LocationId == visitor.ShipId && change.Item.OwnerId == visitor.PilotId
        );
        var corporateCan = contacts.Single(c => c.OwnerId == f.CorporationId);
        taken = wrecks.Take(visitor.PilotId, visitor.ShipId, corporateCan.Id, [corporateOre], null, f.Clock.Now);
        Assert.Contains(taken, change => change.Item.Id == corporateOre && change.Item.OwnerId == visitor.PilotId);
        f.Clock.AdvanceWallTime(TimeSpan.FromHours(2));
        Assert.Empty(wrecks.List(wormhole, f.Clock.Now));
        using var expired = Scope(f);
        Assert.Empty(expired.World.Set<SavedStructureLoot>());
    }

    private static Fixture Prepare()
    {
        var f = new Fixture(liveWorld: true);
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true);
        const string lowSecurity = "{\"security\":0.4}";
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent','30001392',{lowSecurity})");
        using var action = Scope(f);
        var wallet = action.World.Set<PilotWallet>().Find(f.PilotId) ?? action.World.Add(new PilotWallet { PilotId = f.PilotId }).Entity;
        wallet.Cents = Budget;
        action.Commit();
        return f;
    }

    private static WorldActionScope Scope(Fixture f) => new(new(f.Db.Path), new(pilots: [f.PilotId], systems: [Fixture.SystemId]));

    private static long Item(Fixture f, long location, int type, int quantity, bool singleton = false, int flag = (int)InventoryFlag.Hangar)
    {
        using var action = Scope(f);
        long id = InventoryPersistence.NextId(action.World);
        action.World.Add(
            new OwnedItem
            {
                Id = id,
                OwnerId = f.PilotId,
                TypeId = type,
                Quantity = quantity,
                Singleton = singleton,
                LocationId = location,
                FlagId = flag,
            }
        );
        action.Commit();
        return id;
    }

    private static void Dock(Fixture f, int station)
    {
        using var action = Scope(f);
        if (action.World.Positions.Find(f.ShipId) is { } position)
        {
            action.World.Remove(position);
        }
        var ship = action.World.Ships.Find(f.ShipId)!;
        ship.StructureId = null;
        ship.StationId = station;
        action.Commit();
    }
}
