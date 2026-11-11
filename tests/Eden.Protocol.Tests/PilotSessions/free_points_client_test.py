"""Headless Python 2 checks of isolated native methods and Eden's signed adapter.

No client module bodies, game sessions, rendering or input are started.
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
archive = root / '.eden-original-code.ccp'
if not archive.exists():
    archive = root / 'tq/code.ccp'
assert hashlib.sha256(archive.read_bytes()).hexdigest() == (
    '37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368')
modules = ['characterskills/util.pyj', 'characterskills/skill_training.pyj',
           'eve/client/script/ui/services/skillsvc.pyj',
           'eve/client/script/ui/services/skillQueueSvc.pyj',
           'eve/client/script/ui/skilltrading/applySkillPointsWindow.pyj']
with zipfile.ZipFile(archive) as source:
    blobs = [zlib.decompress(source.read(name)) for name in modules]

worker = r'''
import marshal, sys, math

class Value(object):
    def __init__(self, **values):
        self.__dict__.update(values)

names = ['characterskills', 'characterskills.util', 'gametime',
         'eve', 'eve.client', 'eve.client.script', 'eve.client.script.ui',
         'eve.client.script.ui.services', 'eve.client.script.ui.services.skillsvc',
         'eve.client.script.ui.services.skillQueueSvc', 'eve.client.script.ui.skilltrading',
         'eve.client.script.ui.skilltrading.applySkillPointsWindow']
for name in names:
    module = type(sys)(name)
    module.__path__ = []
    sys.modules[name] = module
    parent, _, child = name.rpartition('.')
    if parent:
        setattr(sys.modules[parent], child, module)
characterskills = sys.modules['characterskills']
characterskills.MAX_SKILL_LEVEL = 5
characterskills.SKILL_POINT_MULTIPLIER = 250
now = [0]
sys.modules['gametime'].GetWallclockTime = lambda: now[0]
events, toasts = [], []
scope = {'__builtins__': __builtins__, 'math': math, 'characterskills': characterskills, 'DIVCONSTANT': 2.5 * math.log(2),
         'MIN': 600000000L, 'gametime': sys.modules['gametime'],
         'GetWallclockTime': lambda: now[0],
         'sm': Value(ScatterEvent=lambda *args: events.append(args)),
         'uthread': Value(new=lambda callback: toasts.append(callback)), 'HIGHLIGHT_SP_ADDED': 'sp'}

def walk(code):
    yield code
    for child in code.co_consts:
        if hasattr(child, 'co_code'):
            for nested in walk(child):
                yield nested

decoded = [marshal.loads(blob[8:]) for blob in BLOBS]
def native(index, owner, name):
    code = decoded[index]
    if owner:
        code = next(c for c in walk(code) if c.co_name == owner)
    code = next(c for c in code.co_consts if getattr(c, 'co_name', None) == name)
    return type(lambda: None)(code, scope)

for name in ['GetSPForLevelRaw', 'GetSkillLevelRaw', 'GetLevelProgress', 'GetSkillPointsPerMinute']:
    function = native(0, None, name)
    scope[name] = function
    setattr(sys.modules['characterskills.util'], name, function)
    setattr(characterskills, name, function)
sp = scope['GetSPForLevelRaw']
assert [sp(1, level) for level in range(1, 6)] == [250, 1415, 8000, 45255, 256000]
calc_code = next(c for c in walk(decoded[1]) if c.co_name == 'SkillTrainingTimeCalculator')
Calculator = type('Calculator', (object,), {
    c.co_name: type(lambda: None)(c, scope) for c in calc_code.co_consts if hasattr(c, 'co_code')})

class Skills(object):
    rank = 2
    points = 2829
    freeSkillPoints = 0
    rate_attribute = 10
    def MySkillPoints(self, type_id):
        return self.points
    def GetSkillRank(self, type_id):
        return self.rank
    def GetSkill(self, type_id):
        return Value(skillRank=self.rank, typeID=type_id)
    def GetSkillTrainingTimeCalculator(self):
        interface = Value(get_skill_rank=lambda type_id: self.rank,
                          get_primary_attribute_id=lambda type_id: 1,
                          get_secondary_attribute_id=lambda type_id: 2,
                          get_character_attribute_at_time=lambda *args: self.rate_attribute,
                          get_skill_accelerator_boosters=lambda: Value(get_expiry_times_after_time=lambda time: []))
        return Calculator(interface)
    def ShowSkillPointsNotification_thread(self):
        pass
    SetFreeSkillPoints = native(2, 'SkillsSvc', 'SetFreeSkillPoints')
    OnFreeSkillPointsChanged = native(2, 'SkillsSvc', 'OnFreeSkillPointsChanged')

class Queue(object):
    active = True
    estimated = 5157
    def __init__(self, skills):
        self.skills = skills
    def GetServerQueue(self):
        return [Value(trainingTypeID=3327, trainingEndTime=1)] if self.active else []
    SkillInTraining = native(3, 'SkillQueueService', 'SkillInTraining')
    GetSkillLevelAndProgressWithFreePoints = native(3, 'SkillQueueService', 'GetSkillLevelAndProgressWithFreePoints')
    def GetEstimatedSkillPointsTrained(self, type_id):
        assert self.active and type_id == 3327
        return self.estimated

class Row(object):
    skillTypeID = 3327
    GetTimeSaved = native(4, 'ApplySkillPointsListEntry', 'GetTimeSaved')
    @property
    def progress(self):
        return self.GetProgress()

skills = Skills()
queue = Queue(skills)
scope['sm'].GetService = lambda name: skills if name == 'skills' else Value(highlight_ui_element=lambda value: None)
old = queue.GetSkillLevelAndProgressWithFreePoints({3327: 352})[3327][1]
current = scope['GetLevelProgress'](queue.estimated, skills.rank)
assert old < current  # Reproduces the negative native time estimate.
skills.SetFreeSkillPoints(352, False)
assert len(toasts) == 1  # Native queue refresh announces a silent activity credit.
skills.freeSkillPoints = 0
toasts[:] = []
sys.modules['eve.client.script.ui.services.skillQueueSvc'].SkillQueueService = Queue
sys.modules['eve.client.script.ui.services.skillsvc'].SkillsSvc = Skills
sys.modules['eve.client.script.ui.services.skillsvc'].sm = scope['sm']
sys.modules['eve.client.script.ui.skilltrading.applySkillPointsWindow'].ApplySkillPointsListEntry = Row
assert eval(marshal.loads(EXPRESSION), {}) == {}
installed = Queue.GetSkillLevelAndProgressWithFreePoints.im_func
assert eval(marshal.loads(EXPRESSION), {}) == {}
assert Queue.GetSkillLevelAndProgressWithFreePoints.im_func is installed

for active in [False, True]:
    queue.active = active
    for rank in [1, 2, 5]:
        skills.rank = rank
        skills.points = sp(rank, 2) + 100
        queue.estimated = skills.points + 1000
        baseline = queue.estimated if active else skills.points
        for amount in [1, 352, sp(rank, 3) - baseline, sp(rank, 4) - baseline]:
            level, progress = queue.GetSkillLevelAndProgressWithFreePoints({3327: amount})[3327]
            for target in [3, 4, 5]:
                row = Row()
                row.skillSvc, row.skillQueueSvc = skills, queue
                row.trainingToLevel = target
                row._progress = row.GetProgress()
                row.progressWithFreePoints = 1.0 if level >= target else progress if level == target - 1 else 0.0
                lower, upper = sp(rank, target - 1), sp(rank, target)
                credited = max(0, min(baseline + amount, upper) - max(baseline, lower))
                for attribute in [10, 20, 25]:  # Alpha, Omega and boosted attributes.
                    skills.rate_attribute = attribute
                    expected = credited / (attribute * 1.5) * 600000000L
                    assert abs(row.GetTimeSaved() - expected) < 2, (active, rank, amount, target)
                    before = row.GetTimeSaved()
                    now[0] += 600000000L
                    queue.estimated += 15
                    assert row.GetTimeSaved() == before
                    queue.estimated -= 15
        assert queue.GetSkillLevelAndProgressWithFreePoints({}) == {}

for balance in [352, 727]:  # Mining and then bounty pool refreshes.
    skills.SetFreeSkillPoints(balance, False)
    assert skills.freeSkillPoints == balance and not toasts
assert events[-1] == ('OnFreeSkillPointsChanged_Local',)
skills.OnFreeSkillPointsChanged(1000)
assert skills.freeSkillPoints == 1000 and len(toasts) == 1
assert not skills._eden_announce_free_points
skills.SetFreeSkillPoints(800)
assert skills.freeSkillPoints == 800 and len(toasts) == 1
skills.freeSkillPoints = None
skills.SetFreeSkillPoints(800)
assert skills.freeSkillPoints == 800 and len(toasts) == 1
count = len(events)
skills.SetFreeSkillPoints(800)
assert len(events) == count
print('Native free-point preview assertions passed')
'''.replace('BLOBS', repr(blobs)).replace('EXPRESSION', repr(expression))

with os.add_dll_directory(str(binaries)):
    runtime = ctypes.CDLL(str(runtime_path))
    ctypes.c_int.in_dll(runtime, 'Py_NoSiteFlag').value = 1
    runtime.Py_InitializeEx(0)
    if runtime.PyRun_SimpleString(worker.encode('ascii')):
        raise SystemExit('Native free-point preview assertions failed')
