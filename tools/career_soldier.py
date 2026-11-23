"""Soldier of Fortune report objectives bound to the pinned room objects and trained module effects."""
from mission_content import source


FIRST = 15102
LAST = 15141
REPORT = 'https://wiki.eveuniversity.org/index.php?title=Soldier_of_Fortune&oldid=224310'
PIRATE_FRIGATES = {500010: 'Pithi Arrogator', 500011: 'Gistii Hijacker', 500012: 'Corpii Follower', 500020: 'Coreli Initiate'}
HIDEOUTS = {4072: 961127, 4102: 962162, 4103: 962436, 4104: 962439}


def definitions(db, group):
    results = []
    for mid in range(FIRST, LAST + 1):
        native = source(db, 'client/missions', mid)
        dungeon_id = native['killMission']['dungeonID']
        dungeon = source(db, 'client/dungeons', dungeon_id)
        entry = dungeon['entryObjectID']
        rooms = dungeon['rooms']
        lobby = next(int(r) for r, data in rooms.items() if any(o['objectID'] == entry for o in data['objects'].values()))
        pocket = next((int(r) for r in rooms if int(r) != lobby), lobby)
        definition = dict(missionId=mid, source=REPORT, sourceRevision='224310; build-3662168 client/dungeons', waves=[])
        if pocket != lobby:
            beacon = next(o['objectID'] for o in rooms[str(pocket)]['objects'].values() if o.get('typeID') == 10124)
            definition['rooms'] = [dict(roomId=lobby, entryObjectId=entry), dict(roomId=pocket, entryObjectId=beacon)]
            link, = dungeon['connections']
            definition['gates'] = [dict(fromObjectId=link['fromObjectID'], destinationRoomId=pocket)]

        def wave(*groups):
            definition['waves'].append(dict(roomId=pocket, groups=list(groups)))

        rat = PIRATE_FRIGATES.get(dungeon['factionID'])
        if mid <= 15105:
            for count in (1, 2, 3):
                wave(group(rat, count, 13000))
        elif mid <= 15109:
            wave(group(rat, 3, 15000, required=False))
            definition.update(careerSacrifice=True,
                              careerVisit=dict(objectId=HIDEOUTS[dungeon_id], range=2500, removeObject=True))
        elif mid <= 15113:
            wave(dict(types=[2010], count=1, range=31000, required=False, careerPassive=True, careerName='Fleeing Pirate'))
            definition['careerEffect'] = dict(typeId=2010, effect=39)
        elif mid <= 15117:
            effect = 6188 if dungeon_id == 4173 else 6186
            wave(dict(types=[32471], count=1, range=10000, required=False, careerPassive=True, careerName='Damaged Vessel'))
            definition['careerEffect'] = dict(typeId=32471, effect=effect)
        elif mid <= 15121:
            wave(group('Mercenary Fighter', 1, 24000))
            wave(group('Spider Drone II', 3, 22000), dict(types=[25181], count=10, range=30000))
            definition['careerShipLoss'] = True
        elif mid <= 15125:
            # The range and raw EM pulse come from the recorded gate defense, not dungeon geometry alone.
            definition.update(careerVisit=dict(objectId=968541, range=6800), careerHazard=dict(
                objectId=968533, range=28800, electromagneticDamage=50, intervalSeconds=6))
        elif mid <= 15129:
            wave(group(rat, 1, 10000))
        elif mid <= 15133:
            for _ in range(2):
                wave(group('Mercenary Fighter', 3, 20000, required=False))
            definition.update(careerRescue=True, containers=[dict(objectId=968438, typeId=2013, quantity=10)])
        elif mid <= 15137:
            wave(dict(types=[2010], count=1, range=31000, required=False, careerPassive=True, careerName='Pirate Leader'))
            definition['careerEffect'] = dict(typeId=2010, effect=6426)
        else:
            wave(dict(types=[2012], count=1, range=37000, completesMission=True),
                 dict(types=[2052], count=2, range=37000, required=False))
        results.append(definition)
    return results
