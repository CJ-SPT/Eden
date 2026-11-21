"""Stage Eden's pinned Windows EVE client using the live SharedCache and CCP's CDN."""

import argparse
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from dataclasses import dataclass
import gzip
import hashlib
from http.client import IncompleteRead
from itertools import islice
from pathlib import Path
import re
import stat
import sys
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.request import urlopen
from zlib import error as DecompressionError


BUILD = 3662168
BINARIES = "https://binaries.eveonline.com"
RESOURCES = "https://resources.eveonline.com"
BUILD_INDEX_SHA256 = "b180f67a5bc214df1da6964d065c536758934466bb4fb2fd93461c7dac5bb684"
PINNED_SHA256 = {
    "tq/start.ini": "4f8216ef3873b779acdcf414d6a061c61545fb2a489bb3288ef0cfd95b9965f0",
    "tq/code.ccp": "37fb2af4fa0ec5c8fa320f34cd28c8b7dbf4917418336b38cba20a7860db0368",
    "tq/bin64/exefile.exe": "4c33ad1b4d95c80d2d314849ff6b577a27b6d2420d3ca52641ec3a354ea3d787",
    "tq/bin64/blue.dll": "bfc3c167d25a5bd2cfaf0ea8d60e25028e9f034380885746bcccfb73645fc94a",
    "tq/bin64/packages/certifi/cacert.pem": "54abc073292ab1b3ea95361d7e9fbc0fdf3c2b74777f0a4e2b6ec26670f0a453",
    "tq/manifest.dat": "895b2827174d7bddc54870763e9d5aea056066c01e3d56f0ddf76927dcd3aa26",
    "tq/resfileindex.txt": "cba0852b46e3a1501c6c0653546c4520dbd6aba02a51e1da3409f5efeb253f03",
    "tq/resfileindex_Windows.txt": "0d0ae185002cd41661011af0bf2e6b52f947ca4a8812f73a72ec7152a1f3078b",
    "tq/resfileindex_prefetch.txt": "6d5d67918bcf8804103d92e1211d1d58a5da564dead279912b453c687ad92373",
}
RESOURCE_INDEXES = ("resfileindex.txt", "resfileindex_Windows.txt", "resfileindex_prefetch.txt")
IN_PROGRESS = ".eden-downpatch-in-progress"
READY = ".eden-client-ready"
BUILD_MARKER = f".eden-build-{BUILD}"
BUILD_TEXT = str(BUILD).encode("ascii")
INDEX_FILE = "index_tranquility.txt"
CHUNK_SIZE = 1024 * 1024
NETWORK_TIMEOUT = 30
ATTEMPTS = 3
WORKERS = 12
BATCH_SIZE = 128
TRANSIENT_STATUS = {408, 429, 500, 502, 503, 504}
REPARSE_POINT = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
CDN_PATH = re.compile(r"[a-f0-9]{2}/[a-f0-9]{16}_[a-f0-9]{32}")
APP_COMPONENT = re.compile(r"[A-Za-z0-9_ .-]+")


class InvalidPayload(ValueError):
    pass


@dataclass(frozen=True)
class Entry:
    path: Path
    url: str
    size: int
    md5: str
    sha256: str | None


def read_index(data, *, resources, pin_sha256=True):
    scheme = "res:/" if resources else "app:/"
    sha256_by_path = PINNED_SHA256 if pin_sha256 else {}
    entries = {}
    for line in data.decode("utf-8-sig").splitlines():
        if not line:
            continue
        logical_path, blob, md5, size, *_ = line.split(",")
        size = int(size)
        if not logical_path.startswith(scheme) or not CDN_PATH.fullmatch(blob) or blob.rsplit("_", 1)[1] != md5 or size < 0:
            raise ValueError(f"Invalid CDN index row for {logical_path}")
        if resources:
            path = Path("ResFiles", blob)
            origin = RESOURCES
        else:
            components = logical_path[len(scheme):].split("/")
            if any(not APP_COMPONENT.fullmatch(part) or part.endswith((".", " ")) for part in components):
                raise ValueError(f"Invalid client path in CDN index: {logical_path}")
            path = Path("tq", *components)
            origin = BINARIES
        entry = Entry(path, f"{origin}/{blob}", size, md5, sha256_by_path.get(path.as_posix()))
        add_entry(entries, entry)
    if not entries:
        raise ValueError(f"Empty {scheme} CDN index")
    return entries


def add_entry(entries, entry):
    if entries.setdefault(entry.path, entry) != entry:
        raise ValueError(f"Conflicting CDN index entries for {entry.path}")


@contextmanager
def destination_lock(destination):
    import msvcrt

    lock_name = hashlib.sha256(str(destination).casefold().encode("utf-8")).hexdigest()
    with (Path(tempfile.gettempdir()) / f"eden-downpatch-{lock_name}.lock").open("a+b") as lock:
        lock.seek(0)
        try:
            msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        except OSError as error:
            raise ValueError(f"Another downpatch is using {destination}") from error
        yield


def assert_regular(path, directory=False):
    try:
        info = path.lstat()
    except FileNotFoundError:
        return
    expected = stat.S_ISDIR if directory else stat.S_ISREG
    if not expected(info.st_mode) or getattr(info, "st_file_attributes", 0) & REPARSE_POINT:
        raise ValueError(f"Unexpected file type or link in the destination: {path}")


@contextmanager
def atomic_output(path):
    temporary = path.with_name(path.name + ".eden-part")
    temporary.unlink(missing_ok=True)
    try:
        with temporary.open("xb") as stream:
            yield stream
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def write_metadata(path, data):
    assert_regular(path)
    with atomic_output(path) as stream:
        stream.write(data)


def read_http(url):
    return urlopen(url, timeout=NETWORK_TIMEOUT)


def with_retries(url, operation):
    for attempt in range(ATTEMPTS):
        try:
            return operation()
        except HTTPError as error:
            error.close()
            if error.code not in TRANSIENT_STATUS or attempt == ATTEMPTS - 1:
                raise ValueError(f"{url} returned HTTP {error.code}") from error
        except (URLError, TimeoutError, ConnectionError, IncompleteRead, EOFError,
                gzip.BadGzipFile, DecompressionError, InvalidPayload) as error:
            if attempt == ATTEMPTS - 1:
                raise ValueError(f"Could not retrieve a verified payload from {url}: {error}") from error
        time.sleep(2 ** attempt)


def build_index(destination):
    path = destination / INDEX_FILE
    assert_regular(path)
    if path.is_file():
        data = path.read_bytes()
        if hashlib.sha256(data).hexdigest() == BUILD_INDEX_SHA256:
            return data

    url = f"{BINARIES}/eveonline_{BUILD}.txt"

    def fetch():
        with read_http(url) as response:
            data = response.read(CHUNK_SIZE)
        if hashlib.sha256(data).hexdigest() != BUILD_INDEX_SHA256:
            raise InvalidPayload(f"Unexpected Windows build {BUILD} index checksum")
        return data

    data = with_retries(url, fetch)
    write_metadata(path, data)
    return data


def verified_copy(source, target, entry):
    md5 = hashlib.md5(usedforsecurity=False)
    sha256 = hashlib.sha256() if entry.sha256 else None
    remaining = entry.size
    with atomic_output(target) as output:
        while chunk := source.read(min(CHUNK_SIZE, remaining + 1)):
            remaining -= len(chunk)
            if remaining < 0:
                break
            md5.update(chunk)
            if sha256 is not None:
                sha256.update(chunk)
            output.write(chunk)
        if remaining or md5.hexdigest() != entry.md5 or (sha256 is not None and sha256.hexdigest() != entry.sha256):
            raise InvalidPayload(f"Unexpected size or checksum for {entry.path}")


def matches_existing(path, entry):
    if not path.is_file() or path.stat().st_size != entry.size:
        return False
    with path.open("rb") as stream:
        if hashlib.file_digest(stream, lambda: hashlib.md5(usedforsecurity=False)).hexdigest() != entry.md5:
            return False
        if entry.sha256:
            stream.seek(0)
            return hashlib.file_digest(stream, "sha256").hexdigest() == entry.sha256
    return True


def install_entry(source, destination, entry):
    target = destination / entry.path
    assert_regular(target)
    if matches_existing(target, entry):
        return "resumed"

    live = source / entry.path
    try:
        if live.stat().st_size == entry.size:
            with live.open("rb") as stream:
                verified_copy(stream, target, entry)
            return "reused"
    except (FileNotFoundError, InvalidPayload):
        pass

    def download():
        with read_http(entry.url) as response:
            if response.headers.get("Content-Encoding") == "gzip":
                with gzip.GzipFile(fileobj=response) as decoded:
                    verified_copy(decoded, target, entry)
            else:
                verified_copy(response, target, entry)

    with_retries(entry.url, download)
    return "downloaded"


def install_entries(source, destination, entries, description):
    parents = {entry.path.parent for entry in entries.values()}
    directories = {parent for path in parents for parent in (path, *path.parents)}
    for directory in sorted(directories, key=lambda path: len(path.parts)):
        path = destination / directory
        assert_regular(path, directory=True)
        path.mkdir(exist_ok=True)

    def install(entry):
        try:
            return install_entry(source, destination, entry)
        except (OSError, ValueError) as error:
            raise ValueError(f"Could not stage {entry.path}: {error}") from error

    counts = Counter()
    last_update = time.monotonic()
    print(f"Staging {len(entries):,} {description} files...", flush=True)
    with ThreadPoolExecutor(max_workers=WORKERS) as pool:
        pending = iter(entries.values())
        while batch := list(islice(pending, BATCH_SIZE)):
            counts.update(pool.map(install, batch))
            processed = sum(counts.values())
            now = time.monotonic()
            if processed == len(entries) or now - last_update >= 10:
                print(
                    f"  {processed:,}/{len(entries):,} {description} files "
                    f"({counts['reused']:,} reused, {counts['downloaded']:,} downloaded, "
                    f"{counts['resumed']:,} already staged)",
                    flush=True,
                )
                last_update = now


def downpatch(source, destination):
    source = source.resolve(strict=True)
    destination = destination.resolve()
    if source == destination or source in destination.parents or destination in source.parents:
        raise ValueError("Use separate, non-overlapping live and Eden folders.")
    if not (source / "tq" / "start.ini").is_file() or not (source / "ResFiles").is_dir():
        raise ValueError("Choose the live SharedCache folder containing tq and ResFiles.")

    with destination_lock(destination):
        destination.mkdir(parents=True, exist_ok=True)
        ready = destination / READY
        progress = destination / IN_PROGRESS
        assert_regular(ready)
        assert_regular(progress)
        if ready.exists():
            raise ValueError(f"An Eden client is already prepared at {destination}. Choose a new destination.")
        if progress.is_file():
            if progress.read_bytes() != BUILD_TEXT:
                raise ValueError(f"Unrecognized downpatch in progress at {destination}.")
        else:
            if any(path.name != IN_PROGRESS + ".eden-part" for path in destination.iterdir()):
                raise ValueError("Choose an empty destination for initial preparation.")
            write_metadata(progress, BUILD_TEXT)

        app = read_index(build_index(destination), resources=False)
        if any(Path(path) not in app for path in PINNED_SHA256):
            raise ValueError("The Windows CDN index is missing pinned client files.")
        install_entries(source, destination, app, "client")
        assets = {}
        for name in RESOURCE_INDEXES:
            index = read_index((destination / "tq" / name).read_bytes(), resources=True)
            for entry in index.values():
                add_entry(assets, entry)
        install_entries(source, destination, assets, "resource")
        write_metadata(destination / BUILD_MARKER, BUILD_TEXT)
        write_metadata(ready, BUILD_TEXT)
        progress.unlink()
    print(f"Eden client build {BUILD} is ready at {destination}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path, help="live EVE SharedCache containing tq and ResFiles")
    parser.add_argument("--destination", required=True, type=Path, help="separate, empty Eden client directory")
    args = parser.parse_args()
    if sys.platform != "win32":
        parser.exit(1, "Run the downpatcher with Windows Python.\n")
    try:
        downpatch(args.source, args.destination)
    except KeyboardInterrupt:
        parser.exit(130, "Downpatch interrupted. Rerun the same command to resume.\n")
    except (OSError, ValueError) as error:
        parser.exit(1, f"Downpatch failed: {error}\n")


if __name__ == "__main__":
    main()
