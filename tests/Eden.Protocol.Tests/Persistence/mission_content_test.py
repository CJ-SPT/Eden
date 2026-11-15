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

    def test_scripts_gifts_and_item_rewards_remain_excluded(self):
        changes = [dict(nodeGraphID=1), dict(clientObjectives=[1]), dict(contentTags=[1]),
                   dict(initialAgentGiftTypeID=23),
                   dict(missionRewards=dict(reward=dict(rewardTypeID=23, rewardQuantity=1)))]
        for change in changes:
            with self.subTest(change=change):
                mission = copy.deepcopy(self.mission)
                mission.update(change)
                self.assertIsNotNone(delivery_rejection(mission, 'A delivery', 100))
    def test_singleton_delivery_requires_exactly_one_item(self):
        cargo = self.mission['courierMission']
        cargo.update(objectiveSingleton=1, objectiveQuantity=1)
        self.assertIsNone(delivery_rejection(self.mission, 'A delivery', 100))
        for quantity in [0, -1, 2, 2147483648, None, True, 1.0]:
            with self.subTest(quantity=quantity):
                cargo['objectiveQuantity'] = quantity
                self.assertIsNotNone(delivery_rejection(self.mission, 'A delivery', 100))

    def test_other_singleton_forms_remain_excluded(self):
        for singleton in [-1, 2, None, True, '1']:
            with self.subTest(singleton=singleton):
                self.mission['courierMission'].update(objectiveSingleton=singleton, objectiveQuantity=1)
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

    def test_retribution_uses_reviewed_outposts_and_keeps_unverified_factions_blocked(self):
        manifest = json.loads((ROOT / 'src/Eden.World/Agents/missions-3662168.json').read_text(encoding='utf-8'))
        missions = {m['missionId']: m for m in manifest['missions']}
        for mid, target, guards in [(1186, 593786, 17), (1189, 593786, 17),
                                    (1187, 60753, 16), (1188, 60799, 20),
                                    (1190, 56336, 21), (1191, 56336, 21)]:
            with self.subTest(mission=mid):
                mission = missions[mid]
                self.assertEqual(3, mission['level'])
                self.assertEqual(2, len(mission['rooms']))
                self.assertEqual(1, len(mission['gates']))
                self.assertEqual([dict(objectId=target, required=True, completesMission=True)], mission['structures'])
                self.assertEqual(guards, sum(g['count'] for w in mission['waves'] for g in w['groups']))
                self.assertTrue(all(not g['required'] for w in mission['waves'] for g in w['groups']))
                self.assertTrue(mission['source'].startswith('https://wiki.eveuniversity.org/Retribution_'))
        for mid in [1186, 1189]:
            elite = [g for w in missions[mid]['waves'] for g in w['groups'] if g['types'] == [16568, 16567]]
            self.assertEqual(1, len(elite))
            self.assertEqual(2, elite[0]['count'])
        for mid in [1192, 1194, 1196, 1197]:
            self.assertNotIn(mid, missions)

    def test_spy_objectives_keep_report_groups_and_resolve_sansha_display_aliases(self):
        manifest = json.loads((ROOT / 'src/Eden.World/Agents/missions-3662168.json').read_text(encoding='utf-8'))
        missions = {m['missionId']: m for m in manifest['missions']}
        for mid, level, required, optional in [(888, 2, 4, 6), (898, 3, 8, 6),
                                                (902, 2, 2, 9), (901, 3, 2, 9), (900, 4, 4, 15)]:
            with self.subTest(mission=mid):
                mission = missions[mid]
                groups = mission['waves'][0]['groups']
                self.assertEqual(level, mission['level'])
                self.assertEqual(1, len(mission['waves']))
                self.assertEqual(2, len(mission['rooms']))
                self.assertEqual(1, len(mission['gates']))
                self.assertEqual(mission['rooms'][1]['roomId'], mission['waves'][0]['roomId'])
                self.assertEqual(required, sum(g['count'] for g in groups if g.get('required', True)))
                self.assertEqual(optional, sum(g['count'] for g in groups if not g.get('required', True)))
                self.assertTrue(all(not g.get('completesMission') and not g.get('trigger') for g in groups))
                self.assertNotIn('structures', mission)
        # Report display aliases identify explicit hulls, rather than arbitrary NPC substitutes.
        for mid, expected in [(902, [[23989]]), (901, [[24019]]), (900, [[24021, 24020], [24158, 24157]])]:
            groups = missions[mid]['waves'][0]['groups']
            self.assertEqual(expected, [g['types'] for g in groups if g.get('required', True)])
        # Unreviewed spies and the known timed Serpentis variant remain unavailable.
        for mid in [891, 893, 895, 899, 903, 1062, 1063, 1064, 1065]:
            self.assertNotIn(mid, missions)

    def test_level_three_seek_objectives_use_report_blitz_groups(self):
        manifest = json.loads((ROOT / 'src/Eden.World/Agents/missions-3662168.json').read_text(encoding='utf-8'))
        missions = {m['missionId']: m for m in manifest['missions']}
        for mid, required, optional in [(924, 5, 6), (927, 9, 0), (930, 4, 6), (936, 7, 0)]:
            with self.subTest(mission=mid):
                mission = missions[mid]
                groups = mission['waves'][0]['groups']
                self.assertEqual(3, mission['level'])
                self.assertEqual(1, len(mission['waves']))
                self.assertEqual(2, len(mission['rooms']))
                self.assertEqual(required, sum(g['count'] for g in groups if g['required']))
                self.assertEqual(optional, sum(g['count'] for g in groups if not g['required']))
                self.assertTrue(all(not g.get('completesMission') and not g.get('trigger') for g in groups))
                self.assertNotIn('structures', mission)
        # These catalog pockets differ from the reviewed Sansha level-one dungeon;
        # the level-three report cannot establish their difficulty/roster binding.
        for mid in [933, 939]:
            self.assertNotIn(mid, missions)


if __name__ == '__main__':
    unittest.main()
