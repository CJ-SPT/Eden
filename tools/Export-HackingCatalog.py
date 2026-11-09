"""Export pinned analyzer inputs and ordinary pirate exploration containers; never writes the source catalog."""
import argparse
import json
import sqlite3
from pathlib import Path

BUILD = 3662168
PIRATES = {500010, 500011, 500012, 500019, 500020}


def export(source, fixture, sites):
    if source.resolve() in (fixture.resolve(), sites.resolve()):
        raise ValueError('Output must not overwrite the catalog')
    with sqlite3.connect(source.resolve().as_uri() + '?mode=ro', uri=True) as catalog:
        def record(dataset, key):
            row = catalog.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?',
                                  (dataset, str(key))).fetchone()
            return json.loads(row[0]) if row else {}

        if record('sde/_sde', '"sde"').get('buildNumber') != BUILD:
            raise ValueError('Expected build 3662168')
        definitions = []
        needed = {21718, 13278, 605, 586, 607, 601, 31238, 31240, 31244, 31246}
        for type_id, in catalog.execute('SELECT Id FROM ItemType WHERE GroupId=538 ORDER BY Id'):
            data = record('client/types', type_id)
            attrs = dict(catalog.execute('SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=?', (type_id,)))
            if data.get('published') == 1 and data.get('marketGroupID') and attrs.get(1909, 0) > 0:
                needed.add(type_id)
        for key, raw in catalog.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='client/dungeons' ORDER BY CAST(Key AS INTEGER)"):
            dungeon = json.loads(raw)
            name_id = dungeon.get('dungeonNameID', 0)
            name = record('client/localization_fsd_en-us', name_id).get('text', '')
            if dungeon.get('factionID') not in PIRATES or dungeon.get('archetypeID') not in (44, 45):
                continue
            tiers = {'Crumbling ': 0, 'Local ': 0, 'Decayed ': 1, 'Regional ': 1, 'Ruined ': 2, 'Central ': 2}
            band = next((value for prefix, value in tiers.items() if name.startswith(prefix)), None)
            if band is None or len(dungeon.get('rooms', {})) != 1:
                continue
            room = next(iter(dungeon['rooms'].values()))
            objects = list(room.get('objects', {}).values())
            if any(o.get('entities') for o in objects):
                continue
            entry = next((o.get('position', {}) for o in objects if o.get('objectID') == dungeon.get('entryObjectID')), {})
            containers = []
            for obj in sorted(objects, key=lambda o: o.get('objectID', 0)):
                type_id = obj.get('typeID', 0)
                type_row = catalog.execute('SELECT GroupId FROM ItemType WHERE Id=?', (type_id,)).fetchone()
                if not type_row or type_row[0] != 1207:
                    continue
                attrs = dict(catalog.execute('SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=?', (type_id,)))
                if attrs.get(1927) != 1:
                    continue
                position = obj.get('position', {})
                containers.append(dict(typeId=type_id, difficulty=int(attrs.get(1919, band + 1)),
                                       position={axis.upper(): position.get(axis, 0) - entry.get(axis, 0) for axis in 'xyz'}))
                needed.add(type_id)
            if not containers or len(containers) >= 100:
                continue
            definitions.append(dict(dungeonId=int(key), nameId=name_id, name=name, factionId=dungeon['factionID'],
                                    kind=0 if dungeon['archetypeID'] == 45 else 1, band=band,
                                    difficulty=dungeon.get('difficulty', band + 1), containers=containers))
        # Loot eligibility is catalog-backed; quantity and selection are explicit Eden policy.
        for type_id, group_id in catalog.execute('SELECT Id,GroupId FROM ItemType WHERE GroupId IN (333, 1304, 754) ORDER BY Id'):
            if record('client/types', type_id).get('published') == 1:
                needed.add(type_id)
        types, records, attributes, effects = [], {}, set(), set()
        for key, raw in catalog.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='client/blueprints'"):
            blueprint = json.loads(raw)
            if any(p['typeID'] in (22175, 22177) for p in blueprint.get('activities', {}).get('manufacturing', {}).get('products', [])):
                type_id = int(json.loads(key))
                needed.add(type_id)
                records['client/blueprints', type_id] = blueprint
                for activity in blueprint.get('activities', {}).values():
                    needed.update(m['typeID'] for m in activity.get('materials', []))
                    needed.update(s['typeID'] for s in activity.get('skills', []))
        while needed:
            type_id = min(needed)
            needed.remove(type_id)
            if ('client/types', type_id) in records:
                continue
            row = catalog.execute('SELECT t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity '
                                  'FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?', (type_id,)).fetchone()
            if not row:
                raise ValueError(f'Missing type {type_id}')
            name, group, group_name, category, mass, radius, capacity = row
            values = list(catalog.execute('SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? ORDER BY AttributeId', (type_id,)))
            types.append(dict(id=type_id, name=name, groupId=group, groupName=group_name, categoryId=category,
                              mass=mass, radius=radius, capacity=capacity, attributes=[dict(id=a, value=v) for a, v in values]))
            attributes.update(a for a, _ in values)
            needed.update(int(v) for a, v in values if a in (182, 183, 184, 1285, 1289, 1290) and v)
            for dataset in ('client/types', 'client/typedogma'):
                records[dataset, type_id] = record(dataset, type_id)
            wreck = records['client/types', type_id].get('wreckTypeID')
            if wreck:
                needed.add(wreck)
            effects.update(e['effectID'] for e in records['client/typedogma', type_id].get('dogmaEffects', []))
        for effect in sorted(effects):
            data = record('client/dogmaeffects', effect)
            records['client/dogmaeffects', effect] = data
            for modifier in data.get('modifierInfo', []):
                attributes.update(modifier[k] for k in ('modifiedAttributeID', 'modifyingAttributeID') if k in modifier)
        while attributes:
            attr = attributes.pop()
            if ('client/dogmaattributes', attr) in records:
                continue
            data = record('client/dogmaattributes', attr)
            records['client/dogmaattributes', attr] = data
            attributes.update(data[k] for k in ('minAttributeID', 'maxAttributeID') if data.get(k) is not None)
        for path, data in [(fixture, dict(build=BUILD, types=sorted(types, key=lambda t: t['id']),
                                         records=[dict(dataset=d, key=k, data=v) for (d, k), v in sorted(records.items())])),
                           (sites, dict(build=BUILD, sites=definitions))]:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
        print(f'Exported {len(definitions)} ordinary pirate sites, {len(types)} fixture types')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', type=Path, required=True)
    parser.add_argument('--fixture', type=Path, default=Path('tests/Eden.Protocol.Tests/Fixtures/HackingCatalog.json'))
    parser.add_argument('--sites', type=Path, default=Path('src/Eden.Data/Catalog/Exploration/pirate-exploration-3662168.json'))
    args = parser.parse_args()
    export(args.catalog, args.fixture, args.sites)
