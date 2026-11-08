"""Export build-3662168 overload calculation representatives and inert fitting inputs from the read-only catalog."""
import argparse
import json
from pathlib import Path
import sqlite3


BUILD = 3662168


def export(catalog_path, output, matrix, coverage):
    if (catalog_path.resolve() in (output.resolve(), matrix.resolve()) or output.resolve() == matrix.resolve()
            or coverage.resolve() in (output.resolve(), matrix.resolve())):
        raise ValueError('The output must not overwrite the source catalog')
    with sqlite3.connect(catalog_path.resolve().as_uri() + '?mode=ro', uri=True) as catalog:
        def record(dataset, key):
            row = catalog.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?',
                                  (dataset, str(key))).fetchone()
            return json.loads(row[0]) if row else {}

        if record('sde/_sde', '"sde"').get('buildNumber') != BUILD:
            raise ValueError('Expected the pinned build-3662168 catalog')
        report = json.loads(coverage.read_text(encoding='utf-8'))
        if report['build'] != BUILD:
            raise ValueError('Coverage must come from the pinned build')
        all_effects = {int(k): json.loads(v) for k, v in catalog.execute(
            "SELECT Key,Data FROM SourceRecord WHERE Dataset='client/dogmaeffects'")}
        programs, profiles, hulls, representatives = {}, [], [], {}
        attrs = {9, 1180, 1211, 1212}
        thermal_attrs = {1178, 1179, 1196, 1198, 1199, 1200, 1224, 1259, 1261, 1262}
        for entry in report['entries']:
            if not entry['admitted']:
                continue
            type_id = entry['typeId']
            values = dict(catalog.execute('SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=?', (type_id,)))
            if entry['category'] == 'Ship':
                hulls.append(dict(typeId=type_id, slots={str(k): values.get(k, 0) for k in (12, 13, 14)},
                                  attributes={str(k): values[k] for k in sorted(thermal_attrs) if k in values}))
            if entry['category'] != 'Module':
                continue
            effects = record('client/typedogma', type_id).get('dogmaEffects', [])
            overload = [all_effects[e['effectID']] for e in effects if all_effects[e['effectID']].get('effectCategory') == 5]
            if not overload:
                continue
            if len(overload) != 1:
                raise ValueError('Review multiple overload programs on type ' + str(type_id))
            effect = overload[0]
            bindings = effect.get('modifierInfo', [])
            programs[effect['effectID']] = effect
            representatives.setdefault(effect['effectID'], type_id)
            selected = attrs | {m[k] for m in bindings for k in ('modifiedAttributeID', 'modifyingAttributeID')}
            rack = [name for flag, name in ((11, 'Low'), (13, 'Medium'), (12, 'High'))
                    if any(e['effectID'] == flag for e in effects)]
            if len(rack) != 1:
                raise ValueError('Expected one rack for type ' + str(type_id))
            profiles.append(dict(typeId=type_id, groupId=entry['groupId'], rack=rack[0], effectId=effect['effectID'],
                                 attributes={str(k): values[k] for k in sorted(selected) if k in values}))
        if len(profiles) != 1826 or len(programs) != 19 or len(hulls) != 235:
            raise ValueError('Review the changed admitted thermal cohort')
        definition_ids = {int(k) for p in profiles for k in p['attributes']}
        definition_ids.update(m[k] for e in programs.values() for m in e['modifierInfo']
                              for k in ('modifiedAttributeID', 'modifyingAttributeID'))
        definitions = {}
        while definition_ids:
            key = definition_ids.pop()
            if key in definitions:
                continue
            definition = record('client/dogmaattributes', key)
            definitions[key] = dict(id=key, defaultValue=definition.get('defaultValue', 0),
                                    stackable=definition.get('stackable', 1) == 1,
                                    minimum=definition.get('minAttributeID'), maximum=definition.get('maxAttributeID'))
            definition_ids.update(definition[k] for k in ('minAttributeID', 'maxAttributeID') if definition.get(k) is not None)
        matrix_data = dict(build=BUILD, sourceFingerprint=report['sourceFingerprint'],
                           programs=[programs[k] for k in sorted(programs)], profiles=profiles, hulls=hulls,
                           definitions=[definitions[k] for k in sorted(definitions)])
        matrix.parent.mkdir(parents=True, exist_ok=True)
        matrix.write_text(json.dumps(matrix_data, indent=2) + '\n', encoding='utf-8')
        cohort = sorted(representatives.values())
        # Include all program shapes, normal excluded profiles, real hull racks
        # and the complete Thermodynamics prerequisite/modifier closure.
        pending = set(cohort) | {561, 601, 638, 587, 670, 28164, 2048, 4409, 47254, 28879, 28880, 28668}
        types, records, attributes, effects = [], {}, thermal_attrs | attrs | {1229}, set()
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
            for dataset in ('client/types', 'client/typedogma', 'client/typematerials'):
                records[dataset, type_id] = record(dataset, type_id)
            pending.update(row['materialTypeID'] for row in records['client/typematerials', type_id].get('materials', []))
            wreck = records['client/types', type_id].get('wreckTypeID')
            if wreck:
                pending.add(wreck)
            effects.update(e['effectID'] for e in records['client/typedogma', type_id].get('dogmaEffects', []))
        for effect in sorted(effects):
            data = record('client/dogmaeffects', effect)
            records['client/dogmaeffects', effect] = data
            for modifier in data.get('modifierInfo', []):
                attributes.update(modifier[key] for key in ('modifiedAttributeID', 'modifyingAttributeID') if key in modifier)
        attributes.update({1267, 1277})
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
        print('Exported', len(cohort), 'representatives;', len(profiles), 'matrix profiles;', len(types), 'total types to', output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('tests/Eden.Protocol.Tests/Fixtures/OverloadCatalog.json'))
    parser.add_argument('--matrix', type=Path, default=Path('tests/Eden.Protocol.Tests/Fixtures/ThermalModuleMatrix.json'))
    parser.add_argument('--coverage', type=Path, default=Path('docs/ship-content-coverage.json'))
    args = parser.parse_args()
    export(args.catalog, args.output, args.matrix, args.coverage)
