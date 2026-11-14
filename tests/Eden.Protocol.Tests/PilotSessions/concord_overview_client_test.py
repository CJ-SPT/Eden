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
if (root / '.eden-original-code.ccp').exists():
    archive = root / '.eden-original-code.ccp'
assert hashlib.sha256(archive.read_bytes()).hexdigest() == (
    '37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368')
with zipfile.ZipFile(archive) as source:
    native = zlib.decompress(source.read('spacecomponents/client/components/entitystandings.pyj'))
    active_item = zlib.decompress(source.read('eve/client/script/ui/inflight/activeitem.pyj'))
    selected_item = zlib.decompress(source.read('eve/client/script/ui/inflight/selectedItemWnd.pyj'))

worker = 'NATIVE = %r\nEXPRESSION = %r\nACTIVE_ITEM = %r\nSELECTED_ITEM = %r\n' % (
    native, expression, active_item, selected_item)
worker += r'''
import marshal, sys
names = ['eve', 'eve.client', 'eve.client.script', 'eve.client.script.ui',
         'eve.client.script.ui.services', 'eve.client.script.ui.services.evePhotosvc',
         'inventorycommon', 'inventorycommon.const', 'spacecomponents',
         'spacecomponents.client', 'spacecomponents.client.components',
         'spacecomponents.client.components.entitystandings']
names += ['os', 'os.path', 'blue', 'carbon', 'carbon.common', 'carbon.common.script', 'carbon.common.script.sys',
          'carbon.common.script.sys.serviceConst', 'json', 'locks',
          'xmppchatclient', 'xmppchatclient.xmppchatwindow', 'xmppchatclient.xmppchatsvc',
          'eve.client.script.ui.shared', 'eve.client.script.ui.shared.dockedUI',
          'eve.client.script.ui.shared.dockedUI.guests', 'eve.client.script.ui.station', 'eve.client.script.ui.station.base',
          'eve.client.script.ui.services.skillQueueSvc', 'eve.client.script.ui.services.skillsvc',
          'eve.client.script.ui.skilltrading', 'eve.client.script.ui.skilltrading.applySkillPointsWindow']
names += ['npcs', 'npcs.client', 'npcs.client.components', 'npcs.client.components.entity_bounty',
          'eve.client.script.ui.inflight', 'eve.client.script.ui.inflight.activeitem',
          'eve.client.script.ui.inflight.selectedItemWnd', 'eve.client.script.ui.shared.info',
          'eve.client.script.ui.shared.info.infoContentBase']
names += ['eve.client.script.ui.shared.neocom', 'eve.client.script.ui.shared.neocom.corporation',
          'eve.client.script.ui.shared.neocom.corporation.corporationWindow',
          'eve.client.script.ui.services.corporation',
          'eve.client.script.ui.services.corporation.base_corporation']
for name in names:
    module = type(sys)(name)
    module.__path__ = []
    sys.modules[name] = module
    parent, _, child = name.rpartition('.')
    if parent:
        setattr(sys.modules[parent], child, module)
photo = sys.modules['eve.client.script.ui.services.evePhotosvc']
photo.EvePhoto = type('EvePhoto', (object,), {'GetPortrait': lambda *args: None})
sys.modules['eve.client.script.ui.shared.neocom.corporation.corporationWindow'].CorporationWindow = type(
    'CorporationWindow', (object,), {name: lambda *args: None for name in (
        'ApplyAttributes', 'construct_corporation_panels', 'construct_administration_panels',
        'construct_alliance_panels', 'reconstruct_main_tabs', 'SelectPanel')})
sys.modules['eve.client.script.ui.services.corporation.base_corporation'].Corporation = type(
    'Corporation', (object,), {'InviteToJoinCorp': lambda *args: None})
sys.modules['os.path'].exists = lambda *args: False
sys.modules['blue'].paths = None
sys.modules['carbon.common.script.sys.serviceConst'].ROLE_CHTINVISIBLE = 1048576
sys.modules['eve.client.script.ui.services.skillQueueSvc'].SkillQueueService = type(
    'SkillQueueService', (object,), {'GetSkillLevelAndProgressWithFreePoints': lambda *args: None})
sys.modules['eve.client.script.ui.services.skillsvc'].SkillsSvc = type(
    'SkillsSvc', (object,), {'SetFreeSkillPoints': lambda *args: None, 'OnFreeSkillPointsChanged': lambda *args: None})
sys.modules['eve.client.script.ui.skilltrading.applySkillPointsWindow'].ApplySkillPointsListEntry = type(
    'ApplySkillPointsListEntry', (object,), {})
chat_window = type('XmppChatWindow', (object,), {
    name: lambda *args: None for name in ['InitUsers', 'SetChannel', 'CharacterJoinedChannel', 'CharacterLeftChannel']})
guest_panel = type('GuestsPanel', (object,), {
    name: lambda *args: None for name in ['_update_scroll_content', '_get_filtered_guests',
                                         '_add_guest_to_scroll', '_remove_guest_from_scroll']})
sys.modules['xmppchatclient.xmppchatwindow'].XmppChatWindow = chat_window
sys.modules['xmppchatclient.xmppchatsvc'].XmppChatService = type('XmppChatService', (object,), {
    name: lambda *args: None for name in ['_UnpackVikingPresence', '_HandleMessageFromAdmin', '_AddCharacterToChannelWindow']})
sys.modules['eve.client.script.ui.shared.dockedUI.guests'].GuestsPanel = guest_panel
sys.modules['eve.client.script.ui.station.base'].StationSvc = type('StationSvc', (object,), {
    name: lambda *args: None for name in ['GetGuests', 'OnCharNowInStation', 'OnCharNoLongerInStation']})
const = sys.modules['inventorycommon.const']
const.groupConcordDrone = 301
entity = sys.modules['spacecomponents.client.components.entitystandings']
entity.STANDINGS_FRIENDLY = 1
friendly = ('native-blue', -1, 'friendly-hint')
neutral = ('native-neutral', 0, 'neutral-hint')
hostile = ('native-red', 1, 'hostile-hint')
entity.ICON_SORT_ORDER_HINT_BY_STANDINGS_CLASS = {-1: hostile, 0: neutral, 1: friendly}
bounty = sys.modules['npcs.client.components.entity_bounty']
bounty.get_entity_bounty = lambda type_id, item_id: 3000
active_item = sys.modules['eve.client.script.ui.inflight.activeitem']
selected_item = sys.modules['eve.client.script.ui.inflight.selectedItemWnd']
for module in (active_item, selected_item):
    module.get_entity_bounty = bounty.get_entity_bounty
del sys.modules['eve.client.script.ui.shared.info.infoContentBase']

def walk(code):
    yield code
    for child in code.co_consts:
        if hasattr(child, 'co_code'):
            for nested in walk(child):
                yield nested

def native_method(blob, name, module):
    code = next(c for c in walk(marshal.loads(blob[8:])) if c.co_name == name)
    return type(lambda: None)(code, module.__dict__)

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
import __builtin__
items = {}
michelle = type('Michelle', (), {'GetItem': staticmethod(items.get)})()
__builtin__.sm = type('Services', (), {'GetService': staticmethod(lambda name: {'michelle': michelle}[name])})()
assert eval(expression, {}) == {}
installed = entity._get_icon_color_and_hint_from_entity_standing_response
bounty_lookup = bounty.get_entity_bounty
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
assert bounty.get_entity_bounty is bounty_lookup
assert 'eve.client.script.ui.shared.info.infoContentBase' not in sys.modules
from npcs.client.components.entity_bounty import get_entity_bounty as late_import
assert late_import is bounty_lookup

active_bounty = native_method(ACTIVE_ITEM, 'CheckBounty', active_item)
selected_bounty = native_method(SELECTED_ITEM, '_GetBountyAmount', selected_item)
get_npc_bounty = native_method(ACTIVE_ITEM, 'GetNpcBounty', active_item)
widget = type('ItemWidget', (), {'GetNpcBounty': get_npc_bounty})()
rat = Slim(999, 30380)
items[rat.itemID] = rat
for amount in (3375.01, 0.0):
    rat.edenNpcBounty = amount
    assert active_bounty(widget, rat.typeID, rat) == (amount or None, None, None)
    assert selected_bounty(widget, rat.typeID, rat) == amount
    assert late_import(rat.typeID, rat.itemID) == amount
assert bounty_lookup(rat.typeID + 1, rat.itemID) == 3000
assert bounty_lookup(rat.typeID, None) == 3000
del rat.edenNpcBounty
assert selected_bounty(widget, rat.typeID, rat) == 3000
assert bounty_lookup(rat.typeID, rat.itemID + 1) == 3000
print('CONCORD overview adapter assertions passed in the pinned Python 2 runtime')
print('NPC bounty display assertions passed in the pinned Python 2 runtime')
'''

with os.add_dll_directory(str(binaries)):
    runtime = ctypes.CDLL(str(runtime_path))
    ctypes.c_int.in_dll(runtime, 'Py_NoSiteFlag').value = 1
    runtime.Py_InitializeEx(0)
    if runtime.PyRun_SimpleString(worker.encode('ascii')):
        raise SystemExit('CONCORD overview adapter assertions failed')
