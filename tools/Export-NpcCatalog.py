"""Export build-3662168 ordinary NPC, weapon, wreck and loot test definitions."""

import argparse
import json
import pathlib
import re
import sqlite3


def export(catalog_path, output):
    root = pathlib.Path(__file__).resolve().parent.parent
    groups = {
        int(value) for value in re.findall(
            r"new\((\d+), NpcPirateFaction",
            (root / "src/Eden.Gameplay/Combat/NpcPirateGroups.cs").read_text(encoding="utf-8"),
        )
    }
    loot = {
        int(value) for value in re.findall(
            r"^    \w+ = (\d+),$",
            (root / "src/Eden.Gameplay/Combat/NpcLootRules.cs").read_text(encoding="utf-8"), re.MULTILINE,
        )
    }
    if len(groups) != 30 or len(loot) != 24:
        raise ValueError("NPC group/loot declarations changed; review the export selectors.")
    guard_source = (root / "src/Eden.Gameplay/Combat/NpcGuardRules.cs").read_text(encoding="utf-8")
    guard_enum = guard_source.split("public enum NpcGuardType", 1)[1].split("}", 1)[0]
    guards = {int(value) for value in re.findall(r"^    \w+ = (\d+),$", guard_enum, re.MULTILINE)}
    if len(guards) != 12:
        raise ValueError("Guard roster changed; review the export selector.")
    with sqlite3.connect(catalog_path.resolve().as_uri() + "?mode=ro", uri=True) as catalog:
        def record(dataset, key):
            row = catalog.execute(
                "SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?", (dataset, str(key))
            ).fetchone()
            return json.loads(row[0]) if row else None

        pending = set(loot) | guards
        pending.update(row[0] for row in catalog.execute(
            "SELECT Id FROM ItemType WHERE GroupId IN (" + ",".join("?" for _ in groups) + ")", sorted(groups)
        ))
        types = {}
        records = {}
        while pending:
            type_id = min(pending)
            pending.remove(type_id)
            if type_id in types:
                continue
            row = catalog.execute(
                "SELECT t.Id,t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity "
                "FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?", (type_id,)
            ).fetchone()
            if not row:
                raise ValueError(f"Missing referenced NPC type {type_id}")
            attributes = [{"id": key, "value": value} for key, value in catalog.execute(
                "SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? ORDER BY AttributeId", (type_id,)
            )]
            types[type_id] = dict(zip(
                ("id", "name", "groupId", "groupName", "categoryId", "mass", "radius", "capacity"), row
            ), attributes=attributes)
            definition = record("client/types", type_id)
            if definition is not None:
                records["client/types", type_id] = definition
                if definition.get("wreckTypeID"):
                    pending.add(definition["wreckTypeID"])
            missile = next((a["value"] for a in attributes if a["id"] == 507), 0)
            if missile:
                pending.add(int(missile))
        for faction in (500001, 500002, 500003, 500004, 500010, 500011, 500012, 500019, 500020, 500025):
            definition = record("client/factions", faction)
            records["client/factions", faction] = definition
            corporation = definition["corporationID"]
            records["sde/npcCorporations", corporation] = record("sde/npcCorporations", corporation)
            records["client/npccorporations", corporation] = record("client/npccorporations", corporation)
        # ReadAttributes uses this pinned default when an NPC does not carry a
        # signature override. Keep the exact catalog rule in automated fixtures.
        records["client/dogmaattributes", 552] = record("client/dogmaattributes", 552)
        # Preserve the pinned NPC repair defaults and size-specific chance fields.
        repair_attributes = (630, 631, 636, 637, 638, 639, *range(1006, 1012), *range(1015, 1021))
        for attribute in repair_attributes:
            records["client/dogmaattributes", attribute] = record("client/dogmaattributes", attribute)
        data = {
            "build": 3662168,
            "types": [types[key] for key in sorted(types)],
            "records": [{"dataset": dataset, "key": key, "data": value}
                        for (dataset, key), value in sorted(records.items())],
        }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Exported {len(types)} NPC/dependency/loot types.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    args = parser.parse_args()
    export(args.catalog, args.output)
