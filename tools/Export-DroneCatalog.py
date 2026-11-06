"""Export inert build-3662168 light/medium combat-drone test inputs; never read world data."""
import argparse
import json
import pathlib
import sqlite3


def export(catalog_path, output):
    with sqlite3.connect(catalog_path.resolve().as_uri() + '?mode=ro', uri=True) as catalog:
        def record(dataset, key):
            row = catalog.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?',
                                  (dataset, str(key))).fetchone()
            return json.loads(row[0]) if row else {}

        if record('sde/_sde', '"sde"').get('buildNumber') != 3662168:
            raise ValueError('Expected the pinned build-3662168 catalog')
        pending = {16229, 33151, 2476, 3436, 3437, 3442, 12305, 12484, 12485, 12486, 12487,
                   23566, 23606, 23618, 24241, 33699}
        for type_id, in catalog.execute('SELECT Id FROM ItemType WHERE GroupId=100'):
            attrs = dict(catalog.execute('SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=?', (type_id,)))
            if record('client/types', type_id).get('published') == 1 and attrs.get(1272) in (5, 10):
                pending.add(type_id)
        types, records, attributes, effects = [], {}, set(), set()
        while pending:
            type_id = min(pending)
            pending.remove(type_id)
            if ('client/types', type_id) in records:
                continue
            row = catalog.execute('SELECT t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity '
                                  'FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?', (type_id,)).fetchone()
            name, group_id, group_name, category_id, mass, radius, capacity = row
            values = list(catalog.execute('SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? ORDER BY AttributeId',
                                         (type_id,)))
            types.append(dict(id=type_id, name=name, groupId=group_id, groupName=group_name,
                              categoryId=category_id, mass=mass, radius=radius, capacity=capacity,
                              attributes=[dict(id=a, value=v) for a, v in values]))
            attributes.update(a for a, _ in values)
            for a, v in values:
                if a in (182, 183, 184, 1285, 1289, 1290) and v:
                    pending.add(int(v))
            for dataset in ('client/types', 'client/typedogma'):
                records[dataset, type_id] = record(dataset, type_id)
            effects.update(e['effectID'] for e in records['client/typedogma', type_id].get('dogmaEffects', []))
        for effect in sorted(effects):
            records['client/dogmaeffects', effect] = record('client/dogmaeffects', effect)
        for attr in sorted(attributes):
            records['client/dogmaattributes', attr] = record('client/dogmaattributes', attr)
        data = dict(build=3662168, types=sorted(types, key=lambda t: t['id']),
                    records=[dict(dataset=d, key=k, data=v) for (d, k), v in sorted(records.items())])
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(data, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
        print('Exported', len(types), 'types to', output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', required=True, type=pathlib.Path)
    parser.add_argument('--output', type=pathlib.Path,
                        default=pathlib.Path('tests/Eden.Protocol.Tests/Fixtures/CombatDroneCatalog.json'))
    args = parser.parse_args()
    export(args.catalog, args.output)
