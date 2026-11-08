"""Capture public CCP ESI LP offers for offline use; never contact ESI at server runtime."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import json
from pathlib import Path
import tempfile
import urllib.request


BASE = "https://esi.evetech.net/latest"


def read(route):
    request = urllib.request.Request(BASE + route, headers={"User-Agent": "Eden-LoyaltyOfferExport/1.0"})
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.load(response)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    corporations = read("/corporations/npccorps/")

    def corporation(corp):
        return corp, read(f"/loyalty/stores/{corp}/offers/")

    offers = {}
    stores = {}
    with ThreadPoolExecutor(max_workers=4) as workers:
        for corp, rows in workers.map(corporation, sorted(corporations)):
            if not rows:
                continue
            stores[corp] = sorted(row["offer_id"] for row in rows)
            for row in rows:
                offer = {
                    "id": row["offer_id"], "typeId": row["type_id"], "quantity": row["quantity"],
                    "lpCost": row["lp_cost"], "iskCents": row["isk_cost"] * 100,
                    "analysisKredits": row.get("ak_cost", 0),
                    "requiredItems": [{"typeId": item["type_id"], "quantity": item["quantity"]}
                                      for item in sorted(row["required_items"], key=lambda item: item["type_id"])],
                }
                if offer["id"] in offers and offers[offer["id"]] != offer:
                    raise ValueError(f"Inconsistent offer {offer['id']}")
                offers[offer["id"]] = offer
    payload = {"source": BASE + "/loyalty/stores/{corporation_id}/offers/",
               "retrievedAt": datetime.now(timezone.utc).isoformat(),
               "offers": [offers[key] for key in sorted(offers)], "stores": stores}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=args.output.parent,
                                     prefix="loyalty-offers-", suffix=".tmp", delete=False) as temporary:
        temporary.write(json.dumps(payload, indent=2) + "\n")
        staged = Path(temporary.name)
    staged.replace(args.output)
    print(f"Captured {len(offers)} unique offers across {len(stores)} corporations.")


if __name__ == "__main__":
    main()
