"""Export pinned mission definitions, geometry, NPCs and dependencies for offline automated tests."""
import argparse
import json
import pathlib
import re
import sqlite3
from mission_content import ship_types, special_agent_mission


def export(catalog, output, career=False):
    root = pathlib.Path(__file__).resolve().parent.parent
    manifest = 'career-missions-3662168.json' if career else 'missions-3662168.json'
    content = json.loads((root / 'src/Eden.World/Agents' / manifest).read_text(encoding='utf-8'))
    db = sqlite3.connect(catalog.resolve().as_uri() + '?mode=ro', uri=True)
    records = {}
    factions = set()
    systems = set()
    corporations = set()
    stations = {}
    pending = {i for m in content['missions'] for w in m['waves'] for g in w['groups'] for i in g['types']}
    pending.update(i for m in content['missions'] for i in m.get('itemReward', {}).get('types', []))
    pending.update([23, 2076, 670, 3355, 3356, 3357, 3359, 3361, 3893, 3894, 3895])
    # Existing NPC loss settlement uses the shared Eden ordinary/commander loot policy.
    pending.update([222, 230, 238, 185, 193, 201, 246, 250, 254, 210, 209, 203, 399, 10836, 10838,
                    523, 3528, 3538, 520, 9944, 2363, 12274, 1195, 15331])
    for rules in ['NpcLootRules.cs', 'NpcCommanderLootRules.cs']:
        rewards = (root / 'src/Eden.Gameplay/Combat' / rules).read_text(encoding='utf-8')
        pending.update(int(v) for v in re.findall(r'^    \w+ = (\d+)', rewards, re.MULTILINE))

    def read(dataset, key):
        encoded = json.dumps(str(key)) if dataset == 'client/blueprints' else str(key)
        row = db.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?', (dataset, encoded)).fetchone()
        value = json.loads(row[0]) if row else None
        if value is not None:
            records[dataset, int(key)] = value
        return value

    read('client/typelist', 459)  # Native ship-list expansion used by gate restriction tests.
    for aid in sorted({aid for m in content['missions'] for aid in m.get('fixedAgentIds', [])}):
        npc = read('client/npccharacters', aid)
        read('client/localization_fsd_en-us', npc['nameID'])
        corporations.add(npc['corporationID'])
        if career:
            station = npc['stationID']
            if station not in stations:
                row = db.execute('SELECT s.Id,s.SolarSystemId,s.TypeId,s.OwnerId,n.Name,s.OperationId,s.OrbitId,s.X,s.Y,s.Z '
                                 'FROM Station s JOIN NpcCorporation n ON n.Id=s.OwnerId WHERE s.Id=?', (station,)).fetchone()
                stations[station] = dict(zip(('id', 'solarSystemId', 'typeId', 'ownerId', 'ownerName', 'operationId', 'orbitId',
                                              'x', 'y', 'z'), row))
                read('client/stations', station)
                read('sde/npcStations', station)
                systems.add(stations[station]['solarSystemId'])
                corporations.add(stations[station]['ownerId'])
                pending.add(stations[station]['typeId'])
            read('sde/npcCharacters', aid)
            continue
        location = read('client/agentsinspace', aid)
        systems.add(location['solarSystemID'])
        dungeon = read('client/dungeons', location['dungeonID'])
        pending.add(location['typeID'])
        pending.update(o['typeID'] for r in dungeon['rooms'].values() for o in r['objects'].values())
        if dungeon.get('dungeonNameID'):
            read('client/localization_fsd_en-us', dungeon['dungeonNameID'])
    special = []
    if not career:
        for key, raw in db.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='client/npccharacters' AND Data LIKE '%1000419%'"):
            npc = json.loads(raw)
            if npc.get('corporationID') == 1000419 and not npc.get('deleted') and npc.get('agent', {}).get('agentType') == 13:
                read('client/npccharacters', key)
                read('client/localization_fsd_en-us', npc['nameID'])
        hulls = ship_types(db)
        special = [dict(missionId=int(key), waves=[]) for key, raw in db.execute(
            "SELECT Key,Data FROM SourceRecord WHERE Dataset='client/missions' AND (Data LIKE '%HeraldryMission%' OR Data LIKE '%Research%')"
        ) if special_agent_mission(int(key), json.loads(raw), hulls)]
    for definition in [*content['missions'], *special]:
        mission = read('client/missions', definition['missionId'])
        if mission.get('factionID'):
            factions.add(mission['factionID'])
        if mission.get('corporationID'):
            corporations.add(mission['corporationID'])
        sde = read('sde/missions', definition['missionId'])
        # World uses this record only for its English name fallback. Localized
        # client messages below retain the actual offer and briefing payloads.
        records['sde/missions', definition['missionId']] = dict(name=dict(en=sde['name']['en']))
        for key in [mission['nameID'], *mission.get('messages', {}).values()]:
            read('client/localization_fsd_en-us', key)
        if mission.get('courierMission'):
            pending.add(mission['courierMission']['objectiveTypeID'])
        if mission.get('killMission'):
            if mission['killMission'].get('objectiveTypeID'):
                pending.add(mission['killMission']['objectiveTypeID'])
        if career:
            pending.update(i for i in (definition.get('giftTypeId', mission.get('initialAgentGiftTypeID')), definition.get('objectiveTypeId')) if i)
            pending.update(reward['rewardTypeID'] for reward in mission.get('missionRewards', {}).values()
                           if isinstance(reward, dict) and reward.get('rewardTypeID'))
            pending.update(i['typeId'] for c in definition.get('containers', []) for i in [c, *c.get('additionalItems', [])])
            pending.update(g['key']['typeId'] for g in definition.get('gates', []) if g.get('key'))
            pending.update(g['lootTypeId'] for w in definition['waves'] for g in w['groups'] if g.get('lootTypeId'))
        dungeon_id = definition.get('dungeonId', mission.get('killMission', {}).get('dungeonID'))
        if dungeon_id:
            dungeon = read('client/dungeons', dungeon_id)
            factions.add(dungeon['factionID'])
            factions.update(g['enemyFaction'] for w in definition['waves'] for g in w['groups'] if g.get('enemyFaction'))
            pending.update(o['typeID'] for r in dungeon['rooms'].values() for o in r['objects'].values())
            if career and dungeon.get('dungeonNameID'):
                read('client/localization_fsd_en-us', dungeon['dungeonNameID'])
            named_objects = {c['objectId'] for c in definition.get('containers', [])}
            named_objects.update(s['objectId'] for s in definition.get('structures', []))
            named_objects.update(definition[k]['objectId'] for k in ('careerVisit', 'careerHazard') if definition.get(k))
            for room in dungeon['rooms'].values():
                for obj in room['objects'].values():
                    if obj.get('objectID') in named_objects and obj.get('objectNameID'):
                        read('client/localization_fsd_en-us', obj['objectNameID'])
            for gate in dungeon.get('connections', []):
                for field in ['allowedShipsList', 'disallowedFittedTypesList']:
                    if gate.get(field):
                        read('client/typelist', gate[field])
    types, attributes = {}, set()
    while pending:
        key = pending.pop()
        if key in types:
            continue
        row = db.execute('SELECT t.Id,t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity '
                         'FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?', (key,)).fetchone()
        if not row:
            raise ValueError(f'Missing type {key}')
        values = [dict(id=i, value=v) for i, v in db.execute('SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=?', (key,))]
        attributes.update(v['id'] for v in values)
        types[key] = dict(zip(('id', 'name', 'groupId', 'groupName', 'categoryId', 'mass', 'radius', 'capacity'), row), attributes=values)
        for dataset in ['client/types', 'client/typedogma', 'client/spacecomponentsbytype']:
            value = read(dataset, key)
            if dataset == 'client/spacecomponentsbytype' and value is not None:
                records.pop((dataset, key))
                if 'entityStandings' in value:
                    records[dataset, key] = dict(entityStandings=value['entityStandings'])
            if dataset == 'client/types' and value and value.get('wreckTypeID'):
                pending.add(value['wreckTypeID'])
            if career and dataset == 'client/typedogma' and value:
                for effect in value.get('dogmaEffects', []):
                    read('client/dogmaeffects', effect['effectID'])
        if career:
            pending.update(int(v['value']) for v in values if v['id'] in (182, 183, 184, 1285, 1289, 1290) and v['value'] > 0)
            if row[4] == 9 and (blueprint := read('client/blueprints', key)):
                manufacturing = blueprint.get('activities', {}).get('manufacturing', {})
                pending.update(i['typeID'] for field in ('materials', 'products', 'skills') for i in manufacturing.get(field, []))
        pending.update(int(v['value']) for v in values if v['id'] == 507 and v['value'])
    for key in attributes:
        read('client/dogmaattributes', key)
    for faction in sorted(factions):
        data = read('client/factions', faction) or {}
        if data.get('corporationID'):
            corporations.add(data['corporationID'])
    for corporation in sorted(corporations):
        read('client/npccorporations', corporation)
        read('sde/npcCorporations', corporation)
    output.parent.mkdir(parents=True, exist_ok=True)
    geography = [dict(zip(('id', 'name', 'constellationId', 'constellationName', 'regionId', 'regionName'),
                         db.execute('SELECT s.Id,s.Name,c.Id,c.Name,r.Id,r.Name FROM SolarSystem s '
                                    'JOIN Constellation c ON c.Id=s.ConstellationId JOIN Region r ON r.Id=c.RegionId '
                                    'WHERE s.Id=?', (system,)).fetchone())) for system in sorted(systems)]
    fixture = dict(build=3662168, systems=geography, types=[types[k] for k in sorted(types)], records=[
        dict(dataset=d, key=k, data=v) for (d, k), v in sorted(records.items())])
    if career:
        fixture['stations'] = [stations[key] for key in sorted(stations)]
        orbit_ids = {station['orbitId'] for station in stations.values()}
        for system in systems:
            planet = db.execute('SELECT Id FROM Celestial WHERE SolarSystemId=? AND GroupId=7 ORDER BY Id LIMIT 1', (system,)).fetchone()
            if planet:
                orbit_ids.add(planet[0])
        fixture['celestials'] = [dict(zip(('id', 'solarSystemId', 'typeId', 'groupId', 'radius', 'orbitId', 'x', 'y', 'z'),
                                         db.execute('SELECT Id,SolarSystemId,TypeId,GroupId,Radius,OrbitId,X,Y,Z '
                                                    'FROM Celestial WHERE Id=?', (key,)).fetchone())) for key in sorted(orbit_ids)]
    output.write_text(json.dumps(fixture, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    print(f'Exported {len(types)} types and {len(records)} source records')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', required=True, type=pathlib.Path)
    parser.add_argument('--output', type=pathlib.Path)
    parser.add_argument('--career', action='store_true')
    args = parser.parse_args()
    name = 'CareerMissionCatalog.json' if args.career else 'MissionCatalog.json'
    export(args.catalog, args.output or pathlib.Path('tests/Eden.Protocol.Tests/Fixtures') / name, args.career)
