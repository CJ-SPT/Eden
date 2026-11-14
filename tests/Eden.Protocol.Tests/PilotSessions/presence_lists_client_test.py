"""Run isolated native list methods in pinned Python 2 with cooperative tasklets.

Game modules are decoded as inert code, never imported or executed. UI, owner
lookups and locks are test doubles; no game process, renderer or session starts.
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
    chat = zlib.decompress(source.read('xmppchatclient/xmppchatwindow.pyj'))
    chat_service = zlib.decompress(source.read('xmppchatclient/xmppchatsvc.pyj'))
    guests = zlib.decompress(source.read('eve/client/script/ui/shared/dockedUI/guests.pyj'))
    station = zlib.decompress(source.read('eve/client/script/ui/station/base.pyj'))

worker = r'''
import __builtin__, marshal, sys, stackless

class Value(object):
    def __init__(self, **kwargs):
        self.__dict__.update(kwargs)

class Signal(object):
    def __init__(self, *args):
        self.callbacks = []
    def connect(self, callback):
        if callback not in self.callbacks:
            self.callbacks.append(callback)
    def __call__(self, *args):
        for callback in self.callbacks[:]:
            callback(*args)

class Scroll(object):
    destroyed = False
    def __init__(self):
        self.nodes = []
    def Clear(self):
        self.nodes = []
    def AddNodes(self, index, nodes, **kwargs):
        if index < 0:
            index = len(self.nodes)
        self.nodes[index:index] = nodes
    def RemoveNodes(self, nodes):
        for node in nodes:
            self.nodes.remove(node)
    def GetNodes(self):
        return self.nodes[:]
    def HideLoading(self):
        pass

def walk(code):
    yield code
    for child in code.co_consts:
        if hasattr(child, 'co_code'):
            for nested in walk(child):
                yield nested

def native(blob, owner, name, scope):
    scope['__builtins__'] = __builtins__
    module = marshal.loads(blob[8:])
    cls = next(c for c in walk(module) if c.co_name == owner)
    code = next(c for c in cls.co_consts if getattr(c, 'co_name', None) == name)
    return type(lambda: None)(code, scope)

owners = {i: Value(name=name) for i, name in enumerate(['Alpha', 'i po', 'Zero A-ichi'], 1)}
pause = [False]
def get_owner(char_id):
    if pause[0]:
        pause[0] = False
        stackless.schedule()
    return owners[char_id]

scope = {'cfg': Value(eveowners=Value(Get=get_owner)),
         'service': Value(ROLE_CHTINVISIBLE=1048576),
         'blue': Value(os=Value(GetWallclockTime=lambda: 0)),
         'GetChatService': lambda: Value(GetUserData=lambda char_id: {'role': 0}),
         'logger': Value(debug=lambda *args: None, exception=lambda *args: None)}

class ChatWindow(object):
    verbose = False
    destroyed = False
    def __init__(self):
        self.members = {1: {'role': 0}, 2: {'role': 0}}
        self.userlistNodesByName = {99: Value(charID=99)}
        self.userlist = Scroll()
        self.receiver = 'local_30000142'
    def SetChannel(self, channel):
        if self.receiver == channel:
            return
        self.receiver = channel
        self.userlist.Clear()
        self.userlistNodesByName = {}
        self.members = {}
    def _GetUserEntryNode(self, char_id, owner, data):
        return Value(charID=char_id, charIndex=owner.name.lower(), height=1)
    def GetIdxForUser_WithBinarySearch(self, name):
        return len(self.userlist.nodes)
    def GetThrottleFunc(self):
        return lambda: None
    def UpdateCaption(self):
        self.count = len(self.members)

for name in ['InitUsers', 'CharacterJoinedChannel', 'CharacterLeftChannel']:
    setattr(ChatWindow, name, native(CHAT_BLOB, 'XmppChatWindow', name, scope))

class GuestOwners(object):
    def __init__(self):
        self.data = owners.copy()
        self.knownLuzers = set()
        self.retries = []
    def Prime(self, ids):
        for char_id in ids:
            if char_id not in self.knownLuzers:
                self.data[char_id] = owners[char_id]
    def GetIfExists(self, char_id):
        return self.data.get(char_id)
    def Hint(self, char_id, row):
        self.data[char_id] = Value(name=row[1])
    def fetch(self, ids):
        if pause[0]:
            pause[0] = False
            stackless.schedule()
        self.retries.append(ids)
        return (), [(char_id, owners[char_id].name) for char_id in ids if char_id in owners]

guest_owners = GuestOwners()
__builtin__.cfg = Value(eveowners=guest_owners)
__builtin__.sm = Value(RemoteSvc=lambda name: Value(GetMultiOwnersEx=guest_owners.fetch))

class Guest(object):
    def __init__(self, char_id):
        self.character_id = char_id
    @property
    def name(self):
        return guest_owners.data[self.character_id].name

class GuestList(object):
    def __init__(self):
        native(GUEST_BLOB, 'GuestList', '__init__', {'signals': Value(Signal=Signal)})(self, 60011866, [Guest(1)])
    @property
    def members(self):
        return self._guests

for name in ['guests', 'guests_by_character_id', 'location_id']:
    setattr(GuestList, name, property(native(GUEST_BLOB, 'GuestList', name, {})))
for name in ['add', 'remove', 'discard']:
    setattr(GuestList, name, native(GUEST_BLOB, 'GuestList', name, {}))

class GuestPanel(object):
    def __init__(self):
        self._scroll = Scroll()
        self._controller = Value(guest_list=GuestList())
        self._scroll_update_id = 0
        self._quick_filter = Value(GetValue=lambda: '')
    def _allocate_scroll_update_id(self):
        self._scroll_update_id += 1
        return self._scroll_update_id
    def _on_guest_list_guest_added(self, guest):
        self._add_guest_to_scroll(guest)
    def _on_guest_list_guest_removed(self, guest):
        self._remove_guest_from_scroll(guest)
    def _get_guest_entry(self, guest):
        return Value(charID=guest.character_id, label=guest.name, info=Value(name=guest.name))
    def _get_guest_entries(self, guests):
        entries = [self._get_guest_entry(guest) for guest in guests]
        if pause[0]:
            pause[0] = False
            stackless.schedule()
        return entries

guest_scope = {}
bisect_code = next(c for c in walk(marshal.loads(GUEST_BLOB[8:])) if c.co_name == 'bisect_left')
guest_scope['bisect_left'] = type(lambda: None)(bisect_code, {'__builtins__': __builtins__})
for name in ['_update_scroll_content', '_get_filtered_guests', '_add_guest_to_scroll', '_remove_guest_from_scroll']:
    setattr(GuestPanel, name, native(GUEST_BLOB, 'GuestsPanel', name, guest_scope))

errors = []
def task(action):
    try:
        action()
    except Exception as error:
        errors.append(error)

def race(full, delta):
    errors[:] = []
    pause[0] = True
    stackless.tasklet(task)(full)
    stackless.tasklet(task)(delta)
    stackless.run()

def guest_race(arriving):
    panel = GuestPanel()
    if not arriving:
        panel._controller.guest_list.members[2] = Guest(2)
    panel._update_scroll_content()
    def delta():
        members = panel._controller.guest_list.members
        if arriving:
            guest = Guest(2)
            members[2] = guest
            panel._add_guest_to_scroll(guest)
        else:
            guest = members.pop(2)
            panel._remove_guest_from_scroll(guest)
    race(panel._update_scroll_content, delta)
    return panel

station_session = Value(stationid=60011866)
__builtin__.session = station_session
station_row = (2, 1000044, None, None)

class StationService(object):
    def __init__(self):
        self.guests = {}
        self.guestListReceived = None

def fetch_station():
    stackless.schedule()
    return [station_row]

station_scope = {'session': station_session, 'sm': Value(RemoteSvc=lambda name: Value(GetGuests=fetch_station))}
for name in ['GetGuests', 'OnCharNowInStation', 'OnCharNoLongerInStation']:
    setattr(StationService, name, native(STATION_BLOB, 'StationSvc', name, station_scope))

def station_race():
    station = StationService()
    race(station.GetGuests, lambda: station.OnCharNoLongerInStation(station_row))
    assert not errors, errors
    return station

def load_json(data):
    if data == 'owner':
        return [2]
    assert data == 'departure'
    return {'cmd': 'leave', 'charid': 2}

class ChatService(object):
    verbose = False
    def __init__(self):
        self.window = ChatWindow()
        self.window.receiver = 'local_30000142'
        self.memberRolesByChannels = {self.window.receiver: {2: 'participant'}}
        self.userData = {}
    def GetGroupChatWindow(self, channel):
        return self.window
    def _PopulateEveownersFromInfo(self, char_id, info):
        pass
    def _AddCharacterToChannelWindow(self, window, char_id):
        stackless.schedule()
        window.CharacterJoinedChannel(char_id)

chat_scope = {'json': Value(loads=load_json), 'logger': scope['logger']}
for name in ['_UnpackVikingPresence', '_HandleMessageFromAdmin', 'GetOrCreateGroupChatWindow']:
    setattr(ChatService, name, native(CHAT_SERVICE_BLOB, 'XmppChatService', name, chat_scope))
ChatService.GetOrCreateGroupChatWindow.im_func.func_defaults = (True,)

def chat_race():
    chat = ChatService()
    chat.window.InitUsers()
    packet = Value(children=[Value(attributes={'info': 'owner', 'role': '0'})])
    race(lambda: chat._UnpackVikingPresence(chat.window.receiver, packet),
         lambda: chat._HandleMessageFromAdmin('departure', chat.window))
    assert not errors, errors
    return chat

# The unmodified pinned methods reproduce both overwritten arrival and departure.
for arriving in [True, False]:
    panel = guest_race(arriving)
    assert not errors, errors
    assert set(n.charID for n in panel._scroll.nodes) != set(panel._controller.guest_list.members)
window = ChatWindow()
window.InitUsers()
assert 99 in window.userlistNodesByName
race(window.InitUsers, lambda: window.CharacterJoinedChannel(3))
assert len(errors) == 1 and isinstance(errors[0], RuntimeError), errors
assert 'dictionary changed size' in str(errors[0]), errors
assert 2 in station_race().GetGuests()
assert 2 in chat_race().window.members

window = ChatWindow()
window.InitUsers()
race(lambda: window.CharacterJoinedChannel(3), lambda: window.SetChannel('local_30000143'))
assert 3 in {node.charID for node in window.userlist.nodes}

panel = GuestPanel()
panel._controller.guest_list.members[2] = Guest(2)
del guest_owners.data[2]
guest_owners.knownLuzers.add(2)
try:
    panel._update_scroll_content()
    raise AssertionError('Missing owner should abort the native rebuild')
except KeyError:
    assert not panel._scroll.nodes

# A cooperative lock models the native TempLock contract without importing locks.
class Lock(object):
    def __init__(self):
        self.owner = None
        self.depth = 0
    def __enter__(self):
        current = stackless.getcurrent()
        while self.owner is not None and self.owner is not current:
            stackless.schedule()
        self.owner = current
        self.depth += 1
    def __exit__(self, *args):
        self.depth -= 1
        if self.depth == 0:
            self.owner = None
locks_by_key = {}
def temp_lock(key):
    if key not in locks_by_key:
        locks_by_key[key] = Lock()
    return locks_by_key[key]

names = ['carbon', 'carbon.common', 'carbon.common.script', 'carbon.common.script.sys',
         'carbon.common.script.sys.serviceConst', 'json', 'locks',
         'xmppchatclient', 'xmppchatclient.xmppchatwindow', 'xmppchatclient.xmppchatsvc',
         'eve', 'eve.client', 'eve.client.script', 'eve.client.script.ui',
         'eve.client.script.ui.shared', 'eve.client.script.ui.shared.dockedUI',
         'eve.client.script.ui.shared.dockedUI.guests', 'eve.client.script.ui.station',
         'eve.client.script.ui.station.base']
for name in names:
    module = type(sys)(name)
    module.__path__ = []
    sys.modules[name] = module
    parent, _, child = name.rpartition('.')
    if parent:
        setattr(sys.modules[parent], child, module)
sys.modules['locks'].TempLock = temp_lock
sys.modules['json'].loads = load_json
sys.modules['carbon.common.script.sys.serviceConst'].ROLE_CHTINVISIBLE = 1048576
sys.modules['xmppchatclient.xmppchatwindow'].XmppChatWindow = ChatWindow
sys.modules['xmppchatclient.xmppchatsvc'].XmppChatService = ChatService
sys.modules['eve.client.script.ui.shared.dockedUI.guests'].GuestsPanel = GuestPanel
sys.modules['eve.client.script.ui.station.base'].StationSvc = StationService
expression = marshal.loads(EXPRESSION_BLOB)
assert eval(expression, {}) == {}
installed = ChatWindow.InitUsers.im_func
assert eval(expression, {}) == {}
assert ChatWindow.InitUsers.im_func is installed

panel._update_scroll_content()
assert guest_owners.retries == [[2]]
assert {node.charID for node in panel._scroll.nodes} == {1, 2}
panel._add_guest_to_scroll(Guest(2))
assert len(panel._scroll.nodes) == 2

window = ChatWindow()
window.InitUsers()
race(lambda: window.CharacterJoinedChannel(3), lambda: window.SetChannel('local_30000143'))
assert not errors, errors
assert not window.userlist.nodes and not window.members

chat = ChatService()
chat.window.InitUsers()
chat.window.userlist.RemoveNodes([chat.window.userlistNodesByName[1]])
packet = Value(children=[Value(attributes={'info': 'owner', 'role': '0'})])
chat._UnpackVikingPresence(chat.window.receiver, packet)
assert {node.charID for node in chat.window.userlist.nodes} == {1, 2}


# Roster parsing can yield before it enters CharacterJoinedChannel. Switching
# the reused Local window during that gap must not import source-room members.
chat = ChatService()
chat.window.InitUsers()
race(lambda: chat._UnpackVikingPresence(chat.window.receiver, packet),
     lambda: chat.window.SetChannel('local_30000143'))
assert not errors, errors
assert not chat.window.members, 'Source roster leaked into destination Local'
assert not chat.window.userlist.nodes

# A leave already waiting on the old window must not remove the same pilot
# from the destination after SetChannel reuses that window.
chat = ChatService()
chat.window.InitUsers()
def switch_while_locked():
    with temp_lock(('eden-presence-list', id(chat.window))):
        stackless.schedule()
        chat.window.SetChannel('local_30000143')
        chat.window.CharacterJoinedChannel(2)
race(switch_while_locked, lambda: chat._HandleMessageFromAdmin('departure', chat.window))
assert not errors, errors
assert set(chat.window.members) == {2}
assert {node.charID for node in chat.window.userlist.nodes} == {2}

# Native GuestList mutates before its signal waits on the panel lock. Replace
# the controller and rebuild while an old station signal is still queued.
for present in [False, True]:
    panel = GuestPanel()
    old_list = panel._controller.guest_list
    if present:
        old_list.members[2] = Guest(2)
    panel._update_scroll_content()
    def replace_while_locked():
        with temp_lock(('eden-presence-list', id(panel))):
            stackless.schedule()
            panel._controller.guest_list = GuestList()
            if present:
                panel._controller.guest_list.members[2] = Guest(2)
            panel._update_scroll_content()
    delta = (lambda: old_list.remove(2)) if present else (lambda: old_list.add(Guest(2)))
    race(replace_while_locked, delta)
    assert not errors, errors
    assert {node.charID for node in panel._scroll.nodes} == set(panel._controller.guest_list.members), (
        'Queued guest signal overwrote the current controller membership')

# Ordinary room presence also primes owners before entering the window method.
for channel in ['corp_1000044', 'private_7', 'local_30000142']:
    chat = ChatService()
    chat.window.SetChannel(channel)
    race(lambda: chat._AddCharacterToChannelWindow(chat.window, 2),
         lambda: chat.window.SetChannel('local_30000143'))
    assert not errors, errors
    assert not chat.window.members and not chat.window.userlist.nodes

# An owner RPC can yield after the callback first reads the controller. A
# replacement list must be checked again before adding the old station's row.
panel = GuestPanel()
panel._controller.guest_list.members[2] = Guest(2)
del guest_owners.data[2]
def replace_station():
    panel._controller.guest_list = GuestList()
    panel._controller.guest_list._location_id += 1
race(lambda: panel._add_guest_to_scroll(Guest(2)), replace_station)
assert not errors, errors
assert 2 not in {node.charID for node in panel._scroll.nodes}

# An owner still absent after the retry must not abort other guest rows.
panel = GuestPanel()
panel._controller.guest_list.members[99] = Guest(99)
panel._update_scroll_content()
assert {node.charID for node in panel._scroll.nodes} == {1}
panel._controller.guest_list = None
panel._add_guest_to_scroll(Guest(2))
assert 2 not in {node.charID for node in panel._scroll.nodes}

for cycle in range(3):
    assert 2 not in station_race().GetGuests()
    chat = chat_race()
    assert 2 not in chat.window.members
    assert 2 not in chat.memberRolesByChannels[chat.window.receiver]
    for arriving in [True, False]:
        panel = guest_race(arriving)
        assert not errors, errors
        assert set(n.charID for n in panel._scroll.nodes) == set(panel._controller.guest_list.members)
        window = ChatWindow()
        window.InitUsers()
        assert 99 not in window.userlistNodesByName
        delta = (lambda: window.CharacterJoinedChannel(3)) if arriving else (lambda: window.CharacterLeftChannel(2))
        race(window.InitUsers, delta)
        assert not errors, errors
        assert set(n.charID for n in window.userlist.nodes) == set(window.members)
        assert set(window.userlistNodesByName) == set(window.members)
        assert window.count == len(window.userlist.nodes)
print('Native presence list assertions passed in the pinned Python 2 runtime')
'''
for name, blob in [('CHAT_BLOB', chat), ('CHAT_SERVICE_BLOB', chat_service),
                   ('GUEST_BLOB', guests), ('STATION_BLOB', station), ('EXPRESSION_BLOB', expression)]:
    worker = worker.replace(name, repr(blob))

with os.add_dll_directory(str(binaries)):
    runtime = ctypes.CDLL(str(runtime_path))
    ctypes.c_int.in_dll(runtime, 'Py_NoSiteFlag').value = 1
    runtime.Py_InitializeEx(0)
    if runtime.PyRun_SimpleString(worker.encode('ascii')):
        raise SystemExit('Native presence list assertions failed')
