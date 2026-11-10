"""Export published player Upwell hulls and their services from the read-only pinned catalog."""
import argparse
import hashlib
import json
from pathlib import Path
import sqlite3


def export(path, output):
    with sqlite3.connect(path.resolve().as_uri() + '?mode=ro', uri=True) as db:
        definitions = {int(k): json.loads(v) for k, v in db.execute(
            "SELECT Key,Data FROM SourceRecord WHERE Dataset='client/types'"
        )}
        hulls = [i for i, in db.execute(
            'SELECT t.Id FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId '
            'WHERE g.Id IN (1657,1404,1406,1408,2016,2017,4744,4736,1012) ORDER BY t.Id'
        ) if definitions.get(i, {}).get('published') == 1]
        services = [35892, 35894, 35899, 35891, 45550, 35886, 35878, 35881, 35877,
                    45538, 45537, 45539, 45009, 35913, 35912, 35914, 82941, 78330]
        ids = set(hulls + services + [34, 35, 670, 1130, 215, 3380])
        for i in hulls:
            ids.update(int(v) for a, v in db.execute(
                'SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=?', (i,)
            ) if a in (3101, 5701, 2792) and v > 0)
        ids.update(i for i, in db.execute('SELECT Id FROM ItemType WHERE GroupId=1136'))
        types, records = [], []
        for i in sorted(ids):
            row = db.execute('SELECT t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity '
                             'FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?', (i,)).fetchone()
            if row is None:
                continue
            name, group, group_name, category, mass, radius, capacity = row
            types.append(dict(id=i, name=name, groupId=group, groupName=group_name, categoryId=category,
                              mass=mass, radius=radius, capacity=capacity, attributes=[dict(id=a, value=v)
                              for a, v in db.execute('SELECT AttributeId,Value FROM TypeAttribute '
                                                    'WHERE ItemTypeId=? ORDER BY AttributeId', (i,))]))
            for dataset in ('client/types', 'client/typedogma'):
                raw = db.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?', (dataset, str(i))).fetchone()
                if raw:
                    records.append(dict(dataset=dataset, key=i, data=json.loads(raw[0])))
        raw = db.execute("SELECT Data FROM SourceRecord WHERE Dataset='client/blueprints' AND Key=?",
                         (json.dumps('1130'),)).fetchone()
        records.append(dict(dataset='client/blueprints', key=1130, data=json.loads(raw[0])))
        data = dict(build=3662168, hulls=hulls, types=types, records=records)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')
        print(f'Exported {len(hulls)} Upwell hulls, {len(types)} types; SHA-256 '
              f'{hashlib.sha256(output.read_bytes()).hexdigest()}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('tests/Eden.Protocol.Tests/Fixtures/StructureCatalog.json'))
    args = parser.parse_args()
    export(args.catalog, args.output)
