"""Headless signed-adapter checks; no client rendering or game session."""
import marshal
import builtins
from pathlib import Path
import sys
from types import ModuleType, SimpleNamespace

expressions = [marshal.loads(Path(path).read_bytes()) for path in sys.argv[1:]]
# A default login must not eagerly load the planetary client.
assert eval(expressions[2], {}) == {}
assert 'eve.common.script.util.planetCommon' not in sys.modules
assert 'eve.client.script.ui.shared.planet.pinContainers.ProcessorContainer' not in sys.modules

names = ['eve', 'eve.common', 'eve.common.script', 'eve.common.script.planet',
         'eve.common.script.planet.entities', 'eve.common.script.planet.entities.processPin',
         'eve.common.script.planet.entities.storagePin',
         'eve.common.lib', 'eve.common.lib.appConst', 'eve.client', 'eve.client.script',
         'eve.client.script.environment', 'eve.client.script.environment.invCache', 'utillib',
         'eve.client.script.environment.planet',
         'eve.client.script.environment.planet.clientColony', 'eve.client.script.ui',
         'eve.client.script.ui.shared', 'eve.client.script.ui.shared.planet',
         'eve.client.script.ui.shared.planet.planetEditModeContainer',
         'eve.client.script.ui.shared.planet.planetsWindow',
         'eve.client.script.ui.shared.planet.planetsWindow.coloniesPanel',
         'eve.client.script.ui.shared.planet.pinContainers',
         'eve.client.script.ui.shared.planet.pinContainers.ProcessorContainer', 'localization',
         'eve.common.script.util', 'eve.common.script.util.planetCommon']
for name in names:
    module = ModuleType(name)
    module.__path__ = []
    sys.modules[name] = module
    parent, _, child = name.rpartition('.')
    if parent:
        setattr(sys.modules[parent], child, module)

cpu = [1675, 7057, 12136, 17215, 21315, 25415]
power = [6000, 9000, 12000, 15000, 17000, 19000]
common = sys.modules['eve.common.script.util.planetCommon']
common.GetCPUOutput = lambda level: cpu[level]
common.GetPowerOutput = lambda level: power[level]
appConst = sys.modules['eve.common.lib.appConst']
appConst.SEC = 10000000


class StoragePin:
    def __init__(self, nominal):
        self.nominal = nominal

    def GetCapacity(self):
        return self.nominal


sys.modules['eve.common.script.planet.entities.storagePin'].StoragePin = StoragePin
original_capacity = StoragePin.GetCapacity


class InventoryContainer:
    def __init__(self, type_id):
        self.typeID = type_id
        self.native_capacity = SimpleNamespace(capacity=35000 if type_id == 2233 else 100, used=25)

    def GetCapacity(self, flag=None):
        self.last_flag = flag
        return self.native_capacity


sys.modules['eve.client.script.environment.invCache'].invCacheContainer = InventoryContainer
sys.modules['utillib'].KeyVal = SimpleNamespace
original_inventory_capacity = InventoryContainer.GetCapacity


class ProcessPin:
    def SetSchematic(self, schematicID, schematic=None):
        self.schematicID = schematicID
        self.products = dict(schematic or {1: 20, 2: 5, 3: 3, 4: 1})
        self.demands = {5: 3000}
        self.cycleTime = getattr(self, 'nominalCycleTicks', 1800 * appConst.SEC)
        return schematicID

    def GetProducts(self):
        return dict(self.products)


sys.modules['eve.common.script.planet.entities.processPin'].ProcessPin = ProcessPin

localization = sys.modules['localization']
localization.GetByLabel = lambda label, **kwargs: (label, kwargs)


class Caption:
    def SetSubtext(self, value):
        self.value = value


class ProcessorContainer:
    def __init__(self, pin, product_type):
        self.pin = pin
        self.product_type = product_type
        self.amountPerCycleTxt = Caption()
        self.amountPerHourTxt = Caption()
        self.native_updates = 0

    def _UpdateInfoCont(self):
        self.native_updates += 1
        # Native _UpdateInfoCont reads this number from the unmodified schematic,
        # while its volume display reads the pin's scaled products.
        amount = {1: 20, 2: 5, 3: 3, 4: 1}[self.product_type] if self.pin.schematicID else 0
        self.amountPerCycleTxt.SetSubtext(localization.GetByLabel('UI/PI/Common/UnitsAmount', amount=amount))
        self.amountPerHourTxt.SetSubtext(('native-volume', dict(self.pin.GetProducts())))
        return 'native-result'


sys.modules['eve.client.script.ui.shared.planet.pinContainers.ProcessorContainer'].ProcessorContainer = ProcessorContainer
original_update = ProcessorContainer._UpdateInfoCont
appConst = sys.modules['eve.common.lib.appConst']
appConst.typeInterplanetaryConsolidation = 2495
appConst.typeOmnipotent = 19430
levels = {2495: 2, 19430: None, 2505: 3}
skills = SimpleNamespace(GetEffectiveLevel=lambda type_id: levels.get(type_id),
                         GetRequiredSkills=lambda type_id: {2505: 1})
planets = []
wallet = SimpleNamespace(wealth=100)
services = {'skills': skills, 'planetSvc': SimpleNamespace(GetMyPlanets=lambda: planets), 'wallet': wallet}
sm = SimpleNamespace(GetService=lambda name: services[name])
builtins.sm = sm


class ClientColony:
    def __init__(self):
        self.pin = SimpleNamespace(heads=[(0, 1.0, 1.0)], GetExtractorHeadRadius=lambda: .01)
        self.quote_calls = []
        self.planet = SimpleNamespace(remoteHandler=SimpleNamespace(GetProgramResultInfo=self.quote))

    def quote(self, *args):
        self.quote_calls.append(args)
        return 600000, 9000000000, 12

    def GetPin(self, pin_id):
        return self.pin

    def CreateProgram(self, harmonic, ecuPinID, resourceTypeID, points=None, headRadius=None):
        return 37, 9000000000, 12, {0: .75}

    def PostValidateCreatePin(self, typeID, ownerID=None):
        service = sm.GetService('skills')
        if typeID == 1:
            rows = sm.GetService('planetSvc').GetMyPlanets()
            if rows and service.GetEffectiveLevel(appConst.typeOmnipotent) is None:
                level = service.GetEffectiveLevel(appConst.typeInterplanetaryConsolidation)
                if level is None or level < len(rows):
                    raise ValueError('colony limit')
        elif sm.GetService('wallet').wealth < 1:
            raise ValueError('wallet')
        for type_id, level in service.GetRequiredSkills(typeID).items():
            if service.GetEffectiveLevel(type_id) < level:
                raise ValueError('required skill')
        return ownerID


original_validate = ClientColony.PostValidateCreatePin
sys.modules['eve.client.script.environment.planet.clientColony'].ClientColony = ClientColony


# Import scaffolding for the additional native UI classes used by the adapter.
class PlanetEditModeContainer:
    def GetBuildEntryDisabledHint(self, typeID, groupID):
        pass


class ColoniesPanel:
    def ConstructEmptyEntries(self, planetsData):
        pass


sys.modules['eve.client.script.ui.shared.planet.planetEditModeContainer'].PlanetEditModeContainer = PlanetEditModeContainer
sys.modules['eve.client.script.ui.shared.planet.planetsWindow.coloniesPanel'].ColoniesPanel = ColoniesPanel


def rejects(call, message):
    try:
        call()
        raise AssertionError('expected rejection: ' + message)
    except ValueError as error:
        assert str(error) == message


for expression, cpu_scale, power_scale, products, maximum, storage_scale, speed in [
        (expressions[0], 2, 3, {1: 50, 2: 12, 3: 7, 4: 2}, 8, 1, 1),
        (expressions[1], 1, 1, {1: 2, 2: 1, 3: 1, 4: 1}, 1, 1, 1),
        (expressions[2], 1, 1, {1: 20, 2: 5, 3: 3, 4: 1}, 0, 1, 1),
        (expressions[3], 1, 1, {1: 20, 2: 5, 3: 3, 4: 1}, 12, 1, 1),
        (expressions[4], 100, 100, {1: 2000, 2: 500, 3: 300, 4: 100}, 100, 1, 1),
        (expressions[5], 1, 1, {1: 20, 2: 5, 3: 3, 4: 1}, 0, 1, 1),
        (expressions[6], 1, 1, {1: 20, 2: 5, 3: 3, 4: 1}, 0, 10, 10),
        (expressions[7], 1, 1, {1: 20, 2: 5, 3: 3, 4: 1}, 0, .1, 100),
        (expressions[8], 1, 1, {1: 20, 2: 5, 3: 3, 4: 1}, 0, 100, .1),
        (expressions[2], 1, 1, {1: 20, 2: 5, 3: 3, 4: 1}, 0, 1, 1)]:
    for _ in range(2):
        assert eval(expression, {'sm': sm}) == {}
        limit = maximum or 3
        planets[:] = range(limit - 1)
        colony = ClientColony()
        assert colony.PostValidateCreatePin(1, 42) == 42
        planets[:] = range(limit)
        rejects(lambda: colony.PostValidateCreatePin(1), 'colony limit')
        planets[:] = []
        levels[2505] = 0
        rejects(lambda: colony.PostValidateCreatePin(1), 'required skill')
        levels[2505] = 3
        wallet.wealth = 0
        rejects(lambda: colony.PostValidateCreatePin(2), 'wallet')
        wallet.wealth = 100
        assert skills.GetEffectiveLevel(2495) == 2
        assert original_validate.__globals__['sm'] is sm
        expected = (37, 9000000000, 12, {0: .75}) if expression == expressions[2] else (600000, 9000000000, 12, {0: .75})
        assert colony.CreateProgram('harmonic', (1, 0), 2267) == expected
        if expression == expressions[2]:
            assert colony.quote_calls == []
        else:
            assert colony.quote_calls == [((1, 0), 2267, [(0, 1.0, 1.0)], .01)]
            moved = [(0, 1.1, 1.2)]
            assert colony.CreateProgram('harmonic', 42, 2267, moved, .02) == expected
            assert colony.quote_calls[-1] == (42, 2267, moved, .02)
            previous_calls = len(colony.quote_calls)
            assert colony.CreateProgram('harmonic', 42, 2267, [], .02) == (37, 9000000000, 12, {0: .75})
            assert len(colony.quote_calls) == previous_calls
        if maximum == 0:
            assert ClientColony.PostValidateCreatePin is original_validate
        for level in range(6):
            assert common.GetCPUOutput(level) == cpu[level] * cpu_scale
            assert common.GetPowerOutput(level) == power[level] * power_scale
        for nominal in [500, 12000, 10000]:
            assert StoragePin(nominal).GetCapacity() == nominal * storage_scale
        office = InventoryContainer(2233)
        for unused in range(2):
            assert office.GetCapacity(4).capacity == 35000 * storage_scale
            assert office.GetCapacity(4).used == 25
            assert office.native_capacity.capacity == 35000
        ordinary = InventoryContainer(123)
        assert ordinary.GetCapacity(5) is ordinary.native_capacity
        assert ordinary.last_flag == 5
        if storage_scale == 1:
            assert InventoryContainer.GetCapacity is original_inventory_capacity
        if storage_scale == 1:
            assert StoragePin.GetCapacity is original_capacity
        pin = ProcessPin()
        for _ in range(2):
            assert pin.SetSchematic(121) == 121
            assert pin.products == products
            assert pin.demands == {5: 3000}
            assert pin.cycleTime == 1800 * appConst.SEC / speed
        source = {1: 20, 2: 5, 3: 3, 4: 1}
        pin.SetSchematic(121, source)
        assert source == {1: 20, 2: 5, 3: 3, 4: 1}
        assert pin.products == products
        tiny = ProcessPin()
        tiny.nominalCycleTicks = 1
        tiny.SetSchematic(121)
        assert tiny.cycleTime == appConst.SEC
        for product_type, nominal in source.items():
            single = ProcessPin()
            recipe = {product_type: nominal}
            single.SetSchematic(121, recipe)
            panel = ProcessorContainer(single, product_type)
            for expected_calls in [1, 2]:
                assert panel._UpdateInfoCont() == 'native-result'
                assert panel.amountPerCycleTxt.value == ('UI/PI/Common/UnitsAmount', {'amount': products[product_type]})
                assert panel.amountPerHourTxt.value == ('native-volume', dict(single.GetProducts()))
                assert panel.native_updates == expected_calls
                assert recipe == {product_type: nominal}
                assert single.demands == {5: 3000} and single.cycleTime == 1800 * appConst.SEC / speed
            single.schematicID = None
            panel._UpdateInfoCont()
            assert panel.amountPerCycleTxt.value == ('UI/PI/Common/UnitsAmount', {'amount': 0})
        if products == {1: 20, 2: 5, 3: 3, 4: 1}:
            assert ProcessorContainer._UpdateInfoCont is original_update
print('Planetary adapter assertions passed')
