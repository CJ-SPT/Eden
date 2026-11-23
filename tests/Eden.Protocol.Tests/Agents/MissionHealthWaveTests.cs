using System.Text.Json;
using Eden.Data.Catalog.Agents;
using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Docking;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Server;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionHealthWaveTests
{
    private const int System = 30000142;
    private const int SourceRoom = 52;
    private const int DecoyRoom = 53;
    private const int Definition = 9010021;
    private const int Drone = 9010022;
    private const long Mission = 9010023;
    private const int Wreck = 9010024;
    private const int Capacity = 64;
    private static readonly SpacePosition s_position = new(145000, 15000, 40000);
    private static readonly SpacePosition s_decoyOffset = new(MissionGateRules.RoomSpacing, 0, 0);

    [Fact]
    public async Task DamageSpawnsOnlyTheMatchingWavesAndPreservesProgressAcrossRollbackAndReopen()
    {
        using MissionFixture fixture = new(configure: DroneCatalog);
        WorldDatabase database = new(fixture.Db.Path);
        using var registration = CatalogProvider.Retain(database.CatalogPath);
        InstallProfile(database);
        long now = fixture.Clock.Now;
        using (var services = ServerServices.Build(database, fixture.Clock, liveWorld: true))
        {
            var live = services.GetRequiredService<LiveWorld>();
            var missions = services.GetRequiredService<IPilotMissions>();
            Seed(live, now);
            missions.Arrive(System, s_position);
            missions.Arrive(System, s_position + s_decoyOffset);
            Assert.Equal([0, 1], Spawned(live));

            Assert.Equal(32, Hit(live, database, wave: 1, amount: 32, now).Health.Remaining.Shield);
            Assert.Equal(49, Hit(live, database, wave: 0, amount: 15, now).Health.Remaining.Shield);
            Assert.Equal(0, Hit(live, database, wave: 0, amount: 0, now).Applied);
            Assert.Equal([0, 1], Spawned(live));

            Assert.Equal(48, Hit(live, database, wave: 0, amount: 1, now, commit: false).Health.Remaining.Shield);
            Assert.Equal([0, 1], Spawned(live));
            Assert.Equal(48, Hit(live, database, wave: 0, amount: 1, now).Health.Remaining.Shield);
            Assert.Equal([0, 1, 2], Spawned(live));
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(database, fixture.Clock, liveWorld: true);
        var resident = reopened.GetRequiredService<LiveWorld>();
        using (var repair = resident.Economy.Begin(new(systems: [System])))
        {
            FindNpc(repair, wave: 0).ShieldCharge = Capacity;
            repair.Commit();
        }
        Assert.Equal(48, Hit(resident, database, wave: 0, amount: 16, now).Health.Remaining.Shield);
        Assert.Equal([0, 1, 2], Spawned(resident));

        Assert.True(Hit(resident, database, wave: 0, amount: 256, now).Destroyed);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], Spawned(resident));
        Assert.False(Complete(resident));

        foreach (int wave in new[] { 2, 3, 4, 5 })
        {
            Assert.True(Hit(resident, database, wave, amount: 256, now).Destroyed);
        }
        Assert.True(Complete(resident));
    }

    [Fact]
    public void WarmNonfatalTurretVolleyPublishesItsReinforcement()
    {
        using MissionFixture fixture = new(configure: catalog =>
        {
            DroneCatalog(catalog);
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (6,'capacitorNeed'),(14,'hiSlots'),(48,'cpuOutput'),
                    (11,'powerOutput'),(76,'maxTargetRange');
                INSERT OR REPLACE INTO TypeAttribute VALUES (601,14,2),(601,48,125),(601,11,23),(601,76,50000),
                    (3638,6,0),(3638,51,1000),(3638,54,10000),(3638,64,1),(3638,117,20),
                    (3638,158,3000),(3638,160,500),(3638,620,40);
                """
            );
        });
        WorldDatabase database = new(fixture.Db.Path);
        using var registration = CatalogProvider.Retain(database.CatalogPath);
        InstallProfile(database);
        using var services = ServerServices.Build(database, fixture.Clock, liveWorld: true);
        var live = services.GetRequiredService<LiveWorld>();
        var fitting = services.GetRequiredService<IPilotFitting>();
        var combat = services.GetRequiredService<IPilotCombat>();
        var encounters = services.GetRequiredService<INpcEncounters>();
        var gun = Assert.Single(
            services.GetRequiredService<IPilotInventory>().List(MissionFixture.PilotId, MissionFixture.StationId),
            item => item.TypeId == (int)CombatWeaponType.CivilianGatlingRailgun
        );
        long ship = live.Economy.Inspect(action => action.Pilots.Find(MissionFixture.PilotId)!.ActiveShipId);
        fitting.Fit(MissionFixture.PilotId, [gun.Id], MissionFixture.StationId, (int)InventoryFlag.AutoFit);
        fitting.SetOnline(MissionFixture.PilotId, ship, gun.Id, true);

        Seed(live, fixture.Clock.Now);
        services.GetRequiredService<IPilotMissions>().Arrive(System, s_position);
        var target = Assert.Single(encounters.List(System, fixture.Clock.Now), contact => contact.TypeId == Drone);
        using (var move = live.Economy.Begin(new(systems: [System])))
        {
            var position = move.Set<FlightPosition>().Find(ship) ?? move.Add(new FlightPosition { ShipId = ship }).Entity;
            (position.SystemId, position.X, position.Y, position.Z) = (
                System,
                target.Position.X + 1000,
                target.Position.Y,
                target.Position.Z
            );
            move.Commit();
        }
        var first = Fire(0.05);
        Assert.InRange(first.Applied, 1, 15);
        Assert.False(first.Destroyed);
        Assert.Equal([0], Spawned(live));

        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(2));
        var second = Fire(0.6);
        Assert.True(second.Applied > 0);
        Assert.False(second.Destroyed);
        Assert.InRange(second.Health.Remaining.Shield, 0, 48);
        Assert.Equal([0, 2], Spawned(live));
        Assert.Equal(2, encounters.List(System, fixture.Clock.Now).Count(contact => contact.TypeId == Drone));

        CombatShot Fire(double roll) =>
            combat.Fire(MissionFixture.PilotId, gun.Id, target.Id, SpacePosition.Zero, fixture.Clock.Now, roll, WeaponActivation.Laser);
    }

    private static void DroneCatalog(CatalogDbContext catalog)
    {
        string definition = JsonSerializer.Serialize(new { wreckTypeID = Wreck });
        catalog.Database.ExecuteSql(
            $"""
            INSERT INTO ItemType(Id,Name,GroupId,Radius) VALUES ({Drone},'Test relay drone',596,10),({Wreck},'Test relay wreck',186,10);
            """
        );
        catalog.Database.ExecuteSql($"INSERT INTO SourceRecord (Dataset,Key,Data) VALUES ('client/types',{Drone.ToString()},{definition})");
        catalog.Database.ExecuteSql(
            $"""
            INSERT INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES
                ({Drone},{(int)DogmaAttributeId.ShieldCapacity},{Capacity}),
                ({Drone},{(int)DogmaAttributeId.ArmorHitPoints},{Capacity}),
                ({Drone},{(int)DogmaAttributeId.StructureHitPoints},{Capacity}),
                ({Drone},{(int)DogmaAttributeId.ShieldRechargeTime},1000000000),
                ({Drone},{(int)DogmaAttributeId.SignatureRadius},40)
            """
        );
    }

    private static void InstallProfile(WorldDatabase database)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        MissionProfile profile = new(
            new(
                Definition,
                1,
                500,
                false,
                "Test authored",
                "1",
                [
                    new([new([Drone], 1, 15000)], SourceRoom),
                    new([new([Drone], 1, 15000, Required: false)], DecoyRoom),
                    Threshold(MissionHealthLayer.Shield, 0.75),
                    Threshold(MissionHealthLayer.Armor, 0.5),
                    Threshold(MissionHealthLayer.Hull, 1),
                    Threshold(MissionHealthLayer.Hull, 0),
                    new([new([Drone], 1, 15000, Required: false)], SourceRoom),
                ]
            ),
            "Emergency relay drill",
            0,
            0,
            "",
            Definition,
            0,
            0,
            0,
            0,
            0,
            [new(SourceRoom, SpacePosition.Zero, [], []), new(DecoyRoom, s_decoyOffset, [], [])]
        );
        var definition = new CatalogDefinition<MissionProfile?>("agent-mission", (_, id) => id == Definition ? profile : null);
        Assert.Same(profile, catalog.Definition(definition, Definition));
        Assert.Same(profile, MissionCatalog.Find(catalog, Definition));
    }

    private static MissionWave Threshold(MissionHealthLayer layer, double fraction) =>
        new([new([Drone], 1, 35000)], SourceRoom, MissionWaveTrigger.NpcHealthThreshold, HealthTrigger: new(0, Drone, layer, fraction));

    private static void Seed(LiveWorld live, long now)
    {
        using var action = live.Economy.Begin(new(systems: [System]));
        action.Add(
            new SavedMission
            {
                Id = Mission,
                MissionId = Definition,
                PilotId = MissionFixture.PilotId,
                AgentId = MissionFixture.AgentId,
                SystemId = System,
                State = MissionState.Accepted,
                AcceptedAt = now,
                ExpiresAt = now + TimeSpan.FromMinutes(5).Ticks,
                X = s_position.X,
                Y = s_position.Y,
                Z = s_position.Z,
            }
        );
        action.Commit();
    }

    private static CombatDamageSettlement Hit(LiveWorld live, WorldDatabase database, int wave, int amount, long now, bool commit = true)
    {
        using var action = live.Economy.Begin(new(systems: [System]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = action.Pilots.Find(MissionFixture.PilotId)!;
        var npc = FindNpc(action, wave);
        var profile = new NpcProfileCatalog().Read(catalog, npc.TypeId);
        var result = NpcDamagePersistence.Apply(
            action,
            catalog,
            npc,
            pilot,
            pilot.ActiveShipId,
            profile.Attributes,
            new DamageVector(amount, 0, 0, 0),
            now,
            bypassResistances: true
        );
        if (commit)
        {
            action.Commit();
        }
        return result;
    }

    private static WorldNpc FindNpc(WorldAction action, int wave)
    {
        var member = action.Filter<SavedMissionMember>(m => m.MissionId == Mission && m.Wave == wave).Single();
        return action.Set<WorldNpc>().Find(member.NpcId)!;
    }

    private static int[] Spawned(LiveWorld live) =>
        live.Economy.Inspect(action =>
            action.Filter<SavedMissionMember>(member => member.MissionId == Mission).Select(member => member.Wave).Order().ToArray()
        );

    private static bool Complete(LiveWorld live) =>
        live.Economy.Inspect(action => action.Set<SavedMission>().Find(Mission)!.ObjectiveComplete);
}
