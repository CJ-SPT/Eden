using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Exploration;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Exploration;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Exploration;

public sealed class DedAndGhostSiteTests
{
    [Fact]
    public void PinnedCatalogResolvesLayoutsRestrictionsAndCacheTypes()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadDedAndGhost(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var ded = DedComplexCatalog.Read(catalog);
        Assert.Equal(52, ded.Length);
        var outlet = ded.Single(d => d.Binding.DungeonId == 116);
        Assert.Equal(4, outlet.Rooms.Length);
        Assert.Equal(96282, outlet.Rooms[0].Gates.Single().Body.Id);
        Assert.Equal(17892, outlet.Rooms[2].Gates.Single().Access.Key!.TypeId);
        Assert.True(outlet.Rooms[2].Gates.Single().RequiresClear);
        Assert.True(outlet.Rooms[2].Gates.Single().Access.Key!.Consume);
        Assert.Equal(18634, Assert.Single(outlet.Rooms[3].Members, m => m.Binding.Structure).Binding.TypeId);
        var scout = ded.Single(d => d.Binding.DungeonId == 2249);
        Assert.Equal(3, scout.Rooms.Length);
        Assert.Equal(841485, scout.Rooms[0].Gates.Single().Body.Id);
        Assert.Equal(18632, Assert.Single(scout.Rooms[2].Members, m => m.Binding.Structure).Binding.TypeId);
        var maze = ded.Single(d => d.Binding.DungeonId == 2257);
        Assert.Equal(36, maze.Rooms.Length);
        Assert.Equal(61, maze.Rooms.Sum(r => r.Gates.Length));
        Assert.Equal(5, maze.Difficulty);
        Assert.Equal(10, maze.Binding.Rating);
        Assert.Equal(19421, Assert.Single(maze.Binding.GuaranteedLoot).TypeId);
        var switchback = maze.Rooms[1].Gates.Single(g => g.Body.Id == 842137);
        Assert.Equal(19002, maze.Binding.Rooms[switchback.Destination].RoomId);
        Assert.Equal(19014, maze.Binding.Rooms[Assert.Single(maze.Rooms[switchback.Destination].Gates).Destination].RoomId);
        var assembly = ded.Single(d => d.Binding.DungeonId == 2258);
        Assert.Equal(19954, assembly.Rooms.SelectMany(r => r.Gates).Single(g => g.Body.Id == 842611).Access.Key!.TypeId);
        Assert.Contains(assembly.Binding.SecondaryRewards!, r => r.TypeId == 19960 && r.Loot.Any(l => l.TypeId == 19954));
        var mine = ded.Single(d => d.Binding.DungeonId == DedEscalationRules.DroneInfestedMine);
        Assert.Equal(5, Assert.Single(mine.Rooms).Members.Select(m => m.Binding.Stage).Distinct().Count());

        var ghosts = GhostSiteCatalog.Read(catalog);
        Assert.Equal(20, ghosts.Length);
        Assert.All(
            ghosts,
            ghost =>
            {
                Assert.Equal(4, ghost.Containers.Count(c => !GhostSiteRules.IsBonusCache(c.TypeId)));
                Assert.Equal(ghost.Band < 2 ? 1 : 0, ghost.Containers.Count(c => GhostSiteRules.IsBonusCache(c.TypeId)));
            }
        );
        var angel = ghosts.Single(g => g.DungeonId == 5533);
        var depot = angel.Containers.First(c => c.TypeId == 33530);
        Assert.Equal(10_668.1489, depot.Position.X, 3);
        Assert.Equal(-11_864.2109, depot.Position.Z, 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpeditionGrantIsPrivatePersistentAndExpires(bool resident)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadDedAndGhost(f.CatalogPath);
        int system = CombatAnomalyRules.SystemId;
        long sourceId = CombatAnomalyRules.FirstSiteId + 1000;
        long npcId = CombatAnomalyRules.FirstNpcId;
        using (WorldDbContext db = new(f.Db.Path))
        {
            db.Add(
                new WorldCombatAnomaly
                {
                    Id = sourceId,
                    SystemId = system,
                    Slot = DedComplexRules.SiteSlot,
                    DungeonId = 2455,
                    Wave = 3,
                }
            );
            db.Add(
                new WorldNpc
                {
                    Id = npcId,
                    SystemId = system,
                    EncounterId = sourceId,
                    TypeId = 17118,
                    FactionId = 500020,
                    AnchorKind = NpcAnchorKind.Anomaly,
                    Role = NpcRole.Pirate,
                    DestroyedAt = f.Clock.Now,
                }
            );
            db.Add(
                new WorldAnomalyMember
                {
                    NpcId = npcId,
                    SiteId = sourceId,
                    Wave = 3,
                }
            );
            db.SaveChanges();
        }
        var dbInfo = new WorldDatabase(f.Db.Path);
        var store = new CombatAnomalyStore(dbInfo);
        using (var live = resident ? new LiveWorld(dbInfo, f.Clock, _ => { }) : null)
        {
            Assert.Empty(store.Journal(f.PilotId, f.Clock.Now));
            using CatalogDbContext catalog = new(f.CatalogPath);
            using (var action = new WorldActionScope(dbInfo))
            {
                var world = action.World;
                CombatAnomalyStore.Killed(world, catalog, world.Set<WorldNpc>().Find(npcId)!, f.Clock.Now, f.PilotId);
            }
            Assert.Empty(store.Journal(f.PilotId, f.Clock.Now));
            using (var action = new WorldActionScope(dbInfo))
            {
                var world = action.World;
                var defeated = world.Set<WorldNpc>().Find(npcId)!;
                CombatAnomalyStore.Killed(world, catalog, defeated, f.Clock.Now, f.PilotId);
                CombatAnomalyStore.Killed(world, catalog, defeated, f.Clock.Now, f.PilotId);
                action.Commit();
            }
            var grant = Assert.Single(store.Journal(f.PilotId, f.Clock.Now));
            Assert.Equal(2507, grant.DungeonId);
            Assert.Equal(f.Clock.Now + TimeSpan.TicksPerDay, grant.ExpiresAt);
            Assert.Empty(store.Journal(f.PilotId + 1, f.Clock.Now));
            Assert.Throws<InvalidDataException>(() => store.Find(f.PilotId + 1, grant.SystemId, grant.InstanceId, f.Clock.Now));
            Assert.DoesNotContain(store.Signatures(grant.SystemId, f.Clock.Now), s => s.Id == grant.InstanceId);
            var warp = FlightRequestWire.Read("beyonce", "CmdWarpToStuff", ["epinstance", grant.InstanceId]).Command;
            Assert.Equal(grant.InstanceId, Assert.IsType<FlightCommand.WarpToEscalation>(warp).InstanceId);
            var wire = Assert.IsType<BlueInstance>(new DedEscalationJournal([grant]).ToBlue());
            Assert.Equal("eve.common.script.sys.rowset.Rowset", wire.Name);
            var row = Assert.IsType<object?[]>(new DedEscalationJournalRow(grant).ToBlue());
            var address = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(row[1]).State);
            Assert.Equal(grant.InstanceId, address["instanceID"]);
            Assert.Equal(grant.DungeonId, address["destDungeonID"]);
            var site = store.Find(f.PilotId, grant.SystemId, grant.InstanceId, f.Clock.Now);
            store.Arrive(f.PilotId + 1, grant.SystemId, site.Position, f.Clock.Now);
            store.Arrive(f.PilotId, grant.SystemId, site.Position, f.Clock.Now);
        }
        var reopened = new CombatAnomalyStore(dbInfo);
        var saved = Assert.Single(reopened.Journal(f.PilotId, f.Clock.Now));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Contains(world.Set<WorldNpc>(), n => n.EncounterId == saved.InstanceId && n.RetiredAt == null);
        }
        Assert.Empty(reopened.Journal(f.PilotId, saved.ExpiresAt));
        Assert.Throws<InvalidDataException>(() => reopened.Find(f.PilotId, saved.SystemId, saved.InstanceId, saved.ExpiresAt));
        using WorldDbContext check = new(f.Db.Path);
        Assert.DoesNotContain(check.Set<WorldNpc>(), n => n.EncounterId == saved.InstanceId && n.RetiredAt == null);
    }

    [Fact]
    public void GhostResponseAndBlastPersistAndPreserveTheBonusCache()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadDedAndGhost(f.CatalogPath);
        var exploration = f.Db.Services.GetRequiredService<IPirateExploration>();
        int system = CombatAnomalyRules.SystemId;
        var site = Assert.Single(exploration.Signatures(system, f.Clock.Now), s => s.Exploration == ExplorationKind.Ghost);
        var ordinary = site.Objects.First(c => GhostSiteRules.IsCache(c.TypeId) && !GhostSiteRules.IsBonusCache(c.TypeId));
        var bonus = Assert.Single(site.Objects, c => GhostSiteRules.IsBonusCache(c.TypeId));
        var wire = Assert.IsType<Dictionary<string, object?>>(ProbeScanningWire.Result(new(site, 1, site.Position, 0)).State);
        Assert.Equal(GhostSiteRules.Archetype, wire["archetypeID"]);
        exploration.ArriveUncloaked(system, site.Position, f.Clock.Now);
        long response;
        using (WorldDbContext world = new(f.Db.Path))
        {
            response = world.Set<SavedExplorationSite>().Find(site.Id)!.ResponseAt!.Value;
        }
        _ = exploration.Signatures(system, response - 1);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Empty(world.Set<WorldNpc>().Where(n => n.AnchorKind == NpcAnchorKind.GhostSite));
        }
        _ = exploration.Signatures(system, response);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(3, world.Set<WorldNpc>().Count(n => n.AnchorKind == NpcAnchorKind.GhostSite));
        }
        f.Db.Store.SetPosition(f.ShipId, ordinary.Position, system);
        long originalShip = f.ShipId;
        long blast = response + GhostSiteRules.VisibleCountdown;
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        var after = Assert.Single(exploration.Signatures(system, blast), s => s.Id == site.Id);
        Assert.DoesNotContain(after.Objects, c => c.Id == ordinary.Id);
        Assert.Contains(after.Objects, c => c.Id == bonus.Id);
        _ = exploration.Signatures(system, blast);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.True(saved.Set<SavedExplorationSite>().Find(site.Id)!.Detonated);
        Assert.Single(saved.Set<ShipLoss>().Where(l => l.ShipId == originalShip));
        Assert.Equal(3, saved.Set<WorldNpc>().Count(n => n.AnchorKind == NpcAnchorKind.GhostSite));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrugOutletConsumesItsPinnedPasscardOncePerPilot(bool resident)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadDedAndGhost(f.CatalogPath);
        const int system = 30002659; // Dodixie, Sinq Laison.
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO Region(Id,Name) VALUES (10000032,'Sinq Laison');
                INSERT INTO Constellation(Id,RegionId,Name) VALUES (20000389,10000032,'Viriette');
                INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES (30002659,20000389,'Dodixie');
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z) VALUES (50002659,30002659,16,10,1e10,2e10,3e10);
                """
            );
        }
        long ship = f.ShipId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(ship)!.TypeId = 587;
            world.SaveChanges();
        }
        var store = (CombatAnomalyStore)f.Db.Services.GetRequiredService<ICombatAnomalies>();
        SeedDed(f, system, 116);
        using var live = resident ? new LiveWorld(new(f.Db.Path), f.Clock, _ => { }) : null;
        var site = Assert.Single(store.Signatures(system, f.Clock.Now), s => s.Ded);
        Assert.Equal(116, site.DungeonId);
        var gates = site.Objects.Where(o => DedComplexRules.IsGate(o.Id)).OrderBy(o => o.Id).ToArray();
        f.Db.Store.SetPosition(ship, gates[0].Position, system);
        store.Arrive(system, site.Position, f.Clock.Now);
        for (int i = 0; i < 2; i++)
        {
            f.Db.Store.SetPosition(ship, gates[i].Position, system);
            var gate = store.Gate(f.PilotId, gates[i].Id, f.Clock.Now);
            store.ActivateGate(f.PilotId, gate.Id, gate.Destination, f.Clock.Now);
            f.Db.Store.SetPosition(ship, gate.Destination, system);
            store.Arrive(system, gate.Destination, f.Clock.Now);
        }
        f.Db.Store.SetPosition(ship, gates[2].Position, system);
        Assert.Throws<InvalidDataException>(() => store.Gate(f.PilotId, gates[2].Id, f.Clock.Now));
        using (WorldDbContext world = new(f.Db.Path))
        {
            var members = world.Set<WorldAnomalyMember>().Where(m => m.SiteId == site.Id && m.Wave == 2).Select(m => m.NpcId).ToArray();
            Assert.Equal(18, members.Length);
            foreach (var npc in world.Set<WorldNpc>().Where(n => members.Contains(n.Id)))
            {
                npc.DestroyedAt = f.Clock.Now;
            }
            world.SaveChanges();
        }
        var last = store.Gate(f.PilotId, gates[2].Id, f.Clock.Now);
        Assert.Throws<InvalidDataException>(() => store.ActivateGate(f.PilotId, last.Id, last.Destination, f.Clock.Now));
        long key = f.Grant(17892, cargo: true);
        using (var metrics = SessionPerformance.Begin("DED passcard", _ => { }, force: true)!)
        {
            Assert.Single(store.ActivateGate(f.PilotId, last.Id, last.Destination, f.Clock.Now).Items);
            Assert.Empty(store.ActivateGate(f.PilotId, last.Id, last.Destination, f.Clock.Now).Items);
            if (resident)
            {
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
            }
        }
        if (live is not null)
        {
            await live.FlushAsync();
            live.Dispose();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        var reopened = new CombatAnomalyStore(new(f.Db.Path));
        Assert.Empty(reopened.ActivateGate(f.PilotId, last.Id, last.Destination, f.Clock.Now).Items);
        using WorldDbContext check = new(f.Db.Path);
        Assert.Null(check.Set<OwnedItem>().Find(key));
        Assert.Single(check.Set<SavedDedGateUnlock>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScoutOutpostGateAndTelescopeLootSurviveReopen(bool resident)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadDedAndGhost(f.CatalogPath);
        long ship = f.ShipId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(ship)!.TypeId = 587;
            world.SaveChanges();
        }
        int system = CombatAnomalyRules.SystemId;
        var store = (CombatAnomalyStore)f.Db.Services.GetRequiredService<ICombatAnomalies>();
        SeedDed(f, system, 2249);
        var site = Assert.Single(store.Signatures(system, f.Clock.Now), s => s.Ded);
        Assert.Equal(2249, site.DungeonId);
        var gates = site.Objects.Where(o => DedComplexRules.IsGate(o.Id)).OrderBy(o => o.Id).ToArray();
        Assert.Equal(2, gates.Length);
        f.Db.Store.SetPosition(ship, site.Position, system);
        store.Arrive(system, site.Position, f.Clock.Now);
        var entrance = store.Gate(f.PilotId, gates[0].Id, f.Clock.Now);
        Assert.Empty(store.ActivateGate(f.PilotId, entrance.Id, entrance.Destination, f.Clock.Now).Items);
        Assert.Equal(site.Position, store.WarpDestination(system, SpacePosition.Zero, entrance.Destination, f.Clock.Now));
        Assert.Throws<InvalidDataException>(() => store.WarpDestination(system, site.Position, entrance.Destination, f.Clock.Now));

        f.Db.Store.SetPosition(ship, entrance.Destination, system);
        store.Arrive(system, entrance.Destination, f.Clock.Now);
        f.Db.Store.SetPosition(ship, gates[1].Position, system);
        Assert.Throws<InvalidDataException>(() => store.Gate(f.PilotId, gates[1].Id, f.Clock.Now));
        using (WorldDbContext world = new(f.Db.Path))
        {
            var members = world.Set<WorldNpc>().Where(n => n.EncounterId == site.Id).ToArray();
            Assert.Equal(28, members.Length);
            foreach (var member in members)
            {
                member.DestroyedAt = f.Clock.Now;
            }
            world.SaveChanges();
        }
        var inner = store.Gate(f.PilotId, gates[1].Id, f.Clock.Now);
        f.Db.Store.SetPosition(ship, inner.Destination, system);
        store.Arrive(system, inner.Destination, f.Clock.Now);
        long telescopeId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            telescopeId = Assert.Single(world.Set<WorldNpc>(), n => n.EncounterId == site.Id && n.TypeId == 18632).Id;
        }
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            var attributes = new NpcProfileCatalog().Read(catalog, 18632).Attributes;
            var damage = new DamageVector(0, 0, 500_000, 0);
            if (resident)
            {
                using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
                using var transaction = new WorldActionScope(new(f.Db.Path));
                var world = transaction.World;
                var telescope = world.Set<WorldNpc>().Find(telescopeId)!;
                var pilot = world.Pilots.Single(p => p.Id == f.PilotId);
                Assert.True(NpcDamagePersistence.Apply(world, catalog, telescope, pilot, ship, attributes, damage, f.Clock.Now).Destroyed);
                transaction.Commit();
            }
            else
            {
                using WorldDbContext world = new(f.Db.Path);
                using var transaction = world.Database.BeginTransaction();
                var telescope = world.Set<WorldNpc>().Find(telescopeId)!;
                var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == f.PilotId);
                Assert.True(NpcDamagePersistence.Apply(world, catalog, telescope, pilot, ship, attributes, damage, f.Clock.Now).Destroyed);
                world.SaveChanges();
                transaction.Commit();
            }
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.NotNull(saved.Set<WorldCombatAnomaly>().Find(site.Id)!.CompletedAt);
        var cache = Assert.Single(saved.Set<WorldWreck>(), w => w.TypeId == 23);
        var items = saved.Set<OwnedItem>().Where(i => i.LocationId == cache.Id).ToArray();
        Assert.Contains(items, i => i.TypeId == 19407);
        Assert.Single(items, i => i.TypeId is 19187 or 19213);
        Assert.DoesNotContain(store.Signatures(system, f.Clock.Now), s => s.Id == site.Id);
    }

    private static void SeedDed(Fixture fixture, int system, int dungeon)
    {
        using WorldDbContext world = new(fixture.Db.Path);
        world.Add(
            new WorldCombatAnomaly
            {
                Id = CombatAnomalyRules.FirstSiteId + 1000,
                SystemId = system,
                DungeonId = dungeon,
                Slot = DedComplexRules.SiteSlot,
                IsSignature = true,
                X = 1e11,
                Y = 2e11,
                Z = 3e11,
            }
        );
        world.SaveChanges();
    }
}
