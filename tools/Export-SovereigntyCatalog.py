"""Export inert build-3662168 sovereignty definitions for focused automated tests."""

import argparse
import json
import pathlib
import sqlite3


def export(path, output):
    with sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True) as catalog:
        source = catalog.execute("SELECT Source FROM SourceDataset WHERE Name='sde/mapSolarSystems'").fetchone()
        if source is None or "eve-online-static-data-3662168-jsonl.zip#" not in source[0]:
            raise ValueError("Select the pinned build-3662168 catalog")

        def record(dataset, key):
            row = catalog.execute(
                "SELECT Data FROM SourceRecord WHERE Dataset=? AND Key=?", (dataset, str(key))
            ).fetchone()
            return json.loads(row[0]) if row else None

        pending = {81080, 81143, 81144, 34, 483, 1230, 30380, 34832, 32458, 16275, 601, 643, 19720, 20183, 11176, 3363, 3732}
        pending.update(row[0] for row in catalog.execute("SELECT Id FROM ItemType WHERE GroupId=1313"))
        records, types = {}, {}
        system_id = 30003995
        system = record("sde/mapSolarSystems", system_id)
        constellation_id, region_id = system["constellationID"], system["regionID"]
        for dataset, key in [
            ("sde/mapSolarSystems", system_id), ("sde/mapConstellations", constellation_id),
            ("sde/mapRegions", region_id), ("client/systems", system_id),
        ]:
            value = record(dataset, key)
            if value is not None:
                records[dataset, key] = value
        star = catalog.execute(
            "SELECT Id,SolarSystemId,TypeId,GroupId,Radius,OrbitId,X,Y,Z FROM Celestial "
            "WHERE SolarSystemId=? AND GroupId=6", (system_id,)
        ).fetchone()
        celestials = list(catalog.execute(
            "SELECT c.Id,c.SolarSystemId,c.TypeId,c.GroupId,c.Radius,c.OrbitId,c.X,c.Y,c.Z FROM Celestial c "
            "JOIN SolarSystem s ON s.Id=c.SolarSystemId WHERE s.ConstellationId=? AND c.GroupId IN (6,7) ORDER BY c.Id",
            (constellation_id,)))
        for celestial in celestials:
            pending.add(celestial[2])
            for dataset in ("sde/mapStars", "sde/mapPlanets", "sde/planetResources"):
                value = record(dataset, celestial[0])
                if value is not None:
                    records[dataset, celestial[0]] = value
        for key, value in catalog.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='sde/sovereigntyUpgrades'"):
            pending.add(int(key))
            records["sde/sovereigntyUpgrades", int(key)] = json.loads(value)
        ore_names = ("Veldspar", "Mordunium", "Kylixium", "Griemeer", "Nocxite", "Hezorime", "Ueganite")
        for name in ore_names:
            row = catalog.execute("SELECT Id FROM ItemType WHERE Name=?", (name,)).fetchone()
            if row is None:
                raise ValueError("Missing upgrade ore " + name)
            pending.add(row[0])
        while pending:
            type_id = min(pending)
            pending.remove(type_id)
            if type_id in types:
                continue
            row = catalog.execute(
                "SELECT t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity "
                "FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?", (type_id,)
            ).fetchone()
            if row is None:
                raise ValueError(f"Missing catalog type {type_id}")
            values = list(catalog.execute(
                "SELECT AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=? ORDER BY AttributeId", (type_id,)
            ))
            types[type_id] = dict(zip(
                ["id", "name", "groupId", "groupName", "categoryId", "mass", "radius", "capacity"], [type_id, *row]
            ))
            types[type_id]["attributes"] = [{"id": key, "value": value} for key, value in values]
            pending.update(int(value) for key, value in values if key in (182, 183, 184, 1285, 1289, 1290) and value > 0)
            for dataset in ("client/types", "client/typedogma", "client/spacecomponentsbytype"):
                value = record(dataset, type_id)
                if value is not None:
                    records[dataset, type_id] = value
            if type_id == 30380:
                wreck = records.get(("client/types", type_id), {}).get("wreckTypeID")
                if wreck:
                    pending.add(int(wreck))
            for effect in records.get(("client/typedogma", type_id), {}).get("dogmaEffects", []):
                key = effect["effectID"]
                value = record("client/dogmaeffects", key)
                if value is not None:
                    records["client/dogmaeffects", key] = value
        constellation = record("sde/mapConstellations", constellation_id)
        region = record("sde/mapRegions", region_id)
        constellation_systems = []
        for sid, name in catalog.execute(
            "SELECT Id,Name FROM SolarSystem WHERE ConstellationId=? ORDER BY Id", (constellation_id,)
        ):
            value = record("sde/mapSolarSystems", sid)
            if value:
                records["sde/mapSolarSystems", sid] = value
                for gate in value.get("stargateIDs", []):
                    records["sde/mapStargates", gate] = record("sde/mapStargates", gate)
            constellation_systems.append({"id": sid, "name": name, "constellationId": constellation_id,
                "constellationName": constellation["name"]["en"], "regionId": region_id,
                "regionName": region["name"]["en"]})
        data = {
            "build": 3662168,
            "types": [types[key] for key in sorted(types)],
            "records": [{"dataset": dataset, "key": key, "data": value}
                        for (dataset, key), value in sorted(records.items())],
            "systems": constellation_systems,
            "celestials": [dict(zip(
                ["id", "solarSystemId", "typeId", "groupId", "radius", "orbitId", "x", "y", "z"], row
            )) for row in celestials],
        }
        output.write_text(json.dumps(data, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")
        print(f"Exported {len(types)} types and {len(records)} inert source records to {output}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    export(args.catalog, args.output)
