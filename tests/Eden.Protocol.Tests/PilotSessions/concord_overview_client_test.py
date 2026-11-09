"""Headless Python 2 test of the signed login adapter and inert native color helper.

No game module bodies, UI, renderer, service, or game session are started.
"""
import ctypes
import hashlib
import os
from pathlib import Path
import sys
import zipfile
import zlib

expression = Path(sys.argv[1]).read_bytes()
root = Path(sys.argv[2])
binaries = root / 'tq/bin64'
runtime_path = binaries / 'python27.dll'
assert hashlib.sha256(runtime_path.read_bytes()).hexdigest() == (
    '1266e119d5db70281831f06049faa2694e658a68d504e0cc5680ec5726ba3aa2')
archive = root / 'tq/code.ccp'
assert hashlib.sha256(archive.read_bytes()).hexdigest() == (
    '37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368')
with zipfile.ZipFile(archive) as source:
    native = zlib.decompress(source.read('spacecomponents/client/components/entitystandings.pyj'))

worker = r'''
import marshal, sys
names = ['eve', 'eve.client', 'eve.client.script', 'eve.client.script.ui',
         'eve.client.script.ui.services', 'eve.client.script.ui.services.evePhotosvc',
         'inventorycommon', 'inventorycommon.const', 'spacecomponents',
         'spacecomponents.client', 'spacecomponents.client.components',
         'spacecomponents.client.components.entitystandings']
for name in names:
    module = type(sys)(name)
    module.__path__ = []
    sys.modules[name] = module
    parent, _, child = name.rpartition('.')
    if parent:
        setattr(sys.modules[parent], child, module)
photo = sys.modules['eve.client.script.ui.services.evePhotosvc']
photo.EvePhoto = type('EvePhoto', (object,), {})
const = sys.modules['inventorycommon.const']
const.groupConcordDrone = 301
entity = sys.modules['spacecomponents.client.components.entitystandings']
entity.STANDINGS_FRIENDLY = 1
friendly = ('native-blue', -1, 'friendly-hint')
neutral = ('native-neutral', 0, 'neutral-hint')
hostile = ('native-red', 1, 'hostile-hint')
entity.ICON_SORT_ORDER_HINT_BY_STANDINGS_CLASS = {-1: hostile, 0: neutral, 1: friendly}

class State(object):
    attacking = False
    threatAttackingMe = 99
    def GetService(self, name):
        assert name == 'stateSvc'
        return self
    def GetState(self, item_id, flag):
        assert flag == self.threatAttackingMe
        return self.attacking

class Slim(object):
    def __init__(self, group, type_id):
        self.itemID = type_id
        self.groupID = group
        self.typeID = type_id
        self.hostile_response_threshold = -11
        self.friendly_response_threshold = -11

state = State()
module = marshal.loads(NATIVE[8:])
code = next(c for c in module.co_consts
            if getattr(c, 'co_name', None) == '_get_icon_color_and_hint_from_entity_standing_response')
original = (lambda: None).__class__(code, {
    'ICON_SORT_ORDER_HINT_BY_STANDINGS_CLASS': entity.ICON_SORT_ORDER_HINT_BY_STANDINGS_CLASS,
    'STANDINGS_FRIENDLY': 1, 'STANDINGS_NEUTRAL': 0, 'STANDINGS_HOSTILE': -1,
    'ICON_COLOR_BAD': 'native-red', 'sm': state, 'state': state})
entity._get_icon_color_and_hint_from_entity_standing_response = original
state.attacking = True
assert original(Slim(301, 3885), 0)[0] == 'native-red'
expression = marshal.loads(EXPRESSION)
assert eval(expression, {}) == {}
installed = entity._get_icon_color_and_hint_from_entity_standing_response
assert callable(photo.EvePhoto.GetPortrait)
for attacking in [False, True]:
    state.attacking = attacking
    for standing in [-10, -1, 0, 10]:
        for type_id in [3885, 11125]:
            assert installed(Slim(301, type_id), standing) == friendly
            assert state.attacking == attacking
        other = Slim(999, 30380)
        assert installed(other, standing) == original(other, standing)
        other.hostile_response_threshold = 11
        other.friendly_response_threshold = 11
        assert installed(other, standing) == hostile
assert eval(expression, {}) == {}
assert entity._get_icon_color_and_hint_from_entity_standing_response is installed
print('CONCORD overview adapter assertions passed in the pinned Python 2 runtime')
'''.replace('NATIVE', repr(native)).replace('EXPRESSION', repr(expression))

with os.add_dll_directory(str(binaries)):
    runtime = ctypes.CDLL(str(runtime_path))
    ctypes.c_int.in_dll(runtime, 'Py_NoSiteFlag').value = 1
    runtime.Py_InitializeEx(0)
    if runtime.PyRun_SimpleString(worker.encode('ascii')):
        raise SystemExit('CONCORD overview adapter assertions failed')
