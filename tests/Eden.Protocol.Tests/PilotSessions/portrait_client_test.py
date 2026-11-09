"""Exercise the exact signed portrait expression with a stub photo service in Python 2.

No game module bodies, renderer, service, or session are run.
"""
import ctypes
import hashlib
import os
from pathlib import Path
import sys

expression = Path(sys.argv[1]).read_bytes()
binaries = Path(sys.argv[2]) / 'tq/bin64'
runtime_path = binaries / 'python27.dll'
assert hashlib.sha256(runtime_path.read_bytes()).hexdigest() == (
    '1266e119d5db70281831f06049faa2694e658a68d504e0cc5680ec5726ba3aa2')

worker = r'''
import marshal, sys
names = ['eve', 'eve.client', 'eve.client.script', 'eve.client.script.ui',
         'eve.client.script.ui.services', 'eve.client.script.ui.services.evePhotosvc']
for name in names:
    module = type(sys)(name)
    module.__path__ = []
    sys.modules[name] = module
    parent, _, child = name.rpartition('.')
    if parent:
        setattr(sys.modules[parent], child, module)

class Photo(object):
    def __init__(self):
        self.cached = None
        self.rendered = None
        self.calls = []
    def FindBestPortraitSize(self, size):
        return next(s for s in [32, 64, 128, 256, 512, 1024] if s >= size)
    def _GetCachedScenePortrait(self, char_id, size):
        return self.cached
    def _QueueScenePortrait(self, *args):
        self.calls.append(('queue', args))
    def RenderPortraitFromScene(self, char_id, size, fullPoseSetup=False):
        self.calls.append(('render', char_id, size, fullPoseSetup))
        return self.rendered
    def GetPortraitDefaultIcon(self, size):
        return 'silhouette'

class Sprite(object):
    destroyed = False
    def __init__(self):
        self.paths = []
    def LoadTexture(self, path):
        self.paths.append(path)

sys.modules[names[-1]].EvePhoto = Photo
expression = marshal.loads(EXPRESSION)
assert eval(expression, {}) == {}
for char_id in [90000001, 90000002, 3000001]:
    photo, sprite = Photo(), Sprite()
    assert photo.GetPortrait(char_id, 33, sprite, callback=True) == 'silhouette'
    assert photo.calls == [('queue', (char_id, 64, sprite, True))]
    photo.cached = 'cache:/rendered.jpg'
    assert photo.GetPortrait(char_id, 33, sprite) == photo.cached
    assert len(photo.calls) == 1 and sprite.paths == [photo.cached]
    sprite.destroyed = True
    assert photo.GetPortrait(char_id, 33, sprite) == photo.cached
    assert sprite.paths == [photo.cached]
for options in [{'orderIfMissing': False}, {'allowServerTrip': False}]:
    photo = Photo()
    assert photo.GetPortrait(90000001, 32, **options) == 'silhouette'
    assert photo.calls == [('queue', (90000001, 32, None, False))]
photo, sprite = Photo(), Sprite()
photo.rendered = 'cache:/blocking.jpg'
assert photo.GetPortrait(90000001, 512, sprite, blocking=True, fullPoseSetup=True) == photo.rendered
assert photo.calls == [('render', 90000001, 512, True)]
assert sprite.paths == [photo.rendered]
photo = Photo()
assert photo.GetPortrait(90000001, 128, blocking=True) == 'silhouette'
assert photo.calls == [('render', 90000001, 128, True)]
assert eval(expression, {}) == {}
assert Photo().GetPortrait(90000001, 32) == 'silhouette'
'''.replace('EXPRESSION', repr(expression))

with os.add_dll_directory(str(binaries)):
    runtime = ctypes.CDLL(str(runtime_path))
    ctypes.c_int.in_dll(runtime, 'Py_NoSiteFlag').value = 1
    runtime.Py_InitializeEx(0)
    if runtime.PyRun_SimpleString(worker.encode('ascii')):
        raise SystemExit('Portrait adapter assertions failed')
print('Portrait adapter assertions passed in the pinned Python 2 runtime')
