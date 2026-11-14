"""AutoMining controls for the build-3662168 Eden Mods menu."""
import carbonui.const as uiconst
from carbonui.control.button import Button
from carbonui.control.combo import Combo
from carbonui.control.singlelineedits.singleLineEditText import SingleLineEditText
from carbonui.control.window import Window
from eve.client.script.ui.control.eveLabel import EveLabelMedium


def register(api):
    panel = MiningPanel()
    api.menu('AutoMining', panel.open)
    api.on_session_changed(panel.session_changed)
    sm.RegisterNotify(panel)


class MiningPanel(object):
    __notifyevents__ = ['OnEdenAutoMiningStatus']

    def __init__(self):
        self.window = None
        self.revision = 0
        self.busy = False

    def open(self, *args):
        if self.busy:
            return
        self.revision += 1
        self.window = Window.Open(windowID='eden_automining', caption='AutoMining')
        self.window.SetMinSize((360, 350), refresh=True)
        self.window.content.Flush()
        parent = self.window.content
        self.status = EveLabelMedium(parent=parent, align=uiconst.TOTOP, padBottom=8)
        EveLabelMedium(parent=parent, text='Ore groups, separated by commas. Blank allows any ore.',
                       align=uiconst.TOTOP, padBottom=4)
        self.ore_filter = SingleLineEditText(parent=parent, align=uiconst.TOTOP, maxLength=512, padBottom=8)
        EveLabelMedium(parent=parent, text='Unload station in this system', align=uiconst.TOTOP, padBottom=4)
        self.station = Combo(parent=parent, align=uiconst.TOTOP, options=[('Keep ore on board', 0)], padBottom=8)
        EveLabelMedium(parent=parent, text='Unload at hold percentage (1-100)', align=uiconst.TOTOP, padBottom=4)
        self.threshold = SingleLineEditText(parent=parent, align=uiconst.TOTOP, maxLength=3, padBottom=8)
        self.stations = []
        for label, callback in [('Save Filter', self.save_filter), ('Save Hauling', self.save_hauling), ('Start', self.start),
                                ('Stop', self.stop), ('Refresh Status', self.refresh)]:
            Button(parent=parent, label=label, func=callback, align=uiconst.TOTOP, padBottom=4)
        self.request('GetStatus', load_settings=True)

    def session_changed(self, change):
        if ('charid' in change or 'solarsystemid2' in change) and self.window is not None and not self.window.destroyed:
            self.revision += 1
            self.window.Close()

    def OnEdenAutoMiningStatus(self, status):
        if self.is_current(self.revision):
            self.status.SetText(status)

    def save_filter(self, *args):
        self.request('SaveFilter', (self.ore_filter.GetValue(),), load_filter=True)

    def save_hauling(self, *args):
        try:
            percent = int(self.threshold.GetValue())
        except ValueError:
            self.status.SetText('Enter a whole hold percentage from 1 to 100.')
            return
        self.request('SaveHauling', (self.station.GetValue(), percent), load_hauling=True)

    def start(self, *args):
        self.request('Start')

    def stop(self, *args):
        self.request('Stop')

    def refresh(self, *args):
        self.request('GetStatus')

    def request(self, method, args=(), load_filter=False, load_hauling=False, load_settings=False):
        if not session.charid:
            self.status.SetText('Select a character first.')
            return
        if self.busy:
            return
        revision = self.revision
        self.busy = True
        try:
            remote = sm.RemoteSvc('edenAutoMining')
            result = getattr(remote, method)(*args)
            if load_settings and 'unloadStation' in result:
                stations = remote.GetStations()
                if self.is_current(revision):
                    self.stations = [(name, station) for station, name in stations]
            if self.is_current(revision):
                self.status.SetText(result['status'])
                if load_filter or load_settings:
                    self.ore_filter.SetValue(result['oreFilter'])
                if (load_hauling or load_settings) and 'unloadStation' in result:
                    self.set_hauling(result)
        except UserError as error:
            if self.is_current(revision):
                info = getattr(error, 'dict', None) or {}
                self.status.SetText(info.get('info', 'AutoMining request failed.'))
        finally:
            self.busy = False

    def set_hauling(self, state):
        selected = state['unloadStation']
        options = [('Keep ore on board', 0)] + list(self.stations)
        if selected and not any(station == selected for _, station in options):
            options.append(('Saved station is outside this system', selected))
        self.station.LoadOptions(options, selected)
        self.threshold.SetValue(str(state['unloadThreshold']))

    def is_current(self, revision):
        return revision == self.revision and self.window is not None and not self.window.destroyed
