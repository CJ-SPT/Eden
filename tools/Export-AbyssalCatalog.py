"""Export the build-3662168 Abyssal fixture from the read-only catalog."""
import argparse
import json
import sqlite3
from pathlib import Path


def export(catalog, output):
    with sqlite3.connect(catalog.resolve().as_uri() + '?mode=ro', uri=True) as db:
        records = {}

        def add(dataset, key):
            row = db.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?', (dataset, str(key))).fetchone()
            if not row:
                raise ValueError(f'Missing {dataset}/{key}')
            records[dataset, key] = json.loads(row[0])
            return records[dataset, key]

        needed = {56132, 47458, 47685, 47686, 47951, 48120, 47847, 47848, 47849, 48112, 48121, 621, 670,
                  47472, 47440, 80876, 47437, 47438, 47469, 47470, 47465, 210, *range(47378, 47393)}
        needed.update(row[0] for row in db.execute("SELECT Id FROM ItemType WHERE GroupId=1979"))
        needed.update({48964, 49689, 49692, 47876, 47877, 47878, 47879, 47880, 56213, 47699, 47700, 47701, 587, 16242})
        for list_id in (50, 57, 92, 233):
            add('client/typelist', list_id)
        types = []
        add('client/factions', 500025)
        for system in (32000001, 32000002):
            add('client/systems', system)
        for graphic in (*range(22042, 22049), *range(22207, 22215)):
            add('client/graphicids', graphic)
        for dungeon in (6204, 6207, 6205):
            add('client/dungeons', dungeon)
        while needed:
            type_id = min(needed)
            needed.remove(type_id)
            if ('client/types', type_id) in records:
                continue
            source = add('client/types', type_id)
            name, group, group_name, category, mass, radius, capacity = db.execute(
                'SELECT t.Name,g.Id,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity '
                'FROM ItemType t JOIN ItemGroup g ON t.GroupId=g.Id WHERE t.Id=?', (type_id,)
            ).fetchone()
            values = db.execute('SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? ORDER BY AttributeId',
                                (type_id,)).fetchall()
            types.append(dict(id=type_id, name=name, groupId=group, groupName=group_name, categoryId=category,
                              mass=mass, radius=radius, capacity=capacity,
                              attributes=[dict(id=k, value=v) for k, v in values]))
            dogma = add('client/typedogma', type_id) if db.execute(
                "SELECT 1 FROM SourceRecord WHERE Dataset='client/typedogma' AND Key=?", (str(type_id),)
            ).fetchone() else {}
            for effect in dogma.get('dogmaEffects', []):
                add('client/dogmaeffects', effect['effectID'])
            if source.get('wreckTypeID'):
                needed.add(source['wreckTypeID'])
        data = dict(build=3662168, types=sorted(types, key=lambda t: t['id']),
                    records=[dict(dataset=d, key=k, data=v) for (d, k), v in sorted(records.items())])
        output.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        print(f'Exported {len(types)} types and {len(records)} pinned records')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('tests/Eden.Protocol.Tests/Fixtures/AbyssalCatalog.json'))
    args = parser.parse_args()
    export(args.catalog, args.output)
