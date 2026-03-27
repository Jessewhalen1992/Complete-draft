#!/usr/bin/env python3
import argparse
import json
import math
import os
import sys


def parse_float(value, default=0.0):
    try:
        return float(value)
    except Exception:
        return default


def distance(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def midpoint(a, b):
    return ((a[0] + b[0]) * 0.5, (a[1] + b[1]) * 0.5)


def point_to_segment_distance(point, a, b):
    ax, ay = a
    bx, by = b
    px, py = point
    dx = bx - ax
    dy = by - ay
    if abs(dx) <= 1e-12 and abs(dy) <= 1e-12:
        return distance(point, a)
    t = ((px - ax) * dx + (py - ay) * dy) / ((dx * dx) + (dy * dy))
    t = max(0.0, min(1.0, t))
    closest = (ax + (dx * t), ay + (dy * t))
    return distance(point, closest)


def is_horizontal(a, b):
    return abs(b[0] - a[0]) >= abs(b[1] - a[1])


def is_vertical(a, b):
    return abs(b[1] - a[1]) > abs(b[0] - a[0])


def parse_entities(dxf_path):
    with open(dxf_path, "r", encoding="utf-8", errors="ignore") as handle:
        raw_lines = [line.rstrip("\r\n") for line in handle]

    pairs = []
    for index in range(0, len(raw_lines) - 1, 2):
        pairs.append((raw_lines[index].strip(), raw_lines[index + 1]))

    entities = []
    in_entities = False
    index = 0
    while index < len(pairs):
        code, value = pairs[index]
        if code == "0" and value == "SECTION":
            index += 1
            if index < len(pairs) and pairs[index][0] == "2":
                in_entities = pairs[index][1].strip().upper() == "ENTITIES"
            index += 1
            continue
        if code == "0" and value == "ENDSEC":
            in_entities = False
            index += 1
            continue
        if not in_entities or code != "0":
            index += 1
            continue

        entity_type = value.strip().upper()
        data = {}
        vertices = []
        flags = 0
        index += 1
        while index < len(pairs):
            next_code, next_value = pairs[index]
            if next_code == "0":
                break
            key = next_code.strip()
            val = next_value.strip()
            if entity_type == "LWPOLYLINE":
                if key == "10":
                    vertices.append([parse_float(val), 0.0])
                elif key == "20" and vertices:
                    vertices[-1][1] = parse_float(val)
                elif key == "70":
                    flags = int(parse_float(val, 0.0))
                elif key == "8":
                    data["layer"] = val
            else:
                data[key] = val
            index += 1

        layer = data.get("layer", data.get("8", "")).strip()
        segments = []
        if entity_type == "LINE":
            a = (parse_float(data.get("10")), parse_float(data.get("20")))
            b = (parse_float(data.get("11")), parse_float(data.get("21")))
            segments.append((a, b))
        elif entity_type == "LWPOLYLINE" and len(vertices) >= 2:
            for vertex_index in range(len(vertices) - 1):
                segments.append((tuple(vertices[vertex_index]), tuple(vertices[vertex_index + 1])))
            if flags & 1:
                segments.append((tuple(vertices[-1]), tuple(vertices[0])))

        for segment in segments:
            entities.append(
                {
                    "type": entity_type,
                    "layer": layer,
                    "a": segment[0],
                    "b": segment[1],
                    "mid": midpoint(segment[0], segment[1]),
                }
            )

    return entities


def load_config(path, use_default_blind_midpoints):
    if path:
        with open(path, "r", encoding="utf-8") as handle:
            data = json.load(handle)
    else:
        data = {"checks": []}

    checks = data.get("checks", [])
    if use_default_blind_midpoints and not checks:
        checks = [{"type": "blind_midpoints"}]
    return {"checks": checks}


def run_blind_midpoints(entities, check):
    blind_layers = set(check.get("blind_layers", ["L-USEC"]))
    lsd_layer = check.get("lsd_layer", "L-SECTION-LSD")
    endpoint_tol = float(check.get("endpoint_on_segment_tolerance", 0.75))
    midpoint_tol = float(check.get("midpoint_tolerance", 0.05))
    fail_on_ambiguous = bool(check.get("fail_on_ambiguous", True))

    blind_segments = [
        entity for entity in entities
        if entity["layer"] in blind_layers and is_horizontal(entity["a"], entity["b"])
    ]
    lsd_segments = [
        entity for entity in entities
        if entity["layer"] == lsd_layer and is_vertical(entity["a"], entity["b"])
    ]

    failures = []
    ambiguous = []
    checked = 0

    def find_matches(point):
        matches = []
        for segment in blind_segments:
            seg_distance = point_to_segment_distance(point, segment["a"], segment["b"])
            if seg_distance > endpoint_tol:
                continue
            matches.append(
                {
                    "segment": segment,
                    "distance": seg_distance,
                    "midpoint_distance": distance(point, segment["mid"]),
                }
            )
        matches.sort(key=lambda item: (item["distance"], item["midpoint_distance"]))
        return matches

    for entity in lsd_segments:
        start_matches = find_matches(entity["a"])
        end_matches = find_matches(entity["b"])
        has_start = bool(start_matches)
        has_end = bool(end_matches)
        if has_start and has_end:
            ambiguous.append(
                {
                    "line": entity,
                    "start": entity["a"],
                    "end": entity["b"],
                }
            )
            continue
        if not has_start and not has_end:
            continue

        checked += 1
        endpoint = entity["a"] if has_start else entity["b"]
        match = start_matches[0] if has_start else end_matches[0]
        target = match["segment"]["mid"]
        delta = distance(endpoint, target)
        if delta > midpoint_tol:
            failures.append(
                {
                    "line": entity,
                    "endpoint": endpoint,
                    "target": target,
                    "delta": delta,
                    "blind_segment": match["segment"],
                }
            )

    return {
        "type": "blind_midpoints",
        "checked": checked,
        "failures": failures,
        "ambiguous": ambiguous,
        "passed": not failures and (not ambiguous or not fail_on_ambiguous),
    }


def run_point_match(entities, check):
    layers = set(check.get("layers", [check.get("layer", "L-SECTION-LSD")]))
    tolerance = float(check.get("tolerance", 0.05))
    point_kind = check.get("point_kind", "endpoint")
    expected = tuple(check["point"])

    candidates = []
    for entity in entities:
        if entity["layer"] not in layers:
            continue
        if point_kind == "midpoint":
            candidates.append((entity["mid"], entity))
        else:
            candidates.append((entity["a"], entity))
            candidates.append((entity["b"], entity))

    best = None
    for point, entity in candidates:
        delta = distance(point, expected)
        if best is None or delta < best["delta"]:
            best = {"point": point, "entity": entity, "delta": delta}

    passed = best is not None and best["delta"] <= tolerance
    return {
        "type": "point_match",
        "expected": expected,
        "best": best,
        "passed": passed,
    }


def run_segment_match(entities, check):
    expected_layer = check["layer"]
    endpoint_tolerance = float(check.get("endpoint_tolerance", 0.05))
    expected_a = tuple(check["a"])
    expected_b = tuple(check["b"])

    best = None
    for entity in entities:
        direct = distance(entity["a"], expected_a) + distance(entity["b"], expected_b)
        reverse = distance(entity["a"], expected_b) + distance(entity["b"], expected_a)
        delta = min(direct, reverse)
        if best is None or delta < best["delta"]:
            best = {
                "entity": entity,
                "delta": delta,
            }

    passed = (
        best is not None
        and best["delta"] <= endpoint_tolerance
        and best["entity"]["layer"] == expected_layer
    )
    return {
        "type": "segment_match",
        "expected": {
            "layer": expected_layer,
            "a": expected_a,
            "b": expected_b,
        },
        "best": best,
        "passed": passed,
    }


def run_checks(entities, config):
    results = []
    for check in config.get("checks", []):
        check_type = check.get("type")
        if check_type == "blind_midpoints":
            results.append(run_blind_midpoints(entities, check))
        elif check_type == "point_match":
            results.append(run_point_match(entities, check))
        elif check_type == "segment_match":
            results.append(run_segment_match(entities, check))
        else:
            results.append({"type": check_type or "unknown", "passed": False, "error": "Unsupported check type."})
    return results


def main():
    parser = argparse.ArgumentParser(description="Review ATSBUILD DXF output with geometry checks.")
    parser.add_argument("--dxf", required=True, help="Path to DXF file.")
    parser.add_argument("--config", help="Path to JSON review config.")
    parser.add_argument("--default-blind-midpoints", action="store_true", help="Run the generic blind midpoint check when no config is provided.")
    parser.add_argument("--report", help="Optional JSON report output path.")
    args = parser.parse_args()

    dxf_path = os.path.abspath(args.dxf)
    config = load_config(args.config, args.default_blind_midpoints)
    entities = parse_entities(dxf_path)
    results = run_checks(entities, config)
    passed = all(result.get("passed", False) for result in results) if results else True

    report = {
        "dxf": dxf_path,
        "entity_count": len(entities),
        "passed": passed,
        "results": results,
    }

    if args.report:
        report_path = os.path.abspath(args.report)
        report_dir = os.path.dirname(report_path)
        if report_dir:
            os.makedirs(report_dir, exist_ok=True)
        with open(report_path, "w", encoding="utf-8") as handle:
            json.dump(report, handle, indent=2)

    print(json.dumps(report, indent=2))
    sys.exit(0 if passed else 1)


if __name__ == "__main__":
    main()
