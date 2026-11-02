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
            INSERT INTO DogmaAttribute(Id,Name) VALUES (102,'turretSlotsLeft'),(37,'maxVelocity'),(70,'agility'),(9,'hp'),(482,'capacitorCapacity'),(55,'rechargeRate'),(263,'shieldCapacity'),(265,'armorHP'),(479,'shieldRechargeRate');
            INSERT INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES (601,102,2),(601,37,295),(601,70,4.5),(601,9,175),(601,482,130),(601,55,65000),(601,263,200),(601,265,125),(601,479,860000);
            INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES (40009087,30000142,52673,8,1620000,0,0,0);
            INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                VALUES (60003760,52678,1000035,30000142,14,40009087,-107303362560,-18744975360,436489052160);
            """);
    }
}
