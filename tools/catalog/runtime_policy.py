"""Enforce the catalog storage boundary for ordinary source consumers."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
BOUNDARIES = {
    "src/Eden.Data/Catalog/Entities.cs": "Installed-record storage and typed deserialization models.",
    "src/Eden.World/Catalog/CatalogDbContext.cs": "Catalog context, writable fixtures and generated lookup adapters.",
    "src/Eden.World/Catalog/CatalogFittingReads.cs": "Typed core projections and explicit writable fixture reads.",
    "src/Eden.World/Catalog/CatalogGeographyReads.cs": "Typed geography projections and writable fixture reads.",
    "src/Eden.World/Catalog/CatalogConnectionScope.cs": "Operation-owned read-only catalog connection transport.",
    "src/Eden.World/Catalog/CatalogProvider.cs": "Shared bounded source loading, indexes and administration pages.",
    "src/Eden.World/Catalog/CatalogProvider.Geography.cs": "Shared indexed location loading.",
    "src/Eden.World/Catalog/CatalogSource.cs": "Generated source mappings and full source verification.",
    "src/Eden.World/Catalog/CatalogSources.cs": "Installed catalog build/provenance verification.",
    "src/Eden.World/Fittings/HullSupportAudit.cs": "Explicit offline hull verification/export tool.",
    "src/Eden.Server/Program.cs": "Explicit offline catalog creation and verification commands.",
}
WORLD_TRANSPORT = {
    "src/Eden.World/Storage/WorldConnections.cs": "Owned world connection plumbing.",
    "src/Eden.World/Storage/WorldDbContext.cs": "World context and detached checkpoint connection plumbing.",
    "src/Eden.World/Runtime/LiveWorld.cs": "Owned world memory image and detached checkpoint transport.",
    "src/Eden.World/Runtime/WorldSnapshot.cs": "Detached world checkpoint image transport.",
    "src/Eden.Server/Launcher/LauncherStore.cs": "Authentication persistence through the existing world connection owner.",
}
BOUNDARY_PREFIXES = {
    "src/Eden.World/Storage/Migrations/": "Saved-world upgrades and reference integrity verification.",
    "tests/": "Explicit fixtures, verification and isolated writable catalog tests.",
    "tools/": "Catalog import, migration, generation and verification tooling.",
}
TOKENS = re.compile(r'//[^\n]*|/\*[\s\S]*?\*/|\$*"{3,}[\s\S]*?"{3,}|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"')
ENTITIES = set(re.findall(r"public sealed class (\w+)",
                         (ROOT / "src/Eden.Data/Catalog/Entities.cs").read_text(encoding="utf-8-sig")))
RAW_MODELS = {"SourceRecord", "SourceDataset", "SourceDifference", "SourceOverlap"}


def boundary(path):
    path = path.replace("\\", "/")
    return BOUNDARIES.get(path) or next((reason for prefix, reason in BOUNDARY_PREFIXES.items()
                                        if path.startswith(prefix)), None)


def scan_code(path, text):
    if boundary(path) or path.endswith((".g.cs", ".generated.cs")):
        return []
    code = TOKENS.sub(lambda match: "".join("\n" if character == "\n" else " " for character in match[0]), text)
    contexts = set(re.findall(r"\bCatalogDbContext\s+(\w+)", code))
    contexts.update(re.findall(r"\bvar\s+(\w+)\s*=\s*new\s+CatalogDbContext\b", code))
    aliases = {alias for alias, target in re.findall(
        r"using\s+(\w+)\s*=\s*(?:global::)?Eden\.Data\.Catalog\.(\w+)\s*;", code) if target in ENTITIES}
    patterns = {
        "raw-storage-model": r"\b(?:" + "|".join(sorted(RAW_MODELS)) + r")\b",
        "direct-catalog-set": r"\.\s*Set\s*<\s*(?:global::)?(?:Eden\.Data\.Catalog\.)?(?:"
                              + "|".join(sorted(ENTITIES | aliases)) + r")\s*>",
        "retired-raw-reader": r"\.\s*FindSource\s*\(",
    }
    if path not in WORLD_TRANSPORT:
        patterns["unowned-sqlite-connection"] = r"\bSqliteConnection\b"
    if contexts:
        names = "|".join(re.escape(name) for name in sorted(contexts))
        patterns["catalog-database-bypass"] = r"\b(?:" + names + r")\s*\.\s*Database\b"
        patterns["direct-source-view"] = r"\.\s*(?:Query|Get)\s*\(\s*(?:" + names + r")\b"
    problems = []
    lines = text.splitlines()
    for kind, pattern in patterns.items():
        for match in re.finditer(pattern, code):
            line = code.count("\n", 0, match.start()) + 1
            problems.append({"path": path, "line": line, "rule": kind, "text": lines[line - 1].strip()})
    return sorted(problems, key=lambda problem: (problem["path"], problem["line"], problem["rule"]))


def violations(root=ROOT):
    problems = []
    for path in sorted((root / "src").rglob("*.cs")):
        if {"obj", "bin"}.intersection(path.parts):
            continue
        problems.extend(scan_code(path.relative_to(root).as_posix(), path.read_text(encoding="utf-8-sig")))
    return problems


if __name__ == "__main__":
    problems = violations()
    for problem in problems:
        print(f"{problem['path']}:{problem['line']}: {problem['rule']}: {problem['text']}")
    if problems:
        raise SystemExit(1)
    print("Catalog access policy passed: ordinary consumers use shared typed access.")
