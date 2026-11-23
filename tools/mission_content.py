"""Additional report-backed encounters and exhaustive, fail-closed delivery admission.

Level bands, reward weights and offer order are Eden reconstruction policy. The
client does not publish agent-level assignments or NPC grouping contents.
"""
import csv
import copy
import json
import re
from collections import Counter
from pathlib import Path
from mission_fixed_agents import data_center_definitions

# Build-3662168 destructible Habitat; the similarly named scenery is not an objective.
HABITAT_TYPE = 19715
# Build-3662168 item group for the five basic attribute implant families.
ATTRIBUTE_ENHANCER_GROUP = 745
# Explicit Eden placement hint when a report supplies no NPC-group distances.
UNREPORTED_GROUP_RANGE = 25000

KNOWN_BLOCKERS = {
    895: 'Requires timed reinforcements',
    929: 'Requires a verified reinforcement trigger',
    934: 'Requires a verified reinforcement trigger',
    1215: 'Requires a verified reinforcement trigger',
    2252: 'Report and pinned dungeon level disagree',
    2253: 'Requires a verified reinforcement trigger',
    15552: 'Requires level-five storyline reward policy',
}

# Reviewed standalone mining pockets. The other mining dungeons contain
# scripted encounters, chain stages or duplicate content for the same pocket.
MINING_MISSIONS = {
    4800, 4801, 4802, 4803, 4804, 4805, 4806, 4807, 4808, 4809, 4810, 4811, 4812, 4813, 4814,
    4859, 4862, 4863, 4864, 4865, 4866, 4884, 13822,
}

# Complete, unambiguous native sequences. The first three reports independently
# describe each part; Hauling Fur's numbered records and next-mission messages
# identify its sequence, under the University's general mission-chain contract.
COURIER_CHAINS = [
    ('Save the Trees', 'Save_the_Trees', 215978, 500002,
     [(4838, 45, True), (4839, 28972, True), (4840, 28973, False)]),
    ('Hauling Fur', 'Missions', 230834, 500004,
     [(4841, 11068, True), (4842, 2653, False), (4843, 28975, True)]),
    ('Hunting a Heretic', 'Hunting_a_Heretic', 192145, 500003,
     [(4844, 28827, True), (4845, 28996, False), (4846, 28886, True)]),
    ('For the Troops', 'For_the_Troops', 231115, 500001,
     [(4847, 2610, False), (4849, 28995, False), (4853, 22208, False)]),
]


def source(db, dataset, key):
    row = db.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?', (dataset, str(key))).fetchone()
    return json.loads(row[0]) if row else None


def ship_types(db):
    return {key for key, in db.execute('SELECT t.Id FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE g.CategoryId=6')}


def militia_mission_ids():
    bindings = Path(__file__).resolve().parent.parent / 'src/Eden.Data/Catalog/FactionWarfare/MilitiaMissionBindings.cs'
    return {int(key) for key in re.findall(r'^\s+new\((\d+), \d+, MilitiaFaction\.', bindings.read_text(encoding='utf-8'), re.MULTILINE)}


def career_mission_ids():
    manifest = Path(__file__).resolve().parent.parent / 'src/Eden.World/Agents/career-missions-3662168.json'
    return {row['missionId'] for row in json.loads(manifest.read_text(encoding='utf-8'))['missions']}


def special_agent_mission(mid, data, hulls):
    template = data['contentTemplate'].rsplit('_', 1)[-1]
    if (2792 <= mid <= 2795 and template == 'ResearchTradeMission') or (mid == 2804 and template == 'ResearchCourierMission'):
        return 'Research bonus assignment'
    cargo = data.get('courierMission') or {}
    if (template == 'HeraldryMission' and data.get('agentTypeID') == 13 and data.get('corporationID') == 1000419
            and data.get('hasStandingRewards') == 0 and not data.get('killMission')
            and 0 < data.get('expirationTime', 0) <= 10080
            and data.get('fixedLpRewardAlpha', 0) > 0 and data.get('fixedLpRewardOmega', 0) > 0
            and cargo.get('objectiveSingleton') == 0 and cargo.get('objectiveTypeID') in hulls
            and cargo.get('objectiveQuantity') == 1):
        return 'Paragon packaged hull exchange'
    return None


def trade_definitions(db):
    # University revision 229167 supplies the ore amounts, levels and five-attribute
    # implant grades. The catalog omits rewardTypeID for this random reward family.
    attributes = ['Ocular Filter', 'Memory Augmentation', 'Neural Boost', 'Cybernetic Subprocessor', 'Social Adaptation Chip']
    definitions = []
    for mid, level, ore, quantity in [(15553, 1, 1230, 1000), (15554, 2, 1228, 1500),
                                      (15555, 3, 1227, 10000), (15556, 4, 20, 8000)]:
        mission = source(db, 'client/missions', mid)
        expected_cargo = dict(objectiveSingleton=0, objectiveTypeID=ore, objectiveQuantity=quantity)
        if (not mission or mission['contentTemplate'] != 'agent.missionTemplatizedContent_GenericStorylineTradeMission'
                or mission.get('courierMission') != expected_cargo or mission.get('expirationTime') != 10080
                or mission.get('missionRewards') != dict(reward=dict(rewardQuantity=1))
                or any(mission.get(k) for k in ['nodeGraphID', 'clientObjectives', 'contentTags', 'initialAgentGiftTypeID'])):
            raise ValueError(f'Unreviewed trade objective or reward family for mission {mid}')
        names = [('Limited ' + name + (' - Beta' if level == 2 else '')) if level <= 2
                 else name + (' - Basic' if level == 3 else ' - Standard') for name in attributes]
        reward_types = []
        for name in names:
            rows = db.execute('SELECT Id,GroupId FROM ItemType WHERE Name=?', (name,)).fetchall()
            if len(rows) != 1 or rows[0][1] != ATTRIBUTE_ENHANCER_GROUP:
                raise ValueError(f'Implant reward must resolve to one pinned attribute enhancer: {name}')
            reward_types.append(rows[0][0])
        definitions.append(dict(missionId=mid, level=level, difficulty=500, storyline=True, trade=True, offerOrder=1,
                                source='https://wiki.eveuniversity.org/Materials_For_War_Preparation',
                                sourceRevision='oldid=229167; build-3662168 cargo and implant types', waves=[],
                                itemReward=dict(types=reward_types, quantity=1)))
    return definitions


def delivery_rejection(mission, name, volume, reviewed_chain=False):
    template = mission['contentTemplate'].rsplit('_', 1)[-1]
    if template not in ('BasicCourierMission', 'GenericStorylineCourierMission'):
        return 'Requires another mission or agent lifecycle'
    if not reviewed_chain and (re.search(r'\b\d+\s+of\s+\d+\b', name, re.IGNORECASE)
                               or any('nextmission' in k for k in mission.get('messages', {}))):
        return 'Requires complete mission-chain bindings or additional part mechanics'
    if any(mission.get(k) for k in ('nodeGraphID', 'clientObjectives', 'contentTags', 'initialAgentGiftTypeID')):
        return 'Requires authored scripts, objectives or initial gifts'
    cargo = mission.get('courierMission', {})
    singleton = cargo.get('objectiveSingleton', 0)
    quantity = cargo.get('objectiveQuantity')
    if (type(singleton) is not int or singleton not in (0, 1)
            or singleton == 1 and (type(quantity) is not int or quantity != 1)):
        return 'Requires another objective inventory form'
    if not cargo.get('objectiveTypeID') or not 0 < cargo.get('objectiveQuantity', 0) <= 2147483647 or volume <= 0:
        return 'Missing usable cargo definition'
    expiration = mission.get('expirationTime', 10080)
    if type(expiration) is not int or not 0 < expiration <= 10080:
        return 'Requires another mission deadline lifecycle'
    for reward in mission.get('missionRewards', {}).values():
        if isinstance(reward, dict) and reward.get('rewardTypeID', 29) != 29:
            return 'Requires fixed item rewards'
    return None


def courier_chain_definitions(db):
    definitions = []
    for family, report, revision, faction, parts in COURIER_CHAINS:
        for index, (mid, cargo_type, singleton) in enumerate(parts):
            mission = source(db, 'client/missions', mid)
            sde = source(db, 'sde/missions', mid) or {}
            name = sde.get('name', {}).get('en', '')
            cargo = (mission or {}).get('courierMission', {})
            item = source(db, 'client/types', cargo_type) or {}
            volume = item.get('volume', 0) * cargo.get('objectiveQuantity', 0)
            next_message = any('nextmission' in k for k in (mission or {}).get('messages', {}))
            if (not mission or not name.startswith(family) or f'({index + 1} of {len(parts)})' not in name
                    or mission['contentTemplate'] != 'agent.missionTemplatizedContent_BasicCourierMission'
                    or mission.get('factionID') != faction or mission.get('corporationID')
                    or cargo != dict(objectiveTypeID=cargo_type, objectiveQuantity=1, objectiveSingleton=int(singleton))
                    or next_message != (index < len(parts) - 1)
                    or not 0 < volume <= 320 or delivery_rejection(mission, name, volume, reviewed_chain=True)):
                raise ValueError(f'Unreviewed courier chain part {mid}: {family}')
            definitions.append(dict(missionId=mid, level=1, difficulty=500, storyline=False, offerOrder=1,
                                    source='https://wiki.eveuniversity.org/' + report,
                                    sourceRevision=f'oldid={revision}; build-3662168 numbered chain and cargo', waves=[],
                                    chain=dict(rootMissionId=parts[0][0], previousMissionId=parts[index - 1][0] if index else 0,
                                               nextMissionId=parts[index + 1][0] if index < len(parts) - 1 else 0)))
    return definitions


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
                  habitat=False, structure_type=None):
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
        objective_type = HABITAT_TYPE if habitat else structure_type
        if objective_type is not None:
            # Bind the reviewed destructible type, not similarly named decorative scenery.
            targets = [o for r in rooms.values() for o in r['objects'].values() if o['typeID'] == objective_type]
            if len(targets) != 1 or mission['killMission'].get('objectiveTypeID'):
                raise ValueError(f'Unreviewed structure objective for mission {mid}')
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
    encounter(888, 500010, 'The_Guristas_Spies_(Level_2)', 192010,
              [group('Pithi Despoiler', 5, 30000, required=False),
               group('Pithior Guerilla', 1, 30000, required=False),
               group('Pithi Demolisher/Pithi Destructor', 2, 57000),
               group('Pithior Renegade/Pithior Anarchist', 2, 57000)], 53018)
    # The report's "Infanty" is a typo for the pinned Corelior Infantry type.
    encounter(898, 500020, 'The_Serpentis_Spies_(Level_3)', 223606,
              [group('Corelior Infantry/Corelior Soldier', 3, 30000, required=False),
               group('Corelum Chief Infantry/Corelum Chief Sentinel', 1, 30000, required=False),
               group('Tower Sentry Serpentis I', 2, 62000, required=False),
               group('Coreli Agent/Coreli Spy', 5, 71000), group('Serpentis Spy', 3, 71000)], 53182)
    # University reports give the spy display names. The corresponding EVE-Survival
    # SanshaSpies2/3/4 reports explicitly identify their underlying catalog hulls:
    # Centior Cannibal; Centatis Specter; and Centatis Devil/Wraith, respectively.
    # Native dungeon sentry types also resolve the level-two "Sansha Sentry I" alias.
    encounter(902, 500019, 'The_Sansha_Spies_(Level_2)', 192040,
              [group('Tower Sentry Sansha I', 2, 60000, required=False),
               group('Centii Servant', 7, 60000, required=False), group('Centior Cannibal', 2, 60000)], 53098)
    encounter(901, 500019, 'The_Sansha_Spies_(Level_3)', 192041,
              [group('Tower Sentry Sansha I', 1, 55000, required=False),
               group('Centii Plague/Centii Enslaver', 3, 55000, required=False),
               group('Centior Cannibal/Centior Devourer/Centior Abomination', 5, 70000, required=False),
               group('Centatis Specter', 2, 80000)], 51807)
    encounter(900, 500019, 'The_Sansha_Spies_(Level_4)', 193406,
              [group('Tower Sentry Sansha I', 1, 42000, required=False),
               group('Tower Sentry Sansha II', 4, 42000, required=False),
               group('Centior Devourer/Centior Abomination', 4, 75000, required=False),
               group('Centum Hellhound/Centum Fiend', 4, 75000, required=False),
               group('Centus Savage Lord/Centus Mutant Lord', 2, 75000, required=False),
               group('Centatis Devil/Centatis Wraith', 3, 80000),
               group('Centus Dark Lord/Centus Overlord', 1, 80000)], 70402)
    encounter(2248, 500019, "Eliminate_the_Pirate_Campers_(Sansha's_Nation)", 203023,
              [group('Centii Servant/Centii Minion', 6, 10000)], deadspace=False)
    encounter(2249, 500020, 'Eliminate_the_Pirate_Campers_(Serpentis)', 203018,
              [group('Coreli Initiate/Coreli Scout/Coreli Spy/Coreli Agent', 4, 10000)], deadspace=False)
    encounter(2254, 500015, 'Eliminate_the_Pirate_Campers_(Thukker_tribe)', 191677,
              [group('Thukker Mercenary Fighter', 3, 10000), group('Thukker Mercenary Elite Fighter', 4, 10000)], deadspace=False)
    encounter(2255, 500011, 'Eliminate_the_Pirate_Campers_(Angel_Cartel)', 203561,
              [group('Gistior Defacer/Gistior Shatterer', 5, 5000),
               group('Gistum Depredator/Gistum Predator/Gistum Smasher/Gistum Crusher', 3, 5000)], deadspace=False)

    # The level-one reports identify the center group by the Ghost Ship. The level-two
    # objective is the group around the monument. Other groups can be left alive.
    score = [
        (2407, 500011, '(Angel_Cartel)_(Level_1)', 209646, 653506,
         [('Gistii Raider/Gistii Hunter', 2, 45000, False), ('Gistii Nomad/Gistii Ambusher', 1, 45000, False),
          ('Gistii Nomad/Gistii Ambusher', 1, 48000, True), ('Gistii Hunter/Gistii Raider', 1, 48000, True),
          ('Gistii Ruffian', 1, 48000, True), ('Gistior Defacer/Gistior Haunter', 1, 48000, True),
          ('Gistii Nomad/Gistii Ambusher', 3, 45000, False)]),
        (2391, 500010, '(Guristas_Pirates)_(Level_1)', 192048, 653574,
         [('Pithi Saboteur/Pithi Despoiler', 3, 40000, False),
          ('Pithi Saboteur/Pithi Despoiler/Pithi Wrecker', 3, 50000, True),
          ('Pithior Renegade/Pithior Anarchist', 1, 50000, True),
          ('Pithi Plunderer/Pithi Wrecker/Pithi Destructor', 3, 40000, False)]),
        (2416, 500012, '(Blood_Raiders)', 192046, 652800,
         [('Corpii Collector/Corpii Seeker', 2, 40000, False), ('Corpior Converter', 1, 40000, False),
          ('Corpii Follower/Corpii Seeker', 5, 48000, False), ('Corpii Herald/Corpii Upholder', 3, 62000, True),
          ('Corpior Templar', 1, 62000, True), ('Corpum Arch Reaver', 1, 62000, True)]),
        (2449, 500020, '(Serpentis)_(level_2)', 192055, 652824,
         [('Coreli Initiate/Coreli Scout/Coreli Watchman/Coreli Patroller', 5, 40000, False),
          ('Coreli Initiate/Coreli Scout', 4, 50000, False), ('Corelior Soldier/Corelior Infantry', 1, 50000, False),
          ('Coreli Spy/Coreli Agent', 3, 62000, True), ('Corelior Trooper', 2, 62000, True),
          ('Corelum Chief Spy', 1, 62000, True)]),
    ]
    for mid, faction, variant, revision, entry, roster in score:
        encounter(mid, faction, f'The_Score_{variant}', revision,
                  [group(names, count, distance, required=required) for names, count, distance, required in roster], entry)

    # The Guristas level-three variant locks the second gate until its first
    # pocket is cleared. Only the radio telescope group is required in pocket two.
    first_pocket, second_pocket = 14777, 14778
    missions.append(dict(
        missionId=2470, level=3, difficulty=500, storyline=False, offerOrder=1,
        source='https://wiki.eveuniversity.org/The_Score_(Guristas_Pirates)_(Level_3)', sourceRevision='oldid=223423',
        rooms=[dict(roomId=14776, entryObjectId=642754), dict(roomId=first_pocket, entryObjectId=642757),
               dict(roomId=second_pocket, entryObjectId=642866)],
        gates=[dict(fromObjectId=642755, destinationRoomId=first_pocket),
               dict(fromObjectId=642756, destinationRoomId=second_pocket, requiresClear=True)],
        waves=[
            dict(roomId=first_pocket, groups=[
                group('Pithi Destructor', 1, 50000), group('Pithum Mortifier/Pithum Inferno', 2, 50000),
                group('Pithi Destructor', 1, 54000), group('Pithum Mortifier/Pithum Inferno', 2, 54000),
                group('Pithi Demolisher', 1, 62000), group('Pithior Terrorist', 1, 62000)]),
            dict(roomId=second_pocket, groups=[
                group('Pithi Plunderer', 1, 62000, required=False), group('Pithum Inferno', 2, 62000, required=False),
                group('Pithi Destructor/Pithi Demolisher', 2, 64000, required=False),
                group('Pithum Eraser', 1, 64000, required=False),
                group('Pithi Destructor/Pithi Demolisher', 2, 70000), group('Pithum Mortifier', 1, 70000),
                group('Pithatis Enforcer/Pithatis Assaulter', 1, 70000)]),
        ]))

    militants = 25373
    for mid, faction, title, revision, entry, carrier, groups in [
        (2542, 500011, '(Angel_Cartel)_(Level_2)', 231043, 671874, 'Angel Cartel Personnel Transport',
         [group('Gistii Hijacker/Gistii Rogue/Gistii Ruffian/Gistii Nomad', 6, 35000, required=False),
          group('Gistii Hijacker/Gistii Rogue', 8, 50000), group('Gistior Defacer/Gistior Haunter', 1, 50000),
          group('Gistum Depredator/Gistum Predator', 1, 50000)]),
        (2546, 500019, "(Sansha's_Nation)", 191943, 653071, 'Sanshas Personnel Transport',
         [group('Centii Butcher/Centii Manslayer/Centii Minion/Centii Servant', 3, 40000, required=False),
          group('Centii Slavehunter/Centii Savage/Centii Minion', 6, 45000),
          group('Centum Ravager', 1, 45000)]),
    ]:
        objective = source(db, 'client/missions', mid)['killMission']
        if objective.get('objectiveTypeID') != militants or objective.get('objectiveQuantity') != 10:
            raise ValueError(f'Unreviewed transport cargo for mission {mid}')
        transport = group(carrier, 2, groups[-1]['range'], lootTypeId=militants, lootQuantity=objective['objectiveQuantity'])
        encounter(mid, faction, f'Smuggler_Interception_{title}', revision, [*groups, transport], entry)

    # These dungeons use the generic mission faction in the pinned catalog. The
    # encounter actors are Rogue Drones. The Silo is the localized name of type 18012.
    drone_faction = 500025
    infester_group = 805
    encounter(1237, 500021, 'The_Roving_Rogue_Drones', 203054,
              [group('Belphegor Apis/Mammon Apis', 4, 55000, enemyFaction=drone_faction),
               group('Infester Alvi', 1, 55000, npc_group=infester_group, enemyFaction=drone_faction)], 59191)
    encounter(1214, 500021, 'The_Drone_Infestation_(Level_1)', 230772,
              [group('Belphegor Apis/Mammon Apis', 2, 30000, required=False, enemyFaction=drone_faction),
               group('Infester Alvi', 1, 30000, npc_group=infester_group, required=False, enemyFaction=drone_faction),
               group('Belphegor Apis/Mammon Apis', 2, 45000, required=False, enemyFaction=drone_faction),
               group('Infester Alvi', 1, 45000, npc_group=infester_group, required=False, enemyFaction=drone_faction),
               group('Drone Point Defense Battery', 1, 45000, required=False, enemyFaction=drone_faction)],
              582811, structure_type=18012)

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
        (924, 500010, 'Guristas_Pirates', 3, 218636, 758889,
         [('Pithi Wrecker/Pithi Plunderer', 4, 50000, False),
          ('Pithum Mortifier/Pithum Inferno', 2, 50000, False),
          ('Pithi Plunderer/Pithi Wrecker', 4, 110000, True), ('Pithatis Assaulter', 1, 110000, True)]),
        (927, 500011, 'Angel_Cartel', 3, 201875, 758924,
         [('Angel Viper/Angel Webifier', 1, 25000, True), ('Gistior Defacer/Gistior Haunter', 2, 25000, True),
          ('Gistum Liquidator/Gistum Marauder', 1, 25000, True),
          ('Gistii Ambusher/Gistii Raider', 4, 55000, True), ('Gistatis Primus', 1, 55000, True)]),
        (930, 500012, 'Blood_Raiders', 3, 211495, 758998,
         [('Blood Disciple', 1, 40000, False), ('Corpior Devoter/Corpior Templar', 4, 40000, False),
          ('Corpum Dark Priest', 1, 40000, False), ('Corpii Engraver/Corpii Reaver', 3, 50000, True),
          ('Corpatis Fanatic', 1, 50000, True)]),
        # The report's optional post-completion Chief Guard has no timing/count contract.
        # Admit its verified initial objective, without inventing that later spawn.
        (936, 500020, 'Serpentis', 3, 193399, 758984,
         [('Guardian Veteran', 2, UNREPORTED_GROUP_RANGE, True),
          ('Corelum Chief Safeguard', 4, UNREPORTED_GROUP_RANGE, True),
          ('Corelatis Squad Leader', 1, UNREPORTED_GROUP_RANGE, True)]),
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

    # Retribution completes on the outpost alone. The evejs Guristas kill-all
    # objective is provisional; the University's explicit blitz takes precedence.
    # Outpost type IDs below are build-3662168 mission objects (group 319).
    encounter(1188, 500011, 'Retribution_(Angel_Cartel)', 201368,
              [group('Gistii Nomad/Gistii Ruffian', 2, 30000, required=False),
               group('Gistum Breaker/Gistum Defeater', 2, 30000, required=False),
               group('Gistum Centurion/Gistum Phalanx', 3, 45000, required=False),
               group('Gistii Hijacker/Gistii Rogue', 6, 50000, required=False),
               group('Gistum Depredator/Gistum Predator', 3, 50000, required=False),
               group('Angel Viper/Angel Webifier', 3, 75000, required=False),
               group('Gistatis Legionnaire/Gistatis Primus', 1, 75000, required=False)],
              60787, structure_type=16859)
    encounter(1187, 500010, 'Retribution_(Guristas_Pirates)', 218717,
              [group('Pithum Killer/Pithum Murderer', 4, 30000, required=False),
               group('Pithum Ascriber/Pithum Silencer', 5, 40000, required=False),
               group('Pithum Inferno/Pithum Mortifier', 3, 40000, required=False),
               group('Guristas Kyoukan/Guristas Webifier', 3, 75000, required=False),
               group('Pithatis Enforcer', 1, 75000, required=False)],
              60741, structure_type=17382)
    encounter(1190, 500020, 'Retribution_(Serpentis)', 207699,
              [group('Coreli Guard/Coreli Safeguard', 2, 33000, required=False),
               group('Corelum Chief Defender/Corelum Chief Protector', 2, 33000, required=False),
               group('Guardian Veteran', 2, 40000, required=False),
               group('Corelum Chief Scout/Corelum Chief Spy', 2, 40000, required=False),
               group('Coreli Patroller/Coreli Watchman', 8, 51000, required=False),
               group('Coreli Protector/Coreli Defender', 3, 77000, required=False),
               group('Guardian Veteran', 1, 77000, required=False),
               group('Corelatis Squad Leader/Corelatis Wing Leader', 1, 77000, required=False)],
              56324, structure_type=16855)
    # The report omits the possessive apostrophe. Bind the actual elite types,
    # not evejs's provisional Centii Butcher substitute for the same aliases.
    encounter(1186, 500019, "Retribution_(Sansha's_Nation)", 191886,
              [group('Centii Plague/Centii Enslaver', 2, 35000, required=False),
               group('Centum Slaughterer/Centum Execrator', 2, 35000, required=False),
               group('Centii Butcher/Centii Manslayer', 1, 45000, required=False),
               group("Sansha's Berserker/Sansha's Demon", 2, 45000, required=False),
               group('Centum Execrator/Centum Slaughterer', 1, 45000, required=False),
               group('Centii Butcher/Centii Manslayer', 1, 45000, required=False),
               group('Centum Torturer/Centum Mutilator', 2, 45000, required=False),
               group('Centii Butcher/Centii Manslayer', 4, 75000, required=False),
               group('Centum Fiend/Centum Hellhound', 2, 75000, required=False)],
              593785, structure_type=17381)

    missions.extend(courier_chain_definitions(db))
    missions.extend(trade_definitions(db))
    missions.extend(data_center_definitions(db))
    admitted = {m['missionId'] for m in missions}
    encounters = {tuple(sorted(source(db, 'client/missions', m['missionId'])['killMission'].items())): m
                  for m in missions if m['waves']}
    audit = []
    hulls = ship_types(db)
    militia = militia_mission_ids()
    career = career_mission_ids()
    for key, raw in db.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='client/missions' ORDER BY CAST(Key AS INTEGER)"):
        mid, data = int(key), json.loads(raw)
        sde = source(db, 'sde/missions', mid)
        name = sde.get('name', {}).get('en', str(mid))
        template = data['contentTemplate'].rsplit('_', 1)[-1]
        special_agent = special_agent_mission(mid, data, hulls)
        if mid in militia:
            if template != 'BasicKillMission' or not data.get('killMission', {}).get('dungeonID'):
                raise ValueError(f'Militia binding {mid} needs a catalog encounter')
            special_agent = 'Existing faction warfare mission binding'
        if mid in career:
            special_agent = 'Pinned career agent chain stage'
        if mid in KNOWN_BLOCKERS:
            reason = KNOWN_BLOCKERS[mid]
        elif mid in admitted:
            reason = None
        elif special_agent:
            reason = None
        elif template == 'HeraldryMission':
            reason = 'Requires evidence for Paragon ammunition, singleton or standing rewards'
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
                          else 'Requires complete mission-chain bindings or additional part mechanics' if chain
                          else 'Requires authored scripts or initial gifts' if special
                          else 'Requires verified NPC roster and objective scripts')
        elif template.endswith('TradeMission'):
            reason = 'Requires verified trade content or another agent lifecycle'
        else:
            reason = 'Requires another mission or agent lifecycle'
        audit.append(dict(missionId=mid, name=name, template=template, status='blocked' if reason else 'admitted',
                          reason=reason or special_agent or ('Catalog mining deposits' if template == 'BasicMiningMission'
                                            else 'Catalog cargo or reviewed encounter')))
    return audit


def write_audit(path, audit):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open('w', encoding='utf-8', newline='') as stream:
        writer = csv.DictWriter(stream, fieldnames=['missionId', 'name', 'template', 'status', 'reason'], lineterminator='\n')
        writer.writeheader()
        writer.writerows(audit)
    print('Mission audit:', dict(Counter(row['status'] for row in audit)))
