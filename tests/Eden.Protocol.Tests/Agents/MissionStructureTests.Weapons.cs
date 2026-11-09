using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Protocol.Tests.Combat;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionStructureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissileImpactsUseStructureObjectivesAndReleaseCargoOnlyOnce(bool resident)
    {
        using var f = MissileCombatTests.Prepare(499, 212);
        long launcher = MissileCombatTests.Load(f, 499, 212);
        var target = CombatTarget(f.Db, f.Clock.Now);
        using var live = resident ? new LiveWorld(new(f.Db.Path), f.Clock, _ => { }) : null;
        f.Db.Store.SetPosition(f.ShipId, target.Position + new SpacePosition(1000, 0, 0), 30000144);
        var missiles = f.Db.Services.GetRequiredService<IPilotMissiles>();
        var launch = MissileCombatTests.Launch(f, launcher, target.NpcId);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<OwnedItem>().Where(i => i.TypeId == ObjectiveType));
        }
        f.Clock.Seconds += 2;
        missiles.Advance(30000144, f.Clock.Now);
        Assert.Single(missiles.TakeImpacts(f.PilotId));
        Assert.True(missiles.Impacted(launch.Projectile.Id));
        AssertDestroyed(f.Db, target);
        missiles.Advance(30000144, f.Clock.Now);
        Assert.Empty(missiles.TakeImpacts(f.PilotId));
        AssertDestroyed(f.Db, target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualDroneOrdersCanDestroyStructuresAndSettleTheirMissionDrop(bool resident)
    {
        using CombatDroneTests.Fixture f = new();
        var target = CombatTarget(f.Base.Db, f.Now, f.Npc.Position);
        using var live = resident ? new LiveWorld(new(f.Base.Db.Path), f.Base.Clock, _ => { }) : null;
        var ids = f.Launch(quantity: 1);
        f.Drones.Order(f.Pilot, ids, DroneOrder.Engage, target.NpcId, new HashSet<long> { target.NpcId }, f.Now);
        for (int tick = 0; tick < 30; tick++)
        {
            f.Tick();
            using WorldDbContext world = new(f.Base.Db.Path);
            if (world.Set<SavedMissionStructure>().Find(target.NpcId)!.DestroyedAt.HasValue)
            {
                break;
            }
        }
        AssertDestroyed(f.Base.Db, target);
        f.Tick();
        AssertDestroyed(f.Base.Db, target);
    }

    private sealed record StructureCombatTarget(long MissionId, long NpcId, SpacePosition Position);

    private static StructureCombatTarget CombatTarget(TestWorld db, long now, SpacePosition? targetPosition = null)
    {
        string catalogPath = new WorldDatabase(db.Path).CatalogPath;
        PassiveCatalogFixture.LoadMissions(catalogPath);
        using WorldDbContext world = new(db.Path);
        using CatalogDbContext catalog = new(catalogPath);
        using var transaction = world.Database.BeginTransaction();
        var profile = MissionCatalog.Find(catalog, HiddenStash);
        var source = profile.Rooms.SelectMany(r => r.Objects).Single(o => o.TypeId == WarehouseType);
        var position = targetPosition ?? SpacePosition.Zero;
        var origin = position - source.Position;
        var mission = world
            .Add(
                new SavedMission
                {
                    PilotId = MissionFixture.PilotId,
                    AgentId = MissionFixture.AgentId,
                    MissionId = HiddenStash,
                    Level = 1,
                    State = MissionState.Accepted,
                    OfferedAt = now,
                    AcceptedAt = now,
                    ExpiresAt = now + MissionRules.MissionLifetime.Ticks,
                    DestinationStation = MissionFixture.StationId,
                    SystemId = 30000144,
                    X = origin.X,
                    Y = origin.Y,
                    Z = origin.Z,
                }
            )
            .Entity;
        world.SaveChanges();
        PilotMissionStore.SeedStructures(world, catalog, mission, profile, now);
        world.SaveChanges();
        var npc = world.Set<WorldNpc>().Single(n => n.EncounterId == MissionRules.FirstSiteId + mission.Id && n.TypeId == WarehouseType);
        var defense = new NpcProfileCatalog().Read(catalog, WarehouseType).Defense;
        npc.ShieldCharge = 0;
        npc.ArmorDamage = defense.Armor;
        npc.StructureDamage = defense.Structure - 0.5;
        world.SaveChanges();
        transaction.Commit();
        return new(mission.Id, npc.Id, position);
    }

    private static void AssertDestroyed(TestWorld db, StructureCombatTarget target)
    {
        using WorldDbContext world = new(db.Path);
        Assert.NotNull(world.Set<WorldNpc>().Find(target.NpcId)!.DestroyedAt);
        Assert.NotNull(world.Set<SavedMissionStructure>().Find(target.NpcId)!.DestroyedAt);
        Assert.True(world.Set<SavedMission>().Find(target.MissionId)!.ObjectiveComplete);
        var cargo = Assert.Single(world.Set<OwnedItem>(), i => i.TypeId == ObjectiveType);
        Assert.Equal(MissionContainerRules.Id(target.MissionId, 0), cargo.LocationId);
        Assert.Equal(15, cargo.Quantity);
        Assert.Equal(MissionFixture.PilotId, cargo.OwnerId);
    }
}
