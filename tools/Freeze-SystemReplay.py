"""Freeze read-only copies for matched multi-system server replays."""

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sqlite3
import subprocess


def freeze(source: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=False)
    files = {}
    for name in ("world.sqlite", "catalog.sqlite", "traffic.sqlite"):
        original = source / name
        if not original.exists() and name == "traffic.sqlite":
            continue
        target = destination / name
        with sqlite3.connect(original.as_uri() + "?mode=ro", uri=True) as reader:
            with sqlite3.connect(target) as writer:
                reader.backup(writer)
        with target.open("rb") as data:
            digest = hashlib.file_digest(data, "sha256").hexdigest()
        files[name] = {"bytes": target.stat().st_size, "sha256": digest}
    for name in ("world.sqlite.skills.sqlite", "world.sqlite.skills-active"):
        if (source / name).exists():
            if name.endswith(".sqlite"):
                with sqlite3.connect((source / name).as_uri() + "?mode=ro", uri=True) as reader:
                    with sqlite3.connect(destination / name) as writer:
                        reader.backup(writer)
            else:
                shutil.copy2(source / name, destination / name)
            with (destination / name).open("rb") as data:
                digest = hashlib.file_digest(data, "sha256").hexdigest()
            files[name] = {"bytes": (destination / name).stat().st_size, "sha256": digest}
    marker = destination / "world.sqlite.skills-active"
    if marker.exists():
        package = marker.read_text().strip()
        if len(package) != 64 or any(c not in "0123456789abcdefABCDEF" for c in package):
            raise ValueError("Invalid active skill package identity")
        relative = Path("world.sqlite.skill-packages") / package
        shutil.copytree(source / relative, destination / relative)
        for asset in (destination / relative).rglob("*"):
            if asset.is_file():
                with asset.open("rb") as data:
                    digest = hashlib.file_digest(data, "sha256").hexdigest()
                files[asset.relative_to(destination).as_posix()] = {"bytes": asset.stat().st_size, "sha256": digest}
    manifest = {
        "baselineRevision": subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip(),
        "clock": "2026-11-10T12:00:00Z",
        "files": files,
    }
    (destination / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    freeze(args.source.resolve(), args.destination.resolve())
