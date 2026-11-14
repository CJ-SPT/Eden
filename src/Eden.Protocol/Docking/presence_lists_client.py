"""Build 3662168: serialize yielding presence-list rebuilds and live deltas.

Native name priming and BeNice can yield between capturing members and adding
rows. A concurrent delta must run after that rebuild, or its row is lost while
the independent member/count cache retains it.
"""


def install_presence_lists():
    import json
    import locks
    from carbon.common.script.sys.serviceConst import ROLE_CHTINVISIBLE
    from xmppchatclient.xmppchatsvc import XmppChatService
    from xmppchatclient.xmppchatwindow import XmppChatWindow
    from eve.client.script.ui.shared.dockedUI.guests import GuestsPanel
    from eve.client.script.ui.station.base import StationSvc

    def protect(cls, name, before=None):
        original = getattr(cls, name)
        if getattr(original, '_eden_presence_serialized', False):
            return

        def serialized(self, *args, **kwargs):
            # One lock per view, shared by full rebuilds and incremental deltas.
            # Keep other windows and the authoritative guest controller independent.
            with locks.TempLock(('eden-presence-list', id(self))):
                if before is not None:
                    before(self, *args, **kwargs)
                return original(self, *args, **kwargs)

        serialized._eden_presence_serialized = True
        setattr(cls, name, serialized)

    def reset_chat_index(window):
        # Native InitUsers clears the scroll but retains this index.
        window.userlistNodesByName.clear()

    protect(XmppChatWindow, 'InitUsers', reset_chat_index)
    protect(XmppChatWindow, 'SetChannel')
    protect(XmppChatWindow, 'CharacterJoinedChannel')
    protect(XmppChatWindow, 'CharacterLeftChannel')
    protect(GuestsPanel, '_update_scroll_content')

    if not getattr(GuestsPanel._get_filtered_guests, '_eden_presence_serialized', False):
        filter_guests = GuestsPanel._get_filtered_guests
        add_guest = GuestsPanel._add_guest_to_scroll
        remove_guest = GuestsPanel._remove_guest_from_scroll

        def known_guests(self, guests):
            owners = cfg.eveowners
            missing = [guest.character_id for guest in guests if owners.GetIfExists(guest.character_id) is None]
            if missing:
                # Recordset remembers a miss and skips that ID on future
                # Prime calls. Retry from the connected guest snapshot.
                _, rows = sm.RemoteSvc('config').GetMultiOwnersEx(missing)
                for row in rows:
                    owners.Hint(row[0], row)
                guests = [guest for guest in guests if owners.GetIfExists(guest.character_id) is not None]
            return filter_guests(self, guests)

        def sync_guest(self, guest):
            with locks.TempLock(('eden-presence-list', id(self))):
                while self._scroll is not None and not self._scroll.destroyed:
                    # GuestList mutates before emitting a signal. A callback
                    # waiting behind a rebuild can already have been superseded
                    # by a departure, rejoin or replacement station controller.
                    guest_list = self._controller.guest_list
                    current = None if guest_list is None else guest_list.guests_by_character_id.get(guest.character_id)
                    visible = current is not None and known_guests(self, [current])
                    if guest_list is not self._controller.guest_list:
                        continue
                    if guest_list is not None and current is not guest_list.guests_by_character_id.get(guest.character_id):
                        continue
                    if not visible:
                        return remove_guest(self, guest)
                    if any(node.charID == guest.character_id for node in self._scroll.GetNodes()):
                        return
                    return add_guest(self, current)

        known_guests._eden_presence_serialized = True
        GuestsPanel._get_filtered_guests = known_guests
        GuestsPanel._add_guest_to_scroll = sync_guest
        GuestsPanel._remove_guest_from_scroll = sync_guest

    if not getattr(XmppChatService._UnpackVikingPresence, '_eden_presence_serialized', False):
        unpack = XmppChatService._UnpackVikingPresence
        handle_admin = XmppChatService._HandleMessageFromAdmin
        add_character = XmppChatService._AddCharacterToChannelWindow

        def channel_character(self, window, character_id):
            channel = window.receiver
            with locks.TempLock(('eden-presence-list', id(window))):
                if window.receiver == channel and not window.destroyed:
                    return add_character(self, window, character_id)

        def viking(self, channel, packet):
            with locks.TempLock(('eden-chat-presence', id(self), channel)):
                window = self.GetOrCreateGroupChatWindow(channel)
                if window is None:
                    return
                # Owner priming can yield before CharacterJoinedChannel takes
                # the view lock. Keep the whole roster on its original channel.
                with locks.TempLock(('eden-presence-list', id(window))):
                    if window.receiver != channel or window.destroyed:
                        return
                    result = unpack(self, channel, packet)
                    visible = {char for char, data in window.members.iteritems()
                               if not data.get('role', 0) & ROLE_CHTINVISIBLE}
                    rows = {node.charID for node in window.userlist.GetNodes()}
                    if visible != rows:
                        window.InitUsers()
                    return result

        def admin(self, text, window):
            channel = window.receiver
            with locks.TempLock(('eden-chat-presence', id(self), channel)):
                with locks.TempLock(('eden-presence-list', id(window))):
                    if window.receiver != channel or window.destroyed:
                        return
                    result = handle_admin(self, text, window)
                    command = json.loads(text)
                    if command.get('cmd') == 'leave':
                        roles = self.memberRolesByChannels.get(channel)
                        if roles is not None:
                            roles.pop(command['charid'], None)
                    return result

        viking._eden_presence_serialized = True
        XmppChatService._UnpackVikingPresence = viking
        XmppChatService._HandleMessageFromAdmin = admin
        XmppChatService._AddCharacterToChannelWindow = channel_character

    if not getattr(StationSvc.GetGuests, '_eden_presence_serialized', False):
        get_guests = StationSvc.GetGuests
        arrive = StationSvc.OnCharNowInStation
        depart = StationSvc.OnCharNoLongerInStation

        def station_guests(self):
            with locks.TempLock(('eden-station-guests', id(self))):
                location = session.stationid
                changes = []
                self._eden_guest_changes = changes
                try:
                    guests = get_guests(self)
                finally:
                    self._eden_guest_changes = None
                if session.stationid != location:
                    return {}
                # A departure delivered during the RPC can precede a stale
                # snapshot row. Reapply deltas in their original order.
                for update, record in changes:
                    update(self, record)
                return guests

        def track(update):
            def changed(self, record):
                changes = getattr(self, '_eden_guest_changes', None)
                if changes is not None:
                    changes.append((update, record))
                return update(self, record)
            return changed

        station_guests._eden_presence_serialized = True
        StationSvc.GetGuests = station_guests
        StationSvc.OnCharNowInStation = track(arrive)
        StationSvc.OnCharNoLongerInStation = track(depart)


install_presence_lists()
