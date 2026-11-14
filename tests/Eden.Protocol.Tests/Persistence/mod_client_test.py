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



class AutoMiningPanelTests(unittest.TestCase):
    def test_run_and_save_actions_stay_outside_the_scroll_and_the_picker_applies_settings(self):
        calls = []
        state = {'status': 'Stopped', 'oreGroups': ['Scordite', 'Veldspar'], 'oreFilter': 'Scordite',
                 'resourceFamily': 0, 'targetOrder': 0, 'approach': False, 'jettison': False,
                 'unloadStation': 0, 'unloadThreshold': 95}

        class Remote:
            def GetStatus(self):
                return state.copy()

            def GetStations(self):
                return [(60003760, 'Station')]

            def SaveSettings(self, *args):
                calls.append(args)
                state['oreFilter'] = args[0]
                return state.copy()

        class Control:
            def __init__(self, parent=None, **options):
                self.parent = parent
                self.children = []
                self.options = options
                self.destroyed = False
                self.text = options.get('text', '')
                self.value = options.get('setvalue', options.get('checked', ''))
                if 'options' in options:
                    self.value = options['options'][0][1]
                if parent:
                    parent.children.append(self)

            def SetText(self, text):
                self.text = text

            def GetValue(self):
                return self.value

            def SetValue(self, value):
                self.value = value

            def LoadOptions(self, options, value):
                self.value = value

            def SetChecked(self, value, report):
                self.value = value

            def add_button(self, button):
                button.parent = self
                self.children.append(button)

            def Load(self, contentList, noContentHint):
                self.rows = contentList
                self.selection = []

            def GetSelected(self):
                return self.selection

        class Window(Control):
            @classmethod
            def Open(cls, **options):
                window = cls(**options)
                window.content = Control(window)
                return window

        alignments = ['TOTOP', 'TOALL', 'TOBOTTOM', 'TOLEFT', 'TOLEFT_PROP', 'TORIGHT']
        scope = {name: Control for name in ('Button', 'ButtonGroup', 'Checkbox', 'Combo', 'Container', 'ContainerAutoSize',
                                           'EveHeaderSmall', 'EveLabelMedium', 'Scroll', 'ScrollContainer', 'SingleLineEditText')}
        scope.update(Window=Window, uiconst=types.SimpleNamespace(**{name: name for name in alignments}),
                     ButtonVariant=types.SimpleNamespace(PRIMARY=1), ButtonSizeMode=types.SimpleNamespace(DYNAMIC=1),
                     AxisAlignment=types.SimpleNamespace(START=1),
                     Generic=Control, GetFromClass=lambda entry_type, values: types.SimpleNamespace(**values),
                     session=types.SimpleNamespace(charid=1), sm=types.SimpleNamespace(RemoteSvc=lambda service: Remote()))
        source = ROOT / 'src/Eden.Server/Modding/Client/automining.py'
        exec('\n'.join(line for line in source.read_text(encoding='utf-8').splitlines()
                       if not line.startswith(('import ', 'from '))), scope)
        panel = scope['MiningPanel']()
        panel.open()
        content = panel.window.content
        status, run, footer, body = content.children
        self.assertEqual(('TOTOP', 'TOBOTTOM', 'TOALL'), tuple(item.options['align'] for item in (run, footer, body)))
        self.assertEqual(['Start', 'Stop'], [button.options['label'] for button in run.children])
        self.assertEqual(['Apply settings', 'Reload', 'Clear filter'],
                         [button.options['label'] for button in footer.children[-1].children])
        self.assertEqual('Stopped', status.text)

        panel.search_available.SetValue('invalid')
        panel.search_available.OnChange()
        self.assertEqual([], panel.available.rows)
        panel.search_available.SetValue('veld')
        panel.search_available.OnChange()
        panel.available.selection = panel.available.rows[:]
        panel.add_selected()
        panel.active.selection = [entry for entry in panel.active.rows if entry.oreGroup == 'Scordite']
        panel.remove_selected()
        self.assertEqual({'Veldspar'}, panel.active_groups)
        self.assertEqual([], calls)
        panel.threshold.SetValue('101')
        panel.apply()
        self.assertEqual([], calls)
        panel.threshold.SetValue('81')
        panel.apply()
        self.assertEqual([('Veldspar', 0, 0, False, False, 0, 81)], calls)
        panel.clear_filter()
        self.assertEqual(set(), panel.active_groups)
        panel.reload()
        self.assertEqual({'Veldspar'}, panel.active_groups)


if __name__ == '__main__':
    unittest.main()
