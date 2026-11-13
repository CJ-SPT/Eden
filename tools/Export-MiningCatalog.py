"""Export pinned mining equipment, crystals, type lists and skill prerequisites for automated tests."""
import argparse
import json
import math
from pathlib import Path
import sqlite3


BUILD = 3662168
MERCOXIT = {11396, 17869, 17870}
GROUPS = {54, 464, 483, 482, 663, 737, 4138}

# Positive high-security locations from the four UniWiki ice-belt pages, November 9, 2026.
# Pochven entries are deliberately omitted. These are an admitted subset, not a complete retail distribution.
ICE_SYSTEMS = {
    5496: '''Actee Deninard Antollare Ardallabier Aydoteaux Carirgnottin Jaschercis Tolle Vaurent
             Brapelille Chelien Misneden Stegette Niballe'''.split(),
    5497: '''Uchomida Halaima Kamio Uotila Ahtulaima Gekutami Hentogaira Hurtoken Mitsolen Osmon Outuni
             Silen Sirseshin Vattuolen Wuos Aakari Elonaya Jotenen Kiskoken Oishami Piekura Yoma'''.split(),
    5498: '''Abudban Emolgranlan Endrulf Aderkan Asgeir Barkrik Dantbeinn Eygfe Finanar Gedugaud Hodrold
             Nakugard Nein Oppold Teonusude Varigne'''.split(),
    5499: '''Avada Gamis Gelhan Ihal Moh Orva Serad Arveyil Dihra Esescama Riavayed Afivad Arera Arshat Azizora
             Bashakru Clarelam Esteban Fabum Gosalav Isamm Jerma Knophtikoo Luromooh Martha Nalu Pedel Raren Warouh
             Agal Avyuh Hadji Iderion Manatirid Sigga Ervekam Geztic Gidali Keberz Molea Moniyyuku Moro Saloti Talidal
             Chanoun Dantan Jakri Kamda Koona Kothe Miah Munory Neburab Rayeret Turba Choga Ordion Anjedin Goram Ivih
             Jarzalad Kari Moutid Seil Erkinen'''.split(),
}

# Positive locations from UniWiki Gas_cloud_harvesting, read November 9, 2026.
# The source explicitly says these lists are incomplete; omitted regions are not a retail exclusion claim.
MYKO_REGIONS = {
    'Sister Nebula': ['Aridia', 'Curse', 'Derelik', 'Kador', 'Khanid', 'Kor-Azor', 'Omist', 'Solitude', 'Wicked Creek'],
    'Helix Nebula': ['Aridia', 'Curse', 'Derelik', 'Khanid', 'Kor-Azor', 'Solitude'],
    'Wild Nebula': ['Aridia', 'Insmother', 'Kor-Azor', 'Curse', 'Omist', 'Solitude', 'Tash-Murkon'],
    'Blackeye Nebula': ['Aridia', 'Curse', 'Khanid', 'Kor-Azor', 'Omist', 'Solitude'],
    'Sunspark Nebula': ['Black Rise', 'Lonetrek', 'Outer Passage', 'Sinq Laison', 'The Citadel', 'The Forge', 'The Spire'],
    'Diablo Nebula': ['Black Rise', 'Everyshore', 'Lonetrek', 'Outer Passage', 'Sinq Laison', 'The Citadel', 'The Forge',
                     'Vale of the Silent'],
    'Smoking Nebula': ['Black Rise', 'Etherium Reach', 'Everyshore', 'Lonetrek', 'Outer Passage', 'Perrigen Falls',
                      'Sinq Laison', 'The Citadel', 'The Spire'],
    'Ring Nebula': ['Black Rise', 'Etherium Reach', 'Everyshore', 'Lonetrek', 'Malpais', 'Outer Passage', 'Perrigen Falls',
                   'Sinq Laison', 'The Citadel'],
    'Calabash Nebula': ['Domain', 'Genesis', 'Placid', 'Solitude'],
    'Glass Nebula': ['Domain', 'Essence', 'Solitude', 'Pure Blind', 'Placid', 'Genesis'],
    'Bright Nebula': ['Essence', 'Fountain', 'Placid', 'Tenal', 'Venal'],
    'Sparking Nebula': ['Domain', 'Essence', 'Fountain', 'Genesis', 'Placid', 'Pure Blind', 'Tenal'],
    'Ghost Nebula': ['Curse', 'Derelik', 'Devoid', 'The Bleak Lands', 'Heimatar', 'Molden Heath'],
    'Eagle Nebula': ['Curse', 'Derelik', 'The Bleak Lands', 'Devoid', 'Heimatar', 'Insmother', 'Metropolis', 'Molden Heath',
                    'Tenerifis'],
    'Flame Nebula': ['Curse', 'Derelik', 'Devoid', 'The Bleak Lands', 'Great Wildlands', 'Detorid', 'Heimatar', 'Insmother',
                    'Metropolis', 'Omist', 'Tenerifis'],
    'Pipe Nebula': ['Curse', 'Derelik', 'Devoid', 'The Bleak Lands', 'Heimatar', 'Immensea', 'Metropolis', 'Tenerifis'],
}


# Positive low-security locations for the eight undefended nebula templates.
CYTO_REGIONS = {
    'Bandit Nebula': 'The Forge',
    'Rapture Nebula': 'Molden Heath',
    'Profiteer Nebula': 'Lonetrek',
    'Forgotten Nebula': 'Placid',
    'Phoenix Nebula': 'Solitude',
    'Saintly Nebula': 'Heimatar',
    'Emerald Nebula': 'Derelik',
    'Crimson Nebula': 'Aridia',
}


# Native Fullerite reservoir templates, grouped by documented wormhole access.
RESERVOIRS = {
    **{dungeon: [1, 2, 3, 4] for dungeon in range(3555, 3560)},
    **{dungeon: [3, 4, 5, 6] for dungeon in range(3560, 3562)},
    **{dungeon: [5, 6] for dungeon in range(3562, 3564)},
}


def export(catalog_path, output, sites_output):
    if catalog_path.resolve() in (output.resolve(), sites_output.resolve()) or output.resolve() == sites_output.resolve():
        raise ValueError('The output must not overwrite the source catalog')
    with sqlite3.connect(catalog_path.resolve().as_uri() + '?mode=ro', uri=True) as catalog:
        def record(dataset, key):
            row = catalog.execute('SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?',
                                  (dataset, str(key))).fetchone()
            return json.loads(row[0]) if row else {}

        if record('sde/_sde', '"sde"').get('buildNumber') != BUILD:
            raise ValueError('Expected the pinned build-3662168 catalog')
        pending = {type_id for type_id, group in catalog.execute('SELECT Id,GroupId FROM ItemType')
                   if group in GROUPS and record('client/types', type_id).get('published') == 1}
        pending.update({601, 1230, 1223, 17478, 17480, 17476, 32880, 32918, 3410, 90727, 90728, 25268, 91174, 33697, 28444})
        pending.update(MERCOXIT)
        for ore in MERCOXIT:
            pending.update(material['materialTypeID'] for material in record('client/typematerials', ore)['materials'])
        regions = {name: region_id for region_id, name in catalog.execute('SELECT Id,Name FROM Region')}
        systems = {name: system_id for system_id, name in catalog.execute('SELECT Id,Name FROM SolarSystem')}
        sites = []
        for key, raw in catalog.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='client/dungeons' ORDER BY CAST(Key AS INTEGER)"):
            dungeon = json.loads(raw)
            name_id = dungeon.get('dungeonNameID', 0)
            name = record('client/localization_fsd_en-us', name_id).get('text', '')
            ice = int(key) in ICE_SYSTEMS
            cyto = name in CYTO_REGIONS
            reservoir = RESERVOIRS.get(int(key))
            if ((not ice and ((name not in MYKO_REGIONS and not cyto and reservoir is None)
                              or dungeon.get('archetypeID') != 30))
                    or (ice and dungeon.get('archetypeID') != 28) or len(dungeon.get('rooms', {})) != 1):
                continue
            objects = list(next(iter(dungeon['rooms'].values())).get('objects', {}).values())
            if reservoir is None and any(o.get('entities') for o in objects):
                raise ValueError('Unexpected defenders in ' + name)
            entry = next(o['position'] for o in objects if o.get('objectID') == dungeon.get('entryObjectID'))
            deposits = []
            for obj in sorted(objects, key=lambda o: o['objectID']):
                type_id = obj.get('typeID', 0)
                row = catalog.execute('SELECT Name,GroupId,Radius FROM ItemType WHERE Id=?', (type_id,)).fetchone()
                if not row or row[1] != (465 if ice else 711):
                    continue
                gas_kind = 'Fullerite' if reservoir else 'Cytoserocin' if cyto else 'Mykoserocin'
                if (not ice and gas_kind not in row[0]) or obj.get('radius', 0) <= 0:
                    raise ValueError('Unexpected gas resource in ' + name)
                quantity = int(obj['radius'])
                if not ice and quantity != obj['radius']:
                    raise ValueError('Nonintegral gas deposit')
                # Eden projects native dungeon stock to whole blocks and uses its ore geometry rule for ice.
                radius = math.sqrt(quantity * 1000) if ice else row[2] or 0
                deposits.append(dict(typeId=type_id, quantity=quantity, radius=radius,
                                     position={axis: obj['position'].get(axis, 0) - entry.get(axis, 0) for axis in 'xyz'}))
                pending.add(type_id)
            site = dict(dungeonId=int(key), nameId=name_id, name=name, family=1 if ice else 2,
                        difficulty=dungeon.get('difficulty', 1),
                        regions=[] if ice or reservoir else
                        [regions[r] for r in ([CYTO_REGIONS[name]] if cyto else MYKO_REGIONS[name])],
                        deposits=deposits)
            if cyto:
                site['lowSecurityOnly'] = True
            if reservoir:
                site['wormholeClasses'] = reservoir
            if ice:
                site['systems'] = [systems[s] for s in ICE_SYSTEMS[int(key)]]
            sites.append(site)
        if len(sites) != len(MYKO_REGIONS) + len(CYTO_REGIONS) + len(ICE_SYSTEMS) + len(RESERVOIRS):
            raise ValueError('Missing or ambiguous mining site templates')
        types, records, attributes, effects = [], {}, set(), set()
        while pending:
            type_id = min(pending)
            pending.remove(type_id)
            if ('client/types', type_id) in records:
                continue
            row = catalog.execute('SELECT t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity '
                                  'FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?', (type_id,)).fetchone()
            name, group, group_name, category, mass, radius, capacity = row
            values = list(catalog.execute('SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? ORDER BY AttributeId',
                                         (type_id,)))
            types.append(dict(id=type_id, name=name, groupId=group, groupName=group_name, categoryId=category,
                              mass=mass, radius=radius, capacity=capacity, attributes=[dict(id=a, value=v) for a, v in values]))
            attributes.update(a for a, _ in values)
            for a, v in values:
                if a in (182, 183, 184, 1285, 1289, 1290) and v:
                    pending.add(int(v))
                if a == 3148 and v:
                    records['client/typelist', int(v)] = record('client/typelist', int(v))
            for dataset in ('client/types', 'client/typedogma'):
                records[dataset, type_id] = record(dataset, type_id)
            if type_id in MERCOXIT:
                records['client/typematerials', type_id] = record('client/typematerials', type_id)
            effects.update(e['effectID'] for e in records['client/typedogma', type_id].get('dogmaEffects', []))
        for effect in sorted(effects):
            data = record('client/dogmaeffects', effect)
            records['client/dogmaeffects', effect] = data
            for modifier in data.get('modifierInfo', []):
                attributes.update(modifier[key] for key in ('modifiedAttributeID', 'modifyingAttributeID') if key in modifier)
        while attributes:
            attribute = attributes.pop()
            if ('client/dogmaattributes', attribute) in records:
                continue
            data = record('client/dogmaattributes', attribute)
            records['client/dogmaattributes', attribute] = data
            attributes.update(data[key] for key in ('minAttributeID', 'maxAttributeID') if data.get(key) is not None)
        data = dict(build=BUILD, types=sorted(types, key=lambda t: t['id']),
                    records=[dict(dataset=d, key=k, data=v) for (d, k), v in sorted(records.items())])
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(data, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
        sites_output.parent.mkdir(parents=True, exist_ok=True)
        sites_output.write_text(json.dumps(dict(build=BUILD, sites=sites), indent=2) + '\n', encoding='utf-8')
        print('Exported', len(types), 'mining types and', len(sites), 'resource sites')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--catalog', required=True, type=Path)
    parser.add_argument('--output', type=Path, default=Path('tests/Eden.Protocol.Tests/Fixtures/MiningCatalog.json'))
    parser.add_argument('--sites', type=Path, default=Path('src/Eden.World/Mining/mining-sites-3662168.json'))
    args = parser.parse_args()
    export(args.catalog, args.output, args.sites)
