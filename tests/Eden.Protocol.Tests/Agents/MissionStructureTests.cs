using Eden.Data.World.Agents;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Exploration;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionStructureTests
{
    private const int HiddenStash = 1226;
    private const int ObjectiveType = 16044;
    private const int WarehouseType = 17787;

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, true)]
    public void ObjectiveRulesRequireBothEnemiesAndRequiredStructuresUnlessAnExplicitCompletionTargetIsDestroyed(
        bool enemiesComplete,
        bool destroyed,
        bool completesMission,
        bool expected
    )
    {
        MissionStructureProgress[] structures = [new(1, true, completesMission, destroyed), new(2, false, false, false)];
        Assert.Equal(expected, MissionStructureRules.Complete(enemiesComplete, structures));
        Assert.True(MissionStructureRules.Complete(true, []));
        Assert.False(MissionStructureRules.Complete(false, []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TurretsLockAndDamageStructuresAndFatalSettlementRollsBackWithoutLootOrProgress(bool resident)
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision, live: resident, configure: GunCatalog, combatScene: true);
        var mission = Hidden(f);
        var warehouse = Assert.Single(Structures(f, mission), n => n.TypeId == WarehouseType);
        var inventory = f.Services.GetRequiredService<IPilotInventory>();
        var fitting = f.Services.GetRequiredService<IPilotFitting>();
        long gun = Assert
            .Single(
                inventory.List(MissionFixture.PilotId, MissionFixture.StationId),
                i => i.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun
            )
            .Id;
        fitting.Fit(MissionFixture.PilotId, [gun], MissionFixture.StationId, (int)InventoryFlag.AutoFit);
        fitting.SetOnline(MissionFixture.PilotId, f.Context.Pilot.ShipId, gun, true);
        Move(f, mission.SystemId, warehouse.Position + new SpacePosition(1000, 0, 0));
        var targeting = f.Handler<TargetingHandler>();
        Assert.IsType<PilotCallResult.Success>(f.Call("dogmaIM", "AddTarget", [warehouse.Id]));
        f.Clock.Seconds += 30;
        targeting.Advance(f.Context);
        Assert.True(targeting.IsLocked(warehouse.Id));
        var combat = f.Services.GetRequiredService<IPilotCombat>();
        CombatShot Fire() =>
            combat.Fire(MissionFixture.PilotId, gun, warehouse.Id, SpacePosition.Zero, f.Clock.Now, 0.5, WeaponActivation.Laser);
        Assert.True(Fire().Applied > 0);
        f.Clock.Seconds += 2;
        var encounters = f.Services.GetRequiredService<INpcEncounters>();
        _ = encounters.List(mission.SystemId, f.Clock.Now);
        var sites = f.Missions.Sites(mission.SystemId);
        if (resident)
        {
            using var measure = SessionPerformance.Begin("warm mission structure damage", _ => { }, force: true)!;
            var shot = Fire();
            Assert.True(shot.Applied > 0);
            var current = Assert.Single(encounters.List(mission.SystemId, f.Clock.Now), n => n.Id == warehouse.Id);
            Assert.Equal(shot.Health.Remaining, current.Health.Remaining);
            Assert.Same(sites, f.Missions.Sites(mission.SystemId));
            Assert.Equal(0, measure.Commands);
            Assert.Equal(0, measure.Connections);
            Assert.Equal(0, measure.FittingCalculations);
        }
        else
        {
            Assert.True(Fire().Applied > 0);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            var target = world.Set<WorldNpc>().Find(warehouse.Id)!;
            target.ShieldCharge = 0;
            target.ArmorDamage = warehouse.Health.Defense.Armor;
            target.StructureDamage = warehouse.Health.Defense.Structure - 0.5;
            world.SaveChanges();
            transaction.Commit();
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER Reject_Structure_Death BEFORE UPDATE ON WorldMissionStructure
                    BEGIN SELECT RAISE(ABORT,'injected structure death failure'); END;
                """
            );
        }
        f.Clock.Seconds += 2;
        Assert.Throws<DbUpdateException>(() => Fire());
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Null(world.Set<WorldNpc>().Find(warehouse.Id)!.DestroyedAt);
            Assert.Null(world.Set<SavedMissionStructure>().Find(warehouse.Id)!.DestroyedAt);
            Assert.Empty(world.Set<OwnedItem>().Where(i => i.TypeId == ObjectiveType));
            Assert.False(world.Set<SavedMission>().Find(mission.Id)!.ObjectiveComplete);
            world.Database.ExecuteSqlRaw("DROP TRIGGER Reject_Structure_Death");
        }
        Assert.True(Fire().Destroyed);
        Assert.True(Current(f).CombatComplete);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(15, world.Set<OwnedItem>().Where(i => i.TypeId == ObjectiveType).Sum(i => i.Quantity));
            Assert.Empty(world.Set<NpcAggression>().Where(a => a.NpcId == warehouse.Id));
            Assert.Empty(world.Set<NpcBounty>().Where(b => b.NpcId == warehouse.Id));
        }
        if (resident)
        {
            await LiveWorld.Find(f.Db.Path)!.FlushAsync();
            f.StopResident();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.DoesNotContain(Structures(f, mission), n => n.Id == warehouse.Id);
    }

    private static void GunCatalog(CatalogDbContext catalog)
    {
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (6,'capacitorNeed'),(14,'hiSlots'),
                (48,'cpuOutput'),(11,'powerOutput'),(76,'maxTargetRange'),(192,'maxLockedTargets'),(564,'scanResolution');
            INSERT OR REPLACE INTO TypeAttribute VALUES (601,14,2),(601,48,125),(601,11,23),
                (3638,6,0),(3638,51,1000),(3638,54,10000),(3638,64,1),(3638,117,1),
                (3638,158,3000),(3638,160,500),(3638,620,40),(601,76,50000),(601,192,4),(601,564,500);
            """
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenStashWarehouseDestructionReleasesPersistentCargoAndCompletesOnlyAfterRetrieval(bool resident)
    {
        using MissionFixture f = new(
            division: MissionRules.SecurityDivision,
            live: resident,
            configure: c => c.Database.ExecuteSqlRaw("UPDATE ItemType SET Capacity=200 WHERE Id=601"),
            combatScene: true
        );
        var mission = Hidden(f);
        var targets = Structures(f, mission);
        Assert.Equal(5, targets.Length);
        var warehouse = Assert.Single(targets, n => n.TypeId == WarehouseType);
        Assert.Equal("Warehouse", warehouse.Name);
        Assert.Equal(NpcRole.MissionStructure, warehouse.Role);
        Assert.Equal(0, warehouse.MaxSpeed);
        Assert.Null(warehouse.Navigation);
        long recoveryId = MissionContainerRules.Id(mission.Id, 0);
        var containers = f.Services.GetRequiredService<IPilotMissionContainers>();
        var inner = f.Missions.Sites(mission.SystemId).Single(s => s.Position != mission.Position);
        f.Missions.Arrive(mission.SystemId, inner.Position);
        Move(f, mission.SystemId, warehouse.Position);
        RefreshScene(f);
        Assert.Contains(f.Context.Flight.VisibleNpcs, n => n.Id == warehouse.Id);
        Assert.DoesNotContain(
            f.Missions.Sites(mission.SystemId).SelectMany(s => s.Objects),
            o => targets.Any(t => t.Id == o.Id) || MissionContainerRules.IsContainer(o.Id)
        );
        Assert.Throws<InvalidDataException>(() => containers.Read(MissionFixture.PilotId, recoveryId));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(12, world.Set<SavedMissionMember>().Count(m => m.MissionId == mission.Id));
            Assert.Empty(world.Set<OwnedItem>().Where(i => i.TypeId == ObjectiveType));
        }
        f.Kill(warehouse.Id, commit: false);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Null(world.Set<SavedMissionStructure>().Find(warehouse.Id)!.DestroyedAt);
            Assert.Null(world.Set<WorldNpc>().Find(warehouse.Id)!.DestroyedAt);
            Assert.Empty(world.Set<OwnedItem>().Where(i => i.TypeId == ObjectiveType));
        }
        Assert.False(Current(f).CombatComplete);
        f.Kill(warehouse.Id);
        Assert.True(Current(f).CombatComplete);
        Assert.False(Current(f).ObjectiveComplete);
        RefreshScene(f);
        Assert.DoesNotContain(f.Context.Flight.VisibleNpcs, n => n.Id == warehouse.Id);
        var recovery = Assert.Single(f.Missions.Sites(mission.SystemId).SelectMany(s => s.Objects), o => o.Id == recoveryId);
        Assert.Equal(MissionStructureRules.RecoveryContainerType, recovery.TypeId);
        Assert.Equal(new SpaceContainerState(MissionFixture.PilotId, false), recovery.Container);
        var cargo = Assert.Single(containers.Read(MissionFixture.PilotId, recoveryId).Items);
        Assert.Equal(ObjectiveType, cargo.TypeId);
        Assert.Equal(15, cargo.Quantity);
        Assert.Equal(MissionFixture.PilotId, cargo.OwnerId);
        Assert.Equal(10, cargo.Volume);
        Assert.Throws<InvalidDataException>(() => containers.Read(90000002, recoveryId));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(12, world.Set<SavedMissionMember>().Count(m => m.MissionId == mission.Id));
            Assert.Equal(
                12,
                world
                    .Set<WorldNpc>()
                    .Count(n =>
                        n.EncounterId == MissionRules.FirstSiteId + mission.Id
                        && n.Role == NpcRole.Pirate
                        && n.DestroyedAt == null
                        && n.RetiredAt == null
                    )
            );
            Assert.Empty(world.Set<WorldWreck>().Where(w => w.NpcId == warehouse.Id));
        }
        Assert.Null(MissionFixture.Reply(f.Call("@ship", "Add", [cargo.Id, recoveryId], new() { ["qty"] = 5L })));
        Assert.Equal(10, Assert.Single(containers.Read(MissionFixture.PilotId, recoveryId).Items).Quantity);
        if (resident)
        {
            await LiveWorld.Find(f.Db.Path)!.FlushAsync();
            f.StopResident();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Move(f, mission.SystemId, warehouse.Position);
        RefreshScene(f);
        Assert.DoesNotContain(Structures(f, mission), n => n.Id == warehouse.Id);
        Assert.Equal(10, Assert.Single(containers.Read(MissionFixture.PilotId, recoveryId).Items).Quantity);
        f.Missions.Arrive(mission.SystemId, inner.Position);
        Assert.Equal(10, Assert.Single(containers.Read(MissionFixture.PilotId, recoveryId).Items).Quantity);
        Assert.Null(MissionFixture.Reply(f.Call("@ship", "MultiAdd", [new List<object?> { cargo.Id }, recoveryId])));
        Assert.True(Current(f).ObjectiveComplete);
        Assert.Empty(containers.Read(MissionFixture.PilotId, recoveryId).Items);
        RefreshScene(f);
        Assert.True(Assert.Single(f.Missions.Sites(mission.SystemId).SelectMany(s => s.Objects), o => o.Id == recoveryId).Container!.Empty);
        Assert.Contains(f.Context.Flight.Updates, u => u.Command is DestinyCommand.OnSlimItemChange change && change.Id == recoveryId);
        Assert.Throws<InvalidDataException>(() => f.Kill(warehouse.Id));
        f.Dock(MissionFixture.StationId);
        var paid = f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, mission.Id);
        Assert.Equal(MissionState.Completed, paid.Offer!.State);
        Assert.Equal(15, paid.Items.Sum(i => i.Previous!.Quantity - i.Item.Quantity));
        Assert.NotNull(paid.BalanceCents);
        Assert.Throws<InvalidDataException>(() => containers.Read(MissionFixture.PilotId, recoveryId));
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, mission.Id)
        );
    }

    private static MissionOffer Hidden(MissionFixture f, bool accept = true)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            var offer = f.Request();
            if (offer.MissionId == HiddenStash)
            {
                return accept ? f.Accept(offer) : offer;
            }
            var mission = f.Accept(offer);
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Quit, mission.Id);
        }
        throw new InvalidOperationException("The destructible-structure mission was not offered.");
    }

    private static MissionOffer Current(MissionFixture f) => f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!;

    private static NpcContact[] Structures(MissionFixture f, MissionOffer mission) =>
        f
            .Services.GetRequiredService<INpcEncounters>()
            .List(mission.SystemId, f.Clock.Now)
            .Where(n => n.Role == NpcRole.MissionStructure)
            .ToArray();

    private static void Move(MissionFixture f, int system, SpacePosition position)
    {
        f.Db.Store.SetPosition(f.Context.Pilot.ShipId, position, system);
        f.Context.ChangeShip(f.Db.Store.FindPilot(1000001)!);
        f.Context.Flight.Snapshot();
    }

    private static void RefreshScene(MissionFixture f)
    {
        f.Handler<CombatAnomalyHandler>().Advance(f.Context);
        f.Context.Flight.RefreshNpcs();
    }
}
