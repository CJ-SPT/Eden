"""Build and announce Eden's Windows downpatcher from a Linux host."""

import argparse
from configparser import ConfigParser, Error as ConfigError
import hashlib
import importlib
from io import BytesIO
import json
from pathlib import Path
import re
import sys
from tempfile import TemporaryDirectory
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import HTTPRedirectHandler, Request, build_opener
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo


patcher = importlib.import_module("Downpatch-Client")
TOOLS = Path(__file__).resolve().parent
DEPLOY = TOOLS / "client-downpatcher"
STATUS_URL = "https://esi.evetech.net/latest/status/"
PYTHON_VERSION = "3.14.10"
PYTHON_SHA256 = "90905e64a840557d8c1bc4f5b74db3d7ee85e3ffb217611cac1c7813acea4159"
PYTHON_URL = f"https://www.python.org/ftp/python/{PYTHON_VERSION}/python-{PYTHON_VERSION}-embed-amd64.zip"
MAX_PACKAGE_SIZE = 24 * patcher.CHUNK_SIZE
START_INI = Path("tq/start.ini")
DISCORD_PATH = re.compile(r"/api(?:/v10)?/webhooks/[0-9]+/[A-Za-z0-9_-]+")
BUILD_NUMBER = re.compile(r"[1-9][0-9]{0,11}")


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def fetch(url, limit):
    request = Request(url, headers={"User-Agent": "Eden-Downpatcher/1 (+https://github.com/CJ-SPT/Eden)"})
    with build_opener().open(request, timeout=patcher.NETWORK_TIMEOUT) as response:
        data = response.read(limit + 1)
    if len(data) > limit:
        raise ValueError(f"Response exceeds the expected size at {url}")
    return data


def current_build():
    status = json.loads(fetch(STATUS_URL, patcher.CHUNK_SIZE))
    if not isinstance(status, dict):
        raise ValueError("Unexpected Tranquility status response")
    if status.get("vip") is True:
        return None
    build = status.get("server_version")
    if not isinstance(build, str) or not BUILD_NUMBER.fullmatch(build):
        raise ValueError("Tranquility status is missing a numeric server version")
    return int(build)


def verify_build(build):
    with TemporaryDirectory(prefix="eden-downpatch-check-") as directory:
        work = Path(directory)
        live_index = fetch(f"{patcher.BINARIES}/eveonline_{build}.txt", patcher.CHUNK_SIZE)
        live = patcher.read_index(live_index, resources=False, pin_sha256=False)
        if START_INI not in live:
            raise ValueError(f"Tranquility manifest {build} lacks start.ini")
        patcher.install_entries(work / "empty", work / "live", {START_INI: live[START_INI]}, "live client")
        ini = ConfigParser(interpolation=None)
        ini.read_string((work / "live" / START_INI).read_text(encoding="utf-8-sig"))
        if ini.get("main", "build", fallback="") != str(build):
            raise ValueError(f"Tranquility start.ini does not match build {build}")

        pinned = work / "pinned"
        pinned.mkdir()
        target = patcher.read_index(patcher.build_index(pinned), resources=False)
        critical = {Path(path) for path in patcher.PINNED_SHA256}
        if not critical.issubset(target):
            raise ValueError("The pinned Windows manifest lacks required client files")
        patcher.install_entries(work / "empty", pinned, {path: target[path] for path in sorted(critical)}, "pinned client")
        resources = {}
        for name in patcher.RESOURCE_INDEXES:
            index = patcher.read_index((pinned / "tq" / name).read_bytes(), resources=True)
            for entry in index.values():
                patcher.add_entry(resources, entry)
        print(f"CDN check passed for {build} to {patcher.BUILD} ({len(resources):,} indexed resources).", flush=True)


def package_runtime(state):
    path = state / f"python-{PYTHON_VERSION}-embed-amd64.zip"
    if path.is_file():
        cached = path.read_bytes()
        if hashlib.sha256(cached).hexdigest() == PYTHON_SHA256:
            return cached
    data = fetch(PYTHON_URL, MAX_PACKAGE_SIZE)
    if hashlib.sha256(data).hexdigest() != PYTHON_SHA256:
        raise ValueError("Windows Python runtime SHA-256 does not match the published release")
    patcher.write_metadata(path, data)
    return data


def build_package(runtime):
    output = BytesIO()
    with ZipFile(BytesIO(runtime)) as embedded, ZipFile(output, "w") as package:
        for name in sorted(embedded.namelist()):
            if not embedded.getinfo(name).is_dir():
                package.writestr(ZipInfo(f"runtime/{name}"), embedded.read(name), ZIP_DEFLATED)
        for name, source in (
            ("Downpatch-Client.py", TOOLS / "Downpatch-Client.py"),
            ("Eden-LICENSE.txt", TOOLS.parent / "LICENSE"),
            ("Prompt-Patcher.py", DEPLOY / "Prompt-Patcher.py"),
            ("README.txt", DEPLOY / "README.txt"),
            ("Start-Patcher.cmd", DEPLOY / "Start-Patcher.cmd"),
        ):
            content = source.read_bytes().replace(b"\r\n", b"\n")
            if name == "README.txt":
                content = content.replace(b"@BUILD@", patcher.BUILD_TEXT)
            if name.endswith((".cmd", ".txt")):
                content = content.replace(b"\n", b"\r\n")
            package.writestr(ZipInfo(name), content, ZIP_DEFLATED)
    return output.getvalue()


def discord_webhook(path):
    url = urlsplit(path.read_text(encoding="utf-8").strip())
    if url.scheme != "https" or url.netloc != "discord.com" or not DISCORD_PATH.fullmatch(url.path) or url.query or url.fragment:
        raise ValueError("Expected a Discord incoming webhook URL in the webhook credential file")
    return url.geturl() + "?wait=true"


def announce(webhook, receipt, build, url, checksum):
    content = (
        f"**Eden downpatcher · Tranquility {build} → {patcher.BUILD}**\n"
        "The new Windows build manifest and Eden's pinned files passed CDN verification.\n"
        f"[Download the Windows x64 ZIP](<{url}>)\n"
        "Extract it and run `Start-Patcher.cmd`. The ZIP includes instructions.\n"
        f"SHA-256 `{checksum}`"
    )
    request = Request(
        webhook, data=json.dumps({"content": content, "allowed_mentions": {"parse": []}}).encode("utf-8"),
        headers={"Content-Type": "application/json", "User-Agent": "Eden-Downpatcher/1"}, method="POST",
    )
    # Keep the pending receipt if the response is lost. Discord may already have stored the message.
    patcher.write_metadata(receipt, b"pending\n")
    try:
        with build_opener(NoRedirect()).open(request, timeout=patcher.NETWORK_TIMEOUT) as response:
            message_id = json.load(response)["id"]
        if not isinstance(message_id, str) or not re.fullmatch(r"[0-9]+", message_id):
            raise ValueError("Discord returned an invalid message ID")
    except HTTPError as error:
        error.close()
        if 400 <= error.code < 500 and error.code != 408:
            receipt.unlink()
            raise ValueError(f"Discord rejected the announcement with HTTP {error.code}") from None
        raise ValueError(f"Discord delivery is uncertain (HTTP {error.code}). Check {receipt} before retrying.") from None
    except (KeyError, OSError, TypeError, ValueError):
        raise ValueError(f"Discord delivery is uncertain. Check {receipt} before retrying.") from None
    patcher.write_metadata(receipt, (message_id + "\n").encode("ascii"))
    print(f"Announced Tranquility {build} in Discord message {message_id}.", flush=True)


def publish(build, state, directory, public_url, webhook):
    receipt = state / f"{build}-to-{patcher.BUILD}.discord"
    if receipt.exists():
        if re.fullmatch(r"[0-9]+", receipt.read_text(encoding="ascii").strip()):
            print(f"Tranquility {build} was already announced.")
            return
        raise ValueError(f"Discord delivery is uncertain. Check {receipt} before retrying.")

    verify_build(build)
    data = build_package(package_runtime(state))
    checksum = hashlib.sha256(data).hexdigest()
    name = f"Eden-Downpatcher-{patcher.BUILD}-win-x64-{checksum}.zip"
    archive = directory / name
    if not archive.is_file() or archive.read_bytes() != data:
        patcher.write_metadata(archive, data)
    url = f"{public_url.rstrip('/')}/{name}"
    if hashlib.sha256(fetch(url, MAX_PACKAGE_SIZE)).hexdigest() != checksum:
        raise ValueError("The public download does not match the Windows ZIP")
    announce(webhook, receipt, build, url, checksum)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check-only", action="store_true", help="verify CCP manifests and pinned files without publishing")
    parser.add_argument("--state-dir", type=Path)
    parser.add_argument("--publish-dir", type=Path)
    parser.add_argument("--public-url", help="HTTPS directory URL serving the publish directory")
    parser.add_argument("--webhook-file", type=Path)
    args = parser.parse_args()
    if not args.check_only:
        if not all((args.state_dir, args.publish_dir, args.public_url, args.webhook_file)):
            parser.error("Publishing requires --state-dir, --publish-dir, --public-url and --webhook-file.")
        url = urlsplit(args.public_url)
        if url.scheme != "https" or not url.hostname or url.username or url.query or url.fragment:
            parser.error("--public-url must be an HTTPS directory URL without credentials, a query or a fragment.")
        if sys.platform == "win32":
            parser.error("Run the scheduled publisher on Linux. Use --check-only to verify from Windows.")

    try:
        build = current_build()
        if build is None or build <= patcher.BUILD:
            print(f"No live Tranquility build newer than {patcher.BUILD} to announce.")
        elif args.check_only:
            verify_build(build)
        else:
            import fcntl

            with (args.state_dir / "publish.lock").open("ab") as lock:
                fcntl.flock(lock, fcntl.LOCK_EX)
                publish(build, args.state_dir, args.publish_dir, args.public_url, discord_webhook(args.webhook_file))
    except (OSError, ValueError, ConfigError) as error:
        parser.exit(1, f"Downpatch publisher: {error}\n")


if __name__ == "__main__":
    main()
