"""Headless checks of Eden's authored adapter in the pinned client interpreter.

Set EDEN_TEST_CLIENT_ROOT to an unchanged prepared build-3662168 client.
Only data loaders and inert functions run; the game is never started.
"""
import ast
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
                'travel': {
                    'jumps': [{'stargateID': 58000001, 'fromSystemID': 30000142, 'toSystemID': 30004759, 'jumpType': 2},
                              {'stargateID': 58000002, 'fromSystemID': 30004759, 'toSystemID': 30000142, 'jumpType': 2}],
                    'systems': {'30000142': {'neighbours': [{'solarSystemID': 30004759, 'stargateID': 58000001, 'jumpType': 2}]}},
                    'content': {'30000142': {'58000001': {'destination': 58000002, 'typeID': 3873,
                                                       'position': {'x': 1.0, 'y': 2.0, 'z': 3.0}}}},
                    'locations': {'58000001': {'systemID': 30000142, 'typeID': 3873, 'name': 'Stargate (1DQ1-A)',
                                              'position': {'x': 1.0, 'y': 2.0, 'z': 3.0}}},
                },
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
                        if name in builder.WRAPPERS or name in (builder.GEOGRAPHY_MODULE, 'evetypes/__init__.pyj',
                                                               'eve/client/script/ui/services/skillsvc.pyj',
                                                               'eve/client/script/ui/shared/maps/mapSvc.pyj',
                                                               'eve/client/script/ui/shared/info/infosvc.pyj',
                                                               'loginCharacterselection/charselData.pyj'):
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
    def walk(c,parent=''):
        path=parent+c.co_name
        yield path,c
        for v in c.co_consts:
            if hasattr(v,'co_code'):
                for nested in walk(v,path+'.'):yield nested
    return next(c for path,c in walk(root) if c.co_name==name or path==name)
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
import os
previous_appdata=os.environ.get('LOCALAPPDATA')
os.environ['LOCALAPPDATA']=os.path.dirname(RESULT)
def fail_portrait(*args,**kwargs):raise ValueError('headless character-slot failure')
slot_loader=function(find(MODULES['eve/client/script/ui/login/charSelection/characterSlots.pyj'],'LoadSlot'),
    {'sm':Obj(GetService=lambda service:Obj(GetPortrait=fail_portrait))})
try:
    slot_loader(Obj(portraitSprite=Obj()),90000001,Obj())
except ValueError as error:
    check('Character slot preserves the original exception',str(error)=='headless character-slot failure')
else:
    raise AssertionError('Character slot swallowed the native exception')
diagnostic=os.path.join(os.environ['LOCALAPPDATA'],'Eden','ClientErrors','character-selection-%s.log'%os.getpid())
record=open(diagnostic,'rb').read()
check('Character slot writes a durable native traceback','stage=character-selection character=90000001' in record and 'characterSlots.py' in record and 'headless character-slot failure' in record)
header_loader=function(find(MODULES['eve/client/script/ui/shared/info/infoContentBase.pyj'],
                           '<module>.InfoWindowMonolith.update_description_caption_and_sub_caption'),{})
try:
    header_loader(Obj(item_id=30000142,get_description_caption_and_sub_caption=fail_portrait))
except ValueError:
    record=open(diagnostic,'rb').read()
    check('Blank info header preserves native exception and captures traceback',
          'stage=info-header character=30000142' in record and 'infoContentBase.py' in record)
else:
    raise AssertionError('Info header swallowed the native exception')
geography=MODULES['eve/common/script/sys/eveCfg.pyj']
cfg=Obj(_mapJumpCache=[Obj(stargateID=50000001)],
    _mapSystemCache={30000142:Obj(neighbours=[],regionID=10000002),30000144:Obj(neighbours=[])},
    _mapSolarSystemContentCache={30000142:Obj(stargates={50000001:Obj(destination=50000002)},planets={1:'original'})},
    _map_stargate_id_mapping=None)
env={}
map_jumps=function(find(geography,'mapJumpCache'),env)
jumps=map_jumps(cfg)
check('Patched native jump cache includes the express edge',jumps[-2].fromSystemID==30000142 and jumps[-2].toSystemID==30004759)
check('Warmed jump cache is shared and original stays intact',map_jumps(cfg) is jumps and len(cfg._mapJumpCache)==1)
systems=function(find(geography,'mapSystemCache'),env)(cfg)
check('Native neighbour fields and original region survive',systems[30000142].neighbours[0].stargateID==58000001 and systems[30000142].regionID==10000002)
check('Unchanged systems retain identity',systems[30000144] is cfg._mapSystemCache[30000144])
cfg.mapSolarSystemContentCache=function(find(geography,'mapSolarSystemContentCache'),env)(cfg)
content=cfg.mapSolarSystemContentCache[30000142]
check('Gate art position destination and planets survive',content.stargates[58000001].typeID==3873 and content.stargates[58000001].position.z==3 and content.planets[1]=='original')
mapping=function(find(geography,'mapStargateIdMappingCache'),env)(cfg)
check('Exact native gate-ID mapping resolves both directions',mapping[58000001]==58000002 and mapping[58000002]==58000001)
class Rows(list):
    def __init__(self,header,rows):
        list.__init__(self,rows)
        self.columns=[name for name,kind in header]
    def InsertNew(self,values):
        self.append(Obj(**dict(zip(self.columns,values))))
class Constants(object):
    def __getattr__(self,name):return 0
env.update(boot=Obj(role='client'),blue=Obj(DBRowDescriptor=lambda columns:columns),const=Constants(),CRowset=Rows)
cfg.mapObjectsDb=Obj(execute=lambda sql:[])
cfg.GetLocationRowDescriptor=function(find(geography,'GetLocationRowDescriptor'),env).__get__(cfg)
columns,locations=function(find(geography,'GetLocationsLocal'),env)(cfg,[58000001])
check('Exact native location reply contains express gate name',locations[0].locationID==58000001 and locations[0].locationName=='Stargate (1DQ1-A)')
local=function(find(geography,'GetLocationsLocalBySystem'),env)(cfg,30000142,True,False)
check('Exact native system map reply contains express gate',local[0].itemID==58000001 and local[0].locationID==30000142)
check('Added gates match native celestial hierarchy fields',local[0].orbitID is None and local[0].celestialIndex is None and
      local[0].orbitIndex is None and not local[0].connector)
from fsd.schemas import binaryLoader
import yaml
native_systems=binaryLoader.LoadFromString(open(SYSTEMS,'rb').read(),yaml.safe_load(open(SYSTEM_SCHEMA,'rb')))
info_cfg=Obj(mapSystemCache=eden_skill_overlay.travel_systems(native_systems),
             mapConstellationCache=eden_skill_overlay.MapRows(dict((row.constellationID,Obj(regionID=row.regionID))
                                                                  for identity,row in native_systems.iteritems())),
             evelocations=Obj(Get=lambda identity:Obj(name='Location %s'%identity)))
ids=Obj(IsSolarSystem=lambda identity:30000000<=identity<40000000,
        IsStation=lambda identity:60000000<=identity<70000000,IsCelestial=lambda identity:40000000<=identity<60000000,
        IsConstellation=lambda identity:20000000<=identity<30000000,IsRegion=lambda identity:10000000<=identity<20000000)
constants=Obj(typeSolarSystem=5,groupSolarSystem=5,groupSun=6,groupPlanet=7,groupMoon=8,groupStargate=10,
              groupAsteroidBelt=9,groupStation=15,groupConstellation=4,groupRegion=3,categoryStructure=65,locationUniverse=9)
ids.IsKnownSpaceSystem=ids.IsSolarSystem
ids.IsKnownSpaceConstellation=ids.IsConstellation
ids.IsKnownSpaceRegion=ids.IsRegion
map_env={'cfg':info_cfg,'idCheckers':ids,'const':constants,'utillib':Obj(KeyVal=Obj),
         'IsKnownSpaceSystem':ids.IsSolarSystem,'IsKnownSpaceConstellation':ids.IsConstellation,
         'IsKnownSpaceRegion':ids.IsRegion,'IsValidDestination':lambda identity:True}
map_service=Obj(GetSolarsystemItems=lambda *args:local)
for name in ('GetItem','GetNeighbors','GetParentLocationID','GetParent'):
    method=function(find(MODULES['eve/client/script/ui/shared/maps/mapSvc.pyj'],name),map_env)
    if name=='GetItem':method.func_defaults=(False,None)
    setattr(map_service,name,method.__get__(map_service))
check('Exact native info neighbour lookup includes express destination',map_service.GetNeighbors(30000142)==[30004759])
check('Exact native system item retains its original geometry',map_service.GetItem(30000142).x==native_systems[30000142].center.x)
info_env={'cfg':info_cfg,'idCheckers':ids,'const':constants,
          'evetypes':Obj(GetGroupID=lambda identity:5 if identity==5 else 10,GetCategoryID=lambda identity:2),
          'SortListOfTuples':lambda rows:[value for key,value in sorted(rows)]}
wnd=Obj(data={'structures':{'items':[]},'orbital':{'items':[]}})
info_env.update(TAB_STRUCTURES='structures',TAB_ORBITALBODIES='orbital')
info=Obj(map=map_service)
function(find(MODULES['eve/client/script/ui/shared/info/infosvc.pyj'],'GetOrbitalBodies'),info_env)(info,wnd,30000142,30000142,5)
check('Exact native orbital info call accepts added gate records',wnd.data['structures']['items']==[])
from collections import defaultdict
info_env.update(IsKnownSpaceSystem=ids.IsSolarSystem,IsKnownSpaceConstellation=ids.IsConstellation,IsKnownSpaceRegion=ids.IsRegion,
                GetByLabel=lambda label,**kwargs:label,GetFromClass=lambda cls,data=None,**kwargs:data,
                LabelLocationTextTop=Obj,Bookmark=Obj,is_directional_scanner_suppressed=lambda identity:False,
                GetItemTraderComponent=lambda *args:None)
info_env['sm']=Obj(GetService=lambda name:Obj(IsSystemConquarable=lambda identity:False,
                                            IsWarzoneSolarSystem=lambda identity:False,ShortestGeneralPath=lambda identity:[]))
info_env['evetypes'].GetName=lambda identity:'Type %s'%identity
for tab in ('TAB_ATTRIBUTES','TAB_STATIONS','TAB_NEIGHBORS','TAB_ROUTE','DATA_BUTTONS'):
    info_env[tab]=tab
info.GetOrbitalBodies=function(find(MODULES['eve/client/script/ui/shared/info/infosvc.pyj'],'GetOrbitalBodies'),info_env).__get__(info)
info.GetStationTab=lambda *args:None
info.Bookmark=lambda *args:None
info.GetColorCodedSecurityStringForSystem=lambda identity,name:name
wnd=Obj(data=defaultdict(lambda:{'items':[]}),dynamicTabs=[])
wnd.data['DATA_BUTTONS']=[]
function(find(MODULES['eve/client/script/ui/shared/info/infosvc.pyj'],'UpdateDataCelestial'),info_env)(info,wnd,5,30000142,None)
check('Exact native system info builds the express neighbour tab',wnd.data['TAB_NEIGHBORS']['items'][0]['itemID']==30004759)
try:
    function(find(MODULES['eve/client/script/ui/shared/info/infosvc.pyj'],'UpdateDataCelestial'),info_env)(
        Obj(map=Obj(GetParentLocationID=fail_portrait)),wnd,5,30000142,None)
except ValueError:
    record=open(diagnostic,'rb').read()
    check('Blank info body preserves native exception and captures traceback',
          'stage=celestial-info character=30000142' in record and 'infosvc.py' in record)
else:
    raise AssertionError('Info body swallowed the native exception')
import cPickle
native_starmap=cPickle.loads(open(STARMAP,'rb').read())
for module in ('eve/client/script/ui/shared/mapView/mapViewData.pyj','eve/client/script/ui/shared/maps/starMapSvc.pyj'):
    getter=function(find(MODULES[module],'GetStarMapCache'),{})
    service=Obj(starMapCache=native_starmap,mapJumps=None)
    service.GetStarMapCache=getter.__get__(service)
    view=service.GetStarMapCache()
    check(module+' includes one line per reciprocal route',len(view['jumps'])==len(native_starmap['jumps'])+1)
    check(module+' keeps original cache immutable and reuses overlay',getter(service) is view and
          native_starmap['solarSystems'][30000142]['neighbours']!=view['solarSystems'][30000142]['neighbours'])
    check(module+' preserves original system metadata',
          view['solarSystems'][30000142]['center'] is native_starmap['solarSystems'][30000142]['center'])
    if 'mapViewData' in module:
        service.PrimeJumpData=lambda source,destination,kind:Obj(fromSolarSystemID=source,toSolarSystemID=destination,jumpType=kind)
        rendered=list(function(find(MODULES[module],'IterateJumps'),{})(service))
        check('Exact native map line iterator includes the express route',
              rendered[-1].fromSolarSystemID==30000142 and rendered[-1].toSolarSystemID==30004759 and rendered[-1].jumpType==2)
        check('Warmed native map line iterator does not duplicate lines',
              list(function(find(MODULES[module],'IterateJumps'),{})(service))==rendered)
from fsd.schemas import binaryLoader
native_content=binaryLoader.LoadFromString(open(CONTENT,'rb').read())
class IndexedContent(object):
    def __init__(self,original):
        self.original=original
        self.npcStations={}
        self.stargates={}
        def visit(system,record):
            for field in ('npcStations','stargates','planets','moons'):
                try:values=getattr(record,field)
                except (AttributeError,KeyError):continue
                for identity,row in values.iteritems():
                    if field in ('planets','moons'):
                        visit(system,row)
                    else:
                        getattr(self,field)[identity]=eden_skill_overlay.Row(
                            {'solarSystemID':system,'stationName':'Station %s'%identity},row)
        for system,record in original.iteritems():visit(system,record)
    def __getitem__(self,identity):return self.original[identity]
    def iteritems(self):return self.original.iteritems()
indexed=IndexedContent(native_content)
overlaid=eden_skill_overlay.travel_content(indexed)
check('Native root NPC station index keeps identity',overlaid.npcStations is indexed.npcStations)
check('Native root stargate index includes the new gate',overlaid.stargates[58000001].solarSystemID==30000142 and len(overlaid.stargates)==len(indexed.stargates)+1)
check('Gate vectors preserve native sequence and named access',tuple(overlaid.stargates[58000001].position)==(1,2,3) and overlaid.stargates[58000001].position[0]==1 and overlaid.stargates[58000001].position.z==3)
class StationRows(object):
    def __init__(self,rowclass,key,data):self.data=dict((row.stationID,row) for row in data[1])
    def Get(self,identity):return self.data[identity]
station_cfg=Obj(mapSolarSystemContentCache=overlaid)
station_env={'sysCfg':Obj(Recordset=StationRows,Row=Obj),'NPCStation':lambda **values:Obj(**values),
             'NPCStationProps':[],'DOOMHEIM_STATION':Obj(stationID=-1)}
function(find(geography,'LoadNPCStations'),station_env)(station_cfg)
check('Exact native station loader retains every station',len(station_cfg.stations.data)==len(indexed.npcStations)+1)
check('Exact native station loader restores Kihtaled orbit lookup',station_cfg.stations.Get(60013933).orbitID==40244213)
location_names={30003860:'Kihtaled',40244213:'Kihtaled VIII - Moon 7',60013933:'Kihtaled VIII - Moon 7 - Royal Khanid Navy Assembly Plant'}
station_cfg.evelocations=Obj(Get=lambda identity:Obj(name=location_names[identity]))
details=Obj(stationID=60013933,solarSystemID=30003860,cfg=station_cfg,stripTags=lambda value,**kwargs:value,
            localization=Obj(GetByLabel=lambda label:'Moon',HIGHLIGHT_IMPORTANT_MARKER='*'))
station_info=function(find(MODULES['loginCharacterselection/charselData.pyj'],'GetCurrentStationAndStationLocation'),
                      {'IsStation':lambda identity:True})(details)
check('Exact failing character selector call now resolves station details','Royal Khanid Navy Assembly Plant' in station_info['stationName'] and station_info['shortOrbitName']=='VIII - 7')
eden_skill_overlay.DATA.pop('travel')
check('Disabled mod preserves native cache identity',map_jumps(cfg) is cfg._mapJumpCache)
check('Disabled mod preserves native starmap identity',eden_skill_overlay.travel_starmap(native_starmap) is native_starmap)
if previous_appdata is None:os.environ.pop('LOCALAPPDATA')
else:os.environ['LOCALAPPDATA']=previous_appdata
open(RESULT,'wb').write(repr(checks))
'''
            values = {'LIBRARY': str(library), 'BINARIES': str(root / 'tq/bin64'),
                      'TYPES': resources['res:/staticdata/types.fsdbinary'], 'GROUPS': resources['res:/staticdata/groups.fsdbinary'],
                      'DOGMA': resources['res:/staticdata/typedogma.fsdbinary'],
                      'CONTENT': resources['res:/staticdata/solarsystemcontent.static'],
                      'SYSTEMS': resources['res:/staticdata/systems.static'],
                      'SYSTEM_SCHEMA': resources['res:/staticdata/systems.schema'],
                      'STARMAP': resources['res:/staticdata/starmapcache.pickle'],
                      'MODULES': modules, 'RESULT': str(work / 'result.txt')}
            import re
            worker = re.sub(r'\b(' + '|'.join(values) + r')\b', lambda m: repr(values[m.group()]), worker)
            with os.add_dll_directory(str(root / 'tq/bin64')):
                runtime = ctypes.CDLL(str(root / 'tq/bin64/python27.dll'))
                self.assertEqual(0, runtime.PyRun_SimpleString(worker.encode('ascii')))
            self.assertIn('Exact native discovery includes the packaged custom skill', (work / 'result.txt').read_text())
            count = len(ast.literal_eval((work / 'result.txt').read_text()))
            print('%d native adapter assertions passed; no game session started' % count)


if __name__ == '__main__':
    unittest.main()
