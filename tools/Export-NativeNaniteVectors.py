"""Evaluate isolated build-3662168 repair price functions against the admitted catalog; never launch a client."""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import tempfile
import zipfile
import zlib


ARCHIVE_SHA256 = '37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368'
BUILD = 3662168


def export(catalog_path, root, coverage, output):
    catalog_path, root, coverage, output = (p.resolve() for p in (catalog_path, root, coverage, output))
    if output in (catalog_path, coverage) or output.is_relative_to(root):
        raise ValueError('The output must not overwrite an input or the client installation')
    archive = root / 'tq/code.ccp'
    if hashlib.sha256(archive.read_bytes()).hexdigest().upper() != ARCHIVE_SHA256:
        raise ValueError('Expected the pinned build-3662168 archive')
    report = json.loads(coverage.read_text(encoding='utf-8'))
    if report['build'] != BUILD:
        raise ValueError('Expected pinned content coverage')
    profiles = []
    with sqlite3.connect(catalog_path.as_uri() + '?mode=ro', uri=True) as catalog:
        def record(dataset, key):
            row = catalog.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?', (dataset, str(key))).fetchone()
            return json.loads(row[0]) if row else {}

        if record('sde/_sde', '"sde"').get('buildNumber') != BUILD:
            raise ValueError('Expected the pinned catalog')
        for entry in report['entries']:
            if not entry['admitted'] or entry['category'] != 'Module' or not {11, 12, 13}.intersection(entry['effects']):
                continue
            type_id = entry['typeId']
            source = record('client/types', type_id)
            hp = catalog.execute('SELECT Value FROM TypeAttribute WHERE ItemTypeId=? AND AttributeId=9', (type_id,)).fetchone()
            if not hp or hp[0] <= 0:
                raise ValueError('Review the module HP on type ' + str(type_id))
            materials = []
            for row in record('client/typematerials', type_id).get('materials', []):
                material = record('client/types', row['materialTypeID'])
                materials.append(dict(basePrice=material['basePrice'], portionSize=material['portionSize'], quantity=row['quantity']))
            profiles.append(dict(typeId=type_id, basePrice=source['basePrice'], portionSize=source['portionSize'],
                                 hitPoints=hp[0], materials=materials))
        paste = record('client/types', 28668)['basePrice']
    # Exercise native truncation, zero-portion handling, fallback, and free
    # repair independently of the ordinary module cohort's current values.
    edges = [
        dict(basePrice=123.45, portionSize=10, materials=[]),
        dict(basePrice=99.0, portionSize=0, materials=[]),
        dict(basePrice=999.0, portionSize=1, materials=[dict(basePrice=199.0, portionSize=10, quantity=15)]),
        dict(basePrice=999.0, portionSize=1, materials=[dict(basePrice=100.0, portionSize=0, quantity=50)]),
        dict(basePrice=1.0, portionSize=1, materials=[dict(basePrice=123.1, portionSize=7, quantity=9)]),
        dict(basePrice=0.0, portionSize=1, materials=[]),
    ]
    for index, edge in enumerate(edges):
        profiles.append(dict(typeId=-(index + 1), hitPoints=40.0, **edge))
    with tempfile.TemporaryDirectory(prefix='eden-native-nanite-') as directory:
        directory = Path(directory)
        with zipfile.ZipFile(archive) as source:
            price_code = directory / 'price.pyc'
            repair_code = directory / 'repair.pyc'
            price_code.write_bytes(zlib.decompress(source.read('eve/common/script/util/inventoryUtil.pyj')))
            repair_code.write_bytes(zlib.decompress(source.read('eve/common/script/dogma/baseDogmaLocation.pyj')))
        result = directory / 'results.txt'
        # Supply inert catalog adapters and math only. No imported client
        # module or class body executes, including the dogma location class.
        worker = '''import marshal, math
def find(path, name):
    f=open(path,'rb'); f.read(8); root=marshal.load(f); f.close()
    def walk(code):
        if code.co_name == name: return code
        for value in code.co_consts:
            if type(value) is type(code):
                found=walk(value)
                if found is not None: return found
    return walk(root)
class Quiet:
    def LogError(self,*args): pass
    def LogWarn(self,*args): pass
class Value: pass
class Location:
    def GetAttributeValue(self,*args): return self.multiplier
    def SetAttributeValue(self,*args): pass
location=Location()
class EveTypes:
    def GetBasePrice(self,key): return prices[key]
    def GetPortionSize(self,key): return portions[key]
evetypes=EveTypes()
const=Value(); const.consumptionTypeIDForModuleRepair=28668
const.attributeDamage=3; const.attributeShipBrokenModuleRepairCostMultiplier=1277
price=(lambda:None).__class__(find(%r,'GetComputedBasePrice'),
    {'evetypes':evetypes,'log':Quiet(),'get_type_materials_by_id':lambda key: recipes[key],
     'len':len,'int':int,'float':float,'None':None})
repair=(lambda:None).__class__(find(%r,'FindNumUnitsToRepairModule'),
    {'const':const,'GetComputedBasePrice':price,'math':math,'evetypes':evetypes,'None':None})
output=open(%r,'wb')
for profile in %r:
    key=profile['typeId']; recipes={key:[]}
    prices={key:profile['basePrice'],28668:%r}; portions={key:profile['portionSize']}
    for index,row in enumerate(profile['materials']):
        material=Value(); material.materialTypeID=-(index+100000)
        material.quantity=row['quantity']; recipes[key].append(material)
        prices[material.materialTypeID]=row['basePrice']; portions[material.materialTypeID]=row['portionSize']
    computed=price(key,1); units=[]
    for level in (0,3,5):
        location.multiplier=.5*(1-.05*level)
        value=repair(location,1,profile['hitPoints']/2,profile['hitPoints'],key,1)
        units.append(0 if value is None else int(value))
    output.write('%%d,%%d,%%d,%%d,%%d\\n' %% tuple([key,computed]+units))
output.close()
''' % (str(price_code), str(repair_code), str(result), profiles, paste)
        binaries = root / 'tq/bin64'
        previous = Path.cwd()
        try:
            os.chdir(binaries)
            with os.add_dll_directory(str(binaries)):
                runtime = ctypes.CDLL(str(binaries / 'python27.dll'))
                ctypes.c_int.in_dll(runtime, 'Py_NoSiteFlag').value = 1
                runtime.Py_InitializeEx(0)
                setup = 'import sys\nsys.path[:]=%r\n' % [str(binaries)]
                try:
                    failed = runtime.PyRun_SimpleString((setup + worker).encode('ascii', 'backslashreplace'))
                finally:
                    runtime.PyRun_SimpleString(b"if 'output' in globals(): output.close()")
                    runtime.Py_Finalize()
                if failed:
                    raise RuntimeError('Isolated native nanite evaluation failed')
        finally:
            os.chdir(previous)
        values = [list(map(int, line.split(','))) for line in result.read_text(encoding='ascii').splitlines()]
    if len(values) != len(profiles):
        raise ValueError('Incomplete native comparison')
    for profile, value in zip(profiles, values):
        if profile['typeId'] != value[0]:
            raise ValueError('Native vector order mismatch')
        profile.update(computedBasePrice=value[1], pasteUnits=value[2:])
    output.parent.mkdir(parents=True, exist_ok=True)
    data = dict(build=BUILD, archiveSha256=ARCHIVE_SHA256, sourceFingerprint=report['sourceFingerprint'],
                pasteBasePrice=paste, profiles=profiles)
    output.write_text(json.dumps(data, indent=2) + '\n', encoding='utf-8')
    print('Exported', len(profiles)-len(edges), 'admitted module prices and', len(edges), 'edge vectors to', output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', required=True, type=Path)
    parser.add_argument('--development-root', required=True, type=Path)
    parser.add_argument('--coverage', type=Path, default=Path('docs/ship-content-coverage.json'))
    parser.add_argument('--output', type=Path, default=Path('tests/Eden.Protocol.Tests/Fixtures/NaniteRepairReference.json'))
    args = parser.parse_args()
    export(args.catalog, args.development_root, args.coverage, args.output)
