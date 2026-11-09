"""Regressions for selecting combat-anomaly report variants from physical layouts."""
import importlib.util
import json
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location('anomaly_import', ROOT / 'tools/Import-CombatAnomalyEncounters.py')
IMPORTER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(IMPORTER)


class AnomalyLayoutTests(unittest.TestCase):
    def test_corrected_bindings_match_native_fixture_and_preserve_previous_recipes(self):
        fixture = json.loads((ROOT / 'tests/Eden.Protocol.Tests/Fixtures/CombatAnomalyWorldCatalog.json').read_text(encoding='utf-8'))
        groups = {t['id']: t['groupId'] for t in fixture['types']}
        dungeons = {str(r['key']): r['data'] for r in fixture['records'] if r['dataset'] == 'client/dungeons'}
        facts = json.loads((ROOT / 'src/Eden.World/Exploration/combat-anomaly-encounters-3662168.json').read_text(encoding='utf-8'))
        corrected = 0
        for report in facts['encounters']:
            for dungeon_id, previous in report.get('versionOneDungeonVariants', {}).items():
                with self.subTest(site=report['name'], dungeon=dungeon_id):
                    actual = report['dungeonVariants'][dungeon_id]
                    self.assertNotEqual(previous, actual)
                    self.assertEqual(actual, IMPORTER.sentry_layout_variant(report['variants'], dungeons[dungeon_id], groups))
                    self.assertGreaterEqual(previous, 0)
                    self.assertLess(previous, len(report['variants']))
                    corrected += 1
        self.assertEqual(19, corrected)

    def test_ambiguous_reports_do_not_force_a_layout_binding(self):
        variant = [dict(groups=[dict(sentry=True, types=[100], minimum=2, maximum=2)])]
        dungeon = dict(rooms={'1': dict(objects={'a': dict(typeID=100), 'b': dict(typeID=100)})})
        self.assertIsNone(IMPORTER.sentry_layout_variant([variant, variant], dungeon, {100: 383}))

    def test_layout_without_batteries_selects_the_unarmed_variant(self):
        armed = [dict(groups=[dict(sentry=True, types=[100], minimum=2, maximum=2)])]
        unarmed = [dict(groups=[dict(sentry=False, types=[200], minimum=2, maximum=2)])]
        self.assertEqual(1, IMPORTER.sentry_layout_variant([armed, unarmed], dict(rooms={}), {100: 383}))


if __name__ == '__main__':
    unittest.main()
