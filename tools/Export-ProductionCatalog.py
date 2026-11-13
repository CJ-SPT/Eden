"""Export manufacturing admission and representative native recipes from the pinned catalog."""

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import sqlite3


SAMPLES = [
    "Miner II", "Hobgoblin I", "Hobgoblin II", "1400mm Howitzer Artillery I",
    "Mega Beam Laser I", "Scourge Heavy Missile", "Small Core Defense Field Extender I",
    "Titanium Diborite Armor Plate", "Capital Construction Parts", "Nitrogen Fuel Block",
    "Raven", "Tengu", "Astrahus", "Naglfar", "Ark", "Anshar", "Nomad", "Rhea",
]


def export(path, output):
    with sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True) as db:
        types = {
            i: {"id": i, "name": n, "groupId": g, "categoryId": c}
            for i, n, g, c in db.execute(
                "SELECT t.Id,t.Name,t.GroupId,g.CategoryId FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId"
            )
        }
        definitions = {int(k): json.loads(v) for k, v in db.execute(
            "SELECT Key,Data FROM SourceRecord WHERE Dataset='client/types'"
        )}
        records = [(k, json.loads(v)) for k, v in db.execute(
            "SELECT Key,Data FROM SourceRecord WHERE Dataset='client/blueprints' ORDER BY Key"
        )]
        recipes = []
        fingerprint = hashlib.sha256()
        for key, blueprint in records:
            fingerprint.update(json.dumps([key, blueprint], sort_keys=True, separators=(",", ":")).encode())
            activity = blueprint.get("activities", {}).get("manufacturing")
            if activity is None:
                continue
            bp = int(key.strip('"'))
            products = activity.get("products", [])
            product = products[0] if len(products) == 1 else {}
            product_id = product.get("typeID", 0)
            materials = activity.get("materials", [])
            skills = activity.get("skills", [])
            reason = None
            if len(products) != 1 or not 0 < product.get("quantity", 0) <= 2147483647 or activity.get("time", 0) <= 0:
                reason = "invalid-output-or-time"
            elif definitions.get(bp, {}).get("published") != 1:
                reason = "unpublished-blueprint"
            elif definitions.get(product_id, {}).get("published") != 1:
                reason = "unpublished-product"
            elif types.get(bp, {}).get("categoryId") != 9 or product_id not in types:
                reason = "invalid-type"
            elif not materials or any(m["typeID"] not in types or not 0 < m["quantity"] <= 2147483647 for m in materials):
                reason = "invalid-materials"
            elif len({m["typeID"] for m in materials}) != len(materials):
                reason = "duplicate-materials"
            elif any(types.get(s["typeID"], {}).get("categoryId") != 16 or not 1 <= s["level"] <= 5 for s in skills):
                reason = "invalid-skills"
            recipes.append({
                "blueprintId": bp, "blueprintName": types.get(bp, {}).get("name"),
                "productId": product_id, "productName": types.get(product_id, {}).get("name"),
                "categoryId": types.get(product_id, {}).get("categoryId"),
                "groupId": types.get(product_id, {}).get("groupId"),
                "admitted": reason is None, "exclusion": reason,
                "activity": activity,
            })
        recipes.sort(key=lambda r: r["blueprintId"])
        admitted = [r for r in recipes if r["admitted"]]
        samples = [next(r for r in admitted if r["productName"] == name) for name in SAMPLES]
        relic = next(b for _, b in records if b.get("blueprintTypeID") == 30187)
        invention = relic["activities"]["invention"]
        samples.append(next(r for r in admitted if r["blueprintId"] == invention["products"][0]["typeID"]))
        wanted = {3380, 3388, 30187}
        wanted.update(m["typeID"] for m in invention["materials"])
        wanted.update(s["typeID"] for s in invention["skills"])
        for recipe in samples:
            wanted.update([recipe["blueprintId"], recipe["productId"]])
            wanted.update(m["typeID"] for m in recipe["activity"].get("materials", []))
            wanted.update(s["typeID"] for s in recipe["activity"].get("skills", []))
        fixture_types = []
        for type_id in sorted(wanted):
            fixture_types.append({
                **types[type_id], "definition": definitions.get(type_id, {}),
                "attributes": [{"id": a, "value": v} for a, v in db.execute(
                    "SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? ORDER BY AttributeId", (type_id,)
                )],
            })
        catalog_ids = {r["blueprintId"] for r in recipes} | wanted
        catalog_ids.update(r["productId"] for r in recipes)
        for recipe in recipes:
            catalog_ids.update(m["typeID"] for m in recipe["activity"].get("materials", []))
            catalog_ids.update(s["typeID"] for s in recipe["activity"].get("skills", []))
        catalog_types = [{**types[i], "published": definitions.get(i, {}).get("published") == 1}
                         for i in sorted(catalog_ids) if i in types]
        result = {
            "build": 3662168, "blueprintSha256": fingerprint.hexdigest(),
            "sourceFingerprints": dict(db.execute(
                "SELECT Name,Sha256 FROM SourceDataset WHERE Name IN ('client/types','client/blueprints','client/typedogma') ORDER BY Name"
            )),
            "counts": {"manufacturing": len(recipes), "admitted": len(admitted),
                       "exclusions": dict(sorted(Counter(r["exclusion"] for r in recipes if not r["admitted"]).items()))},
            "materialSources": "Missing sources remain unobtainable; this export grants no inputs or NPC supply.",
            "facilityRestrictions": "NPC stations cannot build supercapitals; ordinary combat/industrial capitals require low/null security.",
            "lowerInventionSkills": json.loads(db.execute("SELECT Data FROM SourceRecord WHERE Dataset='client/typelist' AND Key='799'").fetchone()[0]), "relic": relic, "catalogTypes": catalog_types, "samples": [r["blueprintId"] for r in samples], "fixtureTypes": fixture_types, "recipes": recipes,
        }
        output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(json.dumps(result["counts"]))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=Path("docs/production-catalog.json"))
    args = parser.parse_args()
    export(args.catalog, args.output)
