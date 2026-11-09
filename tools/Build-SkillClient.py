"""Build an Eden skill archive using only inert compilation in the pinned Python runtime.

No game module bodies run. Original client files are read only; output must be elsewhere.
"""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import tempfile
import zipfile
import zlib


PINNED_SHA256 = '37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368'
PYTHON_SHA256 = '1266e119d5db70281831f06049faa2694e658a68d504e0cc5680ec5726ba3aa2'
WRAPPERS = {
    'fsdBuiltData/common/base.pyj': ('GetData', '''
def GetData(cls):
    import eden_skill_overlay
    def _eden_original(cls):
        pass
    return eden_skill_overlay.apply(cls.__resBuiltFile__, _eden_original(cls))
'''),
    'localization/localizationBase.pyj': ('GetByMessageID', '''
def GetByMessageID(self, messageID, languageID=None, **kwargs):
    import eden_skill_overlay
    text = eden_skill_overlay.message(messageID)
    if text is not None:
        return text
    def _eden_original(self, messageID, languageID=None, **kwargs):
        pass
    return _eden_original(self, messageID, languageID, **kwargs)
'''),
    'clonegrade/storage.pyj': ('CloneGradeStorage', '''
def CloneGradeStorage():
    import eden_skill_overlay
    def _eden_original():
        pass
    return eden_skill_overlay.alpha_storage(_eden_original())
'''),
}


NEOCOM_MODULE = 'eve/client/script/ui/shared/neocom/neocom/neocomSvc.pyj'
MOD_WRAPPERS = [
    ('Run', """
def Run(self):
    def _eden_original(self):
        pass
    result = _eden_original(self)
    import eden_mod_loader
    eden_mod_loader.start(self)
    return result
"""),
    ('GetMenu', """
def GetMenu(self):
    def _eden_original(self):
        pass
    import eden_mod_loader
    return eden_mod_loader.menu(_eden_original(self))
"""),
]


def mod_sources(root):
    modules = {}
    entries = []
    if root is not None and root.exists():
        order_path = root / '.order.json'
        order = json.loads(order_path.read_text(encoding='utf-8')) if order_path.exists() else []
        positions = dict((name, index) for index, name in enumerate(order))
        for folder in sorted((path for path in root.iterdir() if path.is_dir()),
                             key=lambda path: (positions.get(path.name, len(order)), path.name)):
            manifest = json.loads((folder / 'manifest.json').read_text(encoding='utf-8'))
            if manifest.get('clientEntry') is None:
                continue
            namespace = 'eden_mod_' + hashlib.sha256(manifest['id'].encode('utf-8')).hexdigest()[:16]
            modules[namespace + '/__init__.pyj'] = ''
            for path in sorted((folder / 'client').rglob('*.py')):
                relative = path.relative_to(folder / 'client').as_posix()
                parts = relative.split('/')
                for count in range(1, len(parts)):
                    modules.setdefault(namespace + '/' + '/'.join(parts[:count]) + '/__init__.pyj', '')
                modules[namespace + '/' + relative[:-3] + '.pyj'] = path.read_text(encoding='utf-8')
            entry = manifest['clientEntry'][len('client/'):-3].replace('/', '.')
            entries.append({'id': manifest['id'], 'module': namespace + '.' + entry})
    if entries:
        source = 'MODS = ' + repr(entries) + '\n'
        source += Path(__file__).with_name('mod_client_loader.py').read_text(encoding='utf-8')
        modules['eden_mod_loader.pyj'] = source
    return modules


def build(root, overlay, output, mods=None):
    root, overlay, output = root.resolve(), overlay.resolve(), output.resolve()
    if output.is_relative_to(root):
        raise ValueError('Skill package output must be outside the source client')
    archive = root / 'tq/code.ccp'
    if hashlib.sha256(archive.read_bytes()).hexdigest() != PINNED_SHA256:
        archive = root / '.eden-original-code.ccp'
    if hashlib.sha256(archive.read_bytes()).hexdigest() != PINNED_SHA256:
        raise ValueError('Select an unchanged build-3662168 source client')
    binaries = root / 'tq/bin64'
    if hashlib.sha256((binaries / 'python27.dll').read_bytes()).hexdigest() != PYTHON_SHA256:
        raise ValueError('The source Python runtime does not match build 3662168')
    source = 'DATA_JSON = ' + repr(overlay.read_text(encoding='utf-8')) + '\n'
    source += Path(__file__).with_name('skill_client_overlay.py').read_text(encoding='utf-8')
    extra_sources = mod_sources(mods)
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='eden-skill-compile-', dir=output.parent) as folder:
        work = Path(folder)
        jobs = []
        with zipfile.ZipFile(archive) as original:
            for name, (function, wrapper) in WRAPPERS.items():
                path = work / (name.replace('/', '_'))
                path.write_bytes(zlib.decompress(original.read(name)))
                jobs.append((str(path), function, wrapper))
            if extra_sources:
                path = work / NEOCOM_MODULE.replace('/', '_')
                path.write_bytes(zlib.decompress(original.read(NEOCOM_MODULE)))
                for function, wrapper in MOD_WRAPPERS:
                    jobs.append((str(path), function, wrapper))
        worker = '''import marshal
def replace(code, constants):
    return type(code)(code.co_argcount,code.co_nlocals,code.co_stacksize,code.co_flags,
        code.co_code,tuple(constants),code.co_names,code.co_varnames,code.co_filename,
        code.co_name,code.co_firstlineno,code.co_lnotab,code.co_freevars,code.co_cellvars)
def patch(code, name, wrapper):
    if code.co_name == name:
        return replace(wrapper, [code if getattr(v,'co_name',None)=='_eden_original' else v
            for v in wrapper.co_consts]), 1
    count = 0
    constants = []
    for v in code.co_consts:
        if hasattr(v,'co_code'):
            v, found = patch(v, name, wrapper)
            count += found
        constants.append(v)
    return replace(code,constants), count
for path, name, source in JOBS:
    f=open(path,'rb');header=f.read(8);code=marshal.load(f);f.close()
    wrapper=next(c for c in compile(source,'eden-skill-adapter','exec').co_consts
        if getattr(c,'co_name',None)==name)
    code,count=patch(code,name,wrapper)
    assert count==1,(name,count)
    open(path,'wb').write(header+marshal.dumps(code,0))
module=compile(MODULE_SOURCE,'eden_skill_overlay.py','exec')
open(MODULE_OUTPUT,'wb').write(header+marshal.dumps(module,0))
for path, name, source in EXTRA_COMPILATIONS:
    module=compile(source,name,'exec')
    open(path,'wb').write(header+marshal.dumps(module,0))
'''
        worker = worker.replace('JOBS', repr(jobs)).replace('MODULE_SOURCE', repr(source))
        worker = worker.replace('MODULE_OUTPUT', repr(str(work / 'eden_skill_overlay.pyj')))
        extras = [(str(work / ('extra-' + str(index))), name,
                   ('# coding: utf-8\n' + module).encode('utf-8'))
                  for index, (name, module) in enumerate(sorted(extra_sources.items()))]
        worker = worker.replace('EXTRA_COMPILATIONS', repr(extras))
        with os.add_dll_directory(str(binaries)):
            runtime = ctypes.CDLL(str(binaries / 'python27.dll'))
            ctypes.c_int.in_dll(runtime, 'Py_NoSiteFlag').value = 1
            runtime.Py_InitializeEx(0)
            if runtime.PyRun_SimpleString(worker.encode('ascii', 'backslashreplace')):
                raise RuntimeError('Inert skill compilation failed')
        with zipfile.ZipFile(archive) as original, zipfile.ZipFile(output, 'w', zipfile.ZIP_STORED) as target:
            for name in dict.fromkeys(original.namelist()):
                if name == 'eden_skill_overlay.pyj':
                    raise ValueError('The original archive already contains an Eden overlay')
                path = work / name.replace('/', '_')
                data = zlib.compress(path.read_bytes()) if name in WRAPPERS or (extra_sources and name == NEOCOM_MODULE) else original.read(name)
                target.writestr(zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0)), data)
            for path, name, module in extras:
                if name in original.namelist():
                    raise ValueError('A mod module overlaps the original archive')
                target.writestr(zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0)),
                                zlib.compress(Path(path).read_bytes()))
            target.writestr(zipfile.ZipInfo('eden_skill_overlay.pyj', (1980, 1, 1, 0, 0, 0)),
                            zlib.compress((work / 'eden_skill_overlay.pyj').read_bytes()))
    print('Skill client archive compiled successfully')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client', type=Path, required=True)
    parser.add_argument('--overlay', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--mods', type=Path)
    args = parser.parse_args()
    build(args.client, args.overlay, args.output, args.mods)
