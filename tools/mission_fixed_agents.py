"""Compile complete native one-time Data Center assignments, without rotating them into ordinary pools."""
import collections
import json

# Build-3662168 Criminal Tags, distinct from unrelated faction insignia groups.
CRIMINAL_TAG_GROUP = 370


def data_center_definitions(db):
    def read(dataset, key):
        row = db.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?', (dataset, str(key))).fetchone()
        return json.loads(row[0]) if row else None

    missions = {int(key): json.loads(raw) for key, raw in db.execute(
        "SELECT Key,Data FROM SourceRecord WHERE Dataset='client/missions'")}
    selected = {int(key) for key, raw in db.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='sde/missions'")
                if json.loads(raw).get('name', {}).get('en', '').casefold() == 'keeping crime in check'}
    assignments = collections.defaultdict(list)
    for key, raw in db.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='client/npccharacters'"):
        npc = json.loads(raw)
        agent = npc.get('agent') or {}
        ids = agent.get('storyLineMissions') or []
        if not selected.intersection(ids) or npc.get('deleted') == 1:
            continue
        if (len(ids) != 1 or agent.get('agentType') != 8 or type(agent.get('agentLevel')) is not int
                or not 1 <= agent['agentLevel'] <= 4):
            raise ValueError(f'Unreviewed Data Center assignment: {key}')
        space = read('client/agentsinspace', key)
        dungeon = read('client/dungeons', (space or {}).get('dungeonID', 0))
        matching = [o for r in (dungeon or {}).get('rooms', {}).values() for o in r.get('objects', {}).values()
                    if o.get('typeID') == (space or {}).get('typeID')]
        if not space or not space.get('solarSystemID') or len(matching) != 1 or not matching[0].get('position'):
            raise ValueError(f'Missing native Data Center agent geometry: {key}')
        assignments[ids[0]].append((int(key), agent['agentLevel']))
    definitions = []
    for mid in sorted(selected):
        mission = missions[mid]
        cargo = mission.get('courierMission') or {}
        item = db.execute('SELECT GroupId,Name FROM ItemType WHERE Id=?', (cargo.get('objectiveTypeID', 0),)).fetchone()
        levels = {level for _, level in assignments[mid]}
        rewards = mission.get('missionRewards') or {}
        if (len(levels) != 1 or mission['contentTemplate'] != 'agent.missionTemplatizedContent_StorylineTradeMission'
                or type(cargo.get('objectiveSingleton')) is not int or cargo['objectiveSingleton'] != 0
                or type(cargo.get('objectiveQuantity')) is not int
                or cargo.get('objectiveQuantity') != (3 if item and item[1].endswith(' Copper Tag') else 20)
                or not item or item[0] != CRIMINAL_TAG_GROUP
                or type(mission.get('expirationTime', 10080)) is not int or mission.get('expirationTime', 10080) != 10080
                or mission.get('hasStandingRewards') != 1
                or any(mission.get(k) for k in ['nodeGraphID', 'clientObjectives', 'contentTags', 'initialAgentGiftTypeID'])
                or any(r for k, r in rewards.items() if k in ('reward', 'bonusReward'))):
            raise ValueError(f'Unreviewed Data Center mission objective, reward or level: {mid}')
        definitions.append(dict(missionId=mid, level=levels.pop(), difficulty=500, storyline=True, trade=True,
                                fixedAgentIds=sorted(aid for aid, _ in assignments[mid]), offerOrder=1,
                                source='https://wiki.eveuniversity.org/Data_centers',
                                sourceRevision='University oldid=224297; build-3662168 assignments, tags and geometry; Eden reward policy',
                                waves=[]))
    if len(definitions) != 50 or sum(len(m['fixedAgentIds']) for m in definitions) != 57:
        raise ValueError('Data Center catalog coverage changed; review every native assignment')
    names = {db.execute('SELECT Name FROM ItemType WHERE Id=?',
                        (missions[m['missionId']]['courierMission']['objectiveTypeID'],)).fetchone()[0] for m in definitions}
    expected = {f'{faction} {grade} Tag' for faction in ['Angel', 'Blood', 'Guristas', 'Sansha', 'Serpentis']
                for grade in ['Copper', 'Bronze', 'Silver', 'Brass', 'Palladium', 'Gold', 'Electrum', 'Crystal', 'Platinum', 'Diamond']}
    if names != expected:
        raise ValueError('Every native criminal tag grade must retain its matching acquisition family')
    return definitions
