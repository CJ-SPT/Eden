"""Compare decoded catalog records with a reference build using Python 3.11+."""

import argparse
from contextlib import closing
from decimal import Decimal, InvalidOperation
from itertools import zip_longest
import json
from pathlib import Path
import sqlite3
import sys


NEGATIVE_INTEGER_ZERO = object()


def read_integer(value):
    return NEGATIVE_INTEGER_ZERO if value == "-0" else int(value)


def reject_constant(value):
    raise ValueError(f"{value} is not a JSON number")


def decode(value):
    return json.loads(value, parse_int=read_integer, parse_float=Decimal, parse_constant=reject_constant)


def values_match(candidate, reference):
    if type(candidate) is not type(reference):
        return False
    if isinstance(candidate, dict):
        return candidate.keys() == reference.keys() and all(
            values_match(value, reference[key]) for key, value in candidate.items()
        )
    if isinstance(candidate, list):
        return len(candidate) == len(reference) and all(
            values_match(left, right) for left, right in zip(candidate, reference)
        )
    if isinstance(candidate, Decimal) and candidate.is_zero() and reference.is_zero():
        return candidate.is_signed() == reference.is_signed()
    return candidate == reference


def open_catalog(path):
    return sqlite3.connect(path.resolve(strict=True).as_uri() + "?mode=ro", uri=True)


def select_datasets(candidate, reference, requested):
    available_candidate = {name for name, in candidate.execute("SELECT Name FROM SourceDataset")}
    available_reference = {name for name, in reference.execute("SELECT Name FROM SourceDataset")}
    selected = set(requested) if requested else available_candidate | available_reference
    if not selected:
        raise ValueError("Neither catalog contains source datasets")
    for label, available in (("candidate", available_candidate), ("reference", available_reference)):
        missing = selected - available
        if missing:
            raise ValueError(f"Missing datasets in {label} catalog: {', '.join(sorted(missing))}")
    return sorted(selected)


def compare_catalogs(candidate_path, reference_path, datasets=()):
    """Return the compared record and dataset counts, or raise at the first mismatch."""
    query = "SELECT Key, Data FROM SourceRecord WHERE Dataset = ? ORDER BY Key"
    compared = 0
    with closing(open_catalog(candidate_path)) as candidate, closing(open_catalog(reference_path)) as reference:
        selected = select_datasets(candidate, reference, datasets)
        for dataset in selected:
            pairs = zip_longest(candidate.execute(query, (dataset,)), reference.execute(query, (dataset,)))
            for candidate_record, reference_record in pairs:
                candidate_key, candidate_json = candidate_record if candidate_record is not None else (None, None)
                reference_key, reference_json = reference_record if reference_record is not None else (None, None)
                if candidate_key != reference_key:
                    raise ValueError(f"{dataset} keys differ (candidate {candidate_key!r}, reference {reference_key!r})")
                if candidate_json != reference_json:
                    try:
                        match = values_match(decode(candidate_json), decode(reference_json))
                    except (ValueError, InvalidOperation) as error:
                        raise ValueError(f"{dataset} key {candidate_key!r} contains invalid JSON: {error}") from error
                    if not match:
                        raise ValueError(f"{dataset} key {candidate_key!r} has a different decoded value")
                compared += 1
    return compared, len(selected)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("candidate", type=Path, help="Catalog with newly decoded records")
    parser.add_argument("reference", type=Path, help="Known-good catalog")
    parser.add_argument("--dataset", action="append", default=[], help="Compare only this exact dataset name. Repeat to select more.")
    args = parser.parse_args()
    try:
        records, datasets = compare_catalogs(args.candidate, args.reference, args.dataset)
    except (OSError, sqlite3.Error, ValueError) as error:
        print(f"Catalog value comparison failed: {error}", file=sys.stderr)
        return 1
    print(f"Catalog values match for {records:,} records across {datasets:,} datasets.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
