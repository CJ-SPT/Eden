"""Import a complete EVE Ref public order snapshot for Eden's Standard market preset.

Reads the installed catalog only. Publishes immutable compressed regional files and
atomically replaces the manifest after every input row has been checked. Never edits
the saved world. Existing servers adopt a new manifest on their next restart.
"""
import argparse
import bz2
from collections import Counter
import csv
from datetime import datetime, timezone
from decimal import Decimal, InvalidOperation
import gzip
import hashlib
import json
from pathlib import Path
import sqlite3
import tempfile
import urllib.request
import uuid


SOURCE = "https://data.everef.net/market-orders/market-orders-latest.v3.csv.bz2"
MAXIMUM_PRICE_CENTS = 922_337_203_685_400
FILETIME_EPOCH = datetime(1601, 1, 1, tzinfo=timezone.utc)


def timestamp(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone(timezone.utc)


def filetime(value):
    delta = value - FILETIME_EPOCH
    return (delta.days * 86400 + delta.seconds) * 10_000_000 + delta.microseconds * 10


def read_catalog(path):
    with sqlite3.connect(path.resolve().as_uri() + "?mode=ro", uri=True) as catalog:
        digest = hashlib.sha256()
        for name, source, sha256, rows in catalog.execute(
                "SELECT Name,Source,Sha256,Rows FROM SourceDataset ORDER BY Name"):
            digest.update(("\0".join(map(str, (name, source, sha256, rows))) + "\n").encode())
        types = {int(key) for key, raw in catalog.execute(
            "SELECT Key,Data FROM SourceRecord WHERE Dataset='client/types'")
            if (data := json.loads(raw)).get("published") == 1 and data.get("marketGroupID", 0) > 0}
        # Require both a client type record and a usable typed item definition.
        types.intersection_update(row[0] for row in catalog.execute("SELECT Id FROM ItemType"))
        operations = {int(key): json.loads(raw).get("services", []) for key, raw in catalog.execute(
            "SELECT Key,Data FROM SourceRecord WHERE Dataset='client/stationoperations'")}
        stations = {station: (system, region) for station, system, region, operation in catalog.execute(
            "SELECT s.Id,s.SolarSystemId,c.RegionId,s.OperationId FROM Station s "
            "JOIN SolarSystem y ON y.Id=s.SolarSystemId JOIN Constellation c ON c.Id=y.ConstellationId")
            if 7 in operations.get(operation, [])}
        return digest.hexdigest().upper(), types, stations


def convert(row, types, stations, captured_at):
    station_id = int(row["location_id"])
    if station_id not in stations:
        return None, "unsupportedLocation"
    type_id = int(row["type_id"])
    if type_id not in types:
        return None, "unsupportedType"
    system_id, region_id = stations[station_id]
    if int(row["system_id"]) != system_id or int(row["region_id"]) != region_id:
        return None, "locationMismatch"
    issued = timestamp(row["issued"])
    duration = int(row["duration"])
    remaining, entered, minimum = map(int, (row["volume_remain"], row["volume_total"], row["min_volume"]))
    if duration < 1 or duration > 365 or remaining < 1 or remaining > entered or entered > 2_147_483_647:
        return None, "unsupportedQuantityOrDuration"
    if (captured_at - issued).total_seconds() >= duration * 86400:
        return None, "expired"
    try:
        price = Decimal(row["price"]) * 100
    except InvalidOperation as error:
        raise ValueError("Invalid live order price; previous snapshot retained") from error
    if not price.is_finite() or price != price.to_integral_value() or not 0 < price <= MAXIMUM_PRICE_CENTS:
        return None, "unsupportedPrice"
    ranges = {"station": -1, "solarsystem": 0, "region": 32767}
    range_value = ranges.get(row["range"])
    if range_value is None:
        range_value = int(row["range"])
    if not -1 <= range_value <= 32767 or not 1 <= minimum <= entered:
        return None, "unsupportedRangeOrMinimum"
    bid_text = row["is_buy_order"].lower()
    if bid_text not in ("true", "false", "0", "1") or int(row["order_id"]) <= 0:
        raise ValueError("Invalid order identity or side")
    return dict(sourceOrderId=int(row["order_id"]), stationId=station_id, systemId=system_id,
                regionId=region_id, typeId=type_id, bid=bid_text in ("true", "1"), priceCents=int(price),
                remaining=remaining, entered=entered, issued=filetime(issued), durationDays=duration,
                range=range_value, minimumVolume=minimum), None


def publish(input_path, output, fingerprint, types, stations, source, captured_at):
    output.mkdir(parents=True, exist_ok=True)
    generation = uuid.uuid4().hex
    target = output / generation
    target.mkdir()
    excluded = Counter()
    counts = Counter()
    handles = {}
    seen = set()
    try:
        with bz2.open(input_path, "rt", encoding="utf-8", newline="") as stream:
            for row in csv.DictReader(stream):
                order, reason = convert(row, types, stations, captured_at)
                if reason:
                    excluded[reason] += 1
                    continue
                if order["sourceOrderId"] in seen:
                    raise ValueError("Duplicate live order identity; previous snapshot retained")
                seen.add(order["sourceOrderId"])
                region = order["regionId"]
                if region not in handles:
                    handles[region] = gzip.open(target / f"{region}.json.gz", "wt", encoding="utf-8", newline="\n")
                    handles[region].write("[")
                if counts[region]:
                    handles[region].write(",")
                json.dump(order, handles[region], separators=(",", ":"), allow_nan=False)
                counts[region] += 1
        if not counts:
            raise ValueError("Snapshot contains no compatible orders; previous snapshot retained")
    finally:
        for handle in handles.values():
            handle.write("]\n")
            handle.close()
    regions = [dict(regionId=region, orders=count,
                    sha256=hashlib.sha256((target / f"{region}.json.gz").read_bytes()).hexdigest().upper())
               for region, count in sorted(counts.items())]
    manifest = dict(formatVersion=1, generation=generation, source=source, capturedAt=captured_at.isoformat(),
                    catalogFingerprint=fingerprint, importedOrders=sum(counts.values()), excludedOrders=dict(excluded), regions=regions)
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", newline="\n", dir=output, suffix=".tmp", delete=False) as stream:
        json.dump(manifest, stream, indent=2, allow_nan=False)
        stream.write("\n")
    Path(stream.name).replace(output / "manifest.json")
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True, help="market-orders directory beside the saved world")
    parser.add_argument("--input", type=Path, help="existing EVE Ref .csv.bz2; otherwise download the latest public snapshot")
    parser.add_argument("--captured-at", help="source capture time in ISO 8601 (required with --input)")
    args = parser.parse_args()
    fingerprint, types, stations = read_catalog(args.catalog)
    with tempfile.TemporaryDirectory(prefix="eden-market-") as temporary:
        input_path = args.input
        if input_path:
            if not args.captured_at:
                parser.error("--input requires --captured-at")
            captured_at = timestamp(args.captured_at)
            source = str(input_path.resolve())
        else:
            request = urllib.request.Request(SOURCE, headers={"User-Agent": "Eden-LiveMarketImporter/1.0"})
            input_path = Path(temporary) / "orders.csv.bz2"
            with urllib.request.urlopen(request, timeout=60) as response, input_path.open("wb") as stream:
                # Preserve the provider's snapshot timestamp, not the local import time.
                from email.utils import parsedate_to_datetime
                captured_at = parsedate_to_datetime(response.headers["Last-Modified"]).astimezone(timezone.utc)
                while block := response.read(1024 * 1024):
                    stream.write(block)
            source = SOURCE
        result = publish(input_path, args.output, fingerprint, types, stations, source, captured_at)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
