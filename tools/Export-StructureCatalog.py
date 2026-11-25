"""Export published player Upwell hulls and their services from the read-only pinned catalog."""
import argparse
import hashlib
import json
from pathlib import Path
import sqlite3


ASSET_SAFETY_WRAP_TYPE = 60  # Build 3662168 inventorycommon.const.typeAssetSafetyWrap.


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
        ids = set(hulls + services + [ASSET_SAFETY_WRAP_TYPE, 34, 35, 670, 1130, 215, 3380, 37796, 37797, 37798, 37799])
        equipment = []
        for i, in db.execute('SELECT t.Id FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE g.CategoryId=66'):
            if definitions.get(i, {}).get('published') != 1:
                continue
            raw = db.execute("SELECT Data FROM SourceRecord WHERE Dataset='client/typedogma' AND Key=?", (str(i),)).fetchone()
            if raw and any(e['effectID'] in (11, 12, 13, 2663) for e in json.loads(raw[0]).get('dogmaEffects', [])):
                equipment.append(i)
        ids.update(equipment)
        charge_groups = {int(v) for i in equipment for a, v in db.execute(
            'SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=?', (i,)
        ) if a in (604, 605, 606, 607, 608, 609) and v > 0}
        ids.update(i for i, g in db.execute('SELECT Id,GroupId FROM ItemType')
                   if g in charge_groups and definitions.get(i, {}).get('published') == 1)
        for i in hulls:
            ids.update(int(v) for a, v in db.execute(
                'SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=?', (i,)
            ) if a in (3101, 5701, 2792) and v > 0)
        ids.update(i for i, in db.execute('SELECT Id FROM ItemType WHERE GroupId=1136'))
        types, records, effects, attribute_ids = [], [], set(), set()
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
            attribute_ids.update(a['id'] for a in types[-1]['attributes'])
            # Standup EWAR modules declare the target's resistance attribute through resistanceAttributeID (2138).
            attribute_ids.update(int(a['value']) for a in types[-1]['attributes']
                                 if a['id'] == 2138 and a['value'] > 0)
            for dataset in ('client/types', 'client/typedogma'):
                raw = db.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?', (dataset, str(i))).fetchone()
                if raw:
                    records.append(dict(dataset=dataset, key=i, data=json.loads(raw[0])))
                    if dataset == 'client/typedogma':
                        effects.update(e['effectID'] for e in json.loads(raw[0]).get('dogmaEffects', []))
        for effect in sorted(effects):
            raw = db.execute("SELECT Data FROM SourceRecord WHERE Dataset='client/dogmaeffects' AND Key=?", (str(effect),)).fetchone()
            if raw:
                value = json.loads(raw[0])
                records.append(dict(dataset='client/dogmaeffects', key=effect, data=value))
                for modifier in value.get('modifierInfo', []):
                    attribute_ids.update(int(modifier[k]) for k in ('modifiedAttributeID', 'modifyingAttributeID')
                                         if modifier.get(k) is not None)
        seen_attributes = set()
        while attribute_ids - seen_attributes:
            attribute = min(attribute_ids - seen_attributes)
            seen_attributes.add(attribute)
            raw = db.execute("SELECT Data FROM SourceRecord WHERE Dataset='client/dogmaattributes' AND Key=?", (str(attribute),)).fetchone()
            if raw:
                value = json.loads(raw[0])
                records.append(dict(dataset='client/dogmaattributes', key=attribute, data=value))
                attribute_ids.update(int(value[k]) for k in ('minAttributeID', 'maxAttributeID') if value.get(k) is not None)
        raw = db.execute("SELECT Data FROM SourceRecord WHERE Dataset='client/blueprints' AND Key=?",
                         (json.dumps('1130'),)).fetchone()
        records.append(dict(dataset='client/blueprints', key=1130, data=json.loads(raw[0])))
        graphics = {definitions[i]['graphicID'] for i in hulls if definitions[i].get('graphicID') is not None}
        locations = set()
        for graphic in sorted(graphics):
            raw = db.execute("SELECT Data FROM SourceRecord WHERE Dataset='client/graphicids' AND Key=?", (str(graphic),)).fetchone()
            if raw:
                value = json.loads(raw[0])
                records.append(dict(dataset='client/graphicids', key=graphic,
                                    data=dict(graphicLocationID=value.get('graphicLocationID'),
                                              sofHullName=value.get('sofHullName'), graphicFile=value.get('graphicFile'))))
                if value.get('graphicLocationID') is not None:
                    locations.add(value['graphicLocationID'])
        for location in sorted(locations):
            raw = db.execute("SELECT Data FROM SourceRecord WHERE Dataset='client/graphiclocations' AND Key=?", (str(location),)).fetchone()
            if raw:
                points = [p for p in json.loads(raw[0]).get('directionalLocators', [])
                          if p.get('category', '').startswith('undockPoint')]
                records.append(dict(dataset='client/graphiclocations', key=location, data=dict(directionalLocators=points)))
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
