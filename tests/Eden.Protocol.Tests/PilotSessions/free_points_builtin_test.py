"""Exercise the signed adapter with built-in services and no game installation."""
import builtins
import marshal
from pathlib import Path
import sys
from types import ModuleType, SimpleNamespace


names = [
    'eve', 'eve.client', 'eve.client.script', 'eve.client.script.ui',
    'eve.client.script.ui.services', 'eve.client.script.ui.services.skillsvc',
    'eve.client.script.ui.services.skillQueueSvc', 'eve.client.script.ui.skilltrading',
    'eve.client.script.ui.skilltrading.applySkillPointsWindow',
]
for name in names:
    module = ModuleType(name)
    module.__path__ = []
    sys.modules[name] = module
    parent, _, child = name.rpartition('.')
    if parent:
        setattr(sys.modules[parent], child, module)

events, original_calls = [], []
builtins.sm = SimpleNamespace(ScatterEvent=lambda *args: events.append(args))


class Skills:
    freeSkillPoints = None

    def SetFreeSkillPoints(self, points, showPointer=True):
        original_calls.append((points, showPointer))
        self.freeSkillPoints = points

    def OnFreeSkillPointsChanged(self, points):
        self.SetFreeSkillPoints(points)


class Queue:
    def GetSkillLevelAndProgressWithFreePoints(self, allocations):
        raise AssertionError('Not used by startup')


class Row:
    pass


sys.modules['eve.client.script.ui.services.skillsvc'].SkillsSvc = Skills
sys.modules['eve.client.script.ui.services.skillQueueSvc'].SkillQueueService = Queue
sys.modules['eve.client.script.ui.skilltrading.applySkillPointsWindow'].ApplySkillPointsListEntry = Row
assert not hasattr(sys.modules['eve.client.script.ui.services.skillsvc'], 'sm')
expression = marshal.loads(Path(sys.argv[1]).read_bytes())
assert eval(expression, {}) == {}
installed = Skills.SetFreeSkillPoints
assert eval(expression, {}) == {}
assert Skills.SetFreeSkillPoints is installed

for initial, points in [(None, 352), (0, 352), (352, 727)]:
    skills = Skills()
    skills.freeSkillPoints = initial
    skills.SetFreeSkillPoints(points, False)
    assert skills.freeSkillPoints == points
    assert events[-1] == ('OnFreeSkillPointsChanged_Local',)
    assert not original_calls

skills.OnFreeSkillPointsChanged(1000)
assert skills.freeSkillPoints == 1000
assert original_calls == [(1000, True)]
assert not skills._eden_announce_free_points
print('Built-in free-point startup assertions passed')
