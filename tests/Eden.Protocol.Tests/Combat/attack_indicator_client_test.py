"""Headless checks of isolated pinned client handlers; no UI or game session starts."""
import ctypes
import hashlib
import os
import sys
from pathlib import Path
import zipfile
import zlib

root = Path(sys.argv[1])
archive = root / '.eden-original-code.ccp'
if not archive.exists():
    archive = root / 'tq/code.ccp'
assert hashlib.sha256(archive.read_bytes()).hexdigest() == '37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368'
with zipfile.ZipFile(archive) as source:
    tactical = zlib.decompress(source.read('eve/client/script/parklife/tactical.pyj'))
    sequencer = zlib.decompress(source.read('eve/client/script/parklife/fxSequencer.pyj'))
    michelle = zlib.decompress(source.read('eve/client/script/remote/michelle.pyj'))
worker = 'TACTICAL = %r\nSEQUENCER = %r\nMICHELLE = %r\n' % (tactical, sequencer, michelle)
worker += r'''
import marshal
def find(code, name):
    if code.co_name == name:
        return code
    for child in code.co_consts:
        if hasattr(child, 'co_code'):
            found = find(child, name)
            if found is not None:
                return found
class Stub(object):
    pass
session = Stub()
session.shipid = 12
state = Stub()
state.threatAttackingMe = 8
const = Stub()
const.categoryStarbase = 23
settings = Stub()
settings.user = Stub()
settings.user.ui = Stub()
settings.user.ui.Get = lambda *args: False
service = Stub()
service.GetItem = lambda *args: None
sm = Stub()
sm.StartService = lambda *args: service
flags = {}
tracker = Stub()
tracker.attackers = {}
tracker.stateSvc = Stub()
tracker.stateSvc.SetState = lambda item, flag, value: flags.__setitem__(item, value)
function = type(lambda: None)
# Both pinned definitions load these five defaults before MAKE_FUNCTION.
defaults = (-1, None, None, 0, None)
native = function(find(marshal.loads(TACTICAL[8:]), 'OnSpecialFX'), globals(), 'OnSpecialFX', defaults)
def emit(module, target=12, active=True, offensive=True):
    native(tracker, 10, module, None, target, None, '', offensive, active, active)
emit(11)
assert flags[10] == 1
emit(13)
emit(11, active=False)
assert flags[10] == 1
emit(13, active=False)
assert flags[10] == 0
emit(14, target=15)
assert flags[10] == 0
emit(14, offensive=False)
assert flags[10] == 0
emit(11)
emit(11)
assert len(tracker.attackers[10]) == 1
emit(11, active=False)
assert flags[10] == 0
logs = []
fx = Stub()
fx.disabledGuids = set()
fx.LogInfo = lambda *args: logs.append(args)
fx.LogWarn = lambda *args: logs.append(args)
class Trigger(object):
    def __init__(self, **kwargs):
        self.__dict__.update(kwargs)
IsTravelEffect = lambda guid: False
FX_LONG_ONESHOT_GUIDS = set()
GetClassification = lambda guid: None
GetScaleTime = lambda *args: 1.0
fx_native = function(find(marshal.loads(SEQUENCER[8:]), 'OnSpecialFX'), globals(), 'OnSpecialFX', defaults)
fx_native(fx, 10, 11, None, 12, None, '', True, True, True)
assert logs[-1] == ('FxSequencer::OnSpecialFx: No guid in trigger',)
collections = Stub()
collections.Iterable = (list, tuple)
dispatch = function(find(marshal.loads(MICHELLE[8:]), 'OnSpecialFX'), globals(), 'OnSpecialFX', defaults)
sm.ScatterEvent = lambda name, *args: (native(tracker, *args), fx_native(fx, *args))
dispatch(Stub(), 10, 11, None, 12, None, '', True, True, True)
assert flags[10] == 1
dispatch(Stub(), 10, 11, None, 12, None, '', True, False, False)
assert flags[10] == 0
print('Native attack indicator start/overlap/stop/retarget/replay and nonvisual FX assertions passed')
'''
with os.add_dll_directory(str(root / 'tq/bin64')):
    runtime_path = root / 'tq/bin64/python27.dll'
    assert hashlib.sha256(runtime_path.read_bytes()).hexdigest() == (
        '1266e119d5db70281831f06049faa2694e658a68d504e0cc5680ec5726ba3aa2')
    runtime = ctypes.CDLL(str(runtime_path))
    ctypes.c_int.in_dll(runtime, 'Py_NoSiteFlag').value = 1
    runtime.Py_InitializeEx(0)
    if runtime.PyRun_SimpleString(worker.encode('ascii')):
        raise SystemExit(1)
