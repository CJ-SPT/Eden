"""Export pinned Sleeper and all 36 environment programs for resident integration tests."""
import argparse
import importlib.util
import json
from pathlib import Path
import sqlite3


def main(args):
    source = Path(__file__).with_name("Export-AnomalyCatalog.py")
    spec = importlib.util.spec_from_file_location("anomaly_export", source)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    # Published blue loot and the established Ancient Salvage materials, build 3662168.
    materials = [30744, 30745, 30746, 30747, 30018, 30019, 30021, 30022, 30024, 30248,
                 30251, 30252, 30254, 30258, 30259, 30268, 30269, 30270, 30271]
    module.export(args.catalog, args.output, args.encounters, materials)
    data = json.loads(args.output.read_text(encoding="utf-8"))
    types = {t['id']: t for t in data['types']}
    records = {(r['dataset'], r['key']): r['data'] for r in data['records']}
    with sqlite3.connect(args.catalog.resolve().as_uri() + '?mode=ro', uri=True) as db:
        def record(dataset, key):
            row = db.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?', (dataset, str(key))).fetchone()
            return json.loads(row[0]) if row else None
        suns = {}
        systems = []
        pending = set()
        for key, raw in db.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='sde/mapSecondarySuns' ORDER BY CAST(Key AS INTEGER)"):
            sun = json.loads(raw)
            system_id = sun['solarSystemID']
            metadata = record('client/systems', system_id)
            if metadata.get('wormholeClassID') not in range(1, 7) or sun['effectBeaconTypeID'] in suns:
                continue
            suns[sun['effectBeaconTypeID']] = sun
            pending.add(sun['effectBeaconTypeID'])
            records['sde/mapSecondarySuns', int(key)] = sun
            records['client/systems', system_id] = metadata
            row = db.execute('SELECT s.Id,s.Name,s.ConstellationId,c.Name,c.RegionId,r.Name FROM SolarSystem s '
                             'JOIN Constellation c ON s.ConstellationId=c.Id JOIN Region r ON r.Id=c.RegionId WHERE s.Id=?',
                             (system_id,)).fetchone()
            systems.append(dict(zip(('id','name','constellationId','constellationName','regionId','regionName'), row)))
        assert len(suns) == 36, len(suns)
        for key in sorted(pending):
            row = db.execute('SELECT t.Id,t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity '
                             'FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?',(key,)).fetchone()
            values = [dict(id=i,value=v) for i,v in db.execute('SELECT AttributeId,Value FROM TypeAttribute '
                      'WHERE ItemTypeId=? ORDER BY AttributeId',(key,))]
            types[key] = dict(zip(('id','name','groupId','groupName','categoryId','mass','radius','capacity'),row), attributes=values)
            records['client/types',key] = record('client/types',key)
            dogma = record('client/typedogma',key)
            records['client/typedogma',key] = dogma
            for effect in dogma['dogmaEffects']:
                eid = effect['effectID']
                value = record('client/dogmaeffects',eid)
                records['client/dogmaeffects',eid] = value
                for modifier in value.get('modifierInfo',[]):
                    aid=modifier['modifiedAttributeID']
                    records['client/dogmaattributes',aid] = record('client/dogmaattributes',aid)
        data['types'] = [types[k] for k in sorted(types)]
        data['records'] = [dict(dataset=d,key=k,data=v) for (d,k),v in sorted(records.items())]
        data['systems'] = sorted(systems,key=lambda s:s['id'])
    args.output.write_text(json.dumps(data,indent=2,ensure_ascii=False)+'\n',encoding='utf-8')
    print('Exported', len(suns), 'environment programs and representative systems')


if __name__ == '__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--encounters',type=Path,default=Path('src/Eden.Data/Catalog/Exploration/sleeper-encounters-3662168.json'))
    main(parser.parse_args())
