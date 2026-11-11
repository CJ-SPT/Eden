"""Export immutable build-3662168 planetary definitions for automated tests."""

import argparse
import json
from pathlib import Path
import sqlite3


def export(catalog):
    with sqlite3.connect(f"file:{catalog.resolve().as_posix()}?mode=ro", uri=True) as db:
        build = json.loads(db.execute(
            "SELECT Data FROM SourceRecord WHERE Dataset='sde/_sde'"
        ).fetchone()[0])["buildNumber"]
        if build != 3662168:
            raise ValueError("Use the pinned build-3662168 catalog.")
        recipes = [(int(key), json.loads(data)) for key, data in db.execute(
            "SELECT Key,Data FROM SourceRecord WHERE Dataset='client/schematics' ORDER BY CAST(Key AS INTEGER)"
        )]
        ids = {2233, 2263, 2280, 2403, 2406, 2495, 2505, 13279, 33467}
        for _, recipe in recipes:
            ids.update(recipe["pins"])
            ids.update(int(key) for key in recipe["types"])
        for key, data in db.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='client/types'"):
            definition = json.loads(data)
            if definition.get("groupID") in (1027, 1028, 1029, 1030, 1063) and definition.get("published") == 1:
                ids.add(int(key))
        # Include transitive skill prerequisites so eligibility uses real dogma.
        changed = True
        while changed:
            before = len(ids)
            for type_id in list(ids):
                for attribute, value in db.execute("SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=?", (type_id,)):
                    if attribute in (182, 183, 184, 1285, 1289, 1290) and value > 0:
                        ids.add(int(value))
            changed = len(ids) != before
        types, records = [], []
        for type_id in sorted(ids):
            row = db.execute(
                "SELECT t.Id,t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity "
                "FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?", (type_id,)
            ).fetchone()
            if row is None:
                raise ValueError(f"Missing planetary type {type_id}")
            entry = dict(zip(("id", "name", "groupId", "groupName", "categoryId", "mass", "radius", "capacity"), row))
            entry["attributes"] = [{"id": key, "value": value} for key, value in db.execute(
                "SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? ORDER BY AttributeId", (type_id,)
            )]
            types.append(entry)
            for dataset in ("client/types", "client/typedogma"):
                result = db.execute("SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?", (dataset, str(type_id))).fetchone()
                if result:
                    records.append({"dataset": dataset, "key": type_id, "data": json.loads(result[0])})
        records.extend({"dataset": "client/schematics", "key": key, "data": data} for key, data in recipes)
        for attribute in (1683, 1687, 1689):
            data = db.execute("SELECT Data FROM SourceRecord WHERE Dataset='client/dogmaattributes' AND Key=?", (str(attribute),)).fetchone()
            records.append({"dataset": "client/dogmaattributes", "key": attribute, "data": json.loads(data[0])})
        return {"build": build, "types": types, "records": records}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=Path("tests/Eden.Protocol.Tests/Fixtures/PlanetaryCatalog.json"))
    args = parser.parse_args()
    data = export(args.catalog)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Exported {len(data['types'])} types and {len(data['records'])} records.")


if __name__ == "__main__":
    main()
