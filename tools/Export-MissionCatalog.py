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
            if len(dungeon['rooms']) != 1 or dungeon['difficulty'] != m['level']:
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
