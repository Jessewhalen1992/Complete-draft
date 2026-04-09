#!/usr/bin/env python3
import argparse
import collections
import json
import os
import sys


SUPPORTED_TYPES = {
    "LINE",
    "LWPOLYLINE",
    "MTEXT",
    "DIMENSION",
    "INSERT",
    "ATTRIB",
}

FLOAT_CODES = {
    "10", "20", "30",
    "11", "21", "31",
    "12", "22", "32",
    "13", "23", "33",
    "14", "24", "34",
    "15", "25", "35",
    "16", "26", "36",
    "38", "39",
    "40", "41", "42", "43", "44", "45",
    "50", "51", "52", "53",
}

INT_CODES = {"66", "70", "71", "72", "73", "74"}

GENERIC_ALLOWED_CODES = {
    "LINE": {"8", "10", "20", "30", "11", "21", "31"},
    "MTEXT": {"8", "10", "20", "30", "40", "41", "42", "43", "44", "50", "71", "72", "1", "3"},
    "DIMENSION": {
        "8", "1", "10", "20", "30", "11", "21", "31",
        "13", "23", "33", "14", "24", "34",
        "15", "25", "35", "16", "26", "36",
        "50", "51", "52", "53", "70",
    },
    "INSERT": {"8", "2", "10", "20", "30", "41", "42", "43", "44", "45", "50", "66"},
    "ATTRIB": {"8", "2", "1", "10", "20", "30", "40", "50", "70", "73", "74"},
}


def parse_float(value):
    try:
        return float(value)
    except Exception:
        return 0.0


def normalize_value(code, value, digits):
    if code in FLOAT_CODES:
        return round(parse_float(value), digits)
    if code in INT_CODES:
        return int(round(parse_float(value)))
    return value.strip()


def canonicalize_point(point):
    return tuple(point)


def canonicalize_segment(a, b):
    point_a = canonicalize_point(a)
    point_b = canonicalize_point(b)
    return (point_a, point_b) if point_a <= point_b else (point_b, point_a)


def canonicalize_open_path(points):
    forward = tuple(points)
    reverse = tuple(reversed(points))
    return min(forward, reverse)


def rotate_path(points, start_index):
    return points[start_index:] + points[:start_index]


def canonicalize_closed_path(points):
    if not points:
        return tuple()

    candidates = []
    for ordered in (list(points), list(reversed(points))):
        for start_index in range(len(ordered)):
            candidates.append(tuple(rotate_path(ordered, start_index)))

    return min(candidates)


def collect_pairs(raw_lines):
    return [(raw_lines[index].strip(), raw_lines[index + 1].rstrip("\r\n")) for index in range(0, len(raw_lines) - 1, 2)]


def build_lwpolyline_fingerprint(entity_type, pairs, digits):
    layer = ""
    vertices = []
    flags = 0

    for code, value in pairs:
        if code == "8":
            layer = value.strip()
        elif code == "10":
            vertices.append([round(parse_float(value), digits), 0.0])
        elif code == "20" and vertices:
            vertices[-1][1] = round(parse_float(value), digits)
        elif code == "70":
            flags = int(round(parse_float(value)))

    point_tuples = [tuple(vertex) for vertex in vertices]
    closed = (flags & 1) == 1
    if closed:
        points = canonicalize_closed_path(point_tuples)
    else:
        points = canonicalize_open_path(point_tuples)

    return {
        "type": entity_type,
        "layer": layer,
        "closed": closed,
        "points": points,
    }


def build_line_fingerprint(entity_type, pairs, digits):
    layer = ""
    start = [0.0, 0.0, 0.0]
    end = [0.0, 0.0, 0.0]

    for code, value in pairs:
        if code == "8":
            layer = value.strip()
        elif code == "10":
            start[0] = round(parse_float(value), digits)
        elif code == "20":
            start[1] = round(parse_float(value), digits)
        elif code == "30":
            start[2] = round(parse_float(value), digits)
        elif code == "11":
            end[0] = round(parse_float(value), digits)
        elif code == "21":
            end[1] = round(parse_float(value), digits)
        elif code == "31":
            end[2] = round(parse_float(value), digits)

    segment = canonicalize_segment(start, end)
    return {
        "type": entity_type,
        "layer": layer,
        "segment": segment,
    }


def build_generic_fingerprint(entity_type, pairs, digits):
    allowed_codes = GENERIC_ALLOWED_CODES[entity_type]
    text_parts = []
    normalized_pairs = []

    for code, value in pairs:
        if code not in allowed_codes:
            continue

        if entity_type == "MTEXT" and code in {"1", "3"}:
            text_parts.append(value)
            continue

        normalized_pairs.append((code, normalize_value(code, value, digits)))

    if entity_type == "MTEXT":
        normalized_pairs.append(("text", "".join(text_parts)))

    return {
        "type": entity_type,
        "pairs": tuple(normalized_pairs),
    }


def build_fingerprint(entity_type, pairs, digits):
    if entity_type == "LWPOLYLINE":
        return build_lwpolyline_fingerprint(entity_type, pairs, digits)
    if entity_type == "LINE":
        return build_line_fingerprint(entity_type, pairs, digits)
    return build_generic_fingerprint(entity_type, pairs, digits)


def parse_dxf_entities(path, digits):
    with open(path, "r", encoding="utf-8", errors="ignore") as handle:
        raw_lines = [line.rstrip("\r\n") for line in handle]

    pairs = collect_pairs(raw_lines)
    in_entities = False
    index = 0
    fingerprints = []
    counts = collections.Counter()

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
        entity_pairs = []
        index += 1
        while index < len(pairs):
            next_code, next_value = pairs[index]
            if next_code == "0":
                break
            entity_pairs.append((next_code.strip(), next_value))
            index += 1

        counts[entity_type] += 1
        if entity_type in SUPPORTED_TYPES:
            fingerprints.append(build_fingerprint(entity_type, entity_pairs, digits))

    return fingerprints, counts


def counter_from_fingerprints(fingerprints):
    counter = collections.Counter()
    for fingerprint in fingerprints:
        serialized = json.dumps(fingerprint, sort_keys=True, separators=(",", ":"))
        counter[serialized] += 1
    return counter


def summarize_diff(before_counter, after_counter, limit):
    missing = []
    added = []

    for serialized, count in before_counter.items():
        delta = count - after_counter.get(serialized, 0)
        if delta > 0:
            missing.append({"count": delta, "entity": json.loads(serialized)})

    for serialized, count in after_counter.items():
        delta = count - before_counter.get(serialized, 0)
        if delta > 0:
            added.append({"count": delta, "entity": json.loads(serialized)})

    missing.sort(key=lambda item: json.dumps(item["entity"], sort_keys=True))
    added.sort(key=lambda item: json.dumps(item["entity"], sort_keys=True))

    return missing[:limit], added[:limit]


def main():
    parser = argparse.ArgumentParser(description="Compare normalized DXF entity fingerprints.")
    parser.add_argument("--before", required=True, help="Path to the baseline DXF.")
    parser.add_argument("--after", required=True, help="Path to the candidate DXF.")
    parser.add_argument("--precision", type=int, default=6, help="Decimal places to keep for numeric normalization.")
    parser.add_argument("--sample-limit", type=int, default=20, help="Number of added/missing sample fingerprints to emit.")
    args = parser.parse_args()

    before_path = os.path.abspath(args.before)
    after_path = os.path.abspath(args.after)

    before_fingerprints, before_counts = parse_dxf_entities(before_path, args.precision)
    after_fingerprints, after_counts = parse_dxf_entities(after_path, args.precision)

    before_counter = counter_from_fingerprints(before_fingerprints)
    after_counter = counter_from_fingerprints(after_fingerprints)
    missing, added = summarize_diff(before_counter, after_counter, args.sample_limit)

    passed = not missing and not added
    report = {
        "before": before_path,
        "after": after_path,
        "supportedTypes": sorted(SUPPORTED_TYPES),
        "beforeSupportedEntityCount": sum(before_counter.values()),
        "afterSupportedEntityCount": sum(after_counter.values()),
        "beforeEntityTypeCounts": dict(before_counts),
        "afterEntityTypeCounts": dict(after_counts),
        "passed": passed,
        "missingSample": missing,
        "addedSample": added,
    }

    print(json.dumps(report, indent=2))
    sys.exit(0 if passed else 1)


if __name__ == "__main__":
    main()
