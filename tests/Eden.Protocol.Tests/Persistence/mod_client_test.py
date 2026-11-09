"""Test the Eden-owned client adapter with inert stand-ins, without importing game modules."""
import importlib.util
import json
from pathlib import Path
import sys
import types
import unittest

ROOT = Path(__file__).resolve().parents[3]


class ClientModTests(unittest.TestCase):
    def load(self):
        spec = importlib.util.spec_from_file_location('eden_mod_loader_test', ROOT / 'tools/mod_client_loader.py')
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module

    def test_commands_are_namespaced_and_decode_structured_replies(self):
        module = self.load()
        calls = []

        class Remote:
            def Execute(self, identity, name, arguments):
                calls.append((identity, name, arguments))
                return {'success': True, 'json': json.dumps({'value': 7})}

        module.sm = types.SimpleNamespace(RemoteSvc=lambda service: Remote() if service == 'edenMods' else None)
        api = module.ModApi(types.SimpleNamespace(entries=[]), 'author.example')
        self.assertEqual({'value': 7}, api.command('read', '{}'))
        self.assertEqual([('author.example', 'read', '{}')], calls)
        module.sm.RemoteSvc = lambda service: types.SimpleNamespace(Execute=lambda *args: {'success': False, 'error': 'denied'})
        with self.assertRaisesRegex(RuntimeError, 'denied'):
            api.command('read')

    def test_bootstrap_registers_once_and_delivers_session_events(self):
        module = self.load()
        notifications = []
        changes = []
        fixture = types.ModuleType('fixture_client_mod')

        def register(api):
            api.menu('Fixture', lambda: None)
            api.on_session_changed(changes.append)

        fixture.register = register
        sys.modules[fixture.__name__] = fixture
        self.addCleanup(sys.modules.pop, fixture.__name__)
        module.MODS = [{'id': 'author.example', 'module': fixture.__name__}]
        module.sm = types.SimpleNamespace(RegisterNotify=notifications.append)
        module.start(None)
        module.start(None)
        self.assertEqual(1, len(notifications))
        self.assertEqual('author.example', module._manager.entries[0][0])
        self.assertEqual('Fixture', module._manager.entries[0][1])
        module._manager.OnSessionChanged(False, None, {'charid': (None, 90000001)})
        self.assertEqual([{'charid': (None, 90000001)}], changes)


if __name__ == '__main__':
    unittest.main()
