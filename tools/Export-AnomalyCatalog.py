"""Export pinned combat-anomaly geometry, NPC profiles and dependencies for automated tests."""
import argparse
import json
import pathlib
import re
import sqlite3


def export(catalog_path, output, encounters):
    root = pathlib.Path(__file__).resolve().parent.parent
    facts = json.loads(encounters.read_text(encoding='utf-8'))
    names = {e['name'] for e in facts['encounters']}
    with sqlite3.connect(catalog_path.resolve().as_uri() + '?mode=ro', uri=True) as db:
        def record(dataset, key):
            row = db.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?', (dataset, str(key))).fetchone()
            return json.loads(row[0]) if row else None
        pending = {i for e in facts['encounters'] for v in e['variants'] for w in v for g in w['groups'] for i in g['types']}
        reward_source = (root / 'src/Eden.Gameplay/Combat/NpcCommanderLootRules.cs').read_text(encoding='utf-8')
        pending.update(int(value) for value in re.findall(r'^    \w+ = (\d+)', reward_source, re.MULTILINE))
        pending.update((222, 230, 238, 185, 193, 201, 246, 250, 254, 210, 209, 203, 399, 10836, 10838,
                        523, 3528, 3538, 520, 9944, 2363, 12274, 1195, 15331))
        records = {}
        for key, raw in db.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='sde/dungeons'"):
            definition = json.loads(raw)
            if definition.get('name', {}).get('en', '').replace('Blood Raider ', 'Blood ') not in names:
                continue
            dungeon = record('client/dungeons', key)
            if not dungeon or dungeon.get('entryTypeID') != 28356:
                continue
            records['sde/dungeons', int(key)] = definition
            records['client/dungeons', int(key)] = dungeon
            pending.update(o['typeID'] for r in dungeon.get('rooms', {}).values() for o in r.get('objects', {}).values())
        types, attributes = {}, set()
        while pending:
            key = pending.pop()
            if key in types:
                continue
            row = db.execute('SELECT t.Id,t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity '
                             'FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?', (key,)).fetchone()
            if row is None:
                raise ValueError('Missing item type ' + str(key))
            values = [dict(id=i, value=v) for i, v in db.execute(
                'SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? ORDER BY AttributeId', (key,))]
            attributes.update(v['id'] for v in values)
            types[key] = dict(zip(('id', 'name', 'groupId', 'groupName', 'categoryId', 'mass', 'radius', 'capacity'), row), attributes=values)
            for dataset in ('client/types', 'client/typedogma'):
                value = record(dataset, key)
                if value is not None:
                    records[dataset, key] = value
                    if dataset == 'client/types' and value.get('wreckTypeID'):
                        pending.add(value['wreckTypeID'])
            pending.update(int(v['value']) for v in values if v['id'] == 507 and v['value'])
        for key in attributes:
            records['client/dogmaattributes', key] = record('client/dogmaattributes', key)
        for faction in (500010, 500011, 500012, 500019, 500020, 500025):
            value = record('client/factions', faction)
            records['client/factions', faction] = value
            corporation = value['corporationID']
            records['sde/npcCorporations', corporation] = record('sde/npcCorporations', corporation)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(dict(build=3662168, types=[types[k] for k in sorted(types)], records=[
            dict(dataset=d, key=k, data=v) for (d, k), v in sorted(records.items())]), indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
        print('Exported', len(types), 'types and', len(records), 'records')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', required=True, type=pathlib.Path)
    parser.add_argument('--output', required=True, type=pathlib.Path)
    parser.add_argument('--encounters', type=pathlib.Path,
                        default=pathlib.Path('src/Eden.World/Exploration/combat-anomaly-encounters-3662168.json'))
    args = parser.parse_args()
    export(args.catalog, args.output, args.encounters)
