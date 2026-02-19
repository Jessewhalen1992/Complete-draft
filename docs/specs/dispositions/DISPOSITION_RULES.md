# Disposition Rules (Authoritative v1, Implementation Snapshot)

Status date: 2026-02-19

These rules are authoritative for ATS background generation. If a code path contradicts them, stop and revisit the implementation.

Primary canonical references:
- `docs/specs/sections/SECTION_RULES.md` (Authoritative v2)
- `docs/specs/road-allowance/ROAD_ALLOWANCE_GOLDEN_RULES.md`

## Shared Definitions

1. Hard section boundaries: `L-SEC`, `L-USEC-0`, `L-USEC-2012` (alias `L-USEC2012`), and correction-zero (`L-USEC-C-0`).
2. 30.18 boundaries: `L-USEC-3018` (alias `L-USEC3018`).
3. Base road allowance offsets are 20.11 m (`L-SEC`) and 30.16 m (`L-USEC`).
4. Scaled working bands may be represented as 20.12 / 30.18.

## Input And Precondition Rules

1. Disposition shapefiles are imported when either `IncludeDispositionLinework` or `IncludeDispositionLabels` is enabled.
2. Label processing only uses imported entities that can be cloned to closed boundary polylines.
3. Object data is read per disposition entity; if OD is missing, the entity is skipped for disposition labeling logic and counted as `SkippedNoOd`.
4. Lookup mapping uses `CompanyLookup.xlsx` and `PurposeLookup.xlsx`; when lookup keys miss, raw OD values are used as fallback.
5. `CurrentClient` is required for build completion and client/foreign layer classification.

## Layer Mapping Rules

1. Purpose suffix comes from `PurposeLookup` entry `Extra`; leading `-` is removed.
2. If a valid suffix exists, line/text layers are derived as:
`C-<suffix>` / `C-<suffix>-T` when current client matches mapped company or raw company,
`F-<suffix>` / `F-<suffix>-T` otherwise.
3. If no suffix mapping exists, existing imported layer names remain in use.
4. Imported disposition linework is moved to mapped line layer and forced to `ByLayer` color (`ColorIndex=256`).
5. Label layers are created on demand before label placement.

## Label Content Rules

1. `DISP_NUM` formatting: values matching `^[A-Z]{3}\\d+` are rendered as `<AAA> <digits>`; non-matching values are left unchanged.
2. Non-width label content format is:
`<MappedCompany>\\P<MappedPurpose>\\P<DispNumFormatted>`.
3. Width-required purposes are determined by normalized match against `Config.WidthRequiredPurposeCodes`.
4. Width labels compute corridor width per quarter-intersection piece and snap display width to nearest `Config.AcceptableRowWidths`.
5. If width is variable after tolerance checks, label content format is:
`<MappedCompany>\\PVariable Width\\P<PurposeTitleCase>\\P<DispNumFormatted>`, color green (`ColorIndex=3`).
6. If variable width can be resolved by OD `DIMENSION` fallback to an acceptable snapped width, fixed-width label text is used but color remains green (`ColorIndex=3`).
7. Fixed-width label content format is:
`<MappedCompany>\\P<SnappedWidth:0.00> <MappedPurpose>\\P<DispNumFormatted>`.
8. Wellsite/surface augmentation: when purpose is `WELL SITE`/`WELLSITE` (normalized) or mapped purpose contains `(Surface)`, a dominant LSD/section token is prepended to mapped purpose when derivable.

## Placement And Entity-Type Rules

1. Labeling is quarter-scoped: targets are derived from disposition/quarter intersection geometry, not global disposition safe points.
2. Multi-quarter behavior is currently forced on (`AllowMultiQuarterDispositions=true`) by build logic.
3. Width-required labels use leader target refinement toward corridor midpoints and avoid targets inside other disposition polygons when possible.
4. Candidate label points are generated as quarter-local spiral samples with in-shape and leader-length constraints.
5. Overlap scoring considers existing label extents, nearby label crowding, and disposition linework intersection; best non-overlap is preferred.
6. If no non-overlap candidate is found and `PlaceWhenOverlapFails=true`, the lowest-score fallback candidate is forced.
7. Label entity selection is:
`AlignedDimension` when `UseAlignedDimensions=true` and `RequiresWidth=true`,
`MLeader` when leaders are enabled and disposition `AddLeader=true`,
`MText` otherwise.
8. If `IncludeDispositionLabels=false`, disposition labels are not created.

## PLSR Check Rules

1. PLSR check runs only when `CheckPlsr=true` and disposition labels are enabled.
2. Labels are collected from `MText`, `MLeader`, and `AlignedDimension`; owner is parsed from first content line and disposition number from last line.
3. Label-to-quarter assignment is by label anchor point-in-quarter containment.
4. Comparison scope is filtered to supported disposition prefixes:
`LOC, PLA, MSL, MLL, DML, EZE, PIL, RME, RML, DLO, ROE, RRD, DPI, DPL, VCE, DRS, SML, SME`.
5. Checks report missing labels, owner mismatches, extra labels not in PLSR, and requested quarters missing from XML.
6. Expired activities are tagged by appending `(Expired)` to label content when expiry date is older than report date.
7. PLSR summary is written to `PLSR_Check.txt`.

## Cleanup And Output Rules

1. If `IncludeDispositionLinework=false`, imported disposition linework is erased after label placement.
2. Labels remain when disposition linework is removed.
3. Build summary reports total dispositions, labels placed, skipped counts, overlap-forced counts, multi-quarter counts, and import stats.

## Program Order Of Operation (Disposition)

1. Build section/quarter geometry from requested sections.
2. Import disposition shapefiles (if linework or labels are requested).
3. Read OD and lookups, map layers, and build disposition label payloads.
4. Clone quarter polygons for label placement scope.
5. Place disposition labels per quarter intersection.
6. Optionally run PLSR validation and apply expired markers.
7. Run build cleanup, including optional erasure of disposition linework.
