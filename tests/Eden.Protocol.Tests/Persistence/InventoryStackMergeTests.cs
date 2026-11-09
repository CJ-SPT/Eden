using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class InventoryStackMergeTests
{
    [Theory]
    [InlineData(InventoryFlag.Hangar, false, false)]
    [InlineData(InventoryFlag.Hangar, true, false)]
    [InlineData(InventoryFlag.Cargo, false, false)]
    [InlineData(InventoryFlag.Cargo, true, false)]
    [InlineData(InventoryFlag.Cargo, false, true)]
    [InlineData(InventoryFlag.Cargo, true, true)]
    public void NativeMultiMergeRetainsChosenTargetAndPublishesPersistentQuantities(InventoryFlag flag, bool partial, bool inSpace)
    {
        using Fixture f = new(flag, inSpace);
        int quantity = partial ? 30 : 80;
        Assert.IsType<PilotCallResult.Success>(f.Merge(quantity));
        var changes = f.Context.Notifications.OfType<ItemChanged>().ToArray();
        Assert.Equal(2, changes.Length);
        var target = Assert.Single(changes, change => change.Item.Id == f.Target);
        Assert.Equal(560 + quantity, target.Item.Quantity);
        Assert.Equal(560, target.Previous[InventoryColumn.Quantity]);
        var source = Assert.Single(changes, change => change.Item.Id == f.Source);
        Assert.Equal(partial ? f.Location : (long)InventoryLocation.Graveyard, source.Item.LocationId);
        Assert.Equal(partial ? 50 : 80, source.Item.Quantity);
        Assert.Equal(partial ? 80 : f.Location, source.Previous[partial ? InventoryColumn.Quantity : InventoryColumn.Location]);
        _ = WorldInitialization.Open(f.Db.Path);
        var saved = f.Snapshot();
        Assert.Equal(560 + quantity, saved.Single(item => item.Id == f.Target).Quantity);
        Assert.Equal(11, saved.Single(item => item.Id == f.Unselected).Quantity);
        Assert.Equal(partial ? 50 : 0, saved.Where(item => item.Id == f.Source).Sum(item => item.Quantity));
        Assert.Equal(
            651,
            saved.Where(item => item.Id == f.Source || item.Id == f.Target || item.Id == f.Unselected).Sum(item => item.Quantity)
        );
        Assert.All(
            saved.Where(item => item.Id == f.Target || item.Id == f.Source),
            item =>
            {
                Assert.Equal(Fixture.UnitVolume, item.Volume);
                Assert.Equal(Fixture.Wear, item.CrystalDamage);
            }
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BatchMergeMovesDockedStacksAtomicallyAndSuppressesIntermediateRows(bool partial)
    {
        using Fixture f = new(InventoryFlag.Cargo);
        f.Edit(
            f.Source,
            item =>
            {
                item.LocationId = f.Station;
                item.FlagId = (int)InventoryFlag.Hangar;
            }
        );
        long second = f.Add(5, f.Station, InventoryFlag.Hangar);
        int quantity = partial ? 30 : 80;
        Assert.IsType<PilotCallResult.Success>(
            f.Call(
                f.Service,
                [
                    new List<object?> { new object?[] { f.Source, f.Target, (long)quantity }, new object?[] { second, f.Target, 5L } },
                    f.Station,
                ]
            )
        );
        Assert.Equal(3, f.Context.Notifications.OfType<ItemChanged>().Count());
        Assert.All(
            f.Context.Notifications.OfType<ItemChanged>(),
            change => Assert.Contains(change.Item.Id, new[] { f.Source, second, f.Target })
        );
        _ = WorldInitialization.Open(f.Db.Path);
        var saved = f.Snapshot();
        Assert.Equal(565 + quantity, saved.Single(item => item.Id == f.Target).Quantity);
        Assert.DoesNotContain(saved, item => item.Id == second);
        Assert.Equal(partial ? 50 : 0, saved.Where(item => item.Id == f.Source).Sum(item => item.Quantity));
        Assert.Equal(11, saved.Single(item => item.Id == f.Unselected).Quantity);
    }

    [Theory]
    [InlineData("type", false)]
    [InlineData("foreign", false)]
    [InlineData("foreign", true)]
    [InlineData("singleton", false)]
    [InlineData("singleton", true)]
    [InlineData("online", false)]
    [InlineData("online", true)]
    [InlineData("volume", false)]
    [InlineData("wear", false)]
    [InlineData("contents", false)]
    [InlineData("contents", true)]
    [InlineData("installed", false)]
    [InlineData("installed", true)]
    [InlineData("flag", false)]
    [InlineData("remote", false)]
    [InlineData("remote", true)]
    [InlineData("missing", false)]
    [InlineData("missing", true)]
    public void IneligibleSourcesAndTargetsRejectWithoutChangingInventory(string failure, bool target)
    {
        using Fixture f = new(InventoryFlag.Hangar);
        long id = target ? f.Target : f.Source;
        f.Edit(
            id,
            item =>
            {
                switch (failure)
                {
                    case "type":
                        item.TypeId = 3638;
                        break;
                    case "foreign":
                        item.OwnerId = 90000002;
                        break;
                    case "singleton":
                        item.Singleton = true;
                        break;
                    case "online":
                        item.Online = true;
                        break;
                    case "volume":
                        item.VolumeOverride = 5;
                        break;
                    case "wear":
                        item.CrystalDamage = 0.5;
                        break;
                    case "flag":
                        item.FlagId = (int)InventoryFlag.HighSlot0;
                        break;
                    case "remote":
                        item.LocationId = f.Station + 1;
                        break;
                }
            }
        );
        if (failure == "contents")
        {
            f.Add(1, id, InventoryFlag.Cargo);
        }
        if (failure is "installed" or "missing")
        {
            using WorldDbContext world = new(f.Db.Path);
            if (failure == "missing")
            {
                world.Remove(world.Set<OwnedItem>().Find(id)!);
            }
            else
            {
                world.Add(
                    new IndustryJob
                    {
                        PilotId = Fixture.Owner,
                        BlueprintId = id,
                        StationId = (int)f.Station,
                        Status = IndustryJobStatus.Installed,
                        Runs = 1,
                        OutputQuantity = 1,
                        DurationSeconds = 1,
                        StartTime = 1,
                        EndTime = 2,
                    }
                );
            }
            world.SaveChanges();
        }
        var before = f.Snapshot();
        Assert.IsType<PilotCallResult.Failure>(f.Merge(80));
        Assert.Equal(before, f.Snapshot());
        Assert.Empty(f.Context.Notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterOverflowRollsBackTheWholeMergeBatchIncludingEarlierTransfers(bool transfer)
    {
        using Fixture f = new(InventoryFlag.Cargo);
        long sourceLocation = transfer ? f.Station : f.Location;
        InventoryFlag sourceFlag = transfer ? InventoryFlag.Hangar : InventoryFlag.Cargo;
        f.Edit(
            f.Source,
            item =>
            {
                item.LocationId = sourceLocation;
                item.FlagId = (int)sourceFlag;
            }
        );
        long second = f.Add(1, sourceLocation, sourceFlag);
        long overflowTarget = f.Add(int.MaxValue, f.Location, InventoryFlag.Cargo);
        f.Edit(second, item => item.VolumeOverride = 0);
        f.Edit(overflowTarget, item => item.VolumeOverride = 0);
        var before = f.Snapshot();
        var failure = Assert.IsType<PilotCallResult.Failure>(
            f.Call(
                f.Service,
                [
                    new List<object?> { new object?[] { f.Source, f.Target, 80L }, new object?[] { second, overflowTarget, 1L } },
                    sourceLocation,
                ]
            )
        );
        Assert.Equal("The combined stack exceeds the supported quantity.", failure.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Empty(f.Context.Notifications);
    }

    [Fact]
    public void MalformedDuplicateAndChainedOperationsFailWithoutNotifications()
    {
        using Fixture f = new(InventoryFlag.Cargo);
        object?[] operation = [f.Source, f.Target, 80L];
        object?[][] requests =
        [
            [],
            [new List<object?>()],
            [new List<object?>(), f.Location],
            [operation, f.Location],
            [new List<object?> { new List<object?>(operation) }, f.Location],
            [new List<object?> { new object?[] { f.Source, f.Target } }, f.Location],
            [new List<object?> { new object?[] { f.Source, f.Target, 0L } }, f.Location],
            [new List<object?> { new object?[] { f.Source, f.Target, -1L } }, f.Location],
            [new List<object?> { new object?[] { f.Source, f.Target, 81L } }, f.Location],
            [new List<object?> { new object?[] { f.Source, f.Target, long.MaxValue } }, f.Location],
            [new List<object?> { new object?[] { f.Source, f.Target, 1d } }, f.Location],
            [new List<object?> { new object?[] { f.Source, f.Source, 1L } }, f.Location],
            [new List<object?> { operation, operation }, f.Location],
            [new List<object?> { operation, new object?[] { f.Target, f.Unselected, 1L } }, f.Location],
            [new List<object?> { operation }, f.Location + 1],
        ];
        var before = f.Snapshot();
        foreach (var args in requests)
        {
            Assert.IsType<PilotCallResult.Failure>(f.Call(f.Service, args));
        }
        Assert.IsType<PilotCallResult.Failure>(f.Call(f.Service, [new List<object?> { operation }, f.Location], new() { ["flag"] = 5L }));
        Assert.IsType<PilotCallResult.Failure>(f.Call($"@ship:{f.Location + 1}", [new List<object?> { operation }, f.Location]));
        Assert.Equal(before, f.Snapshot());
        Assert.Empty(f.Context.Notifications);
    }

    [Fact]
    public void SameHoldMergeNeedsNoExtraCapacityButCrossContainerMergeChecksCapacityAndDocking()
    {
        using Fixture f = new(InventoryFlag.Cargo);
        f.Edit(f.Source, item => item.VolumeOverride = 5);
        f.Edit(f.Target, item => item.VolumeOverride = 5);
        var before = f.Snapshot();
        f.Edit(
            f.Source,
            item =>
            {
                item.LocationId = f.Station;
                item.FlagId = (int)InventoryFlag.Hangar;
            }
        );
        var crossBefore = f.Snapshot();
        Assert.IsType<PilotCallResult.NamedFailure>(
            f.Call(f.Service, [new List<object?> { new object?[] { f.Source, f.Target, 1L } }, f.Station])
        );
        Assert.Equal(crossBefore, f.Snapshot());
        f.Edit(
            f.Source,
            item =>
            {
                item.LocationId = f.Location;
                item.FlagId = (int)InventoryFlag.Cargo;
            }
        );
        Assert.Equal(before, f.Snapshot());
        Assert.IsType<PilotCallResult.Success>(f.Merge(80));
        using Fixture space = new(InventoryFlag.Cargo, true);
        space.Edit(
            space.Source,
            item =>
            {
                item.LocationId = space.Station;
                item.FlagId = (int)InventoryFlag.Hangar;
            }
        );
        var spaceBefore = space.Snapshot();
        Assert.IsType<PilotCallResult.Failure>(
            space.Call(space.Service, [new List<object?> { new object?[] { space.Source, space.Target, 1L } }, space.Station])
        );
        Assert.Equal(spaceBefore, space.Snapshot());
        Assert.Empty(space.Context.Notifications);
    }

    [Fact]
    public void TypedMergeRulesConservePartialQuantitiesAndRejectOverflow()
    {
        InventoryItem source = new(1, 3651, Fixture.Owner, 60003760, (int)InventoryFlag.Hangar, 80, 54, 7, 5);
        var target = source with { Id = 2, Quantity = 560 };
        Assert.Equal(new InventoryStackMergeQuantities(50, 590), InventoryStackRules.Merge(source, target, 30));
        Assert.Throws<InvalidDataException>(() => InventoryStackRules.Merge(source, target, 81));
        Assert.Throws<OverflowException>(() => InventoryStackRules.Merge(source, target with { Quantity = int.MaxValue }, 1));
    }

    private sealed class Fixture : IDisposable
    {
        internal const long Owner = 90000001;
        internal const double UnitVolume = 0.0025;
        internal const double Wear = 0.125;
        internal TestWorld Db { get; } = new();
        internal long Location { get; }
        internal long Station { get; }
        internal long Source { get; }
        internal long Target { get; }
        internal long Unselected { get; }
        internal string Service { get; }
        internal PilotCallContext Context { get; }
        private readonly IServiceScope _scope;

        internal Fixture(InventoryFlag flag, bool inSpace = false)
        {
            var pilot = Db.Store.FindPilot(1000001)!;
            Location = flag == InventoryFlag.Hangar ? pilot.StationId : pilot.ShipId;
            Station = pilot.StationId;
            Service = flag == InventoryFlag.Hangar ? "@hangar" : $"@ship:{pilot.ShipId}";
            Source = Add(80, Location, flag);
            Target = Add(560, Location, flag);
            Unselected = Add(11, Location, flag);
            if (inSpace)
            {
                Db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero);
                pilot = Db.Store.FindPilot(1000001)!;
            }
            _scope = Db.Services.CreateScope();
            Context = new(
                1000001,
                pilot.Account,
                [pilot],
                p => new(Db.Store, p, TimeProvider.System, new(), Db.Objects),
                42,
                SignedSettings.Empty
            );
            Context.Select(pilot);
        }

        internal long Add(int quantity, long location, InventoryFlag flag)
        {
            using WorldDbContext world = new(Db.Path);
            long id = InventoryPersistence.NextId(world);
            world.Add(
                new OwnedItem
                {
                    Id = id,
                    OwnerId = Owner,
                    TypeId = InventoryDefinitions.CivilianMinerType,
                    LocationId = location,
                    FlagId = (int)flag,
                    Quantity = quantity,
                    VolumeOverride = UnitVolume,
                    CrystalDamage = Wear,
                }
            );
            world.SaveChanges();
            return id;
        }

        internal void Edit(long id, Action<OwnedItem> change)
        {
            using WorldDbContext world = new(Db.Path);
            change(world.Set<OwnedItem>().Find(id)!);
            world.SaveChanges();
        }

        internal InventoryItem[] Snapshot()
        {
            using WorldDbContext world = new(Db.Path);
            using CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath);
            return world
                .Set<OwnedItem>()
                .OrderBy(item => item.Id)
                .AsEnumerable()
                .Select(item => PilotInventoryStore.Read(catalog, item))
                .ToArray();
        }

        internal PilotCallResult Merge(int quantity) =>
            Call(Service, [new List<object?> { new object?[] { Source, Target, (long)quantity } }, Location]);

        internal PilotCallResult Call(string service, object?[] args, Dictionary<string, object?>? keywords = null) =>
            _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, service, "MultiMerge", BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
            Db.Dispose();
        }
    }
}
