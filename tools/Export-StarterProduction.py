"""Export the pinned starter-production recipes, fits, suppliers and ore-source evidence."""

import argparse
from collections import defaultdict, deque
import json
from pathlib import Path
import sqlite3


HULLS = [603, 32880, 648]
FITS = {
    603: [561, 561, 561, 399, 439, 2046, 215],
    32880: [483, 483, 399, 439, 2046],
    648: [10836, 12056, 1317, 1317],
}
ORES = [1230, 1228, 18, 20, 21, 1223, 22]


def export(catalog_path, fixture_path, audit_path):
    with sqlite3.connect(catalog_path.resolve().as_uri() + "?mode=ro", uri=True) as db:
        graph = defaultdict(set)
        for origin, destination in db.execute(
                "SELECT a.SolarSystemId,b.SolarSystemId FROM Stargate a JOIN Stargate b ON b.Id=a.DestinationId"):
            graph[origin].add(destination)
        routes = {30000142: [30000142]}
        queue = deque(routes)
        while queue:
            origin = queue.popleft()
            for destination in sorted(graph[origin]):
                if destination not in routes:
                    routes[destination] = routes[origin] + [destination]
                    queue.append(destination)

        def route(system):
            if system not in routes:
                raise ValueError(f"Required supplier or ore source in system {system} is unreachable from Jita by stargates")
            return routes[system]

        def source(dataset, key):
            row = db.execute("SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?", (dataset, str(key))).fetchone()
            return json.loads(row[0]) if row else {}

        products = set(HULLS).union(*(set(items) for items in FITS.values()))
        recipes = []
        records = {}
        records["client/systems", 30000142] = source("client/systems", 30000142)
        pending = set(products) | set(ORES) | {601, 670, 3651, 3638, 3380, 3386, 3385}
        for key, data in db.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='client/blueprints' ORDER BY Key"):
            blueprint = json.loads(data)
            activity = blueprint.get("activities", {}).get("manufacturing", {})
            output = activity.get("products", [])
            if len(output) == 1 and output[0]["typeID"] in products:
                blueprint_id = int(key.strip('"'))
                pending.add(blueprint_id)
                pending.update(m["typeID"] for m in activity.get("materials", []))
                pending.update(s["typeID"] for s in activity.get("skills", []))
                records["client/blueprints", blueprint_id] = blueprint
                recipes.append({"blueprintId": blueprint_id, **activity})
        if {p["typeID"] for r in recipes for p in r["products"]} != products:
            raise ValueError("Every cohort hull, fitted module and consumable must have a blueprint")

        attributes = set()
        types = []
        while pending:
            type_id = min(pending)
            pending.remove(type_id)
            if ("client/types", type_id) in records:
                continue
            row = db.execute(
                "SELECT t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity "
                "FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?", (type_id,)
            ).fetchone()
            name, group, group_name, category, mass, radius, capacity = row
            values = list(db.execute("SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? ORDER BY AttributeId", (type_id,)))
            types.append({"id": type_id, "name": name, "groupId": group, "groupName": group_name,
                          "categoryId": category, "mass": mass, "radius": radius, "capacity": capacity,
                          "attributes": [{"id": k, "value": v} for k, v in values]})
            attributes.update(k for k, _ in values)
            definition = source("client/types", type_id)
            if category == 6 and definition.get("wreckTypeID"):
                pending.add(definition["wreckTypeID"])
            pending.update(int(v) for k, v in values if k in (182, 183, 184, 1285, 1289, 1290) and v > 0)
            for dataset in ("client/types", "client/typedogma", "client/typematerials"):
                data = source(dataset, type_id)
                if data:
                    records[dataset, type_id] = data
            dogma = records.get(("client/typedogma", type_id), {})
            for effect in dogma.get("dogmaEffects", []):
                effect_id = effect["effectID"]
                definition = source("client/dogmaeffects", effect_id)
                records["client/dogmaeffects", effect_id] = definition
                for modifier in definition.get("modifierInfo", []):
                    attributes.update(modifier[k] for k in ("modifiedAttributeID", "modifyingAttributeID") if modifier.get(k))
                    if modifier.get("skillTypeID"):
                        pending.add(modifier["skillTypeID"])

        while attributes:
            attribute = attributes.pop()
            if ("client/dogmaattributes", attribute) in records:
                continue
            definition = source("client/dogmaattributes", attribute)
            records["client/dogmaattributes", attribute] = definition
            attributes.update(definition[k] for k in ("minAttributeID", "maxAttributeID") if definition.get(k))
        wanted = {r["blueprintId"] for r in recipes} | {t["id"] for t in types if t["categoryId"] == 16}
        for type_id in wanted:
            definition = records["client/types", type_id]
            if definition.get("published") != 1 or not definition.get("marketGroupID") or definition.get("basePrice", 0) <= 0:
                raise ValueError(f"Required NPC input {type_id} cannot be purchased through the market")
        suppliers = []
        missing = set(wanted)
        corporations = [(int(k), json.loads(d)) for k, d in db.execute(
            "SELECT Key,Data FROM SourceRecord WHERE Dataset='client/npccorporations' ORDER BY CAST(Key AS INTEGER)"
        )]
        while missing:
            eligible = [(len(missing & {int(k) for k, v in d.get("corporationTrades", {}).items() if v > 0}), corp, d)
                        for corp, d in corporations]
            score, corp, data = max(eligible, key=lambda entry: (entry[0], -entry[1]))
            if score == 0:
                raise ValueError(f"No catalog NPC supplier for {sorted(missing)}")
            offered = {int(k) for k, v in data["corporationTrades"].items() if v > 0} & wanted
            stations = [row for row in db.execute("SELECT Id,SolarSystemId,OperationId FROM Station WHERE OwnerId=? ORDER BY Id", (corp,))
                        if 7 in source("client/stationoperations", row[2]).get("services", [])]
            if not stations:
                raise ValueError(f"Supplier {corp} has no station")
            suppliers.append({"corporationId": corp, "stationId": stations[0][0], "systemId": stations[0][1],
                              "routeFromJita": route(stations[0][1]), "types": sorted(offered)})
            # The fixture carries only the relevant authenticated catalog offers.
            records["client/npccorporations", corp] = {
                "factionID": data.get("factionID"),
                "corporationTrades": {str(k): data["corporationTrades"][str(k)] for k in sorted(offered)},
            }
            missing -= offered

        sources = {}
        for key, data in db.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='client/asteroidcountbytypeidbeltid' ORDER BY Key"):
            for type_id, count in json.loads(data).items():
                ore = int(type_id)
                if ore in ORES and count > 0 and ore not in sources:
                    materials = source("client/typematerials", ore)
                    if materials.get("randomizedMaterials"):
                        raise ValueError(f"Ore {ore} is not a fixed reprocessing source")
                    system_id, system_name = db.execute(
                        "SELECT s.Id,s.Name FROM Celestial b JOIN SolarSystem s ON s.Id=b.SolarSystemId WHERE b.Id=?", (int(key),)
                    ).fetchone()
                    sources[ore] = {"systemId": system_id, "systemName": system_name, "routeFromJita": route(system_id),
                                    "oreTypeId": ore, "beltId": int(key), "count": count,
                                    "materials": materials["materials"]}
        if set(sources) != set(ORES):
            raise ValueError(f"Missing populated belt sources: {set(ORES) - set(sources)}")
        required = {m["typeID"] for r in recipes for m in r["materials"]}
        available = {m["materialTypeID"] for s in sources.values() for m in s["materials"]}
        if not required <= available:
            raise ValueError(f"Unobtainable production inputs: {required - available}")
        fixture = {"build": 3662168, "types": sorted(types, key=lambda t: t["id"]),
                   "records": [{"dataset": d, "key": k, "data": v} for (d, k), v in sorted(records.items())]}
        audit = {"build": 3662168, "hulls": HULLS, "fits": FITS,
                 "recipes": sorted(recipes, key=lambda r: r["blueprintId"]),
                 "suppliers": suppliers, "oreSources": [sources[o] for o in ORES]}
        for path, data in ((fixture_path, fixture), (audit_path, audit)):
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, required=True)
    parser.add_argument("--fixture", type=Path, default=Path("tests/Eden.Protocol.Tests/Fixtures/StarterProductionCatalog.json"))
    parser.add_argument("--audit", type=Path, default=Path("docs/starter-production.json"))
    args = parser.parse_args()
    export(args.catalog, args.fixture, args.audit)
