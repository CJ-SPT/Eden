"""Replay the pinned fleet panel's roster refresh with inert UI objects."""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
import zipfile
import zlib


CLIENT = os.environ.get('EDEN_TEST_CLIENT_ROOT')
REPOSITORY = Path(__file__).resolve().parents[3]
FLEET = 'eve/client/script/ui/shared/fleet/fleet.pyj'
SCROLL = 'carbonui/control/scroll.pyj'


@unittest.skipUnless(CLIENT, 'Set EDEN_TEST_CLIENT_ROOT for pinned native fleet checks')
class FleetClientTests(unittest.TestCase):
    def test_join_notification_during_roster_rebuild_defers_nested_wing_refresh(self):
        spec = importlib.util.spec_from_file_location('eden_builder', REPOSITORY / 'tools/Build-SkillClient.py')
        builder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(builder)
        root = Path(CLIENT)
        original = root / '.eden-original-code.ccp'
        if not original.is_file():
            original = root / 'tq/code.ccp'
        with tempfile.TemporaryDirectory(prefix='eden-fleet-native-') as directory:
            work = Path(directory)
            overlay = work / 'overlay.json'
            overlay.write_text(json.dumps({'datasets': {}, 'messages': {}, 'alpha': {}}), encoding='utf-8')
            package = work / 'code.ccp'
            builder.build(root, overlay, package)
            with zipfile.ZipFile(original) as stock, zipfile.ZipFile(package) as patched:
                (work / 'stock.pyc').write_bytes(zlib.decompress(stock.read(FLEET)))
                (work / 'patched.pyc').write_bytes(zlib.decompress(patched.read(FLEET)))
                (work / 'scroll.pyc').write_bytes(zlib.decompress(stock.read(SCROLL)))
                self.assertEqual(stock.read(SCROLL), patched.read(SCROLL))
            python2 = os.environ.get('EDEN_PYTHON2') or ('python2.7' if os.name != 'nt' else None)
            builder.run_compiler(root, work, REPLAY, python2)


REPLAY = r'''
import marshal

def read_method(path, class_name, method, globals_, defaults=()):
    with open(path, 'rb') as stream:
        stream.read(8)
        module = marshal.load(stream)
    cls = next(code for code in module.co_consts if getattr(code, 'co_name', None) == class_name)
    code = next(code for code in cls.co_consts if getattr(code, 'co_name', None) == method)
    globals_['__builtins__'] = getattr(__builtins__, '__dict__', __builtins__)
    return type(read_method)(code, globals_, None, defaults)

class Node(dict):
    __getattr__ = dict.get
    Get = dict.get

    def __setattr__(self, field, value):
        self[field] = value

def replay(path, assert_first_pass):
    scheduled = []
    roster = {1: Node(charID=1), 2: Node(charID=2)}
    scroll = Node(destroyed=False)
    root = Node(id=('fleet', 'wing_11'), idx=0, scroll=scroll, openByDefault=True)
    scroll.nodes = [root]
    def reset_scroll(contentList):
        root.subNodes = []
        scroll.nodes = [root]
    scroll.Load = reset_scroll
    scroll.GetNodes = lambda: scroll.nodes
    scroll.RemoveNodes = lambda nodes: [scroll.nodes.remove(node) for node in nodes]
    panel = Node(destroyed=False, sr=Node(scroll=scroll), CheckHint=lambda: None)
    panel.HandleFleetChanged = lambda: scheduled.append('refresh')
    errors = []
    def load_fleet():
        try:
            scroll.PrepareSubContent(root)
        except Exception as error:
            errors.append(error)
            raise
    panel.LoadFleet = load_fleet
    root.GetSubContent = lambda node: [Node(squad=12, members=sorted(panel.members))]
    scroll.PrepareSubContent = read_method('scroll.pyc', 'Scroll', 'PrepareSubContent', {
        'uicore': Node(registry=Node(GetListGroupOpenState=lambda group, default: default)),
    }, (False, True)).__get__(scroll)
    changed = read_method(path, 'MyFleetPanel', 'DoHandleFleetChanged', {
        'FleetSvc': lambda: Node(GetMembers=lambda: roster),
        'session': Node(charid=2),
        'blue': Node(pyos=Node(synchro=Node(SleepWallclock=lambda ms: None))),
    }).__get__(panel)
    reload_entry = read_method(path, 'MyFleetPanel', 'ReloadScrollEntry', {}).__get__(panel)
    panel.AddChar = lambda member: reload_entry(root)
    joined = read_method(path, 'MyFleetPanel', 'OnFleetJoin_Local', {
        'session': Node(charid=2),
    }).__get__(panel)

    def add_nodes(index, nodes, parent, **options):
        # Model a fleetmate joining after the scroll has inserted the wing's
        # children but before PrepareSubContent assigns the parent's subNodes.
        scroll.nodes[index:index] = nodes
        if 3 not in roster:
            roster[3] = Node(charID=3)
            joined(roster[3])

    scroll.AddNodes = add_nodes
    changed()
    assert not errors, errors
    members = [node.members for node in scroll.nodes[1:]]
    assert_first_pass(members, scheduled)
    if scheduled:
        scheduled.pop()
        changed()
        assert not errors, errors
    return [node.members for node in scroll.nodes[1:]], reload_entry, root

def stock_first_pass(members, scheduled):
    assert members == [[1, 2], [1, 2]] and scheduled == [], (members, scheduled)

def patched_first_pass(members, scheduled):
    assert members == [[1, 2]] and scheduled == ['refresh'], (members, scheduled)

replay('stock.pyc', stock_first_pass)
members, reload_entry, root = replay('patched.pyc', patched_first_pass)
assert members == [[1, 2, 3]], members
# Once the rebuild has finished, a normal incremental refresh still runs.
reload_entry(root)
assert [node.members for node in root.scroll.nodes[1:]] == [[1, 2, 3]]
'''


if __name__ == '__main__':
    unittest.main()
