"""Test the Eden-owned client adapter with inert stand-ins, without importing game modules."""
import importlib.util
import json
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

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
        shown = []
        module.Manager.show = lambda manager: shown.append(manager)
        module.show_mods()
        module.show_mods()
        self.assertEqual([module._manager, module._manager], shown)
        self.assertEqual(1, len(notifications))
        self.assertEqual('author.example', module._manager.entries[0][0])
        self.assertEqual('Fixture', module._manager.entries[0][1])
        module._manager.OnSessionChanged(False, None, {'charid': (None, 90000001)})
        self.assertEqual([{'charid': (None, 90000001)}], changes)


    def test_neocom_menu_preserves_native_menu_if_the_loader_fails(self):
        spec = importlib.util.spec_from_file_location('eden_builder', ROOT / 'tools/Build-SkillClient.py')
        builder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(builder)
        original = ['Native']
        scope = {}
        source = builder.MOD_MENU_WRAPPER.replace('        pass', '        return original')
        scope['original'] = original
        exec(source, scope)
        with patch.dict(sys.modules, {'eden_mod_loader': None}), self.assertLogs('eden.mods', 'ERROR'):
            self.assertIs(scope['GetMenu'](None), original)

    def test_menu_defers_mods_and_keeps_good_entries_if_another_mod_fails(self):
        def register_broken(api):
            api.menu('Broken', lambda: None)
            raise RuntimeError('broken mod')

        def register_mining(api):
            api.menu('AutoMining', lambda: None)

        services = []
        scope = {
            'MODS': [{'id': 'broken', 'module': 'eden_broken'}, {'id': 'mining', 'module': 'eden_mining'}],
            'sm': types.SimpleNamespace(RegisterNotify=services.append),
        }
        exec((ROOT / 'tools/mod_client_loader.py').read_text(encoding='utf-8'), scope)
        menu_module = types.ModuleType('carbonui.control.contextMenu.menuEntryData')
        menu_module.MenuEntryData = lambda label, func: (label, func)
        mods = {
            menu_module.__name__: menu_module,
            'eden_broken': types.SimpleNamespace(register=register_broken),
            'eden_mining': types.SimpleNamespace(register=register_mining),
        }
        with patch.dict(sys.modules, mods):
            native = ['Native']
            entries = scope['menu'](native)
            self.assertEqual(native, entries[:-1])
            self.assertEqual('Eden Mods', entries[-1][0])
            self.assertIsNone(scope['_manager'])
            self.assertEqual([], services)
            shown = []
            scope['Manager'].show = lambda manager: shown.append([entry[1] for entry in manager.entries])
            with self.assertLogs('eden.mods', 'ERROR') as logged:
                entries[-1][1]()
            self.assertIn('Could not load mod broken', logged.output[0])
            entries[-1][1]()
            self.assertEqual([['AutoMining'], ['AutoMining']], shown)
            self.assertEqual(1, len(services))



if __name__ == '__main__':
    unittest.main()
