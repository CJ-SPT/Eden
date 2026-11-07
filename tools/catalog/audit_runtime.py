"""Inventory remaining catalog reads and content decisions for staged migration."""
import argparse
import json
from pathlib import Path
import re


PATTERNS = {
    "catalog-read": re.compile(r"\b(?:catalog|cat)\.(?:Set<|FindSource\(|FittingType\(|FittingAttributes\()"),
    "catalog-context": re.compile(r"\bCatalogDbContext\b"),
    "content-identifier": re.compile(r"\benum \w*(?:Type|Skill)\b|\bconst int \w*(?:Type|Hull|Group)\w*\s*="),
    "type-selection": re.compile(r"\b(?:typeId|TypeId)\s+switch|\b(?:IsHull|IsUtility|SupportsHull|BlueprintForHull)\("),
}


def audit(root):
    entries = []
    for path in sorted((root / "src").rglob("*.cs")):
        if "obj" in path.parts or "bin" in path.parts or path.name.endswith(".g.cs"):
            continue
        for number, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
            for kind, pattern in PATTERNS.items():
                if pattern.search(line):
                    entries.append({"path": path.relative_to(root).as_posix(), "line": number,
                                    "kind": kind, "text": line.strip()})
    return {"build": 3662168, "purpose": "Migration inventory; identifiers are not automatically removal candidates.",
            "counts": {kind: sum(e["kind"] == kind for e in entries) for kind in PATTERNS}, "entries": entries}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    args.output.write_text(json.dumps(audit(root), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
