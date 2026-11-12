"""Build 3662168: use the native scene portrait queue for every Eden account.

Installed by the signed login expression before character selection. No roles,
client archives, saved appearances, or unrelated photo methods are changed.
"""
from os.path import exists
from blue import paths
from eve.client.script.ui.services.evePhotosvc import EvePhoto


def get_creator_portrait(charID, size):
    # AddPortrait saves the submitted image at 512 and 256 pixels. Local and
    # Guests request 32 pixels, so reuse the larger image at display size.
    for cachedSize in ((256, 512) if size <= 256 else (512, 256)):
        path = 'cache:/Pictures/Characters/%s_%s.jpg' % (charID, cachedSize)
        filename = paths.ResolvePath(path)
        if filename and exists(filename):
            return path
    return None


def announce_portrait(charID, size, path):
    sm.ScatterEvent('OnPortraitCreated', charID, size)


def get_portrait(self, charID, size, sprite=None, orderIfMissing=True,
                 callback=False, allowServerTrip=True, blocking=False,
                 fullPoseSetup=False):
    size = self.FindBestPortraitSize(size)
    path = get_creator_portrait(charID, size) or self._GetCachedScenePortrait(charID, size)
    # These switches control the image-server path. The native scene fallback
    # queues local rendering even when downloading images is disabled.
    if not path:
        if blocking:
            # The pinned blocking path always restores the submitted portrait pose.
            path = self.RenderPortraitFromScene(charID, size, fullPoseSetup=True)
        else:
            if callback and not getattr(self, '_eden_portrait_notifications', False):
                self.on_scene_portrait_rendered.connect(announce_portrait)
                self._eden_portrait_notifications = True
            default = self.GetPortraitDefaultIcon(size)
            if sprite is not None and not sprite.destroyed:
                sprite.LoadTexture(default)
            self._QueueScenePortrait(charID, size, sprite, callback)
            # GetOwnerLogo interprets a non-None result as a finished portrait
            # and suppresses the completion callback for other waiting rows.
            return None if sprite is not None and callback else default
    if path:
        if sprite is not None and not sprite.destroyed:
            sprite.LoadTexture(path)
        return path
    return self.GetPortraitDefaultIcon(size)


if not getattr(EvePhoto.GetPortrait, '_eden_local_portrait', False):
    get_portrait._eden_local_portrait = True
    EvePhoto.GetPortrait = get_portrait
