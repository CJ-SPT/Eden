using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Fixtures;

internal static class TestCatalog
{
    public static void Create(string path)
    {
        using CatalogDbContext db = new(path, writable: true);
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw("""
            PRAGMA application_id=0x4544454E;
            PRAGMA user_version=1;
            INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('sde/_sde','"sde"','{{"buildNumber":3662168}}');
            CREATE VIEW StationDesignation AS SELECT 60003760 AS StationId, NULL AS OrbitName, 4 AS PlanetNumber, 4 AS MoonNumber, 'Assembly Plant' AS Operation;
            INSERT INTO Region(Id,Name) VALUES (10000002,'The Forge');
            INSERT INTO Constellation(Id,RegionId,Name) VALUES (20000020,10000002,'Kimotoro');
            INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES (30000142,20000020,'Jita');
            INSERT INTO ItemCategory(Id,Name) VALUES (6,'Ship'),(3,'Station');
            INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (237,'Corvette',6),(15,'Station',3);
            INSERT INTO ItemType(Id,Name,GroupId,Capacity,Mass,Radius) VALUES
                (601,'Ibis',237,125,1163000,47),(52678,'Station',15,0,0,100000);
            INSERT INTO NpcCorporation(Id,Name) VALUES (1000044,'Pilot corporation'),(1000035,'Caldari Navy');
            INSERT INTO DogmaAttribute(Id,Name) VALUES (102,'turretSlotsLeft'),(37,'maxVelocity'),(70,'agility'),(9,'hp'),(482,'capacitorCapacity'),(55,'rechargeRate'),(263,'shieldCapacity'),(265,'armorHP'),(479,'shieldRechargeRate'),(600,'warpSpeedMultiplier'),(1281,'baseWarpSpeed');
            INSERT INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES (601,102,2),(601,37,295),(601,70,4.5),(601,9,175),(601,482,130),(601,55,65000),(601,263,200),(601,265,125),(601,479,860000),(601,600,3),(601,1281,1);
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
            """);
    }
}
