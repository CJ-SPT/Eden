"""Export the pinned beginner market, prerequisite skills and fitting evidence for portable tests."""

import argparse
import json
from pathlib import Path
import re
import sqlite3


def export(catalog_path, output_path):
    policy = Path(__file__).resolve().parents[1] / "src/Eden.Gameplay/Market/StarterMarketRules.cs"
    types = {int(value) for value in re.findall(r"^    \w+ = (\d+),$", policy.read_text(encoding="utf-8"), re.MULTILINE)}
    if not types:
        raise ValueError("The starter market policy is empty")
    with sqlite3.connect(catalog_path.resolve().as_uri() + "?mode=ro", uri=True) as db:
        def source(dataset, key):
            row = db.execute("SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?", (dataset, str(key))).fetchone()
            return json.loads(row[0]) if row else {}

        records = {}
        pending = set(types)
        for race in (1, 2, 4, 8):
            data = source("client/races", race)
            records["client/races", race] = data
            pending.update(int(skill) for skill in data["skills"])
        definitions = []
        attributes = set()
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
            definitions.append({"id": type_id, "name": name, "groupId": group, "groupName": group_name,
                                "categoryId": category, "mass": mass, "radius": radius, "capacity": capacity,
                                "attributes": [{"id": key, "value": value} for key, value in values]})
            attributes.update(key for key, _ in values)
            for dataset in ("client/types", "client/typedogma", "client/requiredskillsfortypes"):
                data = source(dataset, type_id)
                if data:
                    records[dataset, type_id] = data
            pending.update(int(skill) for skill in records.get(("client/requiredskillsfortypes", type_id), {}))
            pending.update(int(value) for key, value in values if key in (182, 183, 184, 1285, 1289, 1290) and value > 0)
            for effect in records.get(("client/typedogma", type_id), {}).get("dogmaEffects", []):
                effect_id = effect["effectID"]
                definition = source("client/dogmaeffects", effect_id)
                records["client/dogmaeffects", effect_id] = definition
                for modifier in definition.get("modifierInfo", []):
                    attributes.update(modifier[key] for key in ("modifiedAttributeID", "modifyingAttributeID") if modifier.get(key))
                    if modifier.get("skillTypeID"):
                        pending.add(modifier["skillTypeID"])
        while attributes:
            attribute = attributes.pop()
            if ("client/dogmaattributes", attribute) in records:
                continue
            definition = source("client/dogmaattributes", attribute)
            records["client/dogmaattributes", attribute] = definition
            attributes.update(definition[key] for key in ("minAttributeID", "maxAttributeID") if definition.get(key))
        result = {"build": 3662168, "types": sorted(definitions, key=lambda row: row["id"]),
                  "records": [{"dataset": dataset, "key": key, "data": value}
                              for (dataset, key), value in sorted(records.items())]}
    output_path.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Exported {len(result['types'])} types and {len(result['records'])} records")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    export(args.catalog, args.output)
