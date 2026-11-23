"""Build the epic arc objective bindings against the installed build-3662168 catalog."""

import argparse
import json
import sqlite3
from functools import partial
from pathlib import Path

from mission_content import source


ARCS = {29: 64, 40: 40, 48: 24, 52: 27, 53: 19, 55: 20, 56: 22, 128: 3, 132: 8}
ISK_TYPE = 29
TEMPLATES = {'KillMission', 'CourierMission', 'TradeMission', 'TalkToAgentMission', 'AgentInteractionMission'}


def npc_group(db, name, count, distance, npc_group=None, **flags):
    rows = db.execute('SELECT Id FROM ItemType WHERE Name=? AND (? IS NULL OR GroupId=?)',
                      (name, npc_group, npc_group)).fetchall()
    if len(rows) != 1:
        raise ValueError(f'Expected one NPC for {name!r}: {rows}')
    return dict(types=[rows[0][0]], count=count, range=distance, **flags)


def export(catalog, output):
    from epic_blood import definitions as blood
    from epic_empire import definitions as empire
    from epic_pirates import definitions as pirates
    from epic_training import definitions as training

    with sqlite3.connect(catalog.resolve().as_uri() + '?mode=ro', uri=True) as db:
        objective = {}
        for family in (blood, empire, pirates, training):
            for scene in family(db, partial(npc_group, db)):
                mid = scene['missionId']
                if mid in objective:
                    raise ValueError(f'Duplicate epic mission binding {mid}')
                objective[mid] = scene

        output_missions = []
        for arc_id, count in ARCS.items():
            arc = source(db, 'client/epicarcs', arc_id)
            if len(arc['missions']) != count or arc['arcRestartInterval'] != 129600:
                raise ValueError(f'Invalid graph size or replay interval for arc {arc_id}')
            graph = {int(mid): node for mid, node in arc['missions'].items()}
            validate_graph(arc_id, graph)
            for mid, node in sorted(graph.items()):
                mission = source(db, 'client/missions', mid)
                template = mission['contentTemplate'].rsplit('_EpicArc', 1)[-1]
                agent_id = node['agentID']
                agent = source(db, 'client/npccharacters', agent_id)
                if template not in TEMPLATES or agent.get('deleted') or agent['agent']['agentType'] != 10:
                    raise ValueError(f'Unsupported epic mission {mid} or agent {agent_id}')
                if template == 'AgentInteractionMission' and len(node.get('nextMissions', [])) < 2:
                    raise ValueError(f'Choice mission {mid} does not have branches')
                scene = objective.pop(mid)
                scene.update(epicArcId=arc_id, level=agent['agent']['agentLevel'], storyline=False,
                             difficulty=500 if agent['agent']['agentLevel'] == 4 else 250, fixedAgentIds=[agent_id])
                if template == 'TradeMission' and not scene.get('dungeonId'):
                    scene['trade'] = True
                validate_locations(db, scene, mission, agent)
                reward = mission.get('missionRewards', {}).get('reward')
                if reward and reward.get('rewardTypeID', ISK_TYPE) != ISK_TYPE:
                    scene['itemReward'] = dict(types=[reward['rewardTypeID']], quantity=reward['rewardQuantity'])
                output_missions.append(scene)
        if objective:
            raise ValueError(f'Mission bindings outside the journal graphs: {sorted(objective)}')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(dict(build=3662168, missions=output_missions), indent=2) + '\n', encoding='utf-8')
    print(f'Exported {len(output_missions)} epic missions across {len(ARCS)} arcs')


def validate_graph(arc_id, graph):
    incoming = {mid: set() for mid in graph}
    for mid, node in graph.items():
        for following in node.get('nextMissions', []):
            incoming[following].add(mid)
        if node.get('failMissionID', mid) not in graph:
            raise ValueError(f'Invalid failure node for {mid}')
    ready = [mid for mid, refs in incoming.items() if not refs]
    completed = set()
    while ready:
        mid = ready.pop()
        completed.add(mid)
        for following in graph[mid].get('nextMissions', []):
            incoming[following].remove(mid)
            if not incoming[following]:
                ready.append(following)
    if completed != graph.keys():
        raise ValueError(f'Epic arc {arc_id} has unreachable or cyclic stages')


def validate_locations(db, scene, mission, agent):
    mid = scene['missionId']
    agent_id = scene['fixedAgentIds'][0]
    contact = source(db, 'client/agentsinspace', agent_id)
    if scene.get('deliverToAgent') and not contact:
        raise ValueError(f'Mission {mid} requires an authored space contact')
    for station in (agent.get('stationID'), scene.get('deliveryStationId'), scene.get('pickupStationId')):
        if station and not db.execute('SELECT 1 FROM Station WHERE Id=?', (station,)).fetchone():
            raise ValueError(f'Mission {mid} uses an unavailable station {station}')
    dungeon_id = scene.get('dungeonId', mission.get('killMission', {}).get('dungeonID'))
    if not dungeon_id:
        return
    dungeon = source(db, 'client/dungeons', dungeon_id)
    rooms = [room['roomId'] for room in scene['rooms']]
    location = {obj['objectID']: int(room) for room, data in dungeon['rooms'].items() for obj in data['objects'].values()}
    routes = {room: [] for room in rooms}
    for gate in scene.get('gates', []):
        routes[location[gate['fromObjectId']]].append(gate['destinationRoomId'])
    found = set()
    pending = [rooms[0]]
    while pending:
        room = pending.pop()
        if room not in found:
            found.add(room)
            pending.extend(routes[room])
    if found != set(rooms):
        raise ValueError(f'Mission {mid} binds a room unreachable from its warp-in')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('src/Eden.World/Agents/epic-arc-missions-3662168.json'))
    args = parser.parse_args()
    export(args.catalog, args.output)
