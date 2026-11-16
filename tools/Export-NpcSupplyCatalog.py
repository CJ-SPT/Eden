"""Export the complete pinned NPC sell assortment for resident market coverage."""

import argparse
import json
from pathlib import Path
import sqlite3


def export(path):
    with sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True) as db:
        records = {(dataset, key): json.loads(data) for dataset, key, data in db.execute(
            "SELECT Dataset,Key,Data FROM SourceRecord "
            "WHERE Dataset IN ('sde/_sde','client/npccorporations','client/types','client/blueprints') ORDER BY Dataset,Key"
        )}
        build = next(data["buildNumber"] for (dataset, _), data in records.items() if dataset == "sde/_sde")
        if build != 3662168:
            raise ValueError("Use the pinned build-3662168 catalog.")
        corporations = [(key, data) for (dataset, key), data in records.items() if dataset == "client/npccorporations"]
        sold = {int(key) for _, data in corporations for key, value in data.get("corporationTrades", {}).items() if value > 0}
        blueprints = {int(json.loads(key)): data for (dataset, key), data in records.items() if dataset == "client/blueprints"}
        products = {product["typeID"] for type_id in sold for product in
                    blueprints.get(type_id, {}).get("activities", {}).get("manufacturing", {}).get("products", [])}
        ids = sold | products
        types, sources = [], []
        for type_id in sorted(ids):
            row = db.execute(
                "SELECT t.Id,t.Name,t.GroupId,g.Name,g.CategoryId,t.Mass,t.Radius,t.Capacity "
                "FROM ItemType t JOIN ItemGroup g ON g.Id=t.GroupId WHERE t.Id=?", (type_id,)
            ).fetchone()
            if row is None:
                raise ValueError(f"Missing NPC supply type {type_id}")
            entry = dict(zip(("id", "name", "groupId", "groupName", "categoryId", "mass", "radius", "capacity"), row))
            entry["attributes"] = [{"id": 422, "value": value} for (value,) in db.execute(
                "SELECT Value FROM TypeAttribute WHERE ItemTypeId=? AND AttributeId=422", (type_id,)
            )]
            types.append(entry)
            definition = records.get(("client/types", str(type_id)))
            if definition:
                fields = ("typeID", "published", "marketGroupID", "basePrice", "volume", "metaGroupID", "techLevel")
                sources.append({"dataset": "client/types", "key": type_id,
                                "data": {key: value for key, value in definition.items() if key in fields}})
            if type_id in sold and type_id in blueprints:
                products = blueprints[type_id].get("activities", {}).get("manufacturing", {}).get("products", [])
                sources.append({"dataset": "client/blueprints", "key": type_id,
                                "data": {"activities": {"manufacturing": {"products": products}}}})
        sources.extend({"dataset": "client/npccorporations", "key": int(key),
                        "data": {"corporationTrades": data.get("corporationTrades", {})}} for key, data in corporations)
        snapshots = json.loads(Path("src/Eden.World/Market/market-prices.json").read_text(encoding="utf-8"))
        quotes = {entry["typeId"]: entry for entry in snapshots["quotes"]}
        by_id = {entry["id"]: entry for entry in types}
        tech = {entry["id"]: next((a["value"] for a in entry["attributes"] if a["id"] == 422), 0) for entry in types}
        expected, excluded = [], []
        for type_id in sorted(sold):
            definition = records.get(("client/types", str(type_id)), {})
            products = blueprints.get(type_id, {}).get("activities", {}).get("manufacturing", {}).get("products", [])
            reason = None
            if definition.get("published") != 1 or not definition.get("marketGroupID"):
                reason = "unpublished or absent from market"
            elif by_id[type_id]["categoryId"] == 9 and (
                    any(tech[p["typeID"]] > 1 for p in products) if products else tech[type_id] > 1):
                reason = "advanced blueprint or advanced manufacturing output"
            elif not (quotes.get(type_id, {}).get("sellCents") or definition.get("basePrice", 0) > 0):
                reason = "no positive snapshot sell quote or catalog base price"
            if reason:
                excluded.append({"typeId": type_id, "reason": reason})
            else:
                expected.append(type_id)
        return {"build": build, "types": types, "records": sources, "npcSellTypes": sorted(sold),
                "expectedSellTypes": expected, "excludedSellTypes": excluded}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=Path("tests/Eden.Protocol.Tests/Fixtures/NpcSupplyCatalog.json"))
    args = parser.parse_args()
    data = export(args.catalog)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Exported {len(data['npcSellTypes'])} NPC sell types and {len(data['types'])} total type definitions.")


if __name__ == "__main__":
    main()
