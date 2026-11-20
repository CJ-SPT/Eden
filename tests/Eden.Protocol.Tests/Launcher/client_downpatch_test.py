"""Fixture checks for the standalone Windows downpatcher. No CDN or game process required."""

import gzip
import hashlib
import importlib.util
from io import BytesIO
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError


script = Path(__file__).resolve().parents[3] / "tools" / "Downpatch-Client.py"
spec = importlib.util.spec_from_file_location("downpatch_client", script)
patcher = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = patcher
spec.loader.exec_module(patcher)


class ClientDownpatchTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.live = self.root / "live"
        self.destination = self.root / "eden"
        self.payloads = {}
        self.requests = []
        self.write(self.live / "tq" / "start.ini", b"live build")
        (self.live / "ResFiles").mkdir()

    @staticmethod
    def write(path, data):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def index_row(self, name, data, origin, *, compressed=True):
        digest = hashlib.md5(data, usedforsecurity=False).hexdigest()
        blob = f"af/af23456789012345_{digest}"
        encoded = gzip.compress(data) if compressed else data
        url = f"{origin}/{blob}"
        self.payloads[url] = (encoded, "gzip" if compressed else "identity")
        return f"{name},{blob},{digest},{len(data)},{len(encoded)}\n".encode(), blob, url

    def fetch(self, url):
        self.requests.append(url)
        if url not in self.payloads:
            raise HTTPError(url, 404, "Missing fixture", {}, None)
        data, encoding = self.payloads[url]
        response = BytesIO(data)
        response.headers = {"Content-Encoding": encoding}
        return response

    @unittest.skipUnless(sys.platform == "win32", "The downpatcher targets the Windows client.")
    def test_failed_download_keeps_copy_unready_and_verified_resume_completes_it(self):
        start = b"eden build"
        start_row, _, start_url = self.index_row("app:/start.ini", start, patcher.BINARIES)
        manifest = b"unchanged binary"
        manifest_row, _, unchanged_url = self.index_row("app:/manifest.dat", manifest, patcher.BINARIES)
        self.write(self.live / "tq" / "manifest.dat", manifest)
        reused = b"a verified asset"
        reused_row, reused_blob, reused_url = self.index_row("res:/reused", reused, patcher.RESOURCES)
        self.write(self.live / "ResFiles" / reused_blob, reused)
        missing = gzip.compress(b"an asset that is already compressed")
        missing_row, missing_blob, missing_url = self.index_row(
            "res:/missing", missing, patcher.RESOURCES, compressed=False
        )
        raw_index = start_row + manifest_row
        for name, contents in zip(patcher.RESOURCE_INDEXES, (reused_row, missing_row, reused_row), strict=True):
            row, _, _ = self.index_row(f"app:/{name}", contents, patcher.BINARIES)
            raw_index += row
        index_url = f"{patcher.BINARIES}/eveonline_{patcher.BUILD}.txt"
        self.payloads[index_url] = (raw_index, "identity")
        missing_payload = self.payloads.pop(missing_url)
        pinned = {"tq/start.ini": hashlib.sha256(start).hexdigest()}

        with (patch.object(patcher, "read_http", self.fetch),
              patch.object(patcher, "BUILD_INDEX_SHA256", hashlib.sha256(raw_index).hexdigest()),
              patch.object(patcher, "PINNED_SHA256", pinned)):
            with self.assertRaisesRegex(ValueError, "HTTP 404"):
                patcher.downpatch(self.live, self.destination)
            self.assertFalse((self.destination / patcher.READY).exists())
            self.assertFalse((self.destination / "ResFiles" / missing_blob).exists())
            self.assertEqual(start, (self.destination / "tq" / "start.ini").read_bytes())
            self.assertNotIn(unchanged_url, self.requests)
            self.assertNotIn(reused_url, self.requests)

            self.write(self.destination / "tq" / "start.ini", b"corruption")
            self.payloads[missing_url] = missing_payload
            self.requests.clear()
            patcher.downpatch(self.live, self.destination)
            self.assertEqual([start_url, missing_url], self.requests)
            self.assertEqual(start, (self.destination / "tq" / "start.ini").read_bytes())
            self.assertEqual(missing, (self.destination / "ResFiles" / missing_blob).read_bytes())
            self.assertEqual(patcher.BUILD_TEXT, (self.destination / patcher.READY).read_bytes())
            self.assertEqual(patcher.BUILD_TEXT, (self.destination / patcher.BUILD_MARKER).read_bytes())
            self.assertFalse((self.destination / patcher.IN_PROGRESS).exists())
            self.assertEqual(b"live build", (self.live / "tq" / "start.ini").read_bytes())
            with self.assertRaisesRegex(ValueError, "already prepared"):
                patcher.downpatch(self.live, self.destination)

    def test_download_rejects_a_same_size_corrupt_payload_before_publishing_it(self):
        row, blob, url = self.index_row("res:/file", b"asset", patcher.RESOURCES)
        entry = next(iter(patcher.read_index(row, resources=True).values()))
        self.write(self.live / "ResFiles" / blob, b"stale")
        target = self.destination / entry.path
        target.parent.mkdir(parents=True)
        self.payloads[url] = (gzip.compress(b"wrong"), "gzip")
        with patch.object(patcher, "read_http", self.fetch), patch.object(patcher.time, "sleep"):
            with self.assertRaisesRegex(ValueError, "Unexpected size or checksum"):
                patcher.install_entry(self.live, self.destination, entry)
        self.assertEqual(patcher.ATTEMPTS, len(self.requests))
        self.assertEqual([], list(target.parent.iterdir()))
        self.assertEqual(b"stale", (self.live / "ResFiles" / blob).read_bytes())

    @unittest.skipUnless(sys.platform == "win32", "The downpatcher targets the Windows client.")
    def test_source_and_existing_nonempty_destinations_are_protected(self):
        self.write(self.destination / "existing.txt", b"keep")
        for target in (self.live, self.live / "nested", self.root):
            with self.subTest(target=target), self.assertRaisesRegex(ValueError, "non-overlapping"):
                patcher.downpatch(self.live, target)
        with patch.object(patcher, "read_http", self.fetch):
            with self.assertRaisesRegex(ValueError, "empty destination"):
                patcher.downpatch(self.live, self.destination)
        self.assertEqual(["existing.txt"], [path.name for path in self.destination.iterdir()])
        self.assertEqual([], self.requests)

    def test_indexes_reject_outside_paths_and_conflicting_resource_sizes(self):
        row, _, _ = self.index_row("app:/bin64/../../../outside", b"unsafe", patcher.BINARIES)
        with self.assertRaisesRegex(ValueError, "Invalid client path"):
            patcher.read_index(row, resources=False)
        row, _, _ = self.index_row("res:/duplicated", b"same", patcher.RESOURCES)
        conflicting = row.replace(b",4,", b",5,")
        with self.assertRaisesRegex(ValueError, "Conflicting CDN index"):
            patcher.read_index(row + conflicting, resources=True)


if __name__ == "__main__":
    unittest.main()
