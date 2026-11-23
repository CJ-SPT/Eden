"""Bind empire epic arc objectives to existing catalog identifiers and original Eden encounters."""

from mission_content import source


REPORTS = {
    40: ('Penumbra', 231381),
    48: ('Right_to_Rule', 211175),
    52: ('Syndication', 231650),
    53: ('Wildfire', 229556),
}

# NPC wings are Eden encounter designs. Each tuple is a frigate, cruiser and command ship.
WINGS = {
    500001: (25015, 25043, 12391),
    500002: (24786, 24834, 12389),
    500003: (24910, 24933, 12388),
    500004: (25110, 25147, 12390),
    500007: (24910, 24933, 14356),
    500009: (17116, 17033, 17034),
    500011: (16874, 16875, 16892),
    500012: (16936, 16944, 16934),
    500019: (17059, 17057, 17058),
    500020: (17116, 17033, 17034),
    500021: (11673, 12835, 12464),
    500025: (18079, 10986, 10299),
}

# Factions from the scenario role when a drawer is tagged neutral or has no authored faction.
OPPOSITION = {
    14599: 500001, 14607: 500001, 14610: 500001, 14612: 500001, 14619: 500001,
    14646: 500019, 14648: 500001, 14838: 500019, 14842: 500019, 14861: 500019, 14864: 500019,
    14891: 500009, 14892: 500009, 14893: 500009, 14894: 500009,
    14746: 500025, 14819: 500025, 14820: 500011, 14914: 500003, 14915: 500003,
    14852: 500011, 14854: 500011, 14934: 500011, 14935: 500011, 14972: 500001, 14975: 500001,
}

# These contacts are existing interaction points, referenced by ID without redistributing scene payloads.
CACHE = {
    14623: (951224, None),
    14646: (951719, 0),
    14838: (953965, None),
    14839: (954213, None),
    14885: (955635, 0),
    14886: (955635, 0),
    14852: (955500, None),
    14853: (955447, None),
    14934: (955955, None),
    14935: (956020, None),
    14937: (956505, None),
    14961: (957782, 1),
    14962: (957855, 1),
}

DELIVERY = {
    14552: (950837, 30955),
    14553: (950715, 30955),
    14613: (950870, 30966),
    14842: (954829, 32126),
    14854: (955453, 32204),
    14892: (955417, 32200),
    14896: (955794, 32223),
    14901: (955801, 32225),
    14903: (955949, 32225),
    14950: (958463, 32292),
    14951: (958466, 32292),
    14963: (958074, 32290),
    14967: (958425, 32294),
    14968: (958222, 32294),
}

INSTALLATION = {
    14607: 951209,
    14610: 950770,
    14612: 950770,
    14619: 951209,
    14648: 952947,
    14895: 955342,
}

SCOUT = {14558: 950837, 14559: 951816, 14645: 952201, 14856: 956855}
ALTERNATE_DUNGEONS = {14851: 3906, 14882: 3856, 14884: 3856, 14886: 3962}
SYSTEMS = {14552: 'KFR-ZE', 14553: 'Faurulle', 14558: 'KFR-ZE', 14559: 'Faurulle', 14854: 'Todeko', 14856: 'Alakgur'}
COURIERS = {
    14490: (60001831, 60001825), 14898: (60003985, None), 14900: (60003985, None),
    14973: (60015124, 60002065),
}
LOSS_SENSITIVE_COURIERS = {14490, 14552, 14553, 14613, 14898, 14900, 14901, 14903, 14973}


def definitions(db, _group):
    for arc, (report, revision) in REPORTS.items():
        graph = source(db, 'client/epicarcs', arc)['missions']
        for mission_id in sorted(map(int, graph)):
            mission = source(db, 'client/missions', mission_id)
            scene = dict(missionId=mission_id, waves=[],
                         source=f'https://wiki.eveuniversity.org/index.php?title={report}&oldid={revision}',
                         sourceRevision=f'{revision}; pinned client bindings and original Eden encounters')
            if mission_id in LOSS_SENSITIVE_COURIERS:
                scene['failOnCargoLoss'] = True
            site_system = SYSTEMS.get(mission_id)
            if site_system:
                scene['siteSystemId'], = db.execute('SELECT Id FROM SolarSystem WHERE Name=?', (site_system,)).fetchone()
            if mission_id in COURIERS:
                pickup, destination = COURIERS[mission_id]
                scene['pickupStationId'] = pickup
                if destination is None:
                    scene['deliverToAgent'] = True
                else:
                    scene['deliveryStationId'] = destination
            if mission_id in ALTERNATE_DUNGEONS:
                scene['dungeonId'] = ALTERNATE_DUNGEONS[mission_id]
            dungeon_id = scene.get('dungeonId', mission.get('killMission', {}).get('dungeonID'))
            if dungeon_id is not None:
                dungeon = source(db, 'client/dungeons', dungeon_id)
                bind_combat(scene, mission, dungeon, db)
            yield scene


def bind_rooms(scene, dungeon):
    entries = {}
    object_rooms = {}
    for key, room in dungeon['rooms'].items():
        rid = int(key)
        objects = room['objects'].values()
        for obj in objects:
            object_rooms[obj['objectID']] = rid
        entries[rid] = next(obj['objectID'] for obj in objects
                            if obj['objectID'] == dungeon['entryObjectID'] or obj.get('typeID') == 10124)
    first = object_rooms[dungeon['entryObjectID']]
    rooms = [first, *sorted(rid for rid in entries if rid != first)]
    entries[first] = dungeon['entryObjectID']
    scene['rooms'] = [dict(roomId=room, entryObjectId=entries[room]) for room in rooms]
    by_source = {}
    for edge in dungeon.get('connections', []):
        gid = edge['fromObjectID']
        by_source.setdefault(object_rooms[gid], []).append(gid)
    scene['gates'] = []
    # The local snapshot stores the departure object. Follow its explicit target when present,
    # otherwise use stable room order while preserving side-room and return links.
    for i, room in enumerate(rooms):
        for j, gid in enumerate(sorted(by_source.get(room, []))):
            offset = (i + j + 1) % len(rooms)
            if rooms[offset] == room:
                offset = (offset + 1) % len(rooms)
            scene['gates'].append(dict(fromObjectId=gid, destinationRoomId=rooms[offset]))
    return rooms, object_rooms


def bind_combat(scene, mission, dungeon, db):
    mid = scene['missionId']
    rooms, object_rooms = bind_rooms(scene, dungeon)
    cargo = mission.get('killMission', mission.get('courierMission', {}))
    cargo_type = cargo.get('objectiveTypeID', 0)
    quantity = cargo.get('objectiveQuantity', 0)
    if mid == 14853:
        cargo_type, quantity = 32204, 1
        scene.update(objectiveTypeId=cargo_type, objectiveQuantity=quantity, preserveObjectiveCargo=True)
    if mid == 14854:
        scene.update(failOnCargoLoss=True, cargoLossRetryMissionId=14853)

    contact = (CACHE.get(mid) or DELIVERY.get(mid) or (INSTALLATION.get(mid),))[0] or SCOUT.get(mid)
    if mid == 14886 and contact not in object_rooms:
        contact = next(obj['objectID'] for obj in dungeon['rooms'][str(rooms[-1])]['objects'].values()
                       if obj.get('typeID') == 32217)
    objective_room = object_rooms[contact] if contact is not None else rooms[-1]
    if mid in DELIVERY:
        scene['containers'] = [dict(objectId=contact, typeId=0, quantity=0,
                                    depositTypeId=DELIVERY[mid][1], depositQuantity=1)]
    elif mid in CACHE:
        _, analyzer = CACHE[mid]
        cache = dict(objectId=contact, typeId=cargo_type, quantity=quantity, requiresClear=mid != 14853)
        if analyzer is not None:
            cache['analyzer'] = analyzer
        scene['containers'] = [cache]
        if mid == 14853:
            scene['careerVisit'] = dict(objectId=contact, range=2500)
    elif mid in INSTALLATION:
        scene['structures'] = [dict(objectId=contact, required=True, completesMission=True)]
    elif mid in SCOUT:
        scene['careerVisit'] = dict(objectId=contact, range=2500)

    if mid in (14854, 14856, 14896, 14950, 14951, 14967, 14968):
        return

    faction = OPPOSITION.get(mid, dungeon.get('factionID') or 500021)
    faction = faction if faction in WINGS else 500021
    frigate, cruiser, command = WINGS[faction]
    for type_id in (frigate, cruiser, command):
        if db.execute('SELECT Id FROM ItemType WHERE Id=?', (type_id,)).fetchone() is None:
            raise ValueError(f'{mid}: invalid encounter NPC {type_id}')

    def enemy(type_id, count, range_m, **flags):
        return dict(types=[type_id], count=count, range=range_m, enemyFaction=faction, **flags)

    leading = [room for room in rooms if room != objective_room and
               any(o.get('entities') for o in dungeon['rooms'][str(room)]['objects'].values())]
    for room in leading:
        scene['waves'].append(dict(roomId=room, groups=[enemy(frigate, 2, 28000), enemy(cruiser, 2, 35000)]))

    guarded_object = mid in DELIVERY or mid in SCOUT or mid == 14853
    scene['waves'].append(dict(roomId=objective_room, groups=[
        enemy(frigate, 3, 26000, required=not guarded_object),
        enemy(cruiser, 3, 38000, required=not guarded_object),
    ]))
    if guarded_object:
        return
    final = enemy(command, 2, 44000)
    if cargo_type and mid not in CACHE:
        final.update(count=1, completesMission=True, lootTypeId=cargo_type, lootQuantity=quantity)
    scene['waves'].append(dict(roomId=objective_room, groups=[final]))
    for gate in scene['gates']:
        gate['requiresClear'] = object_rooms[gate['fromObjectId']] in leading
