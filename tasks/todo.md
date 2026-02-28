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

## Follow-up (Deferred LSD Erase Scope, 2026-02-25)

- [x] Fix deferred LSD redraw erase rule to use requested-quarter ownership (segment midpoint inside requested quarter polygon), not window intersection.
- [x] Rebuild solution to verify compile safety after erase-scope fix.

## Follow-up (Horizontal LSD Crossing L-SEC, 2026-02-25)

- [x] Change horizontal LSD outer-target priority to prefer surveyed `L-SEC` before `L-USEC` (`2012`/`0`) in matrix endpoint pass.
- [x] Rebuild solution to verify compile safety after priority change.

## Follow-up (Surveyed SEC Cross-Section Targeting, 2026-02-25)

- [x] Scope matrix outer midpoint target candidates to the owning section envelope to prevent selecting adjacent-section `L-SEC` midpoints.
- [x] Rebuild solution to verify compile safety after section-scope candidate filter.

## Follow-up (Correction Seam North-Missing Fallback, 2026-02-25)

- [x] Add surveyed seam classification fallback using south/north surveyed horizontal boundary evidence when surveyed vertical evidence is missing.
- [x] Rebuild solution to verify compile safety after correction seam evidence update.

## Follow-up (Correction Debug Logging, 2026-02-25)

- [x] Add seam-input debug logs for 100m-buffer/range-over cases: section-to-seam side contributions and one-sided synthesized seam detection.
- [x] Add per-seam evidence diagnostics: vertical counts plus surveyed horizontal x-only vs seam-band counts and nearest north/south edge deltas.
- [x] Rebuild solution to verify compile safety after logging instrumentation.

## Follow-up (One-Sided Seam Surveyed Fallback, 2026-02-25)

- [x] Add one-sided seam state to correction seam model so classification can distinguish synthesized opposite-side seams.
- [x] Relax surveyed horizontal seam-band and edge tolerances for one-sided seams only while preserving strict thresholds for two-sided seams.
- [x] Add one-sided x-only surveyed proximity fallback when strict seam-band edge hits are absent.
- [x] Rebuild solution and run Python sanity checks for one-sided vs two-sided surveyed classification behavior.

## Follow-up (Full-Build Crash During Shapefile Import, 2026-02-25)

- [x] Trace latest runtime log and confirm crash boundary occurs inside shapefile import phase (run ends after `Starting shapefile import.`).
- [x] Add importer phase diagnostics (`Init begin/completed`, `Import begin/completed`) to isolate native-import failure boundaries.
- [x] Harden location-window setup to try both coordinate argument orders and log signature/setup failures explicitly.
- [x] Guard large surveyed shapefile imports from unsafe no-window execution by default; require `ATSBUILD_ALLOW_NO_LOCATION_WINDOW_IMPORT=1` to override.
- [x] Apply the same location-window ordering/logging hardening to P3 import helper for consistency.
- [x] Rebuild solution to verify compile safety after importer hardening changes.

## Follow-up (Latest Crash Root Cause + Shape-Set Guardrails, 2026-02-25)

- [x] Inspect latest crash log and confirm failure boundary is native `Importer.Init` on `SURVEYED_POLYGON_N83UTMZ11.shp`.
- [x] Validate active shapefile binary structure in Python and confirm top-level `SURVEYED_POLYGON_N83UTMZ11.shp` is corrupt.
- [x] Add pre-import shapefile structure validation in `ShapefileImporter` and skip/fallback before calling native `Importer.Init`.
- [x] Add recursive valid-copy fallback selection for disposition shapefiles (newest valid matching filename).
- [x] Finish shape auto-update selected-set copy path (no blanket recursive folder copy).
- [x] Add shape-set validity checks to auto-update source selection so corrupted top-level `.shp` does not win over valid backup copies.
- [x] Rebuild `AtsBackgroundBuilder` Debug and verify 0 warnings/0 errors.

## Follow-up (Quarter 1/4 Apparent Intersection, 2026-02-26)

- [x] Reproduce and trace SW/SE `6-75-17-5` 1/4 endpoint miss against correction-adjacent snap logic.
- [x] Patch quarter-line correction-adjacent snap to support bounded apparent intersection fallback when strict segment intersection narrowly misses.
- [x] Build `AtsBackgroundBuilder` to verify compile safety after the snap fix.
- [x] Add review notes with expected vs actual endpoint behavior after fix.

## Review (Quarter 1/4 Apparent Intersection, 2026-02-26)

- Traced the 1/4 endpoint miss to `EnforceQuarterLineEndpointsOnSectionBoundaries(...)` snap logic where strict line-vs-segment intersection can fail on tiny boundary truncations.
- Added `TryIntersectInfiniteLineWithBoundedSegmentExtension(...)` and wired it into both quarter-line snap paths (`TryFindSnapTarget` and correction-adjacent `TryFindCorrectionAdjacentSnapTarget`) as a bounded apparent-intersection fallback.
- Fallback is limited to near misses only (`apparentSegmentExtensionTol = 6.0m`) to avoid broad behavioral changes while allowing the intended apparent intersection to resolve.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true`
  - 0 warnings, 0 errors.

## Follow-up (Quarter Blind-Line Perpendicular Crossing, 2026-02-26)

- [x] Re-evaluate failing SW/SE `6-75-17-5` 1/4 case as blind-line crossing geometry (not endpoint-only snap).
- [x] Patch quarter-view correction south-mid projection to preserve perpendicular blind-line crossing through center-U.
- [x] Prevent correction south-mid from being re-drifted by anchor-chain intersection override.
- [x] Rebuild solution to verify compile safety after projection-path fix.

## Review (Quarter Blind-Line Perpendicular Crossing, 2026-02-26)

- Root cause: the failing point is a blind-line crossing through RA, so endpoint snap logic was insufficient; correction south-mid could drift via anchor-line intersection instead of perpendicular center projection.
- Changes in `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - `ApplyCorrectionSouthOverridesPreClamp(...)` now receives `centerU` and uses `TryProjectBoundarySegmentVAtU(..., centerU, ...)` first for south-mid on correction boundaries.
  - South-mid override from `BottomAnchor->TopAnchor` intersection is now skipped for correction south boundaries, preserving center-U perpendicular crossing.
  - Existing anchor-intersection path remains as fallback when center-U projection is unavailable.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true`
  - 0 warnings, 0 errors.

## Follow-up (Quarter Landing Verification Gate, 2026-02-26)

- [x] Switch correction south blind-line crossing to section-width divider U (`(westEdgeU + eastEdgeU)/2`) before south-segment projection.
- [x] Emit explicit non-suppressed verification logs for shared SW/SE correction corner (`VERIFY-QTR-SOUTHMID`).
- [x] Rebuild solution to verify compile safety after verification instrumentation.
- [ ] Confirm in fresh `/debug-config` run that `sw_se`/`se_sw` lands at `523596.886, 6146221.196` for `S.W. 6-75-17-5` and `S.E. 6-75-17-5`.

## Follow-up (Quarter South-Mid Segment Selection, 2026-02-26)

- [x] Promote correction-south candidate segment when current south boundary selection misses the expected 1/4 blind-line crossing.
- [x] Change correction south-segment ranking to prefer the segment closest to the intended road-allowance offset (not simply the most south segment).
- [x] Add explicit diagnostics for promoted correction-south selection so `/debug-config` can verify target landing quickly.
- [ ] Rebuild solution and verify compile safety after the selection/ranking update.

## Review (Quarter South-Mid Segment Selection, 2026-02-26)

- Updated south-boundary resolution in `Sections/Plugin.Sections.SectionDrawingLsd.cs` to promote a `L-USEC-C-0` south candidate when current south selection fits the 20.11m blind-line target worse than correction candidate.
- Updated `TryResolveQuarterViewSouthMostCorrectionBoundarySegment(...)` scoring to prefer correction segments closest to `RoadAllowanceSecWidthMeters`, with a penalty for over-south candidates instead of selecting the furthest-south segment by default.
- Added non-suppressed diagnostics: `VERIFY-QTR-SOUTHMID-PROMOTE ...` to prove when correction-south promotion occurs and how much target-fit error improved.
- Full `dotnet build` is currently blocked by file locks/access on active output targets (`bin\\x64\\Debug\\...` and `build\\net8.0-windows\\...`), but code compiles via:
  - `dotnet msbuild src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj /t:Compile /p:RestoreIgnoreFailedSources=true`

## Follow-up (Quarter South-Mid Apparent Divider Intersection, 2026-02-26)

- [x] Prioritize apparent intersection of correction south segment against true quarter divider line (`BottomAnchor -> TopAnchor`) with bounded extension tolerance.
- [x] Keep existing divider-U projection and strict anchor intersection as lower-priority fallbacks.
- [x] Emit `VERIFY-QTR-SOUTHMID` for section numbers `6` and `36` even when correction-south tagging is not active, to capture target run diagnostics.
- [x] Rebuild default solution output path and verify compile safety.

## Review (Quarter South-Mid Apparent Divider Intersection, 2026-02-26)

- Updated `ApplyCorrectionSouthOverridesPreClamp(...)` in `Sections/Plugin.Sections.SectionDrawingLsd.cs` to use bounded apparent intersection against the true divider line first, fixing drift introduced by pure constant-U projection on skewed section geometry.
- Added `TryIntersectLocalInfiniteLineWithBoundedSegmentExtension(...)` helper to support this bounded apparent-crossing resolution.
- Expanded verification log gate to include section numbers `6` and `36` and include `southSource` for easier /debug-config validation.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true`
  - 0 warnings, 0 errors.

## Follow-up (Quarter South-Mid Non-Correction + Snap Drift Guard, 2026-02-26)

- [x] Apply bounded apparent divider intersection for non-correction south boundaries before strict intersection fallback.
- [x] Protect computed shared SW/SE south-mid corner from post-draw quarter-box vertex snap drift.
- [x] Rebuild default solution output path and verify compile safety.
- [x] Run Python log check for `VERIFY-QTR-SOUTHMID` nearest-to-target diagnostics.
- [ ] Confirm in fresh `/debug-config` run that `sw_se`/`se_sw` lands at `523596.886, 6146221.196` for `S.W. 6-75-17-5` and `S.E. 6-75-17-5`.

## Review (Quarter South-Mid Non-Correction + Snap Drift Guard, 2026-02-26)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs` so non-correction south-mid resolves through `TryIntersectLocalInfiniteLineWithBoundedSegmentExtension(...)` first, then strict segment intersection.
- Added a protected south-mid corner set (`sw_se`/`se_sw`) and prevented generic post-draw quarter-box vertex snapping from re-moving those vertices.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true`
  - 0 warnings, 0 errors.
- Python verifier on current runtime log: 6 `VERIFY-QTR-SOUTHMID` entries; nearest to target is `523424.990, 6144642.221` (distance `1588.304m`), so no target-case log entry has been produced yet on current recorded runs.

## Follow-up (Quarter Divider Extension Authority, 2026-02-26)

- [x] Add final south-mid authority pass: extend active 1/4 divider trajectory (`center -> north divider`) to south boundary and use that apparent intersection.
- [x] Emit explicit diagnostic (`VERIFY-QTR-SOUTHMID-DIVEXT`) for the divider-extension resolver.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that `sw_se`/`se_sw` lands at `523596.886, 6146221.196` for `S.W. 6-75-17-5` and `S.E. 6-75-17-5`.

## Review (Quarter Divider Extension Authority, 2026-02-26)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs` to apply a final south-mid resolver that intersects the active divider trajectory (`center -> northAtMid`) with the south boundary segment using bounded extension tolerance.
- Added `VERIFY-QTR-SOUTHMID-DIVEXT` logging to prove when this resolver controls the shared SW/SE south-mid point.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true`
  - 0 warnings, 0 errors.

## Follow-up (Quarter Uses Actual L-QSEC Divider, 2026-02-26)

- [x] Use real `L-QSEC` vertical divider segment (when present) as the 1/4 south-mid intersection reference.
- [x] Thread resolved divider line through correction and non-correction south-mid intersection paths.
- [x] Include divider source (`anchors` vs `L-QSEC`) in `VERIFY-QTR-SOUTHMID*` diagnostics.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that `sw_se`/`se_sw` lands at `523596.886, 6146221.196` for `S.W. 6-75-17-5` and `S.E. 6-75-17-5`.

## Review (Quarter Uses Actual L-QSEC Divider, 2026-02-26)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs` to collect `L-QSEC` segments in quarter-view rebuild scope and resolve the best vertical divider per section frame.
- South-mid intersection logic (regular, correction override, and `DIVEXT` authority) now uses that resolved divider line instead of anchor-only divider geometry.
- Added `dividerSource=` diagnostics to `VERIFY-QTR-SOUTHMID`, `VERIFY-QTR-SOUTHMID-SNAP`, and `VERIFY-QTR-SOUTHMID-DIVEXT`.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true`
  - 0 warnings, 0 errors.

## Follow-up (South Segment Must Span Divider, 2026-02-26)

- [x] Use Python on failing `VERIFY-QTR-SOUTHMID-DIVEXT-INPUT` to compute apparent intersection and required segment extension.
- [x] Confirm failing handle (`C599B2`) misses because selected south segment is east-only (`found=False`, required extension ~639m).
- [x] Update south-segment ranking to penalize divider/mid-U coverage gap (favor segment spanning divider crossing).
- [x] Apply same center-gap penalty to correction south selectors for consistency.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that `sw_se`/`se_sw` lands at `523596.886, 6146221.196` for `S.W. 6-75-17-5` and `S.E. 6-75-17-5`.

## Review (South Segment Must Span Divider, 2026-02-26)

- Python check on logged failing geometry (`handle=C599B2`) computed divider-vs-south apparent intersection at `523596.889, 6146220.673` (0.524m from target), but required extension was ~`639.089m`, proving selected south segment did not span the divider crossing.
- Updated `TryResolveQuarterViewSouthBoundaryV(...)`, `TryResolveQuarterViewSouthCorrectionBoundaryV(...)`, and `TryResolveQuarterViewSouthMostCorrectionBoundarySegment(...)` to add a strong center-gap penalty (`DistanceToClosedInterval(frame.MidU, segMinU, segMaxU)`), so non-spanning fragments are heavily de-prioritized.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true`
  - 0 warnings, 0 errors.

## Follow-up (SW Corner Apparent West x South, 2026-02-26)

- [x] Trace failing surveyed correction case `6-39-6-5` (`sec=6`, handle `C5AA27`) and confirm incorrect SW corner `sw_sw=645493.944,5798656.689`.
- [x] Update SW-corner resolver to prioritize infinite west-boundary x infinite south-boundary apparent intersection.
- [x] Prevent apparent-intersection SW corner from being re-clamped to fallback south limit.
- [x] Emit explicit SW-corner verification log (`VERIFY-QTR-SW-SW-APP`) for sec 6/36 diagnostics.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that SW-quarter SW corner lands at `645482.592, 5798669.389` for `6-39-6-5`.

## Review (SW Corner Apparent West x South, 2026-02-26)

- In `Sections/Plugin.Sections.SectionDrawingLsd.cs`, added `TryIntersectLocalInfiniteLines(...)` and used it for the SW-corner (`westAtSouthU`/`southAtWestV`) before strict segment-bounded fallback.
- Added sanity gates on resolved apparent offsets and a lock flag so the resolved SW corner is not re-clamped by generic south-boundary fallback logic.
- Added `VERIFY-QTR-SW-SW-APP` logging for section diagnostics (`sec=6`/`sec=36`) to prove the SW apparent intersection resolver path.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true`
  - 0 warnings, 0 errors.

## Follow-up (Quarter East-Corner Apparent Intersections, 2026-02-26)

- [x] Resolve an explicit east boundary segment in quarter-view build scope and drive east-side corner construction from it.
- [x] Add apparent-intersection authority for `SE.SE` (`east x south`) and `NE.NE` (`east x north`) with strict bounded fallback.
- [x] Add `VERIFY-QTR-EAST-CORNERS` plus `VERIFY-QTR-SE-SE-APP` / `VERIFY-QTR-NE-NE-APP` diagnostics for section 6/36 validation.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that `NE.NE` and `SE.SE` land at user-provided targets for `6-39-6-5`.

## Review (Quarter East-Corner Apparent Intersections, 2026-02-26)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs` to resolve a quarter-view east boundary segment (`L-USEC-0`) and project/intersect east-side corners from that geometry instead of forcing `U = frame.EastEdgeU`.
- Added final apparent intersection passes for east-side corners:
  - `SE.SE`: infinite `east` x `south` preferred, strict segment intersection fallback.
  - `NE.NE`: infinite `east` x `north` preferred, strict segment intersection fallback.
- Updated NE/SE polygon right-side vertices to use resolved east mid/corner points so landing coordinates follow the apparent east boundary.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true`
  - 0 warnings, 0 errors.

## Follow-up (Quarter East Boundary Layer Fallback, 2026-02-26)

- [x] Confirm latest `/debug-config` still used `eastSource=fallback-east` for failing section 6 east corners.
- [x] Expand east-boundary resolver to try prioritized vertical hard-boundary layers (`L-USEC-0`, `L-USEC`, `L-SEC`, `L-SEC-2012`, `L-USEC-20`, `L-USEC-2012`) before fallback east edge.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that `eastSource` is no longer fallback and `NE.NE` / `SE.SE` land at user-provided targets for `6-39-6-5`.

## Review (Quarter East Boundary Layer Fallback, 2026-02-26)

- In `Sections/Plugin.Sections.SectionDrawingLsd.cs`, added layered east-boundary fallback selection so east-corner intersections are no longer blocked when `L-USEC-0` is absent in local geometry.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true`
  - 0 warnings, 0 errors.

## Follow-up (SE 1/4 LSD Outer Endpoint Crossing, 2026-02-26)

- [x] Trace SE-quarter LSD outer endpoint rule-matrix scoring for `L-SEC` target selection.
- [x] Update outer-target selection to preserve endpoints already on preferred boundary segments.
- [x] Change outer-target scoring to prefer nearest outward boundary from inner anchor (prevent skipping first `L-SEC` and crossing to farther `L-SEC`).
- [x] Add `VERIFY-LSD-OUTER` diagnostics for section 6/36 to confirm outer endpoint moves.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that S.E.1/4 LSD line no longer crosses past the intended `L-SEC` boundary.

## Review (SE 1/4 LSD Outer Endpoint Crossing, 2026-02-26)

- Updated `TryFindBoundaryMidpointTarget(...)` in `RoadAllowance/Plugin.RoadAllowance.EndpointEnforcement.cs`:
  - Added early preserve path when endpoint is already on a preferred in-scope boundary segment.
  - Changed scoring to prioritize lower `outwardAdvance` (nearest outward boundary from inner anchor) before endpoint-move distance.
- Added section-scoped diagnostics for outer moves:
  - `VERIFY-LSD-OUTER sec=... q=... line=... inner=... outerFrom=... outerTo=... kinds=...`
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true`
  - 0 warnings, 0 errors.

## Follow-up (Section 11 Non-Correction West-Side 1/4 Points, 2026-02-26)

- [x] Inspect reported `11-39-7-5` screenshot and confirm mismatch is on west-side 1/4 definition points (`N.W.`, `W.1/2`, `S.W.`) in non-correction geometry.
- [x] Add apparent-intersection authority for west/north (`NW`) corner similar to existing west/south (`SW`) handling.
- [x] Protect west-side definition vertices (`NW`, `W.1/2`, `SW`) from generic post-draw quarter vertex snap drift.
- [x] Extend verification diagnostics to include section `11` and emit `VERIFY-QTR-WEST-CORNERS`.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that section `11-39-7-5` west-side 1/4 definition points land on intended non-correction apparent intersections.

## Review (Section 11 Non-Correction West-Side 1/4 Points, 2026-02-26)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - Added `NW` apparent infinite-intersection path (`west x north`) with clamp lock (`VERIFY-QTR-NW-NW-APP`).
  - Added protected west-side vertex set to prevent post-draw snap from re-drifting `NW`, `W.1/2`, `SW`.
  - Extended section verification gate from `6/36` to `6/11/36` and added `VERIFY-QTR-WEST-CORNERS`.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true`
  - 0 warnings, 0 errors.

## Follow-up (Section 11 Multi-Corner Huge Misses, 2026-02-26)

- [x] Trace current failing sec=11 logs and confirm selected south boundary segment does not span divider (`DIVEXT found=False`).
- [x] Thread divider-preferred U into non-correction west/south/north boundary selectors.
- [x] Penalize south/north candidates by divider-span gap and center coverage so non-spanning fragments lose ranking.
- [x] Add west-boundary center-coverage penalty and divider-side guard to avoid unstable west picks near divider.
- [x] Add explicit selection diagnostics (`VERIFY-QTR-SOUTH-SELECT`, `VERIFY-QTR-NORTH-SELECT`, `VERIFY-QTR-WEST-SELECT`) for sec 6/11/36.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that all reported sec=11 corner targets land within `0.01 m`.

## Review (Section 11 Multi-Corner Huge Misses, 2026-02-26)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs` to compute and propagate `dividerPreferredU` from the active divider line and use it in south/north/west boundary selection.
- South and north boundary ranking now includes divider-span penalty (`GetQuarterDividerSpanPenalty`) plus center-gap penalty, preventing east/west-only fragments from winning.
- West boundary ranking now includes center-coverage penalty and rejects candidates too close to/inside divider side.
- Added sec-targeted diagnostics to prove which boundary segments were selected and why:
  - `VERIFY-QTR-SOUTH-SELECT`
  - `VERIFY-QTR-NORTH-SELECT`
  - `VERIFY-QTR-WEST-SELECT`
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true`
  - 0 warnings, 0 errors.

## Follow-up (Section 11 Blind-Boundary Hard Candidate Gate, 2026-02-26)

- [x] Reproduce latest sec=11 miss from `/debug-config` logs and confirm south candidate still cannot intersect active divider (`DIVEXT found=False`).
- [x] Update non-correction blind south selector to require a divider-intersectable candidate when one exists; only fall back to unconstrained scoring if none exist.
- [x] Update north selector tie-break behavior to prefer candidates that can form a bounded apparent intersection with selected west boundary when available.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that sec=11 reported targets land at the user-provided coordinates.

## Review (Section 11 Blind-Boundary Hard Candidate Gate, 2026-02-26)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - `TryResolveQuarterViewSouthBoundaryV(...)` now requires divider-linked candidates for blind (`expectedOffset=0`) south selection when any such candidate exists, using bounded apparent intersection against active divider (`maxSegmentExtension=80m`).
  - `TryResolveQuarterViewNorthBoundaryV(...)` now supports a west-linked candidate preference path (bounded apparent intersection against selected west boundary) for blind non-correction selection.
  - Added selector diagnostics fields:
    - `VERIFY-QTR-SOUTH-SELECT ... dividerLinked=...`
    - `VERIFY-QTR-NORTH-SELECT ... westLinked=...`
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home` in workspace)
  - 0 warnings, 0 errors.

## Follow-up (Section 11 Blind West/South/North Corner Authority, 2026-02-26)

- [x] Add blind non-correction SW corner authority from the active west-boundary south endpoint (`VERIFY-QTR-SW-SW-END`).
- [x] Add blind non-correction NW corner snap from hard west-side boundary corner clusters (`VERIFY-QTR-NW-NW-SNAP`).
- [x] Add blind non-correction south-mid fallback to south endpoint of active divider (`VERIFY-QTR-SOUTHMID-DIVEND`) when bounded divider-vs-south extension is unavailable.
- [x] Emit `VERIFY-QTR-SOUTHMID` for section `11` to expose shared `S.W.1/4 S.E.` and `S.E.1/4 S.W.` landing values.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that remaining sec=11 points (`S.W.1/4 S.W.`, `N.W.1/4 N.W.`, shared south-mid) land at user-provided coordinates.

## Review (Section 11 Blind West/South/North Corner Authority, 2026-02-26)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - Added `TryResolveWestBandCornerFromHardBoundaries(...)` for west-side hard-corner snapping in north/south bands.
  - Added blind SW authority from west-segment south endpoint (`VERIFY-QTR-SW-SW-END`).
  - Added blind NW authority from hard-corner cluster snap (`VERIFY-QTR-NW-NW-SNAP`).
  - Added blind south-mid fallback to divider south endpoint (`VERIFY-QTR-SOUTHMID-DIVEND`) and extended `VERIFY-QTR-SOUTHMID` emission to section `11`.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.

## Follow-up (LSD Enforcement Scope Isolation Across Adjacent Section Builds, 2026-02-27)

- [x] Trace report where building `64-3-6` then `63-3-6` mutates LSD lines in unrelated `64-3-6` area above correction line.
- [x] Restrict correction post-build LSD enforcement to requested-only quarter infos instead of combined requested+context infos.
- [x] Add rule-matrix guard so LSD quarter contexts are ignored unless their quarter/section ids are in the requested scope set.
- [ ] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that building `63-3-6` no longer alters LSD lines in previously-built `64-3-6` above correction line.

## Review (LSD Enforcement Scope Isolation Across Adjacent Section Builds, 2026-02-27)

- Updated `RoadAllowance/Plugin.RoadAllowance.CorrectionLinePostProcessing.cs`:
  - post-correction LSD enforcement now passes a requested-scope-only `QuarterLabelInfo` list (filtered by requested quarter id or section polyline id).
- Updated `RoadAllowance/Plugin.RoadAllowance.EndpointEnforcement.cs`:
  - added a defensive rule-matrix guard to ignore quarter contexts not in requested scope.
- Build status:
  - attempted `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - blocked in this environment by missing package restore (`NU1101 System.Data.OleDb`) due offline NuGet access.

## Follow-up (Section 11 Locked West-Corner U Clamp Drift, 2026-02-26)

- [x] Trace residual drift and confirm `S.W.1/4 S.W.` / `N.W.1/4 N.W.` can be moved by post-lock west-U clamp.
- [x] Preserve locked west-side U values by skipping `ClampWestBoundaryU(...)` for south/north west corners when lock flags are active.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that:
  - `S.W.1/4 S.W.` lands at `642167.465, 5800232.951`
  - `N.W.1/4 N.W.` lands at `642122.338, 5801839.106`

## Review (Section 11 Locked West-Corner U Clamp Drift, 2026-02-26)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs` to skip west-U clamping for locked corners:
  - `westAtSouthU` now clamps only when `southWestLockedByApparentIntersection == false`.
  - `westAtNorthU` now clamps only when `northWestLockedByApparentIntersection == false`.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.

## Follow-up (Section 6 SW Corner Minor South Overshoot, 2026-02-26)

- [x] Trace latest sec=6 miss and confirm `sw_sw` still came from `VERIFY-QTR-SW-SW-APP` with ~1.063m south drift.
- [x] Add a final non-blind west-south hard-corner refinement (`VERIFY-QTR-SW-SW-SNAP`) using clustered hard intersections only (`priority<=0`) with tight move cap (`<=5m`).
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that `S.W.1/4 S.W.` lands at `645482.592, 5798669.389`.

## Review (Section 6 SW Corner Minor South Overshoot, 2026-02-26)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - Extended `TryResolveWestBandCornerFromHardBoundaries(...)` with explicit `maxMove` parameter.
  - Added non-blind SW-corner refinement pass after apparent west/south intersection:
    - snap candidate source: hard corner clusters in south west-band
    - guards: `priority<=0`, `move<=5m`
    - diagnostic: `VERIFY-QTR-SW-SW-SNAP`.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.

## Follow-up (Section 11 NW + South-Mid Skew-Frame Conversion Drift, 2026-02-26)

- [x] Trace latest sec=11 regression and confirm:
  - `N.W.1/4 N.W.` still snapped from `VERIFY-QTR-NW-NW-SNAP` to wrong local point.
  - shared south-mid used `DIVEND` but remained south-shifted (`642992.796,5800225.313`) from target (`642992.804,5800226.246`).
- [x] Replace dot-product world->local conversions with exact 2x2 basis inversion for critical corner paths (`SW-END`, `NW-SNAP`, `SOUTHMID-DIVEND`, hard-corner cluster sampling).
- [x] Prevent `NW-NW-SNAP` from overriding a successful apparent `NW` intersection lock; relax blind NW apparent west-offset cap to avoid near-threshold rejection.
- [x] Preserve blind `SOUTHMID-DIVEND` endpoint V exactly (no quarter-span clamp) so south-half lines terminate on the true divider endpoint.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that sec=11:
  - `N.W.1/4 N.W.` lands at `642122.338, 5801839.106`
  - `S.W.1/4 S.E.` / `S.E.1/4 S.W.` lands at `642992.804, 5800226.246`.

## Review (Section 11 NW + South-Mid Skew-Frame Conversion Drift, 2026-02-26)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - Added `TryConvertQuarterWorldToLocal(...)` (exact basis inversion) and used it in critical corner/endpoint conversions.
  - `NW` apparent acceptance for blind non-correction now uses a relaxed west-offset cap (`120m`).
  - `NW-NW-SNAP` now runs only when apparent `NW` lock is not already active.
  - Blind `DIVEND` south-mid now keeps exact divider endpoint V (no clamp).
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.

## Follow-up (Section 11 NW Snap Reference Regression, 2026-02-26)

- [x] Trace latest sec=11 logs and confirm:
  - south-mid reached exact target in latest run (`642992.804, 5800226.246`)
  - `N.W.1/4 N.W.` still forced by `NW-NW-SNAP` to east candidate (`642141.89, 5801859.763`).
- [x] Change blind NW snap reference from center-west U to raw apparent `west x north` intersection (`VERIFY-QTR-NW-NW-RAW`) and use that as preferred-U/current for hard-corner snap.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that sec=11 `N.W.1/4 N.W.` lands at `642122.338, 5801839.106` while south-mid remains exact at `642992.804, 5800226.246`.

## Review (Section 11 NW Snap Reference Regression, 2026-02-26)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - Added `NW` raw apparent intersection seed log `VERIFY-QTR-NW-NW-RAW`.
  - For blind non-correction sections, `NW-NW` hard-corner snap now seeds from raw `west x north` apparent intersection (`preferredU=rawU`, `currentLocal=(rawU,rawV)`) instead of center-west U.
  - Keeps existing lock guard (`!northWestLockedByApparentIntersection`) and high move envelope for blind sections.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.

## Follow-up (Section 11 NW Snap Over-Drift From Raw Seed, 2026-02-26)

- [x] Trace latest sec=11 run and confirm:
  - `NW-NW-RAW` is present and near expected west-north apparent intersection.
  - `NW-NW-SNAP` still drifts to an incorrect candidate (~20m north).
- [x] Add blind `NW` raw-lock authority (`VERIFY-QTR-NW-NW-RAWLOCK`) before snap; keep raw point when offsets are within broad acceptance band.
- [x] Restrict blind `NW` hard-corner refinement to micro-adjustment when raw exists (`maxMove=5m`, `priority<=0`).
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that sec=11 `N.W.1/4 N.W.` lands at `642122.338, 5801839.106` and no longer drifts north.

## Review (Section 11 NW Snap Over-Drift From Raw Seed, 2026-02-26)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - Added raw-lock stage for blind `NW` from apparent west×north intersection with explicit diagnostics:
    - `VERIFY-QTR-NW-NW-RAW`
    - `VERIFY-QTR-NW-NW-RAWLOCK`
  - Blind `NW-NW-SNAP` now only applies when candidate is `priority<=0` and movement is within local cap:
    - `<=5m` if raw intersection exists
    - `<=90m` fallback when raw is unavailable
  - `NW-NW-SNAP` log now includes `move=...` to prove bounded refinement.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.

## Follow-up (Section 11 NW Skewed Local Intersection Drift, 2026-02-26)

- [x] Verify with Python from latest sec11 `/debug-config` logs that true world `west x north` apparent intersection is near required target while logged `nw_nw` is far off.
- [x] Patch quarter intersection helpers to use exact world->local basis inversion (`TryConvertQuarterWorldToLocal`) instead of dot-product projection.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that sec11 `N.W.1/4 N.W.` lands at `642122.338, 5801839.106`.

## Review (Section 11 NW Skewed Local Intersection Drift, 2026-02-26)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - `TryIntersectLocalInfiniteLineWithBoundedSegmentExtension(...)` now converts all world points through exact basis inversion.
  - `TryIntersectLocalInfiniteLines(...)` now converts all world points through exact basis inversion.
  - `TryIntersectBoundarySegmentsLocal(...)` now converts all world points through exact basis inversion.
- Python verifier on latest sec11 logs:
  - apparent world `west x north` intersection: `642121.250, 5801839.727`
  - required target: `642122.338, 5801839.106`
  - delta: `1.252 m`
  - logged `nw_nw` at failure time: `642151.941, 5801870.076` (delta `42.842 m`)
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.

## Follow-up (Section 11 Blind NW Raw-Lock Rejection, 2026-02-26)

- [x] Inspect latest `/debug-config` sec11 logs and confirm `VERIFY-QTR-NW-NW-RAW` exists while `RAWLOCK` is missing and final `nw_nw` falls back to wrong point.
- [x] Relax blind non-correction `NW` raw-lock offset gate so valid apparent `west x north` raw intersections are accepted and locked.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that sec11 `N.W.1/4 N.W.` no longer falls back to `642151.941, 5801870.076` and lands on the apparent west x north intersection near `642122.338, 5801839.106`.

## Review (Section 11 Blind NW Raw-Lock Rejection, 2026-02-26)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs` raw-lock gate in blind non-correction `NW` path:
  - west min offset: `-20` (was `-6`)
  - north min offset: `-60` (was `-6`)
  - max offset: `180` (was `140`)
- This prevents valid blind apparent intersections (`NW-NW-RAW`) from being rejected and replaced by fallback corner values.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.

## Follow-up (Quarter 3-Point Collapse on SW Endpoint Override, 2026-02-26)

- [x] Trace latest `/debug-config` logs and confirm collapse pattern: `VERIFY-QTR-SW-SW-END` sets `v` near midline, then `VERIFY-QTR-WEST-CORNERS` shows `sw_sw == w_half` (triangle/3-point quarter).
- [x] Patch blind SW endpoint override (`SW-SW-END`) to apply only when west endpoint is in south band near `SouthEdgeV`.
- [x] Rebuild default solution output path and verify compile safety.
- [x] Run Python check on latest logs to confirm bad midpoint override would be rejected by new gate while valid south endpoint overrides remain allowed.
- [ ] Confirm in fresh `/debug-config` run for `59-3-6` that quarter definitions no longer collapse to 3 points.

## Review (Quarter 3-Point Collapse on SW Endpoint Override, 2026-02-26)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs` in blind non-correction SW endpoint authority path:
  - `SW-SW-END` now requires endpoint `v <= SouthEdgeV + 2*dividerIntersectionDriftTolerance` before overriding apparent SW corner.
  - This prevents midpoint-only west segments from collapsing `sw_sw` onto `w_half`.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.
- Python verification on latest failing block:
  - `sec=11`: `SW-SW-END v=-0.000`, inferred south band max `120.002` -> allowed.
  - `sec=36`: `SW-SW-END v=804.199`, inferred south band max `119.995` -> rejected (prevents collapse).

## Follow-up (Section 12 NE Quarter-Corner Post-Snap Drift, 2026-02-27)

- [x] Trace `12-39-7-5` miss report and confirm runtime shows post-draw quarter vertex snapping still active while east corners were not in protected-corner set.
- [x] Protect east apparent corners (`NE.NE`, `SE.SE`) from post-draw quarter vertex snapping when locked by apparent intersection authority.
- [x] Expand section-targeted quarter verification gate to include section `12` for direct `VERIFY-QTR-*` diagnostics on future `/debug-config` runs.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that `N.E.1/4 N.E.` for `12-39-7-5` lands at `645385.349, 5801908.843`.

## Review (Section 12 NE Quarter-Corner Post-Snap Drift, 2026-02-27)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - Added `protectedEastBoundaryCorners` list in quarter-view rebuild pass.
  - Added locked east corners to protected set:
    - `SE.SE` when `southEastLockedByApparentIntersection`
    - `NE.NE` when `northEastLockedByApparentIntersection`
  - Extended protected-corner matcher to include east-corner protected points.
  - Extended section-targeted verification gate to include section `12`.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.

## Follow-up (Section 12/11 East-Corner Drift Persisting, 2026-02-27)

- [x] Inspect latest logs and confirm sec12 runs were still missing full east-corner diagnostics (`emitQuarterVerify` did not include section 12).
- [x] Include section `12` in quarter verify log gate so `VERIFY-QTR-NE-NE-APP` and `VERIFY-QTR-EAST-CORNERS` are emitted for direct sec12 validation.
- [x] Widen east-corner protection scope so post-draw vertex snap cannot move `NE.NE` / `SE.SE` whenever east boundary geometry is present (not only apparent-lock code path).
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run:
  - sec12 `N.E.1/4 N.E.` lands at `645385.349, 5801908.843`
  - sec11 `N.E.1/4 N.E.` no longer has residual drift.

## Review (Section 12/11 East-Corner Drift Persisting, 2026-02-27)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - `emitQuarterVerify` now includes section `12`.
  - East-corner protected-corner set now always includes:
    - `SE.SE` when `hasEastBoundarySegment && hasSouthBoundarySegment`
    - `NE.NE` when `hasEastBoundarySegment && hasNorthBoundarySegment`
  - This prevents post-draw hard-corner snap from re-drifting east quarter definition corners in branches where apparent-lock flags are not set.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.

## Follow-up (Section 12/11 NE Wrong East-Segment Selection, 2026-02-27)

- [x] Inspect latest sec11/sec12 logs and confirm `NE.NE` still driven by east boundary path that does not represent the intended apparent east-side intersection.
- [x] Add blind-section east reselector that evaluates east candidate segments against both resolved south and north boundary intersections before NE/SE corner construction.
- [x] Emit east-selection diagnostics (`VERIFY-QTR-EAST-SELECT`) with east/south/north offsets and chosen segment.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that sec12 `N.E.1/4 N.E.` and sec11 `N.E.1/4 N.E.` both land at required coordinates.

## Review (Section 12/11 NE Wrong East-Segment Selection, 2026-02-27)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - Added `TryResolveQuarterViewEastBoundarySegmentFromNorthSouth(...)` to score/select east boundary segments using:
    - east offset at mid-V
    - apparent intersection offsets vs resolved south boundary
    - apparent intersection offsets vs resolved north boundary
    - preferred east-layer priority.
  - For blind sections, apply this reselector after south/north boundary resolution and before quarter corner construction.
  - Added `VERIFY-QTR-EAST-SELECT` diagnostics for sec-target runs.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.

## Follow-up (Section 12/11 East Selector Rejection, 2026-02-27)

- [x] Inspect newest sec11/sec12 logs and confirm NE misses persisted while no `VERIFY-QTR-EAST-SELECT` entries were emitted (candidate selector returned false).
- [x] Add explicit `VERIFY-QTR-EAST-SELECT ... found=False` logging branch so selector rejection is visible in runtime diagnostics.
- [x] Relax east selector candidate gating (remove hard offset rejection) and bias scoring toward north-intersection fit to stabilize `NE.NE`.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that `VERIFY-QTR-EAST-SELECT` is emitted for sec11/sec12 and `N.E.1/4 N.E.` lands at required coordinates.

## Review (Section 12/11 East Selector Rejection, 2026-02-27)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - Added explicit rejection diagnostics:
    - `VERIFY-QTR-EAST-SELECT ... found=False`
  - Relaxed `TryResolveQuarterViewEastBoundarySegmentFromNorthSouth(...)` by removing hard offset min/max candidate rejection.
  - Updated score weighting to prioritize north fit for NE corner stability:
    - `eastOffset * 1.0`
    - `southOffset * 2.0`
    - `northOffset * 4.0`
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.

## Follow-up (Section 12/11 NE Interior Hard-Node Authority, 2026-02-27)

- [x] Implement a dedicated east-band hard-corner resolver for quarter view using hard boundary corner clusters.
- [x] Apply blind non-correction `N.E.1/4 N.E.` snap to interior hard corner (post east/north intersection, pre-clamp).
- [x] Add explicit diagnostics for NE authority path:
  - `VERIFY-QTR-NE-NE-RAW`
  - `VERIFY-QTR-NE-NE-SNAP`
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that:
  - sec12 `N.E.1/4 N.E.` lands at `645385.349, 5801908.843`
  - sec11 `N.E.1/4 N.E.` lands at its required interior node.

## Review (Section 12/11 NE Interior Hard-Node Authority, 2026-02-27)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - Added `TryResolveEastBandCornerFromHardBoundaries(...)` (east-window analog to west-band snap helper).
  - Added blind non-correction NE snap pass that resolves `N.E.1/4 N.E.` from hard-corner clusters after east/north intersection derivation and before clamping.
  - Added targeted NE diagnostics:
    - `VERIFY-QTR-NE-NE-RAW`
    - `VERIFY-QTR-NE-NE-SNAP`
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.

## Follow-up (Section 12 NE North-Side Overshoot + Shared-Point Erase, 2026-02-27)

- [x] Rework blind `N.E.1/4 N.E.` hard-node snap scoring/gating to reject north-side/exterior candidates and prefer interior road-allowance inset corners.
- [x] Add directional NE guard: when raw NE intersection exists, allow snap only when candidate does not move farther north/east than raw authority.
- [x] Tighten `L-QUATER` stale-erase ownership from centroid-only to centroid+all-vertices-in-same-rebuilt-section.
- [x] Add shared-endpoint guard to shortest-wins section overlap cleanup so adjacent segments touching at one point are never erased.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run:
  - sec12 `N.E.1/4 N.E.` no longer snaps north across RA and lands at required interior point.
  - adjacent section builds no longer erase 1/4 definition objects when geometry only shares endpoints.

## Review (Section 12 NE North-Side Overshoot + Shared-Point Erase, 2026-02-27)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - `TryResolveEastBandCornerFromHardBoundaries(...)` now:
    - limits east/north candidate window to interior-side bands
    - rejects negative insets (north/east exterior candidates)
    - scores by fit to expected RA inset (`RoadAllowanceSecWidthMeters`) plus move/priority.
  - `NE` snap gate now enforces:
    - inset band limits
    - no northward/eastward jump relative to raw apparent `east x north` intersection when present.
  - Stale `L-QUATER` erase now requires full polyline ownership (centroid and all vertices inside same rebuilt section), preventing adjacent shared-point deletions.
- Updated `Core/Plugin.cs`:
  - `CleanupOverlappingSectionLinesByShortest(...)` now skips erase when two candidates only share one endpoint.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.

## Follow-up (Section 12 NE Hard-Node Miss + No Endpoint Termination, 2026-02-27)

- [x] Inspect latest runtime logs and confirm sec12 `NE` still came from projected `east x north` path (or unconstrained cluster snap), not a hard node where a west-running boundary truly reaches the selected east segment.
- [x] Add dedicated `NE` hard-node resolver using:
  - selected east boundary segment,
  - west-running boundary segment candidates,
  - bounded reach checks (no far extension-only intersections),
  - nearby hard-corner-cluster validation.
- [x] Wire `NE` blind non-correction flow to prefer hard-node resolver, with cluster snap fallback only if hard-node resolution fails.
- [x] Add `VERIFY-QTR-NE-NE-NODE` diagnostics for hard-node-resolved corners.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that sec12 `N.E.1/4 N.E.` terminates on the required apparent interior node and no longer appears as a non-ending/missing endpoint.

## Review (Section 12 NE Hard-Node Miss + No Endpoint Termination, 2026-02-27)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - Added `TryResolveNorthEastCornerFromEastHardNode(...)`:
    - intersects east boundary with west-running boundary candidates,
    - rejects candidates requiring excessive horizontal/east-segment reach extension,
    - requires proximity to an observed hard corner cluster,
    - ranks by corner priority, layer priority, cluster distance, reach gap, and move distance.
  - Blind `NE` path now applies hard-node solver first and logs:
    - `VERIFY-QTR-NE-NE-NODE`
  - Existing east-band cluster snap retained as fallback only.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.

## Follow-up (Endpoint-Only Rule for Non-Correction NE Corners, 2026-02-27)

- [x] Apply explicit rule: endpointless/apparent NE corner resolution is allowed only on correction-adjoining sections.
- [x] Add endpoint-corner-cluster NE resolver that requires horizontal+vertical endpoint node evidence for non-correction sections.
- [x] In blind NE flow, prioritize endpoint-corner resolver first, then correction-allowed hard-node resolver, then legacy snap fallback.
- [x] Add diagnostics:
  - `VERIFY-QTR-NE-NE-ENDPT`
  - `VERIFY-QTR-NE-NE-ENDPT-FALLBACK`
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that non-correction sec12 NE corner terminates on endpoint-based node and no longer appears as open/missing.

## Review (Endpoint-Only Rule for Non-Correction NE Corners, 2026-02-27)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - Added `TryResolveNorthEastCornerFromEndpointCornerClusters(...)` for endpoint-validated NE node selection.
  - Added correction-adjoining gate:
    - non-correction sections require endpoint-node NE authority
    - correction-adjoining sections can still use non-endpoint apparent/hard-node logic.
  - Added fallback to east-segment north endpoint when no endpoint node is found in non-correction branch (prevents extension-only non-terminating NE points).
  - Added `VERIFY-QTR-NE-NE-ENDPT` / `VERIFY-QTR-NE-NE-ENDPT-FALLBACK` diagnostics.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.

## Follow-up (NE Fallback Branch Cleanup, 2026-02-27)

- [x] Trim NE fallback flow so non-correction-adjoining sections stop at endpoint authority and do not continue into hard-node / cluster snap branches.
- [x] Keep hard-node + cluster snap fallback only for correction-adjoining sections.
- [x] Preserve `VERIFY-QTR-NE-NE-ENDPT` / `VERIFY-QTR-NE-NE-ENDPT-FALLBACK` diagnostics for endpoint branch visibility.
- [x] Rebuild default solution output path and verify compile safety.
- [ ] Confirm in fresh `/debug-config` run that:
  - non-correction NE corners remain endpoint-terminated with no apparent/projection fallback drift,
  - correction-adjoining sections still resolve required endpointless cases.

## Review (NE Fallback Branch Cleanup, 2026-02-27)

- Updated `Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - Refactored blind NE branch to explicit two-path flow:
    - non-correction-adjoining: endpoint-only (`ENDPT` / `ENDPT-FALLBACK`) and exit.
    - correction-adjoining: hard-node (`NE-NE-NODE`) then cluster snap (`NE-NE-SNAP`) fallback chain.
  - This removes non-correction branch exposure to extension-based fallback logic that previously caused endpoint-miss regressions.
- Build passed:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -p:RestoreIgnoreFailedSources=true` (with `DOTNET_CLI_HOME=.dotnet-home`)
  - 0 warnings, 0 errors.
## Follow-up (Build Recovery, 2026-02-27)

- [x] Identify why local build in this environment fails with missing `Autodesk.*` namespaces.
- [x] Fix project AutoCAD reference hint paths so they resolve correctly from any repo location.
- [x] Rebuild `AtsBackgroundBuilder` Release and confirm output artifacts in `build/net8.0-windows`.

## Review (Build Recovery, 2026-02-27)

- Root cause: AutoCAD assembly `HintPath` entries in `AtsBackgroundBuilder.csproj` used an over-long relative path that resolved to `C:\Users\Program Files\...` instead of `C:\Program Files\...`.
- Fix: switched AutoCAD reference hint paths to `$(ProgramFiles)\Autodesk\AutoCAD 2025\...` for deterministic resolution.
- Build verification:
  - `C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --configfile src\AtsBackgroundBuilder\NuGet.Config`
  - Result: success, 0 errors (57 existing nullable warnings).
- Artifacts verified in:
  - `build\net8.0-windows\AtsBackgroundBuilder.dll`
  - `src\AtsBackgroundBuilder\bin\x64\Release\net8.0-windows\AtsBackgroundBuilder.dll`

## Follow-up (PLSR Existing Labels + 1/4 UI Default, 2026-02-27)

- [x] Remove hard dependency that required `Disposition labels` to be enabled before running `Check PLSR`.
- [x] Reuse existing disposition labels per quarter (from disposition text layers) so label generation skips already-labeled `dispNum` in that same 1/4.
- [x] Keep missing-label generation gated behind `Disposition labels` toggle.
- [x] Keep/verify PLSR expiry tagging by appending `(Expired)` on existing labels where XML shows expired activity.
- [x] Set `1/4 Definition` default to off in UI (WPF + WinForms) and config defaults.
- [x] Verify compile safety after changes.

## Review (PLSR Existing Labels + 1/4 UI Default, 2026-02-27)

- Updated PLSR/build flow in `Core/Plugin.cs`:
  - Quarter scope is now built when either `Disposition labels` OR `Check PLSR` is enabled.
  - `Check PLSR` no longer skips when labels are disabled.
  - Existing labels are indexed by quarter+disp before placement and passed into label placement to prevent duplicate labels.
- Updated label placement in `Dispositions/LabelPlacer.cs`:
  - `PlaceLabels(...)` accepts an optional `existingDispNumsByQuarter` index.
  - Label creation now skips when the same normalized `dispNum` already exists in the same 1/4.
  - New labels added in-run are inserted into the same index to avoid same-run duplicates.
- Updated PLSR label collection in `Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - Existing labels are now filtered to disposition text layers aligned with layering logic (`C-*-T` / `F-*-T`).
- Updated defaults:
  - `Core/Config.cs`: `AllowMultiQuarterDispositions` default set to `false`.
  - `Core/AtsBuildWindow.cs` and `Core/AtsBuildForm.cs`: `1/4 Definition` UI default now falls back to `false`.
  - `Core/AtsBuildForm.cs`: `AtsBuildInput.AllowMultiQuarterDispositions` default set to `false`.
- Verification:
  - `dotnet build` reached compile/link successfully but failed final copy to `build\net8.0-windows` due file lock/access denied on `AtsBackgroundBuilder.dll/.pdb`.
  - `dotnet msbuild src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj /t:Compile /p:Configuration=Release` succeeded (0 errors; existing nullable warnings only).

## Follow-up (PLSR Review Window + AB_LCON Shape Set, 2026-02-28)

- [x] Include `AB_LCON.shp` in default disposition shapefile list.
- [x] Ensure older config files inherit newly added default disposition shapefiles without dropping existing user entries.
- [x] Add a PLSR review window listing results with per-row `Accept/Ignore` decisions.
- [x] Add `Accept All` and `Ignore All` controls in the PLSR review window.
- [x] Apply only accepted actionable PLSR results (`owner mismatch`, `expired`) and leave ignored rows unchanged.
- [x] Rebuild Release and verify output artifact paths.

## Review (PLSR Review Window + AB_LCON Shape Set, 2026-02-28)

- Updated `Core/Config.cs`:
  - `DispositionShapefiles` default now includes both `DAB_APPL.shp` and `AB_LCON.shp`.
  - Added merge logic so loaded configs keep user values and also inherit newly introduced default shape names.
- Updated `Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - Added interactive PLSR review form with per-result `Decision` (`Accept`/`Ignore`), `Accept All`, and `Ignore All`.
  - Applied accepted actionable changes in a transaction:
    - Owner correction for owner mismatches.
    - `(Expired)` tagging for expired PLSR entries.
  - Ignored rows are explicitly skipped.
- Verification:
  - `.\.local_dotnet\dotnet.exe msbuild src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj /t:Compile /p:Configuration=Release` succeeded.
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - Artifacts:
    - `src\AtsBackgroundBuilder\bin\Release\net8.0-windows\AtsBackgroundBuilder.dll`
    - `build\net8.0-windows\AtsBackgroundBuilder.dll`

## Follow-up (PLSR False Missing Labels, 2026-02-28)

- [x] Inspect reference screenshots and trace why existing labels were still reported as missing.
- [x] Fix PLSR label parser to extract `dispNum` from any label line (not only the last line).
- [x] Preserve support for labels with trailing `(Expired)` or additional note lines.
- [x] Recompile to verify compile safety after parser fix.

## Review (PLSR False Missing Labels, 2026-02-28)

- Root cause: `CollectPlsrLabels -> BuildLabelEntry` used `lines.LastOrDefault()` as `dispNum`.
  - Labels ending with `(Expired)` (or other trailing notes) were parsed as disp=`(Expired)` and filtered out.
  - This produced false `Missing label` rows even when the label existed in the drawing.
- Fix in `Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - Added `ExtractDispositionNumber(...)` and `TryParseDispositionNumberFromText(...)`.
  - Parser now scans label lines from bottom to top and extracts known disposition prefixes + numeric suffix (`LOC`, `PLA`, `MSL`, etc.).
  - Falls back to raw contents scan when needed.
- Verification:
  - `.\.local_dotnet\dotnet.exe msbuild src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj /t:Compile /p:Configuration=Release`
  - Result: success (0 errors, existing nullable warnings only).

## Follow-up (PLSR MText Formatting Codes, 2026-02-28)

- [x] Remove AutoCAD MText control codes (example `\A1;`) from PLSR owner/disp parsing.
- [x] Ensure formatting codes do not trigger false owner mismatch or dirty current-value display.
- [x] Recompile to verify compile safety after formatting cleanup.

## Review (PLSR MText Formatting Codes, 2026-02-28)

- Added `StripMTextControlCodes(...)` in `Dispositions/Plugin.Dispositions.LabelingPlsr.cs`.
- Wired cleanup into:
  - `SplitMTextLines(...)` so parsed owner/disp lines are plain text.
  - `NormalizeOwner(...)` so owner comparison ignores inline formatting commands.
  - `TryParseDispositionNumberFromText(...)` so disp extraction works even if line contains formatting tags.
- Verification:
  - `.\.local_dotnet\dotnet.exe msbuild src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj /t:Compile /p:Configuration=Release`
  - Result: success (0 errors, existing nullable warnings only).

## Follow-up (PLSR Quarter-Level Disp Display, 2026-02-28)

- [x] Clarify `Disp` cell for `Missing quarter in XML` results so it does not appear as dropped/missing disp data.
- [x] Recompile to verify compile safety after display tweak.

## Review (PLSR Quarter-Level Disp Display, 2026-02-28)

- Updated `Missing quarter in XML` issue creation to set `DispNum = "N/A"` instead of empty string.
- This makes it clear the row is quarter-level (no per-disposition payload available from XML), not a parser failure.
- Verification:
  - `.\.local_dotnet\dotnet.exe msbuild src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj /t:Compile /p:Configuration=Release`
  - Result: success (0 errors, existing nullable warnings only).

## Follow-up (PLSR Remaining MText Artifact Codes, 2026-02-28)

- [x] Inspect latest screenshot showing residual raw control codes in PLSR review (`\pxqc;`, `\P`).
- [x] Expand MText sanitizer to strip paragraph-style control groups in addition to prior alignment/font controls.
- [x] Convert expired-row `Current` display from raw `MText.Contents` to flattened plain text.
- [x] Recompile to verify compile safety after sanitation/display updates.

## Review (PLSR Remaining MText Artifact Codes, 2026-02-28)

- Root cause:
  - Some labels include paragraph control groups like `\pxqc;` that were not in the original sanitizer pattern.
  - Expired rows displayed raw `label.RawContents`, which intentionally includes markup (`\P`, etc.).
- Fixes in `Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - `StripMTextControlCodes(...)` now strips generic semicolon-terminated control groups and remaining one-letter controls.
  - Added `FlattenMTextForDisplay(...)` and used it for expired-row `CurrentValue`.
- Verification:
  - `.\.local_dotnet\dotnet.exe msbuild src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj /t:Compile /p:Configuration=Release`
  - Result: success (0 errors, existing nullable warnings only).

## Follow-up (PLSR Re-Tagging Already Expired Labels, 2026-02-28)

- [x] Inspect latest screenshot where `Current` already contains `(Expired)` but row still proposes `Add (Expired)`.
- [x] Gate expired issue creation so rows are only actionable when the label does not already contain an expired marker.
- [x] Reuse the same expired-marker detector in apply path to avoid duplicate tag append variants.
- [x] Rebuild/compile to verify safety.

## Review (PLSR Re-Tagging Already Expired Labels, 2026-02-28)

- Added `HasExpiredMarker(...)` in `Dispositions/Plugin.Dispositions.LabelingPlsr.cs`.
- `Expired in PLSR` issue creation now checks `HasExpiredMarker(label.RawContents)` and skips actionable rows when marker already exists.
- `TryApplyExpiredMarker(...)` now also uses `HasExpiredMarker(...)` (instead of only searching literal `(Expired)` in raw contents), preventing format-variant duplicates.
- Verification:
  - `.\.local_dotnet\dotnet.exe msbuild src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj /t:Compile /p:Configuration=Release` succeeded.
  - `.\.local_dotnet\dotnet.exe build ... --no-restore` compiled but failed final copy to `build\net8.0-windows\AtsBackgroundBuilder.dll` due file lock by another process.

## Follow-up (PLSR Fix Not Loaded Due Stale Build DLL, 2026-02-28)

- [x] Verify whether runtime was loading stale `build\net8.0-windows` artifact instead of newly compiled `bin` artifact.
- [x] Sync `build\net8.0-windows\AtsBackgroundBuilder.dll` to latest `bin` output after lock cleared.
- [x] Re-run Release build and verify artifact parity.

## Review (PLSR Fix Not Loaded Due Stale Build DLL, 2026-02-28)

- Confirmed mismatch before sync:
  - `bin\...\AtsBackgroundBuilder.dll` newer/larger (`23:22:33`, `863232` bytes)
  - `build\...\AtsBackgroundBuilder.dll` older/smaller (`23:14:25`, `862720` bytes)
- Manually copied updated DLL from `bin` to `build`.
- Post-sync verification:
  - both `bin` and `build` DLLs now match (`23:22:33`, `863232` bytes).
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
