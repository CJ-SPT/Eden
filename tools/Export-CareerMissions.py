"""Validate career stages against the build-3662168 agent assignments and export the ordered sequences."""
import argparse
import json
from pathlib import Path
import re
import sqlite3
from functools import partial

from mission_content import source


def spawn_group(db, names, count, distance, npc_group=None, **flags):
    ids = []
    for name in names.split('/'):
        matches = db.execute('SELECT Id FROM ItemType WHERE Name=? AND (? IS NULL OR GroupId=?)',
                             (name, npc_group, npc_group)).fetchall()
        if len(matches) != 1:
            raise ValueError(f'Career NPC name must resolve unambiguously: {name}: {matches}')
        ids.append(matches[0][0])
    return dict(types=ids, count=count, range=distance, **flags)


def stage(db, mid):
    name = source(db, 'sde/missions', mid)['name']['en']
    match = re.search(r'\((\d+) of (\d+)\)$', name)
    if not match:
        raise ValueError(f'Career mission {mid} has no ordered stage in {name!r}')
    return tuple(map(int, match.groups()))


def export(catalog, output):
    from career_enforcer import definitions as enforcer
    from career_explorer import definitions as explorer
    from career_industry import definitions as industry
    from career_soldier import definitions as soldier

    with sqlite3.connect(catalog.resolve().as_uri() + '?mode=ro', uri=True) as db:
        authored = {}
        for cohort in (industry, explorer, enforcer, soldier):
            for definition in cohort(db, partial(spawn_group, db)):
                mid = definition['missionId']
                if mid in authored:
                    raise ValueError(f'Career mission {mid} has multiple authored stages')
                authored[mid] = definition
        chains = {}
        assigned = 0
        for key, raw in db.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='client/npccharacters'"):
            agent = json.loads(raw)
            record = agent.get('agent') or {}
            if record.get('agentType') != 12 or agent.get('deleted'):
                continue
            ids = record['storyLineMissions']
            size = len(ids)
            ordered = tuple(sorted(ids, key=lambda mid: stage(db, mid)))
            if size != (5 if record['agentDivisionID'] == 26 else 10) or not agent.get('stationID') or record['agentLevel'] != 1:
                raise ValueError(f'Unexpected career assignment for {key}')
            if [stage(db, mid) for mid in ordered] != [(part, size) for part in range(1, size + 1)]:
                raise ValueError(f'Incomplete career chain for {key}')
            station = db.execute('SELECT Id FROM Station WHERE Id=?', (agent['stationID'],)).fetchone()
            if not station:
                raise ValueError(f'Missing career station for {key}')
            chains.setdefault(ordered, []).append(int(key))
            assigned += 1
        if len(chains) != 20 or assigned != 100 or len(authored) != 180 or set(authored) != {i for chain in chains for i in chain}:
            raise ValueError('Career content must cover all 180 stages, 20 chains and 100 pinned agents')

        result = []
        for chain, agents in sorted(chains.items()):
            for part, mid in enumerate(chain):
                native = source(db, 'client/missions', mid)
                if native['expirationTime'] != 0 or native['fixedLpRewardAlpha'] != 0 or native['fixedLpRewardOmega'] != 0:
                    raise ValueError(f'Unsupported deadline or loyalty payout on career mission {mid}')
                definition = authored[mid]
                definition.update(level=1, storyline=False, career=True, fixedAgentIds=sorted(agents), chain=dict(
                    rootMissionId=chain[0], previousMissionId=chain[part - 1] if part else 0,
                    nextMissionId=chain[part + 1] if part + 1 < len(chain) else 0,
                ))
                definition.setdefault('difficulty', 0)
                definition.setdefault('waves', [])
                if native['contentTemplate'].endswith('TradeMission') and not definition.get('dungeonId'):
                    definition.setdefault('trade', True)
                item = native.get('missionRewards', {}).get('reward')
                if item:
                    definition.setdefault('itemReward', dict(types=[item['rewardTypeID']], quantity=item['rewardQuantity']))
                result.append(definition)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(dict(build=3662168, missions=result), indent=2) + '\n', encoding='utf-8')
    print(f'Exported {len(result)} career missions for {assigned} agents')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=Path('src/Eden.World/Agents/career-missions-3662168.json'))
    args = parser.parse_args()
    export(args.catalog, args.output)
