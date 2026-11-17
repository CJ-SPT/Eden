using Eden.Data.World.Capsuleers;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class FreightTests
{
    private const int Home = 30000141;

    internal static void SetLegacyCargo(WorldDbContext world, WorldDatabase database, long pilot, long ship)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        world.Set<OwnedItem>().Where(i => i.LocationId == ship).ExecuteDelete();
        world.Ships.Find(ship)!.TypeId = HaulingRules.BadgerType;
        world.Add(
            new OwnedItem
            {
                Id = ship + 1,
                OwnerId = pilot,
                TypeId = CapsuleerProvisioningCatalog.FreightOre(catalog),
                LocationId = ship,
                FlagId = (int)InventoryFlag.Cargo,
                Quantity = FreightRules.LegacyOreUnits,
            }
        );
        world.SaveChanges();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyDockLoadsOneShipmentAndPreservesUnrelatedCargoAcrossRestart(bool customized)
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        PassiveCatalogFixture.LoadUtilityHulls(f.Database.CatalogPath);
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        Assert.True(Enumerable.Range(0, 64).Select(slot => CapsuleerFreight.Hull(catalog, 1, Home, slot)).Distinct().Count() >= 8);
        new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Home);
        long pilot;
        long ship;
        int expectedHull;
        const long personal = 3_000_000_000_001;
        using (WorldDbContext setup = new(f.Database.Path))
        {
            var founder = setup.Set<SavedCapsuleerHauler>().Single();
            pilot = founder.PilotId;
            ship = founder.FounderShipId;
            SetLegacyCargo(setup, f.Database, pilot, ship);
            if (customized)
            {
                setup.Ships.Find(ship)!.Name = "Keep my ship";
            }
            setup.Add(
                new OwnedItem
                {
                    Id = personal,
                    OwnerId = pilot,
                    LocationId = ship,
                    TypeId = 215,
                    Quantity = 7,
                    FlagId = (int)InventoryFlag.Cargo,
                }
            );
            setup.SaveChanges();
            var work = HaulerCheckpoint.Read(founder.Progress)! with
            {
                Freight = null,
                Activity = HaulerActivity.Docked,
                Navigation = new NavigationState().Capture(),
                DueTicks = 0,
            };
            setup.Database.ExecuteSql($"DELETE FROM WorldPosition WHERE ShipId={ship}");
            new HaulerCheckpoint(pilot, work).Apply(setup);
            expectedHull = customized ? HaulingRules.BadgerType : CapsuleerFreight.Hull(catalog, 1, Home, 0);
        }
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
            using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
            var worker = host.GetRequiredService<CapsuleerHaulerFactory>().Create(pilot);
            var job = scheduler.Jobs().Single(j => j.PilotId == pilot);
            f.Clock.Seconds += 60;
            worker.Execute(job, 60);
            var observed = worker.Read()!;
            Assert.Equal(expectedHull, observed.Pilot.ShipTypeId);
            Assert.Equal(customized, observed.Work.Freight is null);
            if (!customized)
            {
                Assert.NotEmpty(observed.Work.Freight!.Stacks);
            }
            await host.GetRequiredService<LiveWorld>().FlushAsync();
        }
        using WorldDbContext saved = new(f.Database.Path);
        Assert.Equal(7, saved.Set<OwnedItem>().Find(personal)!.Quantity);
        Assert.Equal(expectedHull, saved.Ships.Find(ship)!.TypeId);
        var manifest = HaulerCheckpoint.Read(saved.Set<SavedCapsuleerHauler>().Single().Progress)!.Freight;
        if (customized)
        {
            Assert.Null(manifest);
            Assert.Equal(FreightRules.LegacyOreUnits, saved.Set<OwnedItem>().Find(ship + 1)!.Quantity);
        }
        else
        {
            Assert.Equal(0, manifest!.Sequence);
            Assert.All(manifest.Stacks, s => Assert.Equal(s.Quantity, saved.Set<OwnedItem>().Find(s.ItemId)!.Quantity));
        }
    }

    private sealed class SurvivingFreight : IShipLootRoll
    {
        public bool Survives() => true;
    }

    [Fact]
    public async Task HaulerWreckRetainsTheScannableStacksAcrossCheckpoint()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(Home);
        var founder = f.Founder();
        var manifest = HaulerCheckpoint.Read(founder.Progress)!.Freight!;
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        using (var action = live.Economy.Begin())
        {
            var pilot = action.Pilots.Find(founder.PilotId)!;
            var ship = pilot.ActiveShip;
            ship.Position ??= new FlightPosition { ShipId = ship.Id, SystemId = Home };
            var scanned = action
                .Filter<OwnedItem>(i => i.LocationId == ship.Id)
                .AsEnumerable()
                .Where(i => ShipHoldRules.IsHold(i.FlagId))
                .OrderBy(i => i.Id)
                .Select(i => new FreightStack(i.Id, i.TypeId, i.Quantity));
            Assert.Equal(manifest.Stacks, scanned);
            var attacker = action.Pilots.Find(90000001L)!;
            ShipLossPersistence.Apply(
                action,
                catalog,
                pilot,
                null,
                ship.Id,
                Home,
                ship.Position,
                f.Clock.Now,
                new SurvivingFreight(),
                Eden.World.Characters.PilotInteractionHistoryStore.Player(action, attacker, attacker.ActiveShipId)
            );
            action.SaveChanges();
            action.Commit();
        }
        await live.FlushAsync();
        using WorldDbContext reopened = new(f.Database.Path);
        var wreck = reopened.Set<WorldWreck>().Single(w => w.ShipId == founder.FounderShipId);
        Assert.Equal(
            manifest.Stacks,
            reopened
                .Set<OwnedItem>()
                .Where(i => i.LocationId == wreck.Id)
                .OrderBy(i => i.Id)
                .Select(i => new FreightStack(i.Id, i.TypeId, i.Quantity))
                .ToArray()
        );
    }

    [Fact]
    public void DifferentShipmentsRespectVolumeValueAndReturnTrips()
    {
        FreightCommodity[] goods = [new(1, 2.5, 4), new(2, 12, 100), new(3, .1, 50), new(4, 70, 300)];
        var signatures = new HashSet<string>();
        int empty = 0;
        for (long sequence = 0; sequence < 256; sequence++)
        {
            var items = FreightRules.Load(104, sequence, FreightKind.Bulk, goods, 2000);
            Assert.Equal(items, FreightRules.Load(104, sequence, FreightKind.Bulk, goods, 2000));
            Assert.True(items.Sum(i => goods[i.TypeId - 1].Volume * i.Quantity) <= 2000);
            Assert.True(items.Sum(i => goods[i.TypeId - 1].UnitValue * i.Quantity) <= 750_000);
            Assert.All(items, i => Assert.True(i.Quantity > 0));
            signatures.Add(string.Join(",", items.Select(i => $"{i.TypeId}x{i.Quantity}")));
            if (items.Length == 0)
            {
                empty++;
            }
        }
        Assert.InRange(empty, 1, 100);
        Assert.True(signatures.Count > 100);
    }
}
