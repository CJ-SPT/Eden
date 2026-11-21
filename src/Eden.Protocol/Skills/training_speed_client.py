"""Build 3662168 skill estimates using Eden's active rate and saved queue."""


def install_training_speed(multiplier):
    import gametime
    from carbon.common.lib.const import MIN, SEC
    from characterskills import skill_training
    from eve.client.script.ui.services import skillsvc
    from eve.client.script.ui.services.skillQueueSvc import SkillQueueService
    from skills.client import skillGroupController

    # These three native consumers import the same SP/minute function. Scale
    # its result after clone grade and the projected booster expiry adjustment.
    current_rate = skill_training.GetSkillPointsPerMinute
    if getattr(current_rate, '_eden_training_speed', False):
        current_rate.multiplier = multiplier
        return

    def training_rate(primary, secondary):
        return current_rate(primary, secondary) * training_rate.multiplier

    def saved_active(entry):
        return (getattr(entry, 'edenTrainingPeriods', None) is not None and
                entry.trainingStartTime is not None and entry.trainingEndTime is not None)

    def points_at(entry, now):
        lower, upper = entry.trainingStartSP, entry.trainingDestinationSP
        if now <= entry.trainingStartTime:
            return lower
        if now >= entry.trainingEndTime:
            return upper
        for start, points, rate in reversed(entry.edenTrainingPeriods):
            if now >= start:
                from decimal import Decimal
                earned = Decimal(points) + Decimal(rate) * (now - start) / MIN
                return max(lower, min(upper, long(earned)))
        elapsed = now - entry.trainingStartTime
        return lower + (upper - lower) * elapsed // (entry.trainingEndTime - entry.trainingStartTime)

    original_progress = SkillQueueService.GetEstimatedSkillPointsTrained
    original_duration = SkillQueueService.GetAddedSpAndAddedTimeForSkill
    original_queue_end = SkillQueueService.GetTrainingEndTimeOfQueue

    def estimated_progress(self, skillTypeID):
        queue = self.GetServerQueue()
        if queue and queue[0].trainingTypeID == skillTypeID and saved_active(queue[0]):
            return points_at(queue[0], gametime.GetWallclockTime())
        return original_progress(self, skillTypeID)

    def added_training(self, skillTypeID, skillLevel, skillSet, theoreticalSkillPointsDict,
                       trainingTimeOffset, skillBoosters):
        position = self.FindInQueue(skillTypeID, skillLevel)
        saved = self.GetServerQueue()
        if position is not None and position < len(saved):
            entry = saved[position]
            if (saved_active(entry) and entry.trainingTypeID == skillTypeID and entry.trainingToLevel == skillLevel and
                    (position == 0 or not self.IsQueueChanged())):
                now = gametime.GetWallclockTime()
                # Native queue previews sample the clock once per level. Allow
                # for that drift while distinguishing a hypothetical placement.
                start_offset = max(0, entry.trainingStartTime - now)
                same_start = trainingTimeOffset == 0 if position == 0 else abs(trainingTimeOffset - start_offset) < SEC
                if same_start:
                    points = theoreticalSkillPointsDict.setdefault(skillTypeID, points_at(entry, now))
                    added_points = max(0, entry.trainingDestinationSP - points)
                    remaining = max(0, entry.trainingEndTime - max(now, entry.trainingStartTime))
                    accelerated = skillBoosters.is_any_booster_active_at_time(max(now, entry.trainingStartTime))
                    return added_points, remaining, accelerated
        return original_duration(self, skillTypeID, skillLevel, skillSet, theoreticalSkillPointsDict,
                                 trainingTimeOffset, skillBoosters)

    def queue_end(self):
        saved = self.GetServerQueue()
        if saved and saved_active(saved[-1]) and not self.IsQueueChanged():
            return saved[-1].trainingEndTime
        return original_queue_end(self)

    training_rate.multiplier = multiplier
    training_rate._eden_training_speed = True
    skill_training.GetSkillPointsPerMinute = training_rate
    skillsvc.GetSkillPointsPerMinute = training_rate
    skillGroupController.GetSkillPointsPerMinute = training_rate
    SkillQueueService.GetEstimatedSkillPointsTrained = estimated_progress
    SkillQueueService.GetAddedSpAndAddedTimeForSkill = added_training
    SkillQueueService.GetTrainingEndTimeOfQueue = queue_end


install_training_speed(eden_training_speed)
