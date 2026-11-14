"""Headless signed-adapter checks; no client rendering or game session."""
import marshal
from pathlib import Path
import sys
from types import ModuleType

expressions = [marshal.loads(Path(path).read_bytes()) for path in sys.argv[1:]]
# A default login must not eagerly load the planetary client.
assert eval(expressions[2], {}) == {}
assert 'planetCommon' not in sys.modules

names = ['eve', 'eve.common', 'eve.common.script', 'eve.common.script.planet',
         'eve.common.script.planet.entities', 'eve.common.script.planet.entities.processPin',
         'planetCommon']
for name in names:
    module = ModuleType(name)
    module.__path__ = []
    sys.modules[name] = module
    parent, _, child = name.rpartition('.')
    if parent:
        setattr(sys.modules[parent], child, module)

cpu = [1675, 7057, 12136, 17215, 21315, 25415]
power = [6000, 9000, 12000, 15000, 17000, 19000]
common = sys.modules['planetCommon']
common.GetCPUOutput = lambda level: cpu[level]
common.GetPowerOutput = lambda level: power[level]


class ProcessPin:
    def SetSchematic(self, schematicID, schematic=None):
        self.products = dict(schematic or {1: 20, 2: 5, 3: 3, 4: 1})
        self.demands = {5: 3000}
        self.cycleTime = 1800
        return schematicID


sys.modules['eve.common.script.planet.entities.processPin'].ProcessPin = ProcessPin
for expression, cpu_scale, power_scale, products in [
        (expressions[0], 2, 3, {1: 50, 2: 12, 3: 7, 4: 2}),
        (expressions[1], 1, 1, {1: 2, 2: 1, 3: 1, 4: 1}),
        (expressions[2], 1, 1, {1: 20, 2: 5, 3: 3, 4: 1})]:
    for _ in range(2):
        assert eval(expression, {}) == {}
        for level in range(6):
            assert common.GetCPUOutput(level) == cpu[level] * cpu_scale
            assert common.GetPowerOutput(level) == power[level] * power_scale
        pin = ProcessPin()
        for _ in range(2):
            assert pin.SetSchematic(121) == 121
            assert pin.products == products
            assert pin.demands == {5: 3000}
            assert pin.cycleTime == 1800
        source = {1: 20, 2: 5, 3: 3, 4: 1}
        pin.SetSchematic(121, source)
        assert source == {1: 20, 2: 5, 3: 3, 4: 1}
        assert pin.products == products
print('Planetary adapter assertions passed')
