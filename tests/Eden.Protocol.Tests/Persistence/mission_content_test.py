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
from mission_combat_expansion import expand


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


class CombatExpansionTests(unittest.TestCase):
    def setUp(self):
        fixture = json.loads((ROOT / 'tests/Eden.Protocol.Tests/Fixtures/MissionCatalog.json').read_text(encoding='utf-8'))
        self.records = {(r['dataset'], str(r['key'])): r['data'] for r in fixture['records']}
        self.types = {t['name']: t['id'] for t in fixture['types']}

    def compile(self):
        missions = []

        def source(db, dataset, key):
            return self.records[dataset, str(key)]

        def group(names, count, distance, **flags):
            return dict(types=[self.types[name] for name in names.split('/')], count=count, range=distance, **flags)

        expand(source, None, missions, group)
        return missions

    def test_reviewed_encounters_reproduce_manifest_without_the_reference_repository_or_network(self):
        manifest = json.loads((ROOT / 'src/Eden.World/Agents/missions-3662168.json').read_text(encoding='utf-8'))
        expected = [m for m in manifest['missions'] if m.get('offerOrder') == 2 and m['missionId'] != 1199]
        self.assertEqual(19, len(expected))
        self.assertEqual(expected, self.compile())
        alias = next(m for m in manifest['missions'] if m['missionId'] == 1199)
        original = copy.deepcopy(next(m for m in expected if m['missionId'] == 1179))
        original['missionId'] = 1199
        self.assertEqual(original, alias)
        self.assertEqual(self.records['client/missions', '1179']['killMission'],
                         self.records['client/missions', '1199']['killMission'])

    def test_changed_lifecycles_cargo_factions_and_room_topology_fail_closed(self):
        changes = [('client/missions', '888', 'nodeGraphID', 1),
                   ('client/missions', '2470', 'initialAgentGiftTypeID', 23),
                   ('client/dungeons', '214', 'factionID', 500011),
                   ('client/dungeons', '214', 'connections', []),
                   ('client/dungeons', '927', 'connections', [])]
        for dataset, key, field, value in changes:
            with self.subTest(dataset=dataset, key=key, field=field):
                original = self.records[dataset, key]
                self.records[dataset, key] = dict(original, **{field: value})
                with self.assertRaises(ValueError):
                    self.compile()
                self.records[dataset, key] = original
        self.records['client/missions', '888']['killMission']['objectiveQuantity'] = 1
        with self.assertRaises(ValueError):
            self.compile()


if __name__ == '__main__':
    unittest.main()
