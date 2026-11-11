"""Summarize retained coarse-simulation replays without combining different workloads."""

import argparse
import json
import math
from pathlib import Path
import re


def milliseconds(value: str) -> float:
    match = re.fullmatch(r"(?:(\d+)\.)?(\d+):(\d+):(\d+(?:\.\d+)?)", value)
    if match is None:
        raise ValueError(f"Invalid TimeSpan: {value}")
    days, hours, minutes, seconds = match.groups()
    return (int(days or 0) * 86400 + int(hours) * 3600 + int(minutes) * 60 + float(seconds)) * 1000


def percentile(values: list[float], fraction: float) -> float | None:
    ordered = sorted(values)
    return None if not ordered else ordered[max(0, math.ceil(len(ordered) * fraction) - 1)]


def summarize(path: Path) -> dict:
    replay = json.loads(path.read_text(encoding="utf-8-sig"))
    warmed = [request for request in replay["Requests"] if request["ArrivalSecond"] > replay["WarmupSeconds"]]
    operations = {}
    for name in ("frame", "GetRegionBest"):
        requests = [request for request in warmed if request["Result"]["Operation"] == name]
        values = [request["Total"] for request in requests]
        operations[name] = {
            "samples": len(values),
            "p95Ms": percentile(values, 0.95),
            "p99Ms": percentile(values, 0.99),
            "maximumMs": max(values, default=None),
            "zeroSqlConnectionFitSamples": sum(
                all(request["Result"][key] == 0 for key in ("Sql", "Connections", "Fits")) for request in requests
            ),
        }
    traffic = [
        timing for timing in replay["Ownership"] if "traffic" in timing["Operation"] or "population" in timing["Operation"]
    ]
    holds = [milliseconds(timing["Hold"]) for timing in traffic]
    runtimes = [sample.get("Runtime", {}) for sample in replay["Samples"]]
    return {
        "source": path.name,
        "systems": replay["systems"],
        "observers": replay.get("observers", 1),
        "backlog": replay["backlog"],
        "measuredSeconds": replay["seconds"],
        "setupMs": replay["SetupMilliseconds"],
        "operations": operations,
        "trafficHoldsAtLeast250Ms": sum(value >= 250 for value in holds),
        "maximumTrafficHoldMs": max(holds, default=0),
        "maximumRemoteAgeSeconds": max((runtime.get("RemoteAgeTicks", 0) / 10_000_000 for runtime in runtimes), default=0),
        "finalRuntime": runtimes[-1] if runtimes else {},
        "cpuMs": replay.get("CpuMilliseconds"),
        "allocatedBytes": replay.get("AllocatedBytes"),
        "checkpointMaximumMs": {
            stage: max((milliseconds(checkpoint[stage]) for checkpoint in replay["Checkpoints"]), default=0)
            for stage in ("Capture", "Copy", "Projection", "Write")
        },
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    options = parser.parse_args()
    paths = sorted(options.directory.glob("throughput-*.json"))
    paths = [path for path in paths if re.fullmatch(r"throughput-\d+-(True|False)-\d+\.json", path.name)]
    summaries = [summarize(path) for path in paths]
    target = options.directory / "summary.json"
    target.write_text(json.dumps(summaries, indent=2) + "\n", encoding="utf-8")
    for summary in summaries:
        print(json.dumps({key: summary[key] for key in ("source", "operations", "trafficHoldsAtLeast250Ms")}))


if __name__ == "__main__":
    main()
