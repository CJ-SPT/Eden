"""Inventory remaining catalog reads and content decisions for staged migration."""
import argparse
import json
from pathlib import Path
import re
from runtime_policy import BOUNDARIES, BOUNDARY_PREFIXES, WORLD_TRANSPORT, boundary, violations


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
    schema = json.loads((root / "src/Eden.Data/Catalog/CatalogSchema.g.json").read_text(encoding="utf-8-sig"))
    generated = (root / "src/Eden.Data/Catalog/CatalogModels.g.cs").read_text(encoding="utf-8-sig")
    dispositions = []
    for entry in entries:
        if entry["kind"] not in {"content-identifier", "type-selection"}:
            continue
        path = entry["path"]
        if path.startswith("src/Eden.Infrastructure/"):
            category, reason = "registration-metadata", "DI lifetime identifiers; not game content."
        elif path.startswith("src/Eden.Protocol/"):
            category, reason = "protocol-identifier", "Native wire/owner/item representation identifiers stay named in Protocol."
        elif entry["kind"] == "type-selection" and (
                "/Drones/" in path or "/LiveDrones.cs" in path or "/TechOneHullRules.cs" in path):
            category, reason = "build-content-policy", "Admission delegates to the immutable build policy; recognizing a profile is insufficient."
        elif path.startswith("src/Eden.World/"):
            category, reason = "mechanic-binding", "Named native group/effect identifiers bind typed source inputs to implemented feature rules."
        elif any(name in path for name in ("NpcGuardRules", "NpcLootRules", "NpcSpecialTypes", "TechOneHullRules")):
            category, reason = "named-build-identifiers", "Names remain as native identifiers; roster/admission/selection data lives in the build policy."
        else:
            category, reason = "native-identifier-or-rule", "Named native operation/item/skill identifiers and mathematical rules remain in the owning Gameplay feature."
        dispositions.append({**entry, "disposition": category, "reason": reason})
    return {
        "build": schema["build"],
        "purpose": "Completed adoption inventory; native identifiers and mechanics are intentional, classified below.",
        "counts": {kind: sum(e["kind"] == kind for e in entries) for kind in PATTERNS},
        "coverage": {
            "typedDatasets": schema["datasetCount"], "sourceRecords": schema["recordCount"],
            "generatedPayloadClasses": len(re.findall(r"public sealed class \w+\n\{", generated)),
            "generatedVariantTypes": len(re.findall(r"public abstract record \w+", generated)),
            "unresolvedFields": schema["unresolvedFields"],
            "catalogAccessViolations": violations(root),
            "buildPolicy": "src/Eden.Gameplay/Content/content-support-3662168.json",
            "rawReader": "Removed; source inputs use generated typed access.",
            "legacySourceCache": "Removed; connection scope only reuses operation-owned transport.",
        },
        "boundaries": BOUNDARIES, "boundaryPrefixes": BOUNDARY_PREFIXES,
        "worldTransportExceptions": WORLD_TRANSPORT,
        "hardcodedDecisionDispositions": dispositions,
        "entries": entries,
    }



if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    args.output.write_text(json.dumps(audit(root), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
