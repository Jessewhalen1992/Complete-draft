#!/usr/bin/env python3
"""Record repeatable DXF mismatch metrics for township compare runs."""

from __future__ import annotations

import argparse
from collections import Counter
import csv
import datetime as dt
import importlib.util
import json
from pathlib import Path
from typing import Any, Dict, Iterable, Tuple


TARGET_LAYERS = (
    "L-SEC",
    "L-USEC",
    "L-USEC-0",
    "L-USEC2012",
    "L-USEC3018",
    "L-USEC-C",
    "L-USEC-C-0",
    "L-QSEC",
    "L-QUATER",
    "L-SECTION-LSD",
)
DEFAULT_LAYER_DIFF_SCRIPT = Path(
    "src/AtsBackgroundBuilder/REFERENCE ONLY/dxf_layer_diff.py"
)
DEFAULT_HISTORY_CSV = Path("data/township-compare-history.csv")


def load_module(module_path: Path, module_name: str):
    spec = importlib.util.spec_from_file_location(module_name, module_path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Unable to load module from {module_path}")

    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def counter_delta_totals(before_counter, after_counter) -> Tuple[int, int]:
    missing_total = 0
    added_total = 0

    for serialized, count in before_counter.items():
        delta = count - after_counter.get(serialized, 0)
        if delta > 0:
            missing_total += delta

    for serialized, count in after_counter.items():
        delta = count - before_counter.get(serialized, 0)
        if delta > 0:
            added_total += delta

    return missing_total, added_total


def build_previous_compare(compare_module, before_path: Path, after_path: Path) -> Dict[str, Any]:
    before_fingerprints, before_counts = compare_module.parse_dxf_entities(str(before_path), 6)
    after_fingerprints, after_counts = compare_module.parse_dxf_entities(str(after_path), 6)

    before_counter = compare_module.counter_from_fingerprints(before_fingerprints)
    after_counter = compare_module.counter_from_fingerprints(after_fingerprints)
    missing_total, added_total = counter_delta_totals(before_counter, after_counter)

    return {
        "before": str(before_path),
        "after": str(after_path),
        "passed": missing_total == 0 and added_total == 0,
        "missingCount": missing_total,
        "addedCount": added_total,
        "beforeSupportedEntityCount": sum(before_counter.values()),
        "afterSupportedEntityCount": sum(after_counter.values()),
        "beforeEntityTypeCounts": dict(before_counts),
        "afterEntityTypeCounts": dict(after_counts),
    }


def build_layer_compare(layer_diff_module, actual_path: Path, expected_path: Path) -> Dict[str, Any]:
    precision_summaries: Dict[str, Any] = {}

    for ndigits in (3, 2, 1):
        actual = layer_diff_module.parse_dxf(actual_path, ndigits=ndigits)
        expected = layer_diff_module.parse_dxf(expected_path, ndigits=ndigits)
        rows = layer_diff_module.layer_summary(actual, expected)

        layer_rows = {
            layer: {
                "actual": actual_count,
                "expected": expected_count,
                "extra": extra,
                "missing": missing,
            }
            for layer, actual_count, expected_count, extra, missing in rows
        }

        total_extra = sum(row["extra"] for row in layer_rows.values())
        total_missing = sum(row["missing"] for row in layer_rows.values())

        precision_summaries[f"{ndigits}dp"] = {
            "layers": layer_rows,
            "totalExtra": total_extra,
            "totalMissing": total_missing,
            "mismatchCount": max(total_extra, total_missing),
        }

    actual_3dp = layer_diff_module.parse_dxf(actual_path, ndigits=3)
    expected_3dp = layer_diff_module.parse_dxf(expected_path, ndigits=3)
    detailed_path, rootcause_path = layer_diff_module.write_reports(actual_path, actual_3dp, expected_3dp)

    return {
        "actual": str(actual_path),
        "expected": str(expected_path),
        "precisionSummaries": precision_summaries,
        "detailedReport": str(detailed_path),
        "rootcauseReport": str(rootcause_path),
    }


def build_layer_compare_from_entity_compare(compare_module, actual_path: Path, expected_path: Path) -> Dict[str, Any]:
    precision_summaries: Dict[str, Any] = {}

    for ndigits in (3, 2, 1):
        actual_fingerprints, _ = compare_module.parse_dxf_entities(str(actual_path), ndigits)
        expected_fingerprints, _ = compare_module.parse_dxf_entities(str(expected_path), ndigits)

        actual_counter = compare_module.counter_from_fingerprints(actual_fingerprints)
        expected_counter = compare_module.counter_from_fingerprints(expected_fingerprints)

        layer_rows: Dict[str, Dict[str, int]] = {}
        total_extra = 0
        total_missing = 0

        for layer in TARGET_LAYERS:
            actual_layer_counter = Counter(
                {serialized: count for serialized, count in actual_counter.items() if json.loads(serialized).get("layer") == layer}
            )
            expected_layer_counter = Counter(
                {serialized: count for serialized, count in expected_counter.items() if json.loads(serialized).get("layer") == layer}
            )

            missing_total, added_total = counter_delta_totals(expected_layer_counter, actual_layer_counter)
            layer_rows[layer] = {
                "actual": sum(actual_layer_counter.values()),
                "expected": sum(expected_layer_counter.values()),
                "extra": added_total,
                "missing": missing_total,
            }
            total_extra += added_total
            total_missing += missing_total

        precision_summaries[f"{ndigits}dp"] = {
            "layers": layer_rows,
            "totalExtra": total_extra,
            "totalMissing": total_missing,
            "mismatchCount": max(total_extra, total_missing),
        }

    return {
        "actual": str(actual_path),
        "expected": str(expected_path),
        "precisionSummaries": precision_summaries,
        "detailedReport": None,
        "rootcauseReport": None,
        "fallback": "compare_dxf_entities",
    }


def flatten_history_row(summary: Dict[str, Any]) -> Dict[str, Any]:
    row: Dict[str, Any] = {
        "timestamp": summary["timestamp"],
        "label": summary["label"],
        "scope": summary["scope"],
        "actual": summary["actual"],
        "expected": summary["expected"],
        "previous": summary.get("previous") or "",
    }

    previous = summary.get("vsPrevious")
    if previous:
        row["vs_previous_passed"] = previous["passed"]
        row["vs_previous_added"] = previous["addedCount"]
        row["vs_previous_missing"] = previous["missingCount"]
    else:
        row["vs_previous_passed"] = ""
        row["vs_previous_added"] = ""
        row["vs_previous_missing"] = ""

    precision_summaries = summary["vsExpected"]["precisionSummaries"]
    for precision_key in ("3dp", "2dp", "1dp"):
        precision_summary = precision_summaries[precision_key]
        row[f"expected_total_extra_{precision_key}"] = precision_summary["totalExtra"]
        row[f"expected_total_missing_{precision_key}"] = precision_summary["totalMissing"]
        row[f"expected_mismatch_count_{precision_key}"] = precision_summary["mismatchCount"]

        for layer in TARGET_LAYERS:
            layer_key = layer.lower().replace("-", "_")
            layer_summary = precision_summary["layers"][layer]
            row[f"{layer_key}_extra_{precision_key}"] = layer_summary["extra"]
            row[f"{layer_key}_missing_{precision_key}"] = layer_summary["missing"]

    return row


def append_csv_row(path: Path, row: Dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    file_exists = path.exists()

    with path.open("a", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(row.keys()))
        if not file_exists:
            writer.writeheader()
        writer.writerow(row)


def main() -> int:
    parser = argparse.ArgumentParser(description="Track township DXF mismatch metrics over time.")
    parser.add_argument("--label", required=True, help="Short run label, for example rerun181.")
    parser.add_argument("--scope", required=True, help="Scope identifier, for example 43-8-5.")
    parser.add_argument("--actual", required=True, help="Path to the newly generated DXF.")
    parser.add_argument("--expected", required=True, help="Path to the corrected reference DXF.")
    parser.add_argument("--previous", help="Optional path to the last approved/generated DXF.")
    parser.add_argument(
        "--history-csv",
        default=str(DEFAULT_HISTORY_CSV),
        help="CSV file that should receive one appended metrics row per run.",
    )
    parser.add_argument(
        "--summary-json",
        help="Optional JSON path to write the full metrics summary for this run.",
    )
    parser.add_argument(
        "--layer-diff-script",
        default=str(DEFAULT_LAYER_DIFF_SCRIPT),
        help="Path to the layer-diff helper script.",
    )
    args = parser.parse_args()

    repo_root = Path(__file__).resolve().parent.parent
    compare_script_path = repo_root / "scripts" / "compare_dxf_entities.py"
    layer_diff_script_path = (repo_root / args.layer_diff_script).resolve()
    actual_path = (repo_root / args.actual).resolve()
    expected_path = (repo_root / args.expected).resolve()
    previous_path = (repo_root / args.previous).resolve() if args.previous else None
    history_csv_path = (repo_root / args.history_csv).resolve()
    summary_json_path = (repo_root / args.summary_json).resolve() if args.summary_json else None

    compare_module = load_module(compare_script_path, "compare_dxf_entities_module")
    layer_diff_module = None
    if layer_diff_script_path.exists():
        layer_diff_module = load_module(layer_diff_script_path, "dxf_layer_diff_module")

    summary: Dict[str, Any] = {
        "timestamp": dt.datetime.now().astimezone().isoformat(timespec="seconds"),
        "label": args.label,
        "scope": args.scope,
        "actual": str(actual_path),
        "expected": str(expected_path),
        "previous": str(previous_path) if previous_path else None,
        "vsExpected": (
            build_layer_compare(layer_diff_module, actual_path, expected_path)
            if layer_diff_module is not None
            else build_layer_compare_from_entity_compare(compare_module, actual_path, expected_path)
        ),
    }

    if previous_path:
        summary["vsPrevious"] = build_previous_compare(compare_module, previous_path, actual_path)

    history_row = flatten_history_row(summary)
    append_csv_row(history_csv_path, history_row)
    summary["historyCsv"] = str(history_csv_path)

    if summary_json_path:
        summary_json_path.parent.mkdir(parents=True, exist_ok=True)
        summary_json_path.write_text(json.dumps(summary, indent=2), encoding="utf-8")
        summary["summaryJson"] = str(summary_json_path)

    print(json.dumps(summary, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
