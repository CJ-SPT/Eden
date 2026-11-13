"""AutoMining controls for the build-3662168 Eden Mods menu."""
import carbonui.const as uiconst
from carbonui.control.button import Button
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
        self.window.content.Flush()
        parent = self.window.content
        self.status = EveLabelMedium(parent=parent, align=uiconst.TOTOP, padBottom=8)
        EveLabelMedium(parent=parent, text='Ore groups, separated by commas. Blank allows any ore.',
                       align=uiconst.TOTOP, padBottom=4)
        self.ore_filter = SingleLineEditText(parent=parent, align=uiconst.TOTOP, maxLength=512, padBottom=8)
        for label, callback in [('Save Filter', self.save_filter), ('Start', self.start),
                                ('Stop', self.stop), ('Refresh Status', self.refresh)]:
            Button(parent=parent, label=label, func=callback, align=uiconst.TOTOP, padBottom=4)
        self.request('GetStatus', load_filter=True)

    def session_changed(self, change):
        if 'charid' in change and self.window is not None and not self.window.destroyed:
            self.revision += 1
            self.window.Close()

    def OnEdenAutoMiningStatus(self, status):
        if self.is_current(self.revision):
            self.status.SetText(status)

    def save_filter(self, *args):
        self.request('SaveFilter', (self.ore_filter.GetValue(),), load_filter=True)

    def start(self, *args):
        self.request('Start')

    def stop(self, *args):
        self.request('Stop')

    def refresh(self, *args):
        self.request('GetStatus')

    def request(self, method, args=(), load_filter=False):
        if not session.charid:
            self.status.SetText('Select a character first.')
            return
        if self.busy:
            return
        revision = self.revision
        self.busy = True
        try:
            result = getattr(sm.RemoteSvc('edenAutoMining'), method)(*args)
            if self.is_current(revision):
                self.status.SetText(result['status'])
                if load_filter:
                    self.ore_filter.SetValue(result['oreFilter'])
        except UserError as error:
            if self.is_current(revision):
                info = getattr(error, 'dict', None) or {}
                self.status.SetText(info.get('info', 'AutoMining request failed.'))
        finally:
            self.busy = False

    def is_current(self, revision):
        return revision == self.revision and self.window is not None and not self.window.destroyed
