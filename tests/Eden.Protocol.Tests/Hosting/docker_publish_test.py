"""Exercise the Docker publish helper against real projects without launching Eden or requiring Docker."""
from pathlib import Path
import hashlib
import os
import shutil
import subprocess
import tempfile
import time
import unittest


ROOT = Path(__file__).resolve().parents[3]
PROJECTS = (
    "Eden.Data", "Eden.Gameplay", "Eden.Infrastructure", "Eden.Modding",
    "Eden.Protocol", "Eden.Server", "Eden.World",
)
FIRST_REVISION = "1" * 40
SECOND_REVISION = "2" * 40


def bash_executable():
    configured = os.environ.get("EDEN_TEST_BASH")
    if configured:
        return configured
    windows_git = Path("C:/Program Files/Git/bin/bash.exe")
    return str(windows_git) if windows_git.is_file() else shutil.which("bash")


class DockerPublishTests(unittest.TestCase):
    def test_publish_reuses_and_invalidates_compiled_outputs(self):
        bash = bash_executable()
        if not bash:
            self.skipTest("Bash is required (Git Bash on Windows).")
        with tempfile.TemporaryDirectory(prefix="eden-docker-publish-") as temporary:
            working = Path(temporary)
            source = working / "input"
            source.mkdir()
            tracked = subprocess.check_output(["git", "ls-files", "-z", "--", "src"], cwd=ROOT)
            paths = [path.decode("utf-8") for path in tracked.split(b"\0") if path]
            paths.extend((
                "global.json", "Directory.Build.props", "Directory.Build.targets", ".editorconfig",
                "assets/icons/eden-server.ico", "tools/Build-SkillClient.py", "tools/mod_client_loader.py",
                "tools/skill_client_overlay.py", "tools/Import-LiveMarket.py", "tools/Publish-Docker.sh",
            ))
            for relative in paths:
                destination = source / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(ROOT / relative, destination)

            probe = source / "src/Eden.Server/DockerPublishProbe.cs"
            self.write_probe(probe, "Eden.Server", "before")
            cache = working / "cache"

            def publish(label, revision=SECOND_REVISION, cache_path=cache):
                output = working / label
                command = [bash, (source / "tools/Publish-Docker.sh").as_posix(),
                           source.as_posix(), cache_path.as_posix(), output.as_posix(), revision]
                started = time.perf_counter()
                result = subprocess.run(command, capture_output=True, text=True, timeout=300)
                elapsed = time.perf_counter() - started
                if result.returncode:
                    self.fail(f"{label} failed:\n{result.stdout}\n{result.stderr}")
                summary = next(line for line in result.stdout.splitlines() if line.startswith("Eden compilation cache:"))
                duration = next(line for line in result.stdout.splitlines() if line.startswith("Eden dotnet publish:"))
                print(f"{label}: {elapsed:.2f}s total; {summary} {duration}", flush=True)
                for project in PROJECTS:
                    self.assertGreater((output / (project + ".dll")).stat().st_size, 0)
                self.assertGreater((output / "wwwroot/_framework/blazor.web.js").stat().st_size, 0)
                self.assertGreater((output / "data-tools/Import-LiveMarket.py").stat().st_size, 0)
                return self.compilations(cache_path), output

            cold, _ = publish("cold", FIRST_REVISION)
            # A new checkout can change every source timestamp without changing its contents.
            for path in source.rglob("*"):
                if path.is_file():
                    os.utime(path, None)
            unchanged, _ = publish("unchanged", FIRST_REVISION)
            self.assertEqual(cold, unchanged)

            revised, _ = publish("revision-only")
            for project in PROJECTS:
                if project != "Eden.Server":
                    self.assertEqual(unchanged[project], revised[project], project)
            self.assertNotEqual(unchanged["Eden.Server"], revised["Eden.Server"])
            self.assert_revision(cache, SECOND_REVISION)

            self.write_probe(probe, "Eden.Server", "after")
            os.utime(probe, (1, 1))
            changed, _ = publish("server-edit-with-old-timestamp")
            self.assertNotEqual(revised["Eden.Server"][0], changed["Eden.Server"][0])
            for project in PROJECTS:
                if project != "Eden.Server":
                    self.assertEqual(revised[project], changed[project], project)

            probe.unlink()
            deleted, _ = publish("source-deletion")
            self.assertNotEqual(changed["Eden.Server"][0], deleted["Eden.Server"][0])
            self.assertFalse((cache / "source/src/Eden.Server/DockerPublishProbe.cs").exists())

            dependency_probe = source / "src/Eden.Gameplay/DockerPublishProbe.cs"
            self.write_probe(dependency_probe, "Eden.Gameplay", "dependency")
            os.utime(dependency_probe, (1, 1))
            dependency, _ = publish("dependency-edit")
            self.assertNotEqual(deleted["Eden.Gameplay"][0], dependency["Eden.Gameplay"][0])
            self.assertEqual(deleted["Eden.Infrastructure"], dependency["Eden.Infrastructure"])

            world_probe = source / "src/Eden.World/DockerPublishProbe.cs"
            self.write_probe(world_probe, "Eden.World", "world")
            changed_world, _ = publish("world-edit")
            self.assertNotEqual(dependency["Eden.World"][0], changed_world["Eden.World"][0])
            self.assertEqual(dependency["Eden.Protocol"], changed_world["Eden.Protocol"])
            self.assertEqual(dependency["Eden.Gameplay"], changed_world["Eden.Gameplay"])

            evicted_cache = working / "empty-cache"
            publish("evicted-cache", cache_path=evicted_cache)
            self.assert_revision(evicted_cache, SECOND_REVISION)

            rejected_cache = source / "nested-cache"
            rejected = subprocess.run(
                [bash, (source / "tools/Publish-Docker.sh").as_posix(), source.as_posix(),
                 rejected_cache.as_posix(), (working / "rejected").as_posix(), SECOND_REVISION],
                capture_output=True, text=True, timeout=15,
            )
            self.assertEqual(2, rejected.returncode)
            self.assertIn("outside the source directory", rejected.stderr)
            self.assertFalse((rejected_cache / "source").exists())

    @staticmethod
    def write_probe(path, namespace, marker):
        path.write_text(
            f'namespace {namespace};\n\ninternal static class DockerPublishProbe\n'
            f'{{\n    internal const string Marker = "{marker}";\n}}\n', encoding="utf-8",
        )

    @staticmethod
    def compilations(cache):
        result = {}
        for project in PROJECTS:
            assembly = cache / "artifacts/bin" / project / "release" / (project + ".dll")
            result[project] = (hashlib.sha256(assembly.read_bytes()).hexdigest(), assembly.stat().st_mtime_ns)
        return result

    def assert_revision(self, cache, revision):
        assembly_info = cache / "artifacts/obj/Eden.Server/release/Eden.Server.AssemblyInfo.cs"
        self.assertIn("+" + revision, assembly_info.read_text(encoding="utf-8-sig"))


if __name__ == "__main__":
    unittest.main()
