using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Propulsion;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Propulsion;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CombatFixture = Eden.Protocol.Tests.Combat.NpcCombatTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcGuardTests
{
    private const int SystemId = 30000142;
    private const long PilotId = 90000001;
    private const long ShipId = 1000000000001;
    private const long Now = 134000000000000000;

    private static void Prepare(TestWorld db, NpcEmpireFaction empire = NpcEmpireFaction.Caldari, double security = 0.949)
    {
        RegionalNpcTests.Prepare(db, security);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        var faction = catalog.Set<SourceRecord>().Find("client/factions", ((int)empire).ToString())!.Read<ClientFactions>();
        catalog
            .Set<Station>()
            .Where(s => s.SolarSystemId == SystemId)
            .ExecuteUpdate(set => set.SetProperty(s => s.OwnerId, checked((int)faction.CorporationID!.Value)));
        string json = JsonSerializer.Serialize(new { securityStatus = security, factionID = (int)empire });
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142',{json})");
    }

    private static IReadOnlyList<NpcContact> Seed(TestWorld db, long now)
    {
        using (WorldDbContext world = new(db.Path))
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath))
        using (var transaction = world.Database.BeginTransaction())
        {
            db.Services.GetRequiredService<NpcPopulationSeeder>().Seed(world, catalog, SystemId, now);
            world.SaveChanges();
            transaction.Commit();
        }
        return db.Services.GetRequiredService<INpcEncounters>().List(SystemId, now).Where(n => n.Role != NpcRole.Pirate).ToArray();
    }

    [Theory]
    [InlineData(NpcEmpireFaction.Amarr)]
    [InlineData(NpcEmpireFaction.Caldari)]
    [InlineData(NpcEmpireFaction.Gallente)]
    [InlineData(NpcEmpireFaction.Minmatar)]
    public void EmpireSystemsHaveThreeGateGuardsAndTwoStationGuards(NpcEmpireFaction empire)
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db, empire);
        var guards = db.Services.GetRequiredService<INpcEncounters>().List(SystemId, Now);
        Assert.Equal(5, guards.Count);
        Assert.All(guards, guard => Assert.Equal((int)empire, guard.FactionId));
        Assert.Equal(1, guards.Count(n => n.Role == NpcRole.Customs));
        using WorldDbContext world = new(db.Path);
        Assert.Equal(3, world.Set<WorldNpc>().Count(n => n.AnchorKind == NpcAnchorKind.Gate));
        Assert.Equal(2, world.Set<WorldNpc>().Count(n => n.AnchorKind == NpcAnchorKind.Station));
        db.Store.SetPosition(ShipId, guards[0].Position + new SpacePosition(100, 0, 0), SystemId);
        var combat = db.Services.GetRequiredService<INpcCombat>();
        combat.Acquire([new(PilotId, ShipId)], Now);
        Assert.Empty(combat.Aggressors(ShipId));
        _ = WorldInitialization.Open(db.Path);
        Assert.Equal(guards, db.Services.GetRequiredService<INpcEncounters>().List(SystemId, Now + TimeSpan.TicksPerSecond));
    }

    [Theory]
    [InlineData(NpcEmpireFaction.Amarr)]
    [InlineData(NpcEmpireFaction.Caldari)]
    [InlineData(NpcEmpireFaction.Gallente)]
    [InlineData(NpcEmpireFaction.Minmatar)]
    public void AllEmpirePatrolHullsSendFriendlyThresholdsInSnapshotsAndAdditions(NpcEmpireFaction empire)
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db, empire);
        var guards = db.Services.GetRequiredService<INpcEncounters>().List(SystemId, Now);
        Assert.Equal(3, guards.Select(n => n.TypeId).Distinct().Count());
        db.Store.SetPosition(ShipId, SpacePosition.Zero, SystemId);
        ShipContact own = new(PilotContact.From(db.Store.FindPilot(1000001)!), SpacePosition.Zero, null, 0, null);
        var snapshot = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(Ballpark.Snapshot(own, 1, [], [], guards))));
        var snapshotSlims = Assert.IsType<List<object?>>(BlueRead.Fields(snapshot.State)["slims"]);
        var addition = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(Ballpark.AddNpcs(guards, 2))), 3);
        var addedSlims = Assert.IsType<List<object?>>(addition[1]);
        foreach (var slims in new[] { snapshotSlims.Skip(1).ToArray(), addedSlims.ToArray() })
        {
            Assert.Equal(guards.Count, slims.Length);
            foreach (var item in slims)
            {
                var slim = Assert.IsType<BlueInstance>(item);
                var fields = BlueRead.Fields(slim.State);
                var guard = guards.Single(n => n.Id == Assert.IsType<long>(fields["itemID"]));
                Assert.Equal("eve.common.script.util.slimItem.SlimItem", slim.Name);
                Assert.Equal(guard.TypeId, Assert.IsType<long>(fields["typeID"]));
                Assert.Equal(guard.GroupId, Assert.IsType<long>(fields["groupID"]));
                Assert.Equal(guard.CategoryId, Assert.IsType<long>(fields["categoryID"]));
                Assert.Equal(guard.CorporationId, Assert.IsType<long>(fields["ownerID"]));
                Assert.Equal(-11d, Assert.IsType<double>(fields["hostile_response_threshold"]));
                Assert.Equal(-11d, Assert.IsType<double>(fields["friendly_response_threshold"]));
            }
        }
    }

    [Theory]
    [InlineData(0.3, true)]
    [InlineData(0, false)]
    [InlineData(-0.5, false)]
    public void EmpireGuardsAreLimitedToHighAndLowSecurity(double security, bool guarded)
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db, security: security);
        var contacts = db.Services.GetRequiredService<INpcEncounters>().List(SystemId, Now);
        Assert.Equal(guarded ? 5 : 0, contacts.Count(n => n.Role != NpcRole.Pirate));
    }

    [Fact]
    public void ARealTurretMissAlertsOnlyTheAttackedAnchorsGroup()
    {
        using CombatFixture f = new(liveWorld: true);
        Prepare(f.Db);
        var guards = Seed(f.Db, f.Clock.Now);
        using WorldDbContext world = new(f.Db.Path);
        var attacked = world.Set<WorldNpc>().First(n => n.AnchorKind == NpcAnchorKind.Gate);
        var contact = guards.Single(g => g.Id == attacked.Id);
        f.Db.Store.SetPosition(ShipId, contact.Position + new SpacePosition(1000, 0, 0), SystemId);
        var shot = f.Weapons.Fire(PilotId, f.Module, contact.Id, new(0, 10000000, 0), f.Clock.Now, 0.999999);
        Assert.Equal(0, shot.Applied);
        var aggression = world.Set<NpcAggression>().Where(a => guards.Select(g => g.Id).Contains(a.NpcId)).ToArray();
        Assert.Equal(3, aggression.Length);
        Assert.All(aggression, a => Assert.Equal(ShipId, a.TargetShipId));
        Assert.All(aggression, a => Assert.Equal(attacked.EncounterId, world.Set<WorldNpc>().Find(a.NpcId)!.EncounterId));
        var deadlines = aggression.ToDictionary(a => a.NpcId, a => a.ReadyAt);
        f.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(500));
        using (WorldDbContext update = new(f.Db.Path))
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath))
        {
            NpcHostility.Record(update, catalog, update.Set<WorldNpc>().Find(attacked.Id)!, ShipId, f.Clock.Now);
            update.SaveChanges();
        }
        using WorldDbContext saved = new(f.Db.Path);
        Assert.All(saved.Set<NpcAggression>(), a => Assert.Equal(deadlines[a.NpcId], a.ReadyAt));
    }

    [Fact]
    public void LethalOpeningAttackAlertsSurvivorsAndGrantsNoPirateRewardsOrLoot()
    {
        using CombatFixture f = new(liveWorld: true);
        Prepare(f.Db);
        var guards = Seed(f.Db, f.Clock.Now);
        using (WorldDbContext world = new(f.Db.Path))
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath))
        {
            var npc = world.Set<WorldNpc>().First(n => n.AnchorKind == NpcAnchorKind.Station);
            var defense = ShipHealth.DefenseFrom(PilotFittingStore.ReadAttributes(catalog, npc.TypeId));
            npc.ShieldCharge = 0;
            npc.ArmorDamage = defense.Armor;
            npc.StructureDamage = defense.Structure - 0.001;
            world.SaveChanges();
            f.Db.Store.SetPosition(ShipId, guards.Single(g => g.Id == npc.Id).Position + new SpacePosition(1000, 0, 0), SystemId);
            var shot = f.Weapons.Fire(PilotId, f.Module, npc.Id, SpacePosition.Zero, f.Clock.Now, 0.5);
            Assert.True(shot.Destroyed);
            Assert.Null(shot.Reward);
        }
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Single(saved.Set<NpcAggression>());
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Empty(saved.Set<PendingSecurityReward>());
        var wreck = Assert.Single(saved.Set<WorldWreck>());
        Assert.Empty(saved.Set<OwnedItem>().Where(i => i.LocationId == wreck.Id));
    }

    [Fact]
    public void MissileLaunchAlertsTheGroupBeforeImpact()
    {
        using var f = MissileCombatTests.Prepare(499, 212);
        long module = MissileCombatTests.Load(f, 499, 212);
        Prepare(f.Db);
        var guards = Seed(f.Db, f.Clock.Now);
        using WorldDbContext world = new(f.Db.Path);
        var npc = world.Set<WorldNpc>().First(n => n.AnchorKind == NpcAnchorKind.Gate);
        f.Db.Store.SetPosition(f.ShipId, guards.Single(g => g.Id == npc.Id).Position + new SpacePosition(1000, 0, 0), SystemId);
        var launch = MissileCombatTests.Launch(f, module, npc.Id);
        Assert.Equal(npc.Id, launch.Projectile.TargetId);
        Assert.Equal(3, world.Set<NpcAggression>().Count());
        Assert.Null(world.Set<WorldNpc>().Find(npc.Id)!.DestroyedAt);
    }

    [Fact]
    public void AppliedWebAlertsTheLocalGroup()
    {
        using var f = TackleTests.Prepare(527);
        long module = f.Fit(527);
        Prepare(f.Db);
        var guards = Seed(f.Db, f.Clock.Now);
        using WorldDbContext world = new(f.Db.Path);
        var npc = world.Set<WorldNpc>().First(n => n.AnchorKind == NpcAnchorKind.Gate);
        f.Db.Store.SetPosition(f.ShipId, guards.Single(g => g.Id == npc.Id).Position + new SpacePosition(1000, 0, 0), SystemId);
        var tackle = f.Db.Services.GetRequiredService<IPilotTackle>();
        tackle.Activate(
            f.PilotId,
            module,
            DogmaEffect.StasisWebifier,
            new(npc.Id, TackleTargetKind.Npc),
            new HashSet<long> { npc.Id },
            f.Clock.Now
        );
        Assert.Equal(3, world.Set<NpcAggression>().Count());
        Assert.Single(world.Set<WorldTackle>());
    }

    [Fact]
    public void CustomsWithoutWreckLinksCanBeDestroyedAndReplacedIndividually()
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db);
        db.Services.GetRequiredService<INpcEncounters>().List(SystemId, Now);
        long original;
        using (WorldDbContext world = new(db.Path))
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath))
        using (var transaction = world.Database.BeginTransaction())
        {
            var npc = world.Set<WorldNpc>().Single(n => n.Role == NpcRole.Customs);
            original = npc.Id;
            var pilot = world.Pilots.Find(PilotId)!;
            NpcDamagePersistence.Apply(
                world,
                catalog,
                npc,
                pilot,
                ShipId,
                PilotFittingStore.ReadAttributes(catalog, npc.TypeId),
                new(1000000000, 0, 0, 0),
                Now
            );
            world.SaveChanges();
            transaction.Commit();
        }
        _ = WorldInitialization.Open(db.Path);
        long due = Now + TimeSpan.FromSeconds(NpcEncounterDefinitions.RespawnSeconds).Ticks;
        var before = db.Services.GetRequiredService<INpcEncounters>().List(SystemId, due - 1);
        Assert.Equal(4, before.Count);
        var replacement = db.Services.GetRequiredService<INpcEncounters>().List(SystemId, due);
        Assert.Equal(5, replacement.Count);
        Assert.DoesNotContain(replacement, n => n.Id == original);
        using WorldDbContext saved = new(db.Path);
        Assert.Empty(saved.Set<WorldWreck>());
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Equal(2, saved.Set<WorldNpc>().Count(n => n.Role == NpcRole.Customs));
    }

    [Fact]
    public void IdleGuardsPatrolUsingSharedMotionAndAReplayCannotMoveThemTwice()
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db);
        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        var first = encounters.List(SystemId, Now);
        db.Store.Advance(SystemId, 1, Now, [], []);
        db.Store.Advance(SystemId, 2, Now + TimeSpan.TicksPerSecond, [], []);
        var moved = encounters.List(SystemId, Now + TimeSpan.TicksPerSecond);
        Assert.Contains(moved, n => n.Position != first.Single(p => p.Id == n.Id).Position);
        Assert.All(moved, n => Assert.Equal(NpcNavigationState.Patrolling, n.Navigation!.State));
        db.Store.Advance(SystemId, 2, Now + TimeSpan.TicksPerSecond, [], []);
        Assert.Equal(moved, encounters.List(SystemId, Now + TimeSpan.TicksPerSecond));
        Assert.NotEmpty(Eden.Protocol.Serialization.BlueMarshal.Encode(Ballpark.AddNpcs(moved, 2)));
    }

    private sealed record StationCase(int TypeId, double Radius, int PointCount);

    private sealed record StationFixture(StationCase[] Stations, SourceRecord[] Records);

    [Fact]
    public void PatrolPlacementClearsAllFortyFourStationModelsAndTheirExitLanes()
    {
        using var stream = typeof(NpcGuardTests).Assembly.GetManifestResourceStream(
            "Eden.Protocol.Tests.Fixtures.StationUndockCatalog.json"
        )!;
        var fixture = JsonSerializer.Deserialize<StationFixture>(stream, JsonSerializerOptions.Web)!;
        Assert.Equal(44, fixture.Stations.Length);
        using TestWorld db = new();
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        foreach (var record in fixture.Records)
        {
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ({record.Dataset},{record.Key},{record.Data})");
        }
        foreach (var station in fixture.Stations)
        {
            var lanes = StationUndockCatalog.Read(catalog, station.TypeId);
            var home = Assert.IsType<SpacePosition>(
                NpcGuardRules.Home(SpacePosition.Zero, station.Radius, lanes, [new(SpacePosition.Zero, station.Radius)])
            );
            Assert.True(home.Length > station.Radius + NpcGuardRules.PatrolRadius + NpcGuardRules.FormationMargin);
            Assert.All(
                lanes,
                lane => Assert.True(NpcGuardRules.DistanceToLane(home, lane) >= NpcGuardRules.PatrolRadius + NpcGuardRules.FormationMargin)
            );
        }
    }

    [Fact]
    public void GuardsFireAfterTheirReactionDelayThenReturnToPatrolOnDisengagement()
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db);
        var contacts = db.Services.GetRequiredService<INpcEncounters>().List(SystemId, Now);
        // There is one frigate at each anchor; use the first stable identity.
        var guard = contacts.First(n => n.TypeId == (int)NpcGuardType.CaldariCaptain);
        db.Store.SetPosition(ShipId, guard.Position + new SpacePosition(1000, 0, 0), SystemId);
        using (WorldDbContext world = new(db.Path))
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath))
        {
            NpcHostility.Record(world, catalog, world.Set<WorldNpc>().Find(guard.Id)!, ShipId, Now);
            world.SaveChanges();
        }
        var combat = db.Services.GetRequiredService<INpcCombat>();
        long ready = Now + (long)(NpcGuardRules.ReactionMilliseconds * TimeSpan.TicksPerMillisecond);
        Assert.Null(combat.Fire(PilotId, ShipId, guard.Id, SpacePosition.Zero, ready - 1, 0.5, 1));
        var shot = Assert.IsType<NpcShot>(combat.Fire(PilotId, ShipId, guard.Id, SpacePosition.Zero, ready, 0.5, 1));
        Assert.True(shot.Applied > 0);
        combat.Disengage(ShipId);
        Assert.Empty(combat.Aggressors(ShipId));
        for (int tick = 1; tick <= 4; tick++)
        {
            db.Store.Advance(SystemId, tick + 1, ready + tick * TimeSpan.TicksPerSecond, [], []);
        }
        var after = db.Services.GetRequiredService<INpcEncounters>().List(SystemId, ready + 4 * TimeSpan.TicksPerSecond);
        Assert.All(after, n => Assert.Equal(NpcNavigationState.Patrolling, n.Navigation!.State));
    }

    [Fact]
    public async Task ConcurrentVisitorsCannotDuplicateGuardSlots()
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db);
        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        var contacts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => encounters.List(SystemId, Now))));
        Assert.All(contacts, list => Assert.Equal(contacts[0], list));
        Assert.Equal(5, contacts[0].Count);
        using WorldDbContext world = new(db.Path);
        Assert.Equal(5, world.Set<WorldNpc>().Count());
    }

    [Fact]
    public void RejectedMissileLaunchDoesNotAlertGuardsOrSpendAmmunition()
    {
        using var f = MissileCombatTests.Prepare(499, 212);
        long module = MissileCombatTests.Load(f, 499, 212);
        Prepare(f.Db);
        var guard = Seed(f.Db, f.Clock.Now).First();
        f.Db.Store.SetPosition(f.ShipId, guard.Position + new SpacePosition(1000, 0, 0), SystemId);
        using WorldDbContext world = new(f.Db.Path);
        int rounds = world.Set<OwnedItem>().Single(i => i.LocationId == module).Quantity;
        Assert.Throws<InvalidDataException>(() =>
            MissileCombatTests
                .Missiles(f)
                .Launch(f.PilotId, module, guard.Id, new HashSet<long>(), new(SpacePosition.Zero, 295, new(0, 0, -1), 42), f.Clock.Now)
        );
        Assert.Empty(world.Set<NpcAggression>());
        Assert.Empty(world.Set<WorldMissile>());
        Assert.Equal(rounds, world.Set<OwnedItem>().AsNoTracking().Single(i => i.LocationId == module).Quantity);
    }
}
