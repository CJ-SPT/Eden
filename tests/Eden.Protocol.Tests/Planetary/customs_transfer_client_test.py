"""Headless customs callbacks; no client rendering or game session."""
import marshal
from pathlib import Path
import sys
from types import ModuleType


name = 'eve.client.script.ui.shared.planet.importExportUI'
for count in range(1, len(name.split('.')) + 1):
    part = '.'.join(name.split('.')[:count])
    module = ModuleType(part)
    module.__path__ = []
    sys.modules[part] = module
    parent, _, child = part.rpartition('.')
    if parent:
        setattr(sys.modules[parent], child, module)


class CustomsWindow:
    def __init__(self):
        self.spaceportPinID = 42
        self.importContents = {}
        self.exportContents = {}
        self.refreshes = 0
        self.requests = []
        self.fail = False

    def ResetContents(self):
        self.importContents = {}
        self.exportContents = {}
        self.refreshes += 1

    def OnRefreshPins(self, pinIDs):
        # Build 3662168 importExportUI.py, lines 574-578.
        if self.spaceportPinID in pinIDs:
            self.ResetContents()

    def OnPlanetPinsChanged(self, planetID):
        # The authoritative completion event still clears staged contents.
        self.ResetContents()

    def ConfirmCommodityTransfer(self, *args):
        # Simulate a cooperative UI yield before native request maps are built,
        # then a second click while the RPC is awaiting its server response.
        self.OnRefreshPins([self.spaceportPinID])
        imports, exports = dict(self.importContents), dict(self.exportContents)
        if self.fail:
            raise ValueError('rejected transfer')
        self.requests.append((imports, exports, args))
        self.ConfirmCommodityTransfer('second click')
        return 'native-result'


sys.modules[name].PlanetaryImportExportUI = CustomsWindow
expression = marshal.loads(Path(sys.argv[1]).read_bytes())
for unused in range(2):
    assert eval(expression, {}) == {}
    window = CustomsWindow()
    window.OnRefreshPins([1])
    assert window.refreshes == 0
    window.OnRefreshPins([42])
    assert window.refreshes == 1
    for imports, exports in [({1001: 5}, {}), ({}, {2398: 244000}), ({1001: 5}, {2398: 20})]:
        window.importContents, window.exportContents = dict(imports), dict(exports)
        before = window.refreshes
        for tick in range(3):
            window.OnRefreshPins([42])
        assert window.importContents == imports and window.exportContents == exports
        assert window.refreshes == before
        assert window.ConfirmCommodityTransfer('click') == 'native-result'
        assert window.requests[-1] == (imports, exports, ('click',))
        assert not window._eden_confirming_transfer
        assert len(window.requests) == 1
        window.OnPlanetPinsChanged(401)
        assert window.refreshes == before + 1
        assert not window.importContents and not window.exportContents
        window.requests[:] = []
    window.importContents = {1001: 5}
    window.fail = True
    try:
        window.ConfirmCommodityTransfer()
        raise AssertionError('missing rejection')
    except ValueError:
        pass
    assert not window._eden_confirming_transfer
    assert window.importContents == {1001: 5} and not window.requests
    window.fail = False
    assert window.ConfirmCommodityTransfer() == 'native-result'
    assert window.requests == [({1001: 5}, {}, ())]
print('Customs transfer assertions passed')
