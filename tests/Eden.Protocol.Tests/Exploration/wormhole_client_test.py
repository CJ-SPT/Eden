"""Execute the pinned wormhole controller with inert objects; no game or renderer starts."""
import ctypes
import hashlib
import os
from pathlib import Path
import sys
import zipfile
import zlib

root = Path(sys.argv[1])
archive = root / '.eden-original-code.ccp'
if not archive.exists():
    archive = root / 'tq/code.ccp'
assert hashlib.sha256(archive.read_bytes()).hexdigest() == '37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368'
with zipfile.ZipFile(archive) as source:
    blob = zlib.decompress(source.read('evegraphics/controllers/clientControllerRegistry.pyj'))
worker = 'BLOB = %r\n' % blob
worker += r'''
import marshal

def walk(code):
    yield code
    for child in code.co_consts:
        if hasattr(child, 'co_code'):
            for nested in walk(child):
                yield nested

code = next(c for c in walk(marshal.loads(BLOB[8:])) if c.co_name == 'WormholeSetup')
setup = (lambda: None).__class__(code, globals())

class Stub(object):
    pass

for size, visual in [(1.0, 1.0), (.7, 2.0), (.4, 3.0)]:
    for age in range(4):
        for mass in range(1, 5):
            slim = Stub()
            slim.wormholeSize = size
            slim.wormholeAge = age
            slim.maxShipJumpMass = mass
            slim.isDestTriglavian = 0
            model = Stub()
            model.exploded = False
            values = {}
            model.SetControllerVariable = lambda name, value: values.update({name: value})
            setup(model, slim)
            assert values['WH_Size'] == visual
            assert values['WH_Age'] == min(age, 2)
            assert values['WH_ShipMass'] == mass
            assert values['WH_State'] == 1
print('48 pinned wormhole controller cases passed')
'''
with os.add_dll_directory(str(root / 'tq/bin64')):
    runtime = ctypes.CDLL(str(root / 'tq/bin64/python27.dll'))
    ctypes.c_int.in_dll(runtime, 'Py_NoSiteFlag').value = 1
    runtime.Py_InitializeEx(0)
    if runtime.PyRun_SimpleString(worker.encode('ascii')):
        raise SystemExit(1)
