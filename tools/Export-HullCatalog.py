"""Reproduce the Tech I inventory and first-cohort test definitions from the pinned catalog."""

import argparse
import json
import pathlib
import sqlite3


def export(catalog_path, output_directory):
    with sqlite3.connect(catalog_path.resolve().as_uri() + "?mode=ro", uri=True) as catalog:
        def record(dataset, key):
            row = catalog.execute(
                "SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?", (dataset, str(key))
            ).fetchone()
            return json.loads(row[0]) if row else None

        hulls = []
        for type_id, name, group_id, group_name in catalog.execute(
            "SELECT t.Id,t.Name,g.Id,g.Name FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId "
            "WHERE g.CategoryId=6 ORDER BY t.Id"
        ):
            source = record("client/types", type_id)
            if source.get("published") != 1 or source.get("techLevel") != 1:
                continue
            dogma = record("client/typedogma", type_id)
            hulls.append({
                "id": type_id, "name": name, "groupId": group_id, "groupName": group_name,
                "acquisition": "market" if source.get("marketGroupID") else "recovery-or-developer-fixture",
                "effects": [e["effectID"] for e in dogma.get("dogmaEffects", [])],
            })
        if len(hulls) != 275:
            raise ValueError(f"Expected build-3662168 inventory of 275 hulls, found {len(hulls)}")

        # Four hulls, their test fits and medium weapons to verify size selectors.
        pending = {587, 594, 597, 603, 561, 484, 450, 1183, 215, 179, 246, 456, 490, 570}
        types = []
        records = {}
        attributes = set()
        while pending:
            type_id = min(pending)
            pending.remove(type_id)
            if ("client/types", type_id) in records:
                continue
            row = catalog.execute(
                "SELECT t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity "
                "FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?", (type_id,)
            ).fetchone()
            name, group_id, group_name, category_id, mass, radius, capacity = row
            values = list(catalog.execute(
                "SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? ORDER BY AttributeId", (type_id,)
            ))
            types.append({
                "id": type_id, "name": name, "groupId": group_id, "groupName": group_name,
                "categoryId": category_id, "mass": mass, "radius": radius, "capacity": capacity,
                "attributes": [{"id": key, "value": value} for key, value in values],
            })
            # requiredSkill1..6, including recursive prerequisites.
            pending.update(int(value) for key, value in values if key in (182, 183, 184, 1285, 1289, 1290) and value > 0)
            attributes.update(key for key, _ in values)
            for dataset in ("client/types", "client/typedogma"):
                records[dataset, type_id] = record(dataset, type_id)
            for effect in records["client/typedogma", type_id].get("dogmaEffects", []):
                effect_id = effect["effectID"]
                definition = record("client/dogmaeffects", effect_id)
                records["client/dogmaeffects", effect_id] = definition
                for modifier in definition.get("modifierInfo", []):
                    attributes.update(modifier[key] for key in ("modifiedAttributeID", "modifyingAttributeID") if key in modifier)
        pending = attributes
        while pending:
            attribute = pending.pop()
            if ("client/dogmaattributes", attribute) in records:
                continue
            definition = record("client/dogmaattributes", attribute)
            records["client/dogmaattributes", attribute] = definition
            pending.update(definition[key] for key in ("minAttributeID", "maxAttributeID") if definition.get(key))

        output_directory.mkdir(parents=True, exist_ok=True)
        outputs = {
            "TechOneHulls.json": {"build": 3662168, "hulls": hulls},
            "FrigateHullCatalog.json": {
                "build": 3662168, "types": sorted(types, key=lambda t: t["id"]),
                "records": [{"dataset": d, "key": k, "data": v} for (d, k), v in sorted(records.items())],
            },
        }
        for name, data in outputs.items():
            (output_directory / name).write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"Exported {len(hulls)} hulls and {len(types)} first-cohort fixture types.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    options = parser.parse_args()
    export(options.catalog, options.output)
