"""Verify administration assets after Docker's project-only restore sequence."""

import json
from pathlib import Path
import shutil
import subprocess
import tempfile


def main():
    root = Path(__file__).resolve().parents[1]
    scratch = root / "artifacts" / "test-data"
    scratch.mkdir(parents=True, exist_ok=True)
    source_files = subprocess.check_output(
        ["git", "ls-files", "-z", "--", "src"], cwd=root
    ).decode().split("\0")
    source_files = [name for name in source_files if name]

    with tempfile.TemporaryDirectory(prefix="eden-admin-publish-", dir=scratch) as directory:
        staging = Path(directory)

        def copy(name):
            destination = staging / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(root / name, destination)

        for name in ["global.json", "Directory.Build.props", "Directory.Build.targets", ".editorconfig"]:
            copy(name)
        for name in source_files:
            if name.endswith(".csproj"):
                copy(name)

        project = "src/Eden.Server/Eden.Server.csproj"
        subprocess.run(
            ["dotnet", "restore", project, "-p:Configuration=Release", "-p:UseAppHost=false"],
            cwd=staging,
            check=True,
        )
        assets = json.loads((staging / "src/Eden.Server/obj/project.assets.json").read_text())
        if not any(name.startswith("Microsoft.AspNetCore.App.Internal.Assets/") for name in assets["libraries"]):
            raise AssertionError("Project-only restore omitted Blazor framework assets; published controls cannot initialize.")

        for name in source_files:
            copy(name)
        for name in [
            "assets/icons/eden-server.ico",
            "tools/Build-SkillClient.py",
            "tools/mod_client_loader.py",
            "tools/skill_client_overlay.py",
        ]:
            copy(name)
        publish = staging / "publish"
        subprocess.run(
            [
                "dotnet", "publish", project, "-c", "Release", "--no-restore",
                "-o", str(publish), "-p:UseAppHost=false",
            ],
            cwd=staging,
            check=True,
        )
        manifest = json.loads((publish / "Eden.Server.staticwebassets.endpoints.json").read_text())
        routes = {endpoint["Route"] for endpoint in manifest["Endpoints"]}
        for name in ["blazor.web.js", "blazor.server.js"]:
            route = "_framework/" + name
            if route not in routes:
                raise AssertionError(f"Published administration is missing the {route} endpoint.")
            script = publish / "wwwroot" / route
            if not script.is_file() or script.stat().st_size < 1000:
                raise AssertionError(f"Published administration is missing the {route} payload.")
        print("Project-only restore and Release publish include Blazor framework endpoints and payloads.")


if __name__ == "__main__":
    main()
