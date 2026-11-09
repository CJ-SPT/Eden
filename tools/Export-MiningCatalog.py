"""Export pinned mining equipment, crystals, type lists and skill prerequisites for automated tests."""
import argparse
import json
from pathlib import Path
import sqlite3


BUILD = 3662168
GROUPS = {54, 464, 483, 482, 663, 737}


def export(catalog_path, output):
    if catalog_path.resolve() == output.resolve():
        raise ValueError('The output must not overwrite the source catalog')
    with sqlite3.connect(catalog_path.resolve().as_uri() + '?mode=ro', uri=True) as catalog:
        def record(dataset, key):
            row = catalog.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?',
                                  (dataset, str(key))).fetchone()
            return json.loads(row[0]) if row else {}

        if record('sde/_sde', '"sde"').get('buildNumber') != BUILD:
            raise ValueError('Expected the pinned build-3662168 catalog')
        pending = {type_id for type_id, group in catalog.execute('SELECT Id,GroupId FROM ItemType')
                   if group in GROUPS and record('client/types', type_id).get('published') == 1}
        pending.update({601, 1230, 1223, 17478, 17480, 17476, 32918, 3410, 90727, 90728})
        types, records, attributes, effects = [], {}, set(), set()
        while pending:
            type_id = min(pending)
            pending.remove(type_id)
            if ('client/types', type_id) in records:
                continue
            row = catalog.execute('SELECT t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity '
                                  'FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?', (type_id,)).fetchone()
            name, group, group_name, category, mass, radius, capacity = row
            values = list(catalog.execute('SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? ORDER BY AttributeId',
                                         (type_id,)))
            types.append(dict(id=type_id, name=name, groupId=group, groupName=group_name, categoryId=category,
                              mass=mass, radius=radius, capacity=capacity, attributes=[dict(id=a, value=v) for a, v in values]))
            attributes.update(a for a, _ in values)
            for a, v in values:
                if a in (182, 183, 184, 1285, 1289, 1290) and v:
                    pending.add(int(v))
                if a == 3148 and v:
                    records['client/typelist', int(v)] = record('client/typelist', int(v))
            for dataset in ('client/types', 'client/typedogma'):
                records[dataset, type_id] = record(dataset, type_id)
            effects.update(e['effectID'] for e in records['client/typedogma', type_id].get('dogmaEffects', []))
        for effect in sorted(effects):
            data = record('client/dogmaeffects', effect)
            records['client/dogmaeffects', effect] = data
            for modifier in data.get('modifierInfo', []):
                attributes.update(modifier[key] for key in ('modifiedAttributeID', 'modifyingAttributeID') if key in modifier)
        while attributes:
            attribute = attributes.pop()
            if ('client/dogmaattributes', attribute) in records:
                continue
            data = record('client/dogmaattributes', attribute)
            records['client/dogmaattributes', attribute] = data
            attributes.update(data[key] for key in ('minAttributeID', 'maxAttributeID') if data.get(key) is not None)
        data = dict(build=BUILD, types=sorted(types, key=lambda t: t['id']),
                    records=[dict(dataset=d, key=k, data=v) for (d, k), v in sorted(records.items())])
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(data, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
        print('Exported', len(types), 'mining types to', output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('tests/Eden.Protocol.Tests/Fixtures/MiningCatalog.json'))
    args = parser.parse_args()
    export(args.catalog, args.output)
