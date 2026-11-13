"""Check the evejs layout reference and export pinned exploration records for automated tests."""
import argparse
import json
import math
import sqlite3
from pathlib import Path

BUILD = 3662168
GHOST_IDS = range(5533, 5553)
GHOST_LOOT = (33577, 33539, 57449, 57450)
GHOST_BLUEPRINTS = (33521, 33543, 33545, 33546, 33548, 33556, 33562, 33701, 33703)


def catalog_record(db, dataset, key):
    row = db.execute("SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?", (dataset, str(key))).fetchone()
    return json.loads(row[0]) if row else None


def objects(dungeon):
    return {int(obj['objectID']): (room_id, obj)
            for room_id, room in dungeon['rooms'].items() for obj in room.get('objects', {}).values()}


def xyz(obj):
    position = obj['position']
    return tuple(position[axis] for axis in 'xyz') if isinstance(position, dict) else tuple(position)


def compare(db, evejs, family):
    pack, = evejs.glob(f'evejs-{family}-*.json')
    templates = json.loads(pack.read_text(encoding='utf-8-sig'))['payload']['dungeon']['templatesByID']
    mismatches = []
    for template in templates.values():
        reference = template['clientDungeonContent']['rawDungeonRecord']
        dungeon_id = template['sourceDungeonID']
        pinned = catalog_record(db, 'client/dungeons', dungeon_id)
        reference_objects = objects(reference)
        pinned_objects = objects(pinned)
        shared = reference_objects.keys() & pinned_objects.keys()
        changed = 0
        for item in shared:
            reference_room, before = reference_objects[item]
            pinned_room, after = pinned_objects[item]
            has_position = bool(before.get('position')), bool(after.get('position'))
            changed += (reference_room != pinned_room or before.get('typeID') != after.get('typeID')
                        or has_position[0] != has_position[1]
                        or all(has_position) and math.dist(xyz(before), xyz(after)) > 0.01)
        gate_difference = (reference.get('connections') or []) != (pinned.get('connections') or [])
        entry_difference = reference.get('entryObjectID') != pinned.get('entryObjectID')
        if reference_objects.keys() != pinned_objects.keys() or changed or gate_difference or entry_difference:
            mismatches.append((dungeon_id, len(shared), len(pinned_objects), changed, gate_difference, entry_difference))
    print(f'{family}: {len(templates)} dungeon IDs. Differences: {mismatches}')
    if mismatches:
        raise ValueError('Reference differs from the pinned build')


def fixture(db, manifest, output):
    definitions = json.loads(manifest.read_text(encoding='utf-8-sig'))['complexes']
    records = {}
    needed = set(GHOST_LOOT + GHOST_BLUEPRINTS) | {23, 601, 587, 621, 670}

    def add(dataset, key):
        stored_key = json.dumps(str(key)) if dataset == 'client/blueprints' else str(key)
        data = catalog_record(db, dataset, stored_key)
        if data is None:
            raise ValueError(f'Missing catalog record {dataset}/{key}')
        records[dataset, key] = data
        return data

    for blueprint in GHOST_BLUEPRINTS:
        add('client/blueprints', blueprint)
    for definition in definitions:
        needed.update(definition['deadspaceModules'] + definition['factionModules'] + [definition['blueprint']])
        needed.update(item['typeId'] for item in definition['overseerLoot'] + definition['guaranteedLoot'])
        needed.update(member['typeId'] for room in definition['rooms'] for member in room['members'])
        add('client/blueprints', definition['blueprint'])
    add('client/systems', 30000144)
    add('client/systems', 30002659)
    needed.update([16874, 16875, 24007, 16935, 16929, 24011, 17001, 16991, 24015, 17068, 17075, 24023, 17118, 17114, 24025])

    for dungeon_id in [d['dungeonId'] for d in definitions] + list(GHOST_IDS):
        dungeon = add('client/dungeons', dungeon_id)
        add('client/factions', dungeon['factionID'])
        add('client/localization_fsd_en-us', dungeon['dungeonNameID'])
        for _, item in objects(dungeon).values():
            if 'typeID' in item:
                needed.add(item['typeID'])
            name = item.get('objectNameID')
            if name and catalog_record(db, 'client/localization_fsd_en-us', name):
                add('client/localization_fsd_en-us', name)
        for gate in dungeon.get('connections', []):
            for field in ('allowedShipsList', 'disallowedFittedTypesList'):
                if gate.get(field):
                    add('client/typelist', gate[field])

    types = []
    while needed:
        type_id = min(needed)
        needed.remove(type_id)
        if ('client/types', type_id) in records:
            continue
        source = add('client/types', type_id)
        name, group, group_name, category, mass, radius, capacity = db.execute(
            'SELECT t.Name,g.Id,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity FROM ItemType t JOIN ItemGroup g ON t.GroupId=g.Id WHERE t.Id=?',
            (type_id,)
        ).fetchone()
        values = list(db.execute('SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? ORDER BY AttributeId', (type_id,)))
        types.append(dict(id=type_id, name=name, groupId=group, groupName=group_name, categoryId=category,
                          mass=mass, radius=radius, capacity=capacity, attributes=[dict(id=k, value=v) for k, v in values]))
        dogma = catalog_record(db, 'client/typedogma', type_id)
        if dogma:
            records['client/typedogma', type_id] = dogma
            for effect in dogma.get('dogmaEffects', []):
                add('client/dogmaeffects', effect['effectID'])
        if source.get('wreckTypeID'):
            needed.add(source['wreckTypeID'])

    data = dict(build=BUILD, types=sorted(types, key=lambda t: t['id']),
                records=[dict(dataset=d, key=k, data=v) for (d, k), v in sorted(records.items())])
    rows = lambda key: ',\n'.join('    ' + json.dumps(row, ensure_ascii=False, separators=(',', ':')) for row in data[key])
    output.write_text(
        '{\n  "build": ' + str(BUILD) + ',\n  "types": [\n' + rows('types') +
        '\n  ],\n  "records": [\n' + rows('records') + '\n  ]\n}\n', encoding='utf-8'
    )
    print(f'Exported {len(types)} types and {len(records)} records')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', type=Path, required=True)
    parser.add_argument('--evejs', type=Path)
    parser.add_argument('--fixture', type=Path, default=Path('tests/Eden.Protocol.Tests/Fixtures/DedAndGhostCatalog.json'))
    args = parser.parse_args()
    if args.catalog.resolve() == args.fixture.resolve():
        raise ValueError('Output must not overwrite the catalog')
    with sqlite3.connect(args.catalog.resolve().as_uri() + '?mode=ro', uri=True) as catalog:
        if catalog_record(catalog, 'sde/_sde', '"sde"')['buildNumber'] != BUILD:
            raise ValueError('Expected build 3662168')
        if args.evejs:
            compare(catalog, args.evejs / 'content-packs', 'ded-rated-sites')
            compare(catalog, args.evejs / 'content-packs', 'ghost-sites')
        fixture(catalog, Path('src/Eden.Data/Catalog/Exploration/ded-complexes-3662168.json'), args.fixture)
