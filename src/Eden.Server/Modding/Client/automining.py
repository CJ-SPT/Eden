"""AutoMining controls for the build-3662168 Eden Mods menu."""
import carbonui.const as uiconst
from carbonui.button.const import ButtonVariant
from carbonui.button.group import ButtonGroup, ButtonSizeMode
from carbonui.control.button import Button
from carbonui.control.checkbox import Checkbox
from carbonui.control.combo import Combo
from carbonui.control.scrollContainer import ScrollContainer
from carbonui.control.singlelineedits.singleLineEditText import SingleLineEditText
from carbonui.control.window import Window
from carbonui.primitives.container import Container
from carbonui.primitives.containerAutoSize import ContainerAutoSize
from carbonui.uiconst import AxisAlignment
from eve.client.script.ui.control.entries.generic import Generic
from eve.client.script.ui.control.entries.util import GetFromClass
from eve.client.script.ui.control.eveLabel import EveHeaderSmall, EveLabelMedium
from eve.client.script.ui.control.eveScroll import Scroll


def register(api):
    panel = MiningPanel()
    api.menu('AutoMining', panel.open)
    api.on_session_changed(panel.session_changed)
    sm.RegisterNotify(panel)


class MiningWindow(Window):
    default_windowID = 'eden_automining'
    default_width = 660
    default_height = 680
    default_minSize = (480, 340)


class MiningPanel(object):
    __notifyevents__ = ['OnEdenAutoMiningStatus']

    FAMILY_OPTIONS = [('Ore', 0), ('Ice', 1), ('Gas', 2)]
    ORDER_OPTIONS = [('Nearest first', 0), ('Largest surveyed deposit', 1), ('Smallest surveyed deposit', 2)]
    FLEET_OPTIONS = [('Solo', 0), ('Lead fleet (boss)', 1), ('Follow fleet boss', 2)]
    KEEP_HARVEST = ('Keep harvest on board', 0)

    def __init__(self):
        self.window = None
        self.revision = 0
        self.busy = False

    def open(self, *args):
        if self.is_current(self.revision):
            self.window.Maximize()
            return
        if self.busy:
            return
        self.revision += 1
        self.window = MiningWindow.Open(caption='AutoMining')
        content = self.window.content
        self.status = EveLabelMedium(parent=content, text='Loading status...', align=uiconst.TOTOP, padBottom=8)
        actions = ButtonGroup(parent=content, align=uiconst.TOTOP, button_alignment=AxisAlignment.START,
                              button_size_mode=ButtonSizeMode.DYNAMIC, padBottom=10)
        actions.add_button(Button(label='Start', func=self.start, variant=ButtonVariant.PRIMARY))
        actions.add_button(Button(label='Stop', func=self.stop))

        footer = ContainerAutoSize(parent=content, align=uiconst.TOBOTTOM, alignMode=uiconst.TOTOP)
        self.notice = EveLabelMedium(parent=footer, align=uiconst.TOTOP, padTop=6)
        footer_actions = ButtonGroup(parent=footer, align=uiconst.TOTOP, button_alignment=AxisAlignment.START,
                                     button_size_mode=ButtonSizeMode.DYNAMIC)
        footer_actions.add_button(Button(label='Apply settings', func=self.apply, variant=ButtonVariant.PRIMARY))
        footer_actions.add_button(Button(label='Reload', func=self.reload))
        footer_actions.add_button(Button(label='Clear filter', func=self.clear_filter))

        body = ScrollContainer(parent=content, align=uiconst.TOALL, alignMode=uiconst.TOTOP, padBottom=4)
        self.build_route(body)
        self.build_options(body)
        self.build_filter(body)
        self.family.OnChange = self.family_changed
        self.station_names = {}
        self.request('GetStatus', load_settings=True)

    def build_options(self, body):
        heading = Container(parent=body, align=uiconst.TOTOP, height=50)
        family = Container(parent=heading, align=uiconst.TOLEFT_PROP, width=0.36, padRight=12)
        order = Container(parent=heading, align=uiconst.TOALL)
        EveLabelMedium(parent=family, text='Resource', align=uiconst.TOTOP, padBottom=2)
        EveLabelMedium(parent=order, text='Target order', align=uiconst.TOTOP, padBottom=2)
        self.family = Combo(parent=family, align=uiconst.TOTOP, options=self.FAMILY_OPTIONS)
        self.order = Combo(parent=order, align=uiconst.TOTOP, options=self.ORDER_OPTIONS)

        toggles = Container(parent=body, align=uiconst.TOTOP, height=28, padTop=4, padBottom=6)
        move = Container(parent=toggles, align=uiconst.TOLEFT_PROP, width=0.5)
        full = Container(parent=toggles, align=uiconst.TOALL)
        self.approach = Checkbox(parent=move, align=uiconst.TOTOP, text='Approach out-of-range targets')
        self.jettison = Checkbox(parent=full, align=uiconst.TOTOP, text='Jettison when unloading')

        EveHeaderSmall(parent=body, text='Unload station', align=uiconst.TOTOP, padTop=8, padBottom=4)
        search = Container(parent=body, align=uiconst.TOTOP, height=30, padBottom=4)
        Button(parent=search, align=uiconst.TORIGHT, label='Search', func=self.search_stations, padLeft=8)
        self.station_search = SingleLineEditText(parent=search, align=uiconst.TOALL, maxLength=100,
                                                 hintText='Station or system name')
        self.station_search.OnReturn = self.search_stations
        hauling = Container(parent=body, align=uiconst.TOTOP, height=50, padBottom=8)
        threshold = Container(parent=hauling, align=uiconst.TORIGHT, width=108, padLeft=12)
        station = Container(parent=hauling, align=uiconst.TOALL)
        EveLabelMedium(parent=station, text='Search results / selected station', align=uiconst.TOTOP, padBottom=2)
        EveLabelMedium(parent=threshold, text='Unload hold %', align=uiconst.TOTOP, padBottom=2)
        self.station = Combo(parent=station, align=uiconst.TOTOP, options=[self.KEEP_HARVEST])
        self.threshold = SingleLineEditText(parent=threshold, align=uiconst.TOTOP, maxLength=3, setvalue='95')

    def build_route(self, body):
        self.route_entries = []
        EveHeaderSmall(parent=body, text='Mining systems', align=uiconst.TOTOP, padBottom=4)
        search = Container(parent=body, align=uiconst.TOTOP, height=30, padBottom=4)
        Button(parent=search, align=uiconst.TORIGHT, label='Search', func=self.search_systems, padLeft=8)
        self.system_search = SingleLineEditText(parent=search, align=uiconst.TOALL, maxLength=100,
                                                hintText='Search system name')
        self.system_search.OnReturn = self.search_systems
        results = Container(parent=body, align=uiconst.TOTOP, height=30, padBottom=4)
        Button(parent=results, align=uiconst.TORIGHT, label='Add system', func=self.add_system, padLeft=8)
        self.system_results = Combo(parent=results, align=uiconst.TOALL, options=[('Search for a system', 0)])
        self.system_names = {}
        self.route_list = Scroll(parent=body, align=uiconst.TOTOP, height=72, hasUnderlay=True, multiSelect=False)
        route_actions = ButtonGroup(parent=body, align=uiconst.TOTOP, button_alignment=AxisAlignment.START,
                                    button_size_mode=ButtonSizeMode.DYNAMIC, padBottom=6)
        route_actions.add_button(Button(label='Remove', func=self.remove_system))
        route_actions.add_button(Button(label='Move up', func=lambda *args: self.move_system(-1)))
        route_actions.add_button(Button(label='Move down', func=lambda *args: self.move_system(1)))
        self.fleet = Combo(parent=body, align=uiconst.TOTOP, options=self.FLEET_OPTIONS, padBottom=4)
        self.fleet_hint = EveLabelMedium(parent=body, align=uiconst.TOTOP, padBottom=10)
        self.fleet.OnChange = self.fleet_changed
        self.refresh_route()
        self.fleet_changed()

    def fleet_changed(self, *args):
        hints = {0: 'Mines selected ore through every belt. Rats are skipped. Empty list = current grid.',
                 1: 'Fleet boss leads this route. Other pilots select Follow fleet boss.',
                 2: "Uses the boss's destination, ignoring your system list. The boss must start Lead fleet first."}
        self.fleet_hint.SetText(hints[self.fleet.GetValue()])

    def refresh_route(self):
        self.route_list.Load(contentList=[GetFromClass(Generic, {'label': '%d. %s' % (index + 1, name),
                                                               'routeIndex': index})
                                          for index, (_, name) in enumerate(self.route_entries)],
                             noContentHint='Current grid only. Search above to add a mining system.')

    def add_system(self, *args):
        system = self.system_results.GetValue()
        if not system:
            self.notice.SetText('Search for a system and choose a result first.')
            return
        if len(self.route_entries) >= 32:
            self.notice.SetText('A route can contain at most 32 systems.')
            return
        self.route_entries.append((system, self.system_names[system]))
        self.refresh_route()
        self.notice.SetText('System added. Start saves your choices and begins mining.')

    def remove_system(self, *args):
        selected = self.route_list.GetSelected()
        if selected:
            del self.route_entries[selected[0].routeIndex]
            self.refresh_route()

    def move_system(self, direction):
        selected = self.route_list.GetSelected()
        if selected:
            before = selected[0].routeIndex
            after = before + direction
            if 0 <= after < len(self.route_entries):
                self.route_entries[before], self.route_entries[after] = self.route_entries[after], self.route_entries[before]
                self.refresh_route()

    def search_systems(self, *args):
        self.search_locations(True)

    def search_stations(self, *args):
        self.search_locations(False)

    def search_locations(self, systems):
        if self.busy or not session.charid:
            return
        query = (self.system_search if systems else self.station_search).GetValue().strip()
        if len(query) < 2:
            self.notice.SetText('Enter at least two letters to search.')
            return
        revision = self.revision
        self.busy = True
        try:
            remote = sm.RemoteSvc('edenAutoMining')
            rows = remote.SearchSystems(query) if systems else remote.SearchStations(query)
            if not self.is_current(revision):
                return
            if systems:
                self.system_names = dict(rows)
                options = [(name, system) for system, name in rows] or [('No mining systems found', 0)]
                self.system_results.LoadOptions(options, options[0][1])
            else:
                self.station_names.update(rows)
                selected = self.station.GetValue()
                options = [self.KEEP_HARVEST] + [(name, station) for station, name in rows]
                if selected and not any(station == selected for _, station in options):
                    options.append((self.station_names.get(selected, 'Saved station'), selected))
                self.station.LoadOptions(options, selected)
            self.notice.SetText('%d results. Choose a result%s' % (len(rows), ' and Add system.' if systems else ' to unload there.'))
        except UserError as error:
            if self.is_current(revision):
                self.show_error(error)
        finally:
            self.busy = False

    def show_error(self, error):
        values = getattr(error, 'dict', None) or {}
        self.notice.SetText(values.get('notify') or values.get('info') or unicode(error))

    def build_filter(self, body):
        self.ore_types = []
        self.active_types = set()
        self.filter_changed = False
        self.saved_filter_unavailable = False
        self.filter_summary = EveLabelMedium(parent=body, align=uiconst.TOTOP, padBottom=6)
        lists = Container(parent=body, align=uiconst.TOTOP, height=216, padBottom=4)
        available = Container(parent=lists, align=uiconst.TOLEFT_PROP, width=0.46, padRight=8)
        arrows = Container(parent=lists, align=uiconst.TOLEFT, width=38, padRight=8, padTop=76)
        active = Container(parent=lists, align=uiconst.TOALL)
        self.available_title = EveHeaderSmall(parent=available, align=uiconst.TOTOP, padBottom=4)
        self.active_title = EveHeaderSmall(parent=active, align=uiconst.TOTOP, padBottom=4)
        self.search_available = SingleLineEditText(parent=available, align=uiconst.TOTOP, hintText='Search available', padBottom=4)
        self.search_active = SingleLineEditText(parent=active, align=uiconst.TOTOP, hintText='Search active', padBottom=4)
        self.available = Scroll(parent=available, align=uiconst.TOALL, hasUnderlay=True, multiSelect=True)
        self.active = Scroll(parent=active, align=uiconst.TOALL, hasUnderlay=True, multiSelect=True)
        Button(parent=arrows, align=uiconst.TOTOP, label=u'\u203a', hint='Add selected ore types',
               func=self.add_selected, padBottom=6)
        Button(parent=arrows, align=uiconst.TOTOP, label=u'\u2039', hint='Remove selected ore types',
               func=self.remove_selected)
        self.search_available.OnChange = self.refresh_available
        self.search_active.OnChange = self.refresh_active
        self.refresh_filter()

    def family_changed(self, *args):
        self.refresh_filter_summary()

    def refresh_filter(self):
        self.refresh_filter_summary()
        self.refresh_available()
        self.refresh_active()

    def refresh_filter_summary(self):
        is_ore = self.family.GetValue() == 0
        if not is_ore:
            summary = 'Saved ore filter applies when Resource is set to Ore'
        elif self.active_types:
            summary = '%d selected' % len(self.active_types)
        elif self.saved_filter_unavailable and not self.filter_changed:
            summary = 'Saved ore is unavailable. Choose a type or clear the filter.'
        else:
            summary = 'All compatible ore'
        self.filter_summary.SetText('Ore filter: ' + summary)

    def refresh_available(self, *args):
        available = [(type_id, name) for type_id, name in self.ore_types if type_id not in self.active_types]
        self.available_title.SetText('Available ore types (%d)' % len(available))
        self.load_types(self.available, self.search_available, available, 'No additional ore types available.')

    def refresh_active(self, *args):
        active = [(type_id, name) for type_id, name in self.ore_types if type_id in self.active_types]
        self.active_title.SetText('Active filter (%d)' % len(active))
        self.load_types(self.active, self.search_active, active, '')

    def load_types(self, scroll, search, ores, empty):
        query = search.GetValue().strip().lower()
        entries = [GetFromClass(Generic, {'label': name, 'oreType': type_id})
                   for type_id, name in ores if query in name.lower()]
        scroll.Load(contentList=entries, noContentHint='No matching ore types.' if query else empty)

    def add_selected(self, *args):
        selected = self.available.GetSelected()
        if selected:
            self.active_types.update(entry.oreType for entry in selected)
            self.filter_changed = True
            self.refresh_filter()

    def remove_selected(self, *args):
        selected = self.active.GetSelected()
        if selected:
            self.active_types.difference_update(entry.oreType for entry in selected)
            self.filter_changed = True
            self.refresh_filter()

    def clear_filter(self, *args):
        self.active_types.clear()
        self.filter_changed = True
        self.refresh_filter()
        self.notice.SetText('Filter cleared. Apply settings to save.')

    def session_changed(self, change):
        if 'charid' in change and self.is_current(self.revision):
            self.revision += 1
            self.window.Close()
        elif 'solarsystemid2' in change and self.is_current(self.revision):
            self.notice.SetText('System changed. The mining plan and unload station are unchanged.')

    def OnEdenAutoMiningStatus(self, status):
        if self.is_current(self.revision):
            self.status.SetText(status)

    def apply(self, *args):
        self.save_plan()

    def save_plan(self, start_after=False):
        try:
            percent = int(self.threshold.GetValue())
            if not 1 <= percent <= 100:
                raise ValueError()
        except ValueError:
            self.notice.SetText('Enter a whole hold percentage from 1 to 100.')
            return
        ore_types = sorted(self.active_types) if self.filter_changed else None
        self.request('SavePlan', (ore_types, self.family.GetValue(), self.order.GetValue(),
                                     bool(self.approach.GetValue()), bool(self.jettison.GetValue()),
                                     self.station.GetValue(), percent,
                                     [system for system, _ in self.route_entries], self.fleet.GetValue()),
                     start_after=start_after)

    def start(self, *args):
        self.save_plan(start_after=True)

    def stop(self, *args):
        self.request('Stop')

    def reload(self, *args):
        self.request('GetStatus', load_settings=True)

    def request(self, method, args=(), load_settings=False, start_after=False):
        if not session.charid:
            self.notice.SetText('Select a character first.')
            return
        if self.busy:
            return
        revision = self.revision
        self.busy = True
        try:
            remote = sm.RemoteSvc('edenAutoMining')
            if start_after and remote.GetStatus()['running']:
                state = remote.Start()
            else:
                state = getattr(remote, method)(*args)
                if start_after:
                    state = remote.Start()
            if self.is_current(revision):
                self.status.SetText(state['status'])
                if load_settings or method == 'SavePlan':
                    self.set_settings(state)
                self.notice.SetText('' if start_after else {'GetStatus': 'Settings loaded.',
                                                          'SavePlan': 'All settings saved.'}.get(method, ''))
        except UserError as error:
            if self.is_current(revision):
                self.show_error(error)
        finally:
            self.busy = False

    def set_settings(self, state):
        self.ore_types = state['oreTypes']
        self.active_types = set(state['selectedOreTypes'])
        self.filter_changed = False
        self.saved_filter_unavailable = bool(state['oreFilter']) and not self.active_types
        self.family.LoadOptions(self.FAMILY_OPTIONS, state['resourceFamily'])
        self.order.LoadOptions(self.ORDER_OPTIONS, state['targetOrder'])
        self.approach.SetChecked(state['approach'], report=False)
        self.jettison.SetChecked(state['jettison'], report=False)
        selected = state['unloadStation']
        if selected:
            self.station_names[selected] = state.get('unloadStationName') or 'Saved station'
        options = [self.KEEP_HARVEST] + [(name, station) for station, name in self.station_names.items()]
        self.station.LoadOptions(options, selected)
        self.threshold.SetValue(str(state['unloadThreshold']))
        self.route_entries = list(state.get('routeStops', []))
        self.fleet.LoadOptions(self.FLEET_OPTIONS, state.get('fleetMode', 0))
        self.fleet_changed()
        self.refresh_route()
        self.refresh_filter()

    def is_current(self, revision):
        return revision == self.revision and self.window is not None and not self.window.destroyed
