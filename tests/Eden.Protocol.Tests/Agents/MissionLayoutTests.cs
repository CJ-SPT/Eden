using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Skills;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.Server;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionLayoutTests
{
    private const int InvisibleBeaconGroup = 4055;
    private const int GuristasBlockade = 2924;

    private sealed record SavedPlacement(long Id, int TypeId, double X, double Y, double Z, double? AcquisitionRange);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlockadeStasisTowerTakesRailgunDamageInsideOptimalRange(bool resident)
    {
        const int towerType = 17606;
        const int railgunType = (int)CombatWeaponType.GatlingRailgun75mm;
        const int chargeType = (int)HybridChargeType.AntimatterSmall;
        const long pilotId = MissionFixture.PilotId;
        const int stationId = MissionFixture.StationId;
        using MissionFixture f = Create(
            GuristasBlockade,
            1,
            resident,
            configure: catalog =>
            {
                PassiveCatalogFixture.LoadWeaponTiers(
                    catalog.Database.GetDbConnection().DataSource,
                    new HashSet<int> { railgunType, chargeType, (int)CombatSkill.Gunnery, (int)CombatSkill.SmallHybridTurret }
                );
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (11,'powerOutput'),(14,'hiSlots'),(48,'cpuOutput'),
                        (76,'maxTargetRange'),(192,'maxLockedTargets'),(564,'scanResolution');
                    INSERT OR REPLACE INTO TypeAttribute VALUES (601,11,23),(601,14,2),(601,48,125),
                        (601,76,50000),(601,192,4),(601,564,500);
                    """
                );
            }
        );
        long gun;
        long ammo;
        using (WorldDbContext world = new(f.Db.Path))
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath))
        {
            foreach (var skill in new[] { CombatSkill.Gunnery, CombatSkill.SmallHybridTurret })
            {
                var trained =
                    world.Set<PilotSkill>().Find(pilotId, (int)skill)
                    ?? world.Add(new PilotSkill { PilotId = pilotId, TypeId = (int)skill }).Entity;
                trained.Points = TrainedSkill.PointsForLevel(1, 1);
            }
            gun = InventoryPersistence.MergeOutput(world, catalog, pilotId, stationId, (int)InventoryFlag.Hangar, railgunType, 1).Item.Id;
            ammo = InventoryPersistence.MergeOutput(world, catalog, pilotId, stationId, (int)InventoryFlag.Hangar, chargeType, 3).Item.Id;
            world.SaveChanges();
        }
        var pilot = f.Context.Pilot;
        var fitting = f.Services.GetRequiredService<IPilotFitting>();
        fitting.Fit(pilotId, [gun], stationId, (int)InventoryFlag.AutoFit);
        fitting.SetOnline(pilotId, pilot.ShipId, gun, true);
        f.Services.GetRequiredService<IPilotAmmunition>().Load(pilotId, pilot.ShipId, [gun], [ammo], stationId, f.Clock.Now);
        f.Clock.Seconds += 6;
        var mission = f.Accept(f.Request());
        f.Missions.Arrive(mission.SystemId, mission.Position);
        var encounters = f.Services.GetRequiredService<INpcEncounters>();
        var tower = Assert.Single(encounters.List(mission.SystemId, f.Clock.Now), npc => npc.TypeId == towerType);
        Assert.InRange(mission.Position.DistanceTo(tower.Position), 25000, 26000);

        f.Db.Store.SetPosition(pilot.ShipId, mission.Position, mission.SystemId);
        f.Context.ChangeShip(f.Db.Store.FindPilot(1000001)!);
        var flight = f.Context.Flight;
        flight.Snapshot();
        Assert.IsType<PilotCallResult.Success>(f.Call("dogmaIM", "AddTarget", [tower.Id]));
        f.Clock.Seconds += 30;
        var targeting = f.Handler<TargetingHandler>();
        targeting.Advance(f.Context);
        Assert.True(targeting.IsLocked(tower.Id));
        var combat = f.Services.GetRequiredService<IPilotCombat>();
        CombatShot Fire() => combat.Fire(pilotId, gun, tower.Id, SpacePosition.Zero, f.Clock.Now, 0.5, WeaponActivation.AmmoFed);

        var miss = Fire();
        Assert.Equal(0, miss.Applied);
        Assert.Equal(tower.Health.Remaining, miss.Health.Remaining);
        Assert.Equal(2, miss.Magazine!.Quantity);

        const double keepAtRange = 2000;
        Assert.Null(flight.CallWire("beyonce", "CmdFollowBall", [tower.Id, keepAtRange]));
        double SurfaceRange() => flight.Pilot.Position!.DistanceTo(tower.Position) - tower.Radius - pilot.Flight.ShipRadius;
        for (int second = 0; second < 180 && SurfaceRange() > 2500; second++)
        {
            f.Clock.Seconds++;
            flight.Checkpoint();
        }
        Assert.InRange(SurfaceRange(), 0, 2500);
        targeting.Advance(f.Context);
        Assert.True(targeting.IsLocked(tower.Id));
        var hit = Fire();
        Assert.Equal(3000, hit.Weapon.Optimal, 8);
        Assert.Equal(3000, hit.Weapon.Falloff, 8);
        Assert.True(hit.Applied > 0, $"Quality {hit.Quality}, damage {hit.Weapon.Damage}, health {hit.Health.Remaining}");
        Assert.Equal(tower.Health.Remaining.Shield - hit.Applied, hit.Health.Remaining.Shield, 8);
        Assert.Equal(1, hit.Magazine!.Quantity);
        flight.RefreshNpcs();
        Assert.Equal(hit.Health, Assert.Single(flight.VisibleNpcs, npc => npc.Id == tower.Id).Health);
        Assert.Contains(flight.Updates, update => update.Command is DestinyCommand.OnDamageStateChange damage && damage.Id == tower.Id);
    }

    [Theory]
    [InlineData(892, 4)]
    [InlineData(1239, 4)]
    [InlineData(1241, 4)]
    [InlineData(1245, 3)]
    [InlineData(1254, 4)]
    public void FixedBatteriesUseTheirOwnRoomCatalogPositions(int missionId, int level)
    {
        using MissionFixture f = Create(missionId, level);
        var mission = f.Accept(f.Request());
        Assert.Equal(missionId, mission.MissionId);
        var room = f.Missions.Sites(mission.SystemId).Single(s => !s.Started);
        var database = new WorldDatabase(f.Db.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var profile = MissionCatalog.Find(catalog, missionId);
        var dungeon = catalog.Read(CatalogData.Client.Dungeons, profile.DungeonId!.Value)!;
        long entryId = profile.Definition.Rooms.IsEmpty
            ? dungeon.EntryObjectID!.Value
            : profile.Definition.Rooms.Single(r => r.RoomId == room.RoomId).EntryObjectId;
        var source = dungeon.Rooms![room.RoomId.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        var entry = source.Objects!.Values.Single(o => o.ObjectID == entryId).Position!;
        var expected = source
            .Objects.Values.Where(o => catalog.RequireItem(checked((int)o.TypeID!.Value)).GroupId == NpcEncounterDefinitions.SentryGroup)
            .Select(o =>
                room.Position
                + new SpacePosition(
                    o.Position!.X!.Value - entry.X!.Value,
                    o.Position.Y!.Value - entry.Y!.Value,
                    o.Position.Z!.Value - entry.Z!.Value
                )
            )
            .ToHashSet();

        f.Missions.Arrive(mission.SystemId, room.Position);
        using WorldDbContext world = new(f.Db.Path);
        var sentries = world
            .Set<WorldNpc>()
            .Where(n => n.EncounterId == MissionRules.FirstSiteId + mission.Id)
            .ToArray()
            .Where(n => catalog.RequireItem(n.TypeId).GroupId == NpcEncounterDefinitions.SentryGroup)
            .ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Count, sentries.Length);
        Assert.All(sentries, n => Assert.Contains(new SpacePosition(n.HomeX!.Value, n.HomeY!.Value, n.HomeZ!.Value), expected));
        Assert.Equal(
            expected.Count,
            sentries.Select(n => new SpacePosition(n.HomeX!.Value, n.HomeY!.Value, n.HomeZ!.Value)).Distinct().Count()
        );
    }

    [Fact]
    public void EveryExistingMissionRetainsNativeSceneryAndRoomLocalSpawnAnchors()
    {
        using MissionFixture f = new();
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var profiles = MissionCatalog.Read(catalog).Where(p => p.Kind == MissionKind.Combat).ToArray();
        Assert.Equal(50, profiles.Length);
        int restoredScenery = 0;
        FlightProperties hull = new(0, 500, 100_000_000, 0.1f, 100, 3);
        foreach (var profile in profiles)
        {
            var dungeon = catalog.Read(CatalogData.Client.Dungeons, profile.DungeonId!.Value)!;
            foreach (var room in profile.Rooms)
            {
                CollisionMotion arrival = new(1, hull, room.Position, SpacePosition.Zero, room.Position, SpacePosition.Zero);
                Assert.False(
                    ShipCollisions.ResolveStatic(arrival, profile.Rooms.SelectMany(r => r.Objects).ToArray()).Bumped,
                    $"Mission {profile.Definition.MissionId}, room {room.Id} has an obstructed entry."
                );
                var source = dungeon.Rooms![room.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)];
                long entryId = profile.Definition.Rooms.IsEmpty
                    ? dungeon.EntryObjectID!.Value
                    : profile.Definition.Rooms.Single(r => r.RoomId == room.Id).EntryObjectId;
                var entry = source.Objects!.Values.Single(o => o.ObjectID == entryId).Position!;
                foreach (var item in source.Objects.Values)
                {
                    var type = catalog.RequireItem(checked((int)item.TypeID!.Value));
                    if (item.Entities is { Count: > 0 } || type.GroupId == NpcEncounterDefinitions.SentryGroup)
                    {
                        var anchor = Assert.Single(room.SpawnAnchors, a => a.ObjectId == item.ObjectID);
                        Assert.Equal(
                            new SpacePosition(
                                item.Position!.X!.Value - entry.X!.Value,
                                item.Position.Y!.Value - entry.Y!.Value,
                                item.Position.Z!.Value - entry.Z!.Value
                            ),
                            anchor.Position
                        );
                    }
                    if (
                        item.ObjectID != entryId
                        && item.Entities is { Count: > 0 }
                        && type.CategoryId is 2 or 25
                        && type.GroupId != InvisibleBeaconGroup
                    )
                    {
                        Assert.Contains(room.Objects, o => o.Id == item.ObjectID);
                        restoredScenery++;
                    }
                }
                if (Enumerable.Range(0, profile.Definition.Waves.Length).Any(w => profile.WaveRoom(w) == room.Id))
                {
                    Assert.NotEmpty(room.SpawnAnchors);
                }
            }
            for (int wave = 0; wave < profile.Definition.Waves.Length; wave++)
            {
                int roomId = profile.WaveRoom(wave);
                var room = profile.Rooms.Single(r => r.Id == roomId);
                List<MissionSpawnMember> members = [];
                var groups = profile.Definition.Waves[wave].Groups;
                for (int group = 0; group < groups.Length; group++)
                {
                    int type = groups[group].Types[0];
                    members.AddRange(
                        Enumerable.Repeat(
                            new MissionSpawnMember(
                                type,
                                group,
                                groups[group].Range,
                                catalog.RequireItem(type).GroupId == NpcEncounterDefinitions.SentryGroup
                            ),
                            groups[group].Count
                        )
                    );
                }
                bool initial = !Enumerable.Range(0, wave).Any(w => profile.WaveRoom(w) == roomId);
                var placements = MissionSpawnRules.Place(initial, members, room.SpawnAnchors);
                Assert.Equal(members.Count, placements.Length);
                Assert.Equal(placements.ToArray(), MissionSpawnRules.Place(initial, members, room.SpawnAnchors).ToArray());
                Assert.All(
                    placements,
                    p =>
                    {
                        Assert.True(double.IsFinite(p.Position.X) && double.IsFinite(p.Position.Y) && double.IsFinite(p.Position.Z));
                        Assert.True(double.IsFinite(p.AcquisitionRange) && p.AcquisitionRange > 0);
                    }
                );
            }
        }
        Assert.True(restoredScenery > 0);
    }

    [Fact]
    public async Task SeparateNativeGroupsSurviveResidentCheckpointAndReopenWithoutRelocation()
    {
        const int guristasSpies = 875;
        using MissionFixture f = Create(guristasSpies, 1, live: true);
        var mission = f.Accept(f.Request());
        var room = f.Missions.Sites(mission.SystemId).Single(s => !s.Started);
        f.Missions.Arrive(mission.SystemId, room.Position);
        var saved = Snapshot(f, mission.Id);
        Assert.Equal(8, saved.Length);
        var firstGroup = new SpacePosition(saved[0].X, saved[0].Y, saved[0].Z);
        var secondGroup = new SpacePosition(saved[4].X, saved[4].Y, saved[4].Z);
        Assert.InRange(firstGroup.DistanceTo(secondGroup), 40000, 42000);
        f.Missions.Arrive(mission.SystemId, room.Position);
        Assert.Equal(saved, Snapshot(f, mission.Id));
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        f.StopResident();
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock);
        reopened.GetRequiredService<IPilotMissions>().Arrive(mission.SystemId, room.Position);
        Assert.Equal(saved, Snapshot(f, mission.Id));
    }

    [Fact]
    public void LaterRoomStartsWithInitialAnchorsEvenWhenItsFirstWaveIndexIsNonzero()
    {
        const int guristasAssault = 2537;
        const int finalRoomId = 17285;
        using MissionFixture f = Create(guristasAssault, 4);
        var mission = f.Accept(f.Request());
        var room = f.Missions.Sites(mission.SystemId).Single(s => s.RoomId == finalRoomId);
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var profile = MissionCatalog.Find(catalog, guristasAssault);
        int firstWave = Enumerable.Range(0, profile.Definition.Waves.Length).First(w => profile.WaveRoom(w) == finalRoomId);
        Assert.True(firstWave > 0);
        var initial = profile
            .Rooms.Single(r => r.Id == finalRoomId)
            .SpawnAnchors.Where(a => a.Initial && a.SentryTypeId is null)
            .Select(a => room.Position + a.Position)
            .ToHashSet();
        Assert.NotEmpty(initial);
        f.Missions.Arrive(mission.SystemId, room.Position);
        Assert.Contains(Snapshot(f, mission.Id), p => initial.Contains(new SpacePosition(p.X, p.Y, p.Z)));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(firstWave, world.Set<SavedMissionRoom>().Find(mission.Id, finalRoomId)!.Wave);
    }

    [Fact]
    public void RolledBackTriggerPreservesPositionsAndCommittedTriggerStartsReinforcements()
    {
        using MissionFixture f = Create(GuristasBlockade, 1);
        var mission = f.Accept(f.Request());
        var room = Assert.Single(f.Missions.Sites(mission.SystemId));
        f.Missions.Arrive(mission.SystemId, room.Position);
        var saved = Snapshot(f, mission.Id);
        long trigger;
        using (WorldDbContext world = new(f.Db.Path))
        {
            trigger = Assert.Single(world.Set<SavedMissionMember>().Where(m => m.MissionId == mission.Id && m.Trigger)).NpcId;
        }
        f.Kill(trigger, commit: false);
        Assert.Equal(saved, Snapshot(f, mission.Id));
        f.Kill(trigger);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(1, world.Set<SavedMission>().Find(mission.Id)!.Wave);
            Assert.NotEmpty(world.Set<SavedMissionMember>().Where(m => m.MissionId == mission.Id && m.Wave == 1));
        }
        var reinforced = Snapshot(f, mission.Id);
        f.Missions.Arrive(mission.SystemId, room.Position);
        Assert.Equal(reinforced, Snapshot(f, mission.Id));
    }

    private static SavedPlacement[] Snapshot(MissionFixture f, long missionId)
    {
        using WorldDbContext world = new(f.Db.Path);
        return world
            .Set<WorldNpc>()
            .Where(n => n.EncounterId == MissionRules.FirstSiteId + missionId && n.DestroyedAt == null)
            .OrderBy(n => n.Id)
            .Select(n => new SavedPlacement(n.Id, n.TypeId, n.HomeX!.Value, n.HomeY!.Value, n.HomeZ!.Value, n.AcquisitionRange))
            .ToArray();
    }

    private static MissionFixture Create(int missionId, int level, bool live = false, Action<CatalogDbContext>? configure = null)
    {
        var fixture = new MissionFixture(
            level,
            MissionRules.SecurityDivision,
            live,
            configure: catalog =>
            {
                string key = missionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                catalog.Database.ExecuteSql($"DELETE FROM SourceRecord WHERE Dataset='client/missions' AND Key<>{key}");
                configure?.Invoke(catalog);
            },
            combatScene: true
        );
        using WorldDbContext world = new(fixture.Db.Path);
        world.Add(
            new SavedNpcStanding
            {
                PilotId = MissionFixture.PilotId,
                SourceId = MissionFixture.AgentId,
                Value = 8,
            }
        );
        world.SaveChanges();
        return fixture;
    }
}
