"""Bind the Explorer career assignments to their pinned training pockets and objectives."""

import json
import re


SOURCE = 'https://wiki.eveuniversity.org/index.php?title=Explorer&oldid=221957'
EMPIRES = {500001, 500002, 500003, 500004}
TOUR = 4082
TOUR_ROOMS = ((22052, 961260), (22053, 961261), (22056, 961264), (22055, 961263), (22057, 961265))
TOUR_GATES = ((961266, 22053), (961267, 22056), (961270, 22055), (961269, 22057))
TRAINING_SITES = {
    # Part: dungeon, scanner kind, objective container object and type, proof type, analyzer kind.
    1: (4087, 1, 961792, 32353, 32350, None),
    3: (4092, 2, 961924, 32369, 32366, 0),
    4: (4091, 3, 961926, 32367, 32365, 1),
    5: (4093, 4, 962128, 32379, 32376, None),
}
GAS_KEY = 32375


def _read(db, dataset, key):
    row = db.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?', (dataset, str(key))).fetchone()
    if not row:
        raise ValueError(f'Missing Explorer catalog source {dataset}/{key}')
    return json.loads(row[0])


def _container(dungeon, object_id, expected_type):
    matches = [item for room in dungeon['rooms'].values() for item in room['objects'].values()
               if item['objectID'] == object_id and item['typeID'] == expected_type]
    if len(matches) != 1:
        raise ValueError(f'Explorer dungeon {dungeon["dungeonID"]} has no expected container {object_id}')


def _site(db, part, mission):
    dungeon_id, scan, object_id, container_type, proof, analyzer = TRAINING_SITES[part]
    dungeon = _read(db, 'client/dungeons', dungeon_id)
    _container(dungeon, object_id, container_type)
    cargo = mission['courierMission']
    if (cargo['objectiveTypeID'], cargo['objectiveQuantity'], cargo['objectiveSingleton']) != (proof, 1, 0):
        raise ValueError(f'Explorer part {part} objective disagrees with the pinned mission')
    container = dict(objectId=object_id, typeId=proof, quantity=1)
    if analyzer is not None:
        container['analyzer'] = analyzer
    result = dict(dungeonId=dungeon_id, scan=scan, containers=[container])
    if part == 5:
        if mission.get('initialAgentGiftTypeID') != GAS_KEY:
            raise ValueError('Explorer gas assignment is missing its catalog passkey gift')
        result.update(
            rooms=[dict(roomId=22074, entryObjectId=961928), dict(roomId=22077, entryObjectId=961991)],
            gates=[dict(fromObjectId=961990, destinationRoomId=22077, key=dict(typeId=GAS_KEY, quantity=1, consume=False))],
        )
    return result


def _tour(db, mission):
    if mission.get('killMission', {}).get('dungeonID') != TOUR:
        raise ValueError('The Explorer tour does not use its pinned dungeon')
    dungeon = _read(db, 'client/dungeons', TOUR)
    _container(dungeon, 961271, 32347)
    if {gate['fromObjectID'] for gate in dungeon['connections']} != {gate for gate, _ in TOUR_GATES}:
        raise ValueError('The Explorer tour gate records have changed')
    return dict(
        rooms=[dict(roomId=room, entryObjectId=entry) for room, entry in TOUR_ROOMS],
        gates=[dict(fromObjectId=gate, destinationRoomId=destination) for gate, destination in TOUR_GATES],
        careerVisit=dict(objectId=961265, range=2500),
        containers=[dict(objectId=961271, typeId=17938, quantity=1, additionalItems=[
            dict(typeId=30013, quantity=16), dict(typeId=27014, quantity=1), dict(typeId=27019, quantity=1),
        ])],
    )


def definitions(db, _group):
    """Derive ordered missions from native titles and agent bindings, excluding obsolete sites."""
    result = []
    ordered = {}
    rows = db.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='client/missions' "
                      "AND EXISTS(SELECT 1 FROM json_each(Data, '$.contentTags') WHERE value='career_path_explorer')")
    for key, raw in rows:
        mission = json.loads(raw)
        title = _read(db, 'client/localization_fsd_en-us', mission['nameID'])['text']
        part = re.search(r'\(([1-5]) of 5\)$', title)
        if not part or mission.get('factionID') not in EMPIRES:
            raise ValueError(f'Unrecognized Explorer mission {key}: {title}')
        index = int(part[1])
        faction = mission['factionID']
        if (faction, index) in ordered:
            raise ValueError(f'Duplicate Explorer mission for faction {faction} part {index}')
        ordered[faction, index] = dict(missionId=int(key), waves=[], source=SOURCE,
                                       sourceRevision='3662168; EVE University oldid=221957',
                                       **(_tour(db, mission) if index == 2 else _site(db, index, mission)))
    for faction in sorted(EMPIRES):
        result.extend(ordered[faction, part] for part in range(1, 6))
    return result
