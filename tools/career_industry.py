"""Build-3662168 Industrialist career missions from native content and reviewed objectives."""
import math
import re
from collections import defaultdict

from mission_content import source


ENTREPRENEUR = 'Balancing the Books'
PRODUCER = 'Making Mountains of Molehills'
FACTIONS = {500001, 500002, 500003, 500004}
BLUEPRINT_GIFTS = {ENTREPRENEUR: {10: 5}, PRODUCER: {3: 1, 6: 2, 8: 1, 10: 5}}

# Native title order follows these node graph bindings, which are shared by each empire's variants.
GRAPHS = {
    ENTREPRENEUR: (428, 429, 431, 430, 433, 432, 435, 434, 437, 436),
    PRODUCER: (385, 390, 389, 422, 402, 424, 423, 426, 425, 427),
}
REPORTS = {ENTREPRENEUR: ('Entrepreneur', 214335), PRODUCER: ('Producer', 229156)}
ROOKIES = {
    500010: 'Guristas Rookie',
    500011: 'Angel Rookie',
    500012: 'Blood Raider Rookie',
    500020: 'Serpentis Rookie',
}
ROOKIE_GROUP = 286
DRONE_MOTH_APIS_GROUP = 806
ROGUE_DRONE_FACTION = 500025
BEACON = 10124
VELDSPAR = 1230
KERNITE = 20
CIVILIAN_TRANSPORT_WRECK = 27275
ANCIENT_SHIP_STRUCTURE = 27278
DATA_STORAGE_DEVICES = {64876, 64877, 64878, 64879}


def definitions(db, group):
    rows = defaultdict(dict)
    for mid, title in db.execute("""SELECT Key,json_extract(Data,'$.name.en') FROM SourceRecord
                                 WHERE Dataset='sde/missions' ORDER BY CAST(Key AS INTEGER)"""):
        if not title:
            continue
        match = re.fullmatch(r'(Balancing the Books|Making Mountains of Molehills) \((\d+) of 10\)', title)
        if not match:
            continue
        family, stage = match[1], int(match[2])
        mission = source(db, 'client/missions', mid)
        if mission is None or mission.get('factionID') not in FACTIONS or mission.get('expirationTime') != 0:
            raise ValueError(f'Unexpected Industrialist assignment {mid}')
        if not 1 <= stage <= 10 or mission.get('nodeGraphID') != GRAPHS[family][stage - 1]:
            raise ValueError(f'Unexpected Industrialist objective graph {mid}')
        key = (family, mission['factionID'])
        if stage in rows[key]:
            raise ValueError(f'Duplicate Industrialist stage {key} {stage}')
        rows[key][stage] = (int(mid), mission)
    if set(rows) != {(family, faction) for family in GRAPHS for faction in FACTIONS}:
        raise ValueError('Missing an empire Industrialist chain')
    for (family, _), chain in sorted(rows.items()):
        if set(chain) != set(range(1, 11)):
            raise ValueError(f'Incomplete Industrialist chain {family}')
        for stage, (mid, mission) in sorted(chain.items()):
            yield _definition(db, group, family, stage, mid, mission)


def _definition(db, group, family, stage, mid, mission):
    report, revision = REPORTS[family]
    result = dict(missionId=mid, waves=[], source=f'https://wiki.eveuniversity.org/Industrialist_({report})',
                  sourceRevision=f'oldid={revision}; build-3662168 mission and dungeon records')
    if stage in BLUEPRINT_GIFTS[family]:
        runs = BLUEPRINT_GIFTS[family][stage]
        if mission.get('initialAgentGiftQuantity') != runs:
            raise ValueError(f'Mission {mid} blueprint run count has changed')
        result['giftBlueprintRuns'] = runs
    kill = mission.get('killMission')
    if kill is None:
        template = mission['contentTemplate'].rsplit('_', 1)[-1]
        if template not in ('StorylineCourierMission', 'StorylineTradeMission') or not mission.get('courierMission'):
            raise ValueError(f'Mission {mid} lacks a supported station objective')
        if template == 'StorylineTradeMission':
            result['trade'] = True
        return result

    dungeon = source(db, 'client/dungeons', kill['dungeonID'])
    rooms = _bind_rooms(result, dungeon)
    if family == ENTREPRENEUR:
        _entrepreneur(group, stage, result, mission, dungeon, rooms)
    else:
        _producer(db, group, stage, result, kill, rooms)
    return result


def _bind_rooms(result, dungeon):
    rooms = sorted((int(room_id), list(room['objects'].values())) for room_id, room in dungeon['rooms'].items())
    entries = [_object(objects, {BEACON}) for _, objects in rooms]
    if entries[0]['objectID'] != dungeon['entryObjectID']:
        raise ValueError(f"Unexpected entry topology in dungeon {dungeon['dungeonID']}")
    result['rooms'] = [dict(roomId=room, entryObjectId=entry['objectID']) for (room, _), entry in zip(rooms, entries)]
    gates = {gate['fromObjectID'] for gate in dungeon.get('connections', [])}
    result['gates'] = []
    for ((_, objects), (destination, _)) in zip(rooms, rooms[1:]):
        gate, = (o['objectID'] for o in objects if o['objectID'] in gates)
        result['gates'].append(dict(fromObjectId=gate, destinationRoomId=destination))
    if {gate['fromObjectId'] for gate in result['gates']} != gates:
        raise ValueError(f"Unbound gate in dungeon {dungeon['dungeonID']}")
    return rooms


def _object(objects, types):
    target, = (obj for obj in objects if obj.get('typeID') in types)
    return target


def _range(anchor, entry):
    return math.dist(tuple(anchor['position'][axis] for axis in ('x', 'y', 'z')),
                     tuple(entry['position'][axis] for axis in ('x', 'y', 'z')))


def _wave(group, room, name, count, npc_group=None, after_mining=False, **flags):
    room_id, objects = room
    entry = _object(objects, {BEACON})
    anchors = [o for o in objects if o.get('entities')]
    if len(anchors) != 1:
        raise ValueError(f'Expected one career encounter anchor in room {room_id}')
    wave = dict(roomId=room_id, groups=[group(name, count, _range(anchors[0], entry), npc_group, **flags)])
    if after_mining:
        wave['trigger'] = 'Mining'
    return wave


def _deposits(result, room, ore):
    _, objects = room
    deposits = [dict(objectId=obj['objectID'], quantity=max(1, round(obj['radius'])))
                for obj in objects if obj.get('typeID') == ore]
    if not deposits:
        raise ValueError(f"Career mission {result['missionId']} has no pinned ore deposits")
    result['careerDeposits'] = sorted(deposits, key=lambda deposit: deposit['objectId'])


def _entrepreneur(group, stage, result, mission, dungeon, rooms):
    rookie = ROOKIES[dungeon['factionID']]
    if stage == 2:
        result['salvageObjectId'] = _object(rooms[-1][1], {CIVILIAN_TRANSPORT_WRECK})['objectID']
        result['waves'] = [_wave(group, rooms[-1], rookie, 1, ROOKIE_GROUP)]
    elif stage == 3:
        _deposits(result, rooms[0], VELDSPAR)
        result['waves'] = [_wave(group, rooms[0], rookie, 1, ROOKIE_GROUP, after_mining=True)]
    elif stage in (4, 7):
        target = _object(rooms[-1][1], DATA_STORAGE_DEVICES if stage == 4 else {ANCIENT_SHIP_STRUCTURE})
        cargo = mission['killMission']
        result['containers'] = [dict(objectId=target['objectID'], typeId=cargo['objectiveTypeID'],
                                     quantity=cargo['objectiveQuantity'], requiresClear=True, analyzer=0 if stage == 4 else 1)]
        result['waves'] = [_wave(group, rooms[-1], rookie, 1 if stage == 4 else 2, ROOKIE_GROUP)]
        if stage == 7:
            result['waves'].insert(0, _wave(group, rooms[-2], rookie, 2, ROOKIE_GROUP, required=False))
    else:
        raise ValueError(f'Unsupported Entrepreneur site at stage {stage}')


def _producer(db, group, stage, result, kill, rooms):
    if stage == 1:
        _deposits(result, rooms[0], VELDSPAR)
        result['mining'] = True
    elif stage == 4:
        _deposits(result, rooms[0], VELDSPAR)
        result['waves'] = [_wave(group, rooms[0], 'Moth Apis', 2, DRONE_MOTH_APIS_GROUP, enemyFaction=ROGUE_DRONE_FACTION)]
    elif stage == 9:
        _deposits(result, rooms[0], KERNITE)
        mission = source(db, 'client/missions', result['missionId'])
        targets = [row['value'] for row in mission['clientObjectives']['contextParameters'] if row['key'] == 'hostile_target']
        target, = targets[0]
        names = db.execute('SELECT Name,GroupId FROM ItemType WHERE Id=?', (target['type_id'],)).fetchall()
        (name, npc_group), = names
        result['waves'] = [_wave(group, rooms[0], name, 1, npc_group, after_mining=True,
                                 lootTypeId=kill['objectiveTypeID'], lootQuantity=kill['objectiveQuantity'])]
    else:
        raise ValueError(f'Unsupported Producer site at stage {stage}')
