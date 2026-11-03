using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CorvetteRecoveryTests
{
    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; } = new();
        public IPilotFleet Fleet => Db.Services.GetRequiredService<IPilotFleet>();
        public DockedPilot Pilot => Db.Store.FindPilot(1000001)!;
        public DockedPilot Venture { get; }

        public Fixture()
        {
            PilotFleetTests.AddVenture(Db);
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (50,'cpu'),(30,'power');
                    INSERT OR REPLACE INTO TypeAttribute VALUES (601,14,2),(601,48,125),(601,11,23),
                        (3651,50,65),(3651,30,2),(3638,50,4),(3638,30,6);
                    """
                );
            }
            using (WorldDbContext world = new(Db.Path))
            {
                world.Add(new PilotWallet { PilotId = Pilot.PilotId, Cents = 100 });
                world.SaveChanges();
            }
            var original = Pilot;
            var purchase = Assert
                .Single(
                    Db.Services.GetRequiredService<IPilotMarket>()
                        .Buy(Pilot.PilotId, Pilot.StationId, [new((int)StarterShipType.Venture, 1000000, 1)])
                        .Changes
                )
                .Item;
            long ship = Assert.Single(Fleet.Assemble(Pilot.PilotId, [purchase.Id], "Working Venture").ShipIds);
            Venture = Fleet.Board(Pilot.PilotId, ship, original.ShipId);
        }

        public ShipBoarding Recover() => Fleet.RecoverCorvette(Venture.PilotId, Venture.ShipId, Venture.StationId);

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void RecoveryAtZeroIskBoardsAnEquippedCorvetteAndPreservesThePreviousShip()
    {
        using Fixture f = new();
        var inventory = f.Db.Services.GetRequiredService<IPilotInventory>();
        var cargo = inventory.List(f.Venture.PilotId, f.Venture.StationId).Single(i => i.TypeId == InventoryDefinitions.CivilianMinerType);
        inventory.Move(f.Venture.PilotId, [cargo.Id], f.Venture.StationId, f.Venture.ShipId, (int)InventoryFlag.Cargo, null);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new ShipHealthState
                {
                    ShipId = f.Venture.ShipId,
                    ShieldCharge = 1,
                    ArmorDamage = 10,
                    StructureDamage = 20,
                }
            );
            world.SaveChanges();
        }
        var recovery = f.Recover();
        Assert.Equal(601, recovery.Pilot.ShipTypeId);
        Assert.Equal(FleetDefinitions.CorvetteGroup, recovery.Pilot.ShipGroupId);
        Assert.NotEqual(f.Venture.ShipId, recovery.Pilot.ShipId);
        Assert.Equal(3, recovery.Changes.Count);
        var modules = inventory.List(f.Pilot.PilotId, recovery.Pilot.ShipId);
        Assert.Equal(
            new[] { InventoryDefinitions.CivilianMinerType, (int)CombatWeaponType.CivilianGatlingRailgun },
            modules.OrderBy(i => i.FlagId).Select(i => i.TypeId)
        );
        Assert.All(
            modules,
            m =>
            {
                Assert.True(m.Singleton);
                Assert.True(m.Online);
            }
        );
        Assert.Equal(cargo.Id, Assert.Single(inventory.List(f.Pilot.PilotId, f.Venture.ShipId)).Id);
        Assert.Equal("Working Venture", f.Fleet.Name(f.Pilot.PilotId, f.Venture.ShipId));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(0, saved.Set<PilotWallet>().Single().Cents);
        Assert.Equal(20, saved.Set<ShipHealthState>().Single().StructureDamage);
        Assert.Empty(saved.Set<ShipHealthState>().Where(h => h.ShipId == recovery.Pilot.ShipId));
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(recovery.Pilot.ShipId, f.Pilot.ShipId);
        Assert.Empty(f.Recover().Changes);
    }

    [Fact]
    public async Task CompetingRequestsAndLaterReuseDoNotGenerateAdditionalShipsOrEquipment()
    {
        using Fixture f = new();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(f.Recover)));
        Assert.Equal(results[0].Pilot.ShipId, results[1].Pilot.ShipId);
        Assert.Single(results, r => r.Changes.Count == 3);
        long recovered = results[0].Pilot.ShipId;
        f.Fleet.Board(f.Pilot.PilotId, f.Venture.ShipId, recovered);
        Assert.Equal(recovered, f.Recover().Pilot.ShipId);
        Assert.Empty(f.Recover().Changes);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(3, saved.Ships.Count(s => s.OwnerId == f.Pilot.PilotId));
        Assert.Equal(4, saved.Set<OwnedItem>().Count(i => i.OwnerId == f.Pilot.PilotId));
        Assert.True(InventoryPersistence.NextId(saved) > saved.Set<OwnedItem>().Max(i => i.Id));
    }

    [Fact]
    public void RejectsInSpaceRemoteStationStaleShipAndAlreadyAboardRequests()
    {
        using Fixture f = new();
        Assert.Throws<InvalidDataException>(() => f.Fleet.RecoverCorvette(f.Pilot.PilotId, long.MaxValue, f.Pilot.StationId));
        Assert.Throws<InvalidDataException>(() => f.Fleet.RecoverCorvette(f.Pilot.PilotId, f.Venture.ShipId, f.Venture.StationId + 1));
        f.Db.Store.SetPosition(f.Venture.ShipId, SpacePosition.Zero, f.Venture.SystemId);
        Assert.Throws<InvalidDataException>(() => f.Recover());
        f.Db.Store.SetPosition(f.Venture.ShipId, null);
        var recovery = f.Recover();
        Assert.Throws<InvalidDataException>(() =>
            f.Fleet.RecoverCorvette(f.Pilot.PilotId, recovery.Pilot.ShipId, recovery.Pilot.StationId)
        );
        Assert.Throws<InvalidDataException>(() => f.Fleet.RecoverCorvette(90000002, f.Venture.ShipId, f.Venture.StationId));
    }

    [Fact]
    public void UnsupportedRecoveryLoadoutRollsBackWithoutChangingTheActiveShipOrWallet()
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=1 WHERE ItemTypeId=601 AND AttributeId=48");
        }
        Assert.Throws<InvalidDataException>(() => f.Recover());
        Assert.Equal(f.Venture.ShipId, f.Pilot.ShipId);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(2, saved.Ships.Count(s => s.OwnerId == f.Pilot.PilotId));
        Assert.Equal(2, saved.Set<OwnedItem>().Count(i => i.OwnerId == f.Pilot.PilotId));
        Assert.Equal(0, saved.Set<PilotWallet>().Single().Cents);
    }

    [Fact]
    public void NativeRecoveryCallIsSelectedPilotOnlyAndReturnsNoneAfterChangingShips()
    {
        using Fixture f = new();
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            f.Venture.Account,
            [f.Venture],
            p => new(f.Db.Store, p, TimeProvider.System, new(), f.Db.Objects),
            42,
            SignedSettings.Empty
        );
        object? Call() =>
            router.CallWire(
                context,
                "dogmaIM",
                "CreateNewbieShip",
                new object?[] { f.Venture.ShipId, (long)f.Venture.StationId },
                _ => { }
            );
        Assert.Same(PilotCallResult.Unsupported, Call());
        context.Select(f.Venture);
        Assert.Null(Call());
        Assert.Equal(601, context.Pilot.ShipTypeId);
        Assert.Equal(3, context.Notifications.Count(n => n.Name == "OnItemChange"));
        Assert.All(
            context.Notifications.Where(n => n.Name == "OnItemChange"),
            n =>
            {
                var previous = Assert.IsType<Dictionary<long, object?>>(n.Arguments[1]);
                Assert.DoesNotContain((long)InventoryColumn.Quantity, previous.Keys);
                Assert.Equal(0, previous[(long)InventoryColumn.StackSize]);
            }
        );
        Assert.Null(Call());
        Assert.Equal(3, context.Notifications.Count(n => n.Name == "OnItemChange"));
    }
}
