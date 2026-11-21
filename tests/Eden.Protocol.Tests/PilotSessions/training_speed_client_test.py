"""Exercise the signed training estimator with isolated native-shaped services."""
import builtins
import marshal
from pathlib import Path
import sys
from types import ModuleType, SimpleNamespace

builtins.long = int
MINUTE = 600000000
SECOND = 10000000
now = 10 * MINUTE
names = ['gametime', 'carbon', 'carbon.common', 'carbon.common.lib', 'carbon.common.lib.const',
         'characterskills', 'characterskills.skill_training', 'eve', 'eve.client', 'eve.client.script',
         'eve.client.script.ui', 'eve.client.script.ui.services', 'eve.client.script.ui.services.skillsvc',
         'eve.client.script.ui.services.skillQueueSvc', 'skills', 'skills.client', 'skills.client.skillGroupController']
for name in names:
    module = ModuleType(name)
    module.__path__ = []
    sys.modules[name] = module
    parent, _, child = name.rpartition('.')
    if parent:
        setattr(sys.modules[parent], child, module)

sys.modules['gametime'].GetWallclockTime = lambda: now
const = sys.modules['carbon.common.lib.const']
const.MIN, const.SEC = MINUTE, SECOND
calculator = sys.modules['characterskills.skill_training']
skill_service = sys.modules['eve.client.script.ui.services.skillsvc']
skill_group = sys.modules['skills.client.skillGroupController']
for module in [calculator, skill_service, skill_group]:
    module.GetSkillPointsPerMinute = lambda primary, secondary: primary + secondary / 2.0


class Queue:
    def __init__(self, saved):
        self.saved = saved
        self.visible = saved[:]

    def GetServerQueue(self):
        return self.saved[:]

    def FindInQueue(self, skill, level):
        return next((index for index, row in enumerate(self.visible)
                     if (row.trainingTypeID, row.trainingToLevel) == (skill, level)), None)

    def IsQueueChanged(self):
        return self.visible != self.saved

    def GetEstimatedSkillPointsTrained(self, skill):
        return -1

    def GetAddedSpAndAddedTimeForSkill(self, skill, level, skill_set, theoretical, offset, boosters):
        return 250, int(250 * MINUTE / calculator.GetSkillPointsPerMinute(20, 20)), False

    def GetTrainingEndTimeOfQueue(self):
        return -1


def row(skill, level, start, end, before, after, periods=()):
    return SimpleNamespace(trainingTypeID=skill, trainingToLevel=level, trainingStartTime=start,
                           trainingEndTime=end, trainingStartSP=before, trainingDestinationSP=after,
                           edenTrainingPeriods=periods)


def duration(queue, skill, level, offset, theoretical=None):
    boosters = SimpleNamespace(is_any_booster_active_at_time=lambda time: time < expiry)
    return queue.GetAddedSpAndAddedTimeForSkill(skill, level, {}, theoretical if theoretical is not None else {}, offset, boosters)


sys.modules['eve.client.script.ui.services.skillQueueSvc'].SkillQueueService = Queue
expressions = [marshal.loads(Path(path).read_bytes()) for path in sys.argv[1:]]

# This queue was scheduled at a prior rate. Saved progress and deadlines take
# precedence over forecasts, including at the exact booster expiry tick.
start = now
expiry = start + 2 * MINUTE
end = expiry + 320 * SECOND
head = row(3327, 1, start, end, 0, 250, [(start, '0', '45'), (expiry, '90', '30')])
tail = row(3327, 2, end, end + 2330 * SECOND, 250, 1415)
queue = Queue([head, tail])
for expression, factor in zip(expressions, [2.5, .1, 1]):
    assert eval(expression, {}) == {}
    for _ in range(2):
        assert eval(expression, {}) == {}
        for module in [calculator, skill_service, skill_group]:
            assert module.GetSkillPointsPerMinute(20, 20) == 30 * factor
            assert module.GetSkillPointsPerMinute(15, 15) == 22.5 * factor
    assert queue.GetEstimatedSkillPointsTrained(3300) == -1
    for sampled, expected in [(start - 1, 0), (start, 0), (expiry - 1, 89), (expiry, 90),
                              (expiry + MINUTE, 120), (end, 250), (end + MINUTE, 250)]:
        now = sampled
        assert queue.GetEstimatedSkillPointsTrained(3327) == expected
    now = expiry + MINUTE
    head_remaining = end - now
    assert duration(queue, 3327, 1, 0) == (130, head_remaining, False)
    assert duration(queue, 3327, 2, head_remaining, {3327: 250}) == (1165, 2330 * SECOND, False)
    assert queue.GetTrainingEndTimeOfQueue() == tail.trainingEndTime
    forecast = int(250 * MINUTE / (30 * factor))
    assert duration(queue, 3340, 1, head_remaining) == (250, forecast, False)
    queue.visible = queue.saved + [row(3340, 1, None, None, 0, 250)]
    assert queue.GetTrainingEndTimeOfQueue() == -1
    assert duration(queue, 3327, 1, 0) == (130, head_remaining, False)
    assert duration(queue, 3327, 2, head_remaining) == (250, forecast, False)
    queue.visible = queue.saved[:]
    assert duration(queue, 3327, 2, head_remaining + MINUTE) == (250, forecast, False)

# A partial Alpha level reports the scheduled cap, followed by a paused tail.
head = row(3327, 1, start, expiry, 10, 50)
tail = row(3327, 2, None, None, 250, 1415)
queue = Queue([head, tail])
now = start + MINUTE
assert queue.GetEstimatedSkillPointsTrained(3327) == 30
assert duration(queue, 3327, 1, 0) == (20, MINUTE, True)
assert duration(queue, 3327, 2, MINUTE) == (250, 500 * SECOND, False)
assert queue.GetTrainingEndTimeOfQueue() == -1
fractional = row(3327, 1, start, end, 41, 250, [(start, '41.625', '37.5')])
now = start + MINUTE // 100
assert Queue([fractional]).GetEstimatedSkillPointsTrained(3327) == 42
print('Training speed adapter assertions passed')
