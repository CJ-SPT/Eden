using System.Text.Json;
using Eden.Data.Catalog.FactionWarfare;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.FactionWarfare;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerMilitiaPatrolTests
{
    private sealed record PatrolScenario(
        int Seed,
        int Frontline,
        int BorderSystem,
        int AttackerSystem,
        MilitiaFaction DefenderFaction,
        MilitiaFaction AttackerFaction,
        int? AlternativeFrontline = null
    )
    {
        internal int DefenderStation => 60_040_000 + Seed * 10;
        internal int AttackerStation => DefenderStation + 1;
        internal int DungeonId => Scout.Dungeon(DefenderFaction);
        internal int[] Route => [AttackerSystem, BorderSystem, Frontline];
        internal MilitiaComplexVariant Scout =>
            MilitiaComplexVariants.All.Where(variant => variant.Family == MilitiaComplexFamily.Scout).ElementAt(Frontline % ScoutVariants);

        private const int ScoutVariants = 4;

        internal static PatrolScenario FromSeed(int seed)
        {
            var roster = FactionWarzoneCatalog.Systems;
            var warzone = seed % 2 == 0 ? WarzoneId.CaldariGallente : WarzoneId.AmarrMinmatar;
            var candidates = (
                from front in FactionWarzoneCatalog.Roster
                where FactionWarzoneCatalog.Warzone(front.OriginalOccupier).Id == warzone && front.SystemId % ScoutVariants == 1
                from borderId in front.Neighbors
                let border = roster[borderId]
                where border.OriginalOccupier != front.OriginalOccupier
                from stagingId in border.Neighbors
                where roster[stagingId].OriginalOccupier == border.OriginalOccupier && !front.Neighbors.Contains(stagingId)
                select new PatrolScenario(seed, front.SystemId, borderId, stagingId, front.OriginalOccupier, border.OriginalOccupier)
            ).ToArray();
            return candidates[new Random(seed).Next(candidates.Length)];
        }

        internal static PatrolScenario WithTwoFrontlines(int seed)
        {
            var roster = FactionWarzoneCatalog.Systems;
            var candidates = (
                from border in FactionWarzoneCatalog.Roster
                let nearby = border.Neighbors.Select(id => roster[id]).ToArray()
                from stage in nearby
                where stage.OriginalOccupier == border.OriginalOccupier
                from first in nearby
                where first.OriginalOccupier != stage.OriginalOccupier
                from second in nearby
                where
                    first.SystemId < second.SystemId
                    && first.OriginalOccupier == second.OriginalOccupier
                    && first.SystemId % ScoutVariants == second.SystemId % ScoutVariants
                    && !first.Neighbors.Contains(stage.SystemId)
                    && !second.Neighbors.Contains(stage.SystemId)
                select new PatrolScenario(
                    seed,
                    first.SystemId,
                    border.SystemId,
                    stage.SystemId,
                    first.OriginalOccupier,
                    stage.OriginalOccupier,
                    second.SystemId
                )
            ).ToArray();
            return candidates[new Random(seed).Next(candidates.Length)];
        }
    }

    private static void PrepareCatalog(string path, PatrolScenario scenario, bool connectTradeHub = false)
    {
        const int gateType = 3876;
        const int stationType = 3870;
        const int moonType = 14;
        const int operation = 27;
        TestCatalog.Create(path);
        PassiveCatalogFixture.LoadFrigateHulls(path);
        PassiveCatalogFixture.LoadWeaponTiers(path, new HashSet<int> { Railgun, Antimatter });
        using CatalogDbContext catalog = new(path, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Mass,Radius) VALUES
                (3876,'Stargate (Gallente Border)',10,100000000000,19410),
                (3870,'Gallente Mining Station',15,0,28083),(14,'Moon',8,0,0);
            """
        );
        AddSource("client/stationoperations", operation, new { services = new[] { 7 } });
        AddSystem(scenario.Frontline, scenario.DefenderFaction);
        AddSystem(scenario.BorderSystem, scenario.AttackerFaction);
        AddSystem(scenario.AttackerSystem, scenario.AttackerFaction);
        AddStation(scenario.DefenderStation, scenario.Frontline, scenario.DefenderFaction, 40_100_000 + scenario.Seed * 10);
        AddStation(scenario.AttackerStation, scenario.AttackerSystem, scenario.AttackerFaction, 40_100_001 + scenario.Seed * 10);
        AddGatePair(scenario.AttackerSystem, scenario.BorderSystem, 50_100_000 + scenario.Seed * 10, 120_000_000_000);
        AddGatePair(scenario.BorderSystem, scenario.Frontline, 50_100_002 + scenario.Seed * 10, 210_000_000_000);
        if (connectTradeHub)
        {
            AddGatePair(Jita, scenario.AttackerSystem, 50_100_006 + scenario.Seed * 10, 350_000_000_000);
        }
        if (scenario.AlternativeFrontline is int alternative)
        {
            AddSystem(alternative, scenario.DefenderFaction);
            AddStation(scenario.DefenderStation + 2, alternative, scenario.DefenderFaction, 40_100_002 + scenario.Seed * 10);
            AddGatePair(scenario.BorderSystem, alternative, 50_100_004 + scenario.Seed * 10, -210_000_000_000);
        }

        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemCategory VALUES (11,'Entity');
            INSERT OR IGNORE INTO ItemGroup VALUES
                (310,'Beacon',2),(366,'Warp Gate',2),(922,'Capture Point',11),(1168,'FW Federation Frigate',11),(29,'Capsule',6);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Mass,Radius) VALUES
                (17831,'Acceleration Gate',366,100000,12706),(75917,'Scout Navy Military Beacon',310,1,30000),
                (89193,'Scout Advanced Military Beacon',310,1,30000),
                (29311,'30km Amarr Capture Point',922,1000000,782),(29421,'30km Caldari Capture Point',922,1000000,782),
                (29422,'30km Gallente Capture Point',922,1000000,782),(29423,'30km Minmatar Capture Point',922,1000000,782),
                (75351,'Imperial Frigate',1168,1190000,29),(75356,'State Frigate',1168,1190000,29),
                (75361,'Federation Frigate',1168,1190000,29),(75366,'Republic Frigate',1168,1190000,29),
                (670,'Capsule',29,32000,2);
            INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES
                (9,'hp'),(37,'maxVelocity'),(51,'speed'),(54,'maxRange'),(64,'damageMultiplier'),(70,'agility'),
                (117,'kineticDamage'),(118,'thermalDamage'),(158,'falloff'),(160,'trackingSpeed'),(245,'gfxTurretID'),
                (247,'entityAttackRange'),(263,'shieldCapacity'),(265,'armorHP'),(479,'shieldRechargeRate'),
                (482,'capacitorCapacity'),(552,'signatureRadius'),(620,'optimalSigRadius');
            INSERT OR IGNORE INTO TypeAttribute (ItemTypeId,AttributeId,Value)
                SELECT hull.Id, attr.Id, attr.Value
                FROM ItemType hull CROSS JOIN (
                    SELECT 9 AS Id,250 AS Value UNION ALL SELECT 37,2500 UNION ALL SELECT 51,5000
                    UNION ALL SELECT 54,15000 UNION ALL SELECT 64,20 UNION ALL SELECT 70,2
                    UNION ALL SELECT 117,6 UNION ALL SELECT 118,4 UNION ALL SELECT 158,5000
                    UNION ALL SELECT 160,112 UNION ALL SELECT 245,565 UNION ALL SELECT 247,20000
                    UNION ALL SELECT 263,250 UNION ALL SELECT 265,500 UNION ALL SELECT 479,300000
                    UNION ALL SELECT 482,350 UNION ALL SELECT 552,40 UNION ALL SELECT 620,40000
                ) attr WHERE hull.Id IN (75351,75356,75361,75366);
            INSERT OR REPLACE INTO SourceRecord VALUES
                ('client/typelist','511','{{"includedGroupIDs":[25],"excludedTypeIDs":[17930,17932,17926,33816,
                    17924,17928,33468,47269,54731,77114]}}'),
                ('client/typelist','984','{{"includedGroupIDs":[315]}}');
            """
        );
        int defenderType = MilitiaComplexCatalog.DefenderType(scenario.DefenderFaction, MilitiaComplexFamily.Scout);
        AddSource("client/typedogma", defenderType, new { dogmaEffects = new[] { new { effectID = 10 } } });
        const int accelerationGate = 17831;
        const int entryObject = 1138164;
        var entry = new { typeID = accelerationGate, objectID = entryObject };
        var scout = scenario.Scout;
        var capture = new SpacePosition(6_000, 8_000, 365);
        AddSource(
            "client/dungeons",
            scenario.DungeonId,
            new
            {
                archetypeID = 33,
                dungeonNameID = 624282,
                difficulty = 4,
                entryTypeID = 28356,
                rooms = new Dictionary<int, object>
                {
                    [1] = new { objects = new Dictionary<int, object> { [entryObject] = entry } },
                    [2] = new
                    {
                        objects = new Dictionary<int, object>
                        {
                            [1138163] = DungeonObject(1138163, scout.BeaconType, capture),
                            [1138166] = DungeonObject(1138166, MilitiaComplexCatalog.CapturePoint(scenario.DefenderFaction), capture),
                            [1362924] = DungeonObject(1362924, scout.DefenderSpawner, capture + new SpacePosition(277, 1_624, 211)),
                        },
                    },
                },
                connections = new[]
                {
                    new
                    {
                        fromObjectID = entryObject,
                        allowedShipsList = 511,
                        disallowedFittedTypesList = 984,
                    },
                },
            }
        );

        void AddSystem(int id, MilitiaFaction faction)
        {
            string name = $"Warzone {id}";
            catalog.Database.ExecuteSql($"INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES ({id},20000020,{name})");
            AddSource("client/systems", id, new { securityStatus = 0.3, factionID = (int)faction });
        }

        void AddStation(int id, int system, MilitiaFaction faction, int orbit)
        {
            int owner = MilitiaRules.Corporation(faction);
            string name = $"{faction} Militia";
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO NpcCorporation(Id,Name) VALUES ({owner},{name})");
            AddSource("client/npccorporations", owner, new { factionID = (int)faction });
            catalog.Database.ExecuteSql(
                $"""
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z)
                VALUES ({orbit},{system},{moonType},8,480000,-500000000000,-25000000000,-940000000000)
                """
            );
            catalog.Database.ExecuteSql(
                $"""
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                VALUES ({id},{stationType},{owner},{system},{operation},{orbit},-500000900000,-25000000000,-940000000000)
                """
            );
        }

        void AddGatePair(int first, int second, int gate, long distance)
        {
            catalog.Database.ExecuteSql(
                $"""
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z) VALUES
                    ({gate},{first},{gateType},10,{distance},190000000000,270000000000),
                    ({gate + 1},{second},{gateType},10,{-distance},-190000000000,-270000000000);
                INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES ({gate},{first},{gate + 1}),({gate + 1},{second},{gate});
                """
            );
        }

        void AddSource(string dataset, int key, object data)
        {
            string value = JsonSerializer.Serialize(data);
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data) VALUES ({dataset},{key.ToString()},{value})"
            );
        }

        static object DungeonObject(int id, int type, SpacePosition point) =>
            new
            {
                typeID = type,
                objectID = id,
                position = new
                {
                    x = point.X,
                    y = point.Y,
                    z = point.Z,
                },
            };
    }
}
