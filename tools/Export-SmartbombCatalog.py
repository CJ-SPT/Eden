"""Export pinned smartbomb fitting definitions and their dogma dependencies."""

import argparse
import json
from pathlib import Path
import sqlite3


def export(path, output):
    if path.resolve() == output.resolve():
        raise ValueError("The fixture must not overwrite the catalog")
    with sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True) as catalog:
        def read(dataset, key):
            row = catalog.execute("SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?", (dataset, str(key))).fetchone()
            return json.loads(row[0]) if row else None

        build = read("sde/_sde", '"sde"')["buildNumber"]
        if build != 3662168:
            raise ValueError("Expected the pinned build-3662168 catalog")
        cohort = []
        for (type_id,) in catalog.execute("SELECT Id FROM ItemType WHERE GroupId=72 ORDER BY Id"):
            source = read("client/types", type_id)
            if source.get("published") == 1:
                cohort.append(type_id)
        if len(cohort) != 137:
            raise ValueError("Expected 125 static and 12 abyssal definitions")

        records, types, effects, attributes = {}, [], set(), set()
        pending = set(cohort) | {210, 3421}
        while pending:
            type_id = min(pending)
            pending.remove(type_id)
            if ("client/types", type_id) in records:
                continue
            row = catalog.execute(
                "SELECT t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity "
                "FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?", (type_id,)
            ).fetchone()
            name, group, group_name, category, mass, radius, capacity = row
            values = list(catalog.execute(
                "SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? ORDER BY AttributeId", (type_id,)
            ))
            types.append(dict(id=type_id, name=name, groupId=group, groupName=group_name, categoryId=category,
                              mass=mass, radius=radius, capacity=capacity, attributes=[dict(id=a, value=v) for a, v in values]))
            attributes.update(a for a, _ in values)
            pending.update(int(v) for a, v in values if a in (182, 183, 184, 1285, 1289, 1290) and v)
            for dataset in ("client/types", "client/typedogma"):
                records[dataset, type_id] = read(dataset, type_id)
            effects.update(e["effectID"] for e in records["client/typedogma", type_id].get("dogmaEffects", []))

        for effect in sorted(effects):
            source = read("client/dogmaeffects", effect)
            records["client/dogmaeffects", effect] = source
            for modifier in source.get("modifierInfo", []):
                attributes.update(modifier[key] for key in ("modifiedAttributeID", "modifyingAttributeID") if key in modifier)
        while attributes:
            attribute = attributes.pop()
            if ("client/dogmaattributes", attribute) in records:
                continue
            source = read("client/dogmaattributes", attribute)
            records["client/dogmaattributes", attribute] = source
            attributes.update(source[key] for key in ("minAttributeID", "maxAttributeID") if source.get(key) is not None)

    result = dict(build=build, types=sorted(types, key=lambda row: row["id"]),
                  records=[dict(dataset=d, key=k, data=v) for (d, k), v in sorted(records.items())])
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", required=True, type=Path)
    parser.add_argument("--output", type=Path, default=Path("tests/Eden.Protocol.Tests/Fixtures/SmartbombCatalog.json"))
    args = parser.parse_args()
    export(args.catalog, args.output)
