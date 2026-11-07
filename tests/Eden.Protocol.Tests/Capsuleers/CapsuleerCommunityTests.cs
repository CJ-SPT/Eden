using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.World.Capsuleers;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerCommunityTests
{
    [Fact]
    public void FreshCommunityGivesItsMinerOrdinaryAssetsOnce()
    {
        var directory = TestDirectory.Create("capsuleer-community-");
        var database = new WorldDatabase(Path.Combine(directory.FullName, "world.sqlite"));
        try
        {
            TestCatalog.Create(database.CatalogPath);
            TestCatalog.AddSchool(database.CatalogPath, 17, 1, 1, 30000141, 60015157, 601);
            var clock = new TestClock();
            WorldInitialization.Initialize(database, clock, economy: new(false, false, CapsuleerSeed: 42));
            var community = new CapsuleerCommunityStore(database);
            var miner = Assert.Single(community.Pilots());
            Assert.Equal((42, 17, CapsuleerCareer.Mining), (miner.Seed, miner.SchoolId, miner.Career));
            long ship;
            using (WorldDbContext world = new(database.Path))
            {
                var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == miner.PilotId);
                Assert.True(pilot.AccountId < 0);
                Assert.False(string.IsNullOrWhiteSpace(pilot.Name));
                Assert.Equal((60015157, 601), (pilot.ActiveShip.StationId, pilot.ActiveShip.TypeId));
                ship = pilot.ActiveShipId;
                Assert.Equal(8000, Assert.Single(world.Set<PilotSkill>().Where(s => s.PilotId == miner.PilotId)).Points);
                var items = world.Set<OwnedItem>().Where(i => i.OwnerId == miner.PilotId).OrderBy(i => i.Id).ToArray();
                Assert.Equal(
                    [InventoryDefinitions.CivilianMinerType, (int)CombatWeaponType.CivilianGatlingRailgun],
                    items.Select(i => i.TypeId)
                );
                Assert.All(items, i => Assert.Equal((60015157L, (int)InventoryFlag.Hangar, 1), (i.LocationId, i.FlagId, i.Quantity)));
                Assert.Equal(0, world.Set<PilotWallet>().Single(w => w.PilotId == miner.PilotId).Cents);
                Assert.Empty(world.Set<TestingWalletGrant>());
            }
            WorldInitialization.Initialize(database, clock);
            Assert.Equal(miner, Assert.Single(community.Pilots()));
            Assert.Throws<InvalidDataException>(() =>
                WorldInitialization.Initialize(database, clock, economy: new(false, false, CapsuleerSeed: 9))
            );
            using WorldDbContext reopened = new(database.Path);
            Assert.Equal(ship, reopened.Pilots.Find(miner.PilotId)!.ActiveShipId);
            Assert.Equal(2, reopened.Set<OwnedItem>().Count(i => i.OwnerId == miner.PilotId));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ExistingWorldHasNoCommunityAndRejectsCreationOverrides()
    {
        using TestWorld db = new();
        var database = new WorldDatabase(db.Path);
        Assert.Empty(new CapsuleerCommunityStore(database).Pilots());
        Assert.Throws<InvalidDataException>(() => WorldInitialization.Initialize(database, economy: new(false, false, CapsuleerSeed: 1)));
        Assert.Throws<InvalidDataException>(() => WorldInitialization.Initialize(database, economy: new(CapsuleerSeed: 1)));
        Assert.Empty(new CapsuleerCommunityStore(database).Pilots());
    }
}
