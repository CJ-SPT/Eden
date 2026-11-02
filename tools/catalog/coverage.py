"""Reconcile the entire source key set against the reviewed build inventory."""
import hashlib
import json
from pathlib import Path

INVENTORY = Path(__file__).with_name("build-3662168.json")


def inventory(db):
    result = {}
    for name, source, sha, count in db.execute("SELECT Name,Source,Sha256,Rows FROM SourceDataset ORDER BY Name"):
        keys = hashlib.sha256()
        for key, in db.execute("SELECT Key FROM SourceRecord WHERE Dataset=? ORDER BY Key", (name,)):
            keys.update(key.encode())
            keys.update(b"\n")
        result[name] = {"source": source, "sha256": sha, "records": count, "keysSha256": keys.hexdigest()}
    return result


def verify_inventory(db):
    actual = inventory(db)
    expected = json.loads(INVENTORY.read_text(encoding="utf-8"))
    mismatches = sorted(n for n in actual.keys() | expected.keys() if actual.get(n) != expected.get(n))
    if mismatches:
        raise ValueError("Source identity, count, or key mismatch: " + ", ".join(mismatches))
