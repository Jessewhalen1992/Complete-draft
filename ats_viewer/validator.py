"""ATS-wide township invariant validator (pre-AutoCAD)."""

from __future__ import annotations

import argparse
import csv
import json
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

from . import cli


@dataclass(frozen=True)
class TownshipKey:
    zone: int
    twp: int
    rge: int
    mer: int

    def label(self) -> str:
        return f"Z{self.zone} TWP {self.twp} RGE {self.rge} W{self.mer}"


@dataclass
class TownshipValidationResult:
    key: TownshipKey
    sections: int
    edges: int
    accepted_pairs: int
    unmatched_edges: int
    unmatched_ratio: float
    sections_with_gt2_unmatched: int
    invalid_polygons: int
    zero_area_polygons: int
    passed: bool
    failures: List[str]

    def to_dict(self) -> Dict[str, object]:
        payload = asdict(self)
        payload["label"] = self.key.label()
        return payload


def _zone_hint_from_arg(zone_raw: str) -> Optional[int]:
    return None if zone_raw == "auto" else int(zone_raw)


def _validate_single_township(
    key: TownshipKey,
    sections: Sequence[cli.SectionRecord],
    args: argparse.Namespace,
) -> TownshipValidationResult:
    invalid_polygons = 0
    zero_area_polygons = 0

    for section in sections:
        poly = cli.make_polygon(section)
        if poly.is_empty or not poly.is_valid:
            invalid_polygons += 1
            continue
        if float(poly.area) <= args.min_polygon_area:
            zero_area_polygons += 1

    edges: List[cli.BoundarySegment] = []
    for section in sections:
        edges.extend(cli.make_segments(section))

    match_args = argparse.Namespace(
        road_width_targets=args.road_width_targets,
        gap_tolerance=args.gap_tolerance,
        angle_tolerance_deg=args.angle_tolerance_deg,
        min_overlap_ratio=args.min_overlap_ratio,
    )
    matched, unmatched, _, _ = cli.match_boundary_edges(edges, match_args)

    unmatched_by_section: Dict[str, int] = {}
    for edge in unmatched:
        sec_key = edge.section_id.key()
        unmatched_by_section[sec_key] = unmatched_by_section.get(sec_key, 0) + 1

    sections_with_gt2_unmatched = sum(1 for count in unmatched_by_section.values() if count > 2)
    unmatched_ratio = (len(unmatched) / len(edges)) if edges else 1.0

    failures: List[str] = []
    if not args.allow_partial_townships and len(sections) != args.expected_sections_per_township:
        failures.append(
            f"INV-T0-001 section_count={len(sections)} expected={args.expected_sections_per_township}"
        )
    if invalid_polygons > 0:
        failures.append(f"INV-T0-002 invalid_polygons={invalid_polygons}")
    if zero_area_polygons > 0:
        failures.append(f"INV-T0-003 zero_area_polygons={zero_area_polygons}")
    if len(edges) == 0:
        failures.append("INV-T1-001 edge_count=0")
    if unmatched_ratio > args.max_unmatched_ratio:
        failures.append(
            f"INV-T1-002 unmatched_ratio={unmatched_ratio:.4f} threshold={args.max_unmatched_ratio:.4f}"
        )
    if sections_with_gt2_unmatched > args.max_sections_with_gt2_unmatched:
        failures.append(
            "INV-T1-003 sections_with_gt2_unmatched="
            f"{sections_with_gt2_unmatched} threshold={args.max_sections_with_gt2_unmatched}"
        )
    if len(matched) == 0:
        failures.append("INV-T1-004 accepted_pairs=0")

    return TownshipValidationResult(
        key=key,
        sections=len(sections),
        edges=len(edges),
        accepted_pairs=len(matched),
        unmatched_edges=len(unmatched),
        unmatched_ratio=unmatched_ratio,
        sections_with_gt2_unmatched=sections_with_gt2_unmatched,
        invalid_polygons=invalid_polygons,
        zero_area_polygons=zero_area_polygons,
        passed=(len(failures) == 0),
        failures=failures,
    )


def _iter_target_townships(args: argparse.Namespace) -> Iterable[Tuple[TownshipKey, List[cli.SectionRecord]]]:
    zone_hint = _zone_hint_from_arg(args.zone)
    if args.township:
        parsed_zone_hint, township = cli.parse_township_request(args.township)
        if parsed_zone_hint is not None:
            zone_hint = parsed_zone_hint
        sections = cli.resolve_township(township.twp, township.rge, township.mer, zone_hint)
        if not sections:
            return
        key = TownshipKey(
            zone=sections[0].sid.zone,
            twp=township.twp,
            rge=township.rge,
            mer=township.mer,
        )
        yield key, list(sections)
        return

    zone_files = cli.discover_zone_files()
    zones = [zone_hint] if zone_hint is not None else sorted(zone_files.keys())
    grouped: Dict[TownshipKey, List[cli.SectionRecord]] = {}
    for zone in zones:
        zone_records = cli.load_zone(zone)
        for section in zone_records.values():
            key = TownshipKey(zone=zone, twp=section.sid.twp, rge=section.sid.rge, mer=section.sid.mer)
            grouped.setdefault(key, []).append(section)

    for key in sorted(grouped.keys(), key=lambda item: (item.zone, item.mer, item.rge, item.twp)):
        yield key, grouped[key]


def _write_outputs(out_dir: Path, args: argparse.Namespace, results: Sequence[TownshipValidationResult]) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)

    passed_count = sum(1 for result in results if result.passed)
    failed = [result for result in results if not result.passed]

    summary = {
        "generated_utc": datetime.now(timezone.utc).isoformat(),
        "configuration": {
            "mode": "township" if args.township else "all-townships",
            "zone": args.zone,
            "road_width_targets": list(args.road_width_targets),
            "gap_tolerance": args.gap_tolerance,
            "angle_tolerance_deg": args.angle_tolerance_deg,
            "min_overlap_ratio": args.min_overlap_ratio,
            "expected_sections_per_township": args.expected_sections_per_township,
            "allow_partial_townships": bool(args.allow_partial_townships),
            "max_unmatched_ratio": args.max_unmatched_ratio,
            "max_sections_with_gt2_unmatched": args.max_sections_with_gt2_unmatched,
            "min_polygon_area": args.min_polygon_area,
        },
        "totals": {
            "townships_checked": len(results),
            "townships_passed": passed_count,
            "townships_failed": len(failed),
        },
        "townships": [result.to_dict() for result in results],
    }
    (out_dir / "validation_summary.json").write_text(
        json.dumps(summary, indent=2),
        encoding="utf-8",
    )

    md_lines = [
        "# ATS Validator Summary",
        "",
        f"- Townships checked: {len(results)}",
        f"- Passed: {passed_count}",
        f"- Failed: {len(failed)}",
        "",
        "## Failed Townships",
    ]
    if not failed:
        md_lines.append("")
        md_lines.append("No failures.")
    else:
        for result in failed:
            md_lines.append("")
            md_lines.append(f"- {result.key.label()}")
            for failure in result.failures:
                md_lines.append(f"  - {failure}")

    (out_dir / "validation_summary.md").write_text("\n".join(md_lines) + "\n", encoding="utf-8")

    with (out_dir / "validation_failures.csv").open("w", newline="", encoding="utf-8") as handle:
        writer = csv.writer(handle)
        writer.writerow(
            [
                "label",
                "zone",
                "twp",
                "rge",
                "mer",
                "sections",
                "edges",
                "accepted_pairs",
                "unmatched_edges",
                "unmatched_ratio",
                "sections_with_gt2_unmatched",
                "invalid_polygons",
                "zero_area_polygons",
                "failures",
            ]
        )
        for result in failed:
            writer.writerow(
                [
                    result.key.label(),
                    result.key.zone,
                    result.key.twp,
                    result.key.rge,
                    result.key.mer,
                    result.sections,
                    result.edges,
                    result.accepted_pairs,
                    result.unmatched_edges,
                    f"{result.unmatched_ratio:.6f}",
                    result.sections_with_gt2_unmatched,
                    result.invalid_polygons,
                    result.zero_area_polygons,
                    " | ".join(result.failures),
                ]
            )


def parse_args(argv: Optional[Sequence[str]] = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run ATS township invariants over JSONL section index data."
    )
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--township", help="Township spec: TWP 57 RGE 18 W5")
    group.add_argument("--all-townships", action="store_true", help="Validate all townships in selected zone(s).")

    parser.add_argument("--zone", default="auto", choices=["11", "12", "auto"])
    parser.add_argument("--out", required=True, help="Output directory for validation summary files.")

    parser.add_argument("--road-width-target", type=float, default=20.11)
    parser.add_argument(
        "--road-width-targets",
        default=None,
        help="Comma-separated targets, e.g. 20.11,30.17 (overrides --road-width-target).",
    )
    parser.add_argument("--gap-tolerance", type=float, default=0.5)
    parser.add_argument("--angle-tolerance-deg", type=float, default=12.0)
    parser.add_argument("--min-overlap-ratio", type=float, default=0.20)

    parser.add_argument("--expected-sections-per-township", type=int, default=36)
    parser.add_argument("--allow-partial-townships", action="store_true")
    parser.add_argument("--max-unmatched-ratio", type=float, default=0.30)
    parser.add_argument("--max-sections-with-gt2-unmatched", type=int, default=0)
    parser.add_argument("--min-polygon-area", type=float, default=1.0)
    return parser.parse_args(argv)


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = parse_args(argv)
    if cli._DEPENDENCY_IMPORT_ERROR is not None:
        raise RuntimeError(
            "Missing runtime dependency for ats_viewer.validator. Install shapely and pyproj in your Python environment."
        ) from cli._DEPENDENCY_IMPORT_ERROR

    args.road_width_targets = cli.parse_target_gaps(
        getattr(args, "road_width_targets", None),
        fallback=args.road_width_target,
    )

    results: List[TownshipValidationResult] = []
    for key, sections in _iter_target_townships(args):
        results.append(_validate_single_township(key, sections, args))

    _write_outputs(Path(args.out), args, results)
    return 0 if all(result.passed for result in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
