using System.Text.Json;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CombatFixture = Eden.Protocol.Tests.Combat.NpcCombatTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcConvoyTests
{
    private const int SystemId = 30000142;
    private const long PilotId = 90000001;
    private const long ShipId = 1000000000001;
    private const long Now = 134000000000000000;

    private static int Prepare(TestWorld db, NpcEmpireFaction empire = NpcEmpireFaction.Caldari, double security = 0.949)
    {
        RegionalNpcTests.Prepare(db, security);
        string path = new WorldDatabase(db.Path).CatalogPath;
        PassiveCatalogFixture.LoadNpcConvoys(path);
        using CatalogDbContext catalog = new(path, writable: true);
        int corporation = checked(
            (int)catalog.Set<SourceRecord>().Find("client/factions", ((int)empire).ToString())!.Read<ClientFactions>().CorporationID!.Value
        );
        catalog.Set<Station>().Where(s => s.SolarSystemId == SystemId).ExecuteUpdate(set => set.SetProperty(s => s.OwnerId, corporation));
        string json = JsonSerializer.Serialize(new { securityStatus = security, factionID = (int)empire });
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142',{json})");
        return corporation;
    }

    private static IReadOnlyList<NpcContact> Convoys(TestWorld db, long now) =>
        db.Services.GetRequiredService<INpcEncounters>().List(SystemId, now).Where(n => n.Role == NpcRole.Convoy).ToArray();

    private static long Deadline(TestWorld db)
    {
        using WorldDbContext world = new(db.Path);
        return world.Set<WorldNpcEncounter>().Single(e => e.Role == NpcRole.Convoy).NextSpawnAt!.Value;
    }

    private static void SeedDirect(TestWorld db, long now)
    {
        using WorldDbContext world = new(db.Path);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        Assert.Equal(5, catalog.Set<ItemType>().Count(t => t.GroupId == 298));
        using var transaction = world.Database.BeginTransaction();
        db.Services.GetRequiredService<NpcPopulationSeeder>().Seed(world, catalog, SystemId, now);
        world.SaveChanges();
        transaction.Commit();
    }

    [Fact]
    public void PinnedCatalogAdmitsAllTwentyFourHaulersAndNoEscortTypes()
    {
        using TestWorld db = new();
        Prepare(db);
        var profiles = db.Services.GetRequiredService<NpcConvoyCatalog>().Profiles;
        Assert.Equal(24, profiles.Count);
        Assert.Equal(4, profiles.Select(p => p.RaceId).Distinct().Count());
        Assert.All(profiles.GroupBy(p => p.RaceId), race => Assert.Equal(6, race.Count()));
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        Assert.All(
            profiles,
            p =>
            {
                var type = catalog.Set<ItemType>().Find(p.TypeId)!;
                Assert.Equal(297, type.GroupId);
                Assert.Equal(type.Mass, p.Mass);
                Assert.Equal(type.Radius, p.Radius);
                Assert.True(p.Attributes[DogmaAttributeId.StructureHitPoints] > 0);
                Assert.True(NpcConvoyRules.DurationSeconds(p.Mass, p.Attributes) > 0);
                using var standing = JsonDocument.Parse(catalog.FindSource("client/spacecomponentsbytype", p.TypeId.ToString())!.Data);
                Assert.Equal(-11, standing.RootElement.GetProperty("entityStandings").GetProperty("hostileResponseThreshold").GetDouble());
            }
        );
        Assert.Equal(200, profiles.Single(p => p.TypeId == 10044).Attributes[DogmaAttributeId.MaxVelocity]);
        Assert.Equal(300, profiles.Single(p => p.TypeId == 20716).Attributes[DogmaAttributeId.MaxVelocity]);
        Assert.Equal(400, profiles.Single(p => p.TypeId == 20717).Attributes[DogmaAttributeId.MaxVelocity]);
    }

    [Theory]
    [InlineData(NpcEmpireFaction.Amarr, false)]
    [InlineData(NpcEmpireFaction.Caldari, false)]
    [InlineData(NpcEmpireFaction.Gallente, false)]
    [InlineData(NpcEmpireFaction.Minmatar, false)]
    [InlineData(NpcEmpireFaction.Amarr, true)]
    [InlineData(NpcEmpireFaction.Caldari, true)]
    [InlineData(NpcEmpireFaction.Gallente, true)]
    [InlineData(NpcEmpireFaction.Minmatar, true)]
    public void HighSecStationTrafficUsesOwnerAndCatalogPhysicsInBothRuntimes(NpcEmpireFaction empire, bool live)
    {
        using TestWorld db = new(regionalNpcs: true, liveWorld: live);
        int corporation = Prepare(db, empire);
        var traffic = Convoys(db, Now);
        Assert.Equal(2, traffic.Count);
        Assert.All(
            traffic,
            n =>
            {
                Assert.Equal(corporation, n.CorporationId);
                Assert.Equal((int)empire, n.FactionId);
                Assert.Equal(297, n.GroupId);
                Assert.Equal(11, n.CategoryId);
                Assert.Equal(NpcNavigationState.Travelling, n.Navigation!.State);
                Assert.NotEqual(n.Position, n.Navigation.Destination);
                Assert.Null(n.Weapon);
            }
        );
        Assert.Equal(5, db.Services.GetRequiredService<INpcEncounters>().List(SystemId, Now).Count(n => n.Role != NpcRole.Convoy));
        var addition = Ballpark.AddNpcs(traffic, 2);
        Assert.Equal((byte)NavigationMode.Goto, addition.State[13]);
        var wire = BlueRead.Tuple(BlueMarshal.Decode(BlueMarshal.Encode(addition)), 3);
        var slims = Assert.IsType<List<object?>>(wire[1]);
        Assert.Equal(2, slims.Count);
        foreach (var item in slims)
        {
            var fields = BlueRead.Fields(Assert.IsType<BlueInstance>(item).State);
            var contact = traffic.Single(n => n.Id == Assert.IsType<long>(fields["itemID"]));
            Assert.Equal((long)contact.TypeId, fields["typeID"]);
            Assert.Equal((long)corporation, fields["ownerID"]);
        }
        var first = traffic[0];
        db.Store.Advance(SystemId, 1, Now, [], []);
        db.Store.Advance(SystemId, 2, Now + TimeSpan.TicksPerSecond, [], []);
        var moved = Convoys(db, Now + TimeSpan.TicksPerSecond).Single(n => n.Id == first.Id);
        Assert.NotEqual(first.Position, moved.Position);
        Assert.True(moved.Position.DistanceTo(first.Navigation!.Destination!) < first.Position.DistanceTo(first.Navigation.Destination!));
        db.Store.Advance(SystemId, 2, Now + TimeSpan.TicksPerSecond, [], []);
        Assert.Equal(moved, Convoys(db, Now + TimeSpan.TicksPerSecond).Single(n => n.Id == first.Id));
        _ = WorldInitialization.Open(db.Path);
        Assert.Equal(traffic.Select(n => n.Id), Convoys(db, Now + TimeSpan.TicksPerSecond).Select(n => n.Id));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0.5, true)]
    [InlineData(0.45, true)]
    [InlineData(0.449, false)]
    [InlineData(0.3, false)]
    [InlineData(0, false)]
    [InlineData(-0.5, false)]
    public void TrafficUsesDisplayedHighSecurityBoundary(double security, bool expected)
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db, security: security);
        Assert.Equal(expected ? 2 : 0, Convoys(db, Now).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletedRunsLeaveWithoutExplodingThenReverseDirectionAfterCooldown(bool live)
    {
        TestClock clock = new(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(250));
        using TestWorld db = new(clock: clock, regionalNpcs: true, liveWorld: live);
        Prepare(db);
        var first = Convoys(db, clock.Now);
        Assert.Equal(2, first.Count);
        var start = first[0].Position;
        var end = first[0].Navigation!.Destination!;
        db.Store.SetPosition(ShipId, start + new SpacePosition(1000, 0, 0), SystemId);
        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        using FlightSession flight = new(db.Store, db.Store.FindPilot(1000001)!, clock, new(), [], encounters: encounters);
        flight.Snapshot();
        Assert.Equal(
            2,
            Assert
                .IsType<DestinyCommand.SetState>(flight.Updates[0].Command)
                .Snapshot.Slims.OfType<NpcSlimItem>()
                .Count(s => s.Npc.Role == NpcRole.Convoy)
        );
        long due = Deadline(db);
        Assert.Equal(first.Select(n => n.Id), Convoys(db, due - 1).Select(n => n.Id));
        flight.Updates.Clear();
        clock.AdvanceWallTime(TimeSpan.FromTicks(due - clock.Now));
        flight.RefreshNpcs();
        Assert.Empty(Convoys(db, due));
        var removals = flight
            .Updates.Select(u => u.Command)
            .OfType<DestinyCommand.RemoveBall>()
            .Where(r => first.Any(n => n.Id == r.Id))
            .ToArray();
        Assert.Equal(2, removals.Length);
        Assert.All(removals, r => Assert.False(r.Explode));
        long replacementAt = Deadline(db);
        Assert.Equal(due + NpcConvoyRules.CooldownSeconds * TimeSpan.TicksPerSecond, replacementAt);
        _ = WorldInitialization.Open(db.Path);
        Assert.Empty(Convoys(db, replacementAt - 1));
        clock.AdvanceWallTime(TimeSpan.FromTicks(replacementAt - clock.Now));
        flight.RefreshNpcs();
        var replacement = Convoys(db, replacementAt);
        Assert.Equal(2, replacement.Count);
        Assert.DoesNotContain(replacement, n => first.Any(old => old.Id == n.Id));
        Assert.Equal(end, replacement[0].Position);
        Assert.Equal(start, replacement[0].Navigation!.Destination);
        Assert.Contains(
            flight.Updates,
            u => u.Command is DestinyCommand.AddBalls a && a.Addition.Slims.OfType<NpcSlimItem>().Any(s => s.Npc.Role == NpcRole.Convoy)
        );
        using WorldDbContext saved = new(db.Path);
        Assert.All(
            saved.Set<WorldNpc>().Where(n => first.Select(f => f.Id).Contains(n.Id)),
            n =>
            {
                Assert.Null(n.DestroyedAt);
                Assert.Equal(due, n.RetiredAt);
            }
        );
        Assert.Empty(saved.Set<WorldWreck>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealPlayerAttacksCannotProvokeConvoysOrAlertStationNavy(bool live)
    {
        using CombatFixture f = new(liveWorld: live, useTestClock: true);
        Prepare(f.Db);
        SeedDirect(f.Db, f.Clock.Now);
        var target = Convoys(f.Db, f.Clock.Now)[0];
        f.Db.Store.SetPosition(ShipId, target.Position + new SpacePosition(1000, 0, 0), SystemId);
        var miss = f.Weapons.Fire(PilotId, f.Module, target.Id, new(0, 10000000, 0), f.Clock.Now, 0.999999);
        Assert.Equal(0, miss.Applied);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        var hit = f.Weapons.Fire(PilotId, f.Module, target.Id, SpacePosition.Zero, f.Clock.Now, 0);
        Assert.True(hit.Applied > 0);
        f.Combat.Acquire([new(PilotId, ShipId)], f.Clock.Now);
        Assert.Empty(f.Combat.Aggressors(ShipId));
        Assert.Null(f.Combat.Fire(PilotId, ShipId, target.Id, SpacePosition.Zero, f.Clock.Now, 0, 2));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<NpcAggression>());
        Assert.Empty(saved.Set<WorldNpcEwar>());
        Assert.DoesNotContain(saved.Set<WorldMissile>(), m => m.SourceShipId == target.Id);
        var remaining = Convoys(f.Db, f.Clock.Now).Single(n => n.Id == target.Id).Health.Remaining;
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(remaining, Convoys(f.Db, f.Clock.Now).Single(n => n.Id == target.Id).Health.Remaining);
    }

    [Fact]
    public void DestroyedConvoysKeepCatalogWrecksWithoutPirateRewardsAndWaitForWholeGroup()
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db);
        var first = Convoys(db, Now);
        using (WorldDbContext world = new(db.Path))
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath))
        {
            var pilot = world.Pilots.Find(PilotId)!;
            foreach (var contact in first)
            {
                var npc = world.Set<WorldNpc>().Find(contact.Id)!;
                var result = NpcDamagePersistence.Apply(
                    world,
                    catalog,
                    npc,
                    pilot,
                    ShipId,
                    PilotFittingStore.ReadAttributes(catalog, npc.TypeId),
                    new(1000000000, 0, 0, 0),
                    Now
                );
                Assert.True(result.Destroyed);
                Assert.Null(result.Reward);
                world.SaveChanges();
                if (contact == first[0])
                {
                    Assert.Equal(0, world.Set<WorldNpcEncounter>().Single(e => e.Role == NpcRole.Convoy).Generation);
                }
            }
        }
        long due = Deadline(db);
        Assert.Equal(Now + NpcConvoyRules.CooldownSeconds * TimeSpan.TicksPerSecond, due);
        _ = WorldInitialization.Open(db.Path);
        Assert.Empty(Convoys(db, due - 1));
        Assert.Equal(2, Convoys(db, due).Count);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(2, saved.Set<WorldWreck>().Count(w => w.TypeId == 27202));
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Empty(saved.Set<PendingSecurityReward>());
        Assert.Empty(saved.Set<NpcAggression>());
        var wreckIds = saved.Set<WorldWreck>().Select(w => w.Id).ToArray();
        Assert.Empty(saved.Set<OwnedItem>().Where(i => wreckIds.Contains(i.LocationId)));
    }

    [Fact]
    public async Task ConcurrentVisitorsShareOnePersistedConvoyPerStation()
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db);
        var views = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => Convoys(db, Now))));
        Assert.All(views, view => Assert.Equal(views[0], view));
        Assert.Equal(2, views[0].Count);
        using WorldDbContext world = new(db.Path);
        var encounter = Assert.Single(world.Set<WorldNpcEncounter>().Where(e => e.Role == NpcRole.Convoy));
        Assert.Equal(NpcConvoyRules.EncounterId(encounter.AnchorId), encounter.Id);
        Assert.Equal(2, world.Set<WorldNpc>().Count(n => n.Role == NpcRole.Convoy));
    }

    [Fact]
    public void InvalidSpeedCannotBecomeASubstituteConvoyHull()
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE TypeAttribute SET Value=0 WHERE ItemTypeId=10044 AND AttributeId=37");
        }
        var profiles = db.Services.GetRequiredService<NpcConvoyCatalog>().Profiles;
        Assert.Equal(23, profiles.Count);
        Assert.DoesNotContain(profiles, p => p.TypeId == 10044);
    }

    [Theory]
    [InlineData("client/npccorporations")]
    [InlineData("client/systems")]
    public void MissingOwnerOrSecurityMetadataCannotProduceTraffic(string dataset)
    {
        using TestWorld db = new(regionalNpcs: true);
        Prepare(db);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Set<SourceRecord>().Where(r => r.Dataset == dataset).ExecuteDelete();
        }
        Assert.Empty(Convoys(db, Now));
        using WorldDbContext world = new(db.Path);
        Assert.Empty(world.Set<WorldNpcEncounter>().Where(e => e.Role == NpcRole.Convoy));
    }

    private sealed record StationCase(int TypeId, double Radius, int PointCount);

    private sealed record StationFixture(StationCase[] Stations, SourceRecord[] Records);

    [Fact]
    public void ConvoyRoutesClearAllFortyFourCatalogStationModelsAndExitLanes()
    {
        using var stream = typeof(NpcConvoyTests).Assembly.GetManifestResourceStream(
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
        const double largestConvoyRadius = 660;
        foreach (var station in fixture.Stations)
        {
            var lanes = StationUndockCatalog.Read(catalog, station.TypeId);
            var route = Assert.IsType<NpcConvoyRoute>(
                NpcConvoyRules.Route(
                    SpacePosition.Zero,
                    station.Radius,
                    largestConvoyRadius,
                    lanes,
                    [new(SpacePosition.Zero, station.Radius)]
                )
            );
            for (int slot = 0; slot < NpcConvoyRules.Members; slot++)
            {
                var start = route.Near + NpcConvoyRules.FormationOffset(slot);
                var end = route.Far + NpcConvoyRules.FormationOffset(slot);
                Assert.True(NpcConvoyRules.DistanceToSegment(SpacePosition.Zero, start, end) > station.Radius + largestConvoyRadius);
                Assert.All(
                    lanes,
                    lane =>
                        Assert.True(NpcConvoyRules.DistanceToLane(start, end, lane) >= largestConvoyRadius + NpcConvoyRules.MemberSpacing)
                );
            }
        }
    }

    [Fact]
    public void RoutesRejectAnObstacleBetweenClearEndpoints()
    {
        var route = NpcConvoyRules.Route(SpacePosition.Zero, 1000, 660, [], [])!;
        var middle = route.Near + (route.Far - route.Near) * 0.5;
        Assert.Null(NpcConvoyRules.Route(SpacePosition.Zero, 1000, 660, [], [new(middle, 100)]));
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(-5, 5)]
    public void ExitClearanceUsesARayInsteadOfAnInfiniteLine(double x, double expected)
    {
        StationUndockPoint lane = new(UndockCategory.Any, SpacePosition.Zero, new(1, 0, 0));
        Assert.Equal(expected, NpcConvoyRules.DistanceToLane(new(x, -1, 0), new(x, 1, 0), lane), 9);
    }
}
