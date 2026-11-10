using Eden.Data.Catalog.Capsuleers;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class TrafficActivityTests
{
    [Fact]
    public void HullGroupsAreChosenBeforeCharactersInARookieHeavySnapshot()
    {
        var profiles = Enumerable
            .Range(1, 1000)
            .Select(i => TrafficSnapshotTests.Profile(i, $"Rookie Pilot {i}") with { KillId = i, HullTypeId = 601 })
            .Concat([
                TrafficSnapshotTests.Profile(2000, "Combat Pilot") with
                {
                    KillId = 2000,
                    HullTypeId = 603,
                },
                TrafficSnapshotTests.Profile(2001, "Cargo Pilot") with
                {
                    KillId = 2001,
                    HullTypeId = 648,
                },
            ])
            .ToArray();
        TrafficSnapshot snapshot = new(null, profiles);
        int Group(TrafficProfile p) =>
            p.HullTypeId == 601 ? 237
            : p.HullTypeId == 603 ? 25
            : 28;
        var selected = Enumerable
            .Range(0, 300)
            .Select(slot => snapshot.Candidates(1, 30000141, slot, 10000002, TrafficSecurityBand.High, hullGroup: Group).First())
            .ToArray();
        foreach (int hull in new[] { 601, 603, 648 })
        {
            Assert.InRange(selected.Count(p => p.HullTypeId == hull), 65, 135);
        }
        Assert.Equal(
            selected.Select(p => p.KillId),
            Enumerable
                .Range(0, 300)
                .Select(slot => snapshot.Candidates(1, 30000141, slot, 10000002, TrafficSecurityBand.High, hullGroup: Group).First().KillId)
        );
    }

    [Fact]
    public void NewSmallRostersDoNotFillWithRookiesWhenTheSnapshotHasNoOtherHull()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        TrafficSnapshotTests.Install(f.Database, TrafficSnapshotTests.Profile() with { HullTypeId = 601, Equipment = [] });
        new CapsuleerProvisioner(f.Database, f.Clock).EnsureSystem(30000141);
        using WorldDbContext world = new(f.Database.Path);
        Assert.Empty(world.Set<SavedTrafficIdentity>());
        var types = (
            from a in world.Set<SavedCapsuleerAgent>()
            join s in world.Ships on a.FounderShipId equals s.Id
            where a.Career == CapsuleerCareer.Hauling
            select s.TypeId
        ).ToArray();
        Assert.NotEmpty(types);
        Assert.DoesNotContain(601, types);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UntouchedRookieCorrectionPreservesIdentityCargoWalletAndGrantAcrossRestart(bool rollback)
    {
        using CapsuleerHaulerTests.Fixture f = new();
        var original = TrafficSnapshotTests.Profile() with { HullTypeId = 601, Equipment = [] };
        var template = PilotSpaceActivitiesTests.CombatProfile with
        {
            CharacterId = original.CharacterId + 1,
            KillId = original.KillId + 1,
            Name = "Another Fit Pilot",
        };
        TrafficSnapshotTests.Install(f.Database, original, template);
        long id = f.Founder().PilotId;
        long shipId;
        long balance;
        long[] cargo;
        using (WorldDbContext world = new(f.Database.Path))
        {
            using CatalogDbContext catalog = new(f.Database.CatalogPath);
            var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == id);
            shipId = pilot.ActiveShipId;
            CapsuleerSupplyStore.Ensure(world, catalog, id, shipId);
            pilot.Name = original.Name;
            pilot.ActiveShip.Name = original.Name;
            TrafficFitting.Apply(world, catalog, id, shipId, original, f.Clock.Now);
            world.Add(
                new SavedTrafficIdentity
                {
                    PilotId = id,
                    CharacterId = original.CharacterId,
                    KillId = original.KillId,
                    SnapshotId = "original",
                }
            );
            world.SaveChanges();
            balance = world.Set<PilotWallet>().Find(id)!.Cents;
            cargo = world
                .Set<OwnedItem>()
                .Where(i => i.LocationId == shipId && i.FlagId == (int)InventoryFlag.Cargo)
                .Select(i => i.Id)
                .ToArray();
        }
        using (WorldDbContext world = new(f.Database.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            using CatalogDbContext catalog = new(f.Database.CatalogPath);
            Assert.True(TrafficCorrection.Apply(f.Database, world, catalog, id, f.Clock.Now));
            Assert.False(TrafficCorrection.Apply(f.Database, world, catalog, id, f.Clock.Now));
            if (!rollback)
            {
                transaction.Commit();
            }
        }
        WorldInitialization.Initialize(f.Database, f.Clock);
        using WorldDbContext saved = new(f.Database.Path);
        var preserved = saved.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == id);
        Assert.Equal(shipId, preserved.ActiveShipId);
        Assert.Equal(original.Name, preserved.Name);
        Assert.Equal(original.Name, preserved.ActiveShip.Name);
        Assert.Equal(rollback ? 601 : 603, preserved.ActiveShip.TypeId);
        Assert.Equal(balance, saved.Set<PilotWallet>().Find(id)!.Cents);
        Assert.Equal(
            cargo,
            saved.Set<OwnedItem>().Where(i => i.LocationId == shipId && i.FlagId == (int)InventoryFlag.Cargo).Select(i => i.Id)
        );
        Assert.Equal(original.KillId, saved.Set<SavedTrafficIdentity>().Find(id)!.KillId);
        Assert.Equal(!rollback, saved.Set<SavedTrafficCorrection>().Any(c => c.PilotId == id));
        Assert.Single(saved.Set<SavedCapsuleerSupplies>().Where(s => s.PilotId == id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DamagedOrCustomizedRookiesAreNotCorrected(bool damaged)
    {
        using CapsuleerHaulerTests.Fixture f = new();
        var original = TrafficSnapshotTests.Profile() with { HullTypeId = 601, Equipment = [] };
        TrafficSnapshotTests.Install(
            f.Database,
            original,
            PilotSpaceActivitiesTests.CombatProfile with
            {
                CharacterId = original.CharacterId + 1,
                KillId = original.KillId + 1,
            }
        );
        using WorldDbContext world = new(f.Database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == f.Founder().PilotId);
        TrafficFitting.Apply(world, catalog, pilot.Id, pilot.ActiveShipId, original, f.Clock.Now);
        pilot.Name = original.Name;
        pilot.ActiveShip.Name = damaged ? original.Name : "Custom ship name";
        world.Add(
            new SavedTrafficIdentity
            {
                PilotId = pilot.Id,
                CharacterId = original.CharacterId,
                KillId = original.KillId,
                SnapshotId = "test",
            }
        );
        if (damaged)
        {
            world.Add(
                new ShipHealthState
                {
                    ShipId = pilot.ActiveShipId,
                    ArmorDamage = 1,
                    UpdatedAt = f.Clock.Now,
                }
            );
        }
        world.SaveChanges();
        Assert.False(TrafficCorrection.Apply(f.Database, world, catalog, pilot.Id, f.Clock.Now));
        Assert.Empty(world.Set<SavedTrafficCorrection>());
        Assert.Equal(601, pilot.ActiveShip.TypeId);
    }
}
