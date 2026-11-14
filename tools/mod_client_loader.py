"""Pinned-build client API. MODS is supplied by inert archive composition."""
import json
import logging

_manager = None


class ModApi(object):
    def __init__(self, manager, identity):
        self.manager = manager
        self.identity = identity
        self.session_callbacks = []

    def menu(self, label, callback):
        self.manager.entries.append((self.identity, label, callback))

    def command(self, name, arguments=''):
        reply = sm.RemoteSvc('edenMods').Execute(self.identity, name, arguments)
        if not reply['success']:
            raise RuntimeError(reply.get('error') or 'Mod command failed')
        return json.loads(reply['json'])

    def window(self, title, body):
        from carbonui.control.window import Window
        from eve.client.script.ui.control.eveLabel import EveLabelMedium
        import carbonui.const as uiconst
        window = Window.Open(windowID='eden_mod_' + self.identity, caption=title)
        window.SetCaption(title)
        window.content.Flush()
        text = body if isinstance(body, basestring) else json.dumps(body, ensure_ascii=True, indent=2)
        text = text.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;').replace('\n', '<br>')
        EveLabelMedium(parent=window.content, text=text, align=uiconst.TOTOP, padding=12)
        return window

    def on_session_changed(self, callback):
        self.session_callbacks.append(callback)

    def log(self, message):
        import logging
        logging.getLogger('eden.mods.' + self.identity).info(message)


class Manager(object):
    __notifyevents__ = ['OnSessionChanged']

    def __init__(self):
        self.entries = []
        self.apis = []
        sm.RegisterNotify(self)
        for mod in MODS:
            api = ModApi(self, mod['id'])
            entry_count = len(self.entries)
            try:
                module = __import__(mod['module'], fromlist=['register'])
                module.register(api)
            except Exception:
                del self.entries[entry_count:]
                logging.getLogger('eden.mods').exception('Could not load mod %s', mod['id'])
            else:
                self.apis.append(api)

    def OnSessionChanged(self, is_remote, session, change):
        for api in self.apis:
            for callback in api.session_callbacks:
                callback(change)

    def show(self, *args):
        from carbonui.control.window import Window
        from carbonui.control.button import Button
        import carbonui.const as uiconst
        window = Window.Open(windowID='eden_mods', caption='Eden Mods')
        window.SetCaption('Eden Mods')
        window.content.Flush()
        for identity, label, callback in self.entries:
            Button(parent=window.content, label=label, func=callback, align=uiconst.TOTOP, padBottom=8)


def show_mods(*args):
    global _manager
    if _manager is None:
        _manager = Manager()
    _manager.show()


def menu(original):
    from carbonui.control.contextMenu.menuEntryData import MenuEntryData
    entries = list(original or [])
    entries.append(MenuEntryData('Eden Mods', func=show_mods))
    return entries
