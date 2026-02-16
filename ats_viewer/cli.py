"""Alberta ATS section viewer command line tool."""

from __future__ import annotations

import argparse
import json
import math
import re
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

try:
    from shapely.geometry import LineString, mapping, Polygon
    from shapely.ops import transform
    from pyproj import Transformer
except Exception as exc:  # pragma: no cover - surfaced with clear runtime message
    LineString = None  # type: ignore
    Polygon = None  # type: ignore
    Transformer = None  # type: ignore
    transform = None  # type: ignore
    mapping = None  # type: ignore
    _DEPENDENCY_IMPORT_ERROR = exc
else:
    _DEPENDENCY_IMPORT_ERROR = None


TARGET_ZONE_CRS = {11: 26911, 12: 26912}
DISPLAY_CRS = "EPSG:4326"


@dataclass(frozen=True)
class SectionID:
    zone: int
    sec: int
    twp: int
    rge: int
    mer: int

    def key(self) -> str:
        return f"{self.sec}-{self.twp}-{self.rge}-W{self.mer}"

    def label(self) -> str:
        return self.key()


@dataclass
class SectionRecord:
    sid: SectionID
    verts: List[Tuple[float, float]]


@dataclass
class BoundarySegment:
    section_id: SectionID
    segment_id: str
    edge_index: int
    line: LineString
    bearing: float
    axis: Tuple[float, float]

    @property
    def zone(self) -> int:
        return self.section_id.zone


@dataclass
class EdgeMatch:
    edge_a: BoundarySegment
    edge_b: BoundarySegment
    gap: float
    target_gap: float
    overlap: float
    overlap_ratio: float
    angle_diff: float
    score: float
    accepted: bool
    centerline: Optional[LineString]


_SECTION_RE = re.compile(
    r"^\s*(\d+)\s*[-\s]\s*(\d+)\s*[-\s]\s*(\d+)\s*(?:-?\s*[Ww]?\s*)?(\d+)\s*$"
)
_TOWNSHIP_RE = re.compile(
    r"(?i)\bTWP\s+(\d+)\s+RGE\s+(\d+)\s+(?:W|W\s+)?\s*(\d+)\b"
)
_ZONE_RE = re.compile(r"(?i)\bZ\s*(\d{1,2})\b")


def _float_pair(value: object) -> Tuple[float, float]:
    x, y = value
    return float(x), float(y)


def _to_int(raw: object) -> int:
    return int(str(raw).strip().replace(",", ""))


def _normalize_meridian(raw: str) -> int:
    return _to_int(str(raw).strip().upper().replace("W", "").replace("MER", "").replace("M", ""))


def _extract_zone_hint(text: str) -> Tuple[Optional[int], str]:
    m = _ZONE_RE.search(text)
    if not m:
        return None, text
    zone = _to_int(m.group(1))
    cleaned = _ZONE_RE.sub(" ", text, count=1).strip()
    return zone, cleaned


def parse_sections_request(raw: str) -> Tuple[Optional[int], List[SectionID]]:
    zone_hint = None
    sections: List[SectionID] = []
    for piece in raw.split(","):
        token = piece.strip()
        if not token:
            continue
        token_zone, token = _extract_zone_hint(token)
        if token_zone is not None:
            if zone_hint is not None and zone_hint != token_zone:
                raise ValueError("Conflicting zone hints in section list.")
            zone_hint = token_zone
        token = token.upper()
        m = _SECTION_RE.match(token)
        if not m:
            raise ValueError(f"Cannot parse section token: {piece!r}")
        sec, twp, rge, mer = m.groups()
        mer_v = _normalize_meridian(mer)
        section = SectionID(0, _to_int(sec), _to_int(twp), _to_int(rge), mer_v)
        if section.sec <= 0 or section.twp <= 0 or section.rge <= 0 or section.mer <= 0:
            raise ValueError(f"Invalid numeric section in token {piece!r}")
        sections.append(section)
    if not sections:
        raise ValueError("No section specification parsed.")
    return zone_hint, sections


def parse_township_request(raw: str) -> Tuple[Optional[int], SectionID]:
    token_zone, cleaned = _extract_zone_hint(raw)
    cleaned = cleaned.upper()
    m = _TOWNSHIP_RE.search(cleaned)
    if not m:
        raise ValueError(f"Cannot parse township token: {raw!r}")
    twp, rge, mer = m.groups()
    sec_id = SectionID(0, 0, _to_int(twp), _to_int(rge), _normalize_meridian(mer))
    if token_zone and token_zone not in TARGET_ZONE_CRS:
        raise ValueError(f"Unsupported zone: {token_zone}")
    return token_zone, sec_id


def parse_target_gaps(value: Optional[str], fallback: float) -> List[float]:
    if value is None:
        return [fallback]
    raw_values = [part.strip() for part in str(value).split(",") if part.strip()]
    if not raw_values:
        raise ValueError("No valid values provided for --road-width-targets.")
    targets: List[float] = []
    for raw in raw_values:
        try:
            target = float(raw)
        except Exception as exc:
            raise ValueError(f"Invalid road-width target: {raw!r}") from exc
        if target <= 0:
            raise ValueError(f"Road-width target must be > 0: {raw!r}")
        targets.append(target)
    return targets


def project_root() -> Path:
    return Path(__file__).resolve().parents[1]


def discover_zone_files() -> Dict[int, Path]:
    roots = [
        project_root() / "data",
        project_root() / "src" / "AtsBackgroundBuilder" / "REFERENCE ONLY",
    ]
    zones: Dict[int, Path] = {}
    for root in roots:
        if not root.exists():
            continue
        for path in root.glob("Master_Sections.index_Z*.jsonl"):
            m = re.search(r"_Z(\d+)\.jsonl$", path.name, re.IGNORECASE)
            if not m:
                continue
            zone = _to_int(m.group(1))
            if zone in zones:
                # prefer `data/` over reference if both exist
                if str(path.parent).endswith("data"):
                    zones[zone] = path
                continue
            zones[zone] = path
    return zones


_ZONE_FILE_CACHE: Dict[int, Path] = discover_zone_files()
_JSONL_CACHE: Dict[int, Dict[str, SectionRecord]] = {}


def load_zone(zone: int) -> Dict[str, SectionRecord]:
    if zone in _JSONL_CACHE:
        return _JSONL_CACHE[zone]
    if zone not in _ZONE_FILE_CACHE:
        raise RuntimeError(f"No index jsonl for zone {zone}.")
    path = _ZONE_FILE_CACHE[zone]
    sections: Dict[str, SectionRecord] = {}
    with path.open("r", encoding="utf-8") as f:
        for line in f:
            if not line.strip():
                continue
            row = json.loads(line)
            raw_zone = row.get("ZONE")
            if raw_zone is None:
                continue
            try:
                row_zone = _to_int(raw_zone)
            except Exception:
                continue
            if row_zone != zone:
                continue
            try:
                sec = _to_int(row["SEC"])
                twp = _to_int(row["TWP"])
                rge = _to_int(row["RGE"])
                mer = _normalize_meridian(row["MER"])
                verts_raw = row.get("Verts")
            except Exception:
                continue
            if not isinstance(verts_raw, list) or len(verts_raw) < 3:
                continue
            try:
                verts = [_float_pair(v) for v in verts_raw]
            except Exception:
                continue
            if len(verts) < 3:
                continue
            sid = SectionID(zone, sec, twp, rge, mer)
            sections[sid.key()] = SectionRecord(sid=sid, verts=verts)
    if not sections:
        raise RuntimeError(f"No usable sections loaded for zone {zone}.")
    _JSONL_CACHE[zone] = sections
    return sections


def resolve_sections(
    sections: Sequence[SectionID],
    zone_hint: Optional[int],
) -> List[SectionRecord]:
    if zone_hint:
        if zone_hint not in TARGET_ZONE_CRS:
            raise ValueError(f"Unsupported zone {zone_hint}.")
        selected_zones = [zone_hint]
    else:
        selected_zones = sorted(_ZONE_FILE_CACHE.keys())
        if not selected_zones:
            raise RuntimeError("No zone index files found.")

    selected: List[SectionRecord] = []
    for req in sections:
        req = SectionID(zone_hint or 0, req.sec, req.twp, req.rge, req.mer)
        candidates: List[SectionRecord] = []
        for z in selected_zones:
            zone_sections = load_zone(z)
            rec = zone_sections.get(req.key())
            if rec:
                candidates.append(rec)
        if not candidates:
            raise RuntimeError(
                f"Section {req.key()} not found in selected zone(s): {', '.join(map(str, selected_zones))}"
            )
        if zone_hint is None and len(candidates) > 1:
            zones = ", ".join(str(c.sid.zone) for c in candidates)
            raise RuntimeError(
                f"Section {req.key()} exists in {zones}. Add --zone 11 or --zone 12."
            )
        selected.append(candidates[0])
    return selected


def resolve_township(twp: int, rge: int, mer: int, zone_hint: Optional[int]) -> List[SectionRecord]:
    if zone_hint and zone_hint not in TARGET_ZONE_CRS:
        raise ValueError(f"Unsupported zone {zone_hint}.")
    zones = [zone_hint] if zone_hint else sorted(_ZONE_FILE_CACHE.keys())
    records: List[SectionRecord] = []
    seen: Dict[str, List[SectionRecord]] = defaultdict(list)
    for zone in zones:
        zset = load_zone(zone)
        for rec in zset.values():
            if rec.sid.twp == twp and rec.sid.rge == rge and rec.sid.mer == mer:
                seen[rec.sid.key()].append(rec)
    for key, recs in seen.items():
        if len(recs) > 1:
            raise RuntimeError(
                f"Township request includes section key {key} in both zones. Add --zone 11 or --zone 12."
            )
        if recs:
            records.append(recs[0])
    if not records:
        raise RuntimeError(
            f"No sections found for township TWP {twp} RGE {rge} W{mer} in selected zone(s)."
        )
    return records


def make_polygon(section: SectionRecord) -> Polygon:
    poly = Polygon(section.verts)
    if not poly.is_valid:
        poly = poly.buffer(0)
    return poly


def make_segments(section: SectionRecord) -> List[BoundarySegment]:
    poly = make_polygon(section)
    ring = list(poly.exterior.coords)
    if len(ring) < 4:
        return []
    segments: List[BoundarySegment] = []
    for i in range(len(ring) - 1):
        a = ring[i]
        b = ring[i + 1]
        if a == b:
            continue
        line = LineString([a, b])
        if line.length < 1e-6:
            continue
        dx = float(b[0] - a[0])
        dy = float(b[1] - a[1])
        l = math.hypot(dx, dy)
        axis = (dx / l, dy / l)
        bearing = math.atan2(axis[1], axis[0])
        if bearing < 0:
            bearing += math.pi
        if bearing >= math.pi:
            bearing -= math.pi
        sid = section.sid
        seg_id = f"{sid.key()}::{sid.zone}::{i}"
        segments.append(
            BoundarySegment(
                section_id=sid,
                segment_id=seg_id,
                edge_index=i,
                line=line,
                bearing=float(bearing),
                axis=axis,
            )
        )
    return segments


def _projection_t(line: LineString, axis: Tuple[float, float]) -> Tuple[float, float]:
    x0, y0 = line.coords[0]
    x1, y1 = line.coords[1]
    t0 = x0 * axis[0] + y0 * axis[1]
    t1 = x1 * axis[0] + y1 * axis[1]
    return (min(t0, t1), max(t0, t1))


def _point_on_line_by_t(line: LineString, axis: Tuple[float, float], t: float) -> Tuple[float, float]:
    (x0, y0), (x1, y1) = line.coords
    t0 = x0 * axis[0] + y0 * axis[1]
    t1 = x1 * axis[0] + y1 * axis[1]
    denom = t1 - t0
    if abs(denom) < 1e-9:
        return ((x0 + x1) / 2.0, (y0 + y1) / 2.0)
    f = (t - t0) / denom
    f = max(0.0, min(1.0, f))
    return (x0 + f * (x1 - x0), y0 + f * (y1 - y0))


def _segment_overlap_and_centerline(
    edge_a: BoundarySegment,
    edge_b: BoundarySegment,
    min_overlap: float,
) -> Tuple[float, float, Optional[LineString]]:
    t0a, t1a = _projection_t(edge_a.line, edge_a.axis)
    t0b, t1b = _projection_t(edge_b.line, edge_a.axis)
    overlap_min = max(t0a, t0b)
    overlap_max = min(t1a, t1b)
    overlap = overlap_max - overlap_min
    if overlap <= 0.0:
        return 0.0, 0.0, None
    if overlap < min_overlap:
        return overlap, overlap / min(edge_a.line.length, edge_b.line.length), None
    a0 = _point_on_line_by_t(edge_a.line, edge_a.axis, overlap_min)
    a1 = _point_on_line_by_t(edge_a.line, edge_a.axis, overlap_max)
    b0 = _point_on_line_by_t(edge_b.line, edge_a.axis, overlap_min)
    b1 = _point_on_line_by_t(edge_b.line, edge_a.axis, overlap_max)
    center_0 = ((a0[0] + b0[0]) / 2.0, (a0[1] + b0[1]) / 2.0)
    center_1 = ((a1[0] + b1[0]) / 2.0, (a1[1] + b1[1]) / 2.0)
    return (
        overlap,
        overlap / min(edge_a.line.length, edge_b.line.length),
        LineString([center_0, center_1]),
    )


def evaluate_pair(
    edge_a: BoundarySegment,
    edge_b: BoundarySegment,
    target_gaps: Sequence[float],
    gap_tolerance: float,
    angle_tolerance: float,
    min_overlap_ratio: float,
) -> Optional[EdgeMatch]:
    if edge_a.section_id.key() == edge_b.section_id.key():
        return None
    gap = edge_a.line.distance(edge_b.line)
    if not target_gaps:
        return None

    max_target = max(target_gaps)
    if gap > max_target + gap_tolerance:
        return None

    best_target = None
    best_gap_score = -1.0
    for target_gap in target_gaps:
        if gap > target_gap + gap_tolerance:
            continue
        gap_score = 1.0 - min(abs(gap - target_gap) / gap_tolerance, 1.0)
        if gap_score > best_gap_score:
            best_gap_score = gap_score
            best_target = float(target_gap)

    if best_target is None:
        return None

    angle_diff = abs(edge_a.bearing - edge_b.bearing)
    if angle_diff > math.pi:
        angle_diff = abs(angle_diff - math.pi)

    angle_tol_rad = math.radians(angle_tolerance)
    if angle_diff > angle_tol_rad:
        # still record near candidates for debug if gap is close enough
        angle_score = 0.0
    else:
        angle_score = 1.0 - (angle_diff / angle_tol_rad)

    overlap, overlap_ratio, centerline = _segment_overlap_and_centerline(
        edge_a, edge_b, min_overlap=0.0
    )
    overlap_score = min(1.0, overlap_ratio / max(min_overlap_ratio, 1e-9))
    score = 0.45 * best_gap_score + 0.35 * angle_score + 0.2 * overlap_score
    accepted = (
        gap <= best_target + gap_tolerance
        and angle_diff <= angle_tol_rad
        and overlap_ratio >= min_overlap_ratio
    )
    if accepted and centerline is None:
        accepted = False
    return EdgeMatch(
        edge_a=edge_a,
        edge_b=edge_b,
        gap=gap,
        target_gap=best_target,
        overlap=overlap,
        overlap_ratio=overlap_ratio,
        angle_diff=math.degrees(angle_diff),
        score=float(score),
        accepted=accepted,
        centerline=centerline,
    )


def match_boundary_edges(edges: Sequence[BoundarySegment], args: argparse.Namespace):
    all_matches: List[EdgeMatch] = []
    near_pairs_by_edge: Dict[str, List[EdgeMatch]] = defaultdict(list)
    for i in range(len(edges)):
        e1 = edges[i]
        for j in range(i + 1, len(edges)):
            e2 = edges[j]
            if e1.zone != e2.zone:
                continue
            match = evaluate_pair(
                e1,
                e2,
                target_gaps=args.road_width_targets,
                gap_tolerance=args.gap_tolerance,
                angle_tolerance=args.angle_tolerance_deg,
                min_overlap_ratio=args.min_overlap_ratio,
            )
            if not match:
                continue
            all_matches.append(match)
            near_pairs_by_edge[match.edge_a.segment_id].append(match)
            near_pairs_by_edge[match.edge_b.segment_id].append(match)

    # Deduplicate centrelines: greedy one-to-one matching of best-scoring candidates
    selected_matches: List[EdgeMatch] = []
    used_edges = set()
    for match in sorted(all_matches, key=lambda m: m.score, reverse=True):
        a = match.edge_a.segment_id
        b = match.edge_b.segment_id
        if a in used_edges or b in used_edges:
            continue
        if not match.accepted:
            continue
        used_edges.add(a)
        used_edges.add(b)
        selected_matches.append(match)

    unmatched: List[BoundarySegment] = [
        e for e in edges if e.segment_id not in used_edges
    ]
    return selected_matches, unmatched, all_matches, near_pairs_by_edge


def make_transformer(zone: int):
    crs_in = f"EPSG:{TARGET_ZONE_CRS[zone]}"
    return Transformer.from_crs(crs_in, DISPLAY_CRS, always_xy=True)


def to_wgs84(geom, zone: int):
    tf = make_transformer(zone)
    return transform(tf.transform, geom)


def feature_from_geometry(geom, properties: Dict[str, object]):
    return {"type": "Feature", "geometry": mapping(geom), "properties": properties}


def write_geojson(path: Path, features: Iterable[Dict[str, object]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = {"type": "FeatureCollection", "features": list(features)}
    with path.open("w", encoding="utf-8") as f:
        json.dump(payload, f, indent=2)


def polygon_features(sections: Sequence[SectionRecord], crs_zone_first=True):
    for section in sections:
        poly = make_polygon(section)
        geom = to_wgs84(poly, section.sid.zone)
        yield feature_from_geometry(
            geom,
            {
                "zone": section.sid.zone,
                "sec": section.sid.sec,
                "twp": section.sid.twp,
                "rge": section.sid.rge,
                "meridian": section.sid.mer,
                "section_key": section.sid.key(),
            },
        )


def label_features(sections: Sequence[SectionRecord]):
    for section in sections:
        geom = to_wgs84(make_polygon(section).representative_point(), section.sid.zone)
        yield feature_from_geometry(
            geom,
            {
                "zone": section.sid.zone,
                "sec": section.sid.sec,
                "twp": section.sid.twp,
                "rge": section.sid.rge,
                "meridian": section.sid.mer,
                "section_key": section.sid.key(),
            },
        )


def centerline_features(matches: Sequence[EdgeMatch], merge: bool = False):
    centerlines = []
    for match in matches:
        if match.centerline is None:
            continue
        geom = to_wgs84(match.centerline, match.edge_a.zone)
        centerlines.append(
            feature_from_geometry(
                geom,
                {
                    "zone": match.edge_a.zone,
                    "section_a": match.edge_a.section_id.key(),
                    "section_b": match.edge_b.section_id.key(),
                    "sec_a": match.edge_a.section_id.sec,
                    "sec_b": match.edge_b.section_id.sec,
                    "gap": round(match.gap, 3),
                    "target_gap": round(match.target_gap, 3),
                    "angle_deg": round(match.angle_diff, 3),
                    "overlap_ratio": round(match.overlap_ratio, 5),
                    "score": round(match.score, 5),
                },
            )
        )
    return centerlines


def edge_features(edges: Sequence[BoundarySegment], unmatched: Optional[Sequence[BoundarySegment]] = None, near_pairs: Optional[Dict[str, List[EdgeMatch]]] = None):
    unmatched_ids = {e.segment_id for e in unmatched} if unmatched is not None else set()
    for edge in edges:
        geom = to_wgs84(edge.line, edge.zone)
        best = None
        if near_pairs is not None:
            pairs = near_pairs.get(edge.segment_id, [])
            if pairs:
                top = max(pairs, key=lambda m: m.score)
                best = {
                    "best_target_gap": round(top.target_gap, 3),
                    "best_gap": round(top.gap, 3),
                    "best_overlap_ratio": round(top.overlap_ratio, 5),
                    "best_angle_deg": round(top.angle_diff, 3),
                    "best_score": round(top.score, 5),
                    "has_candidate": bool(pairs),
                }
        status = "unmatched" if edge.segment_id in unmatched_ids else "paired"
        props = {
            "zone": edge.zone,
            "section_key": edge.section_id.key(),
            "sec": edge.section_id.sec,
            "twp": edge.section_id.twp,
            "rge": edge.section_id.rge,
            "meridian": edge.section_id.mer,
            "edge_index": edge.edge_index,
            "status": status,
            "bearing_deg": round(math.degrees(edge.bearing), 4),
            "length": round(edge.line.length, 4),
        }
        if best:
            props.update(best)
        yield feature_from_geometry(geom, props)


def candidate_features(matches: Sequence[EdgeMatch], max_pairs: int):
    # Export high-value candidate alignments for manual inspection.
    for match in sorted(matches, key=lambda m: m.score, reverse=True)[:max_pairs]:
        if match.centerline is None:
            # simple fallback line between nearest points
            a = match.edge_a.line.interpolate(0.5, normalized=True)
            b = match.edge_b.line.interpolate(0.5, normalized=True)
            geom = LineString([(a.x, a.y), (b.x, b.y)])
        else:
            geom = match.centerline
        wgs = to_wgs84(geom, match.edge_a.zone)
        yield feature_from_geometry(
            wgs,
                {
                    "zone": match.edge_a.zone,
                    "section_a": match.edge_a.section_id.key(),
                    "section_b": match.edge_b.section_id.key(),
                    "accepted": match.accepted,
                    "target_gap": round(match.target_gap, 3),
                    "gap": round(match.gap, 3),
                    "angle_deg": round(match.angle_diff, 3),
                    "overlap_ratio": round(match.overlap_ratio, 5),
                    "score": round(match.score, 5),
                },
        )


def write_summary(
    path: Path,
    sections: Sequence[SectionRecord],
    edges: Sequence[BoundarySegment],
    matched: Sequence[EdgeMatch],
    unmatched: Sequence[BoundarySegment],
    candidates: Sequence[EdgeMatch],
    road_width_targets: Sequence[float],
):
    matched_count = len(matched)
    unmatched_count = len(unmatched)
    zone_list = ", ".join(str(z) for z in sorted(set(s.sid.zone for s in sections))) or "n/a"
    targets_text = ", ".join(f"{t:.3f}" for t in sorted(set(road_width_targets)))
    lines = [
        "ATS Viewer Debug Summary",
        f"Sections requested: {len(sections)}",
        f"Zones represented: {zone_list}",
        f"Road-width targets: {targets_text}",
        f"Boundary edges: {len(edges)}",
        f"Candidate edge pairs within tolerance: {len(candidates)}",
        f"Accepted pairs: {matched_count}",
        f"Unmatched edges: {unmatched_count}",
        "",
        "Top unmatched candidates by score:",
    ]
    for edge in unmatched[:20]:
        lines.append(f"- {edge.section_id.key()} edge {edge.edge_index} (zone {edge.zone})")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def build_preview(out_dir: Path, sections: Sequence[SectionRecord], centrelines: Sequence[EdgeMatch], unmatched: Sequence[BoundarySegment]) -> None:
    try:
        import matplotlib.pyplot as plt
    except Exception:
        return

    from shapely.geometry import mapping

    fig, ax = plt.subplots(figsize=(10, 10))
    for section in sections:
        poly = to_wgs84(make_polygon(section), section.sid.zone)
        xs, ys = poly.exterior.xy
        ax.plot(xs, ys, color="#2E86AB", alpha=0.6, linewidth=1.0)
        c = poly.centroid
        ax.scatter([c.x], [c.y], s=10, color="#2E86AB")
    for match in centrelines:
        if not match.centerline:
            continue
        ln = to_wgs84(match.centerline, match.edge_a.zone)
        xs, ys = ln.xy
        ax.plot(xs, ys, color="#1B9E77", linewidth=1.8)
    for edge in unmatched:
        ln = to_wgs84(edge.line, edge.zone)
        xs, ys = ln.xy
        ax.plot(xs, ys, color="#D62828", linewidth=1.0, linestyle=":")
    ax.set_aspect("equal", adjustable="box")
    ax.axis("off")
    fig.tight_layout()
    out_dir.mkdir(parents=True, exist_ok=True)
    fig.savefig(out_dir / "preview.png", dpi=180)
    plt.close(fig)


def parse_args(argv: Optional[Sequence[str]] = None):
    parser = argparse.ArgumentParser(
        description="Build Alberta ATS polygons and inferred section road-allowance centrelines."
    )
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--sections", help="Comma-separated section keys, e.g. 11-1-1-W5,14-1-1-W5")
    group.add_argument("--township", help="Township spec: TWP 1 RGE 1 W5")

    parser.add_argument("--zone", default="auto", choices=["11", "12", "auto"])
    parser.add_argument("--out", required=True, help="Output directory")
    parser.add_argument("--road-width-target", type=float, default=20.11)
    parser.add_argument(
        "--road-width-targets",
        default=None,
        help="Comma-separated targets, e.g. 20.11,30.17 (overrides --road-width-target).",
    )
    parser.add_argument("--gap-tolerance", type=float, default=0.5)
    parser.add_argument("--angle-tolerance-deg", type=float, default=12.0)
    parser.add_argument("--min-overlap-ratio", type=float, default=0.20)
    parser.add_argument("--no-preview", action="store_true")
    parser.add_argument("--debug", action="store_true", help="Export edge-matching diagnostics.")
    parser.add_argument("--max-debug-pairs", type=int, default=2000)
    return parser.parse_args(argv)


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = parse_args(argv)
    if _DEPENDENCY_IMPORT_ERROR is not None:
        raise RuntimeError(
            "Missing runtime dependency for ats_viewer. Install shapely and pyproj in your Python environment."
        ) from _DEPENDENCY_IMPORT_ERROR

    zone_hint = None if args.zone == "auto" else int(args.zone)

    if args.sections:
        request_zone_hint, secs = parse_sections_request(args.sections)
        if request_zone_hint is not None:
            zone_hint = request_zone_hint
        if not secs:
            raise RuntimeError("No section specs parsed.")
        sections = resolve_sections(secs, zone_hint)
    else:
        twp_zone_hint, township = parse_township_request(args.township)
        if twp_zone_hint is not None:
            zone_hint = twp_zone_hint
        sections = resolve_township(township.twp, township.rge, township.mer, zone_hint)

    road_width_targets = parse_target_gaps(
        getattr(args, "road_width_targets", None),
        fallback=args.road_width_target,
    )
    # Keep backward compatible single-value attribute for downstream consumers.
    args.road_width_targets = road_width_targets

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    sec_features = list(polygon_features(sections))
    label_feats = list(label_features(sections))
    edges = []
    for section in sections:
        edges.extend(make_segments(section))

    matched, unmatched, candidates, near_pairs = match_boundary_edges(edges, args)

    write_geojson(out_dir / "sections.geojson", sec_features)
    write_geojson(out_dir / "centrelines.geojson", centerline_features(matched))
    write_geojson(out_dir / "labels.geojson", label_feats)

    if args.debug:
        write_geojson(out_dir / "unmatched_edges.geojson", edge_features(edges, unmatched, near_pairs))
        write_geojson(out_dir / "section_edges.geojson", edge_features(edges, [], near_pairs))
        write_geojson(out_dir / "edge_pairs_debug.geojson", candidate_features(candidates, args.max_debug_pairs))
        write_summary(
            out_dir / "debug_summary.txt",
            sections=sections,
            edges=edges,
            matched=matched,
            unmatched=unmatched,
            candidates=candidates,
            road_width_targets=road_width_targets,
        )

    if not args.no_preview:
        build_preview(out_dir, sections, matched, unmatched)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())

