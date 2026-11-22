using Eden.World.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerMilitiaPatrolTests
{
    private static void PrepareCatalog(string path)
    {
        TestCatalog.Create(path);
        PassiveCatalogFixture.LoadFrigateHulls(path);
        PassiveCatalogFixture.LoadWeaponTiers(path, new HashSet<int> { Railgun, Antimatter });
        using CatalogDbContext catalog = new(path, writable: true);
        // Build 3662168 station and reciprocal gate identities, positions, type radii, and ownership.
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES
                (30003837,20000020,'Aldranette'),(30045344,20000020,'Nennamaila');
            INSERT INTO SourceRecord VALUES ('client/systems','30003837','{{"securityStatus":0.36751049757003784,"factionID":500004}}'),
                ('client/systems','30045344','{{"securityStatus":0.2742577791213989,"factionID":500001}}');
            INSERT OR IGNORE INTO ItemGroup VALUES (7,'Planet',2);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Mass,Radius) VALUES
                (3876,'Stargate (Gallente Border)',10,100000000000,19410),(3873,'Stargate (Caldari Border)',10,100000000000,14051),
                (3870,'Gallente Mining Station',15,0,28083),(1529,'Caldari Administrative Station',15,0,45734),
                (13,'Planet (Gas)',7,0,0),(14,'Moon',8,0,0);
            INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z) VALUES
                (40242866,30003837,14,8,480000,-502901922418,-25496611586,-943652342964),
                (40349828,30045344,13,7,23130000,-5175245660671.799,-648287773186.7886,-707998767091.5819);
            INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z) VALUES
                (50016294,30003837,3876,10,576992256000,-1171516661760,-1354355712000),
                (50016293,30045344,3873,10,-2776254013440,5636866252800,6516613570560);
            INSERT INTO Stargate(Id,SolarSystemId,DestinationId) VALUES
                (50016294,30003837,50016293),(50016293,30045344,50016294);
            INSERT OR IGNORE INTO NpcCorporation(Id,Name) VALUES
                (1000110,'FedMart'),(1000042,'Wiyrkomi Peace Corps'),
                (1000180,'State Protectorate'),(1000181,'Federal Defense Union');
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/npccorporations','1000110','{{"factionID":500004}}'),
                ('client/npccorporations','1000042','{{"factionID":500001}}'),
                ('client/npccorporations','1000180','{{"factionID":500001}}'),
                ('client/npccorporations','1000181','{{"factionID":500004}}'),
                ('client/stationoperations','14','{{"services":[7]}}'),('client/stationoperations','27','{{"services":[7]}}');
            INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z) VALUES
                (60011014,3870,1000110,30003837,27,40242866,-502902743040,-25496985600,-943651921920),
                (60015126,1529,1000042,30045344,14,40349828,-5175226490880,-648303943680,-707968327680);
            """
        );

        // The generation-zero Aldranette Scout layout retains the pinned gate, beacon, capture point, and defender locator.
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemCategory VALUES (11,'Entity');
            INSERT OR IGNORE INTO ItemGroup VALUES
                (310,'Beacon',2),(366,'Warp Gate',2),(922,'Capture Point',11),(1168,'FW Federation Frigate',11),(29,'Capsule',6);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Mass,Radius) VALUES
                (17831,'Acceleration Gate',366,100000,12706),(75917,'Scout Navy Military Beacon',310,1,30000),
                (29422,'30km Gallente Capture Point',922,1000000,782),(75361,'Federation Frigate',1168,1190000,29),
                (670,'Capsule',29,32000,2);
            INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES
                (9,'hp'),(37,'maxVelocity'),(51,'speed'),(54,'maxRange'),(64,'damageMultiplier'),(70,'agility'),
                (117,'kineticDamage'),(118,'thermalDamage'),(158,'falloff'),(160,'trackingSpeed'),(245,'gfxTurretID'),
                (247,'entityAttackRange'),(263,'shieldCapacity'),(265,'armorHP'),(479,'shieldRechargeRate'),
                (482,'capacitorCapacity'),(552,'signatureRadius'),(620,'optimalSigRadius');
            INSERT OR IGNORE INTO TypeAttribute (ItemTypeId,AttributeId,Value) VALUES
                (75361,9,250),(75361,37,2500),(75361,51,5000),(75361,54,15000),(75361,64,20),(75361,70,2),
                (75361,117,6),(75361,118,4),(75361,158,5000),(75361,160,112),(75361,245,565),(75361,247,20000),
                (75361,263,250),(75361,265,500),(75361,479,300000),(75361,482,350),(75361,552,40),(75361,620,40000);
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/typedogma','75361','{{"dogmaEffects":[{{"effectID":10}}]}}'),
                ('client/typelist','511','{{"includedGroupIDs":[25],"excludedTypeIDs":[17930,17932,17926,33816,
                    17924,17928,33468,47269,54731,77114]}}'),
                ('client/typelist','984','{{"includedGroupIDs":[315]}}');
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/dungeons','9601','{{"archetypeID":33,"dungeonNameID":624282,
                "difficulty":4,"entryTypeID":28356,"rooms":{{
                    "1":{{"objects":{{"1138164":{{"typeID":17831,"objectID":1138164}}}}}},
                    "2":{{"objects":{{
                        "1138163":{{"typeID":75917,"objectID":1138163,"position":{{"x":6000,"y":8000,"z":365}}}},
                        "1138166":{{"typeID":29422,"objectID":1138166,"position":{{"x":6000,"y":8000,"z":365}}}},
                        "1362924":{{"typeID":90808,"objectID":1362924,
                            "position":{{"x":6277.3818359375,"y":9623.87109375,"z":576.1682739257812}}}}
                    }}}}
                }},"connections":[{{"fromObjectID":1138164,"allowedShipsList":511,"disallowedFittedTypesList":984}}]}}');
            """
        );
    }
}
