"""Reviewed numeric encounter facts discovered through evejs's report index.

Original EVE University report revisions supply rosters and objectives. The
build-3662168 catalog supplies identities and room bindings. No evejs runtime,
generated spawn policy, reward tables or prose is imported.
"""


def expand(source, db, missions, group):
    def read_encounter(mid, faction):
        native = source(db, 'client/missions', mid)
        if native['contentTemplate'].rsplit('_', 1)[-1] != 'BasicKillMission' or any(
                native.get(k) for k in ('nodeGraphID', 'clientObjectives', 'contentTags', 'initialAgentGiftTypeID')):
            raise ValueError(f'Unreviewed lifecycle for mission {mid}')
        dungeon = source(db, 'client/dungeons', native['killMission']['dungeonID'])
        if dungeon['factionID'] != faction or native['killMission'].get('objectiveQuantity'):
            raise ValueError(f'Unreviewed faction or cargo for mission {mid}')
        return dungeon

    def encounter(mid, faction, title, revision, entry, roster, habitat=None, deadspace=True):
        dungeon = read_encounter(mid, faction)
        rooms = dungeon['rooms']
        definition = dict(missionId=mid, level=dungeon['difficulty'], difficulty=500, storyline=False,
                          source='https://wiki.eveuniversity.org/' + title, sourceRevision=f'oldid={revision}',
                          offerOrder=2, deadspace=deadspace, waves=[dict(groups=roster)])
        if entry:
            lobby = next(int(rid) for rid, room in rooms.items()
                         if any(o['objectID'] == dungeon['entryObjectID'] for o in room['objects'].values()))
            destination = next(int(rid) for rid, room in rooms.items()
                               if any(o['objectID'] == entry for o in room['objects'].values()))
            if len(rooms) != 2 or lobby == destination or len(dungeon.get('connections', [])) != 1:
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
        if habitat:
            target = next(o for r in rooms.values() for o in r['objects'].values() if o['objectID'] == habitat)
            # This exact destructible catalog type is the report's Habitat objective.
            if target['typeID'] != 19715 or any(g.get('required', True) for g in roster):
                raise ValueError(f'Unreviewed habitat objective for mission {mid}')
            definition['structures'] = [dict(objectId=habitat, required=True, completesMission=True)]
        missions.append(definition)
        return definition

    encounter(888, 500010, 'The_Guristas_Spies_(Level_2)', 192010, 53018,
              [group('Pithi Despoiler', 5, 30000, required=False), group('Pithior Guerilla', 1, 30000, required=False),
               group('Pithi Demolisher/Pithi Destructor', 2, 57000), group('Pithior Renegade/Pithior Anarchist', 2, 57000)])
    # The report identifies the renamed spies as Pithatis Executors.
    encounter(893, 500010, 'The_Guristas_Spies_(Level_3)', 192011, 53027,
              [group('Pithatis Executor', 3, 30000), group('Pithior Anarchist', 3, 60000, required=False),
               group('Pithatis Assaulter/Pithatis Enforcer', 1, 60000, required=False)])
    # Retain the same reported spy hull; unidentified "..." alternatives are excluded.
    encounter(891, 500010, 'The_Guristas_Spies_(Level_4)', 196566, 592833,
              [group('Pithior Supremacist/Pithior Terrorist', 1, 27000, required=False),
               group('Pith Extinguisher/Pith Destroyer', 3, 27000, required=False),
               group('Pithatis Assaulter/Pithatis Enforcer', 3, 30000, required=False),
               group('Pith Eradicator/Pith Obliterator', 4, 30000, required=False),
               group('Pithior Terrorist', 2, 68000), group('Pithatis Executor', 3, 68000),
               group('Pith Massacrer/Pith Conquistador', 1, 68000)])
    encounter(898, 500020, 'The_Serpentis_Spies_(Level_3)', 223606, 53182,
              [group('Corelior Infantry/Corelior Soldier', 3, 28000, required=False),
               group('Corelum Chief Infantry/Corelum Chief Sentinel', 1, 28000, required=False),
               group('Tower Sentry Serpentis I', 2, 52000, required=False),
               group('Coreli Agent/Coreli Spy', 5, 70000), group('Serpentis Spy', 3, 70000)])
    encounter(1065, 500012, 'The_Blood_Raider_Spies_(Level_4)', 208031, 70433,
              [group('Corpatis Bishop/Corpatis Seer', 3, 5000, required=False),
               group('Corpus Harbinger/Corpus Oracle', 1, 5000, required=False),
               group('Corpior Visionary/Corpior Converter', 3, 35000, required=False),
               group('Corpum Shadow Sage/Corpum Dark Priest', 3, 35000, required=False),
               group('Corpus Archon/Corpus Prophet', 3, 35000, required=False),
               group('Corpior Cleric/Corpior Friar', 3, 55000), group('Corpatis Exorcist', 3, 55000)])

    for mid in (2235, 2237):
        encounter(mid, 500019, "Pirate_Aggression_(Sansha's_Nation)", 209078, None,
                  [group('Centii Ravener/Centii Scavenger', 3, 10000)], deadspace=False)
    for mid in (2236, 2238):
        encounter(mid, 500011, 'Pirate_Aggression_(Angel_Cartel)', 230763, None,
                  [group('Gistii Hijacker', 6, 7000)], deadspace=False)

    encounter(2391, 500010, 'The_Score_(Guristas_Pirates)_(Level_1)', 192048, 653574,
              [group('Pithi Saboteur/Pithi Despoiler', 3, 40000, required=False),
               group('Pithi Saboteur/Pithi Despoiler/Pithi Wrecker', 3, 50000),
               group('Pithior Renegade/Pithior Anarchist', 1, 50000),
               group('Pithi Plunderer/Pithi Wrecker/Pithi Destructor', 3, 40000, required=False)])
    encounter(2407, 500011, 'The_Score_(Angel_Cartel)_(Level_1)', 209646, 653506,
              [group('Gistii Raider/Gistii Hunter', 2, 45000, required=False),
               group('Gistii Nomad/Gistii Ambusher', 1, 45000, required=False),
               group('Gistii Nomad/Gistii Ambusher', 1, 45000), group('Gistii Hunter/Gistii Raider', 1, 45000),
               group('Gistii Ruffian', 1, 45000), group('Gistior Defacer/Gistior Haunter', 1, 45000),
               group('Gistii Nomad/Gistii Ambusher', 3, 45000, required=False)])
    encounter(2416, 500012, 'The_Score_(Blood_Raiders)', 192046, 652800,
              [group('Corpii Collector/Corpii Seeker', 2, 38000, required=False),
               group('Corpior Converter', 1, 38000, required=False),
               group('Corpii Follower/Corpii Seeker', 5, 43000, required=False),
               group('Corpii Herald/Corpii Upholder', 3, 60000), group('Corpior Templar', 1, 60000),
               group('Corpum Arch Reaver', 1, 60000)])
    encounter(2449, 500020, 'The_Score_(Serpentis)_(level_2)', 192055, 652824,
              [group('Coreli Initiate/Coreli Scout/Coreli Watchman/Coreli Patroller', 5, 38000, required=False),
               group('Coreli Initiate/Coreli Scout', 4, 43000, required=False),
               group('Corelior Soldier/Corelior Infantry', 1, 43000, required=False),
               group('Coreli Spy/Coreli Agent', 3, 48000), group('Corelior Trooper', 2, 48000),
               group('Corelum Chief Spy', 1, 48000)])

    for mid, entry, habitat in ((1180, 55664, 55669), (1181, 55688, 55693)):
        encounter(mid, 500011, 'Avenge_a_Fallen_Comrade_(Angel_Cartel)_(Level_1)', 209636, entry,
                  [group('Gistii Hijacker', 2, 2000, required=False),
                   group('Gistii Hijacker/Gistii Rogue', 3, 45000, required=False),
                   group('Gistii Raider/Gistii Ambusher', 1, 55000, required=False),
                   group('Gistii Hijacker', 8, 55000, required=False)], habitat)
    encounter(1177, 500010, 'Avenge_a_Fallen_Comrade_(Guristas_Pirates)_(Level_2)', 203235, 595380,
              [group('Pithi Arrogator/Pithi Imputor', 4, 10000, required=False),
               group('Pithi Infiltrator/Pithi Invader', 5, 40000, required=False),
               group('Pithior Nihilist', 1, 40000, required=False),
               group('Pithi Infiltrator/Pithi Invader', 3, 40000, required=False),
               group('Pithum Silencer', 1, 40000, required=False),
               group('Pithi Despoiler/Pithi Saboteur', 5, 60000, required=False)], 595398)
    encounter(1179, 500020, 'Avenge_a_Fallen_Comrade_(Serpentis)_(Level_2)', 193385, 55616,
              [group('Coreli Safeguard', 2, 2000, required=False), group('Coreli Initiate', 4, 40000, required=False),
               group('Corelior Soldier', 1, 40000, required=False), group('Coreli Agent/Coreli Spy', 3, 50000, required=False),
               group('Corelum Chief Patroller', 1, 50000, required=False),
               group('Coreli Watchman/Coreli Patroller', 7, 60000, required=False)], 55621)

    # Two combat rooms plus an empty entry lobby. Gate two needs room-one clearance.
    for mid, faction, title, revision, roster, bindings, gate, final in (
        (2470, 500010, 'The_Score_(Guristas_Pirates)_(Level_3)', 223423,
         [group('Pithi Destructor', 1, 48000), group('Pithum Mortifier/Pithum Inferno', 2, 48000),
          group('Pithi Destructor', 1, 48000), group('Pithum Mortifier/Pithum Inferno', 2, 48000),
          group('Pithi Demolisher', 1, 60000), group('Pithior Terrorist', 1, 60000)],
         ((14776, 642754), (14777, 642757), (14778, 642866)), 642756,
         [group('Pithi Plunderer', 1, 60000, required=False), group('Pithum Inferno', 2, 60000, required=False),
          group('Pithi Destructor/Pithi Demolisher', 2, 55000, required=False), group('Pithum Eraser', 1, 55000, required=False),
          group('Pithi Destructor/Pithi Demolisher', 2, 63000), group('Pithum Mortifier', 1, 63000),
          group('Pithatis Enforcer/Pithatis Assaulter', 1, 63000)]),
        (2473, 500011, 'The_Score_(Angel_Cartel)_(Level_3)', 223035,
         [group('Gistii Ambusher/Gistii Nomad/Gistii Raider/Gistii Ruffian', 7, 30000),
          group('Gistior Defacer/Gistior Haunter', 2, 30000),
          group('Gistum Crusher/Gistum Defeater/Gistum Depredator/Gistum Smasher', 8, 30000)],
         ((14945, 650165), (14946, 650168), (14947, 650206)), 650167,
         [group('Gistii Ambusher/Gistii Raider/Gistii Thug', 6, 65000),
          group('Gistum Centurion/Gistum Liquidator/Gistum Marauder/Gistum Phalanx', 4, 65000),
          group('Gistatis Tribuni', 1, 65000),
          group('Gistii Ambusher/Gistii Outlaw/Gistii Raider/Gistii Thug', 2, 63000, required=False),
          group('Gistum Centurion/Gistum Liquidator/Gistum Marauder/Gistum Phalanx', 2, 63000, required=False)]),
    ):
        dungeon = read_encounter(mid, faction)
        if dungeon['difficulty'] != 3 or len(dungeon['rooms']) != 3 or len(dungeon.get('connections', [])) != 2:
            raise ValueError(f'Unreviewed multi-room mission {mid}')
        rooms = [dict(roomId=rid, entryObjectId=oid) for rid, oid in bindings]
        gates = [dict(fromObjectId=dungeon['connections'][0]['fromObjectID'], destinationRoomId=bindings[1][0]),
                 dict(fromObjectId=gate, destinationRoomId=bindings[2][0], requiresClear=True)]
        if {g['fromObjectId'] for g in gates} != {g['fromObjectID'] for g in dungeon['connections']}:
            raise ValueError(f'Unreviewed multi-room gates for mission {mid}')
        missions.append(dict(missionId=mid, level=3, difficulty=500, storyline=False, offerOrder=2,
                             source='https://wiki.eveuniversity.org/' + title, sourceRevision=f'oldid={revision}',
                             rooms=rooms, gates=gates,
                             waves=[dict(roomId=bindings[1][0], groups=roster), dict(roomId=bindings[2][0], groups=final)]))
