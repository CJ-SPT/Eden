using System.Text.Json;
using Eden.World.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Fixtures;

internal static class TestCatalog
{
    private static readonly Lazy<byte[]> Template = new(CreateTemplate);

    private static byte[] CreateTemplate()
    {
        string directory = TestDirectory.Create("eden-catalog-template-").FullName;
        try
        {
            string path = Path.Combine(directory, "catalog.sqlite");
            Build(path);
            return File.ReadAllBytes(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static void AddSchool(
        string path,
        int schoolId,
        int raceId,
        int bloodlineId,
        int systemId,
        int stationId,
        int hull,
        bool starterSpace = false
    )
    {
        using CatalogDbContext db = new(path, writable: true);
        db.Database.ExecuteSqlRaw(
            "CREATE VIEW IF NOT EXISTS records_client_schoolmap AS SELECT Key,Data FROM SourceRecord WHERE Dataset='client/schoolmap'"
        );
        db.Database.ExecuteSql($"INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES ({systemId},20000020,'School system')");
        db.Database.ExecuteSql(
            $"""
            INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
            VALUES ({stationId},52678,1000035,{systemId},14,40009087,1,2,3)
            """
        );
        if (hull != 601)
        {
            db.Database.ExecuteSql(
                $"INSERT INTO ItemType(Id,Name,GroupId,Capacity,Mass,Radius) VALUES ({hull},'Starter ship',237,125,1163000,47)"
            );
            db.Database.ExecuteSql($"INSERT INTO TypeAttribute SELECT {hull},AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=601");
        }
        Add("client/races", raceId, new { shipTypeID = hull, skills = new Dictionary<string, int> { ["3327"] = 3 } });
        Add("client/bloodlines", bloodlineId, new { raceID = raceId });
        // The first station in the list is deliberately in the wrong system.
        Add(
            "client/schools",
            schoolId,
            new
            {
                raceID = raceId,
                corporationID = 1000044,
                is_starter_space_school = starterSpace ? 1 : 0,
                startingStations = starterSpace ? new[] { stationId } : new[] { 60003760, stationId },
            }
        );
        if (!starterSpace)
        {
            Add("client/schoolmap", schoolId - 10, new { schoolID = schoolId, solarSystemID = systemId });
        }

        void Add(string dataset, int key, object value)
        {
            db.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data) VALUES ({dataset},{key},{JsonSerializer.Serialize(value)})"
            );
        }
    }

    public static void Create(string path)
    {
        // Only immutable seed bytes are shared. Each test owns its writable copy.
        byte[] template = Template.Value;
        using FileStream file = new(path, FileMode.CreateNew);
        file.Write(template);
    }

    private static void Build(string path)
    {
        using CatalogDbContext db = new(path, writable: true);
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw(
            """
            PRAGMA application_id=0x4544454E;
            PRAGMA user_version=1;
            INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('sde/_sde','"sde"','{{"buildNumber":3662168}}');
            INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/races','1','{{"shipTypeID":601,"skills":{{"3327":3}}}}');
            INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/clonegrades','"1"','{{"skills":[{{"typeID":3327,"level":4}}]}}');
            INSERT INTO SourceRecord VALUES ('client/typedogma','3651',
                '{{"dogmaEffects":[{{"effectID":12,"isDefault":0}},
                {{"effectID":16,"isDefault":0}},
                {{"effectID":42,"isDefault":0}},
                {{"effectID":67,"isDefault":1}}]}}');
            -- Build 3662168 effect metadata, including the targeted mining contract.
            INSERT INTO SourceRecord VALUES ('client/dogmaeffects','12','{{"effectID":12,"effectCategory":0}}'),
                ('client/dogmaeffects','16','{{"effectID":16,"effectCategory":1}}'),
                ('client/dogmaeffects','42','{{"effectID":42,"effectCategory":0}}'),
                ('client/dogmaeffects','67','{{"effectID":67,"effectCategory":2,"rangeAttributeID":54,
                    "dischargeAttributeID":6,"durationAttributeID":73,"isWarpSafe":0}}');
            INSERT INTO SourceRecord VALUES ('client/typedogma','3638',
                '{{"dogmaEffects":[{{"effectID":12,"isDefault":0}},
                {{"effectID":16,"isDefault":0}},
                {{"effectID":42,"isDefault":0}},
                {{"effectID":10,"isDefault":1}}]}}');
            INSERT INTO SourceRecord VALUES ('client/typedogma','483',
                '{{"dogmaEffects":[{{"effectID":12,"isDefault":0}},
                {{"effectID":16,"isDefault":0}},
                {{"effectID":42,"isDefault":0}},
                {{"effectID":67,"isDefault":1}}]}}');
            INSERT INTO SourceRecord VALUES ('client/typedogma','482',
                '{{"dogmaEffects":[{{"effectID":12,"isDefault":0}},
                {{"effectID":16,"isDefault":0}},
                {{"effectID":42,"isDefault":0}},
                {{"effectID":67,"isDefault":1}}]}}');
            INSERT INTO SourceRecord VALUES ('client/typedogma','561',
                '{{"dogmaEffects":[{{"effectID":12,"isDefault":0}},
                {{"effectID":16,"isDefault":0}},
                {{"effectID":42,"isDefault":0}},
                {{"effectID":34,"isDefault":1}},{{"effectID":3001,"isDefault":0}}]}}');
            INSERT INTO SourceRecord VALUES ('client/typedogma','215',
                '{{"dogmaEffects":[{{"effectID":596}},{{"effectID":804}}]}}'),
                ('client/typedogma','222','{{"dogmaEffects":[{{"effectID":596}},{{"effectID":804}}]}}');
            INSERT INTO SourceRecord VALUES ('client/typedogma','399',
                '{{"dogmaEffects":[{{"effectID":13,"isDefault":0}},
                {{"effectID":16,"isDefault":0}},
                {{"effectID":4,"isDefault":1}}]}}');
            INSERT INTO SourceRecord VALUES ('client/typedogma','439',
                '{{"dogmaEffects":[{{"effectID":13,"isDefault":0}},
                {{"effectID":16,"isDefault":0}},
                {{"effectID":6731,"isDefault":1}}]}}');
            CREATE VIEW records_client_clonegrades AS SELECT Key,Data FROM SourceRecord WHERE Dataset='client/clonegrades';
            CREATE VIEW records_client_requiredskillsfortypes AS SELECT Key,Data FROM SourceRecord WHERE
                Dataset='client/requiredskillsfortypes';
            CREATE VIEW records_client_races AS SELECT Key,Data FROM SourceRecord WHERE Dataset='client/races';
            CREATE VIEW StationDesignation AS SELECT 60003760 AS StationId, NULL AS OrbitName, 4 AS PlanetNumber, 4 AS MoonNumber,
                'Assembly Plant' AS Operation
                UNION ALL SELECT Id,NULL,1,NULL,NULL FROM Station WHERE Id <> 60003760;
            INSERT INTO Region(Id,Name) VALUES (10000002,'The Forge');
            INSERT INTO Constellation(Id,RegionId,Name) VALUES (20000020,10000002,'Kimotoro');
            INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES (30000142,20000020,'Jita');
            INSERT INTO ItemCategory(Id,Name) VALUES (6,'Ship'),(3,'Station');
            INSERT INTO ItemCategory(Id,Name) VALUES (7,'Module');
            INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (54,'Mining Laser',7);
            INSERT INTO ItemType(Id,Name,GroupId) VALUES (3651,'Civilian Miner',54);
            INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (53,'Energy Weapon',7);
            INSERT INTO ItemType(Id,Name,GroupId) VALUES (3638,'Civilian Gatling Railgun',53);
            INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/types','3638','{{"volume":5.0}}');
            INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/types','3651','{{"published":1,"marketGroupID":1,"volume":5.0}}'),
                ('client/types','601','{{"volume":15000.0}}');
            INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (237,'Corvette',6),(15,'Station',3);
            INSERT INTO ItemType(Id,Name,GroupId,Capacity,Mass,Radius) VALUES
                (601,'Ibis',237,125,1163000,47),(52678,'Station',15,0,0,100000);
            INSERT INTO NpcCorporation(Id,Name) VALUES (1000044,'Pilot corporation'),(1000035,'Caldari Navy');
            INSERT INTO DogmaAttribute(Id,Name) VALUES (102,'turretSlotsLeft'),(37,'maxVelocity'),(70,'agility'),(9,'hp'),
                (482,'capacitorCapacity'),(55,'rechargeRate'),(263,'shieldCapacity'),(265,'armorHP'),(479,'shieldRechargeRate'),
                (600,'warpSpeedMultiplier'),(1281,'baseWarpSpeed');
            INSERT INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES (601,102,2),(601,37,295),(601,70,4.5),(601,9,175),
                (601,482,130),(601,55,65000),(601,263,200),(601,265,125),(601,479,860000),(601,600,3),(601,1281,1);
            INSERT INTO ItemCategory(Id,Name) VALUES (16,'Skill');
            INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (257,'Spaceship Command',16);
            INSERT INTO ItemType(Id,Name,GroupId) VALUES (3327,'Spaceship Command',257);
            INSERT INTO DogmaAttribute(Id,Name) VALUES (275,'skillTimeConstant'),(180,'primaryAttribute'),(181,'secondaryAttribute');
            INSERT INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES (3327,275,1),(3327,180,167),(3327,181,168);
            INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES (40009087,30000142,52673,8,1620000,0,0,0);
            INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES (30000144,20000020,'Perimeter');
            INSERT INTO ItemCategory(Id,Name) VALUES (2,'Celestial');
            INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (8,'Moon',2);
            INSERT INTO ItemType(Id,Name,GroupId) VALUES (52673,'Moon',8);
            INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (10,'Stargate',2);
            INSERT INTO ItemType(Id,Name,GroupId,Radius) VALUES (16,'Stargate (Caldari System)',10,3532);
            INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z) VALUES
                (50001249,30000142,16,10,-4067676856320,-710577070080,-3956622581760),
                (50002185,30000144,16,10,1e10,2e10,3e10);
            INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES (50001249,30000142,50002185),(50002185,30000144,50001249);
            INSERT INTO ItemType(Id,Name,GroupId,Radius) VALUES (30577,'Pulsar',8,10000),(56247,'Disrupted gate',10,4500);
            INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/solarsystemcontent','30000144',
                '{{"secondarySun":{{"itemID":40455847,"typeID":30577,"position":{{"x":1,"y":2,"z":3}}}},
                "disruptedStargates":{{"50016295":{{"typeID":56247,"position":{{"x":4,"y":5,"z":6}}}}}}}}');
            INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                VALUES (60003760,52678,1000035,30000142,14,40009087,-107303362560,-18744975360,436489052160);
            """
        );
        PassiveCatalogFixture.LoadWeaponMetadata(path, new HashSet<int>());
    }
}
