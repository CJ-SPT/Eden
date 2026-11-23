"""Explicit objective and encounter bindings for build-3662168 Enforcer missions."""

REPORT = 'https://wiki.eveuniversity.org/Enforcer_(Career_Agent)'
REPORT_REVISION = 'oldid=215464; build-3662168 rooms, items and objectives'
BEACON_TYPE = 10124
GATE_TYPE = 17831
WEBBER_DRONE = 30365
RUNNER_DRONE = 30366
PIRATE_CAPSULE = 27287
CIVILIANS = 24441
DOCUMENTS = 24734

# Source mission numbers are in chain order. The NPC choices independently
# bind the reported encounter roles to exact catalog hulls for each faction.
# Nearby retail observations in evejs.career-agents 1.4.4 corroborate the
# selections; this exporter uses Eden's pinned layouts.
FACTIONS = (
    # Supplier, enemy, mission IDs, basic/strong frigates, escorts, rookie,
    # outpost defense sentry, hotel attackers, pirate leader.
    (500001, 500010, (3122, 3160, 3163, 3131, 3134, 3139, 3143, 3147, 13868, 3151),
     16981, 16987, (16994, 16996, 16997), 30380, 13068, (17001, 17006), 'Tahamar'),
    (500002, 500011, (3120, 3157, 3162, 3128, 3135, 3136, 3140, 3145, 13866, 3149),
     16877, 16887, (16886, 16883, 16888), 30369, 13114, (16874, 16885), 'Steon'),
    (500003, 500012, (3121, 3159, 3164, 3129, 3132, 3137, 3142, 3146, 13867, 3150),
     16936, 16947, (16950, 16946, 16933), 30379, 13116, (16945, 16932), 'The Devout'),
    (500004, 500020, (3123, 3161, 3165, 3130, 3133, 3138, 3144, 3148, 13869, 3152),
     17121, 17116, (17117, 17279, 17118), 30381, 13115, (17278, 17113), 'Wolf'),
)


def definitions(db, group):
    from mission_content import source

    def npc(type_id, amount, distance, **flags):
        name, npc_group = db.execute('SELECT Name, GroupId FROM ItemType WHERE Id=?', (type_id,)).fetchone()
        binding = group(name, amount, distance, npc_group=npc_group)
        if binding['types'] != [type_id]:
            raise ValueError(f'NPC binding changed for Enforcer type {type_id}')
        return dict(binding, **flags)

    def find_object(room, type_id=None, name=None):
        candidates = []
        for obj in room['objects'].values():
            if type_id is not None and obj['typeID'] != type_id:
                continue
            if name is not None:
                message = source(db, 'client/localization_fsd_en-us', obj.get('objectNameID'))
                if not message or message.get('text') != name:
                    continue
            candidates.append(obj['objectID'])
        if len(candidates) != 1:
            raise ValueError(f'Expected one Enforcer scene object for {(type_id, name)}: {candidates}')
        return candidates[0]

    def waves(room, roster):
        return [dict(roomId=room, groups=groups) for groups in roster]

    def waves_optional(room, roster):
        return waves(room, [[dict(npc_type, required=False, trigger=True) for npc_type in groups] for groups in roster])

    result = []
    for supplier, enemy, parts, basic, strong, escorts, rookie, sentry, ambush, leader in FACTIONS:
        scout, guard, veteran = escorts
        for part, mid in enumerate(parts, 1):
            mission = source(db, 'client/missions', mid)
            name = source(db, 'sde/missions', mid)['name']['en']
            objective = mission['killMission']
            dungeon = source(db, 'client/dungeons', objective['dungeonID'])
            expected_cargo = {2: CIVILIANS, 3: DOCUMENTS}.get(part)
            if (name != f'Cash Flow for Capsuleers ({part} of 10)'
                    or mission['contentTemplate'] != 'agent.missionTemplatizedContent_StorylineKillMission'
                    or mission['factionID'] != supplier or mission['expirationTime'] != 0
                    or objective.get('objectiveTypeID') != expected_cargo
                    or objective.get('objectiveQuantity') != int(expected_cargo is not None)
                    or dungeon['difficulty'] != 1 or dungeon['factionID'] != (500021 if part == 4 else enemy)):
                raise ValueError(f'Enforcer native mission binding changed: {mid}')
            rooms = sorted((int(room_id), room) for room_id, room in dungeon['rooms'].items())
            expected_rooms = {6: 3, 9: 2, 10: 4}.get(part, 1)
            gates = {c['fromObjectID'] for c in dungeon.get('connections', [])}
            bindings = [dict(roomId=room, entryObjectId=find_object(data, BEACON_TYPE)) for room, data in rooms]
            if (len(rooms) != expected_rooms or len(gates) != expected_rooms - 1
                    or bindings[0]['entryObjectId'] != dungeon['entryObjectID']):
                raise ValueError(f'Enforcer native room order changed: {mid}')

            definition = dict(missionId=mid, source=REPORT, sourceRevision=REPORT_REVISION, rooms=bindings, waves=[])
            result.append(definition)
            first = rooms[0][0]
            if gates:
                definition['gates'] = [dict(fromObjectId=find_object(data, GATE_TYPE), destinationRoomId=rooms[i + 1][0],
                                            requiresClear=part in (6, 10) and i > 0)
                                       for i, (_, data) in enumerate(rooms[:-1])]
                if {g['fromObjectId'] for g in definition['gates']} != gates:
                    raise ValueError(f'Enforcer native gate order changed: {mid}')

            if part == 1:
                definition['waves'] = waves(first, [[npc(basic, n, 37000)] for n in (1, 2, 1)])
            elif part == 2:
                definition['waves'] = waves(first, [[npc(basic, 3, 40000)],
                                                   [npc(basic, 2, 40000, lootTypeId=CIVILIANS, lootQuantity=1,
                                                        lootToPilotCargo=True)]])
            elif part == 3:
                definition['waves'] = waves(first, [[npc(basic, 4, 23000, required=False),
                                                    npc(WEBBER_DRONE, 1, 22000, required=False),
                                                    npc(RUNNER_DRONE, 1, 29000, completesMission=True,
                                                        lootTypeId=DOCUMENTS, lootQuantity=1, lootToPilotCargo=True)]])
            elif part == 4:
                definition['careerVisit'] = dict(objectId=find_object(rooms[0][1], 24252), range=500)
            elif part == 5:
                definition['waves'] = waves(first, [[npc(strong, 1, 14000)]])
            elif part == 6:
                second, last = rooms[1][0], rooms[2][0]
                outpost = find_object(rooms[2][1], name=f"{leader}'s Outpost")
                definition['structures'] = [dict(objectId=outpost, required=True, completesMission=True)]
                definition['waves'] = [
                    *waves(second, [[npc(basic, 1, 20000), npc(scout, 3, 42000)],
                                    [npc(basic, 3, 25000)], [npc(basic, 2, 30000)]]),
                    dict(roomId=last, trigger='structureDamaged', triggerObjectId=outpost,
                         groups=[npc(basic, 2, 28000, required=False), npc(sentry, 2, 26000, required=False)]),
                ]
            elif part == 7:
                definition['waves'] = waves(first, [[npc(basic, 2, 20000)],
                                                   [npc(scout, 1, 24000), npc(guard, 3, 23000), npc(veteran, 1, 27000)],
                                                   [npc(basic, 2, 25000), npc(scout, 1, 27000)]])
            elif part == 8:
                definition['careerVisit'] = dict(
                    objectId=find_object(rooms[0][1], 64184), range=1800, removeObject=True, thermalDamage=250)
                definition['waves'] = [dict(roomId=first, trigger='visit', groups=[
                    npc(ambush[0], 2, 26000, required=False), npc(ambush[1], 1, 30000, required=False)])]
            elif part == 9:
                warehouse = find_object(rooms[1][1], name='Narcotics Warehouse')
                definition['structures'] = [dict(objectId=warehouse, required=True, completesMission=True)]
                definition['waves'] = [
                    *waves_optional(first, [[npc(rookie, 3, 28000)], [npc(rookie, 2, 30000), npc(basic, 1, 30000)]]),
                    *waves_optional(rooms[1][0], [[npc(rookie, 3, 30000)], [npc(rookie, 2, 30000), npc(basic, 1, 30000)],
                                                 [npc(rookie, 2, 30000), npc(veteran, 1, 30000)]]),
                ]
            else:
                last = rooms[3][0]
                tower = find_object(rooms[3][1], name=f"{leader}'s Stasis Tower")
                definition['structures'] = [dict(objectId=tower, required=True)]
                definition['waves'] = [
                    *waves(rooms[1][0], [[npc(rookie, 6, 12000)], [npc(guard, 1, 16000), npc(veteran, 1, 16000)]]),
                    *waves(rooms[2][0], [[npc(basic, 2, 22000), npc(scout, 2, 22000)],
                                        [npc(guard, 1, 23000), npc(veteran, 1, 23000)], [npc(veteran, 2, 23000)]]),
                    *waves(last, [[npc(veteran, 2, 22000, required=False), npc(strong, 1, 22000, required=False)]]),
                    dict(roomId=last, trigger='structureDestroyed', triggerObjectId=tower,
                         groups=[npc(PIRATE_CAPSULE, 1, 21000, completesMission=True, careerName=f"{leader}'s Capsule")]),
                ]
    return result
