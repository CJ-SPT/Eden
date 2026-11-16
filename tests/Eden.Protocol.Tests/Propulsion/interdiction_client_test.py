"""Execute the pinned probe forcefield setup with inert objects; no game or renderer starts."""
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
    blob = zlib.decompress(source.read('eve/client/script/environment/spaceObject/MobileWarpDisruptor.pyj'))
    hic = zlib.decompress(source.read('eve/client/script/environment/effects/WarpDisruptFieldGenerating.pyj'))
worker = 'BLOB = %r\nHIC = %r\n' % (blob, hic)
worker += r'''
import marshal

def walk(code):
    yield code
    for child in code.co_consts:
        if hasattr(child, 'co_code'):
            for nested in walk(child):
                yield nested

class Stub(object):
    pass

code = next(c for c in walk(marshal.loads(BLOB[8:])) if c.co_name == 'Assemble')
assemble = (lambda: None).__class__(code, globals())
for radius in [10000.0, 20000.0]:
    for free in [False, True]:
        for has_model in [False, True]:
            calls = []
            state = Stub()
            definition = Stub()
            definition.warpScrambleRange = radius
            manager = Stub()
            manager.GetType = lambda type_id: definition
            service = Stub()
            service.GetStateManager = lambda: manager
            state.sm = Stub()
            state.sm.GetService = lambda name: service
            state.typeID = 22778 if radius == 20000.0 else 34260
            state.model = Stub() if has_model else None
            state.isFree = free
            state.ShowForcefield = lambda **kw: calls.append(('field', kw))
            state.SetColor = lambda color: calls.append(('color', color))
            state.ScaleAttenuation = lambda size: calls.append(('radius', size))
            assemble(state)
            assert state.effectRadius == radius
            assert ('radius', radius) in calls
            assert (('field', {'animated': False}) in calls) == (has_model and not free)
print('8 pinned probe forcefield cases passed; no renderer or client started')
class ShipEffect(object):
    def __init__(self, *args):
        pass

hic_code = next(c for c in walk(marshal.loads(HIC[8:])) if c.co_name == '__init__')
initialize = (lambda: None).__class__(hic_code, globals())
for radius in [None, 16000.0, 20000.0, 26000.0]:
    trigger = Stub()
    trigger.moduleTypeID = 28654
    trigger.graphicInfo = None
    if radius is not None:
        trigger.graphicInfo = Stub()
        trigger.graphicInfo.range = radius
    definition = Stub()
    definition.warpScrambleRange = 16000.0
    manager = Stub()
    manager.GetType = lambda type_id: definition
    service = Stub()
    service.GetStateManager = lambda: manager
    sm = Stub()
    sm.GetService = lambda name: service
    state = Stub()
    initialize(state, trigger)
    assert state.realRadius == (radius if radius is not None else 16000.0)
    assert state.radius == 20000.0
print('4 pinned HIC range override/fallback cases passed; no renderer or client started')
'''
with os.add_dll_directory(str(root / 'tq/bin64')):
    runtime = ctypes.CDLL(str(root / 'tq/bin64/python27.dll'))
    ctypes.c_int.in_dll(runtime, 'Py_NoSiteFlag').value = 1
    runtime.Py_InitializeEx(0)
    if runtime.PyRun_SimpleString(worker.encode('ascii')):
        raise SystemExit(1)
