"""Build 3662168: compare free-point previews with the same trained-SP baseline."""


def install_free_point_preview():
    from eve.client.script.ui.services.skillQueueSvc import SkillQueueService
    from eve.client.script.ui.services.skillsvc import SkillsSvc
    from eve.client.script.ui.skilltrading.applySkillPointsWindow import ApplySkillPointsListEntry

    if getattr(SkillQueueService.GetSkillLevelAndProgressWithFreePoints, '_eden_free_points', False):
        return

    def current_points(queue, skills, type_id):
        if queue.SkillInTraining(type_id) is not None:
            return queue.GetEstimatedSkillPointsTrained(type_id)
        return skills.MySkillPoints(type_id) or 0

    def preview_levels(self, allocations):
        from characterskills.util import GetSkillLevelRaw, GetLevelProgress
        result = {}
        for type_id, amount in allocations.iteritems():
            points = current_points(self, self.skills, type_id) + amount
            rank = self.skills.GetSkillRank(type_id)
            result[type_id] = (GetSkillLevelRaw(points, rank), GetLevelProgress(points, rank))
        return result

    def row_progress(self):
        from characterskills import GetSPForLevelRaw
        rank = self.skillSvc.GetSkillRank(self.skillTypeID)
        lower = GetSPForLevelRaw(rank, self.trainingToLevel - 1)
        upper = GetSPForLevelRaw(rank, self.trainingToLevel)
        points = current_points(self.skillQueueSvc, self.skillSvc, self.skillTypeID)
        return min(1.0, max(0.0, float(points - lower) / (upper - lower)))

    def time_saved(self):
        from characterskills import GetSPForLevelRaw
        from gametime import GetWallclockTime
        skill = self.skillSvc.GetSkill(self.skillTypeID)
        calculator = self.skillSvc.GetSkillTrainingTimeCalculator()
        lower = GetSPForLevelRaw(skill.skillRank, self.trainingToLevel - 1)
        _, duration = calculator.get_skill_points_and_time_to_train_given_existing_skill_points(
            self.skillTypeID, self.trainingToLevel, GetWallclockTime(), lower)
        # _progress is the native row's construction/animation baseline. Passive
        # training after opening must not subtract from the free-point award.
        return max(0.0, self.progressWithFreePoints - self._progress) * duration

    original_set_points = SkillsSvc.SetFreeSkillPoints
    original_changed = SkillsSvc.OnFreeSkillPointsChanged

    def set_points(self, new_points, showPointer=True):
        # Queue refreshes also call SetFreeSkillPoints. Only the explicit server
        # reward event should announce an increase; mining/bounty credits omit it.
        if not getattr(self, '_eden_announce_free_points', False) and (
                self.freeSkillPoints is None or new_points > self.freeSkillPoints):
            self.freeSkillPoints = new_points
            from eve.client.script.ui.services import skillsvc
            skillsvc.sm.ScatterEvent('OnFreeSkillPointsChanged_Local')
            return
        return original_set_points(self, new_points, showPointer)

    def points_changed(self, *args, **kwargs):
        previous = getattr(self, '_eden_announce_free_points', False)
        self._eden_announce_free_points = True
        try:
            return original_changed(self, *args, **kwargs)
        finally:
            self._eden_announce_free_points = previous

    preview_levels._eden_free_points = True
    SkillQueueService.GetSkillLevelAndProgressWithFreePoints = preview_levels
    ApplySkillPointsListEntry.GetProgress = row_progress
    ApplySkillPointsListEntry.GetTimeSaved = time_saved
    SkillsSvc.SetFreeSkillPoints = set_points
    SkillsSvc.OnFreeSkillPointsChanged = points_changed


install_free_point_preview()
