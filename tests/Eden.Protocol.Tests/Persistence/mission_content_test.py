"""Admission regressions for catalog-complete mission delivery content."""
import copy
import csv
import json
import pathlib
import sys
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / 'tools'))
from mission_content import delivery_rejection


class MissionAdmissionTests(unittest.TestCase):
    def setUp(self):
        self.mission = dict(contentTemplate='agent.missionTemplatizedContent_BasicCourierMission',
                            courierMission=dict(objectiveSingleton=0, objectiveTypeID=16044, objectiveQuantity=10))

    def test_supported_stack_delivery_is_admitted(self):
        self.assertIsNone(delivery_rejection(self.mission, 'A delivery', 100))
        self.mission['contentTemplate'] = 'agent.missionTemplatizedContent_GenericStorylineCourierMission'
        self.assertIsNone(delivery_rejection(self.mission, 'A delivery', 100))

    def test_special_agent_lifecycles_and_chains_remain_excluded(self):
        for template in ['StorylineCourierMission', 'EpicArcCourierMission', 'ResearchCourierMission']:
            with self.subTest(template=template):
                self.mission['contentTemplate'] = 'agent.missionTemplatizedContent_' + template
                self.assertIsNotNone(delivery_rejection(self.mission, 'A delivery', 100))
        self.setUp()
        self.assertIsNotNone(delivery_rejection(self.mission, 'Cargo - The Delivery (2 of 5)', 100))
        self.mission['messages'] = {'messages.mission.completed.nextmission.agentsays': 100}
        self.assertIsNotNone(delivery_rejection(self.mission, 'A delivery', 100))

    def test_scripts_gifts_singletons_and_item_rewards_remain_excluded(self):
        changes = [dict(nodeGraphID=1), dict(clientObjectives=[1]), dict(contentTags=[1]),
                   dict(initialAgentGiftTypeID=23),
                   dict(missionRewards=dict(reward=dict(rewardTypeID=23, rewardQuantity=1)))]
        for change in changes:
            with self.subTest(change=change):
                mission = copy.deepcopy(self.mission)
                mission.update(change)
                self.assertIsNotNone(delivery_rejection(mission, 'A delivery', 100))
        self.mission['courierMission']['objectiveSingleton'] = 1
        self.assertIsNotNone(delivery_rejection(self.mission, 'A delivery', 100))

    def test_finite_catalog_deadlines_are_admitted(self):
        for minutes in [1, 120, 240, 480, 720, 10080]:
            with self.subTest(minutes=minutes):
                self.mission['expirationTime'] = minutes
                self.assertIsNone(delivery_rejection(self.mission, 'A delivery', 100))

    def test_unsupported_deadline_lifecycles_are_excluded(self):
        for minutes in [0, -1, 10081, None, 1.5, True, '120']:
            with self.subTest(minutes=minutes):
                self.mission['expirationTime'] = minutes
                self.assertIsNotNone(delivery_rejection(self.mission, 'A delivery', 100))

    def test_invalid_cargo_is_not_admitted(self):
        for quantity in [0, -1, 2147483648]:
            with self.subTest(quantity=quantity):
                self.mission['courierMission']['objectiveQuantity'] = quantity
                self.assertIsNotNone(delivery_rejection(self.mission, 'A delivery', 100))
        self.setUp()
        self.assertIsNotNone(delivery_rejection(self.mission, 'A delivery', 0))

    def test_audit_covers_the_pinned_catalog_and_exactly_matches_the_offer_manifest(self):
        with (ROOT / 'docs/data/mission-support-3662168.csv').open(encoding='utf-8', newline='') as stream:
            audit = list(csv.DictReader(stream))
        manifest = json.loads((ROOT / 'src/Eden.World/Agents/missions-3662168.json').read_text(encoding='utf-8'))
        self.assertEqual(2892, len(audit))
        self.assertEqual(len(audit), len({row['missionId'] for row in audit}))
        admitted = {int(row['missionId']) for row in audit if row['status'] == 'admitted'}
        self.assertEqual({m['missionId'] for m in manifest['missions']}, admitted)
        self.assertTrue(all(row['reason'] for row in audit))


if __name__ == '__main__':
    unittest.main()
