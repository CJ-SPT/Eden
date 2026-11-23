"""Original Eden objectives and encounter balance for the pinned Sisters of EVE arc assignments."""

from mission_content import source


ARC_ID = 29
REPORT = 'https://wiki.eveuniversity.org/index.php?title=The_Blood-Stained_Stars&oldid=222549'
REPORT_REVISION = '222549; build-3662168 missions and dungeon object references'

ANGEL = 500011
BLOOD = 500012
GURISTAS = 500010
MORDU = 500018
ROGUE_DRONE = 500025
SERPENTIS = 500020
UNAFFILIATED = 500021

TAR = 30005199
MANARQ = 30005201
COMMANDER_SYSTEMS = {14443: 30005217, 14444: 30005215, 14445: 30005218, 14446: MANARQ}

COURIERS = {
    14134: dict(pickupStationId=60006547, deliveryStationId=60000541),
    14140: dict(pickupStationId=60010723, deliveryStationId=60012607),
    14142: dict(pickupStationId=60010447, deliveryStationId=60012607),
    14386: dict(deliveryStationId=60000118),
    14413: dict(deliveryStationId=60011317),
}

CARGO_RETRIES = {14386: 14139, **{mission: mission - 24 for mission in range(14467, 14471)}}
LOSS_SENSITIVE = {14124, 14135, 14139, 14387, 14388, 14399, 14414, *COMMANDER_SYSTEMS, *CARGO_RETRIES}

# These encounters use Eden's density and reinforcement choices. Existing local catalog
# ships stand in for hidden server-side combat profiles and actor placements.
FRIGATES = {
    ANGEL: ('Gistii Nomad', 597),
    BLOOD: ('Corpii Follower', 606),
    GURISTAS: ('Pithi Invader', 615),
    MORDU: ('Mordus Gladius', 699),
    ROGUE_DRONE: ('Infester Alvi', 805),
    SERPENTIS: ('Coreli Agent', 633),
    UNAFFILIATED: ('Mercenary Rookie', 818),
}


def definitions(db, group):
    arc = source(db, 'client/epicarcs', ARC_ID)
    for key in sorted(arc['missions'], key=int):
        mid = int(key)
        native = source(db, 'client/missions', mid)
        row = dict(missionId=mid, source=REPORT, sourceRevision=REPORT_REVISION, waves=[])
        if mid in LOSS_SENSITIVE:
            row['failOnCargoLoss'] = True
        if mid in CARGO_RETRIES:
            row['cargoLossRetryMissionId'] = CARGO_RETRIES[mid]
        if mid in COURIERS:
            row.update(COURIERS[mid])
        elif 'killMission' in native:
            _encounter(db, group, row, native)
        yield row


def _encounter(db, group, row, native):
    mid = row['missionId']
    objective = native['killMission']
    snapshot = source(db, 'client/dungeons', objective['dungeonID'])
    objects = {int(room): list(data['objects'].values()) for room, data in snapshot['rooms'].items()}
    object_rooms = {body['objectID']: room for room, bodies in objects.items() for body in bodies}
    first_room = object_rooms[snapshot['entryObjectID']]
    gates = [connection['fromObjectID'] for connection in snapshot.get('connections', [])]
    gate_rooms = [object_rooms[gate] for gate in gates]
    room_ids = gate_rooms + sorted(objects.keys() - set(gate_rooms)) if gates else [first_room]
    if len(room_ids) != len(objects) or len(set(room_ids)) != len(objects) or room_ids[0] != first_room:
        raise ValueError(f'Unbound Sisters of EVE room in mission {mid}')
    row['rooms'] = [dict(roomId=room, entryObjectId=_object(objects[room], 10124)) for room in room_ids]
    row['gates'] = [dict(fromObjectId=gate, destinationRoomId=destination, requiresClear=mid == 14390)
                    for gate, destination in zip(gates, room_ids[1:])]
    room = room_ids[-1]

    def ships(faction, count, distance, name=None, npc_group=None, **flags):
        label, category = FRIGATES[faction]
        return group(name or label, count, distance, npc_group=npc_group or category, enemyFaction=faction, **flags)

    def wave(*groups, pocket=room, **trigger):
        row['waves'].append(dict(roomId=pocket, groups=list(groups), **trigger))

    def rescue(target_type, after_combat=False):
        row['containers'] = [dict(objectId=_object(objects[room], target_type), typeId=objective['objectiveTypeID'],
                                  quantity=objective['objectiveQuantity'], requiresClear=after_combat,
                                  completesOnTake=not after_combat)]

    def deliver(target_type, completes=True):
        cargo = native['initialAgentGiftTypeID']
        quantity = native['initialAgentGiftQuantity']
        target = _object(objects[room], target_type)
        row['containers'] = [dict(objectId=target, typeId=0, quantity=0, depositTypeId=cargo,
                                  depositQuantity=quantity, depositCompletesMission=completes)]
        return target

    def recovered_from_enemy(faction, count, **flags):
        return ships(faction, count, 22000, lootTypeId=objective['objectiveTypeID'],
                     lootQuantity=objective['objectiveQuantity'], **flags)

    if mid == 14117:
        row['siteSystemId'] = MANARQ
        row['careerVisit'] = dict(objectId=1049505, range=6000)
    elif mid == 14119:
        row['siteSystemId'] = MANARQ
        wave(ships(BLOOD, 2, 18000))
        wave(recovered_from_enemy(BLOOD, 2, lootToPilotCargo=True))
    elif mid == 14120:
        row['siteSystemId'] = TAR
        wave(ships(BLOOD, 2, 20000))
        wave(recovered_from_enemy(BLOOD, 2, name='Corpii Diviner', lootToPilotCargo=True))
    elif mid == 14122:
        wave(ships(SERPENTIS, 2, 17000))
        wave(recovered_from_enemy(SERPENTIS, 3))
    elif mid == 14123:
        rescue(30760)
        wave(ships(ROGUE_DRONE, 3, 24000, required=False))
    elif mid == 14124:
        target = deliver(objective['dropItemInMissionContainer'], completes=False)
        wave(ships(SERPENTIS, 2, 17000, required=False), recovered_from_enemy(SERPENTIS, 2, lootToPilotCargo=True),
             trigger='delivery', triggerObjectId=target)
    elif mid == 14125:
        rescue(30775)
        wave(ships(ROGUE_DRONE, 5, 22500, required=False))
    elif mid == 14127:
        rescue(30781)
        wave(ships(ROGUE_DRONE, 2, 22000, name='Belphegor Apis', npc_group=806, required=False))
    elif mid == 14128:
        deliver(objective['dropItemInMissionContainer'])
    elif mid == 14129:
        wave(ships(SERPENTIS, 4, 23000))
        wave(ships(SERPENTIS, 2, 25000, name='Coreli Watchman'),
             ships(SERPENTIS, 1, 27000, name='Corelum Chief Scout', npc_group=631))
    elif mid == 14130:
        rescue(30785, after_combat=True)
        wave(ships(ANGEL, 4, 18000))
        wave(ships(ANGEL, 1, 24000, name='Gistum Depredator', npc_group=595), ships(ANGEL, 1, 21000))
    elif mid == 14132:
        row['careerVisit'] = dict(objectId=938974, range=10000)
        wave(ships(ROGUE_DRONE, 4, 32500, required=False))
    elif mid == 14133:
        wave(ships(GURISTAS, 2, 16000), ships(UNAFFILIATED, 2, 20000))
    elif mid == 14135:
        deliver(objective['dropItemInMissionContainer'])
        wave(ships(ROGUE_DRONE, 5, 36000, required=False))
    elif mid == 14137:
        wave(ships(ROGUE_DRONE, 6, 36000))
        wave(ships(ROGUE_DRONE, 2, 28000, name='Render Alvi'),
             ships(ROGUE_DRONE, 1, 31000, name='Wrecker Alvum', npc_group=803))
    elif mid == 14138:
        rescue(30815)
        wave(ships(ROGUE_DRONE, 7, 38000, required=False))
    elif mid == 14139:
        rescue(30813)
        row['preserveObjectiveCargo'] = True
        wave(ships(MORDU, 3, 31000, required=False), ships(MORDU, 1, 34000, name='Mordus Bounty Hunter', required=False))
    elif mid == 14387:
        target = deliver(30820)
        wave(ships(MORDU, 4, 19000, required=False), trigger='delivery', triggerObjectId=target)
    elif mid == 14388:
        target = deliver(30805)
        wave(ships(SERPENTIS, 4, 21000, required=False))
        wave(ships(SERPENTIS, 2, 17000, name='Coreli Watchman', required=False), trigger='delivery', triggerObjectId=target)
    elif mid == 14389:
        row['careerVisit'] = dict(objectId=row['rooms'][-1]['entryObjectId'], range=12000)
    elif mid == 14390:
        wave(ships(ROGUE_DRONE, 4, 23000), pocket=first_room)
        wave(ships(ROGUE_DRONE, 4, 25000), ships(ROGUE_DRONE, 1, 17000, name='Strain Infester Alvi'), pocket=room_ids[1])
        wave(ships(ROGUE_DRONE, 1, 22000, name='Wrecker Alvum', npc_group=803), ships(ROGUE_DRONE, 3, 23000))
    elif mid == 14391:
        row['careerVisit'] = dict(objectId=939019, range=16000)
        wave(ships(ANGEL, 3, 35000, required=False), ships(SERPENTIS, 3, 35000, required=False))
    elif mid == 14393:
        row['structures'] = [dict(objectId=939445, required=True, completesMission=True)]
        wave(ships(GURISTAS, 2, 23000, required=False), ships(SERPENTIS, 2, 24000, required=False))
    elif mid == 14394:
        row['structures'] = [dict(objectId=939432, required=True)]
        wave(ships(UNAFFILIATED, 5, 24000))
    elif mid == 14396:
        wave(ships(UNAFFILIATED, 3, 22000))
        wave(ships(UNAFFILIATED, 2, 21000, name='Mercenary Fighter', required=False),
             ships(UNAFFILIATED, 1, 27000, name='Izia Tabar', npc_group=665, completesMission=True))
    elif mid == 14397:
        wave(ships(UNAFFILIATED, 4, 20000))
        wave(ships(UNAFFILIATED, 2, 20000, name='Mercenary Fighter', required=False),
             ships(UNAFFILIATED, 1, 25000, name='Mercenary Elite Fighter', careerName='Mizara', completesMission=True))
    elif mid == 14399:
        deliver(30777)
    elif mid == 14403:
        rescue(30779, after_combat=True)
        row['containers'][0]['analyzer'] = 1
        wave(ships(BLOOD, 2, 19000, name='Corpii Seeker'), ships(BLOOD, 1, 25000, name='Corpum Arch Reaver', npc_group=604))
    elif mid == 14404:
        wave(recovered_from_enemy(UNAFFILIATED, 5, name='Mercenary Fighter'))
    elif mid == 14406:
        wave(ships(UNAFFILIATED, 1, 17000, name='Independent Green-Crewed Punisher', npc_group=665, careerName='Taphos'))
    elif mid == 14414:
        deliver(30654)
    elif mid == 14415:
        wave(ships(UNAFFILIATED, 3, 21000, required=False),
             ships(UNAFFILIATED, 1, 28000, name='Independent Green-Crewed Vexor', npc_group=678,
                   careerName='Society Spy', completesMission=True))
    elif mid == 14416:
        wave(ships(UNAFFILIATED, 4, 22000, required=False),
             recovered_from_enemy(UNAFFILIATED, 1, name='Mercenary Commander', npc_group=817,
                                  careerName='Kritsan Parthus', completesMission=True))
    elif mid in COMMANDER_SYSTEMS:
        row.update(siteSystemId=COMMANDER_SYSTEMS[mid], preserveObjectiveCargo=True)
        wave(ships(UNAFFILIATED, 2, 22000, name='Mercenary Fighter', required=False),
             recovered_from_enemy(UNAFFILIATED, 1, name='SoCT Gnosis', npc_group=1666, careerName='Dagan', completesMission=True))
    elif mid in (14467, 14468, 14469, 14470):
        row.update(siteSystemId=COMMANDER_SYSTEMS[mid - 24], giftQuantity=0)
        deliver(30899)
    else:
        raise ValueError(f'No Sisters of EVE objective definition for {mid}')


def _object(bodies, type_id):
    object_id, = (body['objectID'] for body in bodies if body['typeID'] == type_id)
    return object_id
