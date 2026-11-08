"""Extract numeric encounter facts from saved EVE University revisions and the pinned catalog."""
import argparse
import json
import pathlib
import re
import sqlite3


def clean(value):
    value = re.sub(r"\[\[(?:[^]|]*\|)?([^]]+)\]\]", r"\1", value)
    return re.sub(r"\s+", " ", value.replace("’", "'").strip())


def templates(text):
    pattern = re.compile(r"\{\{\s*(NPCTableHead|NPCTableRow|NPCTableSeparator)\s*\|", re.I)
    for match in pattern.finditer(text):
        cursor, depth = match.end(), 1
        begin = cursor
        while cursor < len(text) and depth:
            if text[cursor:cursor + 2] == '{{':
                depth += 1
                cursor += 2
            elif text[cursor:cursor + 2] == '}}':
                depth -= 1
                cursor += 2
            else:
                cursor += 1
        body = text[begin:cursor - 2]
        parts, start, nesting = [], 0, 0
        for i, char in enumerate(body):
            if body[i:i + 2] in ('{{', '[['):
                nesting += 1
            elif body[i:i + 2] in ('}}', ']]'):
                nesting -= 1
            if char == '|' and nesting == 0:
                parts.append(clean(body[start:i]))
                start = i + 1
        parts.append(clean(body[start:]))
        yield match.start(), match.group(1).lower(), parts


def import_encounters(catalog_path, references, output):
    pages = json.loads(references.read_text(encoding='utf-8'))
    db = sqlite3.connect(catalog_path.resolve().as_uri() + '?mode=ro', uri=True)
    types = {row[0]: dict(id=row[0], name=row[1], group=row[2], groupName=row[3]) for row in db.execute(
        'SELECT t.Id,t.Name,t.GroupId,g.Name FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE g.CategoryId=11')}
    # Later irregular drone definitions reuse ordinary deadspace English names.
    # Prefer deadspace/asteroid profiles rather than whichever row SQLite returned last.
    ordered_types = sorted(types.values(), key=lambda t: ('Deadspace' in t['groupName'] or 'Asteroid' in t['groupName'], -t['id']))
    by_name = {clean(t['name']).lower(): t['id'] for t in ordered_types}
    aliases = {'Pithii ': 'Pithi ', 'Matriach ': 'Matriarch ', 'Coreli Guardian Chief ': 'Corelum Guardian Chief ',
               'Gistastis ': 'Gistatis ', 'Gistatsis ': 'Gistatis ', 'Gistatus ': 'Gistatis ',
               'Colerior ': 'Corelior ', 'Corelium ': 'Corelum ', 'Ursurper': 'Usurper',
               'Gurilla': 'Guerilla', 'Corpier ': 'Corpior ', 'Deviner': 'Diviner',
               'Desintegrator': 'Disintegrator', 'Infestor': 'Infester',
               'Gistum Legatus': 'Gistatis Legatus', 'Elder Corpii Fanatic': 'Corpatis Fanatic',
               'Elder Corpii Exorcist': 'Corpatis Exorcist', 'Corpum Seer': 'Corpatis Seer',
               'Corpus Seer': 'Corpatis Seer', 'Corpior Defacer': 'Corpior Devoter',
               'Corpum Commander': 'Corpus Crimson Commander', 'Corpum Engraver': 'Corpum Arch Engraver',
               'Dismantler Alvi': 'Dismantler Alvior', 'Dread Gurista ': 'Dread Guristas ',
               'Nulifier': 'Nullifier', 'Mishape': 'Misshape', 'Partroller': 'Patroller',
               'Captain Sentinel': 'Captain Sentry', "Sansha's Nation Dreadnought": "Sansha's Dreadnought",
               "True Sansha's Nation Supercarrier": "True Sansha's Supercarrier", 'Angel Domination Titan': 'Domination Titan'}
    unresolved = []
    room_types = {}
    for key, raw in db.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='sde/dungeons'"):
        name = json.loads(raw).get('name', {}).get('en', '').replace('Blood Raider ', 'Blood ')
        row = db.execute("SELECT Data FROM SourceRecord WHERE Dataset='client/dungeons' AND Key=?", (key,)).fetchone()
        if row:
            room_types.setdefault(name, set()).update(o.get('typeID') for r in json.loads(row[0]).get('rooms', {}).values()
                                                     for o in r.get('objects', {}).values())

    def resolve(description, title, npc_kind):
        description = clean(description)
        if description == 'Elder Corp':
            description = 'Elder Corpum'
        description = description.replace(' (rare)', '')
        for original, correction in aliases.items():
            description = description.replace(original, correction)
        descriptions = [description]
        for match in list(re.finditer(r'\(([^)]+)\)', description)):
            descriptions.extend(description[:match.start()] + item + description[match.end():]
                                for item in match[1].split('/'))
        found = set()
        for value in descriptions:
            if value.lower() in by_name:
                found.add(by_name[value.lower()])
            alternatives = [part.strip().rstrip('.') for part in value.split('/') if part.strip().rstrip('.')]
            if not alternatives:
                continue
            first, last = alternatives[0].split(), alternatives[-1].split()
            for alt in alternatives:
                for prefix in range(len(first) + 1):
                    for suffix in range(len(last) + 1):
                        candidate = ' '.join(first[:prefix] + alt.split() + (last[-suffix:] if suffix else []))
                        if candidate.lower() in by_name:
                            found.add(by_name[candidate.lower()])
        if not found:
            for inner in re.findall(r'\(([^)]+)\)', description):
                if inner.lower() in by_name:
                    found.add(by_name[inner.lower()])
        hull_names = ('Frigate', 'Destroyer', 'Cruiser', 'Battlecruiser', 'Battleship')
        hull = next((i for i, hull in enumerate(hull_names) if npc_kind.lower().endswith(hull.lower())), None)
        prefixes = {'Angel': ('Gistii', 'Gistior', 'Gistum', 'Gistatis', 'Gist'),
                    'Blood': ('Corpii', 'Corpior', 'Corpum', 'Corpatis', 'Corpus'),
                    'Guristas': ('Pithi', 'Pithior', 'Pithum', 'Pithatis', 'Pith'),
                    'Sansha': ('Centii', 'Centior', 'Centum', 'Centatis', 'Centus'),
                    'Serpentis': ('Coreli', 'Corelior', 'Corelum', 'Corelatis', 'Core')}
        faction = title.split()[0]
        if faction in prefixes and hull is not None and re.fullmatch(r'(?:Elder )?(?:Corp\w*|Core\w*)', description):
            prefix = prefixes[faction][hull]
            elite = 'Elder ' if description.startswith('Elder ') and any(
                t['name'].startswith('Elder ' + prefix + ' ') for t in types.values()) else ''
            description = elite + prefix
        if not found and faction in prefixes and hull is not None:
            prefix = prefixes[faction][hull]
            for value in descriptions:
                value = re.sub(r'^(?:Elder )?(?:Gist\w*|Corp\w*|Cent\w*|Core\w*|Coler\w*|Pith\w*)\s+', '', value)
                for part in value.split('/'):
                    candidate = prefix + ' ' + part.strip().rstrip('.')
                    if candidate.lower() in by_name:
                        found.add(by_name[candidate.lower()])
        if not found and faction == 'Drone':
            suffix = ('Alvi', 'Alvior', 'Alvum', 'Alvatis', 'Alvus')[hull or 0]
            for value in descriptions:
                first = value.split('/')[0].strip().split()
                for part in value.split('/'):
                    for prefix_length in range(len(first)):
                        candidate = ' '.join(first[:prefix_length] + part.strip().split() + [suffix])
                        if candidate.lower() in by_name:
                            found.add(by_name[candidate.lower()])
        if found:
            return sorted(found)
        if npc_kind.lower() == 'sentry':
            sentries = [i for i in room_types.get(title, ()) if types.get(i, {}).get('group') == 383]
            if sentries:
                return sorted(sentries)
        faction_terms = {'Angel': ('angel', 'gist'), 'Blood': ('blood', 'corp'),
                         'Guristas': ('gurista', 'pith'), 'Sansha': ('sansha', 'cent'),
                         'Serpentis': ('serpentis', 'core'), 'Drone': ('drone', 'alv', 'strain')}
        terms = faction_terms.get(faction, ())
        candidates = {t['name'].lower(): i for i, t in types.items()
                      if any(term in (t['groupName'] + ' ' + t['name']).lower() for term in terms)}
        # Generic commander labels resolve only within the stated faction and
        # hull class. Similar spellings must never promote a frigate to a titan.
        commander = {'Angel': 'Domination', 'Blood': 'Dark Blood', 'Guristas': 'Dread Guristas',
                     'Sansha': 'True Sansha', 'Serpentis': 'Shadow Serpentis', 'Drone': 'Sentient'}
        if hull is not None and description.startswith(commander.get(faction, '\0')):
            family = [t['id'] for t in types.values() if 'Commander' in t['groupName']
                      and t['groupName'].lower().endswith(hull_names[hull].lower())
                      and t['name'].startswith(commander[faction])
                      and any(term in t['groupName'].lower() for term in terms)]
            if family:
                return sorted(family)
        # Some reports identify a family rather than a flavor. Preserve the
        # reported hull class so a generic frigate cannot resolve to an overseer battleship.
        prefix = description.lower().rstrip(' .')
        family = [i for name, i in candidates.items() if name.startswith(prefix + ' ')
                  and hull is not None and types[i]['groupName'].lower().endswith(hull_names[hull].lower())]
        if family and len(prefix) >= 6:
            return sorted(family)
        return []

    encounters = []
    for title, page in sorted(pages.items()):
        text = page.get('text', '')
        if not text:
            unresolved.append(dict(site=title, problem='missing report'))
            continue
        variants, waves, current = [], [], None
        for position, kind, fields in templates(text):
            if kind != 'npctablerow':
                heading = fields[0] if fields else ''
                if kind == 'npctableseparator':
                    if current and re.search(r'rare|possible|commander', heading, re.I):
                        current['rareContext'] = True
                    continue
                if current and current['groups']:
                    waves.append(current)
                initial = bool(re.search(r'initial|on grid|at arrival', heading, re.I))
                first = bool(re.search(r'^first (?:spawn|wave)$', heading, re.I))
                if initial and waves and any(w.get('initial') for w in waves):
                    variants.append(waves)
                    waves = []
                elif first and waves and not any(w.get('initial') for w in waves):
                    variants.append(waves)
                    waves = []
                current = dict(initial=initial, heading=heading, groups=[])
                continue
            if len(fields) < 3 or current is None:
                continue
            positional = [field for field in fields if not re.match(r'[a-zA-Z_0-9]+\s*=', field)]
            if len(positional) < 3:
                continue
            npc_kind, quantity, name = positional[:3]
            if npc_kind.lower() == 'structure':
                continue
            rare = current.get('rareContext', False) or quantity.startswith('0') or bool(
                re.search(r'bonus|rare|commander|boss', current['heading'], re.I))
            if re.search(r'commander|dreadnought|titan|carrier|supercarrier', npc_kind, re.I):
                rare = True
            if re.match(r'Domination|Dark Blood|Dread Guristas|True Sansha|Shadow Serpentis|Sentient', name):
                rare = True
            ids = resolve(name, title, npc_kind)
            numbers = [int(n) for n in re.findall(r'\d+', quantity)]
            if ids and not numbers and rare:
                numbers = [0, 1]
            if ids and not numbers and npc_kind.lower() == 'sentry':
                numbers = [len([i for i in room_types.get(title, ()) if i in ids])]
            if not ids or not numbers:
                unresolved.append(dict(site=title, heading=current['heading'], name=name,
                                       problem='NPC identity or quantity', rare=rare))
                continue
            minimum, maximum = numbers[0], numbers[-1]
            if rare:
                minimum = 0
                maximum = max(1, maximum)
            trigger = next((f.split('=', 1)[1] for f in fields[3:] if re.match(r'trigger\s*=', f, re.I)), '')
            current['groups'].append(dict(minimum=minimum, maximum=maximum, types=ids,
                                          trigger=bool(trigger), optional=rare, sentry=npc_kind.lower() == 'sentry'))
        if current and current['groups']:
            waves.append(current)
        if waves:
            variants.append(waves)
        variants = [variant for variant in variants if any(not g['optional'] for w in variant for g in w['groups'])]
        for variant in variants:
            for wave in variant:
                wave.pop('initial', None)
                wave.pop('rareContext', None)
                delay = re.search(r'timed.*?(\d+)\s*seconds?', wave['heading'], re.I)
                wave['delaySeconds'] = int(delay[1]) if delay else 0
        locations = re.search(r'\|\s*location\s*=([^\n}]+)', text, re.I)
        encounters.append(dict(name=title, source='https://wiki.eveuniversity.org/' + title.replace(' ', '_'),
                               revision=page['revision'], locations=locations[1] if locations else '', variants=variants))
    # Four missing reports use an explicitly authored recipe. The documented
    # Sansha high-tier wave curve supplies quantities; each faction uses its own
    # strongest third of catalog deadspace hulls, selected by catalog bounty.
    model = next(e for e in encounters if e['name'] == 'Sansha Forsaken Sanctum')
    reconstructed = {'Angel Forsaken Sanctum': 'Angel Cartel', 'Blood Forsaken Sanctum': 'Blood Raiders',
                     'Guristas Forsaken Sanctum': 'Guristas', 'Teeming Drone Horde': 'Rogue Drone'}
    for title, faction in reconstructed.items():
        waves = []
        for wave in model['variants'][0]:
            groups = []
            for group in wave['groups']:
                if group['optional']:
                    continue
                prototype = types[group['types'][0]]
                hull = prototype['groupName'].split()[-1]
                group_name = 'Deadspace ' + faction + ' ' + hull
                candidates = [t['id'] for t in types.values() if t['groupName'].lower() == group_name.lower()]
                ranked = sorted(candidates, key=lambda i: db.execute(
                    'SELECT COALESCE(MAX(Value),0) FROM TypeAttribute WHERE ItemTypeId=? AND AttributeId=481', (i,)).fetchone()[0])
                if not ranked:
                    raise ValueError('Missing reconstructed pool ' + group_name)
                groups.append(dict(group, types=ranked[len(ranked) * 2 // 3:]))
            if groups:
                waves.append(dict(heading=wave['heading'], groups=groups))
        encounters.append(dict(name=title, source=model['source'], revision=model['revision'], locations='Null',
                               reconstructed=True, variants=[waves]))
    hideaway = next(e for e in encounters if e['name'] == 'Guristas Hideaway')
    # EVE-Survival's dated report distinguishes the telescope and bunker layouts.
    # Preserve wiki-reported optional commanders, and replace ordinary counts
    # with the four groups explicitly reported for each physical layout.
    rare = [g for v in hideaway['variants'] for w in v for g in w['groups'] if g['optional']]
    variants = []
    for final_names in ('Pithi Infiltrator/Pithi Invader', 'Pithi Wrecker/Pithi Plunderer'):
        waves = []
        for index, names in enumerate(('Pithi Arrogator/Pithi Imputor', 'Pithi Infiltrator/Pithi Invader',
                                       'Pithi Arrogator/Pithi Imputor', final_names)):
            waves.append(dict(heading='Initial' if index == 0 else 'Wave ' + str(index), delaySeconds=0, groups=[
                dict(minimum=1 if index < 2 else 2, maximum=2 if index < 2 else 3,
                     types=resolve(names, 'Guristas Hideaway', 'Frigate'), trigger=False, optional=False, sentry=False)]))
        waves[-1]['groups'].extend(rare)
        variants.append(waves)
    hideaway['variants'] = variants
    hideaway['layoutSource'] = 'https://eve-survival.org/?wakka=GuristasHideaway (2015-10-06 14:41:54 UTC)'
    bindings = {'Guristas Hideaway': {2134: 0, 2154: 1, 14211: 0, 14212: 1},
                'Guristas Haven': {2283: 1, 2307: 0}, 'Guristas Sanctum': {2321: 0, 2342: 1}}
    for encounter in encounters:
        ids = []
        for key, raw in db.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='sde/dungeons'"):
            summary = json.loads(raw)
            if summary.get('archetypeID') != 24 or summary.get('name', {}).get('en', '').replace('Blood Raider ', 'Blood ') != encounter['name']:
                continue
            row = db.execute("SELECT Data FROM SourceRecord WHERE Dataset='client/dungeons' AND Key=?", (key,)).fetchone()
            if row and json.loads(row[0]).get('entryTypeID') == 28356:
                ids.append(int(key))
        # Stable authored bindings remain explicit in the exported facts. They
        # are not evidence that a report names a particular catalog dungeon ID.
        encounter['dungeonVariants'] = {key: bindings.get(encounter['name'], {}).get(key, index % len(encounter['variants']))
                                        for index, key in enumerate(sorted(ids))}
    result = dict(build=3662168, encounters=encounters, unresolved=unresolved)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
    print(json.dumps(dict(reports=len(encounters), variants=sum(len(e['variants']) for e in encounters),
                         unresolved=len(unresolved), required=sum(not item.get('rare', False) for item in unresolved))))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', required=True, type=pathlib.Path)
    parser.add_argument('--references', required=True, type=pathlib.Path)
    parser.add_argument('--output', required=True, type=pathlib.Path)
    args = parser.parse_args()
    import_encounters(args.catalog, args.references, args.output)
