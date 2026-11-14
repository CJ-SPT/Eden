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

    def test_scripts_gifts_singletons_deadlines_and_item_rewards_remain_excluded(self):
        changes = [dict(nodeGraphID=1), dict(clientObjectives=[1]), dict(contentTags=[1]),
                   dict(initialAgentGiftTypeID=23), dict(expirationTime=60),
                   dict(missionRewards=dict(reward=dict(rewardTypeID=23, rewardQuantity=1)))]
        for change in changes:
            with self.subTest(change=change):
                mission = copy.deepcopy(self.mission)
                mission.update(change)
                self.assertIsNotNone(delivery_rejection(mission, 'A delivery', 100))
        self.mission['courierMission']['objectiveSingleton'] = 1
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

    def test_avenge_variants_keep_report_objectives_and_exclude_unverified_reinforcements(self):
        manifest = json.loads((ROOT / 'src/Eden.World/Agents/missions-3662168.json').read_text(encoding='utf-8'))
        missions = {m['missionId']: m for m in manifest['missions']}
        for mid in [1177, 1179, 1180, 1181, 1199, 1201, 1566]:
            with self.subTest(mission=mid):
                mission = missions[mid]
                self.assertEqual(2, len(mission['rooms']))
                self.assertEqual(1, len(mission['gates']))
                self.assertEqual(1, len(mission['structures']))
                self.assertTrue(mission['structures'][0]['required'])
                self.assertTrue(mission['structures'][0]['completesMission'])
                self.assertNotIn('drop', mission['structures'][0])
                self.assertTrue(all(not g['required'] for w in mission['waves'] for g in w['groups']))
        for mid in [1200, 1565]:
            self.assertNotIn('structures', missions[mid])
            self.assertEqual(6, sum(g['count'] for w in missions[mid]['waves'] for g in w['groups']))
        for mid in [1178, 1182, 1183, 1184, 1198]:
            self.assertNotIn(mid, missions)


if __name__ == '__main__':
    unittest.main()
