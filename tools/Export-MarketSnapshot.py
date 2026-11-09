"""Capture real Jita buy/sell quotes for offline market seeding. Failed exports preserve the previous snapshot."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from decimal import Decimal, ROUND_HALF_UP
import json
from pathlib import Path
import sqlite3
import tempfile
import urllib.request


STATION = 60003760
SOURCE = "https://market.fuzzwork.co.uk/aggregates/"


def read(url):
    request = urllib.request.Request(url, headers={"User-Agent": "Eden-MarketSnapshot/1.0"})
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.load(response)


def cents(side):
    if Decimal(str(side.get("volume", 0))) <= 0:
        return None
    price = Decimal(str(side.get("percentile", 0)))
    if not price.is_finite() or price <= 0:
        return None
    value = int((price * 100).quantize(Decimal(1), rounding=ROUND_HALF_UP))
    # Match MarketDefinitions.MaximumOrderPrice, expressed in ISK cents.
    return value if 0 < value <= 922_337_203_685_400 else None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    with sqlite3.connect(args.catalog.resolve().as_uri() + "?mode=ro", uri=True) as catalog:
        types = sorted(int(key) for key, raw in catalog.execute(
            "SELECT Key,Data FROM SourceRecord WHERE Dataset='client/types'")
            if (data := json.loads(raw)).get("published") == 1 and data.get("marketGroupID", 0) > 0)
    started = datetime.now(timezone.utc).isoformat()
    batches = [types[offset:offset + 250] for offset in range(0, len(types), 250)]

    def batch(ids):
        return read(SOURCE + "?station=" + str(STATION) + "&types=" + ",".join(map(str, ids)))

    quotes = {}

    def capture(result, location):
        for type_id, sides in result.items():
            buy, sell = cents(sides.get("buy", {})), cents(sides.get("sell", {}))
            # Do not manufacture a spread or a missing side. Crossed aggregate
            # books are omitted rather than enabling guaranteed instant profit.
            if buy is not None and sell is not None and buy >= sell:
                continue
            if buy is not None or sell is not None:
                quotes[int(type_id)] = {"typeId": int(type_id), "buyCents": buy, "sellCents": sell, "locationId": location}

    with ThreadPoolExecutor(max_workers=4) as pool:
        for result in pool.map(batch, batches):
            capture(result, STATION)
        # NPC-only blueprints/books may have no Jita listings. A global quote
        # preserves their real observed price without inventing a catalog price.
        missing = [type_id for type_id in types if type_id not in quotes]
        for offset in range(0, len(missing), 250):
            ids = ",".join(map(str, missing[offset:offset + 250]))
            capture(read(SOURCE + "?region=0&types=" + ids), 0)
    if not quotes:
        raise ValueError("The market source returned no usable quotes")
    snapshot = {"source": SOURCE, "stationId": STATION, "startedAt": started,
                "retrievedAt": datetime.now(timezone.utc).isoformat(), "statistic": "volume-weighted best 5 percent",
                "fallbackLocationId": 0, "quotes": sorted(quotes.values(), key=lambda quote: quote["typeId"])}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", newline="\n", delete=False,
                                     dir=args.output.parent, suffix=".tmp") as output:
        json.dump(snapshot, output, indent=2, allow_nan=False)
        output.write("\n")
    Path(output.name).replace(args.output)
    print(f"Captured {len(quotes)} quotes from {len(types)} published market types")


if __name__ == "__main__":
    main()
