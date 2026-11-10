"""Export build-3662168 cloaks and standard micro-jump drives and inert fitting inputs from the read-only catalog."""
import argparse
import json
from pathlib import Path
import sqlite3


GROUPS = {330, 1189}
BUILD = 3662168


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
        cohort = []
        for type_id, group_id in catalog.execute('SELECT Id,GroupId FROM ItemType ORDER BY Id'):
            source = record('client/types', type_id) if group_id in GROUPS else {}
            if source.get('published') == 1:
                cohort.append(type_id)
        if len(cohort) != 18:
            raise ValueError('Expected 14 published cloaks and four standard micro-jump drives')
        # Include cloak/MJD skills, eligible hulls, a rejected frigate and an MWD.
        pending = set(cohort) | {601, 638, 587, 16227, 33468, 11579, 4385, 434}
        types, records, attributes, effects = [], {}, {306, 560, 619, 973, 552}, set()
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
            wreck = records['client/types', type_id].get('wreckTypeID')
            if wreck:
                pending.add(wreck)
            effects.update(e['effectID'] for e in records['client/typedogma', type_id].get('dogmaEffects', []))
        for effect in sorted(effects):
            data = record('client/dogmaeffects', effect)
            records['client/dogmaeffects', effect] = data
            for modifier in data.get('modifierInfo', []):
                attributes.update(modifier[key] for key in ('modifiedAttributeID', 'modifyingAttributeID') if key in modifier)
        while attributes:
            attr = attributes.pop()
            if ('client/dogmaattributes', attr) in records:
                continue
            data = record('client/dogmaattributes', attr)
            records['client/dogmaattributes', attr] = data
            attributes.update(data[key] for key in ('minAttributeID', 'maxAttributeID') if data.get(key) is not None)
        data = dict(build=BUILD, types=sorted(types, key=lambda t: t['id']),
                    records=[dict(dataset=d, key=k, data=v) for (d, k), v in sorted(records.items())])
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(data, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
        print('Exported', len(cohort), 'modules;', len(types), 'total types to', output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('tests/Eden.Protocol.Tests/Fixtures/SpatialModuleCatalog.json'))
    args = parser.parse_args()
    export(args.catalog, args.output)
