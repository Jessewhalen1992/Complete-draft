# ATS-Wide Section Validation (Pre-AutoCAD)

Status date: 2026-03-02

This document defines the headless validation workflow used to detect section-building risk before AutoCAD execution.

Primary rules reference:
- `docs/specs/sections/SECTION_RULES.md`
- `docs/specs/road-allowance/ROAD_ALLOWANCE_GOLDEN_RULES.md`

## Goal

Run deterministic checks over JSONL index geometry at township scale (or ATS-wide batch) so regressions are found by automation instead of manual map inspection.

## Scope

Current scope (implemented now):
- Township-level geometry sanity from JSONL.
- Township edge-pairing stability using the same `ats_viewer` boundary matcher.
- Batch pass/fail summaries suitable for CI/nightly gates.

Out of scope (future phase):
- Full 1:1 headless execution of AutoCAD plugin linework generation (`L-SEC`, `L-USEC`, `L-QSEC`, `L-SECTION-LSD`) without AutoCAD runtime APIs.

## Validation Tiers

1. Tier 0: Input geometry sanity
2. Tier 1: Township boundary-network pairing checks
3. Tier 2: AutoCAD parity checks against exported `cad_lines.geojson` (when available)
4. Tier 3: Pure C# rule-engine parity (future extraction from AutoCAD-coupled code)

## Invariants

Invariant IDs below are intended to be stable so they can be referenced in bug reports and CI output.

### Tier 0 (Input Geometry)

- `INV-T0-001`: Section count per township is expected (`36` unless partial-township mode is enabled).
- `INV-T0-002`: Section polygons must be non-empty and valid after standard repair (`buffer(0)` fallback).
- `INV-T0-003`: Section polygons must have positive area above minimum floor.

### Tier 1 (Network Pairing)

- `INV-T1-001`: Township edge set must not be empty.
- `INV-T1-002`: Unmatched-edge ratio must stay below configured threshold.
- `INV-T1-003`: Count of sections with more than two unmatched edges must stay below configured threshold.
- `INV-T1-004`: At least one accepted boundary pair must exist in a township run.

## Recommended Gating Defaults

- `expected_sections_per_township = 36`
- `allow_partial_townships = false`
- `max_unmatched_ratio = 0.30`
- `max_sections_with_gt2_unmatched = 0`
- `min_polygon_area = 1.0`

These defaults are conservative enough to catch obvious geometry/rule drift while avoiding overfitting to one township.

## CLI Runner

Use `python -m ats_viewer.validator` to run these invariants.

Outputs:
- `validation_summary.json` (machine-readable)
- `validation_summary.md` (human summary)
- `validation_failures.csv` (spreadsheet-friendly failure rows)

## Operational Workflow

1. Run township-level validation during feature development.
2. Run all-townships validation before merge/release.
3. For failures, inspect township-level debug output and rule deltas.
4. Add targeted regression tests/rule fixes, then rerun full batch.

## Future Parity Plan

To validate `L-QUATER` / `L-QSEC` landing and endpoint behavior with full confidence, extract geometry rule logic into a pure engine shared by:
- AutoCAD plugin adapter (runtime side effects)
- Headless validator adapter (ATS-wide batch simulation)

Until that extraction is complete, Tier 0/1 invariants provide early warning but are not a complete substitute for runtime parity checks.
