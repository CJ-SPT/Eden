"""Import factual UniWiki Sleeper rosters and bind names to the pinned catalog.

No reference server implementation is used. HTML evidence is cached under artifacts.
"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import html
import json
from pathlib import Path
import re
import sqlite3
import urllib.parse
import urllib.request

ANOMALIES = [
    ["Perimeter Ambush Point", "Perimeter Camp", "Phase Catalyst Node", "The Line"],
    ["Perimeter Checkpoint", "Perimeter Hangar", "The Ruins of Enclave Cohort 27", "Sleeper Data Sanctuary"],
    ["Fortification Frontier Stronghold", "Outpost Frontier Stronghold", "Solar Cell", "The Oruze Construct"],
    ["Frontier Barracks", "Frontier Command Post", "Integrated Terminus", "Sleeper Information Sanctum"],
    ["Core Garrison", "Core Stronghold", "Oruze Osobnyk", "Quarantine Area"],
    ["Core Citadel", "Core Bastion", "Strange Energy Readings", "The Mirror"],
]
DATA = [
    ["Unsecured Perimeter Amplifier", "Unsecured Perimeter Information Center"],
    ["Unsecured Perimeter Comms Relay", "Unsecured Perimeter Transponder Farm"],
    ["Unsecured Frontier Database", "Unsecured Frontier Receiver"],
    ["Unsecured Frontier Digital Nexus", "Unsecured Frontier Trinary Hub"],
    ["Unsecured Frontier Enclave Relay", "Unsecured Frontier Server Bank"],
    ["Unsecured Core Backup Array", "Unsecured Core Emergence"],
]
RELIC = [
    ["Forgotten Perimeter Coronation Platform", "Forgotten Perimeter Power Array"],
    ["Forgotten Perimeter Gateway", "Forgotten Perimeter Habitation Coils"],
    ["Forgotten Frontier Quarantine Outpost", "Forgotten Frontier Recursive Depot"],
    ["Forgotten Frontier Conversion Module", "Forgotten Frontier Evacuation Center"],
    ["Forgotten Core Data Field", "Forgotten Core Information Pen"],
    ["Forgotten Core Assembly Hall", "Forgotten Core Circuitry Disassembler"],
]
GAS = [
    ("Barren Perimeter Reservoir", 1), ("Minor Perimeter Reservoir", 1),
    ("Ordinary Perimeter Reservoir", 1), ("Sizeable Perimeter Reservoir", 1),
    ("Token Perimeter Reservoir", 1), ("Bountiful Frontier Reservoir", 3),
    ("Vast Frontier Reservoir", 3), ("Instrumental Core Reservoir", 5), ("Vital Core Reservoir", 5),
]


def text(raw):
    return " ".join(html.unescape(re.sub(r"<[^>]+>", " ", raw)).split())


def main(args):
    jobs = [(name, level + 1, kind) for kind, classes in enumerate([ANOMALIES, DATA, RELIC])
            for level, names in enumerate(classes) for name in names]
    jobs.extend((name, level, 3) for name, level in GAS)
    args.cache.mkdir(parents=True, exist_ok=True)

    def fetch(job):
        name, level, kind = job
        path = args.cache / (name.replace(" ", "_") + ".html")
        url = "https://wiki.eveuniversity.org/" + urllib.parse.quote(name.replace(" ", "_"))
        if not path.exists():
            request = urllib.request.Request(url, headers={"User-Agent": "Eden-content-research/1.0"})
            path.write_bytes(urllib.request.urlopen(request, timeout=45).read())
        return job, url, path.read_text(encoding="utf-8")

    with sqlite3.connect(args.catalog.resolve().as_uri() + "?mode=ro", uri=True) as db:
        types = {}
        for tid, name, category in db.execute("SELECT t.Id,t.Name,g.CategoryId FROM ItemType t "
                                            "JOIN ItemGroup g ON g.Id=t.GroupId ORDER BY t.Id"):
            if category == 11:
                types.setdefault(name, tid)
        localization = {key: json.loads(raw).get("text", "").strip() for key, raw in db.execute(
            "SELECT Key,Data FROM SourceRecord WHERE Dataset='client/localization_fsd_en-us'")}
        dungeons = {}
        for key, raw in db.execute("SELECT Key,Data FROM SourceRecord WHERE Dataset='client/dungeons'"):
            dungeon = json.loads(raw)
            name = localization.get(str(dungeon.get("dungeonNameID")), "")
            if dungeon.get("entryTypeID") in [28356, 19728] and len(dungeon.get("rooms", {})) == 1:
                dungeons.setdefault(name, []).append(int(key))
        encounters, failures = [], []
        with ThreadPoolExecutor(max_workers=4) as pool:
            for (name, level, kind), url, raw in pool.map(fetch, jobs):
                try:
                    tables = re.findall(r"<table\b.*?</table>", raw, re.S | re.I)
                    table = next(t for t in tables if "Sleeper Ship Name" in text(t) and "Wave" in text(t))
                    waves = []
                    unknown = []
                    next_trigger = 0
                    for row in re.findall(r"<tr\b[^>]*>(.*?)</tr>", table, re.S | re.I):
                        cells = re.findall(r"<t[dh]\b[^>]*>(.*?)</t[dh]>", row, re.S | re.I)
                        line = text(row)
                        if re.search(r"capital|escalation|drifter", line, re.I):
                            break
                        if re.match(r"Wave \d", line):
                            trigger = 3 if "cans have been hacked or failed" in line else next_trigger
                            waves.append(dict(heading=line, groups=[], trigger=trigger))
                            next_trigger = 0
                            continue
                        if not waves:
                            continue
                        candidate = next((text(cell) for cell in cells
                                          if re.match(r"^\d+(?:x?\s*[-/–]\s*\d+)?\s*[x×]\s*", text(cell))), None)
                        if candidate is None:
                            continue
                        match = re.match(r"^(\d+)(?:x?\s*[-/–]\s*(\d+))?\s*[x×]\s*(.+)", candidate)
                        minimum, maximum, npc = match.groups()
                        optional = "Possible additional spawn" in npc or "May not always appear" in npc
                        npc = re.sub(r"\s*\((?:Possible additional spawn|May not always appear|Trigger to finish site)\)", "", npc)
                        npc = npc.replace(" Trigger to complete site", "").strip()
                        if "Next wave is triggered by hacking" in npc:
                            next_trigger = 1
                        elif "Next wave is triggered by failing a hack" in npc:
                            next_trigger = 2
                        condition = 2 if "on aggression" in npc else 1 if "Talocan relic can" in npc else 0
                        matches = [tid for type_name, tid in types.items()
                                   if npc == type_name or npc.startswith(type_name + " (") or npc == type_name + " on aggression"]
                        if not matches:
                            unknown.append(npc)
                            continue
                        waves[-1]["groups"].append(dict(minimum=int(minimum), maximum=int(maximum or minimum),
                            types=matches, trigger="TRIGGER for next wave" in row, optional=optional,
                            sentry="Sentry Tower" in npc, spawnCondition=condition))
                    if unknown or not waves or any(not wave["groups"] for wave in waves):
                        raise ValueError("Unknown NPCs or empty waves: " + repr(unknown))
                    if name not in dungeons:
                        raise ValueError("No matching pinned native dungeon")
                    revisions = re.findall(r"oldid=(\d+)", raw)
                    encounters.append(dict(name=name, source=url, revision=int(revisions[-1]), locations="C" + str(level),
                        variants=[waves], reconstructed=False, dungeonVariants={str(k): 0 for k in sorted(dungeons[name])},
                        wormholeClass=level, wormholeKind=kind))
                    print("OK", name, len(waves), sum(g["maximum"] for w in waves for g in w["groups"]))
                except (ValueError, StopIteration, IndexError) as error:
                    failures.append(dict(name=name, error=str(error)))
                    print("BLOCKED", name, str(error))
        args.output.write_text(json.dumps(dict(build=3662168, encounters=encounters, blocked=failures),
                                         indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print("Admitted", len(encounters), "Blocked", len(failures))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cache", type=Path, default=Path("artifacts/wormhole-research"))
    main(parser.parse_args())
