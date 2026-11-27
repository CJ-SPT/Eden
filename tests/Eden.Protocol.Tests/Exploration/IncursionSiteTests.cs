using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Mining;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Exploration;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Exploration;

public sealed class IncursionSiteTests
{
    [Fact]
    public void ConsolidationNetworkRetainsHullRestrictedEntrancesAndBothLaneClearance()
    {
        var clock = ActiveClock();
        using TestWorld fixture = new(clock: clock);
        var database = new WorldDatabase(fixture.Path);
        PassiveCatalogFixture.LoadIncursions(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        using LiveWorld live = new(database, clock, _ => { });
        CombatAnomalyStore sites = new(database);
        var focus = HighFocus(new(database), clock.Now);
        int system = Assert.Single(focus.Systems, row => row.SceneType == IncursionScene.Assault).SystemId;
        var site = Site(sites, system, IncursionSiteCatalog.NationConsolidationNetwork, clock.Now);
        AssertSite(site, 5172, 62, 114109);
        var pilot = fixture.Store.FindPilot(1000001)!;
        var gates = site.Objects.Where(o => o.GroupId == MissionGateRules.AccelerationGateGroup).ToArray();
        Assert.Equal(8, gates.Length);
        fixture.Store.SetPosition(pilot.ShipId, gates[0].Position, system);
        var entry = sites.Gate(pilot.PilotId, gates[0].Id, clock.Now);
        Assert.NotNull(entry.Access.AllowedShips);
        fixture.Store.SetPosition(pilot.ShipId, gates[1].Position, system);
        Assert.Throws<InvalidDataException>(() => sites.Gate(pilot.PilotId, gates[1].Id, clock.Now));
        sites.Arrive(system, entry.Destination, clock.Now);
        var screen = Npcs(live, site.Id);
        var left = screen.Where(n => new SpacePosition(n.X, n.Y, n.Z).DistanceTo(entry.Destination) < 200_000).ToArray();
        Assert.NotEmpty(left);
        Assert.NotEmpty(screen.Except(left));
        foreach (var npc in left)
        {
            Assert.True(Hit(live, catalog, npc.Id, pilot.PilotId, clock.Now).Destroyed);
        }
        Assert.Equal(0, Wave(live, site.Id));
        fixture.Store.SetPosition(pilot.ShipId, gates[2].Position, system);
        Assert.Throws<InvalidDataException>(() => sites.Gate(pilot.PilotId, gates[2].Id, clock.Now));
        foreach (var npc in screen.Except(left))
        {
            Assert.True(Hit(live, catalog, npc.Id, pilot.PilotId, clock.Now).Destroyed);
        }
        Assert.Equal(1, Wave(live, site.Id));
        var continuation = sites.Gate(pilot.PilotId, gates[2].Id, clock.Now);
        Assert.NotEqual(entry.Destination, continuation.Destination);
        Assert.Empty(sites.ActivateGate(pilot.PilotId, continuation.Id, continuation.Destination, clock.Now).Items);
    }

    [Fact]
    public async Task MiningColonyRequiresExactLyaviteAndRetiresTheLocalOreAfterCheckpoint()
    {
        var clock = ActiveClock();
        using TestWorld fixture = new(clock: clock);
        var database = new WorldDatabase(fixture.Path);
        PassiveCatalogFixture.LoadIncursions(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = fixture.Store.FindPilot(1000001)!;
        IncursionCargoStore cargo = new(database, clock);
        CombatAnomalyStore sites = new(database);
        CombatAnomaly colony;
        long target;
        long oreId;
        using (LiveWorld live = new(database, clock, _ => { }))
        {
            var focus = HighFocus(new(database), clock.Now);
            int system = focus.Systems.First(row => row.SceneType == IncursionScene.Vanguard).SystemId;
            colony = Site(sites, system, IncursionSiteCatalog.NationMiningColony, clock.Now);
            AssertSite(colony, 5165, 61, 114105);
            sites.Arrive(system, colony.Position + IncursionSiteCatalog.CombatOffset, clock.Now);
            var receiver = Assert.Single(colony.Objects, body => IncursionObjectIds.IsCargo(body.Id));
            target = receiver.Id;
            fixture.Store.SetPosition(pilot.ShipId, receiver.Position, system);
            long rock = IncursionObjectIds.Object(colony.Id, IncursionObjectIds.OreDeposit);
            Assert.Equal(IncursionSiteCatalog.Lyavite, live.Economy.Inspect(world => world.Set<WorldAsteroid>().Find(rock)!.TypeId));
            Assert.Throws<InvalidDataException>(() => cargo.Read(pilot.PilotId, target));
            for (int wave = 0; wave < 3; wave++)
            {
                foreach (var npc in Npcs(live, colony.Id))
                {
                    Assert.True(Hit(live, catalog, npc.Id, pilot.PilotId, clock.Now).Destroyed);
                }
            }
            Assert.Equal(colony.Id, Site(sites, system, colony.DungeonId, clock.Now).Id);
            using (var action = live.Economy.Begin(new(pilots: [pilot.PilotId])))
            {
                oreId = InventoryPersistence
                    .MergeOutput(action, catalog, pilot.PilotId, pilot.ShipId, (int)InventoryFlag.Cargo, IncursionSiteCatalog.Lyavite, 256)
                    .Item.Id;
                action.Commit();
            }
            await live.FlushAsync();
        }

        using LiveWorld reopened = new(database, clock, _ => { });
        var quantity = new InventoryMoveRequest([oreId], pilot.ShipId, (int)InventoryFlag.Cargo, 254);
        Assert.Throws<InvalidDataException>(() => cargo.Deposit(pilot.PilotId, pilot.ShipId, target, quantity));
        Assert.Equal(1, Assert.Single(cargo.Deposit(pilot.PilotId, pilot.ShipId, target, quantity with { Quantity = 255 })).Item.Quantity);
        Assert.DoesNotContain(sites.Incursions(colony.SystemId, clock.Now), s => s.Id == colony.Id);
        Assert.Throws<InvalidDataException>(() => cargo.Deposit(pilot.PilotId, pilot.ShipId, target, quantity with { Quantity = 255 }));
        Assert.Equal(
            0,
            reopened.Economy.Inspect(world =>
                world.Set<WorldAsteroid>().Find(IncursionObjectIds.Object(colony.Id, IncursionObjectIds.OreDeposit))!.Quantity
            )
        );
    }

    [Fact]
    public void AllVariantsUsePinnedArchetypesAndLowSecuritySelectsUroborus()
    {
        string? catalogPath = Environment.GetEnvironmentVariable("EDEN_TEST_INCURSION_CATALOG");
        using TestWorld? fixture = string.IsNullOrWhiteSpace(catalogPath) ? new() : null;
        if (fixture is not null)
        {
            catalogPath = new WorldDatabase(fixture.Path).CatalogPath;
            PassiveCatalogFixture.LoadIncursions(catalogPath);
        }
        using CatalogDbContext catalog = new(catalogPath!);
        var variants = Enum.GetValues<IncursionSiteKind>()
            .SelectMany(kind => IncursionSiteCatalog.Variants(catalog, kind, IncursionSecurity.High))
            .ToArray();
        Assert.Equal(14, variants.Length);
        Assert.Equal(14, variants.Select(site => site.Slot).Distinct().Count());
        Assert.Equal(4, variants.Count(site => site.Kind == IncursionSiteKind.Scout && site.Gates.IsEmpty));
        var headquarters = Assert.Single(
            variants,
            site => site.Encounter.DungeonId == IncursionSiteCatalog.TruePowerProvisionalHeadquarters
        );
        Assert.Equal(NpcPirateFaction.Sansha, headquarters.Encounter.Faction);
        Assert.Equal(3, headquarters.Gates.Length);
        Assert.Contains(headquarters.Encounter.Objects, body => body.Id == 1009453 && body.OwnerId == (int)NpcPirateFaction.Sansha);
        var flagship = Assert.Single(IncursionSiteCatalog.Variants(catalog, IncursionSiteKind.Mothership, IncursionSecurity.Low));
        Assert.Equal(5201, flagship.Encounter.DungeonId);
        Assert.True(IncursionSiteCatalog.IsMothershipFlagship(flagship.Encounter.DungeonId, 3071));
        Assert.Contains(flagship.Encounter.Waves[^1].Groups, group => group.Types.Contains(3071));
    }

    [Fact]
    public void ResearchCenterIssuesOneMtacAndRequiresDeliveryBeforeTowerAppears()
    {
        var clock = ActiveClock();
        using TestWorld fixture = new(clock: clock);
        var database = new WorldDatabase(fixture.Path);
        PassiveCatalogFixture.LoadIncursions(database.CatalogPath);
        using LiveWorld live = new(database, clock, _ => { });
        using CatalogDbContext catalog = new(database.CatalogPath);
        CombatAnomalyStore sites = new(database);
        IncursionCargoStore cargo = new(database, clock);
        var focus = HighFocus(new(database), clock.Now);
        var pilot = fixture.Store.FindPilot(1000001)!;
        int system = Assert.Single(focus.Systems, row => row.SceneType == IncursionScene.Headquarters).SystemId;
        var research = Site(sites, system, IncursionSiteCatalog.TrueCreationsResearchCenter, clock.Now);
        var source = Assert.Single(
            research.Objects,
            body => body.Id == IncursionObjectIds.Object(research.Id, IncursionObjectIds.CargoSource)
        );
        var target = Assert.Single(
            research.Objects,
            body => body.Id == IncursionObjectIds.Object(research.Id, IncursionObjectIds.CargoDestination)
        );
        sites.Arrive(system, research.Position + IncursionSiteCatalog.CombatOffset, clock.Now);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(45));
        Site(sites, system, research.DungeonId, clock.Now);
        fixture.Store.SetPosition(pilot.ShipId, source.Position, system);
        Assert.Throws<InvalidDataException>(() => cargo.Read(pilot.PilotId, source.Id));
        foreach (var npc in Npcs(live, research.Id))
        {
            Assert.True(Hit(live, catalog, npc.Id, pilot.PilotId, clock.Now).Destroyed);
        }
        Assert.Empty(Npcs(live, research.Id));
        Assert.Equal(1, Wave(live, research.Id));

        var mtac = Assert.Single(cargo.Read(pilot.PilotId, source.Id).Items);
        Assert.Equal(IncursionSiteCatalog.ConcordMtac, mtac.TypeId);
        var move = new InventoryMoveRequest([mtac.Id], source.Id, (int)InventoryFlag.Cargo, 1);
        long owned = Assert.Single(cargo.Take(pilot.PilotId, pilot.ShipId, move), change => change.Item.Quantity == 1).Item.Id;
        Assert.Empty(cargo.Read(pilot.PilotId, source.Id).Items);
        Assert.Throws<InvalidDataException>(() => cargo.Take(pilot.PilotId, pilot.ShipId, move));
        fixture.Store.SetPosition(pilot.ShipId, target.Position, system);
        var deliver = new InventoryMoveRequest([owned], pilot.ShipId, (int)InventoryFlag.Cargo, 1);
        Assert.Equal(0, Assert.Single(cargo.Deposit(pilot.PilotId, pilot.ShipId, target.Id, deliver)).Item.Quantity);
        Assert.Equal(2, Wave(live, research.Id));
        Assert.Single(
            Npcs(live, research.Id),
            n => n.TypeId == IncursionSiteCatalog.ReinforcedNationOutpost && n.Role == NpcRole.MissionStructure
        );
    }

    [Fact]
    public async Task ScoutAssaultAndHeadquartersUseTierObjectivesAcrossCheckpoint()
    {
        var clock = ActiveClock();
        using TestWorld fixture = new(clock: clock);
        var database = new WorldDatabase(fixture.Path);
        PassiveCatalogFixture.LoadIncursions(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = fixture.Store.FindPilot(1000001)!;
        CombatAnomalyStore sites = new(database);
        IncursionStore campaigns = new(database);
        CombatAnomaly headquarters;
        long deadline;

        using (LiveWorld live = new(database, clock, _ => { }))
        {
            var focus = HighFocus(campaigns, clock.Now);
            var scout = Site(sites, focus.StagingSystemId, IncursionSiteCatalog.DistressBeacon, clock.Now);
            AssertSite(scout, 5193, 60, 114117);
            Assert.DoesNotContain(scout.Objects, o => o.GroupId == MissionGateRules.AccelerationGateGroup);
            sites.Arrive(scout.SystemId, scout.Position, clock.Now);
            Assert.Equal(0, Wave(live, scout.Id));
            Assert.NotEmpty(Npcs(live, scout.Id));

            int assaultSystem = Assert.Single(focus.Systems, s => s.SceneType == IncursionScene.Assault).SystemId;
            var assault = Site(sites, assaultSystem, IncursionSiteCatalog.NationCommanderStronghold, clock.Now);
            AssertSite(assault, 5168, 62, 114110);
            sites.Arrive(assault.SystemId, assault.Position, clock.Now);
            Assert.Empty(Npcs(live, assault.Id));
            sites.Arrive(assault.SystemId, assault.Position + IncursionSiteCatalog.CombatOffset, clock.Now);
            var screen = Npcs(live, assault.Id);
            var triggers = screen.Where(n => n.TypeId == 2936).ToArray();
            Assert.Equal(2, triggers.Length);
            Assert.True(Hit(live, catalog, triggers[0].Id, pilot.PilotId, clock.Now).Destroyed);
            Assert.Equal(0, Wave(live, assault.Id));
            Assert.True(Hit(live, catalog, triggers[1].Id, pilot.PilotId, clock.Now).Destroyed);
            Assert.Equal(1, Wave(live, assault.Id));
            Assert.Contains(Npcs(live, assault.Id), n => screen.Any(prior => prior.Id == n.Id));

            int headquartersSystem = Assert.Single(focus.Systems, s => s.SceneType == IncursionScene.Headquarters).SystemId;
            headquarters = Site(sites, headquartersSystem, IncursionSiteCatalog.NationRebirthFacility, clock.Now);
            AssertSite(headquarters, 5171, 63, 114112);
            sites.Arrive(headquarters.SystemId, headquarters.Position + IncursionSiteCatalog.CombatOffset, clock.Now);
            deadline = checked(clock.Now + 45 * TimeSpan.TicksPerSecond);
            Assert.Equal(deadline, live.Economy.Inspect(w => w.Set<WorldCombatAnomaly>().Find(headquarters.Id)!.NextWaveAt));
            await live.FlushAsync();
        }

        using LiveWorld reopened = new(database, clock, _ => { });
        sites = new(database);
        var initial = Npcs(reopened, headquarters.Id).Select(n => n.Id).ToArray();
        Assert.NotEmpty(initial);
        Assert.Equal(headquarters.Id, Site(sites, headquarters.SystemId, headquarters.DungeonId, deadline - 1).Id);
        Assert.Equal(0, Wave(reopened, headquarters.Id));
        Assert.Equal(headquarters.Id, Site(sites, headquarters.SystemId, headquarters.DungeonId, deadline).Id);
        Assert.Equal(1, Wave(reopened, headquarters.Id));
        var reinforced = Npcs(reopened, headquarters.Id).Select(n => n.Id).ToArray();
        Assert.All(initial, id => Assert.Contains(id, reinforced));
        Assert.True(reinforced.Length > initial.Length);
        Site(sites, headquarters.SystemId, headquarters.DungeonId, deadline);
        Assert.Equal(reinforced.Order(), Npcs(reopened, headquarters.Id).Select(n => n.Id).Order());
    }

    [Fact]
    public void UnlockedMothershipRequiresFlagshipAndRemainingGuards()
    {
        var clock = ActiveClock();
        using TestWorld fixture = new(clock: clock);
        var database = new WorldDatabase(fixture.Path);
        PassiveCatalogFixture.LoadIncursions(database.CatalogPath);
        using LiveWorld live = new(database, clock, _ => { });
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = fixture.Store.FindPilot(1000001)!;
        IncursionStore campaigns = new(database);
        CombatAnomalyStore sites = new(database);
        var focus = HighFocus(campaigns, clock.Now);
        int system = Assert.Single(focus.Systems, s => s.SceneType == IncursionScene.Headquarters).SystemId;
        var hq = Site(sites, system, IncursionSiteCatalog.NationRebirthFacility, clock.Now);
        using (var action = live.Economy.Begin(new(systems: [system])))
        {
            var campaign = action.Set<SavedIncursionCampaign>().Find(focus.TaleId)!;
            campaign.Clears = IncursionRules.ClearsToUnlockFinal;
            campaign.HeadquartersCleared = true;
            CombatAnomalyStore.Invalidate(action, system);
            action.Commit();
        }

        Assert.True(HighFocus(campaigns, clock.Now).HasFinalEncounter);
        var flagshipSite = Site(sites, system, IncursionSiteCatalog.KundaliniManifest, clock.Now);
        AssertSite(flagshipSite, 5158, 64, 114113);
        Assert.NotEqual(hq.Position, flagshipSite.Position);
        sites.Arrive(system, flagshipSite.Position + IncursionSiteCatalog.CombatOffset, clock.Now);
        for (int wave = 0; wave < 2; wave++)
        {
            var screen = Npcs(live, flagshipSite.Id);
            Assert.NotEmpty(screen);
            Assert.DoesNotContain(screen, n => n.TypeId == 2192);
            foreach (var npc in screen)
            {
                Assert.True(Hit(live, catalog, npc.Id, pilot.PilotId, clock.Now).Destroyed);
            }
        }
        var final = Npcs(live, flagshipSite.Id);
        var flagship = Assert.Single(final, n => n.TypeId == 2192);
        Assert.True(Hit(live, catalog, flagship.Id, pilot.PilotId, clock.Now).Destroyed);
        Assert.Single(sites.Incursions(system, clock.Now), s => s.Id == flagshipSite.Id);
        foreach (var guard in final.Where(n => n.Id != flagship.Id))
        {
            Assert.True(Hit(live, catalog, guard.Id, pilot.PilotId, clock.Now).Destroyed);
        }
        Assert.DoesNotContain(campaigns.Read(clock.Now), entry => entry.Security == IncursionSecurity.High);
        Assert.Empty(sites.Incursions(system, clock.Now));
    }

    [Fact]
    public void ScopedRetirementInvalidatesWarmedNpcViewsInAnotherVanguardSystem()
    {
        TestClock clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        clock.UtcNow = DateTimeOffset.FromFileTime(IncursionRules.OpensAt(IncursionRules.TaleId(clock.Now))).ToUniversalTime();
        using TestWorld fixture = new(clock: clock);
        var database = new WorldDatabase(fixture.Path);
        PassiveCatalogFixture.LoadIncursions(database.CatalogPath);
        using LiveWorld live = new(database, clock, _ => { });
        IncursionStore campaigns = new(database);
        CombatAnomalyStore sites = new(database);
        var focus = HighFocus(campaigns, clock.Now);
        var systems = focus.Systems.Where(s => s.SceneType == IncursionScene.Vanguard).ToArray();
        var warmed = systems
            .Take(2)
            .Select(system =>
            {
                var contact = Site(sites, system.SystemId, IncursionSiteCatalog.NationCommanderOutpost, clock.Now);
                sites.Arrive(system.SystemId, contact.Position + IncursionSiteCatalog.CombatOffset, clock.Now);
                var npc = Npcs(live, contact.Id)[0];
                var combat = live.CombatForSystem(system.SystemId)!;
                Assert.True(combat.RattingNpc(npc.Id, clock.Now)?.Available);
                return (Npc: npc.Id, Combat: combat);
            })
            .ToArray();

        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = fixture.Store.FindPilot(1000001)!;
        var mining = Site(sites, systems[2].SystemId, IncursionSiteCatalog.NationMiningColony, clock.Now);
        sites.Arrive(mining.SystemId, mining.Position + IncursionSiteCatalog.CombatOffset, clock.Now);
        for (int wave = 0; wave < 3; wave++)
        {
            foreach (var npc in Npcs(live, mining.Id))
            {
                Assert.True(Hit(live, catalog, npc.Id, pilot.PilotId, clock.Now).Destroyed);
            }
        }
        Assert.Empty(Npcs(live, mining.Id));
        var asteroids = fixture.Services.GetRequiredService<IAsteroidField>();
        long ore = IncursionObjectIds.Object(mining.Id, IncursionObjectIds.OreDeposit);
        Assert.Contains(asteroids.List(mining.SystemId), body => body.Id == ore);

        using (var action = live.Economy.Begin(new(systems: [systems[0].SystemId])))
        {
            CombatAnomalyStore.RetireIncursion(action, focus.TaleId, clock.Now);
            action.Commit();
        }

        Assert.All(warmed, view => Assert.Null(view.Combat.RattingNpc(view.Npc, clock.Now)));
        Assert.DoesNotContain(asteroids.List(mining.SystemId), body => body.Id == ore);
    }

    [Fact]
    public async Task VanguardGateAndFourWavesAdvanceAcrossCheckpointAndRetireAtExpiry()
    {
        TestClock clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        clock.UtcNow = DateTimeOffset.FromFileTime(IncursionRules.OpensAt(IncursionRules.TaleId(clock.Now))).ToUniversalTime();
        using TestWorld fixture = new(clock: clock);
        var database = new WorldDatabase(fixture.Path);
        PassiveCatalogFixture.LoadIncursions(database.CatalogPath);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = fixture.Store.FindPilot(1000001)!;
        CombatAnomalyStore sites = new(database);
        IncursionStore campaigns = new(database);
        CombatAnomaly first;
        IncursionFocus focus;
        MissionGate gate;
        int system;

        using (LiveWorld live = new(database, clock, _ => { }))
        {
            focus = HighFocus(campaigns, clock.Now);
            system = focus.Systems.First(s => s.SceneType == IncursionScene.Vanguard).SystemId;
            Site(sites, focus.StagingSystemId, IncursionSiteCatalog.DistressBeacon, clock.Now);
            first = Site(sites, system, IncursionSiteCatalog.NationCommanderOutpost, clock.Now);
            Assert.DoesNotContain(sites.List(system, clock.Now), s => s.Id == first.Id);
            Assert.Equal(IncursionSiteCatalog.NationCommanderOutpost, first.DungeonId);
            var beacon = Assert.Single(first.Objects, o => o.IncursionBeacon is not null);
            var slim = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(new StaticSlimItem(beacon))));
            var fields = BlueRead.Fields(slim.State);
            Assert.Equal(61L, fields["archetypeID"]);
            Assert.Equal(5166L, fields["dungeonID"]);
            Assert.Equal(114106L, fields["dungeonNameID"]);
            Assert.Equal(500019L, fields["ownerID"]);
            Assert.Equal(first.Id, beacon.Id);

            var entrance = Assert.Single(first.Objects, o => o.GroupId == MissionGateRules.AccelerationGateGroup);
            fixture.Store.SetPosition(pilot.ShipId, first.Position - new SpacePosition(1e9, 0, 0), system);
            AssertNativeWarp("item", first.Id);
            AssertNativeWarp("scan", first.TargetId);
            fixture.Store.SetPosition(pilot.ShipId, first.Position, system);
            sites.Arrive(system, first.Position, clock.Now);
            Assert.Empty(Npcs(live, first.Id));
            Assert.Throws<InvalidDataException>(() => sites.Gate(pilot.PilotId, entrance.Id, clock.Now));
            fixture.Store.SetPosition(pilot.ShipId, entrance.Position, system);
            gate = sites.Gate(pilot.PilotId, entrance.Id, clock.Now);
            Assert.Equal(first.Position + IncursionSiteCatalog.CombatOffset, gate.Destination);
            Assert.Empty(sites.ActivateGate(pilot.PilotId, gate.Id, gate.Destination, clock.Now).Items);
            var distant = first.Position - new SpacePosition(1e9, 0, 0);
            Assert.Equal(first.Position, sites.WarpDestination(system, distant, gate.Destination, clock.Now));
            Assert.Throws<InvalidDataException>(() => sites.WarpDestination(system, first.Position, gate.Destination, clock.Now));

            fixture.Store.SetPosition(pilot.ShipId, gate.Destination, system);
            sites.Arrive(system, gate.Destination, clock.Now);
            var wave = Npcs(live, first.Id);
            Assert.Contains(wave, npc => npc.TypeId == 2859); // Romi Thalamus
            Assert.All(wave, n => Assert.InRange(new SpacePosition(n.X, n.Y, n.Z).DistanceTo(gate.Destination), 15_000, 60_000));
            Assert.True(Hit(live, catalog, wave[0].Id, pilot.PilotId, clock.Now).Destroyed);
            Assert.Equal(0, Wave(live, first.Id));
            await live.FlushAsync();
        }

        using LiveWorld reopened = new(database, clock, _ => { });
        sites = new(database);
        Assert.Equal(first.Id, Site(sites, system, first.DungeonId, clock.Now).Id);
        Assert.Equal(0, Wave(reopened, first.Id));
        for (int index = 0; index < 4; index++)
        {
            var wave = Npcs(reopened, first.Id);
            Assert.NotEmpty(wave);
            Assert.Equal(index == 2, wave.Any(n => n.TypeId == 3492)); // Sansha's Nation Commander
            foreach (var npc in wave)
            {
                var hit = Hit(reopened, catalog, npc.Id, pilot.PilotId, clock.Now);
                Assert.True(hit.Destroyed);
                Assert.Null(hit.Reward);
            }
        }
        Assert.Empty(Npcs(reopened, first.Id));
        Assert.DoesNotContain(sites.Incursions(system, clock.Now), s => s.DungeonId == first.DungeonId);
        Assert.Empty(reopened.Economy.Inspect(w => w.Filter<SavedCombatDamage>(_ => true).ToArray()));
        Assert.Empty(reopened.Economy.Inspect(w => w.Filter<NpcBounty>(_ => true).ToArray()));
        Assert.Empty(reopened.Economy.Inspect(w => w.Filter<WorldWreck>(_ => true).ToArray()));

        long due = checked(clock.Now + CombatAnomalyRules.ReplacementSeconds * TimeSpan.TicksPerSecond);
        Assert.DoesNotContain(sites.Incursions(system, due - 1), s => s.DungeonId == first.DungeonId);
        var replacement = Site(sites, system, first.DungeonId, due);
        Assert.NotEqual(first.Id, replacement.Id);
        sites.Arrive(system, replacement.Position + IncursionSiteCatalog.CombatOffset, due);
        long expired = Npcs(reopened, replacement.Id)[0].Id;
        var combat = reopened.CombatForSystem(system)!;
        Assert.NotNull(combat.RattingNpc(expired, focus.EndsAt - 1));
        Assert.Null(combat.RattingNpc(expired, focus.EndsAt));
        Assert.Equal(0, Hit(reopened, catalog, expired, pilot.PilotId, focus.EndsAt).Applied);
        Assert.Empty(sites.Incursions(system, focus.EndsAt));
        campaigns.Advance(focus.EndsAt);
        Assert.Empty(Npcs(reopened, replacement.Id));

        void AssertNativeWarp(string subject, object target)
        {
            var current = fixture.Store.FindPilot(1000001)!;
            using PilotCallContext context = new(
                1000001,
                current.Account,
                [current],
                p => new(fixture.Store, p, clock, new SharedSpace(), []),
                42,
                SignedSettings.Empty
            );
            context.Select(current);
            context.Flight.Snapshot();
            using var scope = fixture.Services.CreateScope();
            var flight = scope.ServiceProvider.GetRequiredService<FlightCallHandler>();
            Assert.IsType<PilotCallResult.Success>(flight.Call(context, "beyonce", "CmdWarpToStuff", [subject, target]));
        }
    }

    private static IncursionFocus HighFocus(IncursionStore campaigns, long now) =>
        Assert.Single(campaigns.Read(now), focus => focus.Security == IncursionSecurity.High);

    private static CombatAnomaly Site(CombatAnomalyStore sites, int system, int dungeon, long now) =>
        Assert.Single(sites.Incursions(system, now), site => site.DungeonId == dungeon);

    private static TestClock ActiveClock()
    {
        TestClock clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        clock.UtcNow = DateTimeOffset.FromFileTime(IncursionRules.OpensAt(IncursionRules.TaleId(clock.Now))).ToUniversalTime();
        return clock;
    }

    private static void AssertSite(CombatAnomaly site, int dungeon, int archetype, int name)
    {
        Assert.Equal(dungeon, site.DungeonId);
        Assert.Equal(archetype, site.ArchetypeId);
        var beacon = Assert.Single(site.Objects, o => o.IncursionBeacon is not null);
        Assert.Equal(site.Id, beacon.Id);
        Assert.Equal(new IncursionBeacon(dungeon, name, archetype), beacon.IncursionBeacon);
    }

    private static WorldNpc[] Npcs(LiveWorld world, long site) =>
        world.Economy.Inspect(w =>
            w.Filter<WorldNpc>(n => n.EncounterId == site && n.DestroyedAt == null && n.RetiredAt == null).ToArray()
        );

    private static int Wave(LiveWorld world, long site) => world.Economy.Inspect(w => w.Set<WorldCombatAnomaly>().Find(site)!.Wave);

    private static CombatDamageSettlement Hit(LiveWorld world, CatalogDbContext catalog, long npcId, long pilotId, long now)
    {
        using var action = world.Economy.Begin(new(pilots: [pilotId]));
        var npc = action.Set<WorldNpc>().Find(npcId)!;
        var pilot = action.Pilots.Find(pilotId)!;
        var attributes = new NpcProfileCatalog().Read(catalog, npc.TypeId).Attributes;
        var hit = NpcDamagePersistence.Apply(action, catalog, npc, pilot, pilot.ActiveShipId, attributes, new(1_000_000, 0, 0, 0), now);
        action.Commit();
        return hit;
    }
}
