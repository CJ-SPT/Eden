"""Fixture tests for the Ubuntu publisher. No credentials or Discord posts required."""

import hashlib
import importlib
from io import BytesIO
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch
from urllib.error import HTTPError, URLError
from zipfile import ZipFile


sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "tools"))
try:
    publisher = importlib.import_module("Watch-ClientDownpatch")
finally:
    sys.path.pop(0)


class ClientDownpatchPublisherTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.state = Path(temp.name) / "state"
        self.state.mkdir()
        self.public = Path(temp.name) / "public"
        self.public.mkdir()
        self.build = publisher.patcher.BUILD + 1
        self.receipt = self.state / f"{self.build}-to-{publisher.patcher.BUILD}.discord"
        self.public_url = "https://downloads.example.test/downpatchers"
        self.webhook = "https://discord.com/api/webhooks/123/fixture-token?wait=true"

    def test_publishes_verified_zip_and_suppresses_duplicate_announcements(self):
        embedded = BytesIO()
        with ZipFile(embedded, "w") as runtime:
            runtime.writestr("python.exe", b"example runtime")

        def public_download(url, limit):
            return (self.public / url.rsplit("/", 1)[1]).read_bytes()

        discord = Mock()
        discord.open.side_effect = lambda *args, **kwargs: BytesIO(b'{"id":"456"}')
        with (
            patch.object(publisher, "verify_build") as verify,
            patch.object(publisher, "package_runtime", return_value=embedded.getvalue()),
            patch.object(publisher, "fetch", side_effect=public_download),
            patch.object(publisher, "build_opener", return_value=discord),
        ):
            publisher.publish(self.build, self.state, self.public, self.public_url, self.webhook)
            publisher.publish(self.build, self.state, self.public, self.public_url, self.webhook)

        verify.assert_called_once_with(self.build)
        self.assertEqual("456\n", self.receipt.read_text())
        (zip_path,) = self.public.glob("*.zip")
        checksum = hashlib.sha256(zip_path.read_bytes()).hexdigest()
        self.assertIn(checksum, zip_path.name)
        with ZipFile(zip_path) as package:
            self.assertIsNone(package.testzip())
            self.assertEqual(b"example runtime", package.read("runtime/python.exe"))
            self.assertIn(str(publisher.patcher.BUILD).encode(), package.read("README.txt"))
            self.assertIn(b"Prompt-Patcher.py", package.read("Start-Patcher.cmd"))
        discord.open.assert_called_once()
        request = discord.open.call_args.args[0]
        message = json.loads(request.data)
        self.assertEqual(self.webhook, request.full_url)
        self.assertEqual({"parse": []}, message["allowed_mentions"])
        self.assertIn(f"{self.public_url}/{zip_path.name}", message["content"])
        self.assertIn(checksum, message["content"])

    def test_verification_or_bad_public_download_prevents_a_post(self):
        with (
            patch.object(publisher, "verify_build", side_effect=ValueError("CDN failure")) as verify,
            patch.object(publisher, "package_runtime"),
            patch.object(publisher, "build_package", return_value=b"local package"),
            patch.object(publisher, "fetch", return_value=b"different public package"),
            patch.object(publisher, "announce") as announce,
        ):
            with self.assertRaisesRegex(ValueError, "CDN failure"):
                publisher.publish(self.build, self.state, self.public, self.public_url, self.webhook)
            self.assertEqual([], list(self.public.iterdir()))
            verify.side_effect = None
            with self.assertRaisesRegex(ValueError, "public download"):
                publisher.publish(self.build, self.state, self.public, self.public_url, self.webhook)
        announce.assert_not_called()
        self.assertFalse(self.receipt.exists())

    def test_rate_limit_allows_retry_but_an_ambiguous_delivery_blocks_another_post(self):
        discord = Mock()
        discord.open.side_effect = [HTTPError(self.webhook, 429, "rate limited", {}, None), URLError("response lost")]
        with patch.object(publisher, "build_opener", return_value=discord):
            with self.assertRaisesRegex(ValueError, "HTTP 429"):
                publisher.announce(self.webhook, self.receipt, self.build, self.public_url, "checksum")
            self.assertFalse(self.receipt.exists())
            with self.assertRaisesRegex(ValueError, "delivery is uncertain") as error:
                publisher.announce(self.webhook, self.receipt, self.build, self.public_url, "checksum")
            self.assertNotIn("fixture-token", str(error.exception))
            self.assertEqual("pending\n", self.receipt.read_text())
            with self.assertRaisesRegex(ValueError, "delivery is uncertain"):
                publisher.publish(self.build, self.state, self.public, self.public_url, self.webhook)
        self.assertEqual(2, discord.open.call_count)

    def test_status_rejects_malformed_versions_and_skips_vip_mode(self):
        with patch.object(publisher, "fetch", return_value=b'{"server_version":"3724492","vip":true}'):
            self.assertIsNone(publisher.current_build())
        with patch.object(publisher, "fetch", return_value=b'{"server_version":"../3724492"}'):
            with self.assertRaisesRegex(ValueError, "numeric server version"):
                publisher.current_build()


if __name__ == "__main__":
    unittest.main()
