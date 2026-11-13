"""Build a local catalog from verified build 3662168 files (64-bit Windows Python 3)."""
import argparse
import csv
from contextlib import closing
import struct
import hashlib
import json
import os
from pathlib import Path
import pickle
import sqlite3
import subprocess
import sys
import tempfile
import urllib.request
import zipfile

from coverage import verify_inventory
from reconcile import compare

BUILD = 3662168
ARCHIVE = f"eve-online-static-data-{BUILD}-jsonl.zip"
URL = "https://developers.eveonline.com/static-data/tranquility/" + ARCHIVE
HASHES = {
    ARCHIVE: "2114f82f41c390f3df43d6f94277ac6d658c03f13c7e255dcbe3d86e5a523751",
    "code.ccp": "37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368",
    "resfileindex.txt": "cba0852b46e3a1501c6c0653546c4520dbd6aba02a51e1da3409f5efeb253f03",
    "planetResources.pickle": "b8826871df0c8fac0c561a1279466bb818b57c46bc709edd9b97cd034eea4000",
    "mapObjects.db": "2e04b4162cb4e80097a6a304ddd471fa1e8ed1e78b4e62f9310810877277467d",
}
HERE = Path(__file__).resolve().parent


def digest(path, algorithm="sha256"):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, algorithm).hexdigest()


def verified(path, expected_name=None):
    if digest(path) != HASHES[expected_name or path.name]:
        raise ValueError(f"Unexpected build or damaged file: {path}")
    return path


def compact(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False)


def unicode_values(value):
    if isinstance(value, bytes):
        return value.decode("utf-8")
    if isinstance(value, dict):
        return {unicode_values(k): unicode_values(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [unicode_values(v) for v in value]
    return value


def write_source(db, name, source, sha, records):
    count = 0
    with db:
        for key, value in records:
            # A JSON key distinguishes e.g. the string "1" from the integer 1.
            db.execute("INSERT INTO SourceRecord(Dataset,Key,Data) VALUES (?,?,?)", (name, compact(key), compact(value)))
            count += 1
            if count % 2048 == 0:
                db.commit()
        stored = db.execute("SELECT count(*) FROM SourceRecord WHERE Dataset=?", (name,)).fetchone()[0]
        if count != stored:
            raise ValueError(f"Row reconciliation failed for {name}: {count} != {stored}")
        db.execute("INSERT INTO SourceDataset(Name,Source,Sha256,Rows) VALUES (?,?,?,?)", (name, source, sha, count))
    print(f"{name}: {count:,}", flush=True)


def sde_records(stream):
    for line in stream:
        value = json.loads(line)
        yield value.pop("_key"), value


def read_sde(db, archive):
    with zipfile.ZipFile(verified(archive, ARCHIVE)) as source:
        if json.loads(source.read("_sde.jsonl"))["buildNumber"] != BUILD:
            raise ValueError("Wrong SDE build")
        for entry in source.infolist():
            if not entry.filename.endswith(".jsonl"):
                raise ValueError(f"Unrecognized export file {entry.filename}")
            with source.open(entry) as stream:
                sha = hashlib.file_digest(stream, "sha256").hexdigest()
            with source.open(entry) as stream:
                write_source(db, "sde/" + entry.filename[:-6], URL + "#" + entry.filename, sha, sde_records(stream))


def pickle_records(path, name):
    with path.open("rb") as stream:
        data = pickle.load(stream, encoding="bytes")
    if name.startswith("localization_fsd_") and not name.endswith("_main"):
        language, messages = data
        for key, (text, metadata, tokens) in messages.items():
            yield key, {"language": unicode_values(language), "text": text,
                        "metadata": unicode_values(metadata), "tokens": unicode_values(tokens)}
    else:
        for key, value in data.items():
            yield unicode_values(key), unicode_values(value)



def read_planets(db, root, regions, work):
    paths = {str(path): str(work / (Path(uri).stem + ".region.jsonl")) for uri, path in regions.items()}
    manifest = work / "regions.json"
    manifest.write_text(json.dumps(paths))
    subprocess.run(["dotnet", "run", "--no-build", "--project", str(HERE.parents[1] / "src/Eden.Server"),
                    "--", "--decode-planet-files", str(manifest)], check=True)
    for uri, path in regions.items():
        with open(paths[str(path)], encoding="utf-8-sig") as stream:
            write_source(db, "planet/" + Path(uri).stem, uri, digest(path), map(json.loads, stream))
    resources = verified(root / "tq/res/planetResources.pickle")
    with resources.open("rb") as stream:
        data = pickle.load(stream, encoding="bytes")
    templates = data.pop(b"depletionTemplates")
    write_source(db, "client/depletionParameters", "res/planetResources.pickle", digest(resources),
                 ((key.decode(), value) for key, value in data.items()))
    # Keep every 32-bit word of the packed templates bit-exact, including signed zero.
    write_source(db, "client/depletionTemplates", "res/planetResources.pickle", digest(resources),
                 enumerate([word for word, in struct.iter_unpack("<I", template)] for template in templates))


def read_loose(db, root):
    binaries = root / "tq/bin64"
    positions = binaries / "packages/evemap/overriddenCoordinates.json"
    write_source(db, "client/overriddenCoordinates", "bin64/packages/evemap/overriddenCoordinates.json",
                 digest(positions), ((int(k), v) for k, v in json.loads(positions.read_bytes()).items()))
    for sample in sorted((binaries / "exoplanets/sampledata").glob("*.tab")):
        with sample.open(encoding="ascii", newline="") as stream:
            rows = csv.DictReader(stream, delimiter="\t")
            if rows.fieldnames != ["epoch", "flux"]:
                raise ValueError(f"Unexpected light curve columns: {sample}")
            write_source(db, "exoplanet/" + sample.stem, "bin64/exoplanets/sampledata/" + sample.name,
                         digest(sample), enumerate({"epoch": float(row["epoch"]), "flux": float(row["flux"])} for row in rows))


def read_client(db, root, work):
    index = verified(root / "tq/resfileindex.txt")
    verified(root / "tq/code.ccp")
    resources = {}
    regions = {}
    for line in index.read_text().splitlines():
        uri, relative, md5, *_ = line.split(",")
        if uri.startswith("res:/staticdata/") or uri.endswith((".pickle", ".region")):
            path = root / "ResFiles" / relative
            if digest(path, "md5") != md5:
                raise ValueError(f"Cache checksum mismatch for {uri}")
            if uri.endswith(".region"):
                regions[uri] = path
            else:
                resources[uri] = path
    read_loose(db, root)
    read_planets(db, root, regions, work)
    binaries = root / "tq/bin64"
    loaders = {p.stem.lower(): p.stem for p in binaries.glob("*Loader.pyd")}
    jobs = []
    for uri, path in resources.items():
        name = Path(uri).stem
        with path.open("rb") as stream:
            header = stream.read(16)
        if uri.endswith(".schema"):
            # Schema descriptors are consumed by their paired .static loaders.
            write_source(db, "schema/" + name, uri, digest(path), [("definition", path.read_text())])
        elif uri.endswith(".pickle"):
            write_source(db, "client/" + name, uri, digest(path), pickle_records(path, name))
        elif header == b"SQLite format 3\0":
            with sqlite3.connect(path.as_uri() + "?mode=ro", uri=True) as cache:
                write_source(db, "client/" + name, uri, digest(path),
                             ((k, json.loads(v)) for k, v in cache.execute("SELECT key,value FROM cache")))
                write_source(db, "index/" + name, uri, digest(path),
                             enumerate({"key": k, "value": v} for k, v in cache.execute("SELECT key,value FROM indexes")))
        else:
            schema = resources.get(uri.rsplit(".", 1)[0] + ".schema")
            loader = loaders[name + "loader"] if uri.endswith(".fsdbinary") else None
            jobs.append([uri, str(path), str(schema) if schema else None, loader, str(work / (name + ".jsonl"))])
    jobs_path = work / "jobs.json"
    jobs_path.write_text(json.dumps(jobs))
    subprocess.run([sys.executable, str(HERE / "decode_client.py"), str(root), str(jobs_path)], check=True)
    for uri, path, _, _, output in jobs:
        with open(output, encoding="utf-8") as stream:
            write_source(db, "client/" + Path(uri).stem, uri, digest(Path(path)), map(json.loads, stream))

    path = verified(binaries / "staticdata/mapObjects.db")
    with sqlite3.connect(path.as_uri() + "?mode=ro", uri=True) as source:
        source.row_factory = sqlite3.Row
        for table, key in [("celestials", "celestialID"), ("npcStations", "stationID")]:
            write_source(db, "map/" + table, "bin64/staticdata/mapObjects.db#" + table, HASHES[path.name],
                         ((row[key], dict(row)) for row in source.execute("SELECT * FROM " + table)))


def create_model_views(db):
    for name, in db.execute("SELECT Name FROM SourceDataset"):
        # Identifiers and string literals use separate SQLite quoting rules.
        view = ('records_' + name.replace('/', '_')).replace('"', '""')
        literal = name.replace("'", "''")
        db.execute(f"""CREATE VIEW "{view}" AS SELECT Key,Data FROM SourceRecord WHERE Dataset='{literal}'""")


def build(root, archive, destination):
    if destination.name.lower() == "world.sqlite":
        raise ValueError("Choose a separate catalog destination. world.sqlite is the player save.")
    if destination.exists():
        with closing(sqlite3.connect(destination.as_uri() + "?mode=ro", uri=True)) as previous:
            if not previous.execute("SELECT 1 FROM sqlite_master WHERE name='SourceDataset'").fetchone():
                raise ValueError("Existing destination is not an Eden catalog")
    destination.parent.mkdir(parents=True, exist_ok=True)
    if not archive.exists():
        archive.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=archive.parent) as temp:
            download = Path(temp) / ARCHIVE
            urllib.request.urlretrieve(URL, download)
            verified(download).replace(archive)
    def populate(candidate, work):
        subprocess.run(["dotnet", "run", "--no-build", "--project", str(HERE.parents[1] / "src/Eden.Server"),
                        "--", "--create-catalog", str(candidate)], check=True)
        db = sqlite3.connect(candidate)
        try:
            read_sde(db, archive)
            read_client(db, root, work)
            db.executescript((HERE / "project.sql").read_text())
            verify_inventory(db)
            compare(db)
            create_model_views(db)
            db.commit()
            db.execute("PRAGMA wal_checkpoint(TRUNCATE)")
            db.execute("PRAGMA journal_mode=DELETE")
            if db.execute("PRAGMA foreign_key_check").fetchone() is not None:
                raise ValueError("Catalog contains an unresolved relationship")
            if db.execute("PRAGMA integrity_check").fetchone() != ("ok",):
                raise ValueError("Catalog integrity check failed")
        finally:
            db.close()

    def validate(candidate):
        subprocess.run(["dotnet", "run", "--no-build", "--project", str(HERE.parents[1] / "src/Eden.Server"),
                        "--", "--verify-catalog", str(candidate)], check=True)
    publish(destination, populate, validate)
    print(f"Activated {destination}")


def publish(destination, populate, validate):
    with tempfile.TemporaryDirectory(dir=destination.parent) as temporary:
        work = Path(temporary)
        candidate = work / "catalog.sqlite"
        populate(candidate, work)
        validate(candidate)
        os.replace(candidate, destination)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("client", type=Path)
    server = HERE.parents[1] / "src/Eden.Server"
    data = os.environ.get("EDEN_DATA_DIR", "")
    directory = server / (data if data.strip() else "data")
    archive_cache = Path(os.environ["LOCALAPPDATA"]) / "Eden/catalog-input"
    parser.add_argument("--archive", type=Path, default=archive_cache / ARCHIVE)
    parser.add_argument("--output", type=Path, default=directory / "catalog.sqlite")
    args = parser.parse_args()
    build(args.client.resolve(), args.archive.resolve(), args.output.resolve())
