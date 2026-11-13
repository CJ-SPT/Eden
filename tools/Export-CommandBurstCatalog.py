"""Export the published command bursts, their charges, and a real fitting hull from the pinned catalog."""

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

        groups = (1769, 1770, 1771, 1772, 1773, 1774, 4905)
        marks = ",".join("?" for _ in groups)
        cohort = [(i, g) for i, g in catalog.execute(f"SELECT Id,GroupId FROM ItemType WHERE GroupId IN ({marks})", groups)
                  if read("client/types", i).get("published") == 1]
        collections = set()
        for item, group in cohort:
            if group != 1770:
                collections.update(int(v["value"]) for v in read("client/typedogma", item)["dogmaAttributes"]
                                   if v["attributeID"] in (2468, 2470, 2472, 2536) and v["value"] > 0)
        records = {("client/dbuffcollections", key): read("client/dbuffcollections", json.dumps(str(key))) for key in collections}
        types, effects, attributes = [], set(), set()
        pending = {i for i, _ in cohort} | {16227, 3354, 11574, 24764}
        while pending:
            item = min(pending)
            pending.remove(item)
            if ("client/types", item) in records:
                continue
            name, group, group_name, category, mass, radius, capacity = catalog.execute(
                "SELECT t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity "
                "FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?", (item,)
            ).fetchone()
            values = list(catalog.execute(
                "SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? ORDER BY AttributeId", (item,)
            ))
            types.append(dict(id=item, name=name, groupId=group, groupName=group_name, categoryId=category,
                              mass=mass, radius=radius, capacity=capacity, attributes=[dict(id=a, value=v) for a, v in values]))
            attributes.update(a for a, _ in values)
            pending.update(int(v) for a, v in values if a in (182, 183, 184, 1285, 1289, 1290) and v)
            for dataset in ("client/types", "client/typedogma"):
                records[dataset, item] = read(dataset, item)
            effects.update(e["effectID"] for e in records["client/typedogma", item].get("dogmaEffects", []))

        for effect in sorted(effects):
            row = read("client/dogmaeffects", effect)
            records["client/dogmaeffects", effect] = row
            for modifier in row.get("modifierInfo", []):
                attributes.update(modifier[key] for key in ("modifiedAttributeID", "modifyingAttributeID") if key in modifier)
        for collection in (records["client/dbuffcollections", key] for key in collections):
            for field in ("itemModifiers", "locationModifiers", "locationGroupModifiers", "locationRequiredSkillModifiers"):
                attributes.update(m["dogmaAttributeID"] for m in collection.get(field, []))
        while attributes:
            attribute = attributes.pop()
            if ("client/dogmaattributes", attribute) in records:
                continue
            source = read("client/dogmaattributes", attribute)
            records["client/dogmaattributes", attribute] = source
            attributes.update(source[key] for key in ("minAttributeID", "maxAttributeID") if source.get(key) is not None)

    result = dict(build=build, types=sorted(types, key=lambda row: row["id"]),
                  records=[dict(dataset=d, key=k, data=v) for (d, k), v in sorted(records.items())])
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", required=True, type=Path)
    parser.add_argument("--output", type=Path, default=Path("tests/Eden.Protocol.Tests/Fixtures/CommandBurstCatalog.json"))
    args = parser.parse_args()
    export(args.catalog, args.output)
