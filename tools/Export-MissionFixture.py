"""Export pinned mission definitions, geometry, NPCs and dependencies for offline automated tests."""
import argparse
import json
import pathlib
import re
import sqlite3


def export(catalog, output):
    root = pathlib.Path(__file__).resolve().parent.parent
    content = json.loads((root / 'src/Eden.World/Agents/missions-3662168.json').read_text(encoding='utf-8'))
    db = sqlite3.connect(catalog.resolve().as_uri() + '?mode=ro', uri=True)
    records = {}
    pending = {i for m in content['missions'] for w in m['waves'] for g in w['groups'] for i in g['types']}
    pending.update([3355, 3356, 3357, 3359, 3361, 3894, 3895])
    # Existing NPC loss settlement uses the shared Eden ordinary/commander loot policy.
    pending.update([222, 230, 238, 185, 193, 201, 246, 250, 254, 210, 209, 203, 399, 10836, 10838,
                    523, 3528, 3538, 520, 9944, 2363, 12274, 1195, 15331])
    rewards = (root / 'src/Eden.Gameplay/Combat/NpcCommanderLootRules.cs').read_text(encoding='utf-8')
    pending.update(int(v) for v in re.findall(r'^    \w+ = (\d+)', rewards, re.MULTILINE))

    def read(dataset, key):
        row = db.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?', (dataset, str(key))).fetchone()
        value = json.loads(row[0]) if row else None
        if value is not None:
            records[dataset, int(key)] = value
        return value

    for definition in content['missions']:
        mission = read('client/missions', definition['missionId'])
        read('sde/missions', definition['missionId'])
        for key in [mission['nameID'], *mission.get('messages', {}).values()]:
            read('client/localization_fsd_en-us', key)
        if mission.get('courierMission'):
            pending.add(mission['courierMission']['objectiveTypeID'])
        if mission.get('killMission'):
            dungeon = read('client/dungeons', mission['killMission']['dungeonID'])
            pending.update(o['typeID'] for r in dungeon['rooms'].values() for o in r['objects'].values())
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
        pending.update(int(v['value']) for v in values if v['id'] == 507 and v['value'])
    for key in attributes:
        read('client/dogmaattributes', key)
    for faction in [500010, 500011]:
        data = read('client/factions', faction)
        read('client/npccorporations', data['corporationID'])
        read('sde/npcCorporations', data['corporationID'])
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(dict(build=3662168, types=[types[k] for k in sorted(types)], records=[
        dict(dataset=d, key=k, data=v) for (d, k), v in sorted(records.items())]), indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    print(f'Exported {len(types)} types and {len(records)} source records')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', required=True, type=pathlib.Path)
    parser.add_argument('--output', type=pathlib.Path, default=pathlib.Path('tests/Eden.Protocol.Tests/Fixtures/MissionCatalog.json'))
    args = parser.parse_args()
    export(args.catalog, args.output)
