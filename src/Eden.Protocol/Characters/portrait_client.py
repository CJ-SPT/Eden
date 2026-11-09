"""Build 3662168: use the native scene portrait queue for every Eden account.

Installed by the signed login expression before character selection. No roles,
client archives, saved appearances, or unrelated photo methods are changed.
"""
from eve.client.script.ui.services.evePhotosvc import EvePhoto


def get_portrait(self, charID, size, sprite=None, orderIfMissing=True,
                 callback=False, allowServerTrip=True, blocking=False,
                 fullPoseSetup=False):
    size = self.FindBestPortraitSize(size)
    path = self._GetCachedScenePortrait(charID, size)
    # These switches control the image-server path. The native scene fallback
    # queues local rendering even when downloading images is disabled.
    if not path:
        if blocking:
            # The pinned blocking path always restores the submitted portrait pose.
            path = self.RenderPortraitFromScene(charID, size, fullPoseSetup=True)
        else:
            self._QueueScenePortrait(charID, size, sprite, callback)
    if path:
        if sprite is not None and not sprite.destroyed:
            sprite.LoadTexture(path)
        return path
    return self.GetPortraitDefaultIcon(size)


EvePhoto.GetPortrait = get_portrait
