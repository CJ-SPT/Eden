"""Headless failure-capture checks; no game, renderer or client session."""
import marshal
import os
from pathlib import Path
import sys
from types import ModuleType

expression = marshal.loads(Path(sys.argv[1]).read_bytes())
root = Path(sys.argv[2])
os.environ['TEMP'] = str(root)

for leaf in ('eve.client.script.ui.shared.neocom.corporation.corporationWindow',
             'eve.client.script.ui.services.corporation.base_corporation'):
    parts = leaf.split('.')
    for length in range(1, len(parts) + 1):
        name = '.'.join(parts[:length])
        if name not in sys.modules:
            module = ModuleType(name)
            module.__path__ = []
            sys.modules[name] = module
            if length > 1:
                setattr(sys.modules['.'.join(parts[:length - 1])], parts[length - 1], module)

result = object()
failure = AttributeError('private-input-must-not-be-logged')


class Window:
    def ApplyAttributes(self, attributes):
        return self.construct_corporation_panels(attributes)

    def construct_corporation_panels(self, attributes):
        if attributes:
            raise failure
        return result

    construct_administration_panels = construct_corporation_panels
    construct_alliance_panels = construct_corporation_panels
    reconstruct_main_tabs = construct_corporation_panels
    SelectPanel = construct_corporation_panels


class Corporation:
    def InviteToJoinCorp(self, characterID, confirm=True):
        assert characterID == 7
        if confirm:
            raise failure
        return result


sys.modules['eve.client.script.ui.shared.neocom.corporation.corporationWindow'].CorporationWindow = Window
sys.modules['eve.client.script.ui.services.corporation.base_corporation'].Corporation = Corporation
assert eval(expression, {}) == {}
wrapped = Window.ApplyAttributes
assert eval(expression, {}) == {}
assert Window.ApplyAttributes is wrapped
window = Window()
corp = Corporation()
assert window.ApplyAttributes(False) is result
assert corp.InviteToJoinCorp(7, confirm=False) is result
for action in (lambda: window.ApplyAttributes(True), lambda: corp.InviteToJoinCorp(7)):
    try:
        action()
        assert False, 'Expected the original exception'
    except AttributeError as error:
        assert error is failure

path = root / ('eden-corporation-%s.log' % os.getpid())
log = path.read_text()
assert log.count('installed-v1') == 1
assert 'event=construct_corporation_panels:failed error=AttributeError' in log
assert 'event=InviteToJoinCorp:failed error=AttributeError' in log
assert 'corporation_diagnostics_client_test.py' in log
assert 'private-input' not in log

# Exhaust successes first: the independent failure quota must still record errors.
for _ in range(200):
    window.ApplyAttributes(False)
for _ in range(200):
    try:
        corp.InviteToJoinCorp(7)
    except AttributeError as error:
        assert error is failure
lines = path.read_text().splitlines()
assert len(lines) == 256
assert sum('error=AttributeError' in line for line in lines) == 128

# A fresh install with an unusable output directory still preserves native calls.
delattr(wrapped, '_eden_corporation_diagnostic')
os.environ['TEMP'] = str(root / 'missing')
assert eval(expression, {}) == {}
assert window.ApplyAttributes(False) is result
try:
    corp.InviteToJoinCorp(7)
except AttributeError as error:
    assert error is failure
else:
    assert False, 'Diagnostic I/O must not swallow the native failure'
print('Corporation diagnostic assertions passed')
