"""Exercise pinned probe visuals and HIC visuals and activation with inert objects."""
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
    button = zlib.decompress(source.read('eve/client/script/ui/inflight/shipModuleButton/shipmodulebutton.pyj'))
    godma = zlib.decompress(source.read('eve/client/script/environment/godma.pyj'))
    sequencer = zlib.decompress(source.read('eve/client/script/parklife/fxSequencer.pyj'))
worker = 'BLOB = %r\nHIC = %r\nBUTTON = %r\nGODMA = %r\nSEQUENCER = %r\n' % (blob, hic, button, godma, sequencer)
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

def method(parent, name):
    code = next(c for c in walk(parent) if c.co_name == name)
    return (lambda: None).__class__(code, globals())

assemble = method(marshal.loads(BLOB[8:]), 'Assemble')
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

    def Prepare(self):
        self.gfxModel = Stub()

field = marshal.loads(HIC[8:])
initialize = method(field, '__init__')
prepare = method(field, 'Prepare')
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
    expected = radius if radius is not None else 16000.0
    assert state.realRadius == expected
    assert state.radius == 20000.0
    prepare(state)
    assert state.gfxModel.scaling == (expected / 20000.0,) * 3
print('4 pinned HIC radius and scaling cases passed; no renderer or client started')

activation = next(c for c in walk(marshal.loads(SEQUENCER[8:])) if c.co_name == 'Activation')
repeat_activation = method(activation, 'Repeat')
purge_triggers = method(activation, 'PurgeTriggers')
update_repeat_time = method(activation, 'UpdateRepeatTime')
remove_trigger = method(activation, 'RemoveTrigger')
repeat_field = method(field, 'Repeat')
SECOND = 10000000L
blue = Stub()
blue.os = Stub()
blue.os.GetSimTime = lambda: now
heapq = Stub()
heapq.heappop = lambda entries: entries.pop(0)
heapq.heapify = lambda entries: None
for repeat in [1, 2147483647]:
    started = 134000000000000000L
    trigger = Stub()
    trigger.duration = 30000.0
    # OnSpecialFX has already normalized the wire count and AddTrigger has stamped the expiry.
    trigger.repeat = repeat
    trigger.stamp = started + 30 * SECOND * repeat
    stops = []
    state = Stub()
    state.triggers = [trigger]
    state.stamp = started + 30 * SECOND
    state.duration = trigger.duration
    state.effect = Stub()
    state.effect.Repeat = lambda duration: repeat_field(state.effect, duration)
    state.isOn = True
    state.Stop = lambda: stops.append(True)
    state.PurgeTriggers = lambda: purge_triggers(state)
    state.UpdateRepeatTime = lambda: update_repeat_time(state)
    now = started + 30 * SECOND + 1
    repeat_activation(state)
    if repeat == 1:
        assert stops == [True] and not state.triggers
    else:
        assert not stops and state.triggers == [trigger]
        assert state.stamp == started + 60 * SECOND
        remove_trigger(state, trigger)
        assert not state.triggers
print('2 pinned HIC visual expiry cases passed; no renderer or client started')

button = marshal.loads(BUTTON[8:])
relevant_effect = method(button, 'GetRelevantEffect')
activate_effect = method(button, 'ActivateEffect')
godma = marshal.loads(GODMA[8:])
wrapper = next(c for c in walk(godma) if c.co_name == 'EffectWrapper')
activate_wrapper = method(wrapper, 'Activate')
const = Stub()
const.orbitalStrikeAmmo = []
const.dgmEffOverload = 5
session = Stub()
session.IsItSafe = lambda: True
target = Stub()
target.GetActiveTargetID = lambda: 200
consider = Stub()
consider.DoAttackConfirmations = lambda target_id, effect: target_id == 200 and effect is focused
sm.ScatterEvent = lambda *args: None
sm.GetService = {'target': target, 'consider': consider, 'godma': service}.__getitem__
for script, name in [(29003, 'shipModuleFocusedWarpDisruptionScript'), (45010, 'shipModuleFocusedWarpScramblingScript')]:
    calls = []
    state = Stub()
    state.charge = Stub()
    state.charge.typeID = script
    state.moduleinfo = Stub()
    state.moduleinfo.isPointTargeted = False
    state.sr = Stub()
    state.sr.Get = lambda key: None
    state.GetRelevantEffect = lambda: relevant_effect(state)
    state.IsEffectRepeatable = lambda *args: True
    state.autorepeat = 1
    state.def_effect = Stub()
    state.def_effect.effectName = 'warpDisruptSphere'
    state.def_effect.effectID = 3380
    state.def_effect.effectCategory = 1
    state.def_effect.isActive = False
    state.def_effect.itemID = 100
    state.def_effect.statemanager = Stub()
    state.def_effect.statemanager.Activate = lambda *args: calls.append(args)
    state.def_effect.Activate = lambda target_id, repeat: activate_wrapper(state.def_effect, target_id, repeat)
    focused = Stub()
    focused.effectName = name
    focused.effectCategory = 2
    focused.isOffensive = True
    manager.GetDefaultEffect = {script: focused}.__getitem__
    assert relevant_effect(state) is focused
    activate_effect(state, state.def_effect, None, None)
    assert calls == [(100, 'warpDisruptSphere', 200, 1)]
print('2 pinned HIC script activation cases passed; no renderer or client started')
'''
with os.add_dll_directory(str(root / 'tq/bin64')):
    runtime = ctypes.CDLL(str(root / 'tq/bin64/python27.dll'))
    ctypes.c_int.in_dll(runtime, 'Py_NoSiteFlag').value = 1
    runtime.Py_InitializeEx(0)
    if runtime.PyRun_SimpleString(worker.encode('ascii')):
        raise SystemExit(1)
