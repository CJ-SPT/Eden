"""Export fitting and geography inputs for build-3662168 capital travel and combat fixtures."""

import argparse
import json
from pathlib import Path
import sqlite3


def export(source, destination):
    if source.resolve() == destination.resolve():
        raise ValueError('The fixture must not overwrite the source catalog')
    types = []
    records = {}
    with sqlite3.connect(source.resolve().as_uri() + '?mode=ro', uri=True) as catalog:
        def read(dataset, key):
            row = catalog.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?', (dataset, str(key))).fetchone()
            return json.loads(row[0]) if row else None

        def keep(dataset, key, fields=None):
            value = read(dataset, key)
            if value is not None:
                records[dataset, key] = value if fields is None else {name: value[name] for name in fields if name in value}
            return value or {}

        if read('sde/_sde', '"sde"')['buildNumber'] != 3662168:
            raise ValueError('Expected client build 3662168')
        pending = {34, 648, 3638, 11963, 12729, 12731, 21094, 21096, 22430, 28646, 28650, 28652, 28844, 28846, 28848, 28850,
                   32880, 33476, 89648, 52694, 52696, 57319, 57425, 59630, 59631,
                   16273, 16274, 16275, 17887, 17888, 17889,
                   670, 26475, 40639, 19720, 19722, 19724, 19726, 37604, 37605, 37606, 37607, 73787, 73790, 73792, 73793,
                   63140, 83094, 83465, 83485, 92822, 92823, 92824, 92825, 95230, 95231, 95232, 95233,
                   671, 3764, 11567, 22852, 23773, 23913, 23917, 23919, 23953, 81623,
                   26487, 26490, 26509, 26512, 26528, 26531, 26534, 26553, 26556, 28603, 40699,
                   24550, 24552, 24554, 23674, 41537, 40631, 41439, 41440, 41441, 41233,
                   40632, 41442, 41443, 41444, 40633, 40634, 43658, 43660, 43662, 43664,
                   77281, 77283, 77284, 77288, 77391, 77392, 77393, 77394, 77398, 77399, 77400, 77401, 11357, 11370,
                   42126, 41700, 42241, 41691, 45649, 41693, 78576, 41689, 42124, 41699, 42243, 41690,
                   45647, 41692, 87381, 87382, 52907, 53025, 3514, 3260, 42125, 72817, 42242, 72814, 45645, 72816,
                   23735, 23757, 23911, 23915, 24242, 24483, 23055, 23057, 23059, 23061, 32325,
                   37599, 40345, 40346, 40347, 40568, 40569, 40570, 40571,
                   40358, 40359, 40360, 40361, 40552, 40553, 40554, 40555, 83585, 83586, 83587, 83589,
                   40362, 40363, 40364, 40365, 40560, 40562, 40564, 40566,
                   41548, 41549, 41550, 41551,
                   42522, 16272, 28352, 28583, 42890, 41030, 43681, 1230, 62516, 62453, 62632, 62633, 62634, 62635, 62636,
                   20280, 4292, 27951, 4294, 20444, 17686, 20701, 24569,
                   34122, 34124, 34126, 1531, 2016, 3456, 21610, 21611, 21603}
        blueprint = 23920  # Aeon Blueprint
        recipe = read('client/blueprints', json.dumps(str(blueprint)))
        records['client/blueprints', str(blueprint)] = recipe
        pending.add(blueprint)
        pending.update(row['typeID'] for key in ('materials', 'skills') for row in recipe['activities']['manufacturing'][key])
        visited = set()
        while pending:
            type_id = min(pending)
            pending.remove(type_id)
            visited.add(type_id)
            row = catalog.execute('SELECT t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity '
                                  'FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?', (type_id,)).fetchone()
            name, group, group_name, category, mass, radius, capacity = row
            values = catalog.execute('SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? ORDER BY AttributeId',
                                     (type_id,)).fetchall()
            types.append(dict(id=type_id, name=name, groupId=group, groupName=group_name, categoryId=category,
                              mass=mass, radius=radius, capacity=capacity,
                              attributes=[dict(id=a, value=v) for a, v in values]))
            keep('client/types', type_id, ('published', 'techLevel', 'metaGroupID', 'volume', 'wreckTypeID'))
            dogma = keep('client/typedogma', type_id)
            for effect in dogma.get('dogmaEffects', []):
                keep('client/dogmaeffects', effect['effectID'])
            if category == 87:  # Fighters.
                abilities = read('client/fighterabilitiesbytype', json.dumps(str(type_id)))
                if abilities:
                    records['client/fighterabilitiesbytype', str(type_id)] = abilities
            pending.update(int(v) for a, v in values if a in (182, 183, 184, 1285, 1289, 1290) and v > 0 and int(v) not in visited)
        for type_list in (11, 330, 331, 332, 333, 334, 341, 342, 343, 348, 1000):
            keep('client/typelist', type_list)
        for attribute in (3319, 3320, 3321, 3322, 3324, 3325, 5682, 6182, 6183, 6350):
            keep('client/dogmaattributes', attribute)
        keep('client/compressibletypes', 1230)
        for buff in (3, 4, 5, 6, 8, 9, *range(39, 55), 55, 56, 57, 58, 59, 61):
            records['client/dbuffcollections', str(buff)] = read('client/dbuffcollections', json.dumps(str(buff)))
        for deployable in (33476, 35841, 57319, 59630):
            keep('client/spacecomponentsbytype', deployable)
        for system_id in (30000142, 30001385, 30000248, 30000251):
            keep('client/systems', system_id, ('center', 'securityStatus', 'wormholeClassID', 'regionID', 'factionID'))
        systems = catalog.execute('SELECT s.Id,s.Name,c.Id,c.Name,r.Id,r.Name FROM SolarSystem s '
                                 'JOIN Constellation c ON s.ConstellationId=c.Id JOIN Region r ON c.RegionId=r.Id '
                                 'WHERE s.Id IN (30001385,30000248,30000251) ORDER BY s.Id').fetchall()
        orbit = catalog.execute('SELECT * FROM Celestial WHERE Id=40088235').fetchone()
    system_fields = ('id', 'name', 'constellationId', 'constellationName', 'regionId', 'regionName')
    orbit_fields = ('id', 'solarSystemId', 'typeId', 'groupId', 'radius', 'orbitId', 'x', 'y', 'z')
    lines = ['{', '  "build": 3662168,', '  "systems": ' + json.dumps([dict(zip(system_fields, system)) for system in systems]) + ',',
             '  "celestials": ' + json.dumps([dict(zip(orbit_fields, orbit))]) + ',', '  "types": [']
    lines.append(',\n'.join('    ' + json.dumps(row, ensure_ascii=False) for row in sorted(types, key=lambda row: row['id'])))
    lines += ['  ],', '  "records": [']
    lines.append(',\n'.join('    ' + json.dumps(dict(dataset=d, key=k, data=v), ensure_ascii=False)
                            for (d, k), v in sorted(records.items())))
    lines += ['  ]', '}', '']
    destination.write_text('\n'.join(lines), encoding='utf-8', newline='\n')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('tests/Eden.Protocol.Tests/Fixtures/CapitalTravelCatalog.json'))
    args = parser.parse_args()
    export(args.catalog, args.output)
