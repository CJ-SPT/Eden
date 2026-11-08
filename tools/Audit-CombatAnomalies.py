"""Audit build-3662168 combat anomalies and export the first encounter fixture.

Reads the catalog in SQLite read-only mode. NPC grouping references remain unresolved.
"""
import argparse
import collections
import json
import pathlib
import sqlite3


def audit(catalog, output, fixture=None):
    with sqlite3.connect(catalog.resolve().as_uri() + "?mode=ro", uri=True) as db:
        def record(dataset, key):
            row = db.execute("SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?", (dataset, str(key))).fetchone()
            return json.loads(row[0]) if row else None

        build = db.execute("SELECT Source FROM SourceDataset WHERE Name='sde/dungeons'").fetchone()[0]
        if '3662168' not in build:
            raise ValueError("Expected pinned build 3662168")
        sites = []
        for key, payload in db.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='sde/dungeons'"):
            definition = json.loads(payload)
            dungeon = record('client/dungeons', key)
            if definition.get('archetypeID') != 24 or dungeon.get('entryTypeID') != 28356:
                continue
            rooms = dungeon.get('rooms', {})
            objects = [o for room in rooms.values() for o in room.get('objects', {}).values()]
            entities = [e for obj in objects for e in obj.get('entities', {}).values()]
            sites.append(dict(dungeonId=int(key), name=definition['name']['en'], roomCount=len(rooms),
                              objectCount=len(objects), entryResolved=any(
                                  o.get('objectID') == dungeon.get('entryObjectID') and bool(o.get('position')) for o in objects),
                              npcGroupIds=sorted({e['npcGroupingID'] for e in entities}),
                              triggered=any(e.get('triggerSpawn') == 1 for e in entities),
                              objectivesId=dungeon.get('objectivesID')))
        summary = dict(variants=len(sites), names=len({s['name'] for s in sites}),
                       entries=sum(s['entryResolved'] for s in sites), singleRoom=sum(s['roomCount'] == 1 for s in sites),
                       multiRoom=sum(s['roomCount'] > 1 for s in sites),
                       withNpcGroups=sum(bool(s['npcGroupIds']) for s in sites),
                       triggered=sum(s['triggered'] for s in sites),
                       objectives=sum(s['objectivesId'] is not None for s in sites),
                       unresolvedGroups=len({g for s in sites for g in s['npcGroupIds']}))
        result = dict(build=3662168, summary=summary, sites=sorted(sites, key=lambda s: s['dungeonId']))
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
        print(json.dumps(summary))
        if fixture is None:
            return
        dungeon = record('client/dungeons', 2134)
        pending = {16981, 16994, 16996, 16997}
        pending.update(o['typeID'] for room in dungeon['rooms'].values() for o in room['objects'].values())
        types, records, attributes = {}, {}, set()
        while pending:
            key = pending.pop()
            if key in types:
                continue
            row = db.execute('SELECT t.Id,t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity '
                             'FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?', (key,)).fetchone()
            values = [dict(id=i, value=v) for i, v in db.execute(
                'SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? ORDER BY AttributeId', (key,))]
            attributes.update(v['id'] for v in values)
            types[key] = dict(zip(('id', 'name', 'groupId', 'groupName', 'categoryId', 'mass', 'radius', 'capacity'), row),
                              attributes=values)
            for dataset in ('client/types', 'client/typedogma'):
                value = record(dataset, key)
                if value is not None:
                    records[dataset, key] = value
                    if dataset == 'client/types' and value.get('wreckTypeID'):
                        pending.add(value['wreckTypeID'])
            pending.update(int(v['value']) for v in values if v['id'] == 507 and v['value'])
        for key in attributes:
            records['client/dogmaattributes', key] = record('client/dogmaattributes', key)
        records['client/dungeons', 2134] = dungeon
        records['sde/dungeons', 2134] = record('sde/dungeons', 2134)
        fixture.parent.mkdir(parents=True, exist_ok=True)
        fixture.write_text(json.dumps(dict(build=3662168, types=[types[k] for k in sorted(types)], records=[
            dict(dataset=d, key=k, data=v) for (d, k), v in sorted(records.items())]), indent=2,
            ensure_ascii=False) + '\n', encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', required=True, type=pathlib.Path)
    parser.add_argument('--output', required=True, type=pathlib.Path)
    parser.add_argument('--fixture', type=pathlib.Path)
    args = parser.parse_args()
    audit(args.catalog, args.output, args.fixture)
