"""Build 3662168: retain staged customs transfers across local simulation refreshes."""


def install_planetary_transfer_refresh():
    from eve.client.script.ui.shared.planet.importExportUI import PlanetaryImportExportUI

    owner = PlanetaryImportExportUI
    original_refresh = getattr(owner.OnRefreshPins, '_eden_original', owner.OnRefreshPins)
    original_refresh = getattr(original_refresh, 'im_func', original_refresh)
    original_confirm = getattr(owner.ConfirmCommodityTransfer, '_eden_original', owner.ConfirmCommodityTransfer)
    original_confirm = getattr(original_confirm, 'im_func', original_confirm)

    def refresh(self, pinIDs):
        # Native OnRefreshPins calls ResetContents, clearing both staged maps.
        # Local production ticks must not cancel a selection or an in-flight RPC.
        # Authoritative OnPlanetPinsChanged and inventory callbacks still refresh.
        if (getattr(self, '_eden_confirming_transfer', False)
                or getattr(self, 'importContents', None) or getattr(self, 'exportContents', None)):
            return
        return original_refresh(self, pinIDs)

    def confirm(self, *args):
        # The RPC yields to native UI callbacks. A second click must not submit
        # the same staged batch again while its first confirmation is pending.
        if getattr(self, '_eden_confirming_transfer', False):
            return
        self._eden_confirming_transfer = True
        try:
            return original_confirm(self, *args)
        finally:
            self._eden_confirming_transfer = False

    refresh._eden_original = original_refresh
    confirm._eden_original = original_confirm
    owner.OnRefreshPins = refresh
    owner.ConfirmCommodityTransfer = confirm


install_planetary_transfer_refresh()
