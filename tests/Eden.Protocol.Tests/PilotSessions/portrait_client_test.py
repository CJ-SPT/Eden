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
import __builtin__, marshal, sys
names = ['os', 'os.path', 'blue', 'eve', 'eve.client', 'eve.client.script', 'eve.client.script.ui',
         'eve.client.script.ui.services', 'eve.client.script.ui.services.evePhotosvc']
for name in names:
    module = type(sys)(name)
    module.__path__ = []
    sys.modules[name] = module
    parent, _, child = name.rpartition('.')
    if parent:
        setattr(sys.modules[parent], child, module)

files = set()
sys.modules['os.path'].exists = files.__contains__
sys.modules['blue'].paths = type('Paths', (), {'ResolvePath': staticmethod(lambda path: path)})

class Signal(object):
    def __init__(self):
        self.listeners = []
    def connect(self, listener):
        self.listeners.append(listener)
    def emit(self, *args):
        for listener in self.listeners:
            listener(*args)

events = []
__builtin__.sm = type('Services', (), {'ScatterEvent': staticmethod(lambda *args: events.append(args))})

class Photo(object):
    def __init__(self):
        self.cached = None
        self.rendered = None
        self.calls = []
        self.on_scene_portrait_rendered = Signal()
    def GetPortrait(self, *args, **kwargs):
        raise AssertionError('Native image-server lookup should not run')
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
    second_sprite = Sprite()
    assert photo.GetPortrait(char_id, 33, sprite, callback=True) is None
    assert photo.calls == [('queue', (char_id, 64, sprite, True))]
    assert photo.GetPortrait(char_id, 33, second_sprite, callback=True) is None
    assert len(photo.on_scene_portrait_rendered.listeners) == 1
    assert sprite.paths == second_sprite.paths == ['silhouette']
    photo.cached = 'cache:/rendered.jpg'
    photo.on_scene_portrait_rendered.emit(char_id, 64, photo.cached)
    assert events[-1] == ('OnPortraitCreated', char_id, 64)
    assert photo.GetPortrait(char_id, 33, sprite) == photo.cached
    assert len(photo.calls) == 2 and sprite.paths == ['silhouette', photo.cached]
    sprite.destroyed = True
    assert photo.GetPortrait(char_id, 33, sprite) == photo.cached
    assert sprite.paths == ['silhouette', photo.cached]
photo, sprite = Photo(), Sprite()
creator_path = 'cache:/Pictures/Characters/90000001_512.jpg'
files.add(creator_path)
assert photo.GetPortrait(90000001, 32, sprite, callback=True) == creator_path
assert photo.GetPortrait(90000001, 512, sprite, blocking=True) == creator_path
assert not photo.calls and sprite.paths == [creator_path, creator_path]
files.clear()
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
installed = Photo.GetPortrait.im_func
assert eval(expression, {}) == {}
assert Photo.GetPortrait.im_func is installed
assert Photo().GetPortrait(90000001, 32) == 'silhouette'
'''.replace('EXPRESSION', repr(expression))

with os.add_dll_directory(str(binaries)):
    runtime = ctypes.CDLL(str(runtime_path))
    ctypes.c_int.in_dll(runtime, 'Py_NoSiteFlag').value = 1
    runtime.Py_InitializeEx(0)
    if runtime.PyRun_SimpleString(worker.encode('ascii')):
        raise SystemExit('Portrait adapter assertions failed')
print('Portrait adapter assertions passed in the pinned Python 2 runtime')
