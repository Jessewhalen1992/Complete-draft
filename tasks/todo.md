# Correction-Line Context Shift Fix (2026-02-18)

- [x] Confirm current forced correction-context mapping and mismatch with expected Section 6/1 behavior.
- [x] Update forced correction-context rules for above/below correction-line range-shift cases.
- [x] Build `AtsBackgroundBuilder` Release and verify the change compiles cleanly.

## Review

- Updated forced context mapping to match correction-line shift cases:
  - Above line: `6 -> 35 (twp-1, range+1)`, `1 -> 36 (twp-1, same range)`.
  - Below line (opposite): `35 -> 6 (twp+1, range-1)`, `36 -> 1 (twp+1, same range)`.
- `dotnet build -c Release src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj` succeeded with 0 warnings and 0 errors.

## Follow-up (2026-02-18)

- [x] Add missing correction opposite-seam context pair for `58-18-5` case (`32 -> 5` and symmetric `5 -> 32`).
- [x] Rebuild Release to confirm compile safety after follow-up adjustment.
- [x] Extend `32 <-> 5` mapping to include both range variants so `58-19-5` can include `5-59-18-5`.
- [x] Isolate forced correction-context sections from correction-line seam/LSD scope to avoid side effects.

## Follow-up (LSD Midpoints)

- [x] Prioritize exact regular-boundary (`L-SEC`/`L-USEC`/0/20) midpoint targets for vertical LSD endpoints before quarter/component midpoint fallbacks.
- [x] Rebuild Release to confirm compile safety after midpoint-priority adjustment.

## Feature (Quaterview)

- [x] Add user toggle `Quaterview` (YES/NO) to control quarter-boundary visualization.
- [x] Draw persistent quarter polygons on new orange layer `L-QUATER` when toggle is enabled.
- [x] Rebuild Release to verify compile safety after feature addition.

## Rule-Matrix Reset (LSD, 2026-02-25)

- [x] Add a fresh deterministic LSD endpoint rule-matrix pass based on section/quarter rules and correction override.
- [x] Wire all final LSD enforcement call-sites to pass quarter/section info into the rule-matrix pass.
- [x] Build `AtsBackgroundBuilder` and run a Python rule-matrix verification script for section-group mapping.

## Review (Rule-Matrix Reset, 2026-02-25)

- Implemented `TryEnforceLsdLineEndpointsByRuleMatrix(...)` and made `EnforceLsdLineEndpointsOnHardSectionBoundaries(...)` call it first when quarter info is available.
- Rule matrix implemented:
  - Inner endpoints: snapped to deterministic 1/4 anchors (midpoint between section-center and QSEC endpoint per quarter side).
  - Horizontal outer endpoints: west half -> `L-USEC-2012`, east half -> `L-USEC-0`, with `L-SEC` fallback.
  - Vertical outer endpoints:
    - Group A sections `1-6, 13-18, 25-30`: south -> `2012`, north -> `L-USEC` (blind-line layer), with `L-SEC` fallback.
    - Group B sections `7-12, 19-24, 31-36`: south -> `L-USEC` (blind-line layer), north -> `0`, with `L-SEC` fallback.
  - Explicit guard by design: matrix path never selects `L-USEC-3018` as an LSD endpoint target.
  - Correction override: vertical endpoints near correction rows prioritize `L-USEC-C-0` midpoint.
- Updated all LSD endpoint enforcement calls to pass quarter info:
  - `Core/Plugin.cs` final/deferred LSD passes
  - `RoadAllowance/CorrectionLinePostProcessing.cs` post-correction pass
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true`
  - 0 errors (network-blocked vulnerability feed warnings only).

## Follow-up (1/4 West-Half Endpoint Gap, 2026-02-25)

- [x] Rework Rule 1 anchor sourcing to use final adjusted `L-QSEC` components instead of section-anchor proxies.
- [x] Keep outer endpoint rule matrix unchanged (`2012`/`0`/blind/`L-SEC`), only replace inner 1/4 target derivation path.
- [x] Rebuild solution to verify compile safety after the QSEC-component anchor update.

## Review (1/4 West-Half Endpoint Gap, 2026-02-25)

- Added in-matrix QSEC component resolution from final `L-QSEC` geometry and merged split components (bridging one-RA gaps).
- Quarter inner anchors now override from resolved QSEC directional half-midpoints:
  - SW: `top=westHalf`, `right=southHalf`
  - SE: `top=eastHalf`, `left=southHalf`
  - NW: `bottom=westHalf`, `right=northHalf`
  - NE: `bottom=eastHalf`, `left=northHalf`
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true`
  - 0 warnings, 0 errors.

## Follow-up (Cross-Section QSEC Bleed, 2026-02-25)

- [x] Constrain post-adjustment `L-QSEC` component resolution to the owning section envelope before computing 1/4 half-midpoint targets.
- [x] Rebuild solution to verify compile safety after section-scoping change.
