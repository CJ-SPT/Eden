"""Offline checks for the Standard market importer; no network or saved-world database."""
import bz2
import csv
from datetime import datetime, timezone
import gzip
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("live_market", ROOT / "tools" / "Import-LiveMarket.py")
IMPORTER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(IMPORTER)
NOW = datetime(2026, 11, 16, tzinfo=timezone.utc)
STATIONS = {60003760: (30000142, 10000002)}
ROW = dict(order_id="123", location_id="60003760", system_id="30000142", region_id="10000002", type_id="34",
           is_buy_order="false", issued="2026-11-15T00:00:00Z", duration="90", range="station", min_volume="1",
           volume_remain="42", volume_total="50", price="123.45")


class LiveMarketImportTests(unittest.TestCase):
    def test_preserves_both_sides_and_exact_quantities_without_aggregation(self):
        for bid in ("true", "false"):
            order, reason = IMPORTER.convert(dict(ROW, is_buy_order=bid), {34}, STATIONS, NOW)
            self.assertIsNone(reason)
            self.assertEqual((123, 12345, 42, 50, bid == "true"),
                             tuple(order[key] for key in ("sourceOrderId", "priceCents", "remaining", "entered", "bid")))

    def test_exclusions_are_explicit_and_never_relocate_orders(self):
        for field, value, reason in (("location_id", "9999999999999", "unsupportedLocation"),
                                     ("type_id", "99999", "unsupportedType"),
                                     ("system_id", "30000144", "locationMismatch"),
                                     ("price", "0.001", "unsupportedPrice"),
                                     ("volume_total", "2147483648", "unsupportedQuantityOrDuration"),
                                     ("issued", "2020-01-01T00:00:00Z", "expired")):
            with self.subTest(field=field):
                self.assertEqual((None, reason), IMPORTER.convert(dict(ROW, **{field: value}), {34}, STATIONS, NOW))

    def test_named_ranges_match_the_pinned_wire_values(self):
        for label, expected in (("station", -1), ("solarsystem", 0), ("5", 5), ("region", 32767)):
            order, _ = IMPORTER.convert(dict(ROW, range=label), {34}, STATIONS, NOW)
            self.assertEqual(expected, order["range"])

    def test_publishes_individual_price_levels_and_preserves_manifest_on_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "orders.csv.bz2"

            def write(rows):
                with bz2.open(source, "wt", encoding="utf-8", newline="") as stream:
                    writer = csv.DictWriter(stream, fieldnames=list(ROW))
                    writer.writeheader()
                    writer.writerows(rows)

            write([ROW, dict(ROW, order_id="124", price="150.00")])
            output = root / "market-orders"
            manifest = IMPORTER.publish(source, output, "test", {34}, STATIONS, "test", NOW)
            self.assertEqual(2, manifest["importedOrders"])
            with gzip.open(output / manifest["generation"] / "10000002.json.gz", "rt", encoding="utf-8") as stream:
                rows = json.load(stream)
            self.assertEqual([12345, 15000], [order["priceCents"] for order in rows])
            previous = (output / "manifest.json").read_bytes()
            for bad_rows in ([ROW, ROW], [dict(ROW, price="broken")], [dict(ROW, type_id="99999")]):
                write(bad_rows)
                with self.assertRaises(ValueError):
                    IMPORTER.publish(source, output, "test", {34}, STATIONS, "test", NOW)
                self.assertEqual(previous, (output / "manifest.json").read_bytes())


if __name__ == "__main__":
    unittest.main()
