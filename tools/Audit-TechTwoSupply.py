"""Audit how an economy world can obtain the inputs to build-3662168 T2 industry."""

import argparse
from collections import Counter, defaultdict, deque
import json
from pathlib import Path
import sqlite3

BUILD = 3662168
JITA = 30000142
MARKET_SERVICE = 7
# Ordinary ore groups in MiningResourceGroup, excluding moon deposits.
ORE_GROUPS = {
    450, 451, 452, 453, 454, 455, 456, 457, 458, 459, 460, 461, 462, 467, 469,
    1911, 2006, 2024, 4029, 4030, 4031, 4161, 4513, 4514, 4515, 4516, 4568,
    4755, 4756, 4757, 4758, 4759, 4857, 5083, 5084, 5085, 5086,
}
ICE_GROUP = 465
MERCOXIT_GROUP = 468
MOON_ORE_GROUPS = {1884, 1920, 1921, 1922, 1923}
PLANETARY_RESOURCE_CATEGORY = 42
INDUSTRIAL_CATALOG_PRICES = {11395, 12109, 36977, 36978, 37032, 37034, 45540, 45541, 45542}
ENCRYPTION_LOOT_FACTIONS = {21790: (500010,), 21791: (500011,), 23087: (500012, 500019), 23121: (500020,)}
CONDENSER_LOOT_FACTIONS = {
    57445: (500012, 500019), 57446: (500010, 500019),
    57447: (500010, 500011, 500020), 57448: (500011, 500020),
}
RACIAL_MOLECULAR_FORMULAS = set(range(57496, 57500)) | set(range(57503, 57507))
COMPOSITE_FORMULAS = 1888
HYBRID_FORMULAS = 1889
COMPRESSED_PRISMATICITE = 90307
RESEARCH_AGENT = 4
DATACORE_GROUP = 333
REQUIRED_SKILL = 182
TECH_LEVEL = 422
BOOTSTRAP_NAMES = (
    "Athanor", "Tatara", "Athanor Upwell Quantum Core", "Tatara Upwell Quantum Core",
    "Standup Reprocessing Facility I", "Standup Moon Drill I", "Standup Composite Reactor I",
    "Standup Hybrid Reactor I", "Standup Biochemical Reactor I",
    "Survey Probe Launcher I", "Discovery Survey Probe I", "Deep Core Mining Laser I",
    "Helium Fuel Block", "Hydrogen Fuel Block", "Oxygen Fuel Block", "Nitrogen Fuel Block",
    "Deep Core Mining", "Reactions", "Mass Reactions", "Advanced Mass Reactions",
)
SAMPLE_NAMES = ("Miner II", "Ishtar", "Acolyte II", "Ark", "Anshar", "Nomad", "Rhea")
REPOSITORY = Path(__file__).resolve().parents[1]


def records(db, dataset):
    return {int(key.strip('"')): json.loads(data) for key, data in db.execute(
        "SELECT Key, Data FROM SourceRecord WHERE Dataset=? ORDER BY CAST(trim(Key, '""') AS INTEGER)", (dataset,)
    )}


def reachable_systems(db):
    gates = defaultdict(list)
    for origin, destination in db.execute(
        "SELECT a.SolarSystemId,b.SolarSystemId FROM Stargate a JOIN Stargate b ON b.Id=a.DestinationId"
    ):
        gates[origin].append(destination)
    reachable = {JITA}
    pending = deque(reachable)
    while pending:
        for destination in gates[pending.popleft()]:
            if destination not in reachable:
                reachable.add(destination)
                pending.append(destination)
    return reachable


def audit(db, production, market, mining):
    types = records(db, "client/types")
    blueprints = records(db, "client/blueprints")
    type_materials = records(db, "client/typematerials")
    names = dict(db.execute("SELECT Id,Name FROM ItemType"))
    ids = {name: type_id for type_id, name in names.items()}
    groups = dict(db.execute("SELECT Id,GroupId FROM ItemType"))
    categories = dict(db.execute("SELECT Id,CategoryId FROM ItemGroup"))
    tech = dict(db.execute("SELECT ItemTypeId,Value FROM TypeAttribute WHERE AttributeId=?", (TECH_LEVEL,)))
    skill_requirements = dict(db.execute(
        "SELECT ItemTypeId,Value FROM TypeAttribute WHERE AttributeId=?", (REQUIRED_SKILL,)
    ))
    reachable = reachable_systems(db)
    if production["build"] != BUILD or mining["build"] != BUILD:
        raise ValueError(f"Expected build {BUILD} production and mining data")
    dataset_names = (
        "client/blueprints", "client/types", "client/typedogma", "client/typematerials", "client/schematics",
        "client/npccorporations", "client/asteroidcountbytypeidbeltid", "sde/mapMoons", "sde/npcCharacters",
        "client/npccharacters", "client/stationoperations", "client/solarsystemcontent",
    )
    fingerprints = dict(db.execute(
        "SELECT Name,Sha256 FROM SourceDataset ORDER BY Name"
    ))
    if any(fingerprints.get(name) != expected for name, expected in production["sourceFingerprints"].items()):
        raise ValueError("The catalog fingerprints differ from the production audit")

    paths = defaultdict(list)

    def add(output, source, reference, inputs=(), milestone=1):
        paths[output].append({
            "source": source, "reference": reference, "inputs": sorted(set(inputs)), "milestone": milestone,
        })

    def inputs(activity):
        return [m["typeID"] for m in activity.get("materials", []) + activity.get("skills", [])]

    def describe(type_id):
        return {"typeId": type_id, "name": names[type_id]}

    def reachable_market_stations():
        operations = records(db, "client/stationoperations")
        stations = {}
        for station, owner, system, operation in db.execute(
            "SELECT Id,OwnerId,SolarSystemId,OperationId FROM Station ORDER BY Id"
        ):
            if system in reachable and MARKET_SERVICE in operations.get(operation, {}).get("services", []):
                stations.setdefault(owner, station)
        return stations

    market_stations = reachable_market_stations()
    if not market_stations:
        raise ValueError("The pinned catalog has no reachable market stations")
    sell_quotes = {q["typeId"] for q in market["quotes"] if q.get("sellCents", 0) and q["sellCents"] > 0}
    npc_offers = {}
    for corporation, data in records(db, "client/npccorporations").items():
        if corporation not in market_stations:
            continue
        for type_key, amount in data.get("corporationTrades", {}).items():
            type_id = int(type_key)
            definition = types.get(type_id, {})
            if amount > 0 and definition.get("published") == 1 and definition.get("marketGroupID"):
                npc_offers.setdefault(type_id, market_stations[corporation])
    for type_id, station in sorted(npc_offers.items()):
        definition = types[type_id]
        manufactured = blueprints.get(type_id, {}).get("activities", {}).get("manufacturing", {})
        if (tech.get(type_id, 0) > 1 and type_id in blueprints) or any(
            tech.get(p["typeID"], 0) > 1 for p in manufactured.get("products", [])
        ):
            continue
        if type_id in sell_quotes:
            add(type_id, "institutional sell offer", station)
        elif type_id in INDUSTRIAL_CATALOG_PRICES and definition.get("basePrice", 0) > 0:
            add(type_id, "institutional sell offer at catalog price", station)

    for skillbook, factions in ENCRYPTION_LOOT_FACTIONS.items():
        if types[skillbook].get("published") == 1:
            for faction in factions:
                add(skillbook, "pirate data site", faction)

    for condenser, factions in CONDENSER_LOOT_FACTIONS.items():
        if types[condenser].get("published") == 1:
            for faction in factions:
                add(condenser, "pirate data Info Shard", faction, milestone=5)

    ore_systems = dict(db.execute("SELECT Id,SolarSystemId FROM Celestial WHERE GroupId=9"))
    for belt, deposits in records(db, "client/asteroidcountbytypeidbeltid").items():
        if ore_systems.get(belt) not in reachable:
            continue
        for ore_key, count in deposits.items():
            ore = int(ore_key)
            group = groups.get(ore)
            if count > 0 and (group in ORE_GROUPS or group == MERCOXIT_GROUP) and not paths.get(ore):
                add(ore, "asteroid belt", belt, milestone=4 if group == MERCOXIT_GROUP else 1)
    region_systems = defaultdict(list)
    for system_id, region_id in db.execute(
        "SELECT s.Id,c.RegionId FROM SolarSystem s JOIN Constellation c ON c.Id=s.ConstellationId"
    ):
        if system_id in reachable:
            region_systems[region_id].append(system_id)
    system_records = records(db, "client/solarsystemcontent")
    for site in mining["sites"]:
        if site["family"] == 1 and any(system in reachable for system in site["systems"]):
            for deposit in site["deposits"]:
                ore = deposit["typeId"]
                if groups.get(ore) == ICE_GROUP and not paths.get(ore):
                    add(ore, "ice field", site["dungeonId"])
        if site["family"] == 2 and any(
            system_id in system_records and (
                not site.get("lowSecurityOnly") or 0 < system_records[system_id].get("security", 1) < 0.45
            )
            for region in site["regions"] for system_id in region_systems[region]
        ):
            for deposit in site["deposits"]:
                gas = deposit["typeId"]
                if not paths.get(gas):
                    add(gas, "gas signature", site["dungeonId"], milestone=5 if site.get("lowSecurityOnly") else 1)

    for type_id, data in types.items():
        if data.get("published") != 1:
            continue
        if groups.get(type_id) in MOON_ORE_GROUPS and data.get("variationParentTypeID", type_id) == type_id:
            if not names.get(type_id, "").startswith("Compressed "):
                add(type_id, "moon extraction", groups[type_id], milestone=2)
        if names.get(type_id, "").startswith("Fullerite-C"):
            add(type_id, "wormhole gas site", None, milestone=5)
        if categories.get(groups.get(type_id)) == PLANETARY_RESOURCE_CATEGORY:
            add(type_id, "planetary extraction", None)

    for schematic, data in records(db, "client/schematics").items():
        required = [int(type_id) for type_id, row in data["types"].items() if row["isInput"] == 1]
        for type_id, row in data["types"].items():
            if row["isInput"] == 0:
                add(int(type_id), "planetary schematic", schematic, required)

    character_skills = records(db, "client/npccharacters")
    datacores = {skill_requirements[type_id]: type_id for type_id in sorted(groups, reverse=True)
                 if groups[type_id] == DATACORE_GROUP and type_id in skill_requirements
                 and types[type_id].get("published") == 1}
    station_systems = dict(db.execute("SELECT Id,SolarSystemId FROM Station"))
    for character, data in records(db, "sde/npcCharacters").items():
        agent = data.get("agent") or {}
        if agent.get("agentTypeID") != RESEARCH_AGENT or station_systems.get(data.get("locationID")) not in reachable:
            continue
        for row in character_skills.get(character, {}).get("skills", []):
            skill = row["typeID"]
            if skill in datacores and not any(p["source"] == "R&D agent" for p in paths[datacores[skill]]):
                add(datacores[skill], "R&D agent", character, [skill])

    for recipe in production["recipes"]:
        if recipe["admitted"]:
            blueprint = recipe["blueprintId"]
            add(recipe["productId"], "manufacturing", blueprint, [blueprint, *inputs(recipe["activity"])])

    moon_alchemy_outputs = set()
    counts = Counter()
    for blueprint, data in blueprints.items():
        if types.get(blueprint, {}).get("published") != 1:
            continue
        activities = data["activities"]
        if invention := activities.get("invention"):
            for product in invention.get("products", []):
                add(product["typeID"], "invention", blueprint, [blueprint, *inputs(invention)])
        if not (reaction := activities.get("reaction")):
            continue
        required = inputs(reaction)
        group = groups[blueprint]
        if group == COMPOSITE_FORMULAS and COMPRESSED_PRISMATICITE not in required:
            milestone = 3
            label = "moon composite reaction"
        elif group == HYBRID_FORMULAS:
            milestone = 5
            label = "hybrid reaction"
        elif blueprint in RACIAL_MOLECULAR_FORMULAS:
            milestone = 5
            label = "racial molecular-forged reaction"
        else:
            milestone = 6
            label = "Prismaticite reaction" if COMPRESSED_PRISMATICITE in required else "other reaction"
        counts[label] += 1
        for product in reaction["products"]:
            output = product["typeID"]
            add(output, label, blueprint, [blueprint, *required], milestone)
            if milestone == 3 and names[output].startswith("Unrefined "):
                moon_alchemy_outputs.add(output)

    for source, data in type_materials.items():
        if data.get("randomizedMaterials") or source not in paths:
            continue
        if not any(p["source"] in {"asteroid belt", "ice field", "moon extraction"} for p in paths[source]):
            if source not in moon_alchemy_outputs:
                continue
        for material in data.get("materials", []):
            if material["quantity"] > 0 and types[source].get("portionSize", 0) > 0:
                add(material["materialTypeID"], "reprocessing", source, [source],
                    milestone=3 if source in moon_alchemy_outputs else 1)

    def acquisition(limit):
        # A route only closes once all its dependencies have independent paths.
        available = set()
        while True:
            added = {item for item, routes in paths.items() if any(
                p["milestone"] <= limit and available.issuperset(p["inputs"]) for p in routes
            )} - available
            if not added:
                return available
            available.update(added)

    def blockers(item, available, seen=frozenset()):
        if item in available:
            return set()
        if item in seen:
            return {item}
        routes = [p for p in paths[item] if p["milestone"] <= 5]
        if not routes:
            return {item}
        alternatives = [set().union(*(blockers(i, available, seen | {item}) for i in p["inputs"])) for p in routes]
        return min(alternatives, key=lambda candidate: (len(candidate), sorted(candidate)))

    milestones = {str(m): acquisition(m) for m in (1, 2, 3, 4, 5)}
    baseline, planned = milestones["1"], milestones["5"]
    t2 = sorted({r["productId"] for r in production["recipes"] if r["admitted"] and tech.get(r["productId"]) == 2})
    roots = sorted(set(t2) | {ids[name] for name in BOOTSTRAP_NAMES})
    relevant = set(roots)
    queue = deque(roots)
    while queue:
        for path in paths[queue.popleft()]:
            for item in path["inputs"]:
                if item not in relevant:
                    relevant.add(item)
                    queue.append(item)
    return {
        "build": BUILD,
        "model": "Recipe reachability under each planned milestone. Invention includes access to a copy source.",
        "scope": "Catalog and modeled acquisition paths. Travel, fitted equipment, standings, prices and throughput "
                 "still require gameplay validation. Unmodeled sources appear as blockers.",
        "sourceFingerprints": {dataset: fingerprints[dataset] for dataset in dataset_names},
        "reactionCounts": dict(sorted(counts.items())),
        "reachableSystems": len(reachable),
        "techTwoProducts": len(t2),
        "techTwoDirectMorphite": sum(11399 in path["inputs"] for product in t2
                                    for path in paths[product] if path["source"] == "manufacturing"),
        "closedTechTwoPathsByMilestone": {m: len(available.intersection(t2)) for m, available in milestones.items()},
        "bootstrap": [{**describe(ids[name]), "closed": ids[name] in baseline,
                       "blockers": [describe(i) for i in sorted(blockers(ids[name], baseline))]}
                      for name in BOOTSTRAP_NAMES],
        "samples": [{**describe(ids[name]), "closedAfterMilestone": next(
            (int(m) for m, available in milestones.items() if ids[name] in available), None
        ), "blockers": [describe(i) for i in sorted(blockers(ids[name], planned))]} for name in SAMPLE_NAMES],
        "remainingBlockers": [describe(item) for item in sorted(set().union(
            *(blockers(product, planned) for product in t2 if product not in planned)
        ))],
        "acquisition": [{**describe(item), "paths": sorted(
            paths[item], key=lambda p: (p["milestone"], p["source"], p["reference"] or 0)
        )}
                        for item in sorted(relevant)],
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=REPOSITORY / "docs/tech-two-supply-audit.json")
    args = parser.parse_args()
    production = json.loads((REPOSITORY / "docs/production-catalog.json").read_text(encoding="utf-8"))
    market = json.loads((REPOSITORY / "src/Eden.World/Market/market-prices.json").read_text(encoding="utf-8"))
    mining = json.loads((REPOSITORY / "src/Eden.World/Mining/mining-sites-3662168.json").read_text(encoding="utf-8"))
    with sqlite3.connect(args.catalog.resolve().as_uri() + "?mode=ro", uri=True) as catalog:
        result = audit(catalog, production, market, mining)
    # Each acquisition record stays on one line for reviewing catalog diffs.
    acquisition = result.pop("acquisition")
    header = json.dumps(result, indent=2, ensure_ascii=False)
    rows = ",\n".join("    " + json.dumps(row, ensure_ascii=False) for row in acquisition)
    output = header.removesuffix("\n}") + ',\n  "acquisition": [\n' + rows + "\n  ]\n}\n"
    args.output.write_text(output, encoding="utf-8", newline="\n")
