"""Bind ORE and AIR epic arcs to resident training rules and the installed catalog."""

from mission_content import source


FRACTURED_LEGACY = 128
VISIONS_OF_GREATNESS = 132
VELDSPAR = 1230
SCORDITE = 1228
VELDSPAR_ISOTOPE = 90421
RUBBLE = 90426
ORE_COMMUNICATION_ARRAY = 1347607
EMERGENCY_PURGE_INITIATOR = 1354485
GRAVIMETRIC_TRANSMITTER = 1354486

# Ordered anchors identify existing installed rooms. These scenes use invisible
# entry markers instead of the standard deadspace beacon.
ROOMS = {
    17130: (1342000, 1329457),
    17131: (1350259, 1350258),
    17132: (1350289, 1350288),
    17183: (1490441, 1490439, 1507524, 1507523),
    17184: (1490442, 1490444, 1498727),
    17185: (1490451, 1490449),
    17186: (1490452, 1490455),
}

REPORTS = {
    17130: ('The_Missing_Pilot', 231718),
    17131: ('Pioneering_Spirit', 231592),
    17132: ('Shattered_Catalyst', 232556),
    17183: ('Golgothan_Fields', 231735),
    17184: ('Caldari_Prime_Breakout', 231712),
    17185: ('Intaki_Liberation', 231736),
    17186: ('Battle_of_Pator', 231734),
    17187: ('Travel_to_Amarr', 231732),
    17188: ('Holographic_Reenactment', 231730),
    17189: ('Travel_to_the_Gallente', 231733),
    17190: ('Travel_to_Minmatar', 231731),
}

AIR_SUPPLIES = {
    # The server-specific crate loot tables are absent; issue the documented
    # hull and basic fittings directly through the mission's acceptance grant.
    17183: (597, ((533, 1), (450, 1), (439, 1), (11355, 1), (246, 3))),
    17184: (603, ((1957, 1), (561, 1), (439, 1), (3586, 1), (222, 2000))),
    17185: (593, ((1968, 1), (561, 1), (439, 1), (2454, 5), (222, 2000))),
    17186: (587, ((22175, 1), (484, 1), (439, 1), (12709, 1), (185, 2000))),
}

MINMATAR_FRIGATE = 24786
MINMATAR_DESTROYER = 24810
GALLENTE_FRIGATE = 14361
GALLENTE_DESTROYER = 25129
CALDARI_FRIGATE = 25015
CALDARI_DESTROYER = 25026
AMARR_FRIGATE = 14362

SIMULATION_WAVES = {
    17183: ((MINMATAR_FRIGATE, 3), (MINMATAR_DESTROYER, 2), (MINMATAR_DESTROYER, 3)),
    17184: ((GALLENTE_FRIGATE, 3), (GALLENTE_DESTROYER, 3)),
    17185: ((CALDARI_FRIGATE, 4),),
    17186: ((AMARR_FRIGATE, 4),),
}


def definitions(db, _group):
    for arc_id in (FRACTURED_LEGACY, VISIONS_OF_GREATNESS):
        graph = source(db, 'client/epicarcs', arc_id)['missions']
        for mission_id in sorted(map(int, graph)):
            page, revision = REPORTS[mission_id]
            url = f'https://wiki.eveuniversity.org/index.php?title={page}&oldid={revision}'
            result = dict(missionId=mission_id, source=url,
                          sourceRevision=f'{revision}; build-3662168 mission and dungeon records', waves=[])
            if mission_id in ROOMS:
                mission = source(db, 'client/missions', mission_id)
                dungeon = source(db, 'client/dungeons', mission['killMission']['dungeonID'])
                room_ids, objects = _bind(result, dungeon, ROOMS[mission_id])
                if arc_id == FRACTURED_LEGACY:
                    _ore(result, mission_id, objects)
                else:
                    _simulation(result, mission_id, room_ids)
            yield result


def _bind(result, dungeon, anchors):
    objects = {
        obj['objectID']: (int(room), obj)
        for room, data in dungeon['rooms'].items()
        for obj in data['objects'].values()
    }
    rooms = [objects[anchor][0] for anchor in anchors]
    if anchors[0] != dungeon['entryObjectID'] or len(set(rooms)) != len(dungeon['rooms']):
        raise ValueError(f"Training arc entrance changed for dungeon {dungeon['dungeonID']}")
    result['rooms'] = [dict(roomId=room, entryObjectId=anchor) for room, anchor in zip(rooms, anchors)]
    next_room = dict(zip(rooms, rooms[1:]))
    result['gates'] = [
        dict(fromObjectId=edge['fromObjectID'], destinationRoomId=next_room[objects[edge['fromObjectID']][0]])
        for edge in dungeon['connections']
    ]
    return rooms, objects


def _ore(result, mission_id, objects):
    if mission_id == 17130:
        samples = [(obj['objectID'], int(obj['radius'])) for _, obj in objects.values()
                   if obj.get('typeID') in (VELDSPAR, SCORDITE)]
        result['containers'] = [_analyzer(ORE_COMMUNICATION_ARRAY, requiresMiningDepletion=True)]
    elif mission_id == 17131:
        # A fixed 4,000 m3 sample keeps the pilot's progress and objective
        # consistent after a restart without replicating the hidden selector.
        samples = [(obj['objectID'], 2000) for _, obj in objects.values() if obj.get('typeID') == VELDSPAR_ISOTOPE][:2]
        result.update(mining=True, objectiveTypeId=VELDSPAR_ISOTOPE, objectiveQuantity=4000)
        result['gates'][0]['requiresMiningSurvey'] = True
    else:
        samples = [(obj['objectID'], int(obj['radius'])) for _, obj in objects.values() if obj.get('typeID') == RUBBLE][:3]
        result['containers'] = [
            _analyzer(EMERGENCY_PURGE_INITIATOR, requiresMiningDepletion=True),
            _analyzer(GRAVIMETRIC_TRANSMITTER, requiresMiningDepletion=True),
        ]
    if not samples or any(quantity <= 0 for _, quantity in samples):
        raise ValueError(f'No mineable training deposits for mission {mission_id}')
    result['careerDeposits'] = [dict(objectId=object_id, quantity=quantity) for object_id, quantity in samples]


def _analyzer(object_id, **options):
    return dict(objectId=object_id, typeId=0, quantity=0, analyzer=0, completesOnHack=True, **options)


def _simulation(result, mission_id, rooms):
    hull, supplies = AIR_SUPPLIES[mission_id]
    result.update(
        giftTypeId=hull,
        giftQuantity=1,
        additionalGifts=[dict(typeId=type_id, quantity=count) for type_id, count in supplies],
    )
    for room, (type_id, count) in zip(rooms[1:], SIMULATION_WAVES[mission_id]):
        result['waves'].append(dict(roomId=room, groups=[dict(types=[type_id], count=count, range=16000)]))
    for gate in result['gates'][1:]:
        gate['requiresClear'] = True
    if mission_id == 17185:
        result['waves'].append(dict(roomId=rooms[-1], groups=[dict(types=[CALDARI_DESTROYER], count=3, range=22000)]))
    elif mission_id == 17186:
        result['containers'] = [_analyzer(object_id) for object_id in (1490893, 1490894, 1490895)]
        result['requiresAllHacks'] = True
