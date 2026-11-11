"""Build 3662168: serialize yielding presence-list rebuilds and live deltas.

Native name priming and BeNice can yield between capturing members and adding
rows. A concurrent delta must run after that rebuild, or its row is lost while
the independent member/count cache retains it.
"""


def install_presence_lists():
    import locks
    from xmppchatclient.xmppchatwindow import XmppChatWindow
    from eve.client.script.ui.shared.dockedUI.guests import GuestsPanel

    def protect(cls, name, reset_index=False):
        original = getattr(cls, name)
        if getattr(original, '_eden_presence_serialized', False):
            return

        def serialized(self, *args, **kwargs):
            # One lock per view, shared by full rebuilds and incremental deltas.
            # Keep other windows and the authoritative guest controller independent.
            with locks.TempLock(('eden-presence-list', id(self))):
                if reset_index:
                    # Native InitUsers clears the scroll but retains this index.
                    self.userlistNodesByName.clear()
                return original(self, *args, **kwargs)

        serialized._eden_presence_serialized = True
        setattr(cls, name, serialized)

    protect(XmppChatWindow, 'InitUsers', reset_index=True)
    protect(XmppChatWindow, 'CharacterJoinedChannel')
    protect(XmppChatWindow, 'CharacterLeftChannel')
    protect(GuestsPanel, '_update_scroll_content')
    protect(GuestsPanel, '_add_guest_to_scroll')
    protect(GuestsPanel, '_remove_guest_from_scroll')


install_presence_lists()
