"""Bind the existing pirate arc nodes to Eden's authored encounter objectives."""

from mission_content import source


SMASH_AND_GRAB = 55
ANGEL_SOUND = 56
BEACON = 10124

CALDARI_FRIGATE = 25015
CALDARI_DESTROYER = 25026
CALDARI_CRUISER = 25043
CALDARI_BATTLECRUISER = 25063
CALDARI_HAULER = 20386
CALDARI_ELITE_FRIGATE = 25037
CALDARI_LIGHT_BATTERY = 17601
MINMATAR_FRIGATE = 24786
MINMATAR_DESTROYER = 24810
MINMATAR_CRUISER = 24834
GURISTAS_FRIGATE = 16996
GURISTAS_DESTROYER = 23971
GURISTAS_CRUISER = 16991
GURISTAS_SENTRY = 13068
SANSHA_FRIGATE = 24053
SANSHA_CRUISER = 17062
MERCENARY_CRUISER = 12835
ROGUE_DRONE = 18079
ROGUE_DRONE_CRUISER = 10986
YUKIRO_DEMENSE = 32391
GATH_RENTON = 32390
EROMA_ERALEN = 32392
SARO_FRIGATE = 2045

EXPLOSIVE_SYSTEM = 32383
STRIKE_FORCE = 32389
RESEARCH_CACHE = 32393
FOXFIRE = 32398
OVERLAY_TRANSPONDER = 32403
SECURITY_CODES = 32418

SYSTEMS = {
    15020: 'P-FSQE',
    15021: 'P-FSQE',
    15046: 'K-QWHE',
    15047: 'K-QWHE',
    15061: 'Q-7SUI',
    15063: 'AJCJ-1',
    15064: 'AJCJ-1',
}

REFERENCES = {
    SMASH_AND_GRAB: (
        ('Smash_and_Grab/Chapter_1:_Probation', 206680),
        ('Smash_and_Grab/Chapter_2:_For_Fun_and_Profit', 206689),
        ('Smash_and_Grab/Chapter_3:_Internal_Security_(Irichi_Path)', 206696),
        ('Smash_and_Grab/Chapter_3:_Internal_Security_(Kori_Path)', 206702),
    ),
    ANGEL_SOUND: (
        ('Angel_Sound/Chapter_1_-_Rapture', 206602),
        ('Angel_Sound/Chapter_2_-_Heaven', 206610),
        ('Angel_Sound/Chapter_2_-_Utopia', 206617),
        ('Angel_Sound/Chapter_3_-_Paradise', 206624),
    ),
}

ANGEL_HEAVEN = {15034, 15035, 15038, 15039, 15042, 15044}
ANGEL_UTOPIA = {15033, 15036, 15037, 15040, 15041, 15043}


def definitions(db, _group):
    for arc_id in (SMASH_AND_GRAB, ANGEL_SOUND):
        graph = source(db, 'client/epicarcs', arc_id)['missions']
        for mission_id in sorted(map(int, graph)):
            mission = source(db, 'client/missions', mission_id)
            chapter = _chapter(arc_id, mission_id)
            title, revision = REFERENCES[arc_id][chapter]
            scene = dict(
                missionId=mission_id,
                source=f'https://wiki.eveuniversity.org/index.php?title={title}&oldid={revision}',
                sourceRevision=f'{revision}; build-3662168 mission assignments and existing pocket bindings',
                waves=[],
            )
            if mission_id in SYSTEMS:
                scene['siteSystemId'], = db.execute(
                    'SELECT Id FROM SolarSystem WHERE Name=?', (SYSTEMS[mission_id],)
                ).fetchone()
            kill = mission.get('killMission')
            if kill is not None:
                dungeon = source(db, 'client/dungeons', kill['dungeonID'])
                rooms = _rooms(scene, dungeon)
                _combat(scene, mission_id, rooms, kill)
            yield scene


def _chapter(arc_id, mission_id):
    if arc_id == SMASH_AND_GRAB:
        if mission_id in (15060, 15061, 15065, 15066):
            return 2
        if mission_id in (15062, 15063, 15064):
            return 3
        return int(15055 <= mission_id <= 15059)
    if mission_id in ANGEL_HEAVEN:
        return 1
    if mission_id in ANGEL_UTOPIA:
        return 2
    return 3 if mission_id in (15045, 15046, 15047, 15144) else 0


def _rooms(scene, dungeon):
    entries = {
        int(room): next(obj['objectID'] for obj in data['objects'].values() if obj.get('typeID') == BEACON)
        for room, data in dungeon['rooms'].items()
    }
    rooms = sorted(entries, key=lambda room: (entries[room] != dungeon['entryObjectID'], room))
    if entries[rooms[0]] != dungeon['entryObjectID']:
        raise ValueError(f"Pirate dungeon {dungeon['dungeonID']} has no bound entrance")
    scene['rooms'] = [dict(roomId=room, entryObjectId=entries[room]) for room in rooms]
    objects = {obj['objectID']: int(room) for room, data in dungeon['rooms'].items() for obj in data['objects'].values()}
    next_room = dict(zip(rooms, rooms[1:]))
    scene['gates'] = [
        dict(fromObjectId=link['fromObjectID'], destinationRoomId=next_room[objects[link['fromObjectID']]])
        for link in dungeon.get('connections', [])
    ]
    return rooms


def _group(type_id, count=1, distance=24000, **options):
    return dict(types=[type_id], count=count, range=distance, **options)


def _wave(scene, room, *groups, **options):
    scene['waves'].append(dict(roomId=room, groups=list(groups), **options))


def _container(scene, object_id, item_id=0, **options):
    scene.setdefault('containers', []).append(dict(objectId=object_id, typeId=item_id, quantity=int(item_id != 0), **options))


def _structure(scene, object_id, **options):
    scene.setdefault('structures', []).append(dict(objectId=object_id, **options))


def _lock(scene, *gates):
    for index in gates:
        scene['gates'][index]['requiresClear'] = True


def _combat(scene, mission_id, rooms, kill):
    first, end = rooms[0], rooms[-1]
    objective = kill.get('objectiveTypeID')
    if mission_id in (15018, 15143):
        scene['deadspace'] = False
        _wave(scene, first, _group(CALDARI_FRIGATE, 2), _group(CALDARI_DESTROYER))
        _wave(scene, first, _group(CALDARI_HAULER, completesMission=True, lootTypeId=objective,
                                  lootQuantity=1, lootToPilotCargo=True, careerName='Expedition Transport'),
              _group(CALDARI_ELITE_FRIGATE, 2, required=False))
    elif mission_id in (15020, 15021):
        if mission_id == 15021:
            _lock(scene, 1)
            _wave(scene, rooms[1], _group(CALDARI_FRIGATE, 4), _group(CALDARI_CRUISER))
        _wave(scene, end, _group(CALDARI_FRIGATE, 3), _group(CALDARI_DESTROYER, 2), _group(CALDARI_ELITE_FRIGATE))
        _container(scene, 961881 if mission_id == 15020 else 962056, objective, requiresClear=True)
    elif mission_id in (15028, 15058):
        scene['careerVisit'] = dict(objectId=962492, range=85000)
        _wave(scene, end, _group(SANSHA_FRIGATE, 3, 52000, required=False), _group(SANSHA_CRUISER, required=False))
    elif mission_id == 15033:
        _wave(scene, end, _group(SANSHA_FRIGATE, 4), _group(SANSHA_CRUISER))
        _wave(scene, end, _group(SANSHA_FRIGATE, 3), _group(SANSHA_CRUISER, 2))
    elif mission_id == 15034:
        _container(scene, 963191, depositTypeId=RESEARCH_CACHE, depositQuantity=1)
    elif mission_id in (15035, 15039):
        _wave(scene, end, _group(YUKIRO_DEMENSE, completesMission=True), _group(GURISTAS_FRIGATE, 2, required=False))
    elif mission_id == 15036:
        _wave(scene, end, _group(GURISTAS_FRIGATE, 3), _group(GURISTAS_DESTROYER))
        _wave(scene, end, _group(GURISTAS_FRIGATE, 2), _group(GURISTAS_DESTROYER, 2))
    elif mission_id == 15037:
        _wave(scene, end, _group(GATH_RENTON, completesMission=True, enemyFaction=500010),
              _group(GURISTAS_DESTROYER, 2, required=False, enemyFaction=500010))
    elif mission_id == 15038:
        scene['careerVisit'] = dict(objectId=963768, range=2500, requiresClear=True)
        _wave(scene, end, _group(MERCENARY_CRUISER, 2), _group(ROGUE_DRONE, 3))
        _wave(scene, end, _group(ROGUE_DRONE, 4), _group(MERCENARY_CRUISER))
    elif mission_id == 15040:
        _wave(scene, end, _group(SANSHA_FRIGATE, 3, enemyFaction=500019), _group(GURISTAS_DESTROYER, 2))
        _wave(scene, end, _group(YUKIRO_DEMENSE), _group(GURISTAS_FRIGATE, 3))
    elif mission_id in (15045, 15047):
        _lock(scene, 1, 2)
        objectives = (964743, 964742, 964741) if mission_id == 15045 else (965013, 965034, 965060)
        for room, object_id, count in zip(rooms[1:], objectives, (3, 4, 5)):
            _wave(scene, room, _group(MINMATAR_DESTROYER, count), _group(MINMATAR_FRIGATE, 2))
            if mission_id == 15045:
                _structure(scene, object_id, required=True)
        if mission_id == 15047:
            _structure(scene, objectives[-1], completesMission=True)
    elif mission_id == 15046:
        _wave(scene, end, _group(MINMATAR_CRUISER, lootTypeId=SECURITY_CODES, lootQuantity=1,
                                  careerName='Communications Engineer'))
        _container(scene, 964756, depositTypeId=SECURITY_CODES, depositQuantity=1)
    elif mission_id == 15055:
        _container(scene, 961995, analyzer=0, completesOnHack=True)
        _wave(scene, end, _group(CALDARI_FRIGATE, 4, required=False), _group(CALDARI_DESTROYER, 2, required=False))
        _wave(scene, end, _group(CALDARI_ELITE_FRIGATE, 2, required=False),
              trigger='HackResolved', triggerObjectId=961995)
    elif mission_id == 15056:
        _lock(scene, 0)
        _wave(scene, first, _group(CALDARI_FRIGATE, 3), _group(CALDARI_DESTROYER, 2))
        _wave(scene, end, _group(CALDARI_FRIGATE, 3, required=False), _group(CALDARI_CRUISER, 2, required=False))
        for object_id in (969082, 969083, 969084):
            _structure(scene, object_id, completesMission=True)
    elif mission_id == 15057:
        _container(scene, 962426, EXPLOSIVE_SYSTEM)
        scene['careerVisit'] = dict(objectId=962434, range=30000, requiredCargoTypeId=EXPLOSIVE_SYSTEM, removeObject=True)
        scene['careerSacrifice'] = True
    elif mission_id == 15061:
        _wave(scene, end, _group(CALDARI_LIGHT_BATTERY, 2, required=False), _group(CALDARI_FRIGATE, 2, required=False))
        _structure(scene, 962588)
        _container(scene, 962589, depositTypeId=STRIKE_FORCE, depositQuantity=1, depositRequiresDestroyedObjectId=962588)
    elif mission_id == 15062:
        scene['careerVisit'] = dict(objectId=963077, range=9000, requiresClear=True)
        _wave(scene, end, _group(ROGUE_DRONE, 3), _group(ROGUE_DRONE_CRUISER))
        _wave(scene, end, _group(ROGUE_DRONE, 2), _group(ROGUE_DRONE_CRUISER, 2))
    elif mission_id in (15063, 15065):
        _lock(scene, 1, 2)
        _wave(scene, rooms[1], _group(GURISTAS_SENTRY, 4))
        _wave(scene, end, _group(CALDARI_FRIGATE, 3), _group(CALDARI_DESTROYER, 2))
        _structure(scene, 963553 if mission_id == 15063 else 963753, required=True,
                   drop=dict(typeId=objective, quantity=1))
    elif mission_id == 15064:
        scene['gates'][1]['key'] = dict(typeId=OVERLAY_TRANSPONDER)
        _wave(scene, first, _group(EROMA_ERALEN, lootTypeId=OVERLAY_TRANSPONDER, lootQuantity=1))
        _wave(scene, end, _group(CALDARI_ELITE_FRIGATE, 3), _group(CALDARI_DESTROYER, 2), _group(CALDARI_CRUISER))
        _structure(scene, 964077, required=True)
        _structure(scene, 964078, required=True, drop=dict(typeId=objective, quantity=1))
    elif mission_id == 15066:
        _container(scene, 964051, depositTypeId=FOXFIRE, depositQuantity=1, depositCompletesMission=False)
        scene['gates'][1]['requiresDeliveryObjectId'] = 964051
        _wave(scene, end, _group(CALDARI_FRIGATE, 3), _group(CALDARI_DESTROYER))
        _wave(scene, end, _group(CALDARI_BATTLECRUISER, lootTypeId=objective, lootQuantity=1))
    elif mission_id == 15144:
        _wave(scene, end, _group(SARO_FRIGATE, completesMission=True, careerName='Concord Marshal'))
    else:
        raise ValueError(f'Unbound pirate arc combat mission {mission_id}')
