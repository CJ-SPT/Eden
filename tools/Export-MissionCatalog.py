"""Bind curated numeric mission facts to the read-only build-3662168 catalog.

Encounter quantities come from the linked EVE-Survival reports. Alternative
names represent alternatives, not additional ships. Offer pools, reward weights
and courier level assignments are explicit Eden policy, not recovered CCP rules.
No gameplay network access or client code execution is involved.
"""
import argparse
import json
import pathlib
import sqlite3


def export(catalog, output):
    db = sqlite3.connect(catalog.resolve().as_uri() + '?mode=ro', uri=True)

    def group(names, count, distance, **flags):
        ids = []
        for name in names.split('/'):
            rows = db.execute('SELECT Id FROM ItemType WHERE Name=?', (name,)).fetchall()
            if len(rows) != 1:
                raise ValueError(f'NPC name must resolve unambiguously: {name}: {rows}')
            ids.append(rows[0][0])
        return dict(types=ids, count=count, range=distance, **flags)

    missions = []

    def combat(mid, level, weight, report, revision, waves):
        missions.append(dict(missionId=mid, level=level, difficulty=weight, storyline=False,
                             source=f'https://eve-survival.org/?wakka={report}', sourceRevision=revision,
                             waves=[dict(groups=w) for w in waves]))

    combat(896, 1, 700, 'AngelCartelSpies1', '2017-11-17 14:30 UTC',
           [[group('Gistii Hijacker', 9, 25000)]])
    combat(897, 2, 500, 'AngelCartelSpies2', '2012-07-14 05:27 UTC',
           [[group('Gistii Impaler/Gistii Rogue/Gistii Hijacker/Gistii Raider/Gistii Hunter', 8, 48000)]])
    combat(890, 3, 500, 'AngelCartelSpies3', '2013-04-14 02:15 UTC',
           [[group('Gistior Defacer/Gistior Shatterer', 3, 30000), group('Gistum Smasher/Gistum Crusher', 4, 35000)]])
    combat(892, 4, 500, 'AngelCartelSpies4', '2017-04-12 02:00 UTC',
           [[group('Angel Stasis Tower', 1, 50000, required=False), group('Angel Heavy Missile Battery', 2, 50000, required=False),
             group('Gistatis Praefectus/Gistatis Tribunus', 4, 50000), group('Gist Warlord/Gist Saint/Gist Nephilim', 3, 50000)]])
    combat(916, 1, 0, 'EliminateaPirateNuisance1an', '2012-07-14 06:24 UTC',
           [[group('Gistii Nomad/Gistii Hijacker', 2, 20000)]])
    combat(920, 1, 0, 'EliminateaPirateNuisance1gu', '2017-11-17 13:40 UTC',
           [[group('Pithi Arrogator', 1, 45000), group('Pithi Imputor', 1, 45000)]])
    combat(2924, 1, 1000, 'Blockade1gu', '2017-10-16 08:49 UTC', [
        [group('Minmatar Stasis Tower', 1, 25000, required=False), group('Pithi Arrogator', 4, 25000),
         group('Pithi Plunderer/Pithi Wrecker', 1, 25000, trigger=True)],
        [group('Pithi Arrogator/Pithi Imputor', 6, 120000), group('Pithi Infiltrator', 1, 120000, trigger=True)],
        [group('Pithi Arrogator', 6, 115000), group('Pithi Despoiler/Pithi Saboteur', 1, 115000, trigger=True)],
        [group('Pithi Imputor/Pithi Arrogator', 8, 100000), group('Pithi Saboteur/Pithi Despoiler', 1, 100000, trigger=True)],
        [group('Pithi Saboteur/Pithi Despoiler', 4, 100000), group('Pithi Plunderer', 1, 100000, completesMission=True)]])
    combat(875, 1, 500, 'GuristaSpies1', '2012-07-14 06:31 UTC',
           [[group('Pithi Arrogator', 4, 20000), group('Pithi Arrogator', 3, 60000), group('Pithi Invader', 1, 60000)]])
    missions[-1]['rooms'] = [dict(roomId=2711, entryObjectId=53017), dict(roomId=2710, entryObjectId=53011)]
    missions[-1]['gates'] = [dict(fromObjectId=53016, destinationRoomId=2710)]
    missions[-1]['waves'][0]['roomId'] = 2710

    # The dated report describes variable reinforcement quantities. Use its upper bounds.
    # Select a highest-bounty battleship as the trigger deterministically, then bind that
    # identity explicitly rather than treating every alternative as a separate trigger.
    def trigger(names, count, distance, required=True):
        roster = group(names, count, distance, required=required)
        bounty_attribute = 481
        ids = roster['types']
        highest = max(ids, key=lambda tid: db.execute(
            'SELECT Value FROM TypeAttribute WHERE ItemTypeId=? AND AttributeId=?', (tid, bounty_attribute)).fetchone()[0])
        return [dict(types=[highest], count=1, range=distance, trigger=True, required=required),
                dict(types=[tid for tid in ids if tid != highest], count=count-1, range=distance, required=required)]

    combat(2537, 4, 1000, 'Assault4gu', '2017-10-16 08:49 UTC; completion clarification 2018-03-01 04:27 UTC', [
        [group('Dire Pithi Wrecker/Dire Pithi Plunderer', 2, 31000, required=False),
         *trigger('Pith Dismantler/Pith Obliterator/Pith Destroyer/Pith Eliminator', 4, 31000, required=False),
         group('Dire Pithum Inferno/Dire Pithum Mortifier', 4, 85000, required=False),
         group('Pithatis Assassin/Pithatis Assaulter', 3, 85000, required=False)],
        [group('Pithatis Assassin/Pithatis Enforcer/Pithatis Executor/Pithatis Revolter', 4, 120000, required=False),
         group('Pith Conquistador/Pith Eliminator/Pith Exterminator/Pith Destroyer', 4, 120000, required=False)],
        [group('Dire Pithi Invader/Dire Pithi Infiltrator', 2, 60000),
         group('Dire Pithum Abolisher/Dire Pithum Eraser', 2, 60000),
         group('Pith Dismantler/Pith Extinguisher', 3, 60000),
         group('Dire Pithum Annihilator/Dire Pithum Killer/Dire Pithum Murderer/Dire Pithum Nullifier', 2, 70000),
         *trigger('Pith Extinguisher/Pith Destroyer/Pith Eliminator/Pith Conquistador', 5, 70000),
         group('Dire Pithi Infiltrator/Dire Pithi Invader', 2, 110000),
         group('Pithior Guerilla/Pithior Renegade', 3, 110000),
         group('Pithatis Assaulter/Pithatis Enforcer', 3, 110000),
         group('Pith Exterminator/Pith Extinguisher/Pith Destroyer/Pith Conquistador', 2, 110000)],
        [group('Pithum Eraser/Pithum Inferno/Pithum Mortifier/Pithum Abolisher', 4, 40000),
         group('Pith Eliminator/Pith Exterminator/Pith Conquistador', 4, 40000)],
        [group('Pithum Eraser/Pithum Inferno/Pithum Mortifier', 2, 40000)]])
    missions[-1]['rooms'] = [dict(roomId=17283, entryObjectId=761873), dict(roomId=17284, entryObjectId=761876),
                             dict(roomId=17285, entryObjectId=761945)]
    missions[-1]['gates'] = [dict(fromObjectId=761874, destinationRoomId=17284),
                             dict(fromObjectId=761944, destinationRoomId=17285)]
    for index, wave in enumerate(missions[-1]['waves']):
        wave['roomId'] = 17284 if index < 2 else 17285

    # One explicit report variant per room; early Scarlet escape/implant scripts remain excluded.
    gate_key = db.execute("SELECT Id FROM ItemType WHERE Name='Gate Key'").fetchone()[0]
    serpentis, guristas, blood, angels, scarlet_faction = 500020, 500010, 500012, 500011, 500021
    combat(15237, 4, 1000, 'DreadPirateScarlet4', '2020-10-20 15:52 UTC', [
        [group('Corelatis Squad Leader', 3, 55000, required=False, enemyFaction=serpentis),
         group('Corelum Chief Safeguard', 2, 55000, required=False, enemyFaction=serpentis),
         group('Core Admiral/Core High Admiral', 4, 55000, required=False, enemyFaction=serpentis,
               lootTypeId=gate_key, lootQuantity=1)],
        [group('Pithatis Enforcer/Pithatis Death Dealer/Pithatis Revolter/Pithatis Assassin/Pithatis Executor/Pithatis Assaulter',
               5, 60000, required=False, enemyFaction=guristas),
         group('Pith Exterminator/Pith Eliminator', 4, 60000, required=False, enemyFaction=guristas)],
        [group('Corelior Artillery/Corelior Cannoneer', 3, 33000, required=False, trigger=True, enemyFaction=serpentis),
         group('Pith Conquistador/Pith Massacrer', 4, 33000, required=False, trigger=True, enemyFaction=guristas)],
        [group('Pith Eliminator/Pith Extinguisher', 4, 35000, required=False, enemyFaction=guristas)],
        [group('Anire Scarlet', 1, 84000, completesMission=True, enemyFaction=scarlet_faction),
         group('Angel Heavy Missile Battery', 1, 84000, required=False, enemyFaction=angels),
         group('Blood Heavy Missile Battery', 1, 84000, required=False, enemyFaction=blood),
         group('Guristas Cruise Missile Battery', 1, 84000, required=False, enemyFaction=guristas),
         group('Serpentis Cruise Missile Battery', 1, 84000, required=False, enemyFaction=serpentis),
         group('Dire Pithi Imputor', 2, 62000, required=False, enemyFaction=guristas),
         group('Dire Pithum Abolisher/Dire Pithum Annihilator', 3, 62000, required=False, enemyFaction=guristas),
         group('Pithatis Enforcer/Pithatis Executor', 4, 62000, required=False, enemyFaction=guristas),
         group('Corelatis Squad Leader/Corelatis Wing Leader', 4, 112000, required=False, enemyFaction=serpentis),
         group('Core Baron/Core Commodore', 4, 112000, required=False, trigger=True, enemyFaction=serpentis)],
        [group('Core Port Admiral/Core Rear Admiral', 4, 105000, required=False, trigger=True, enemyFaction=serpentis)],
        [group('Core Grand Admiral/Core High Admiral', 5, 106000, required=False, enemyFaction=serpentis)]])
    scarlet = missions[-1]
    scarlet['deadspace'] = False  # Report explicitly allows direct fleet warps into its normal-space pockets.
    scarlet['rooms'] = [dict(roomId=22286, entryObjectId=969328), dict(roomId=22287, entryObjectId=969330),
                         dict(roomId=22288, entryObjectId=969332), dict(roomId=22289, entryObjectId=969339)]
    scarlet['gates'] = [dict(fromObjectId=969329, destinationRoomId=22287,
                             key=dict(typeId=gate_key, quantity=1, consume=False)),
                         dict(fromObjectId=969331, destinationRoomId=22288),
                         dict(fromObjectId=969333, destinationRoomId=22289)]
    for wave, room in zip(scarlet['waves'], [22286, 22287, 22288, 22288, 22289, 22289, 22289]):
        wave['roomId'] = room

    # The updated University roster gives three elite frigates; the older report gives four.
    combat(1770, 3, 700, 'SpyStash3gu', '2017-02-22 13:34 UTC; EVE University oldid=218641 (2024-09-07)', [
        [group('Pithatis Assassin/Pithatis Death Dealer', 2, 35000, required=False),
         group('Pithum Inferno/Pithum Mortifier', 2, 35000, required=False),
         group('Dire Pithi Wrecker/Dire Pithi Plunderer', 3, 30000, required=False),
         group('Pithior Terrorist/Pithior Guerilla', 3, 30000, required=False),
         group('Guristas Light Missile Battery', 3, 35000, required=False)],
        [group('Pithatis Assassin', 2, 40000), group('Pithum Murderer', 1, 40000),
         group('Pithior Terrorist/Pithior Guerilla', 2, 35000),
         group('Guristas Heavy Missile Battery', 2, 40000, required=False),
         group('Tower Sentry Guristas II', 2, 40000, required=False)]])
    stash = missions[-1]
    stash['rooms'] = [dict(roomId=18410, entryObjectId=810929), dict(roomId=18411, entryObjectId=810931),
                      dict(roomId=18412, entryObjectId=810941)]
    stash['gates'] = [dict(fromObjectId=810928, destinationRoomId=18411),
                      dict(fromObjectId=810930, destinationRoomId=18412)]
    stash['containers'] = [dict(objectId=810942, typeId=22222, quantity=5, requiresClear=True)]
    for wave, room in zip(stash['waves'], [18411, 18412]):
        wave['roomId'] = room

    # The catalog retains an empty gate lobby omitted by the single-pocket gameplay report.
    combat(1226, 1, 500, 'HiddenStash1an', 'EVE University oldid=192015 (2022-08-27 15:59 UTC)', [
        [group('Gistii Hijacker', 8, 20000, required=False), group('Gistii Hijacker', 4, 25000, required=False)]])
    hidden = missions[-1]
    hidden['rooms'] = [dict(roomId=3399, entryObjectId=66426), dict(roomId=3400, entryObjectId=66471)]
    hidden['gates'] = [dict(fromObjectId=66425, destinationRoomId=3400)]
    hidden['waves'][0]['roomId'] = 3400
    hidden['structures'] = [dict(objectId=66472, required=True, completesMission=True,
                                 drop=dict(typeId=16044, quantity=15)),
                            *[dict(objectId=oid) for oid in [66431, 66432, 66433, 66434]]]

    for level, mid in enumerate([2912, 2913, 2914, 2915], 1):
        missions.append(dict(missionId=mid, level=level, difficulty=500, storyline=False,
                             source='build-3662168 client/missions; Eden courier offer policy', sourceRevision='3662168', waves=[]))
    for level, mid in enumerate([2919, 2920, 2921, 2922], 1):
        missions.append(dict(missionId=mid, level=level, difficulty=500, storyline=True,
                             source='build-3662168 client/missions; Eden storyline offer policy', sourceRevision='3662168', waves=[]))
    for m in missions:
        row = db.execute("SELECT Data FROM SourceRecord WHERE Dataset='client/missions' AND Key=?", (str(m['missionId']),)).fetchone()
        if not row:
            raise ValueError(f'Missing catalog mission {m["missionId"]}')
        data = json.loads(row[0])
        if m['waves']:
            dungeon = json.loads(db.execute("SELECT Data FROM SourceRecord WHERE Dataset='client/dungeons' AND Key=?",
                                           (str(data['killMission']['dungeonID']),)).fetchone()[0])
            if len(dungeon['rooms']) != len(m.get('rooms', [None])) or dungeon['difficulty'] != m['level']:
                raise ValueError(f'Mission {m["missionId"]} needs unsupported room or level handling')
        elif not data.get('courierMission'):
            raise ValueError(f'Mission {m["missionId"]} is not a courier mission')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(dict(build=3662168, missions=missions), indent=2) + '\n', encoding='utf-8')
    print(f'Exported {len(missions)} mission definitions')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', required=True, type=pathlib.Path)
    parser.add_argument('--output', type=pathlib.Path, default=pathlib.Path('src/Eden.World/Agents/missions-3662168.json'))
    args = parser.parse_args()
    export(args.catalog, args.output)
