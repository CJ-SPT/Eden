"""Export pinned compression mappings, allowed structures types and gas skills for automated tests."""
import argparse
import hashlib
import json
from pathlib import Path
import sqlite3

BUILD = 3662168
GAS_SKILL = 62452
STRUCTURE_TYPELIST = 336
REQUIRED_SKILLS = (182, 183, 184, 1285, 1289, 1290)


def export(path, output):
    with sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True) as db:
        build = db.execute("SELECT Data FROM SourceRecord WHERE Dataset='sde/_sde' AND Key=?", ('"sde"',)).fetchone()
        if not build or json.loads(build[0]).get('buildNumber') != BUILD:
            raise ValueError('Use the pinned build-3662168 catalog')
        mappings = [(int(json.loads(k)), int(json.loads(v))) for k, v in db.execute(
            "SELECT Key,Data FROM SourceRecord WHERE Dataset='client/compressibletypes'")]
        ids = {GAS_SKILL} | {i for pair in mappings for i in pair}
        pending, visited = [GAS_SKILL], set()
        while pending:
            skill = pending.pop()
            if skill in visited:
                continue
            visited.add(skill)
            for attribute, value in db.execute('SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=?', (skill,)):
                if attribute in REQUIRED_SKILLS and value > 0:
                    ids.add(int(value))
                    pending.append(int(value))
        types, records = [], []
        for item in sorted(ids):
            row = db.execute('SELECT t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity '
                             'FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?', (item,)).fetchone()
            if row is None:
                raise ValueError(f'Unknown mapped/required type {item}')
            name, group, group_name, category, mass, radius, capacity = row
            types.append(dict(id=item, name=name, groupId=group, groupName=group_name, categoryId=category,
                              mass=mass, radius=radius, capacity=capacity, attributes=[dict(id=a, value=v)
                              for a, v in db.execute('SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? '
                                                    'ORDER BY AttributeId', (item,))]))
            for dataset in ('client/types', 'client/typedogma', 'client/typematerials'):
                raw = db.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?', (dataset, str(item))).fetchone()
                if raw:
                    records.append(dict(dataset=dataset, key=item, data=json.loads(raw[0])))
        records.extend(dict(dataset='client/compressibletypes', key=raw, data=compressed) for raw, compressed in sorted(mappings))
        raw = db.execute("SELECT Data FROM SourceRecord WHERE Dataset='client/typelist' AND Key=?", (str(STRUCTURE_TYPELIST),)).fetchone()
        if raw is None:
            raise ValueError('Missing structure compression type list')
        records.append(dict(dataset='client/typelist', key=STRUCTURE_TYPELIST, data=json.loads(raw[0])))
        payload = dict(build=BUILD, types=types, records=records)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(payload, indent=2) + '\n', encoding='utf-8')
        print(f'Exported {len(mappings)} compression pairs and {len(types)} types; SHA-256 '
              f'{hashlib.sha256(output.read_bytes()).hexdigest()}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('tests/Eden.Protocol.Tests/Fixtures/CompressionCatalog.json'))
    args = parser.parse_args()
    export(args.catalog, args.output)
