"""Headless checks of Eden's authored adapter in the pinned client interpreter.

Set EDEN_TEST_CLIENT_ROOT to an unchanged prepared build-3662168 client.
Only data loaders and inert functions run; the game is never started.
"""
import ctypes
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
import zipfile
import zlib


REPOSITORY = Path(__file__).resolve().parents[3]
CLIENT = os.environ.get('EDEN_TEST_CLIENT_ROOT')


@unittest.skipUnless(CLIENT, 'Set EDEN_TEST_CLIENT_ROOT for pinned native data checks')
class SkillClientTests(unittest.TestCase):
    def test_signed_package_adapter_preserves_native_data_and_adds_skills(self):
        root = Path(CLIENT).resolve()
        spec = importlib.util.spec_from_file_location('skill_builder', REPOSITORY / 'tools/Build-SkillClient.py')
        builder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(builder)
        with tempfile.TemporaryDirectory(prefix='eden-skill-native-') as directory:
            work = Path(directory)
            overlay = {
                'datasets': {
                    'types.fsdbinary': {'1500000000': {
                        'typeID': 1500000000, 'groupID': 257, 'published': 1,
                        'typeNameID': 1000000000, 'descriptionID': 1000000001}},
                    'typedogma.fsdbinary': {'1500000000': {
                        'dogmaAttributes': [{'attributeID': 275, 'value': 3.0}], 'dogmaEffects': []}},
                    'requiredskillsfortypes.fsdbinary': {'1500000000': {'3327': 2}},
                    'requiredskillsindex.fsdbinary': {'3327': {'2': {'150': {'1': [1500000000]}}}},
                },
                'messages': {'1000000000': 'Eden café skill', '1000000001': 'A custom description'},
                'alpha': {'1': {'1500000000': 3}, '2': {'1500000000': 0}},
            }
            document = work / 'overlay.json'
            document.write_text(json.dumps(overlay), encoding='utf-8')
            output = work / 'code.ccp'
            original_hash = hashlib.sha256((root / 'tq/code.ccp').read_bytes()).hexdigest()
            builder.build(root, document, output)
            self.assertEqual(original_hash, hashlib.sha256((root / 'tq/code.ccp').read_bytes()).hexdigest())
            library = work / 'library.zip'
            modules = {}
            with zipfile.ZipFile(output) as archive, zipfile.ZipFile(library, 'w') as libraries:
                for name in dict.fromkeys(archive.namelist()):
                    if name.endswith('.pyj'):
                        raw = zlib.decompress(archive.read(name))
                        libraries.writestr(name[:-1] + 'c', raw)
                        if name in builder.WRAPPERS or name in ('evetypes/__init__.pyj', 'eve/client/script/ui/services/skillsvc.pyj'):
                            path = work / name.replace('/', '_')
                            path.write_bytes(raw)
                            modules[name] = str(path)
            resources = {line.split(',')[0]: str(root / 'ResFiles' / line.split(',')[1])
                         for line in (root / 'tq/resfileindex.txt').read_text().splitlines()}
            worker = r'''
import sys,marshal
sys.path[:]=[LIBRARY,BINARIES]
import eden_skill_overlay,typesLoader,groupsLoader,typeDogmaLoader
def find(path,name):
    f=open(path,'rb');f.read(8);root=marshal.load(f);f.close()
    def walk(c):
        yield c
        for v in c.co_consts:
            if hasattr(v,'co_code'):
                for nested in walk(v):yield nested
    return next(c for c in walk(root) if c.co_name==name)
def function(code,env):
    env.setdefault('__builtins__',__builtins__)
    return (lambda:None).__class__(code,env)
checks=[]
def check(name,condition):
    assert condition,name
    checks.append(name)
class Obj(object):
    def __init__(self,**values):self.__dict__.update(values)
original=typesLoader.load(TYPES)
get_data=function(find(MODULES['fsdBuiltData/common/base.pyj'],'GetData'),{})
storage=Obj(__binary__=original,__resBuiltFile__='res:/staticdata/types.fsdbinary')
expanded=get_data(storage)
check('New ID added through the actual patched GetData',1500000000 in expanded and 1500000000 not in original)
check('Original rows and warmed view retain identity',expanded[3327].typeNameID==original[3327].typeNameID and get_data(storage) is expanded)
check('Added type has native-style attributes',expanded[1500000000].groupID==257 and expanded[1500000000].published==1)
native_dogma=typeDogmaLoader.load(DOGMA)
profiles=eden_skill_overlay.apply('res:/staticdata/typeDogma.fsdbinary',native_dogma)
check('Rank profile supports native attribute access',profiles[1500000000].dogmaAttributes[0].value==3)
required=eden_skill_overlay.apply('res:/staticdata/requiredSkillsForTypes.fsdbinary',{})
check('Prerequisite maps retain integer keys',required[1500000000]=={3327:2})
index=eden_skill_overlay.apply('res:/staticdata/requiredSkillsIndex.fsdbinary',{})
check('Reverse prerequisite index retains integer keys',index[3327][2][150][1]==[1500000000])
localize=function(find(MODULES['localization/localizationBase.pyj'],'GetByMessageID'),{'UNRESOLVED_LOC_MARKER':'missing'})
localizer=Obj(Get=lambda *args,**kwargs:'original text')
check('New message supports Unicode localization',localize(localizer,1000000000,None)==unichr(233).join([u'Eden caf',u' skill']))
check('Unchanged localization keeps original function',localize(localizer,123,None)=='original text')
raw={1:Obj(skills=[Obj(typeID=3327,level=3)]),2:Obj(skills=[])}
env={}
clone=function(find(MODULES['clonegrade/storage.pyj'],'CloneGradeStorage'),env)
env['CloneGradeStorage']=clone
clone._storage=raw
grades=clone()
check('Alpha additions and Omega-only limits are applied',grades[1].skillsByTypeID[1500000000].level==3 and grades[2].skillsByTypeID[1500000000].level==0)
check('Original Alpha skills remain intact',grades[1].skillsByTypeID[3327].level==3)
types_by_group={}
for tid,row in expanded.iteritems():types_by_group.setdefault(row.groupID,set()).add(tid)
groups=groupsLoader.load(GROUPS)
evetypes=Obj(GetTypeIDsByCategory=lambda cat:set(tid for gid,ids in types_by_group.iteritems() if groups[gid].categoryID==cat for tid in ids),IsPublished=lambda tid:expanded[tid].published)
skill_env={'evetypes':evetypes,'const':Obj(categorySkill=16),'CharacterSkillEntry':lambda *args:args}
discover=function(find(MODULES['eve/client/script/ui/services/skillsvc.pyj'],'GetAllSkills'),skill_env)
service=Obj(allskills=None,GetSkillRank=lambda tid:3.0 if tid==1500000000 else 1.0)
skills=discover(service)
check('Exact native discovery includes the packaged custom skill',len(skills)==513 and skills[1500000000]==(1500000000,0,0,3.0,None))
open(RESULT,'wb').write(repr(checks))
'''
            values = {'LIBRARY': str(library), 'BINARIES': str(root / 'tq/bin64'),
                      'TYPES': resources['res:/staticdata/types.fsdbinary'], 'GROUPS': resources['res:/staticdata/groups.fsdbinary'],
                      'DOGMA': resources['res:/staticdata/typedogma.fsdbinary'], 'MODULES': modules, 'RESULT': str(work / 'result.txt')}
            import re
            worker = re.sub(r'\b(' + '|'.join(values) + r')\b', lambda m: repr(values[m.group()]), worker)
            with os.add_dll_directory(str(root / 'tq/bin64')):
                runtime = ctypes.CDLL(str(root / 'tq/bin64/python27.dll'))
                self.assertEqual(0, runtime.PyRun_SimpleString(worker.encode('ascii')))
            self.assertIn('Exact native discovery includes the packaged custom skill', (work / 'result.txt').read_text())
            print('11 native adapter assertions passed; no game session started')


if __name__ == '__main__':
    unittest.main()
