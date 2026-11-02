-- The pinned client supplies the effective values used in flight. The source
-- records retain the exact export and client representations independently.
-- Pair the two representations by their original key. The English display name
-- is localized separately in client FSD, whereas the SDE embeds translations.
CREATE TEMP VIEW Definition AS
SELECT s.Dataset, CAST(s.Key AS INTEGER) AS Id, s.Data AS Export, c.Data AS Client,
    coalesce(message.Data->>'$.text', s.Data->>'$.name.en','') AS EnglishName
FROM SourceRecord s LEFT JOIN SourceRecord c
ON c.Key=s.Key AND c.Dataset=CASE s.Dataset
    WHEN 'sde/mapRegions' THEN 'client/regions'
    WHEN 'sde/mapConstellations' THEN 'client/constellations'
    WHEN 'sde/mapSolarSystems' THEN 'client/systems'
    ELSE 'client/' || lower(substr(s.Dataset,5)) END
LEFT JOIN SourceRecord message ON message.Dataset='client/localization_fsd_en-us'
    AND message.Key=CAST(coalesce(c.Data->>'$.nameID',c.Data->>'$.typeNameID',
        c.Data->>'$.groupNameID',c.Data->>'$.categoryNameID') AS TEXT)
WHERE s.Dataset LIKE 'sde/%';

INSERT INTO NpcCorporation(Id,Name) SELECT Id, EnglishName FROM Definition WHERE Dataset='sde/npcCorporations';
INSERT INTO DogmaAttribute(Id,Name) SELECT Id, coalesce(Client->>'$.name',Export->>'$.name') FROM Definition WHERE Dataset='sde/dogmaAttributes';
INSERT INTO Region(Id,Name) SELECT Id, EnglishName FROM Definition WHERE Dataset='sde/mapRegions';
INSERT INTO Constellation(Id,Name,RegionId) SELECT Id, EnglishName, coalesce(Client->>'$.regionID', Export->>'$.regionID')
    FROM Definition WHERE Dataset='sde/mapConstellations';
INSERT INTO SolarSystem(Id,Name,ConstellationId) SELECT Id, EnglishName, coalesce(Client->>'$.constellationID', Export->>'$.constellationID')
    FROM Definition WHERE Dataset='sde/mapSolarSystems';
INSERT INTO ItemCategory(Id,Name) SELECT Id, EnglishName FROM Definition WHERE Dataset='sde/categories';
INSERT INTO ItemGroup(Id,Name,CategoryId) SELECT Id, EnglishName, coalesce(Client->>'$.categoryID', Export->>'$.categoryID')
    FROM Definition WHERE Dataset='sde/groups';
INSERT INTO ItemType(Id,Name,GroupId,Mass,Radius,Capacity) SELECT Id, EnglishName, coalesce(Client->>'$.groupID',Export->>'$.groupID'),
    coalesce(Client->>'$.mass',Export->>'$.mass'), coalesce(Client->>'$.radius',Export->>'$.radius'),
    coalesce(Client->>'$.capacity',Export->>'$.capacity') FROM Definition WHERE Dataset='sde/types';

INSERT INTO TypeAttribute(ItemTypeId,AttributeId,Value) SELECT CAST(r.Key AS INTEGER), a.value->>'$.attributeID', a.value->>'$.value'
    FROM SourceRecord r, json_each(r.Data,'$.dogmaAttributes') a WHERE r.Dataset='sde/typeDogma';
INSERT OR REPLACE INTO TypeAttribute(ItemTypeId,AttributeId,Value) SELECT CAST(r.Key AS INTEGER), a.value->>'$.attributeID', a.value->>'$.value'
    FROM SourceRecord r, json_each(r.Data,'$.dogmaAttributes') a WHERE r.Dataset='client/typedogma';

INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,OrbitId,X,Y,Z) SELECT CAST(Key AS INTEGER), Data->>'$.solarSystemID', Data->>'$.typeID', Data->>'$.groupID',
    Data->>'$.radius', Data->>'$.orbitID', Data->>'$.x', Data->>'$.y', Data->>'$.z' FROM SourceRecord WHERE Dataset='map/celestials';
INSERT INTO Station(Id,SolarSystemId,TypeId,OwnerId,OperationId,OrbitId,X,Y,Z) SELECT CAST(Key AS INTEGER), Data->>'$.solarSystemID', Data->>'$.typeID', Data->>'$.ownerID',
    Data->>'$.operationID', Data->>'$.orbitID', Data->>'$.x', Data->>'$.y', Data->>'$.z' FROM SourceRecord WHERE Dataset='map/npcStations';
INSERT INTO Stargate(Id,SolarSystemId,DestinationId) SELECT CAST(Key AS INTEGER), Data->>'$.solarSystemID', Data->>'$.destination.stargateID'
    FROM SourceRecord WHERE Dataset='sde/mapStargates';

-- Solar-system content stores each destination by gate ID inside its parent system.
INSERT OR REPLACE INTO Stargate(Id,SolarSystemId,DestinationId)
    SELECT CAST(g.key AS INTEGER), CAST(r.Key AS INTEGER), g.value->>'$.destination'
    FROM SourceRecord r, json_each(r.Data,'$.stargates') g
    WHERE r.Dataset='client/solarsystemcontent' AND g.value->>'$.destination' IS NOT NULL;

CREATE VIEW StationDesignation AS
SELECT s.Id AS StationId, location.Data->>'$.text' AS OrbitName,
    CASE WHEN orbit.Data->>'$.groupID' IN (7,8) THEN orbit.Data->>'$.celestialIndex' END AS PlanetNumber,
    CASE WHEN orbit.Data->>'$.groupID'=8 THEN orbit.Data->>'$.orbitIndex' END AS MoonNumber,
    CASE WHEN raw.Data->>'$.useOperationName' THEN coalesce(opName.Data->>'$.text',sdeOp.Data->>'$.operationName.en') END AS Operation
FROM Station s
JOIN SourceRecord raw ON raw.Dataset='map/npcStations' AND raw.Key=CAST(s.Id AS TEXT)
JOIN SourceRecord orbit ON orbit.Dataset='map/celestials' AND orbit.Key=CAST(s.OrbitId AS TEXT)
LEFT JOIN SourceRecord location ON location.Dataset='client/localization_fsd_en-us' AND location.Key=CAST(orbit.Data->>'$.celestialNameID' AS TEXT)
LEFT JOIN SourceRecord clientOp ON clientOp.Dataset='client/stationoperations' AND clientOp.Key=CAST(s.OperationId AS TEXT)
LEFT JOIN SourceRecord opName ON opName.Dataset='client/localization_fsd_en-us' AND opName.Key=CAST(clientOp.Data->>'$.operationNameID' AS TEXT)
LEFT JOIN SourceRecord sdeOp ON sdeOp.Dataset='sde/stationOperations' AND sdeOp.Key=CAST(s.OperationId AS TEXT);

PRAGMA application_id=0x4544454E;
PRAGMA user_version=1;
