"""Additional report-backed encounters and exhaustive, fail-closed delivery admission.

Level bands, reward weights and offer order are Eden reconstruction policy. The
client does not publish agent-level assignments or NPC grouping contents.
"""
import csv
import copy
import json
import re
from collections import Counter

# Build-3662168 destructible Habitat; the similarly named scenery is not an objective.
HABITAT_TYPE = 19715

KNOWN_BLOCKERS = {
    895: 'Requires timed reinforcements',
    929: 'Requires a verified reinforcement trigger',
    934: 'Requires a verified reinforcement trigger',
    2252: 'Report and pinned dungeon level disagree',
    2253: 'Requires a verified reinforcement trigger',
}

# Reviewed standalone mining pockets. The other mining dungeons contain
# scripted encounters, chain stages or duplicate content for the same pocket.
MINING_MISSIONS = {
    4800, 4801, 4802, 4803, 4804, 4805, 4806, 4807, 4808, 4809, 4810, 4811, 4812, 4813, 4814,
    4859, 4862, 4863, 4864, 4865, 4866, 4884, 13822,
}


def source(db, dataset, key):
    row = db.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?', (dataset, str(key))).fetchone()
    return json.loads(row[0]) if row else None


def delivery_rejection(mission, name, volume):
    template = mission['contentTemplate'].rsplit('_', 1)[-1]
    if template not in ('BasicCourierMission', 'GenericStorylineCourierMission'):
        return 'Requires another mission or agent lifecycle'
    if re.search(r'\b\d+\s+of\s+\d+\b', name, re.IGNORECASE) or any('nextmission' in k for k in mission.get('messages', {})):
        return 'Requires mission-chain progression'
    if any(mission.get(k) for k in ('nodeGraphID', 'clientObjectives', 'contentTags', 'initialAgentGiftTypeID')):
        return 'Requires authored scripts, objectives or initial gifts'
    cargo = mission.get('courierMission', {})
    if cargo.get('objectiveSingleton'):
        return 'Requires singleton objective inventory'
    if not cargo.get('objectiveTypeID') or not 0 < cargo.get('objectiveQuantity', 0) <= 2147483647 or volume <= 0:
        return 'Missing usable cargo definition'
    expiration = mission.get('expirationTime', 10080)
    if type(expiration) is not int or not 0 < expiration <= 10080:
        return 'Requires another mission deadline lifecycle'
    for reward in mission.get('missionRewards', {}).values():
        if isinstance(reward, dict) and reward.get('rewardTypeID', 29) != 29:
            return 'Requires fixed item rewards'
    return None


def mining_definition(db, mission_id, mission):
    objective = mission['killMission']
    dungeon = source(db, 'client/dungeons', objective['dungeonID'])
    level = dungeon['difficulty']
    rooms = dungeon['rooms']
    if not 1 <= level <= 4 or len(rooms) != 1 or dungeon.get('connections'):
        raise ValueError(f'Mining mission {mission_id} needs unsupported room or level handling')
    objects = next(iter(rooms.values()))['objects'].values()
    rocks = [o for o in objects if o.get('typeID') == objective['objectiveTypeID']]
    if (not rocks or any(o.get('entities') for o in objects)
            or any(not o.get('position') or not 0 < o.get('radius', 0) == int(o['radius']) for o in rocks)
            or sum(o['radius'] for o in rocks) != objective['objectiveQuantity']):
        raise ValueError(f'Mining mission {mission_id} needs verified deposits or encounters')
    return dict(missionId=mission_id, level=level, difficulty=500, storyline=False, mining=True, deadspace=False,
                source='build-3662168 client/missions and client/dungeons; Eden mining reward policy',
                sourceRevision='3662168', waves=[])


def extend(db, missions, group):
    def encounter(mid, faction, title, revision, groups, entry=None, warehouse=False, delivery=False, deadspace=True,
                  habitat=False):
        mission = source(db, 'client/missions', mid)
        dungeon = source(db, 'client/dungeons', mission['killMission']['dungeonID'])
        if dungeon['factionID'] != faction:
            raise ValueError(f'Wrong report faction for mission {mid}')
        rooms = dungeon['rooms']
        definition = dict(missionId=mid, level=dungeon['difficulty'], difficulty=500, storyline=False,
                          source='https://wiki.eveuniversity.org/' + title, sourceRevision=f'oldid={revision}',
                          offerOrder=1, deadspace=deadspace, waves=[dict(groups=groups)])
        if entry:
            lobby = next(int(rid) for rid, room in rooms.items()
                         if any(o['objectID'] == dungeon['entryObjectID'] for o in room['objects'].values()))
            destination = next(int(rid) for rid, room in rooms.items()
                               if any(o['objectID'] == entry for o in room['objects'].values()))
            if len(rooms) != 2 or lobby == destination or len(dungeon['connections']) != 1:
                raise ValueError(f'Unreviewed gate topology for mission {mid}')
            gate = dungeon['connections'][0]['fromObjectID']
            if not any(o['objectID'] == gate for o in rooms[str(lobby)]['objects'].values()):
                raise ValueError(f'Wrong entrance gate for mission {mid}')
            definition['rooms'] = [dict(roomId=lobby, entryObjectId=dungeon['entryObjectID']),
                                   dict(roomId=destination, entryObjectId=entry)]
            definition['gates'] = [dict(fromObjectId=gate, destinationRoomId=destination)]
            definition['waves'][0]['roomId'] = destination
        elif len(rooms) != 1 or dungeon.get('connections'):
            raise ValueError(f'Missing room binding for mission {mid}')
        if warehouse:
            structures = [o for r in rooms.values() for o in r['objects'].values()
                          if db.execute('SELECT GroupId FROM ItemType WHERE Id=?', (o['typeID'],)).fetchone()[0] == 319]
            targets = [o for o in structures if o['typeID'] == 17787]
            if len(targets) != 1:
                raise ValueError(f'Unreviewed warehouse objective for mission {mid}')
            target = targets[0]['objectID']
            definition['structures'] = [dict(objectId=target, required=True, completesMission=True,
                                             drop=dict(typeId=mission['killMission']['objectiveTypeID'],
                                                       quantity=mission['killMission']['objectiveQuantity'])),
                                        *[dict(objectId=o['objectID']) for o in structures if o['objectID'] != target]]
        if delivery:
            carriers = [g for g in groups if g.get('required', True)]
            if len(carriers) != 1:
                raise ValueError('One explicit objective carrier group is required')
            carriers[0].update(lootTypeId=mission['killMission']['objectiveTypeID'],
                               lootQuantity=mission['killMission']['objectiveQuantity'])
        if habitat:
            # Build 3662168 uses the overseer structure type, not decorative habitation scenery.
            targets = [o for r in rooms.values() for o in r['objects'].values() if o['typeID'] == HABITAT_TYPE]
            if len(targets) != 1 or mission['killMission'].get('objectiveTypeID'):
                raise ValueError(f'Unreviewed habitat objective for mission {mid}')
            definition['structures'] = [dict(objectId=targets[0]['objectID'], required=True, completesMission=True)]
        missions.append(definition)

    hidden = [
        (1227, 500020, 'Serpentis', 1, 192024, 590136, [('Coreli Initiate', 12, 15000)]),
        (1228, 500010, 'Guristas_Pirates', 1, 192019, 590184, [('Pithi Arrogator/Pithi Imputor', 5, 20000)]),
        (1229, 500012, 'Blood_Raiders', 1, 192017, 590232, [('Corpii Upholder/Corpii Herald', 4, 12000)]),
        (1230, 500019, "Sansha's_Nation", 1, 192022, 590280, [('Centii Servant', 12, 20000)]),
        (1231, 500011, 'Angel_Cartel', 2, 229335, 84372,
         [('Gistior Defacer/Gistior Haunter', 2, 20000), ('Gistum Breaker/Gistum Defeater', 1, 25000)]),
        (1232, 500010, 'Guristas_Pirates', 2, 192021, 590088,
         [('Pithi Despoiler/Pithi Saboteur', 5, 20000), ('Pithum Killer', 1, 25000)]),
        (1233, 500020, 'Serpentis', 2, 192025, 590326, [('Corelior Soldier/Corelior Infantry', 5, 20000)]),
        (1234, 500012, 'Blood_Raiders', 2, 192018, 590376,
         [('Corpii Seeker/Corpii Collector', 2, 20000), ('Corpum Arch Engraver', 1, 25000)]),
        (1235, 500019, "Sansha's_Nation", 2, 192023, 590424,
         [('Centii Manslayer/Centii Butcher/Centii Slavehunter/Centii Savage', 7, 20000)]),
    ]
    for mid, faction, variant, level, revision, entry, roster in hidden:
        encounter(mid, faction, f'The_Hidden_Stash_({variant})_(Level_{level})', revision,
                  [group(*g, required=False) for g in roster], entry, warehouse=True)

    duo = [
        (1238, 500011, 'Angel_Cartel', 3, 203789, 62932, 'Gistatis Legatus/Gistatis Tribunus', 50000, []),
        (1239, 500011, 'Angel_Cartel', 4, 209368, 115038, 'Gist Seraphim', 50000,
         [('Spider Drone I', 2, 50000), ('Angel Heavy Missile Battery', 4, 60000)]),
        (1240, 500020, 'Serpentis', 3, 191665, 63073, 'Corelatis Captain/Corelatis High Captain', 18000, []),
        (1241, 500020, 'Serpentis', 4, 191666, 115157, 'Core Lord Admiral', 25000,
         [('Spider Drone II', 2, 15000), ('Tower Sentry Serpentis III', 3, 63000)]),
        (1242, 500012, 'Blood_Raiders', 3, 191659, 63605, 'Corpatis Exorcist/Corpatis Phantom', 20000, []),
        (1243, 500012, 'Blood_Raiders', 4, 226697, 115212, 'Corpus Pope', 55000, [('Spider Drone I', 6, 55000)]),
        (1245, 500010, 'Guristas_Pirates', 3, 191661, 63291,
         'Pithatis Death Dealer/Pithatis Revolter', 55000, [('Tower Sentry Guristas II', 2, 55000)]),
        (1246, 500010, 'Guristas_Pirates', 4, 215960, 115070, 'Pith Usurper', 18000,
         [('Spider Drone I', 2, 18000), ('Guristas Cruise Missile Battery', 3, 18000)]),
        (1247, 500019, "Sansha's_Nation", 3, 191663, 184243, 'Centatis Daemon/Centatis Behemoth', 18000,
         [('Spider Drone I', 2, 18000), ('Tower Sentry Sansha I', 2, 18000)]),
        (1254, 500019, "Sansha's_Nation", 4, 229683, 184197, 'Centus Tyrant', 50000,
         [('Spider Drone I', 2, 50000), ('Tower Sentry Sansha III', 4, 75000)]),
    ]
    for mid, faction, variant, level, revision, entry, targets, distance, escorts in duo:
        encounter(mid, faction, f'Duo_of_Death_({variant})_(Level_{level})', revision,
                  [group(targets, 2, distance), *[group(*g, required=False) for g in escorts]], entry, delivery=True)

    encounter(917, 500012, 'Eliminate_a_Pirate_Nuisance_(Blood_Raiders)', 191668,
              [group('Splinter Alvi', 1, 5000, npc_group=805, enemyFaction=500025),
               group('Corpii Seeker/Corpii Follower', 2, 5000)], 53230)
    encounter(918, 500019, "Eliminate_a_Pirate_Nuisance_(Sansha's_Nation)", 218038,
              [group('Belphegor Apis/Mammon Apis', 2, 25000, enemyFaction=500025),
               group('Infester Alvi', 1, 25000, npc_group=805, enemyFaction=500025),
               group('Centii Savage/Centii Servant/Centii Slavehunter', 2, 50000)], 46891)
    encounter(919, 500020, 'Eliminate_a_Pirate_Nuisance_(Serpentis)', 191671,
              [group('Coreli Initiate', 2, 4000), group('Coreli Agent/Coreli Spy', 2, 40000)], 53120)
    encounter(894, 500020, 'The_Serpentis_Spies_(Level_2)', 192058,
              [group('Coreli Agent/Coreli Initiate/Coreli Safeguard', 5, 20000), group('Coreli Spy', 3, 70000)], 53179)
    encounter(2248, 500019, "Eliminate_the_Pirate_Campers_(Sansha's_Nation)", 203023,
              [group('Centii Servant/Centii Minion', 6, 10000)], deadspace=False)
    encounter(2249, 500020, 'Eliminate_the_Pirate_Campers_(Serpentis)', 203018,
              [group('Coreli Initiate/Coreli Scout/Coreli Spy/Coreli Agent', 4, 10000)], deadspace=False)
    encounter(2254, 500015, 'Eliminate_the_Pirate_Campers_(Thukker_tribe)', 191677,
              [group('Thukker Mercenary Fighter', 3, 10000), group('Thukker Mercenary Elite Fighter', 4, 10000)], deadspace=False)
    encounter(2255, 500011, 'Eliminate_the_Pirate_Campers_(Angel_Cartel)', 203561,
              [group('Gistior Defacer/Gistior Shatterer', 5, 5000),
               group('Gistum Depredator/Gistum Predator/Gistum Smasher/Gistum Crusher', 3, 5000)], deadspace=False)

    seek = [
        (931, 500011, 'Angel_Cartel', 1, 191907, 758908,
         [('Gistii Hijacker', 6, 55000, True), ('Gistii Hijacker', 6, 95000, False)]),
        (926, 500011, 'Angel_Cartel', 2, 191908, 758919,
         [('Gistii Ambusher/Gistii Raider', 4, 30000, False), ('Gistior Shatterer/Gistior Defacer', 1, 30000, False),
          ('Gistii Hijacker/Gistii Rogue', 5, 67000, True), ('Gistum Depredator/Gistum Predator', 1, 67000, True)]),
        (928, 500012, 'Blood_Raiders', 1, 191910, 758933,
         [('Corpii Follower', 5, 35000, True), ('Corpii Follower', 4, 70000, False)]),
        (922, 500010, 'Guristas_Pirates', 1, 191913, 776444,
         [('Pithi Arrogator', 3, 20000, True), ('Pithi Arrogator', 3, 40000, True)]),
        (923, 500010, 'Guristas_Pirates', 2, 191914, 758905,
         [('Pithi Wrecker/Pithi Plunderer', 4, 30000, True), ('Pithum Ascriber/Pithum Silencer', 1, 30000, True)]),
        (925, 500019, "Sansha's_Nation", 1, 219387, 758948,
         [('Centii Servant', 4, 35000, True), ('Centii Servant', 5, 65000, False)]),
        (932, 500019, "Sansha's_Nation", 2, 191917, 758955,
         [('Centii Butcher/Centii Manslayer', 4, 20000, False), ('Centii Ravener/Centii Scavenger', 3, 50000, True),
          ('Centum Ravager/Centum Ravisher', 1, 50000, True)]),
        (935, 500020, 'Serpentis', 2, 191920, 758969,
         [('Coreli Guard/Coreli Safeguard', 4, 25000, False),
          ('Coreli Guard/Coreli Safeguard/Coreli Protector/Coreli Defender', 4, 75000, True)]),
    ]
    for mid, faction, variant, level, revision, entry, roster in seek:
        encounter(mid, faction, f'Seek_and_Destroy_({variant})_(Level_{level})', revision,
                  [group(names, count, distance, required=required) for names, count, distance, required in roster], entry)

    # Independently bind the report facts to the pinned catalog. The evejs packs
    # select these reports but label their generic kill-all completion provisional.
    # These variants have no reported reinforcement triggers; other variants stay blocked.
    for mid, entry in [(1180, 55664), (1181, 55688)]:
        encounter(mid, 500011, 'Avenge_a_Fallen_Comrade_(Angel_Cartel)_(Level_1)', 209636,
                  [group('Gistii Hijacker', 2, 2000, required=False),
                   group('Gistii Hijacker/Gistii Rogue', 3, 50000, required=False),
                   group('Gistii Raider/Gistii Ambusher', 1, 60000, required=False),
                   group('Gistii Hijacker', 8, 60000, required=False)], entry, habitat=True)
    encounter(1177, 500010, 'Avenge_a_Fallen_Comrade_(Guristas_Pirates)_(Level_2)', 203235,
              [group('Pithi Arrogator/Pithi Imputor', 4, 10000, required=False),
               group('Pithi Infiltrator/Pithi Invader', 5, 40000, required=False),
               group('Pithior Nihilist', 1, 40000, required=False),
               group('Pithi Infiltrator/Pithi Invader', 3, 40000, required=False),
               group('Pithum Silencer', 1, 40000, required=False),
               group('Pithi Despoiler/Pithi Saboteur', 5, 60000, required=False)], 595380, habitat=True)
    encounter(1179, 500020, 'Avenge_a_Fallen_Comrade_(Serpentis)_(Level_2)', 193385,
              [group('Coreli Safeguard', 2, 2000, required=False),
               group('Coreli Initiate', 4, 40000, required=False),
               group('Corelior Soldier', 1, 40000, required=False),
               group('Coreli Agent/Coreli Spy', 3, 50000, required=False),
               group('Corelum Chief Patroller', 1, 50000, required=False),
               group('Coreli Watchman/Coreli Patroller', 7, 25000, required=False)], 55616, habitat=True)
    encounter(1200, 500006, 'Avenge_a_Fallen_Comrade_(DED)_(Level_1)', 191580,
              [group('DED Soldier 3rd Class', 6, 25000)], 56845)
    encounter(1201, 500006, 'Avenge_a_Fallen_Comrade_(DED)_(Level_2)', 191581,
              [group('DED Soldier 1st Class/DED Soldier 3rd Class', 7, 45000, required=False),
               group('DED Special Ops Piranha', 4, 55000, required=False),
               group('DED Soldier 2nd Class', 4, 55000, required=False),
               group('DED Officer 1st Class', 1, 55000, required=False)], 57908, habitat=True)

    admitted = {m['missionId'] for m in missions}
    encounters = {tuple(sorted(source(db, 'client/missions', m['missionId'])['killMission'].items())): m
                  for m in missions if m['waves']}
    audit = []
    for key, raw in db.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='client/missions' ORDER BY CAST(Key AS INTEGER)"):
        mid, data = int(key), json.loads(raw)
        sde = source(db, 'sde/missions', mid)
        name = sde.get('name', {}).get('en', str(mid))
        template = data['contentTemplate'].rsplit('_', 1)[-1]
        if mid in KNOWN_BLOCKERS:
            reason = KNOWN_BLOCKERS[mid]
        elif mid in admitted:
            reason = None
        elif template == 'BasicMiningMission':
            if mid in MINING_MISSIONS:
                missions.append(mining_definition(db, mid, data))
                reason = None
            else:
                reason = 'Requires verified mining scripts, chain progression or a distinct pocket'
        elif 'CourierMission' in template:
            cargo = data.get('courierMission', {})
            item = source(db, 'client/types', cargo.get('objectiveTypeID', 0)) or {}
            volume = item.get('volume', 0) * cargo.get('objectiveQuantity', 0)
            reason = delivery_rejection(data, name, volume)
            if reason is None:
                level = 1 if volume <= 320 else 2 if volume <= 1000 else 3 if volume <= 5000 else 4
                missions.append(dict(missionId=mid, level=level, difficulty=500,
                                     storyline=template == 'GenericStorylineCourierMission', offerOrder=1,
                                     source='build-3662168 client/missions; Eden delivery volume-band policy',
                                     sourceRevision='3662168', waves=[]))
        elif template == 'BasicKillMission':
            dungeon = source(db, 'client/dungeons', data.get('killMission', {}).get('dungeonID', 0)) or {}
            equivalent = encounters.get(tuple(sorted(data.get('killMission', {}).items())))
            special = any(data.get(k) for k in ('nodeGraphID', 'clientObjectives', 'contentTags', 'initialAgentGiftTypeID'))
            chain = re.search(r'\b\d+\s+of\s+\d+\b', name, re.IGNORECASE) or any('nextmission' in k for k in data.get('messages', {}))
            if equivalent and not special and not chain:
                definition = copy.deepcopy(equivalent)
                definition.update(missionId=mid, offerOrder=1)
                missions.append(definition)
                reason = None
            else:
                reason = ('Requires level-five encounter policy' if dungeon.get('difficulty') == 5
                          else 'Requires mission-chain progression' if chain
                          else 'Requires authored scripts or initial gifts' if special
                          else 'Requires verified NPC roster and objective scripts')
        else:
            reason = 'Requires another mission or agent lifecycle'
        audit.append(dict(missionId=mid, name=name, template=template, status='blocked' if reason else 'admitted',
                          reason=reason or ('Catalog mining deposits' if template == 'BasicMiningMission'
                                            else 'Catalog cargo or reviewed encounter')))
    return audit


def write_audit(path, audit):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('w', encoding='utf-8', newline='') as stream:
        writer = csv.DictWriter(stream, fieldnames=['missionId', 'name', 'template', 'status', 'reason'], lineterminator='\n')
        writer.writeheader()
        writer.writerows(audit)
    print('Mission audit:', dict(Counter(row['status'] for row in audit)))
