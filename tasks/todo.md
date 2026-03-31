# Follow-up (Disposition Label Placement Review Export, 2026-03-31)

- [x] Review `tasks/lessons.md` and locate the disposition-label code paths that control placement, reuse, and PLSR interaction.
- [x] Identify the primary placement engine plus the upstream config/orchestration code that decides when it runs.
- [x] Generate a consolidated `.txt` review file with the relevant source and line-numbered excerpts for placement logic.
- [x] Verify the review file contains the key placement paths and document the outcome.

## Review (Disposition Label Placement Review Export, 2026-03-31)

- Generated `output/disposition-label-placement-review.txt` with line-numbered source for the main placement engine `src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs`, plus supporting config/orchestration excerpts from `src/AtsBackgroundBuilder/Core/BuildExecutionPlan.cs`, `src/AtsBackgroundBuilder/Core/Config.cs`, `src/AtsBackgroundBuilder/Core/DispositionLabelColorPolicy.cs`, `src/AtsBackgroundBuilder/Core/Plugin.cs`, `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`, and targeted tests in `src/AtsBackgroundBuilder.DecisionTests/Program.cs`.
- Verified the export exists at `output/disposition-label-placement-review.txt`, is non-empty (`264034` bytes / `5328` lines), and includes the key sections for the placement call site, the full `LabelPlacer` implementation, and the PLSR existing-label scan / missing-label recreation paths.
- Placement summary for quick review: `BuildExecutionPlan.ShouldPlaceLabelsBeforePlsr` gates the pre-PLSR run, `Plugin.ExecutePostQuarterPipeline(...)` invokes `LabelPlacer.PlaceLabels(...)`, `ProcessDispositionPolylines(...)` decides whether a disposition becomes width/aligned-dimension, leader, or plain-text label, and `LabelPlacer` ranks candidate points by collision overlap, disposition linework overlap, nearby-label density, and distance from the search target before optionally forcing the best fallback when overlap-free placement fails.

# Follow-up (64-3-6 Top Vertical Hard Stop, 2026-03-26)

- [x] Reproduce the `64-3-6` vertical overshoot from `406073.455,6050001.730 -> 406075.509,6050101.134` and identify the emitted layer plus the intended hard-stop target at `406073.871,6050021.834`.
- [x] Trace the shared endpoint-enforcement path that should clamp this northbound section/road-allowance line to the nearby hard-row endpoint instead of extending to the next segment north.
- [x] Implement the smallest shared fix, avoiding township-specific fallbacks, and rerun build/tests plus the AutoCAD/DXF verification.

## Review (64-3-6 Top Vertical Hard Stop, 2026-03-26)

- Root cause: the reported line was the live `L-USEC-0` vertical `406073.455116,6050001.729900 -> 406075.509360,6050101.133613`, and the correct stop `406073.870587,6050021.834292` was the east endpoint of a short `L-SEC` hard-boundary row. The shared no-crossing terminator pass was still too strict in two ways: it only treated same-band terminators as valid for `0/20` sources, and its segment intersection helper rejected endpoint hits when floating-point math produced a parameter like `1.0000000000009763` instead of exactly `1.0`.
- Fix: in `src/AtsBackgroundBuilder/Core/Plugin.cs`, broadened the `0/20` terminator rule so `0/20` lines can stop on any non-`30.18` section-type terminator endpoint, and hardened the three local `TryIntersectSegments(...)` helpers with a small segment-parameter epsilon plus `[0,1]` clamping. This keeps the rule generic and lets endpoint-based hard stops survive exact-endpoint floating-point noise instead of forcing township-specific fallback logic.
- Verification:
  - `dotnet build "src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj" -c Release -p:Platform=x64 -p:NuGetAudit=false -o "artifacts/verify-build/twp64-3-6-top-ra-v4"` passed.
  - `dotnet build "src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj" -c Release -p:Platform=x64 -p:NuGetAudit=false -o "artifacts/verify-build/twp64-3-6-top-ra-v4-tests"` passed.
  - `dotnet "artifacts/verify-build/twp64-3-6-top-ra-v4-tests/AtsBackgroundBuilder.DecisionTests.dll"` passed.
  - `scripts/atsbuild_harness.ps1` with `data/twp64-3-6-top-ra-spec.json` and `data/twp64-3-6-top-ra-review.json` passed in `data/twp64-3-6-top-ra-run-rerun6/artifacts/review-report.json`.
  - The exact reported vertical now emits as `L-USEC-0` `406073.870587,6050021.834292 -> 406075.509360,6050101.133613`, matching the user target within review tolerance.
  - The fresh run log in `data/twp64-3-6-top-ra-run-rerun6/artifacts/AtsBackgroundBuilder.run.log` now records `no-crossing enforcement adjusted 0/20=5 ... [terminator=5]` and later `0/20=11 ... [terminator=11]`, confirming the shared terminator rule now catches these hard-stop endpoint cases.

# Follow-up (64-3-6 Top Road-Allowance Layer Misclassification, 2026-03-26)

- [x] Reproduce the `64-3-6` top road-allowance layer misclassification from the user-supplied segment and identify the emitted layer in the latest run output.
- [x] Trace the shared top-edge road-allowance classification/reapply path that decides whether these north-edge segments stay `L-SEC` or get relabeled to another `L-USEC*` layer.
- [x] Implement the smallest shared fix, avoiding township-specific fallbacks, so top road-allowance segments that match the original section-edge authority remain `L-SEC`.
- [x] Build, run decision tests, and run a focused AutoCAD/DXF verification for the exact `64-3-6` top-edge case.
- [x] Reproduce the new `64-3-6` vertical overshoot `406073.455,6050001.730 -> 406075.509,6050101.134` and identify the exact emitted layer plus intended hard stop at `406073.871,6050021.834`.
- [x] Trace the shared north-end endpoint-enforcement path that should stop this line on the correct top hard row instead of extending through it.
- [x] Implement the smallest shared endpoint fix, then rebuild and rerun the focused `64-3-6` AutoCAD/DXF checks.

## Review (64-3-6 Top Road-Allowance Layer Misclassification, 2026-03-26)

- Root cause: the shared baseline township normalizer in `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.Normalization.cs` already handles top-township rows (`township % 4 == 0`, sections `31..36`), but its candidate filter excluded `L-USEC3018`. In the reproduced `64-3-6` output, the exact bad top road-allowance segment `406104.027,6050021.238 -> 406897.098,6050005.556` was still on `L-USEC3018`, so the baseline-hint pass never evaluated it and the later deterministic relayer left it there. The same pattern also hit the adjacent top segment `407730.373,6049989.078 -> 408523.453,6049973.380`.
- Fix: widened the local seam candidate filter in `NormalizeBottomTownshipBoundaryLayers(...)` so the explicit top/bottom baseline-hint pass can promote `L-USEC3018` rows to `L-SEC` when the geometry proves they are the surveyed baseline row. This is still a shared geometric rule, not a township-specific fallback. I also renamed the pass logs from `bottom-township` to `baseline-township` to match what the code actually does.
- Verification:
  - `dotnet build "src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj" -c Release -p:Platform=x64 -p:NuGetAudit=false -o "artifacts/verify-build/twp64-3-6-top-ra"` passed.
  - `dotnet build "src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj" -c Release -p:Platform=x64 -p:NuGetAudit=false -o "artifacts/verify-build/twp64-3-6-top-ra-tests"` passed.
  - `dotnet "artifacts/verify-build/twp64-3-6-top-ra-tests/AtsBackgroundBuilder.DecisionTests.dll"` passed.
  - `scripts/atsbuild_harness.ps1` with `data/twp64-3-6-top-ra-spec.json` and `data/twp64-3-6-top-ra-review.json` passed in `data/twp64-3-6-top-ra-run-rerun3/artifacts/review-report.json`.
  - The exact reported segments now emit on `L-SEC` in the DXF review:
    - `406093.977,6050021.436 -> 406897.098,6050005.556` matched the user target within the configured endpoint tolerance and passed as `L-SEC`.
    - `407720.323,6049989.277 -> 408523.453,6049973.380` likewise passed as `L-SEC`.
  - The fresh run log in `data/twp64-3-6-top-ra-run-rerun3/artifacts/AtsBackgroundBuilder.run.log` records `normalized 32 baseline-township seam segment(s)` and `baseline-township rule forced 32 baseline-township seam segment(s) to L-SEC`, confirming the shared top/bottom baseline pass now catches these rows.

# Follow-up (63-12-5 Correction-Line Endpoints + 76-11-6 RA Layer Classification, final, 2026-03-26)

- [x] Reproduce the new `63-12-5` short `L-SEC` and `L-USEC2012` correction-line endpoints from the user-supplied coordinates and map them to emitted ATS entities/layers.
- [x] Identify the `76-11-6` late road-allowance relayer path that was still synthesizing `L-SEC` output without original range-edge anchors.
- [x] Implement the smallest shared fix so the range-edge `L-SEC` reapply restores only true original anchor-backed classification, while keeping the verified `63-12-5` correction-line endpoint fix intact.
- [x] Build, run decision tests, rerun the exact `63-12-5` AutoCAD review, and rerun the focused `76-11-6` township build/DXF comparison.

## Review (63-12-5 Correction-Line Endpoints + 76-11-6 RA Layer Classification, final, 2026-03-26)

- Root cause: the `63-12-5` correction-line miss was already fixed by the earlier shared correction-zero companion targeting change, but `76-11-6` still had a late cleanup relayer problem. `ReapplyOriginalRangeEdgeSecRoadAllowanceLayers(...)` was designed to restore original range-edge `L-SEC` classification after deterministic relayer cleanup, yet the pass still had synthetic fallback logic that could promote live `L-USEC*` rows to `L-SEC` even when no original range-edge `L-SEC` anchors had been captured for the township. That made the final cleanup non-authoritative and produced wrong layer output in `76-11-6`.
- Fix: in `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.Normalization.cs`, reduced `ReapplyOriginalRangeEdgeSecRoadAllowanceLayers(...)` to true anchor-backed reapply only. The pass now exits when the original anchor snapshot is empty, and it no longer synthesizes new `L-SEC` reclassifications from companion geometry or the old range-edge `2012 -> SEC` shortcut. Generated segments are only eligible when they actually match an original captured anchor corridor.
- Verification:
  - `dotnet build "src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj" -c Release -p:Platform=x64 -p:NuGetAudit=false -o "artifacts/verify-build/final-63-76"` passed.
  - `dotnet run --project "src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj" -c Release --no-restore -p:Platform=x64 -p:NuGetAudit=false` passed.
  - `scripts/atsbuild_harness.ps1` with `data/twp63-12-5-correctionline-spec.json` and `data/twp63-12-5-correctionline-followup-review.json` passed in `data/twp63-12-5-correctionline-followup-run-main-rerun4/artifacts/review-report.json`, with `L-USEC-0` endpoint `579391.979706,6030280.202022` and `L-USEC2012` endpoint `579412.086401,6030280.566849`.
  - `scripts/atsbuild_harness.ps1` with `data/twp76-11-6-layer-spec.json` completed in `data/twp76-11-6-layer-run-main-rerun4`, and `data/twp76-11-6-layer-run-main-rerun4/artifacts/AtsBackgroundBuilder.run.log` now records `skipped range-edge L-SEC reapply because no original range-edge L-SEC anchors were captured`.
  - Old-vs-new `76-11-6` DXF comparison shows the late wrong relabels are gone: `L-SEC` counts dropped from `H/V = 38/32` to `27/28`, while `L-USEC-0` rose from `43/92` to `54/94`. Example corrected relabels include `L-SEC -> L-USEC-0` at `339013.556,6158725.693 -> 339016.091,6158795.545` and `339391.223,6168499.860 -> 339471.123,6168497.020`.

# Follow-up (63-12-5 Correction-Line Endpoints + 76-11-6 RA Layer Classification, 2026-03-26)

- [x] Reproduce the new `63-12-5` section/`L-USEC2012` endpoint misses from the user-supplied coordinates and map them to the exact emitted ATS entities/layers.
- [x] Trace the shared `76-11-6` road-allowance misclassification to the final emitted layer-classification path instead of the earlier normalization logs.
- [x] Implement the smallest shared fix without section-specific fallbacks.
- [x] Build, run decision tests, and rerun focused AutoCAD harness/DXF comparisons for the exact `63-12-5` checks and the `76-11-6` classification case.

## Review (63-12-5 Correction-Line Endpoints + 76-11-6 RA Layer Classification, 2026-03-26)

- Root cause: `63-12-5` was already fixed by the earlier correction-zero companion/offset work, but `76-11-6` still had a live final-DXF layer regression. The real culprit was not the earlier bottom-township normalization logs; it was the later `ReapplyOriginalRangeEdgeSecRoadAllowanceLayers(...)` pass in `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.Normalization.cs`. In the `76-11-6` run, the log shows `snapshot captured 0 original range-edge L-SEC anchor(s)` and then immediately `reapplied original range-edge L-SEC classification to 20 segment(s)`, which means the pass was fabricating new `L-SEC` range-edge relabels even though there were no original `L-SEC` anchors to restore.
- Fix: guard `ReapplyOriginalRangeEdgeSecRoadAllowanceLayers(...)` so it exits when no original range-edge `L-SEC` anchors were captured. That keeps the pass aligned with its actual contract, lets the deterministic section-edge relayer remain authoritative, and avoids another heuristic overwrite after final classification.
- Verification:
  - `dotnet build "src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj" -c Release -p:Platform=x64 -p:NuGetAudit=false -o "artifacts/verify-build/final-63-76-fix"` passed.
  - `dotnet build "src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj" -c Release -p:Platform=x64 -p:NuGetAudit=false -o "artifacts/verify-build/final-63-76-tests"` passed.
  - `dotnet "artifacts/verify-build/final-63-76-tests/AtsBackgroundBuilder.DecisionTests.dll"` passed.
  - `scripts/atsbuild_harness.ps1` with `data/twp63-12-5-correctionline-spec.json` and `data/twp63-12-5-correctionline-followup-review.json` passed in `data/twp63-12-5-correctionline-followup-run-main-rerun3/artifacts/review-report.json`, with the emitted endpoints still at `579391.979706, 6030280.202022` on `L-USEC-0` and `579412.086401, 6030280.566849` on `L-USEC2012`.
  - `scripts/atsbuild_harness.ps1` with `data/twp76-11-6-layer-spec.json` completed in `data/twp76-11-6-layer-run-main-rerun3`. The fresh run log now records `skipped range-edge L-SEC reapply because no original range-edge L-SEC anchors were captured` twice instead of relabeling 20 segments after the deterministic relayer.
  - Comparing `data/twp76-11-6-layer-run-rerun1/artifacts/output.dxf` to `data/twp76-11-6-layer-run-main-rerun3/artifacts/output.dxf` shows the final DXF no longer force-promotes the affected range-edge rows back to `L-SEC`. Representative changes include `L-SEC -> L-USEC-0` at `339013.556,6158725.693 -> 339016.091,6158795.545`, `339016.826,6158815.641 -> 339046.187,6159619.580`, and the matching east-edge/horizontal band relabels around `6168498.440` through `6168872.927`.

# Follow-up (Correction-Line Section Building Regressions: 59-18-5 and 63-12-5, 2026-03-26)

- [x] Reproduce the `59-18-5` and `63-12-5` misses from the user-supplied coordinates and map them to the exact emitted ATS entities/layers.
- [x] Trace the shared correction-line section-building path that leaves the `59-18-5` `L-USEC-2012` endpoint short and the `63-12-5` line un-offset.
- [x] Implement the smallest shared fix, not a section-specific fallback, for both correction-line section-building failures.
- [x] Build, run decision tests, and run focused AutoCAD harness/DXF comparisons for both exact user cases.

## Review (Correction-Line Section Building Regressions: 59-18-5 and 63-12-5, 2026-03-26)

- Root cause: there were two related but not identical correction-line section-building misses. For `59-18-5`, the shared correction-zero companion projection was still allowed to prefer a faraway parallel `L-USEC-C-0` trend because it ranked infinite-line intersections by raw shift alone; that left the `L-USEC2012` endpoint at `519551.434, 5990794.880` instead of the local correction-row hit. For `63-12-5`, the local correction-zero row was already present at `582636.216, 6030344.187`, but the later vertical target adjustment on `L-SEC` was being blocked by the strict north/south direction guard that had been tightened for the `L-USEC2012` case, so the section line stayed at the un-offset endpoint `582636.307, 6030339.169`.
- Fix: in `src/AtsBackgroundBuilder/Core/CorrectionZeroCompanionProjection.cs`, changed companion-row selection to prefer the locally relevant finite correction-row trend by ranking candidates on finite-segment proximity plus exact inset fit before the old along-direction tie-break. In `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.CorrectionLinePostProcessing.cs`, kept the stricter direction guard for `L-USEC*` targets but allowed a narrow bypass for local `L-SEC` correction-inset moves (`<= 6.02m`), which restores the proper `L-SEC` endpoint snap to the already selected local `L-USEC-C-0` row without reopening the `59-18-5` regression.
- Verification:
  - `dotnet build "src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj" -c Release -p:Platform=x64 -p:NuGetAudit=false -o "out/corrfix-plugin"` passed.
  - `dotnet build "src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj" -c Release -p:NuGetAudit=false -o "out/corrfix-tests"` passed.
  - `dotnet out/corrfix-tests/AtsBackgroundBuilder.DecisionTests.dll` passed.
  - `scripts/atsbuild_harness.ps1` with `data/twp59-18-5-correctionline-spec.json` and `data/twp59-18-5-correctionline-review.json` passed in `data/twp59-18-5-correctionline-run-rerun5/artifacts/review-report.json`, with the emitted `L-USEC2012` endpoint at `519551.434986, 5990794.710737`.
  - `scripts/atsbuild_harness.ps1` with `data/twp63-12-5-correctionline-spec.json` and the corrected `data/twp63-12-5-correctionline-review.json` passed in `data/twp63-12-5-correctionline-run-rerun4/artifacts/review-report.json`, with the emitted `L-SEC` endpoint at `582636.215955, 6030344.187046`.

# Follow-up (Dispositions-Only Run Erased Existing Section Fabric, 2026-03-26)

- [x] Identify the exact ATSBUILD run/log that matches the user's dispositions-only report and confirm which DLL timestamp it loaded.
- [x] Trace the cleanup path that ran after label placement and prove whether it erased section/quarter geometry in the current build.
- [x] Record whether this was a stale-build issue or a current cleanup-policy bug, plus the next fix direction.
- [x] Implement the cleanup fix so dispositions-only runs preserve preexisting `L-QUATER` section fabric while still cleaning up this run's temporary quarter-view output.
- [x] Rebuild and run the decision tests to confirm the patch compiles cleanly and does not regress the existing ATS logic paths.

## Review (Dispositions-Only Run Erased Existing Section Fabric, 2026-03-26)

- Root cause: this was not an old build. The reported run loaded `build\net8.0-windows\AtsBackgroundBuilder.dll`, and the log records that exact assembly at `2026-03-25 12:06:21 PM`. The user-visible destructive path was the `1/4 definition quarter view` cleanup: `CleanupAfterBuild(...)` erased every `L-QUATER` entity inside the requested section windows, without distinguishing preexisting quarter-view fabric from quarter-view generated during the current run. In the same run, cleanup also removed the temporary generated ATS helper/section IDs because `CleanupPlan.Create(...)` maps `!input.IncludeAtsFabric` to `EraseQuarterHelpers`, `EraseSectionOutlines`, `EraseContextSectionPieces`, and `EraseSectionLabels`.
- Evidence: `build\net8.0-windows\AtsBackgroundBuilder.log` shows `ATSBUILD assembly: C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\build\net8.0-windows\AtsBackgroundBuilder.dll` at line `26394`, then the cleanup erasures at lines `26997`-`27002`. The source in `src/AtsBackgroundBuilder\Diagnostics\Plugin.Diagnostics.CleanupDiagnostics.cs` previously erased quarter view by layer+window rather than by generated ID set, which is why prior `L-QUATER` entities were vulnerable during a dispositions-only run.
- Fix: snapshot any preexisting `L-QUATER` entity IDs in the requested cleanup windows before ATS draws the new run, carry those IDs in `SectionDrawResult`, and have `CleanupAfterBuild(...)` skip those preserved IDs when erasing quarter-view output. This keeps the current run's temporary quarter-view removable while leaving the user's earlier quarter-view/section fabric intact.
- Follow-up fix: the first patch protected preexisting `L-QUATER`, but the user then reported that existing magenta correction-line fabric was still being deleted. The remaining issue was restore timing: with `ATS Fabric` off, `FinalizeRoadAllowanceCleanup(...)` restored stashed existing section/correction entities before `ApplyCorrectionLinePostBuildRules(...)` and the late correction cleanup passes, so those generated-geometry passes could still prune or relayer the restored existing correction rows. The fix now delays restoring stashed existing section-building geometry until after the generated correction-line cleanup is complete whenever `ATS Fabric` is off.
- Final cleanup gap: even after preserving/restoring existing geometry correctly, ATS-fabric-off runs were still leaving behind the current run's generated road-allowance/correction-line entities because `CleanupAfterBuild(...)` never received or erased the `GeneratedRoadAllowanceIds` set. The fix now carries those generated IDs through `SectionDrawResult` and erases them during final cleanup whenever `ATS Fabric` is off, so the run preserves old magenta correction lines but does not leave new temporary correction-line output behind.
- Late-created correction-row gap: the user still reported leftover `L-USEC-C-0` lines after the previous patch because some correction rows were being created or relayered after the original `GeneratedRoadAllowanceIds` list was formed. The fix now rescans the live `L-USEC*`/`L-USEC-C*` layers inside the requested isolation windows just before restored geometry comes back, adds any remaining generated road-allowance/correction entities to the cleanup ID set, and then lets `CleanupAfterBuild(...)` erase them.
- Verification:
  - `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release -p:Platform=x64 -p:NuGetAudit=false` passed.
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -p:NuGetAudit=false` passed.
  - I did not rerun the exact AutoCAD UI repro, because the problematic run came from the live ATSBUILD dialog and there is no saved workbook/artifact set for that exact dispositions-only scenario in the repo.

# Follow-up (Section 18 Regular RA Midpoints After Rule-Matrix, 2026-03-19)

- [x] Reproduce the two new section 18 SW `TWENTY/SEC` misses from the user-supplied coordinates and map them to the exact emitted `L-SECTION-LSD` entities.
- [x] Prove whether the rule-matrix is still choosing the wrong owner rows or is stopping at projected station points on the correct live `L-USEC2012` rows.
- [x] Implement one shared fix so final regular road-allowance/section-boundary LSD endpoints normalize to the owner-row midpoint after rule-matrix selection, without adding another section-specific fallback.
- [x] Rebuild, rerun decision tests, and rerun the AutoCAD harness with a corrected focused review config that checks the actual emitted LSD endpoints.

## Review (Section 18 Regular RA Midpoints After Rule-Matrix, 2026-03-19)

- Root cause: both new section 18 SW misses were already terminating on the correct live `L-USEC2012` road-allowance rows, but the rule-matrix stopped at projected station points on those rows (`510139.660, 5994010.570` and `509730.190, 5994416.389`) instead of the exact row midpoints. The existing regular-boundary midpoint logic only ran before the rule-matrix, so once the rule-matrix moved an outer endpoint onto a regular `TWENTY/SEC` row there was no shared final pass to normalize it.
- Fix: added `EnforceRegularBoundaryLsdMidpointsAfterRuleMatrix(...)` in `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.EndpointEnforcement.cs` and call it immediately after a successful rule-matrix run. The post-pass scans final `L-SECTION-LSD` endpoints already touching live non-correction `L-USEC/L-USEC2012/L-SEC` rows and snaps them to the exact regular boundary midpoint, which fixes the whole path consistently instead of patching section 18 alone. Added `data/twp59-19-5-new-misses-review-rerun2.json` so the harness verifies the corrected LSD endpoints directly.
- Verification:
  - `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release -p:Platform=x64 -p:NuGetAudit=false` passed.
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -p:NuGetAudit=false` passed.
  - `scripts/atsbuild_harness.ps1` with `data/twp59-19-5-all-sections-spec.json` and `data/twp59-19-5-new-misses-review-rerun2.json` passed in `data/twp59-19-5-all-sections-run-rerun3/artifacts/review-report.json`.
  - The fresh run reports section 18 SW endpoints at `510134.619, 5994010.554` and `509730.201, 5994411.348` on `L-SECTION-LSD`, matching the user-supplied targets within tolerance.

# Follow-up (Section 2 SW LSD + Section 17 SE L-USEC2012 RA Stops, 2026-03-19)

- [x] Reproduce the new `2-59-19-5` SW LSD miss and the `17-59-19-5` SE `L-USEC2012` overrun from the user-supplied coordinates.
- [x] Trace whether the section 2 miss is still on the wrong correction row or is now a same-row station-vs-midpoint issue, and isolate why the section 17 overrun survives the generic final 0/20 cleanup.
- [x] Implement the smallest endpoint-enforcement and final 0/20 cleanup changes that stop the SW LSD at `516687.634, 5990782.885` and stop the SE `L-USEC2012` endpoint at `512979.659, 5994019.466`.
- [x] Rebuild, rerun decision tests, and rerun the AutoCAD harness checker with a focused review config for both exact user points.

## Review (Section 2 SW LSD + Section 17 SE L-USEC2012 RA Stops, 2026-03-19)

- Root cause: the section 2 SW vertical LSD was no longer on the wrong south correction row after the earlier fixes, but the generic `CORRZERO/SEC` station projection still landed a couple of meters off the intended quarter midpoint on the correct inset row. Separately, the section 17 SE `L-USEC2012` overrun was not a dangling-end case: the bad endpoint was already connected to the adjacent `2012` chain, so the generic final `0/20` overhang pass refused to trim it inward to the opposite-band zero stop.
- Fix: in `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.EndpointEnforcement.cs`, kept the generic station-ranked south-quarter correction-zero selection, then added a tightly scoped same-row midpoint snap so a vertical LSD already sitting on a live inset `L-USEC-C-0` row can finish at that row's quarter midpoint. In `src/AtsBackgroundBuilder/Core/Plugin.cs`, updated the final `TrimZeroTwentyPassThroughExtensions(...)` pass so a connected `0/20` endpoint can still trim inward when the true stop is an interior crossing on the opposite `0/20` band, which is what section 17 needed.
- Verification:
  - `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release -p:Platform=x64 -p:NuGetAudit=false` passed.
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -p:NuGetAudit=false` passed.
  - `scripts/atsbuild_harness.ps1` with `data/section2-17-followup-input.xlsx` and `data/section2-17-followup-review-rerun7.json` passed in `data/section2-17-followup-run-rerun8/artifacts/review-report.json`.
  - The fresh run reports the SW LSD endpoint at `516687.634, 5990782.885` on `L-SECTION-LSD`, and the SE stop at `512979.659, 5994019.466` on `L-USEC2012`, both within tolerance of the user-supplied targets.

# Follow-up (Corrected Quarter Divider + LSD Targets For 6-59-19-5, 2026-03-19)

- [x] Reproduce the user-corrected `L-QSEC` divider endpoint at `510545.768, 5990763.599` and the SW vertical LSD endpoint at `510141.927, 5990762.348`.
- [x] Trace which recent correction-line changes moved the center `L-QSEC` divider onto the lower RA row and why the SW LSD outer target stayed about `5.03m` too far east.
- [x] Implement the minimal fix so the in-drawing `L-QSEC` divider stays on the upper inset correction row and the SW vertical LSD resolves off the corrected quarter station.
- [x] Rebuild and rerun the exact AutoCAD checker against the user-corrected points.

## Review (Corrected Quarter Divider + LSD Targets For 6-59-19-5, 2026-03-19)

- Root cause: the previous exact-case fix overreached. It started rebuilding in-frame `L-QSEC` linework from the lower road-allowance south-mid, which pulled the center divider through the road allowance and shifted the SW quarter's live `L-QSEC` context. Separately, the SW vertical LSD `CORRZERO` outer target was still being projected from the stale outer endpoint station instead of the inner quarter station, leaving it about `5.03m` too far east on the skewed inset correction row.
- Fix: removed the aggressive in-frame `L-QSEC` redraw path so the existing divider and midline remain authoritative, and updated `TryFindBoundaryStationTarget(...)` in `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.EndpointEnforcement.cs` so vertical `CORRZERO` candidates project from the inner endpoint station instead of the old outer endpoint station. The quarter-specific correction-zero resolver now wins and logs `outer source=corrzero-resolved` for the SW vertical LSD.
- Compatibility note: kept the lower hard-boundary south selector in place for the other correction-line path; this change only stops the divider/midline from being forcibly rebuilt onto that lower node and tightens the LSD `CORRZERO` projection reference.
- Verification:
  - `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release -p:Platform=x64 -p:NuGetAudit=false` passed.
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -p:NuGetAudit=false` passed.
  - `scripts/atsbuild_harness.ps1` with `data/quarter-correctionline-input-6-59-19-5.xlsx` and `data/quarter-correctionline-review-6-59-19-5-user-corrections.json` passed in `data/quarter-correctionline-run-6-59-19-5-rerun14/artifacts/review-report.json`.
  - The fresh run reports `L-QSEC` endpoint `510545.764,5990763.599` and SW vertical LSD endpoint `510141.927,5990762.348`, matching the user-corrected targets within tolerance.

# Follow-up (Quarter Divider + LSD Crossing RA In 6-59-19-5, 2026-03-19)

- [x] Map the user's new `L-QSEC` divider endpoint miss and the linked `L-SECTION-LSD` endpoint miss to the exact emitted entities in the `6-59-19-5` repro.
- [x] Separate the quarter south-edge rule from the quarter divider/LSD midpoint rule so the divider no longer crosses the road allowance while the south edge keeps its intended geometry.
- [x] Implement the minimal fix for the `L-QSEC` divider south stop and the south-quarter vertical LSD correction-zero midpoint selection.
- [x] Rebuild, rerun decision tests, and rerun the AutoCAD harness against the exact follow-up coordinates.

## Review (Quarter Divider + LSD Crossing RA In 6-59-19-5, 2026-03-19)

- Root cause: the follow-up regression was not the SW quarter south edge itself. The redrawn `L-QSEC` vertical divider was being rebuilt to the lower road-allowance south-mid point (`510545.814, 5990742.205`), which made that 1/4 line cross the road allowance. Then the south-west vertical LSD outer endpoint used the generic correction-row station projection, landing at `510146.954, 5990762.364` instead of the midpoint of the qsec-anchored inner correction row.
- Fix: preserved the existing `L-QSEC` divider's own south endpoint when rebuilding quarter-definition linework in `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs`, so the divider now stops at `510545.768, 5990763.599`. In `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.EndpointEnforcement.cs`, added a qsec-anchored correction-zero midpoint target for south-quarter vertical LSDs and let that outrank the generic station-projection path.
- Verification:
  - `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release -p:Platform=x64 -p:NuGetAudit=false` passed.
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -p:NuGetAudit=false` passed.
  - `scripts/atsbuild_harness.ps1` with `data/quarter-correctionline-review-6-59-19-5-followup.json` passed in `data/quarter-correctionline-run-6-59-19-5-rerun12/artifacts/review-report.json`.
  - Harness output now matches `L-QSEC` endpoint `510545.764, 5990763.599` and `L-SECTION-LSD` endpoint `510141.927, 5990762.348`, matching the user's requested targets within tolerance.

# Follow-up (Exact SW Quarter RA Corners For 6-59-19-5, 2026-03-19)

- [x] Reproduce the exact `6-59-19-5` quarter-definition miss with the user-supplied `S.W. 1/4 S.W.` and `S.E.` target coordinates.
- [x] Trace the remaining south-corner authority path that is still pulling the SW quarter definition above the road allowance.
- [x] Implement the minimal fix so the SW quarter south edge lands at `509718.024, 5990739.877` and `510545.814, 5990742.205`.
- [x] Rebuild and rerun the AutoCAD checker against those exact coordinates.

## Review (Exact SW Quarter RA Corners For 6-59-19-5, 2026-03-19)

- Root cause: the quarter-view south selector was still willing to keep the inset `L-USEC-C-0` row for section `6-59-19-5`, and even after the selector was corrected, the persisted `L-QSEC` 1/4-definition linework was still the stale initial helper set rather than the final road-allowance-resolved geometry.
- Fix: updated the correction-boundary promotion heuristics in `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs` so center-linked hard correction fragments can win and correction-south promotion scores against the road-allowance target. Then rebuilt the section-scoped `L-QSEC` quarter-definition lines from the final solved corners inside the final quarter-view pass so the CAD entities now match the corrected south edge.
- Compatibility note: kept the existing LSD endpoint enforcement flow; the change makes its `L-QSEC` source geometry authoritative and current instead of changing the downstream rule matrix.
- Verification:
  - `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release -p:Platform=x64 -p:NuGetAudit=false` passed.
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -p:NuGetAudit=false` passed.
  - `scripts/atsbuild_harness.ps1` with `data/quarter-correctionline-spec-6-59-19-5.json` and `data/quarter-correctionline-review-6-59-19-5.json` passed in both `data/quarter-correctionline-run-6-59-19-5-rerun8/artifacts/review-report.json` and the fresh rerun `data/quarter-correctionline-run-6-59-19-5-rerun9/artifacts/review-report.json`.
  - Runtime verification log for section `6` now reports `sw_sw=509718.024,5990739.877` and `sw_se=510545.814,5990742.205`, matching the user-supplied targets exactly.

# Follow-up (1/4 Definition Correction-Line RA Regression, 2026-03-19)

- [x] Inspect recent correction-line git changes and identify which ones could have regressed `1/4 Definition` endpoints on correction-line sections.
- [x] Trace the current `1/4 Definition` geometry path and prove where road-allowance-inclusive targeting is being lost.
- [x] Implement the minimal fix so correction-line `1/4 Definition` lines finish through the road allowance again.
- [x] Rebuild, run focused decision verification, and run the AutoCAD checker/harness for the affected path.

## Review (1/4 Definition Correction-Line RA Regression, 2026-03-19)

- Root cause: recent correction-line quarter-view changes in `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs` started allowing inset `L-USEC-C` candidates to satisfy the south correction selector, but the caller still labeled every successful correction pick as `L-USEC-C-0`. That blocked the promotion path that restores the hard road-allowance boundary and let some `L-QSEC` quarter-definition lines stop above the road allowance again.
- Fix: updated `TryResolveQuarterViewSouthCorrectionBoundaryV(...)` to return the actual selected correction source layer, updated the quarter-view caller to preserve that layer, and changed `TryResolveQuarterViewSouthMostCorrectionBoundarySegment(...)` so quarter-view promotion restores the hard correction boundary whenever it exists instead of preferring the inset fallback first.
- Compatibility note: updated the shared endpoint-enforcement call site signature only; the actual endpoint-enforcement selection logic was not otherwise retuned.
- Verification:
  - `dotnet msbuild src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -p:Platform=x64 -p:NuGetAudit=false -verbosity:minimal` passed. `NU1900` warning only because NuGet vulnerability metadata could not be fetched from `https://api.nuget.org/v3/index.json`.
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -p:NuGetAudit=false` passed.
  - Generated harness workbook from `data/quarter-correctionline-spec.json` and ran `scripts/atsbuild_harness.ps1` against `src/AtsBackgroundBuilder/REFERENCE ONLY/correctionlineaboveandbelowquaterdef.dwg` with `data/quarter-correctionline-review.json`.
  - Harness artifacts under `data/quarter-correctionline-run/artifacts` show `ATSBUILD_XLS_BATCH exit stage: completed (ok)` and `review-report.json` passed both `L-QSEC` endpoint checks at the correction road-allowance boundary with `delta = 0.0`.

# Follow-up (PLSR Touch-Quarter Matching, 2026-03-06)

- [x] Verify that fresh-build labels are still being collapsed to a single quarter during PLSR scan.
- [x] Let label existence satisfy every quarter the label visibly touches, while keeping a primary quarter for `Not in PLSR`.
- [x] Rebuild plugin and rerun decision tests.

## Review (PLSR Touch-Quarter Matching, 2026-03-06)

- The PLSR scan was still collapsing each parsed label into one quarter bucket, which meant a fresh-build label could visibly touch the correct quarter and still be missed if another quarter won the assignment.
- Updated src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs so label existence now indexes every touched quarter, while `Not in PLSR` still uses a primary quarter bucket only.
- Added pure touch-resolution coverage in src/AtsBackgroundBuilder/Core/PlsrQuarterPointMatcher.cs and src/AtsBackgroundBuilder.DecisionTests/Program.cs so multi-quarter touch behavior is testable without AutoCAD.
- Verification:
  - dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore (passed; NU1900 warning only because the sandbox could not reach api.nuget.org).
  - dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore (Decision tests passed.).

# Follow-up (PLSR Best-Quarter Label Assignment, 2026-03-06)

- [x] Verify whether labels that touch multiple quarters are being assigned to the first matching quarter instead of the best one.
- [x] Score quarter matches and assign each scanned label to the best quarter, not the first touching quarter.
- [x] Rebuild plugin and rerun decision tests.

## Review (PLSR Best-Quarter Label Assignment, 2026-03-06)

- The collector was still using first-match quarter assignment even after broader point/bounds checks, so labels touching more than one quarter could be claimed by the wrong quarter and then appear as false `Missing label` / `Not in PLSR` rows.
- Updated src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs so scanned labels now resolve to the best quarter based on in-quarter point hits first, with bounds-overlap only as a weak fallback tie-breaker.
- Verification:
  - dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore (passed; NU1900 warning only because sandboxed vulnerability checks could not reach api.nuget.org).
  - dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore (Decision tests passed.).
# Follow-up (PLSR Bounds Touch + Label Spacing, 2026-03-06)

- [x] Review the fresh-build evidence, including the supplied PDF, to separate false missing-labels from placement overlap.
- [x] Treat rendered label bounds touching a quarter as sufficient label existence for PLSR review.
- [x] Apply a low-risk spacing tweak so leader labels search farther and reserve a larger collision footprint.
- [x] Rebuild plugin and rerun decision tests.

## Review (PLSR Bounds Touch + Label Spacing, 2026-03-06)

- Rendered `missing labels.pdf` locally and confirmed the remaining problem is a mix of false `Missing label` rows plus heavy corridor label clustering.
- Updated src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs so label-quarter matching now falls back to rendered entity-bounds touching the quarter, not just sampled anchor points.
- Updated src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs so leader labels search farther (`160` candidates, `300m` max leader length) and use a larger collision gap when evaluating placements; this is a heuristic overlap reduction, not a full layout solver.
- Verification:
  - dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore (passed; NU1900 warning only because sandboxed vulnerability checks could not reach api.nuget.org).
  - dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore (Decision tests passed.).
# Follow-up (PLSR Label Any-Point Quarter Matching, 2026-03-06)

- [x] Broaden quarter matching so plain text labels are not judged by a single insertion point.
- [x] Include sampled text extents for text, leaders, and dimensions when deciding whether a label exists in a quarter.
- [x] Add decision coverage for the new extent-point builder.
- [x] Rebuild plugin and rerun decision tests.

## Review (PLSR Label Any-Point Quarter Matching, 2026-03-06)

- Updated src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs so PLSR label existence uses sampled text-extents points for plain `MText`, `MLeader`, and `AlignedDimension` entities, in addition to leader vertices and dimension endpoints.
- Added src/AtsBackgroundBuilder/Core/PlsrQuarterPointMatcher.cs extent-point builder coverage so quarter matching now aligns better with the rule "if any part of the label lands in the quarter, count it".
- This specifically reduces false `Missing label` / `Not in PLSR` rows when text is offset but some visible label geometry still falls inside the quarter.
- Verification:
  - dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore (passed; NU1900 warning only because sandboxed vulnerability checks could not reach api.nuget.org).
  - dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore (Decision tests passed.).
# Follow-up (Dispose OD Table Wrappers, 2026-03-06)

- [x] Capture the latest crash evidence instead of guessing from the ATS log alone.
- [x] Trace every `ODTables[tableName]` / `tables[tableName]` path that can leave `Autodesk.Gis.Map.ObjectData.Table` wrappers for finalization.
- [x] Dispose those OD table wrappers deterministically.
- [x] Rebuild plugin and rerun decision tests.

## Review (Dispose OD Table Wrappers, 2026-03-06)

- Windows Application/Error Reporting showed `acad.exe` crashing with `System.AccessViolationException` in `Autodesk.Gis.Map.ObjectData.Table.DeleteUnmanagedObject()` from the finalizer thread.
- Updated src/AtsBackgroundBuilder/Dispositions/OdHelpers.cs and src/AtsBackgroundBuilder/Dispositions/ShapefileImporter.cs so OD table wrappers fetched from `tables[tableName]` are wrapped in `using (table)` and disposed immediately instead of being left for finalization.
- This is a runtime stability fix; it does not change the PLSR decision rules.
- Verification:
  - dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore (passed; NU1900 warning only because sandboxed vulnerability checks could not reach api.nuget.org).
  - dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore (Decision tests passed.).
# Follow-up (PLSR Nonstandard-Layer Missing-Label Suppression, 2026-03-06)

- [x] Track visible DISP candidates on nonstandard layers during PLSR label collection.
- [x] Suppress `Missing label` review rows when a nonstandard-layer DISP candidate already exists in that quarter.
- [x] Add decision coverage for the suppression-key logic.
- [x] Rebuild plugin and rerun decision tests.

## Review (PLSR Nonstandard-Layer Missing-Label Suppression, 2026-03-06)

- Updated src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs so nonstandard-layer text entities that still expose a parsed DISP number register as visible quarter candidates even when they do not qualify as full PLSR labels.
- Missing-label generation now skips `Create missing label` for quarter/DISP pairs already backed by one of those nonstandard-layer candidates, and writes a log line when suppression occurs.
- Added src/AtsBackgroundBuilder/Core/PlsrMissingLabelSuppressionPolicy.cs plus decision coverage in src/AtsBackgroundBuilder.DecisionTests/Program.cs.
- Verification:
  - dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore (passed; NU1900 warning only because sandboxed vulnerability checks could not reach api.nuget.org).
  - dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore (Decision tests passed.).
# Follow-up (PLSR Nonstandard Label Layer Scan, 2026-03-06)

- [x] Verify whether visible missing-label examples are being filtered out before quarter matching.
- [x] Stop requiring the narrow C/F-...-T layer pattern when the entity text already parses as a valid disposition label.
- [x] Add runtime logging so the next test run shows whether nonstandard-layer fallback was used.
- [x] Rebuild plugin and rerun decision tests.

## Review (PLSR Nonstandard Label Layer Scan, 2026-03-06)

- Updated src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs so the PLSR scan no longer drops parsed labels solely because their layer is not recognized as C-...-T or F-...-T.
- Preserved the existing layer check as a diagnostic signal instead of a hard gate; parsed labels on nonstandard layers now count, and the scan logs the first few accepted fallback cases plus a summary count.
- This specifically addresses cases like the visible DRS label that exists in the drawing but was still being flagged as missing before any quarter test ran.
- Verification:
  - dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore (passed; NU1900 warning only because sandboxed vulnerability check could not reach api.nuget.org).
  - dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore (Decision tests passed.).

# Follow-up (PLSR Aligned-Dimension Quarter Matching, 2026-03-06)

- [x] Extend PLSR quarter matching for aligned-dimension labels beyond the text position.
- [x] Treat in-quarter dimension geometry as sufficient even when the dimension text drifts slightly outside the 1/4.
- [x] Add decision coverage for dimension-point quarter candidates.
- [x] Rebuild plugin and rerun decision tests.

## Review (PLSR Aligned-Dimension Quarter Matching, 2026-03-06)

- Updated src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs so aligned-dimension labels contribute quarter-test points from their extension-line endpoints and midpoint, not just the text anchor.
- Switched aligned-dimension PLSR anchor preference to the extension-line midpoint, which better reflects the in-quarter width geometry created during label placement.
- Expanded src/AtsBackgroundBuilder/Core/PlsrQuarterPointMatcher.cs with PlsrQuarterPointBuilder.BuildDimensionPoints(...) so dimension candidate-point logic is pure and testable.
- Added decision tests in src/AtsBackgroundBuilder.DecisionTests/Program.cs for midpoint inclusion and dimension-point dedupe behavior.
- Verification:
  - dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore (passed; NU1900 warning only because sandboxed vulnerability check could not reach api.nuget.org).
  - dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore (Decision tests passed.).

# Follow-up (Variable Width Review Color, 2026-03-06)

- [x] Centralize the rule that forced Variable Width labels must be green.
- [x] Keep non-variable label colors unchanged.
- [x] Add decision coverage for the variable-width color policy.
- [x] Rebuild plugin and rerun decision tests.

## Review (Variable Width Review Color, 2026-03-06)

- Added `src/AtsBackgroundBuilder/Core/DispositionLabelColorPolicy.cs` so review-color enforcement is explicit and testable instead of relying only on upstream width-label branches.
- Updated `src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs` to resolve the final text color through `DispositionLabelColorPolicy.ResolveTextColorIndex(...)` before creating aligned dimensions, leaders, or plain `MText` labels.
- Preserved existing non-variable color behavior; only labels whose final text contains `Variable Width` are forced to ACI 3 (green).
- Added decision tests in `src/AtsBackgroundBuilder.DecisionTests/Program.cs` for: variable-width labels forcing green, and non-variable labels preserving the requested color.
- Verification:
  - `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` (passed; `NU1900` warning only because sandboxed vulnerability check could not reach `api.nuget.org`).
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).
# Follow-up (PLSR Leader Quarter Matching, 2026-03-06)

- [x] Update PLSR label quarter matching to consider leader points, not just text location.
- [x] Keep leader label placement unchanged; only change missing-label detection behavior.
- [x] Add decision coverage for candidate-point quarter matching.
- [x] Rebuild plugin and rerun decision tests.

## Review (PLSR Leader Quarter Matching, 2026-03-06)

- Added `src/AtsBackgroundBuilder/Core/PlsrQuarterPointMatcher.cs` to centralize quarter membership against multiple candidate points while keeping the polygon test injected by the caller.
- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs` so `MLeader` labels carry quarter-test points from both the text location and reflected leader vertices; PLSR quarter assignment now accepts the label when any of those points lands inside the quarter polygon.
- Preserved label placement behavior; the change is limited to PLSR missing-label detection and still uses the farthest leader point as the stored anchor/display location.
- Added decision test coverage in `src/AtsBackgroundBuilder.DecisionTests/Program.cs` for: text-outside/leader-inside match, all-points-outside reject, and inside-bounds-but-outside-polygon reject.
- Verification:
  - `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` (passed; `NU1900` warning only because sandboxed vulnerability check could not reach `api.nuget.org`).
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).
# Follow-up (PLSR Missing-Label Candidate Selector Tests, 2026-03-02)

- [x] Extract missing-label candidate selection ordering/dedupe into a pure helper.
- [x] Wire disposition create-missing path to use selector output while preserving existing skip reasons/log text.
- [x] Add decision tests for preferred-candidate ordering, dedupe behavior, and blank-candidate filtering.
- [x] Rebuild plugin and rerun decision tests.

## Review (PLSR Missing-Label Candidate Selector Tests, 2026-03-02)

- Added `src/AtsBackgroundBuilder/Core/PlsrMissingLabelCandidateSelector.cs`:
  - `PlsrMissingLabelCandidateSelectionInput`
  - `PlsrMissingLabelCandidateSelectionResult`
  - `PlsrMissingLabelCandidateSelector.Select(...)`
  - behavior: preferred candidate first, stable indexed order for remaining candidates, case-insensitive dedupe, ignore blank IDs.
- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - `CreatePlsrMissingLabelsFromDispositions(...)` now:
    - builds indexed candidate ID map/order,
    - routes ordering/dedupe through `PlsrMissingLabelCandidateSelector`,
    - preserves existing skip messages:
      - `no disposition candidates indexed`
      - `candidate list empty after dedupe`
    - preserves placement behavior and counters.
- Updated decision test project wiring:
  - `src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj`
    - linked `..\AtsBackgroundBuilder\Core\PlsrMissingLabelCandidateSelector.cs`.
- Added decision tests in `src/AtsBackgroundBuilder.DecisionTests/Program.cs`:
  - `TestPlsrMissingLabelCandidateSelectorPrefersIssueCandidateAndDedupes`
  - `TestPlsrMissingLabelCandidateSelectorPreservesIndexedOrderWithoutPreferred`
  - `TestPlsrMissingLabelCandidateSelectorSkipsBlankCandidates`
- Verification:
  - build succeeded (warnings only):
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - decision tests passed:
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (PLSR Apply Decision Engine Tests, 2026-03-02)

- [x] Extract pure apply-routing logic into a testable decision engine.
- [x] Wire plugin apply stage to use routed decision output (accepted/ignored counters + ordered routed issues).
- [x] Add decision tests for accepted/ignored counts and routing order.
- [x] Rebuild plugin and rerun decision tests.

## Review (PLSR Apply Decision Engine Tests, 2026-03-02)

- Added `src/AtsBackgroundBuilder/Core/PlsrApplyDecisionEngine.cs`:
  - `PlsrApplyDecisionItem`
  - `PlsrApplyDecisionRoutedIssue`
  - `PlsrApplyDecisionResult`
  - `PlsrApplyDecisionEngine.Route(...)`
  - `PlsrApplyDecisionActionType`
- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - `ApplyAcceptedPlsrActions(...)` now:
    - maps issues to `PlsrApplyDecisionItem`,
    - calls `PlsrApplyDecisionEngine.Route(...)`,
    - uses routed accepted issue order for apply execution,
    - sources accepted/ignored counters from decision result.
  - added `MapPlsrApplyDecisionActionType(...)` to map plugin enum to decision-engine enum.
  - preserved existing apply exception logs, stage markers, and create-missing routing behavior.
- Updated decision test project wiring:
  - `src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj`
    - linked `..\AtsBackgroundBuilder\Core\PlsrApplyDecisionEngine.cs`.
- Added decision tests in `src/AtsBackgroundBuilder.DecisionTests/Program.cs`:
  - `TestPlsrApplyDecisionEngineRoutesAcceptedAndIgnored`
  - `TestPlsrApplyDecisionEnginePreservesAcceptedOrder`
  - `TestPlsrApplyDecisionEngineIgnoresNonActionableEvenIfAccepted`
- Verification:
  - build succeeded (warnings only):
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - decision tests passed:
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (PLSR Summary Composer Tests, 2026-03-02)

- [x] Extract PLSR summary/warning formatting into a pure helper that is testable without AutoCAD dependencies.
- [x] Wire `BuildPlsrSummary(...)` to use the pure composer output.
- [x] Add decision tests for summary formatting and warning generation behavior.
- [x] Rebuild plugin and rerun decision tests.

## Review (PLSR Summary Composer Tests, 2026-03-02)

- Added `src/AtsBackgroundBuilder/Core/PlsrSummaryComposer.cs`:
  - `PlsrSummaryComposeInput`
  - `PlsrSummaryComposeResult`
  - `PlsrSummaryComposer.Compose(...)`
  - preserves existing summary and warning wording/order behavior, including sorted prefixes/examples.
- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - `BuildPlsrSummary(...)` now maps scan/apply state into `PlsrSummaryComposer.Compose(...)`.
  - local `PlsrSummaryResult` is still used by existing PLSR flow; behavior unchanged.
- Updated test project wiring:
  - `src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj`
    - linked `..\AtsBackgroundBuilder\Core\PlsrSummaryComposer.cs`.
- Added decision tests in `src/AtsBackgroundBuilder.DecisionTests/Program.cs`:
  - `TestPlsrSummaryComposerBuildsSummaryWithSortedPrefixes`
  - `TestPlsrSummaryComposerBuildsWarningWithSortedExamples`
  - `TestPlsrSummaryComposerSkipsWarningWhenTextFallbackAllowed`
- Verification:
  - build succeeded (warnings only):
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - decision tests passed:
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (PLSR Apply Helper Split Refactor, 2026-03-02)

- [x] Split `RunPlsrApply(...)` into smaller helper methods for readability and maintenance.
- [x] Keep action routing, exception handling, and log strings unchanged.
- [x] Keep stage flow unchanged (`apply_actions`, `create_missing_labels`).
- [x] Rebuild plugin and rerun decision tests.

## Review (PLSR Apply Helper Split Refactor, 2026-03-02)

- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - added `PlsrAcceptedActionBuckets` for accepted create-missing action groups.
  - refactored `RunPlsrApply(...)` orchestration to call:
    - `ApplyAcceptedPlsrActions(...)`
    - `ApplyAcceptedPlsrMissingLabelCreates(...)`
  - extracted focused helpers:
    - `ApplyAcceptedPlsrAction(...)`
    - `CreatePlsrMissingLabelsFromDispositions(...)`
    - `CreatePlsrMissingLabelsFromTemplates(...)`
    - `CreatePlsrMissingLabelsFromXml(...)`
  - preserved existing behavior:
    - per-issue actionable acceptance handling,
    - direct owner/expired updates,
    - grouped missing-label creation paths,
    - all existing skip/failure log text and counters.
- Verification:
  - build succeeded (warnings only):
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - decision tests passed:
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (PLSR Summary Extraction Refactor, 2026-03-02)

- [x] Extract PLSR summary text composition from `RunPlsrCheck(...)` into a dedicated helper.
- [x] Extract skipped-text-only warning composition into the same summary helper output.
- [x] Keep summary-stage flow and final log/write behavior unchanged.
- [x] Rebuild plugin and rerun decision tests.

## Review (PLSR Summary Extraction Refactor, 2026-03-02)

- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - added `PlsrSummaryResult` carrier:
    - `SummaryText`
    - `WarningText`
    - `ShouldShowWarning`
  - added `BuildPlsrSummary(PlsrScanResult, PlsrApplyResult)`:
    - composes full summary text from scan/apply counters and issue lines,
    - composes warning text for skipped text-only fallback labels (with full ordered examples list).
  - `RunPlsrCheck(...)` now:
    - calls `BuildPlsrSummary(...)` at stage `summary`,
    - shows warning dialog using `summaryResult.WarningText`,
    - writes `summaryResult.SummaryText` to `PLSR_Check.txt`.
- Verification:
  - build succeeded (warnings only):
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - decision tests passed:
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (PLSR Apply Extraction Refactor, 2026-03-02)

- [x] Extract PLSR apply-phase action handling from `RunPlsrCheck(...)` into a dedicated helper.
- [x] Extract missing-label creation branch (`CreateMissingLabel`, template, XML) into the same apply helper.
- [x] Keep stage markers and behavior unchanged (`apply_actions`, `create_missing_labels`).
- [x] Rebuild plugin and rerun decision tests.

## Review (PLSR Apply Extraction Refactor, 2026-03-02)

- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - added `PlsrApplyResult` carrier for:
    - `OwnerUpdated`
    - `ExpiredTagged`
    - `MissingCreated`
    - `AcceptedActionable`
    - `IgnoredActionable`
    - `ApplyErrors`
  - extracted apply + create-missing flow into `RunPlsrApply(...)`:
    - preserves the existing `switch` behavior for actionable issue types,
    - preserves collection of accepted create-missing issue groups,
    - preserves all existing exception/log messages,
    - preserves missing-label creation mechanics and skip/failure logs.
  - simplified `RunPlsrCheck(...)` to:
    - run review dialog,
    - call `RunPlsrApply(...)`,
    - consume returned counters for summary output.
- Verification:
  - build succeeded (warnings only):
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - decision tests passed:
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (PLSR Scan Extraction Refactor, 2026-03-02)

- [x] Extract scan-only PLSR analysis from `RunPlsrCheck(...)` into a dedicated helper.
- [x] Add a dedicated scan result carrier for issues, counters, and lookup/index outputs.
- [x] Keep review/apply/summary behavior unchanged while wiring to extracted scan outputs.
- [x] Rebuild plugin and rerun decision tests.

## Review (PLSR Scan Extraction Refactor, 2026-03-02)

- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - added `PlsrScanResult` carrier type to hold:
    - generated issues,
    - scan counters (`missing`, `owner mismatch`, `extra`, `expired`, skipped fallback),
    - scan artifacts (`NotIncludedPrefixes`, `LabelByQuarter`, `DispositionsByDispNum`),
    - text-only fallback warning examples.
  - extracted scan phase into `RunPlsrScan(...)`:
    - XML load,
    - requested quarter key build,
    - current label collection/indexing,
    - issue generation for missing/owner mismatch/not-in-PLSR/expired/missing-quarter.
  - simplified `RunPlsrCheck(...)` orchestration to:
    - validate inputs,
    - call `RunPlsrScan(...)`,
    - continue existing review/apply/create-missing/summary flow unchanged using scan outputs.
  - tightened nullable flow in scan helper via `labelsForQuarter` local to avoid nullable warnings introduced by extraction.
- Verification:
  - initial restore-based build failed due blocked network to NuGet (`api.nuget.org`).
  - no-restore build succeeded (warnings only):
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - decision tests passed:
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (/debug-config lingering command prompt screenshot, 2026-03-02)

- [x] Confirm screenshot state against current source behavior.
- [x] Rebuild Release plugin and re-run decision tests.
- [x] Sync latest DLL/PDB to AutoCAD runtime path and verify source/runtime parity.

## Review (/debug-config lingering command prompt screenshot, 2026-03-02)

- Finding:
  - screenshot (`src/AtsBackgroundBuilder/REFERENCE ONLY/Screenshot 2026-03-01 194742.png`) shows the older modeless PLSR review variant (`Drawing changed during review. Apply is disabled; rerun PLSR Check.`).
  - current source in `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs` is modal `ShowDialog()` flow and does not contain that modeless guard text.
- Build/test verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).
- Runtime sync:
  - copied Release artifacts to `C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows\`.
- parity confirmed:
  - `AtsBackgroundBuilder.dll` source/runtime: `2026-03-01 19:50:32`, `977920` bytes.
  - `AtsBackgroundBuilder.pdb` source/runtime: `2026-03-01 19:50:32`, `391544` bytes.

# Follow-up (PLSR Review Must Allow Pan/Zoom During Accept/Ignore, 2026-03-02)

- [x] Restore modeless PLSR review interaction so model space can be inspected while review is open.
- [x] Keep Apply/Cancel decision flow deterministic (no stale DialogResult dependency).
- [x] Rebuild plugin, run decision tests, and sync runtime DLL/PDB with timestamp parity.

## Review (PLSR Review Must Allow Pan/Zoom During Accept/Ignore, 2026-03-02)

- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - `ShowPlsrReviewDialog(...)` now opens review with `Application.ShowModelessDialog(form)` again.
  - replaced modal `ShowDialog()` return handling with explicit `applyRequested` flag:
    - `Apply Decisions` commits grid edits, sets flag, closes form.
    - `Cancel` clears flag and closes form.
  - added non-blocking wait loop (`DoEvents` + short sleep) while form is visible so command flow resumes only after user closes review.
  - updated top guidance text to explicitly state pan/zoom is allowed during review.
- Verification:
  - build succeeded:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - decision tests passed:
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).
  - runtime sync + parity:
    - `AtsBackgroundBuilder.dll` source/runtime: `2026-03-01 19:59:45`, `978432` bytes.
    - `AtsBackgroundBuilder.pdb` source/runtime: `2026-03-01 19:59:45`, `391616` bytes.

# Follow-up (Boundary Prompt Lingers On Command Bar After Add Sections From BDY, 2026-03-02)

- [x] Refresh AutoCAD editor command prompt immediately after boundary `GetEntity` selection exits.
- [x] Rebuild plugin, run decision tests, and sync runtime DLL/PDB.
- [x] Verify source/runtime timestamp + size parity.

## Review (Boundary Prompt Lingers On Command Bar After Add Sections From BDY, 2026-03-02)

- Updated `src/AtsBackgroundBuilder/Core/BoundarySectionImportService.cs`:
  - wrapped boundary `editor.GetEntity(...)` in `try/finally` inside `StartUserInteraction(...)` scope.
  - added `RefreshEditorPrompt(Editor)` and call it from `finally` so prompt state is normalized whether select succeeds, fails, or cancels.
  - refresh behavior uses:
    - `editor.WriteMessage("\n")`
    - `editor.PostCommandPrompt()`
  - intent: clear stale `ATSBUILD Select closed boundary polyline` prompt residue after boundary import returns to UI.
- Verification:
  - build succeeded:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - decision tests passed:
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).
  - runtime sync + parity:
    - `AtsBackgroundBuilder.dll` source/runtime: `2026-03-01 20:29:03`, `978432` bytes.
    - `AtsBackgroundBuilder.pdb` source/runtime: `2026-03-01 20:29:02`, `391696` bytes.

# Follow-up (Boundary Select Command Context Stuck, 2026-03-01)

- [x] Replace boundary-selection hide/show round-trip with AutoCAD `StartUserInteraction` host-window integration.
- [x] Wire host window handles from both UI shells (WPF + WinForms) into boundary selection service.
- [x] Keep boundary import behavior unchanged while avoiding lingering prompt-command state.
- [x] Build plugin, run decision tests, and sync runtime DLL/PDB.

## Review (Boundary Select Command Context Stuck, 2026-03-01)

- Root concern:
  - command line could remain stuck on `ATSBUILD select closed polyline` after boundary import, and user interaction (pan/navigation) was constrained.
  - hide/show round-trips were also a likely contributor to UI lifecycle churn.
- Updated `src/AtsBackgroundBuilder/Core/BoundarySectionImportService.cs`:
  - `TryCollectEntriesFromBoundary(...)` now accepts `hostWindowHandle`.
  - wraps `editor.GetEntity(...)` with `editor.StartUserInteraction(hostWindowHandle)` when available.
  - adds safe no-op fallback when interaction wrapper is unavailable.
- Updated boundary-trigger callers:
  - `src/AtsBackgroundBuilder/Core/AtsBuildWindow.cs`
    - passes `new WindowInteropHelper(this).Handle`.
    - removes hide/show round-trip around boundary import.
  - `src/AtsBackgroundBuilder/Core/AtsBuildForm.cs`
    - passes `Handle`.
    - removes hide/show round-trip around boundary import.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).
  - runtime DLL sync succeeded:
    - source/runtime timestamp `2026-03-01 7:43:02 PM`, size `980480`.

# Follow-up (PLSR Modeless Review + Drawing-Change Guard, 2026-03-01)

- [x] Convert PLSR Accept/Ignore review UI to modeless so users can pan/zoom model space while reviewing.
- [x] Track drawing changes while review is open (`ObjectModified`, `ObjectErased`, `ObjectAppended`).
- [x] Block Apply when drawing changed during review and instruct rerun of PLSR check.
- [x] Build plugin, run decision tests, and sync runtime DLL/PDB.

## Review (PLSR Modeless Review + Drawing-Change Guard, 2026-03-01)

- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - changed review entry call to pass `Database` into `ShowPlsrReviewDialog(...)`.
  - switched PLSR review form from modal `ShowDialog()` to modeless `Application.ShowModelessDialog(form)`.
  - added runtime message loop wait so ATSBUILD pauses until review closes while still allowing drawing interaction.
  - subscribed during review to:
    - `database.ObjectModified`
    - `database.ObjectErased`
    - `database.ObjectAppended`
  - when any drawing change is detected:
    - disables `Apply Decisions`,
    - updates review banner text,
    - blocks apply and prompts user to rerun PLSR check.
  - always detaches database event handlers in `finally`.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).
  - runtime DLL sync succeeded:
    - source/runtime timestamp `2026-03-01 7:30:13 PM`, size `979968`.

# Follow-up (PLSR Final Summary Popup Removal, 2026-03-01)

- [x] Remove the final `PLSR Check` summary popup after the review/apply flow.
- [x] Keep `PLSR Review` (Accept/Ignore) and warning popup behavior unchanged.
- [x] Keep PLSR summary logging/file output (`PLSR_Check.txt`) intact.
- [x] Build plugin, run decision tests, and sync runtime DLL/PDB.

## Review (PLSR Final Summary Popup Removal, 2026-03-01)

- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - removed the final `WinForms.MessageBox.Show(summaryText, "PLSR Check", ...)` popup.
  - retained `PLSR Review` decision grid and `PLSR Label Warning` popup behavior.
  - retained summary generation + `PLSR_Check.txt` write path.
  - now writes command line confirmation: `PLSR check complete. Summary written to PLSR_Check.txt.`
- Verification:
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).
  - standard release output build was blocked by file lock on `bin\Release\net8.0-windows\AtsBackgroundBuilder.dll`.
  - compiled equivalent artifact via alternate output path:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - runtime sync succeeded from alternate build output:
    - source/runtime timestamp `2026-03-01 7:24:20 PM`, size `977408`.

# Follow-up (PLSR Skip Warning Full List, 2026-03-01)

- [x] Remove capped skipped-label examples list (`Count < 10`) in PLSR warning path.
- [x] Show all skipped text-only fallback labels in warning dialog output.
- [x] Rebuild plugin, run decision tests, and sync runtime DLL/PDB.

## Review (PLSR Skip Warning Full List, 2026-03-01)

- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - removed the 10-item cap when collecting skipped text-only fallback labels.
  - warning dialog now lists every skipped label entry and orders them alphabetically for readability.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).
  - runtime DLL/PDB synced to `C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows`.

# Follow-up (PLSR Floating Text Fallback Guard, 2026-03-01)

- [x] Confirm floating `MText` labels are coming from text-only fallback creation paths.
- [x] Disable text-only fallback label creation for PLSR missing-label apply (no env override).
- [x] Ensure skipped no-geometry/text-only cases are surfaced in warning dialog + summary.
- [x] Build plugin, run decision tests, and sync runtime DLL/PDB.

## Review (PLSR Floating Text Fallback Guard, 2026-03-01)

- Root cause:
  - floating labels were produced by text-only fallback creation paths in `RunPlsrCheck(...)`:
    - `CreateMissingLabelFromTemplate`
    - `CreateMissingLabelFromXml`
  - those paths create `MText` without source disposition geometry (no aligned-dimension anchor/width line).
- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - hard-disabled text-only fallback feature flag by changing:
    - `IsPlsrTextOnlyFallbackLabelsEnabled()` -> always returns `false`.
  - this prevents fallback creation regardless of `ATSBUILD_PLSR_TEXT_FALLBACK` env state.
  - existing skip + warning dialog path remains active:
    - skipped text-only candidates are counted,
    - warning dialog lists skipped count and examples.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).
  - runtime sync succeeded:
    - `src\AtsBackgroundBuilder\bin\Release\net8.0-windows\AtsBackgroundBuilder.dll`
    - `C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows\AtsBackgroundBuilder.dll`
    - timestamp `2026-03-01 6:39:00 PM`, size `976896`.

# Follow-up (Open/Trimmed Disposition Boundary Recovery, 2026-03-01)

- [x] Validate user hypothesis that open/trimmed OD polylines are hard-skipped as `not closed`.
- [x] Add safe open-boundary recovery so recoverable trimmed polygons are converted to temporary closed boundaries.
- [x] Wire processing log when boundary recovery path is used.
- [x] Build plugin, run decision tests, and sync runtime DLL/PDB.

## Review (Open/Trimmed Disposition Boundary Recovery, 2026-03-01)

- Root cause confirmed:
  - `ProcessDispositionPolylines(...)` only accepted `GeometryUtils.TryGetClosedBoundaryClone(...)`.
  - open polylines were counted as `Skipped (not closed)` and never available for source matching/label workflows.
- Updated `src/AtsBackgroundBuilder/Geometry/GeometryUtils.cs`:
  - added overload `TryGetClosedBoundaryClone(..., out bool recoveredFromOpen)`.
  - added open-polyline recovery path:
    - clones open polyline,
    - closes it only when endpoint gap is within a bounded proportion of retained path length,
    - validates finite non-trivial area before accepting.
  - extended explode-based boundary selection to also recover open exploded polylines and select best candidate by area/extent score.
- Updated `src/AtsBackgroundBuilder/Core/Plugin.cs`:
  - uses new overload and logs:
    - `Disposition boundary recovery: closed trimmed/open boundary for entity ...`
  - keeps original `SkippedNotClosed` behavior only for unrecoverable cases.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).
  - runtime DLL synced:
    - source + runtime `AtsBackgroundBuilder.dll` timestamp `2026-03-01 6:26:16 PM`, size `975360`.

# Follow-up (PLSR Missing Rows Still Non-Actionable, 2026-03-01)

- [x] Diagnose latest `/debug-config` run where `Missing labels` remained high (`140`) and `Missing labels created` stayed low (`41`).
- [x] Add XML fallback actionable path for missing-label rows that have no source geometry and no same-DISP template.
- [x] Ensure XML fallback writes labels onto a recognized disposition text layer for future scan parity.
- [x] Build plugin, run decision tests, and sync runtime DLL/PDB.

## Review (PLSR Missing Rows Still Non-Actionable, 2026-03-01)

- Root cause from latest logs:
  - `PLSR_Check.txt` showed `Missing labels: 140`, `Missing labels created: 41`, `Actionable results accepted: 42`.
  - gap indicated most missing rows were still non-actionable (no source geometry / no template path).
- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - added new action type `CreateMissingLabelFromXml`.
  - missing-label issue generation now marks no-source/no-template rows as actionable (when quarter context exists).
  - apply stage now processes this path and creates fallback `MText` labels using:
    - owner from mapped expected owner,
    - disposition number line,
    - template styling when available,
    - fallback recognized layer `C-PLSR-T` (created if missing),
    - deterministic in-quarter placement offsets to avoid full stacking.
  - review action text now includes `Create missing label (XML fallback)`.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).
  - synced runtime plugin:
    - `src\AtsBackgroundBuilder\bin\Release\net8.0-windows\AtsBackgroundBuilder.dll`
    - `C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows\AtsBackgroundBuilder.dll`
    - both timestamp `2026-03-01 6:15:36 PM`.

# Follow-up (PLSR Missing Labels Skip Coverage Expansion, 2026-03-01)

- [x] Diagnose why `/debug-config` still reports many missing labels as effectively skipped.
- [x] Add a no-source fallback path that creates missing labels from an existing label template for the same DISP number.
- [x] Broaden existing OD source scan to include polyline-based disposition geometry outside strict `C-/F-` layer naming.
- [x] Build plugin and rerun decision tests.

## Review (PLSR Missing Labels Skip Coverage Expansion, 2026-03-01)

- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - added `CreateMissingLabelFromTemplate` action type for missing-label rows with no quarter-matching source geometry but with an existing same-DISP label elsewhere.
  - missing-label issue generation now marks those rows actionable with clear detail text.
  - apply stage now supports accepted template-create actions.
  - added `BuildLabelTemplatesByDispNum(...)` to index reusable labels.
  - added `TryCreateMissingLabelFromTemplate(...)`:
    - creates an `MText` in target quarter interior using template contents/layer/style cues.
    - updates owner line to expected XML owner.
    - updates per-quarter existing-disp tracking to prevent duplicates.
  - review action text now includes `Create missing label (template)`.
- Updated `src/AtsBackgroundBuilder/Core/Plugin.cs`:
  - relaxed fallback existing-source scan to include `Polyline`/`Polyline2d`/`Polyline3d` entities with OD DISP data regardless of layer naming.
  - this avoids missing valid source candidates that are not on strict `C-/F-` layers.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (PLSR Disp Number Canonicalization For Actionable Coverage, 2026-03-01)

- [x] Diagnose low actionable missing-label counts after crash-guard run (`Missing labels` high but `Actionable results accepted` low).
- [x] Canonicalize disposition number normalization across PLSR check + label placer matching/dedupe.
- [x] Build plugin and re-run decision tests.

## Review (PLSR Disp Number Canonicalization For Actionable Coverage, 2026-03-01)

- Context from latest `/debug-config` outputs:
  - `Missing labels: 115`
  - `Missing labels created: 25`
  - `Actionable results accepted: 25`
  - indicates many missing rows were non-actionable (source matching failed before placement).
- Updated normalization in:
  - `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`
  - `src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs`
- New canonical DISP_NUM normalization behavior:
  - uppercase
  - remove whitespace + non-alphanumeric characters
  - preserve 3-letter prefix when present
  - normalize numeric suffix by trimming leading zeros (keeping at least one zero)
  - fall back to cleaned alphanumeric token when no 3-letter prefix exists.
- Expected impact:
  - better matching between XML DISP numbers, existing label DISP numbers, and OD/source DISP numbers (for example zero-padded variants).
  - more missing-label rows become actionable without requiring risky supplemental import.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (PLSR Import Crash Guard For Partial Existing Coverage, 2026-03-01)

- [x] Diagnose AutoCAD crash after latest PLSR supplemental-import change.
- [x] Confirm crash boundary from ATSBUILD log stage markers.
- [x] Add default-off guard for PLSR supplemental shapefile import when existing disposition source geometry is already present.
- [x] Keep an explicit env override for power users who want to force supplemental import.
- [x] Build + run decision tests.

## Review (PLSR Import Crash Guard For Partial Existing Coverage, 2026-03-01)

- Crash boundary confirmed from `src/AtsBackgroundBuilder/bin/Release/net8.0-windows/AtsBackgroundBuilder.log`:
  - reached `ATSBUILD stage: disposition_import`
  - reached `Importer.Import begin.`
  - no subsequent `Importer.Import completed.` / no ATSBUILD completion marker in that run section
  - consistent with native Map import hard-termination in this path.
- Updated `src/AtsBackgroundBuilder/Core/Plugin.cs`:
  - added `IsPlsrSupplementalImportEnabled()` env gate (`ATSBUILD_PLSR_SUPPLEMENT_IMPORT`).
  - in PLSR-only mode (`Check PLSR` ON, disposition linework/labels OFF):
    - if existing disposition source geometry is already present and env override is OFF, supplemental shapefile import is skipped by default.
    - new log line:
      - `PLSR supplemental import skipped: existing disposition source geometry found and ATSBUILD_PLSR_SUPPLEMENT_IMPORT is OFF.`
- Safety intent:
  - prevents default path from entering known crash-prone native importer call for partial-coverage runs.
  - still allows explicit opt-in supplemental import via env var when needed.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (PLSR Supplemental Import Gate For Missing Labels, 2026-03-01)

- [x] Analyze `/debug-config` PLSR logs where many missing labels were not created.
- [x] Update PLSR import gating to allow supplemental disposition import when missing-label precheck indicates gaps, even if some existing disposition polylines are already present.
- [x] Update decision tests for new BuildExecutionPlan behavior.
- [x] Build + run decision tests.

## Review (PLSR Supplemental Import Gate For Missing Labels, 2026-03-01)

- Log findings from `AtsBackgroundBuilder.log` + `PLSR_Check.txt`:
  - `Missing labels: 98`, `Missing labels created: 28`, `Actionable results accepted: 29`, `Apply errors: 0`.
  - Existing disposition scan found partial coverage (`Disposition source scan: found 65 existing OD polyline(s)`), so PLSR missing-label creation had limited source geometry.
  - Previous gate only imported fallback shapefiles when existing disposition count was `0`, which blocked supplemental import for partial-coverage scenarios.
- Updated `src/AtsBackgroundBuilder/Core/BuildExecutionPlan.cs`:
  - `ShouldImportDispositions(...)` now returns `true` for PLSR runs regardless of existing-disposition count (still gated by precheck in PLSR-only mode).
  - `ShouldRunPlsrMissingLabelPrecheck(...)` now applies to all PLSR-only runs (`CheckPlsr=ON`, linework/labels OFF), not just when existing count is `0`.
  - Net effect: PLSR-only runs can now import supplemental disposition sources when missing labels are detected, even with partial existing disposition coverage.
- Updated `src/AtsBackgroundBuilder.DecisionTests/Program.cs` expectations for the new plan behavior.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (PLSR Cleanup Scope Guard, 2026-03-01)

- [x] Confirm PLSR fallback path mixes existing disposition IDs with cleanup erase candidates.
- [x] Separate imported disposition IDs from disposition processing IDs.
- [x] Restrict cleanup erase scope to imported disposition IDs only.
- [x] Verify compile after refactor.

## Review (PLSR Cleanup Scope Guard, 2026-03-01)

- Updated `src/AtsBackgroundBuilder/Core/Plugin.cs`:
  - `PrepareDispositionInputs(...)` now maintains two lists:
    - `DispositionPolylines` (all polylines used for processing/checks: existing + imported)
    - `ImportedDispositionPolylines` (imported-only cleanup candidates)
  - shapefile import writes into `importedDispositionPolylines`, then merges into processing list.
  - `ExecutePostQuarterPipeline(...)` now passes imported-only IDs to `CleanupAfterBuild(...)`.
  - `DispositionPreparationResult` now carries `ImportedDispositionPolylines`.
- Verification:
  - default Release build path is currently locked by another process (`bin\\Release\\net8.0-windows\\AtsBackgroundBuilder.dll`).
  - compile verified successfully to alternate output path:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).

# Follow-up (1/4 Definitions UI Default Off, 2026-03-01)

- [x] Remove config-driven default for `1/4 Definitions` in shared ATSBUILD option catalog.
- [x] Rebuild release and sync runtime artifacts.

## Review (1/4 Definitions UI Default Off, 2026-03-01)

- Updated `Core/AtsBuildOptionCatalog.cs`:
  - removed `config => config.AllowMultiQuarterDispositions` default resolver from `AllowMultiQuarterDispositions`
  - `1/4 Definitions` now defaults unchecked in UI unless explicitly set by seeded state during recovery flows
- Build and artifact sync:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - DLL/PDB parity synced to:
    - `src\AtsBackgroundBuilder\bin\Release\net8.0-windows`
    - `build\net8.0-windows`
    - `C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows`

# Follow-up (Boundary Round-Trip Empty Snapshot Reopen Guard, 2026-03-01)

- [x] Diagnose latest `ui_cancelled` runs where boundary round-trip path had `section_requests_empty`.
- [x] Prevent immediate cancellation when boundary round-trip snapshot is unavailable.
- [x] Reopen dialog without snapshot (bounded retries) so user can continue to explicit Build.
- [x] Build and sync release artifacts.

## Review (Boundary Round-Trip Empty Snapshot Reopen Guard, 2026-03-01)

- Root cause from runtime logs:
  - `BoundaryRoundTrip=True` + `snapshot unavailable (reason=section_requests_empty)` dropped directly to `ui_cancelled`.
  - This could happen before user got a stable explicit Build submission path.
- Updated `Core/Plugin.cs` UI recovery loop:
  - added boundary-roundtrip-specific reopen fallback when snapshot is unavailable:
    - closes visible stale window if needed
    - reopens dialog without seed input
    - bounded by existing retry limit
  - new log line:
    - `UI boundary round-trip recovery: reopening dialog without snapshot (...)`
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - synced DLL/PDB parity at:
    - `src\AtsBackgroundBuilder\bin\Release\net8.0-windows`
    - `build\net8.0-windows`
    - `C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows`

# Follow-up (1/4 Definition Visibility Decoupled From Processing, 2026-03-01)

- [x] Confirm quarter-dependent logic reliability can be preserved independently of `1/4 Definitions` display toggle.
- [x] Decouple quarter-definition processing from final visible output in the ATSBUILD execution flow.
- [x] Remove quarter-definition linework output when `1/4 Definitions` is off (unless env quarter-view override is enabled).
- [x] Build Release and sync `build` + runtime plugin artifacts.

## Review (1/4 Definition Visibility Decoupled From Processing, 2026-03-01)

- Updated `Core/Plugin.cs`:
  - internal quarter-view build is now always enabled for processing reliability (`drawQuarterView = true`)
  - final visibility intent remains driven by UI + env override:
    - `showQuarterDefinitionLinework = input.AllowMultiQuarterDispositions || EnableQuarterViewByEnvironment`
  - updated runtime log line to make this explicit:
    - `internalBuild=ON` with separate UI/env visibility state
- Updated `Diagnostics/Plugin.Diagnostics.CleanupDiagnostics.cs`:
  - when `1/4 Definitions` is off and no env quarter-view override is active:
    - erase generated `L-QSEC` quarter-definition lines from the current build ID set
    - erase `L-QUATER` quarter-view output within requested-section windows
  - this keeps quarter-dependent processing available while removing visible 1/4 definition output when user disables it
- Build and sync verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - DLL/PDB synced with parity:
    - `src\AtsBackgroundBuilder\bin\Release\net8.0-windows`
    - `build\net8.0-windows`
    - `C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows`

# Follow-up (Surface Impact Recovery Option Persistence, 2026-03-01)

- [x] Diagnose why some recovered UI runs execute PLSR but skip Surface Impact.
- [x] Persist last PLSR option selection (`Check PLSR`, `Surface Impact`) in the WPF ATSBUILD window.
- [x] Use persisted PLSR option selection during UI auto-close recovery instead of forcing `Check PLSR` only.
- [x] Build `AtsBackgroundBuilder` Release and sync `build` artifacts.

## Review (Surface Impact Recovery Option Persistence, 2026-03-01)

- Updated `Core/AtsBuildWindow.cs`:
  - added persisted option-state storage (`AtsBackgroundBuilder.plsr.options.txt`) for:
    - `CheckPlsr`
    - `IncludeSurfaceImpact`
  - restored persisted PLSR option selection on window open when no seed input is provided
  - persisted option selection on successful Build submit (`OnBuild` success path)
  - exposed `TryGetPersistedPlsrOptionSelection(...)` for recovery flows
- Updated `Core/Plugin.cs` auto-close recovery:
  - when recovering from no-result UI and loading persisted XML paths, it now restores persisted PLSR options (including `Surface Impact`) instead of always forcing PLSR-only
  - fallback still guarantees at least one option is enabled when persisted XML-driven recovery is used
  - retained existing `UI options: CheckPLSR=..., SurfaceImpact=..., XML files=...` runtime log line for direct verification
- Build verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - build succeeded (warnings only, no errors)
- Artifact sync:
  - copied updated `AtsBackgroundBuilder.dll/.pdb` to `build\net8.0-windows` (source/build parity confirmed)
  - runtime copy to `C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows` is currently blocked by file lock (`AtsBackgroundBuilder.dll` and `.pdb` in use)

# Follow-up (ATSBUILD Shared PLSR XML Helper Refactor, 2026-03-01)

- [x] Add a shared Core helper for PLSR/Surface XML picker constants and XML path validation.
- [x] Refactor `AtsBuildWindow` build/snapshot XML flows to use the shared helper.
- [x] Refactor `AtsBuildForm` build XML flow to use the shared helper.
- [x] Build `AtsBackgroundBuilder` Release and document verification in this file.

## Review (ATSBUILD Shared PLSR XML Helper Refactor, 2026-03-01)

- Added shared helper at `src/AtsBackgroundBuilder/Core/PlsrXmlSelectionService.cs`:
  - dialog constants (`DialogFilter`, `DialogTitle`, `RequiredSelectionMessage`)
  - `RequiresXml(...)` gate for `CheckPlsr`/`IncludeSurfaceImpact`
  - centralized path validation/normalization with failure classification (`EmptySelection`, `NoValidFiles`)
- Updated `AtsBuildWindow`:
  - `OnBuild` now uses shared constants + shared validation for selected XML files
  - preserves existing abort trace behavior (`onbuild_abort_plsr_xml_dialog_cancelled` vs `onbuild_abort_plsr_xml_no_valid_paths`)
  - `TryBuildInputSnapshot` now uses shared path validation for persisted XML reuse
- Updated `AtsBuildForm`:
  - `OnBuild` now uses shared constants and shared validation logic for XML selection
- Build verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - build succeeded (warnings only, no errors).

# Follow-up (ATSBUILD Shared Shape-Update Service Refactor, 2026-03-01)

- [x] Add a shared Core service for shape update sources/destinations, date-folder resolution, and copy execution.
- [x] Refactor `AtsBuildWindow` to call shared shape-update service (remove local duplicate helpers/constants).
- [x] Refactor `AtsBuildForm` to call shared shape-update service (remove local duplicate helpers/constants).
- [x] Build `AtsBackgroundBuilder` Release and document verification in this file.

## Review (ATSBUILD Shared Shape-Update Service Refactor, 2026-03-01)

- Added shared service at `src/AtsBackgroundBuilder/Core/ShapeUpdateService.cs` with:
  - supported shape types (`Disposition`, `Compass Mapping`, `Crown Reservations`)
  - source-root selection rules
  - latest-date folder discovery rules (`dids_*` and dated folders)
  - destination copy execution (full copy vs selected shape-set copy)
- Refactored `AtsBuildWindow`:
  - removed local shape-update roots/base-name constants + helper methods
  - now populates shape-type combo from `ShapeUpdateService.SupportedShapeTypes`
  - now prepares/executes updates via `ShapeUpdateService.TryPreparePlan(...)` and `ExecutePlan(...)`
- Refactored `AtsBuildForm` with the same shared service path and removed duplicated local helpers/constants.
- Build verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - build succeeded (warnings only, no errors).

# Follow-up (ATSBUILD Shared Option Catalog Refactor, 2026-03-01)

- [x] Add a shared ATSBUILD option catalog for grouped Build Settings metadata (group, label, order, defaults).
- [x] Refactor `AtsBuildWindow` to render option groups from the shared catalog.
- [x] Refactor `AtsBuildForm` to render option groups from the shared catalog.
- [x] Build `AtsBackgroundBuilder` Release and document verification in this file.

## Review (ATSBUILD Shared Option Catalog Refactor, 2026-03-01)

- Added shared metadata source at `src/AtsBackgroundBuilder/Core/AtsBuildOptionCatalog.cs`:
  - option keys
  - group definitions/order
  - display labels
  - default resolver support (`AllowMultiQuarterDispositions` from config)
- Updated `AtsBuildWindow` to configure and render grouped options by iterating the shared catalog instead of hardcoded label/group lists.
- Updated `AtsBuildForm` to do the same, preserving the same grouped output and order as WPF.
- Build verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - build succeeded (warnings only, no errors).

# Follow-up (ATSBUILD Option Grouping by Section, 2026-03-01)

- [x] Group ATSBUILD Build Settings checkboxes into `SECTIONS`, `SHAPE FILES`, and `PLSR`.
- [x] Update option labels/order to match requested grouping:
  - `SECTIONS`: `ATS Fabric`, `LSDs`, `1/4 Definitions`, `1/4 SEC. Labels`
  - `SHAPE FILES`: `Dispositions`, `Disposition Labels`, `CLRs`, `P3 Water`, `Compass Mapping`
  - `PLSR`: `PLSR Check`, `Surface Impact`
- [x] Apply the same grouping model in both UI surfaces (`AtsBuildWindow` and `AtsBuildForm`).
- [x] Build `AtsBackgroundBuilder` and document verification in this file.

## Review (ATSBUILD Option Grouping by Section, 2026-03-01)

- Updated both ATSBUILD UIs to group option checkboxes into titled groups: `SECTIONS`, `SHAPE FILES`, and `PLSR`.
- Updated labels/order to match requested naming:
  - `SECTIONS`: `ATS Fabric`, `LSDs`, `1/4 Definitions`, `1/4 SEC. Labels`
  - `SHAPE FILES`: `Dispositions`, `Disposition Labels`, `CLRs`, `P3 Water`, `Compass Mapping`
  - `PLSR`: `PLSR Check`, `Surface Impact`
- Implemented the grouping in:
  - `src/AtsBackgroundBuilder/Core/AtsBuildWindow.cs`
  - `src/AtsBackgroundBuilder/Core/AtsBuildForm.cs`
- Build verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - build succeeded (warnings only, no errors).

# Feature (Surface Impact Build Option, 2026-03-01)

- [x] Add `Surface Impact` checkbox to both ATSBUILD UIs and wire it into `AtsBuildInput` seed/snapshot flow.
- [x] Share PLSR XML picker between `Check PLSR` and `Surface Impact` so one XML selection is reused when both are enabled.
- [x] Port GLIMPS/Surface table parser + processor + table builder logic from `PLSR-MANAGER` into ATSBUILD.
- [x] Filter Surface Impact records by ATSBUILD section input scope (M/RGE/TWP/SEC + quarter expansion) and remove manual checklist filtering.
- [x] Run Surface Impact as the final ATSBUILD stage and prompt insertion point; keep cancel safe.
- [x] Build `AtsBackgroundBuilder` Release and document verification in this file.

## Review (Surface Impact Build Option, 2026-03-01)

- Added `Surface Impact` option to both UIs (`AtsBuildWindow` + `AtsBuildForm`) and to shared `AtsBuildInput`.
- XML picker now runs once for either option (`Check PLSR` or `Surface Impact`) and stores shared XML paths in `PlsrXmlPaths`.
- Added new Surface Impact pipeline under `src/AtsBackgroundBuilder/SurfaceImpact/`:
  - `SurfaceImpactXmlParser` (GLIMPS XML parse with required `ReportRunDate`)
  - `SurfaceImpactProcessor` (same inclusion/ordering rules as PLSR Manager)
  - `SurfaceImpactTableBuilder` (same full table layout: FMA/TPA/Surface)
  - `Plugin.SurfaceImpact` runner (newest-wins by land location, builder-input scoping, final insertion-point prompt)
- Removed manual surface checklist behavior by replacing it with ATSBUILD input scoping only.
- Execution order: Surface Impact runs as final ATSBUILD stage before summary; canceling insertion point safely skips only the table insert.
- Build verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
- Runtime sync:
  - copied `AtsBackgroundBuilder.dll` and `AtsBackgroundBuilder.pdb` from `bin\Release\net8.0-windows-surfaceimpact` to `C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows` (timestamp `2026-03-01 12:14:42 PM`).

# Follow-up (Permanent-Fix-Only Cleanup, 2026-03-01)

- [x] Remove temporary verbose UI payload tracing (`UI[...]`, snapshot payload summaries) added for triage.
- [x] Keep permanent behavior fixes only (no-result seeded reopen + duplicate-window close-before-reopen guard).
- [x] Rebuild `AtsBackgroundBuilder` Release and sync runtime DLL/PDB.

## Review (Permanent-Fix-Only Cleanup, 2026-03-01)

- Removed temporary high-volume UI trace plumbing from `AtsBuildWindow` and detailed payload-summary logs from `Plugin` fallback flow.
- Retained permanent fixes:
  - no-result recovery reopening to seeded dialog requiring explicit Build
  - bounded reopen attempts
  - duplicate-window guard (`Close()` still-visible original window before seeded reopen)
  - validation-abort gate (`onbuild_abort_*`) on snapshot-run fallback.
- Build verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - build succeeded (warnings only, no errors).

# Follow-up (UI Section Payload Trace + Duplicate Reopen Guard, 2026-03-01)

- [x] Add per-window ATSBUILD UI trace logging for boundary import, Build click, parsed section requests, XML selection, and snapshot payloads.
- [x] Emit UI payload summaries in plugin fallback flow so log shows whether sections survive from click to final input.
- [x] Prevent duplicate UI reopen by closing any still-visible original window before seeded reopen.
- [x] Rebuild `AtsBackgroundBuilder` Release and sync runtime DLL/PDB.

## Review (UI Section Payload Trace + Duplicate Reopen Guard, 2026-03-01)

- Added `UI[windowId] ...` trace lines from `AtsBuildWindow` into main `AtsBackgroundBuilder.log` via constructor trace callback.
- New traces include:
  - boundary import before/after row snapshots
  - Build-button click row snapshot
  - parsed section request count/sample in `OnBuild`
  - PLSR XML dialog start/selected counts
  - `TryBuildInputSnapshot` success/failure with section sample and XML counts
- Plugin UI loop now logs input summaries for snapshot probe, recovered snapshot, and direct modal result.
- Added duplicate guard: if no-result recovery needs seeded reopen and previous window is still visible, close old window before reopening.

# Follow-up (Build Click Self-Cancel Guard, 2026-03-01)

- [x] Confirm UI no-intent auto-close path is still cancelling ATSBUILD after boundary/build interactions.
- [x] Replace immediate cancel on non-explicit/no-intent close with seeded dialog reopen (explicit Build required), with bounded retry attempts.
- [x] Prevent snapshot-run fallback from treating validation-aborted build attempts as runnable build intent.
- [x] Rebuild `AtsBackgroundBuilder` Release and sync runtime DLL/PDB.

## Review (Build Click Self-Cancel Guard, 2026-03-01)

- Updated `Core/Plugin.cs` UI loop to reopen `AtsBuildWindow` (seeded with captured input) when dialog closes without explicit cancel and without build intent, rather than immediately cancelling.
- Added bounded recovery (`3` attempts) to avoid endless reopen loops on persistent host/modal lifecycle failures.
- Tightened snapshot execution gate to ignore validation-aborted build traces (`onbuild_abort_*`) so fallback cannot run when Build was attempted but validation intentionally failed.
- Build + runtime sync verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - copied `AtsBackgroundBuilder.dll` and `AtsBackgroundBuilder.pdb` to `C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows`.

# Follow-up (Boundary Dialog Round-Trip Resume, 2026-03-01)

- [x] Confirm latest self-cancel runs occur with `BuildAttempted=False`/`BuildRequested=False` after boundary workflow.
- [x] Add boundary round-trip detection and UI-state seeding so modal false-return reopens UI instead of cancelling.
- [x] Rebuild and sync runtime DLL.

## Review (Boundary Dialog Round-Trip Resume, 2026-03-01)

- Root cause: `ADD SECTIONS FROM BDY` hides the WPF dialog to allow AutoCAD entity selection; that modal hide can cause `ShowDialog()` to return `false` before Build is clicked.
- Added `BoundaryImportRoundTripUsed` tracking in `AtsBuildWindow` and seeded-window restore support (`AtsBuildWindow(..., AtsBuildInput? seedInput)` + `ApplySeedInput(...)`).
- Updated `Plugin.cs` UI loop to detect boundary round-trip false-return and reopen the dialog with captured state, waiting for explicit Build click instead of cancelling or auto-running.
- Build verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - build succeeded (warnings only, no errors).

## Follow-up (Build Click Trace, 2026-03-01)

- [x] Add explicit UI trace markers for build-click lifecycle (`build_button_click`, `onbuild_start`, abort reasons, success).
- [x] Emit build-trace + boundary-roundtrip flags in plugin no-result log line for deterministic triage.
- [x] Rebuild and sync runtime DLL.

# Follow-up (Auto-Build Regression Guard, 2026-03-01)

- [x] Reproduce user report that ATSBUILD now runs before pressing Build.
- [x] Remove non-explicit auto-close snapshot recovery that can trigger build without a Build click.
- [x] Rebuild and sync runtime DLL after rollback.

## Review (Auto-Build Regression Guard, 2026-03-01)

- User correction confirmed regression: fallback was too aggressive and allowed build execution from UI auto-close.
- Restored strict recovery intent: snapshot recovery now requires explicit build intent (`BuildRequested || BuildAttempted`), preserving normal close-as-cancel behavior.
- Build verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - build succeeded (warnings only, no errors).

# Follow-up (Debug-Config PLSR Auto-Close Recovery, 2026-03-01)

- [x] Confirm latest `/debug-config` log shows `BuildAttempted=False` / `BuildRequested=False` cancellation path.
- [x] Extend UI fallback to recover snapshot on non-explicit auto-close when snapshot is PLSR-enabled.
- [x] Rebuild `AtsBackgroundBuilder` and sync runtime DLL.

## Review (Debug-Config PLSR Auto-Close Recovery, 2026-03-01)

- Latest run (`2026-03-01 8:53:01 AM`) logged `BuildRequested=False, BuildAttempted=False` then cancelled at UI stage, so `/debug-config` was closing without firing Build.
- Updated `Core/Plugin.cs` UI fallback gate to try snapshot recovery on non-explicit auto-close, but only continue when the recovered snapshot is PLSR-enabled (`CheckPlsr=true`) to avoid broad close-behavior regressions.
- Added diagnostic line for this path: `UI auto-close fallback: recovered build input snapshot without explicit Build click (PLSR-enabled snapshot).`
- Follow-up adjustment: for auto-close with valid snapshot and `CheckPlsr=false`, auto-enable PLSR using persisted XML paths (when available) instead of cancelling.
- Build verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - build succeeded (warnings only, no errors).
- Runtime sync:
  - copied `AtsBackgroundBuilder.dll` and `AtsBackgroundBuilder.pdb` to `C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows` (timestamp `2026-03-01 8:56:47 AM`).

# Follow-up (PLSR Build Attempt Recovery, 2026-03-01)

- [x] Confirm latest log failure mode for `Add Sections from BDY -> Check PLSR -> Build -> Select XML`.
- [x] Add persistent `BuildAttempted` UI state so fallback recovery can distinguish "attempted build" from "window closed".
- [x] Update plugin UI fallback gate to use build-attempt state and keep explicit-cancel behavior intact.
- [x] Rebuild `AtsBackgroundBuilder` to verify compile safety.

## Review (PLSR Build Attempt Recovery, 2026-03-01)

- Latest runtime log (`2026-03-01 8:41:54 AM`) showed: `UI returned without direct result (..., ExplicitCancel=False, BuildRequested=False)` followed by `UI closed without build request; treating as cancel.`
- Added `BuildAttempted` state in `Core/AtsBuildWindow.cs` so fallback logic can still recover when a build was attempted but `_buildRequested` was later reset by validation/dialog paths.
- Updated `Core/Plugin.cs` fallback gate to recover on `BuildRequested || BuildAttempted`, while preserving explicit cancel behavior.
- Build verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - build succeeded (existing nullable warnings remain, no new errors).

# Follow-up (PLSR Fatal Guardrails, 2026-03-01)

- [x] Trace PLSR fatal path and identify unhandled exception vectors in the check flow.
- [x] Add fail-safe exception guards in PLSR scan/apply paths so bad entities cannot terminate ATSBUILD.
- [x] Add stage-aware PLSR diagnostics to isolate future failures quickly from logs.
- [x] Rebuild `AtsBackgroundBuilder` to verify compile safety after guardrail changes.

## Review (PLSR Fatal Guardrails, 2026-03-01)

- Root-cause class identified as unhandled runtime exceptions in PLSR execution paths (label scan / apply writes) that could bubble out of `RunPlsrCheck(...)`.
- Added stage-scoped top-level guard in `RunPlsrCheck(...)` and per-issue apply guards so recoverable entity/update failures are logged and skipped instead of terminating ATSBUILD.
- Hardened `CollectPlsrLabels(...)` with per-entity exception isolation and bounded error logging.
- Hardened `HasPotentialMissingPlsrLabels(...)` precheck with conservative fallback (`return true`) when precheck throws.
- Build verification:
  - `.\.local_dotnet\dotnet.exe msbuild src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj /t:Compile /p:RestoreIgnoreFailedSources=true`
  - compile succeeded (existing nullable warnings remain).

## Follow-up (Boundary-Imported Rows + Crash Telemetry, 2026-03-01)

- [x] Ensure `ADD SECTIONS FROM BDY` removes placeholder blank grid rows before appending imported rows.
- [x] Add immediate log flush markers for PLSR precheck and shapefile-import crash boundaries.
- [x] Rebuild Release output to verify compile safety after UI/logging changes.

## Review (Boundary-Imported Rows + Crash Telemetry, 2026-03-01)

- Updated both UI paths to clear placeholder empty rows before adding boundary-imported entries:
  - `Core/AtsBuildWindow.cs`
  - `Core/AtsBuildForm.cs`
- Updated logger immediate-flush rules to preserve crash-boundary lines even on hard termination:
  - `ATSBUILD assembly:`
  - `PLSR precheck:`
  - `Starting shapefile import.`
  - `Importer.Init begin:`
  - `Importer.Import begin.`
- Build verification:
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore`
  - build succeeded (existing nullable warnings remain).

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
  - Added raw-lock stage for blind `NW` from apparent west????????????north intersection with explicit diagnostics:
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

## Follow-up (PLSR Review Decision Dropdown Not Working, 2026-02-28)

- [x] Fix row decision dropdown interactivity so `Accept/Ignore` can be changed in the review grid.
- [x] Ensure selected dropdown value is committed when clicking `Apply Decisions`.
- [x] Rebuild and verify compile/build safety.

## Review (PLSR Review Decision Dropdown Not Working, 2026-02-28)

- Root cause: `ShowPlsrReviewDialog(...)` canceled `CellBeginEdit` for non-actionable rows, which made dropdowns appear broken in common result sets.
- Fixes in `Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - Removed the `CellBeginEdit` cancellation gate for decision column.
  - Set `grid.EditMode = EditOnEnter` to make combo interaction immediate.
  - Added `Apply Decisions` handler commit path (`grid.EndEdit()` + `CurrencyManager.EndCurrentEdit()`) before dialog close so latest user selection is persisted.
- Verification:
  - `.\.local_dotnet\dotnet.exe msbuild src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj /t:Compile /p:Configuration=Release` succeeded.
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (0 errors).

## Follow-up (PLSR-Driven Missing Label Generation + A-DIM Only, 2026-02-28)

- [x] Ensure `Check PLSR` can generate disposition labels even when `Disposition labels` toggle is off.
- [x] For PLSR-only runs, first reuse existing in-drawing disposition polylines with OD in the requested scope; if none are found, import disposition shapefiles and label from those.
- [x] Force label placement to use A-DIM style for disposition labeling (no leader-version path).
- [x] Remove `A-DIM` checkbox from both UI implementations (WPF + WinForms) and default input to A-DIM mode.
- [x] Rebuild Release and verify output artifact sync.

## Review (PLSR-Driven Missing Label Generation + A-DIM Only, 2026-02-28)

- Updated build flow in `Core/Plugin.cs`:
  - Added `shouldGenerateDispositionLabels = IncludeDispositionLabels || CheckPlsr`.
  - For PLSR-only runs, scans existing modelspace polylines in requested quarter scope for disposition OD (`DISP_NUM`) before importing shapefiles.
  - Imports disposition shapefiles as fallback when PLSR is on and no existing OD dispositions are found.
  - Label placement now runs when `Check PLSR` is on (even with labels toggle off), with duplicate-prevention reuse index as before.
  - `LabelPlacer` is invoked with `useAlignedDimensions: true` to keep disposition labels on A-DIM path.
- Added helper methods in `Core/Plugin.cs`:
  - `FindExistingDispositionPolylinesWithObjectData(...)`
  - `IsLikelyDispositionLineLayer(...)`
- Updated shape auto-update gating in `Core/Plugin.Core.ImportWindowing.cs`:
  - Disposition shape-set auto-update now triggers for `Check PLSR` runs too.
- Updated UI/input:
  - Removed `A-DIM` checkbox from `Core/AtsBuildWindow.cs` and `Core/AtsBuildForm.cs`.
  - `AtsBuildInput.UseAlignedDimensions` now defaults to `true`.
  - Input builders now set `UseAlignedDimensions = true` directly.
- Verification:
  - `.\.local_dotnet\dotnet.exe msbuild src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj /t:Compile /p:Configuration=Release` succeeded.
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - Output parity confirmed:
    - `src\AtsBackgroundBuilder\bin\Release\net8.0-windows\AtsBackgroundBuilder.dll`
    - `build\net8.0-windows\AtsBackgroundBuilder.dll`

## Follow-up (PLSR Missing Labels Must Be Accept-Driven, 2026-02-28)

- [x] Stop pre-generating disposition labels when `Check PLSR` is enabled.
- [x] Make missing-label findings actionable (`Create missing label`) only when source disposition geometry is available in the quarter.
- [x] Apply missing-label creation only for rows user accepts in PLSR review.
- [x] Keep normal owner/expired apply behavior unchanged.
- [x] Rebuild and verify artifact sync.

## Review (PLSR Missing Labels Must Be Accept-Driven, 2026-02-28)

- Updated pre-PLSR flow in `Core/Plugin.cs`:
  - Label auto-placement before PLSR now runs only when `IncludeDispositionLabels && !CheckPlsr`.
  - This prevents auto-generation of all missing labels during PLSR checks.
- Updated PLSR issue model and apply flow in `Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - Added `CreateMissingLabel` change type.
  - Missing-label rows are now actionable only when matching disposition source + quarter are found.
  - Accepted missing-label rows are created after review via `LabelPlacer` (A-DIM path), one accepted row at a time.
  - Non-accepted rows remain unchanged.
- Verification:
  - `.\.local_dotnet\dotnet.exe msbuild src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj /t:Compile /p:Configuration=Release` succeeded.
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `bin` and `build` DLL outputs are synchronized (`867840` bytes, same timestamp).

## Follow-up (PLSR Accepted Missing-Label Apply Guard, 2026-02-28)

- [x] Fix PLSR apply loop so `CreateMissingLabel` rows are not skipped by a `Label != null` precondition.
- [x] Keep owner/expired label edits guarded by existing label presence.
- [x] Rebuild to verify compile/build safety.

## Review (PLSR Accepted Missing-Label Apply Guard, 2026-02-28)

- Root cause:
  - Apply loop short-circuited with `if (!issue.IsActionable || issue.Label == null) continue;`.
  - `CreateMissingLabel` issues are intentionally actionable without `Label` (they use `Disposition+Quarter`), so accepted rows were skipped.
- Fix in `Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - Changed top guard to `if (!issue.IsActionable) continue;`.
  - Kept `issue.Label != null` checks scoped to `UpdateOwner` and `TagExpired` switch branches only.
  - `CreateMissingLabel` accepted rows now flow into post-transaction placement list as intended.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (0 errors, existing nullable warnings).

## Follow-up (PLSR Check Runtime Optimization, 2026-02-28)

- [x] Reduce expensive per-entity work while collecting existing PLSR labels from modelspace.
- [x] Add cheaper prechecks/caching before expensive disposition-quarter overlap checks.
- [x] Rebuild to verify compile/build safety after optimization.

## Review (PLSR Check Runtime Optimization, 2026-02-28)

- Updated `Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - `CollectPlsrLabels(...)` now:
    - skips non-text/non-dimension entities before layer matching,
    - caches disposition-layer match results per layer name,
    - uses quarter extents prefilter before `IsPointInsidePolyline(...)`.
  - `TryFindDispositionSourceForQuarterDisp(...)` now:
    - uses cached `QuarterInfo.Bounds` and `DispositionInfo.Bounds` instead of recomputing geometric extents,
    - checks `candidate.SafePoint` inside quarter first, then falls back to expensive polygon-overlap test.
  - `IsDispositionTextLayer(...)` now uses an equivalent fast string check instead of regex.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (`0` errors, existing nullable warnings).

## Follow-up (PLSR Pre-Result Slowdown Before Fallback Import, 2026-02-28)

- [x] Diagnose remaining `Check PLSR` delay occurring before review output.
- [x] Avoid fallback disposition shapefile import in PLSR-only mode when no missing labels are detected.
- [x] Optimize existing in-drawing disposition source scan used by PLSR-only runs.
- [x] Rebuild to verify compile/build safety.

## Review (PLSR Pre-Result Slowdown Before Fallback Import, 2026-02-28)

- Updated `Core/Plugin.cs`:
  - Added PLSR-only precheck gate before fallback import:
    - builds PLSR quarter scope,
    - compares XML activities vs existing labels,
    - skips fallback shapefile import when no missing labels are present.
  - Added precheck timing log output (`PLSR precheck: ... ms`).
  - Optimized `FindExistingDispositionPolylinesWithObjectData(...)`:
    - removed expensive per-entity closed-boundary cloning,
    - switched to extents-based scope prefilter + OD check,
    - added scan timing log output.
  - Replaced regex in `IsLikelyDispositionLineLayer(...)` with equivalent fast string checks.
- Added helper in `Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - `HasPotentialMissingPlsrLabels(...)` for import gating logic.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (`0` errors, existing nullable warnings).

## Follow-up (/debug-config Logging Overhead Optimization, 2026-02-28)

- [x] Reduce high-volume debug-config logging overhead without removing targeted diagnostics.
- [x] Buffer logger disk writes to avoid per-line flush cost.
- [x] Move heavy wellsite debug command-line output behind explicit opt-in.
- [x] Rebuild/compile to verify safety after logger changes.

## Review (/debug-config Logging Overhead Optimization, 2026-02-28)

- Updated `Core/Plugin.cs`:
  - Added `ATSBUILD_WELLSITE_DEBUG` env toggle to gate `WELLSITE DEBUG` `editor.WriteMessage(...)` + logger spam.
  - Logger now writes with buffered flushes (`FlushIntervalLines=64`) instead of `AutoFlush=true` per line.
  - Immediate flush retained for key terminal messages (`ATSBUILD exit stage`, summary, PLSR log write).
  - Added default suppression prefixes for high-volume trace lines:
    - `TRACE-LSD-PAIRY`
    - `TRACE-LSD-CLAMP`
    - `TRACE-RELAYER id=`
    - existing `TRACE-LSD-CORR` suppression kept.
  - Logger null-safety cleanup to avoid nullable warning in new path.
- Verification:
  - `.\.local_dotnet\dotnet.exe msbuild src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj /t:Compile /p:Configuration=Release` succeeded (`0` errors, existing nullable warnings).
  - Full `build` also succeeded; observed only existing warnings plus locked-output copy retries when `build\net8.0-windows\AtsBackgroundBuilder.dll` was in use.

## Follow-up (UI Add Sections From Boundary, 2026-03-01)

- [x] Add a shared boundary-import service that prompts for a closed boundary and resolves fully-contained `L-QUATER` polygons to SEC/TWP/RGE/MER + quarter rows.
- [x] Add `ADD SECTIONS FROM BDY` button to WPF ATSBUILD window and append non-duplicate grid rows from boundary import results.
- [x] Add parity `ADD SECTIONS FROM BDY` button/handler to WinForms ATSBUILD form.
- [x] Compile `AtsBackgroundBuilder` using `/t:Compile` to verify compile safety.

## Review (UI Add Sections From Boundary, 2026-03-01)

- Added `Core/BoundarySectionImportService.cs`:
  - prompts for one closed boundary polyline in CAD,
  - filters `L-QUATER` closed polygons fully within the boundary (extents containment + interior sampling),
  - reads section metadata from `L-SECLBL` attributes (`SEC`, `TWP`, `RGE`, `MER`),
  - infers `NW/NE/SW/SE` by quarter interior point relative to section label center,
  - returns normalized, sorted section-grid entries.
- Updated `Core/AtsBuildWindow.cs`:
  - added `ADD SECTIONS FROM BDY` action button,
  - hides window for CAD selection, restores it after prompt,
  - appends only non-duplicate section rows and shows summary.
- Updated `Core/AtsBuildForm.cs` with matching behavior for WinForms path.
- Verification:
  - `.\.local_dotnet\dotnet.exe msbuild src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj /t:Compile /p:Configuration=Release /p:RestoreIgnoreFailedSources=true` succeeded (`0` errors; existing nullable warnings remain in unrelated files).

## Follow-up (Boundary Import Must Work Pre-Build, 2026-03-01)

- [x] Remove dependency on pre-existing `L-QUATER` drawing entities for `ADD SECTIONS FROM BDY`.
- [x] Derive quarter definitions in-memory from section index geometry for selected zone, then filter to quarters fully inside selected boundary.
- [x] Ensure the boundary import action does not leave temporary quarter helper entities visible in the drawing.
- [x] Rebuild/compile after pre-build flow change.

## Review (Boundary Import Must Work Pre-Build, 2026-03-01)

- Updated `Sections/SectionIndexReader.cs`:
  - Added `SectionOutlineEntry` and `TryLoadSectionOutlinesForZone(...)` to enumerate all section outlines for a zone from cached index files.
- Added `Core/Plugin.Core.BoundaryImport.cs`:
  - Internal wrappers to reuse existing quarter-map generation and quarter-token mapping from `Plugin` quarter utilities.
- Reworked `Core/BoundarySectionImportService.cs`:
  - Prompts for boundary polyline.
  - Loads zone section outlines from section index search folders.
  - Builds quarter polygons in-memory via existing quarter utilities.
  - Keeps only quarter polygons fully inside selected boundary.
  - Returns deduplicated `M/RGE/TWP/SEC/HQ` rows; draws nothing to model space.
- Updated UI callers:
  - `Core/AtsBuildWindow.cs` and `Core/AtsBuildForm.cs` now pass current `Config` + selected `Zone` into boundary import.
- Verification:
  - `.\.local_dotnet\dotnet.exe msbuild src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj /t:Compile /p:Configuration=Release /p:RestoreIgnoreFailedSources=true` succeeded (`0` errors; existing warnings remain unrelated).

## Follow-up (PLSR Crash After Boundary Rows + XML Selection, 2026-03-01)

- [x] Harden PLSR missing-label source matching so one bad candidate geometry cannot crash the run.
- [x] Add immediate-flush stage breadcrumbs for ATSBUILD + PLSR to pinpoint any remaining hard-fail stage.
- [x] Guard quarter/disposition cached bounds against invalid `GeometricExtents` reads.
- [x] Rebuild and confirm output DLL artifacts are synchronized.

## Review (PLSR Crash After Boundary Rows + XML Selection, 2026-03-01)

- Updated `Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - Added `PLSR stage: ...` breadcrumbs via `SetStage(...)`.
  - Hardened `TryFindDispositionSourceForQuarterDisp(...)`:
    - added `Logger` parameter,
    - wrapped safe-point and overlap geometry checks in per-candidate try/catch,
    - skipped only failing candidates instead of aborting the check.
- Updated `Dispositions/LabelPlacer.cs`:
  - `QuarterInfo` and `DispositionInfo` now build cached bounds via guarded extents resolution:
    - try `GeometricExtents`,
    - fallback to vertex-derived extents,
    - final safe default extents when neither is available.
- Updated `Core/Plugin.cs`:
  - Added `SetExitStage(...)` breadcrumb logging (`ATSBUILD stage: ...`) throughout command flow.
  - Logger immediate flush gate now includes:
    - `ATSBUILD stage:`
    - `PLSR stage:`
- Verification:
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (`0` errors; existing nullable warnings unchanged).
  - Output parity confirmed:
    - `src\AtsBackgroundBuilder\bin\Release\net8.0-windows\AtsBackgroundBuilder.dll`
    - `build\net8.0-windows\AtsBackgroundBuilder.dll`

## Follow-up (Unhandled E0434352 Guardrails, 2026-03-01)

- [x] Add top-level ATSBUILD fatal catch with stage-aware failure logging and graceful command exit.
- [x] Add section-build start stage marker to isolate faults before `sections_built`.
- [x] Add UI event-handler try/catch guards for boundary import and build submission in both WPF/WinForms windows.
- [x] Add additional unhandled exception hooks for WinForms/WPF UI threads.
- [x] Rebuild release output to verify compile safety.

## Review (Unhandled E0434352 Guardrails, 2026-03-01)

- Updated `Core/Plugin.cs`:
  - Wrapped post-UI ATSBUILD execution in a top-level `try/catch` that logs and surfaces:
    - `ATSBUILD failed at stage '<stage>'`.
  - Added explicit `ATSBUILD stage: sections_building` marker before section draw pipeline call.
  - Added unhandled UI thread hooks:
    - `System.Windows.Forms.Application.ThreadException`
    - `System.Windows.Application.DispatcherUnhandledException` (marks handled after logging).
  - Extended immediate log flush prefixes with:
    - `ATSBUILD failed at stage`
- Updated `Core/AtsBuildWindow.cs` and `Core/AtsBuildForm.cs`:
  - Wrapped `OnAddSectionsFromBoundary()` and `OnBuild()` in defensive `try/catch`.
  - Added best-effort UI exception append to `AtsBackgroundBuilder.crash.log` for post-mortem.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (`0` errors; existing warnings unchanged).

## Follow-up (WPF DialogResult Crash Guard, 2026-03-01)

- [x] Fix WPF `DialogResult` assignment path so build/cancel works when window is not launched via `ShowDialog()`.
- [x] Rebuild and sync active runtime DLL after dialog-result guard change.

## Review (WPF DialogResult Crash Guard, 2026-03-01)

- Updated `Core/AtsBuildWindow.cs`:
  - Added `CloseAsDialogResultOrWindow(bool)` helper.
  - Replaced direct `DialogResult = ...; Close();` in `Cancel` and `OnBuild()` success path with guarded helper.
  - If window is modeless, falls back to `Close()` without throwing `InvalidOperationException`.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (`0` errors; existing warnings unchanged).
  - Synced DLL to active runtime path:
    - `C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows\AtsBackgroundBuilder.dll`

## Follow-up (UI Cancel Gate Regression, 2026-03-01)

- [x] Fix ATSBUILD UI acceptance logic so modeless-safe close path still executes build when UI produced valid input.
- [x] Rebuild and sync runtime DLL so `/debug-config` runs new gate logic.

## Review (UI Cancel Gate Regression, 2026-03-01)

- Updated `Core/Plugin.cs`:
  - Changed UI cancellation gate from `dr != true || window.Result == null` to `window.Result == null`.
  - Added diagnostic log when `DialogResult` is not `true` but `window.Result` is present, then proceeds.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (`0` errors; existing warnings unchanged).
  - Synced DLL parity confirmed at:
    - `src\AtsBackgroundBuilder\bin\Release\net8.0-windows\AtsBackgroundBuilder.dll`
    - `build\net8.0-windows\AtsBackgroundBuilder.dll`
    - `C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows\AtsBackgroundBuilder.dll`

## Follow-up (UI Recovery Refactor, 2026-03-01)

- [x] Extract no-result UI recovery decision logic from `Core/Plugin.cs` into `Core/UiSessionRecoveryService.cs`.
- [x] Preserve ATSBUILD behavior and log strings for auto-close recovery, boundary round-trip reopen, snapshot fallback, and cancel path.
- [x] Keep persisted PLSR option/path restore logic in the refactored service path.
- [x] Rebuild with Release settings to verify compile safety.

## Review (UI Recovery Refactor, 2026-03-01)

- Updated `Core/Plugin.cs`:
  - Replaced inline no-result decision branches with `UiSessionRecoveryService.EvaluateNoResult(...)`.
  - Preserved existing observable logs and side effects for:
    - auto-close reopen with snapshot,
    - boundary round-trip reopen without snapshot,
    - snapshot-based build fallback,
    - cancel path handling.
  - Switched persisted PLSR option/path fallback restoration to `UiSessionRecoveryService.TryRestorePersistedPlsrSelectionFromAutoCloseFallback(...)`.
- Verified compile safety:
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (`0` errors; existing warnings unchanged).

## Follow-up (Boundary Reopen Should Not Auto-Enable PLSR Toggles, 2026-03-01)

- [x] Diagnose why `PLSR Check` and `Surface Impact` can appear ON by default after boundary round-trip reopen.
- [x] Stop applying persisted PLSR option state as constructor-time UI defaults in WPF window.
- [x] Keep persisted-option restore logic only in backend recovery fallback path.
- [x] Rebuild Release to verify compile safety.

## Review (Boundary Reopen Should Not Auto-Enable PLSR Toggles, 2026-03-01)

- Updated `Core/AtsBuildWindow.cs`:
  - removed constructor block that auto-applied `TryGetPersistedPlsrOptionSelection(...)` when `seedInput == null`.
  - result: boundary round-trip reopen without a seed no longer turns `PLSR Check`/`Surface Impact` on from persisted state.
- Preserved existing fallback behavior:
  - persisted PLSR options are still restored only through `UiSessionRecoveryService.TryRestorePersistedPlsrSelectionFromAutoCloseFallback(...)` in recovery scenarios.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (`0` errors; existing warnings unchanged).

## Follow-up (Build Plan + Recovery Decision Tests Refactor, 2026-03-01)

- [x] Extract a `BuildExecutionPlan` in `Core` to centralize ATSBUILD option-to-pipeline decision logic.
- [x] Refactor `Core/Plugin.cs` to consume the execution plan for import/label/PLSR/surface-impact gates.
- [x] Extract pure no-result UI decision logic into a testable decision engine independent of UI persistence paths.
- [x] Add automated decision-matrix tests for no-result UI recovery behavior.
- [x] Build/re-run verification for main project plus decision tests.

## Review (Build Plan + Recovery Decision Tests Refactor, 2026-03-01)

- Added `Core/BuildExecutionPlan.cs`:
  - centralizes ATSBUILD option-derived pipeline decisions (imports, labels, PLSR, quarter behavior, surface impact).
  - exposes focused helper gates used by plugin flow (`ShouldImportDispositions`, PLSR precheck gate, supplemental-section-info gate).
- Updated `Core/Plugin.cs`:
  - replaced direct option branching with `BuildExecutionPlan` usage for:
    - quarter draw/processing setup,
    - shape auto-update gate,
    - P3/Compass/CLR import gates,
    - disposition import and PLSR-only precheck decisions,
    - quarter load, label placement ordering, PLSR check, quarter section labels, and surface impact gates.
- Added pure decision engine `Core/UiSessionRecoveryDecisionEngine.cs` and updated `Core/UiSessionRecoveryService.cs`:
  - no-result UI recovery decision logic now lives in a pure, testable unit.
  - persisted PLSR fallback restore remains in service path.
- Added automated decision-matrix tests:
  - project: `src/AtsBackgroundBuilder.DecisionTests`
  - runner: `Program.cs`
  - covers reopen/cancel/recover matrix and log-flag expectations for no-intent closes, boundary round-trip resumes, build-attempt traces, and explicit cancel cases.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (`0` errors; existing warnings unchanged).
  - `.\.local_dotnet\dotnet.exe restore src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj --configfile src/AtsBackgroundBuilder.DecisionTests/NuGet.Config` succeeded.
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` succeeded.
  - `.\.local_dotnet\dotnet.exe run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed (`UiSessionRecoveryDecisionEngine tests passed.`).

## Follow-up (BuildExecutionPlan Decision Tests, 2026-03-01)

- [x] Extend the decision test project to compile `Core/BuildExecutionPlan.cs` without AutoCAD/UI dependencies.
- [x] Add focused tests for BuildExecutionPlan gates (quarter visibility, disposition scope/import, label ordering, precheck/supplemental gates, pass-through flags).
- [x] Re-run decision test restore/build/run verification.

## Review (BuildExecutionPlan Decision Tests, 2026-03-01)

- Updated test project `src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj`:
  - linked `Core/BuildExecutionPlan.cs` into the test runner.
  - retained pure runner setup with no external test packages.
- Added stub input model `src/AtsBackgroundBuilder.DecisionTests/TestStubs/AtsBuildInputStub.cs`:
  - minimal `AtsBuildInput` surface needed by `BuildExecutionPlan.Create(...)`.
- Extended `src/AtsBackgroundBuilder.DecisionTests/Program.cs` with BuildExecutionPlan coverage:
  - default plan behavior
  - quarter visibility (`UI 1/4` toggle vs env override)
  - PLSR-driven scope/import behavior
  - label placement ordering gate
  - PLSR missing-label precheck gate
  - supplemental section-info gate
  - pass-through flag mapping
- Verification:
  - `.\.local_dotnet\dotnet.exe restore src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj --configfile src/AtsBackgroundBuilder.DecisionTests/NuGet.Config` succeeded.
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` succeeded.
  - `.\.local_dotnet\dotnet.exe run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed (`Decision tests passed.`).

## Follow-up (Plugin Execution Flow Method Split, 2026-03-01)

- [x] Extract disposition/import preparation block from `AtsBuild()` into a dedicated helper method.
- [x] Extract summary emission block from `AtsBuild()` into a dedicated helper method.
- [x] Keep stage transitions, logs, and behavior unchanged.
- [x] Rebuild plugin and rerun decision tests.

## Review (Plugin Execution Flow Method Split, 2026-03-01)

- Updated `Core/Plugin.cs`:
  - Added `PrepareDispositionInputs(...)` to isolate:
    - optional P3/Compass/Crown import stages,
    - disposition scope build,
    - PLSR fallback scan,
    - PLSR missing-label precheck gate,
    - shapefile import selection and result capture.
  - Added `EmitBuildSummary(...)` to isolate command-line + log summary output.
  - Added `DispositionPreparationResult` carrier type for the extracted preparation stage.
  - `AtsBuild()` now orchestrates via helper calls while preserving existing stage labels and side effects.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (`0` errors; existing warnings unchanged).
  - `.\.local_dotnet\dotnet.exe run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed (`Decision tests passed.`).

## Follow-up (Disposition Transaction Block Extraction, 2026-03-01)

- [x] Extract the large in-method disposition transaction processing block from `AtsBuild()` into a dedicated helper.
- [x] Preserve stage label progression, log output, and disposition/label preparation behavior.
- [x] Rebuild plugin and rerun decision tests after extraction.

## Review (Disposition Transaction Block Extraction, 2026-03-01)

- Updated `Core/Plugin.cs`:
  - Added `ProcessDispositionPolylines(...)` helper that encapsulates:
    - supplemental section-info load gate,
    - transaction scope for disposition entities,
    - OD mapping/layer assignment,
    - wellsite/surface-purpose enrichment,
    - disposition label payload preparation.
  - Replaced the former inline transaction body in `AtsBuild()` with a single call to this helper.
  - Kept `SetExitStage("processing_dispositions")` and existing log messages within the extracted path.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (`0` errors; existing warnings unchanged).
  - `.\.local_dotnet\dotnet.exe run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed (`Decision tests passed.`).

## Follow-up (Quarter Context + Existing Label Index Extraction, 2026-03-01)

- [x] Extract quarter cloning/loading and existing label index build from `AtsBuild()` into a dedicated helper.
- [x] Preserve quarter-source precedence (`LabelQuarterInfos` first, fallback to quarter polyline ids).
- [x] Preserve existing label reuse logging and dictionary semantics.
- [x] Rebuild plugin and rerun decision tests.

## Review (Quarter Context + Existing Label Index Extraction, 2026-03-01)

- Updated `Core/Plugin.cs`:
  - Added `BuildQuarterLabelContext(...)` helper to encapsulate:
    - quarter clone loading from transaction,
    - fallback loading from `quarterPolylinesForLabelling`,
    - existing label reuse index build (`existingDispNumsByQuarter`) and log line.
  - Added `QuarterLabelContext` carrier type.
  - Replaced former inline quarter/index block in `AtsBuild()` with a single helper call.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (`0` errors; existing warnings unchanged).
  - `.\.local_dotnet\dotnet.exe run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed (`Decision tests passed.`).

## Follow-up (Post-Quarter Orchestration Extraction, 2026-03-01)

- [x] Extract remaining post-quarter orchestration from `AtsBuild()` into a dedicated helper.
- [x] Preserve stage sequencing for label placement, PLSR, quarter labels, cleanup, surface impact, and summary.
- [x] Rebuild plugin and rerun decision tests.

## Review (Post-Quarter Orchestration Extraction, 2026-03-01)

- Updated `Core/Plugin.cs`:
  - Added `ExecutePostQuarterPipeline(...)` helper to encapsulate:
    - pre-PLSR label placement,
    - result import counters assignment,
    - PLSR check dispatch,
    - quarter section labels placement,
    - cleanup + optional GeoJSON export,
    - surface impact run,
    - summary emission.
  - Replaced inline orchestration block in `AtsBuild()` with single helper invocation.
  - Preserved outer completion stages (`completed`, `EmitExit("ok")`) and command finalization.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (`0` errors; existing warnings unchanged).
  - `.\.local_dotnet\dotnet.exe run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed (`Decision tests passed.`).

# Follow-up (ATS Fabric Should Keep 1/4 Linework, 2026-03-02)

- [x] Confirm quarter linework visibility/cleanup gates in build plan and cleanup pipeline.
- [x] Update quarter-visibility decision so ATS Fabric implies visible 1/4 linework.
- [x] Update cleanup gate so ATS-on does not erase quarter helper/view linework when 1/4 Definitions is off.
- [x] Add decision test coverage for ATS-driven quarter visibility.
- [x] Build plugin and run decision tests.

## Review (ATS Fabric Should Keep 1/4 Linework, 2026-03-02)

- Updated `src/AtsBackgroundBuilder/Core/BuildExecutionPlan.cs`:
  - `ShowQuarterDefinitionLinework` now enables when any of these are true:
    - `IncludeAtsFabric`
    - `AllowMultiQuarterDispositions`
    - `enableQuarterViewByEnvironment`
- Updated `src/AtsBackgroundBuilder/Diagnostics/Plugin.Diagnostics.CleanupDiagnostics.cs`:
  - quarter helper/view erase now runs only when all are false:
    - `IncludeAtsFabric`
    - `AllowMultiQuarterDispositions`
    - `EnableQuarterViewByEnvironment`
  - effect: ATS sections ON keeps 1/4 linework visible even if UI `1/4 Definitions` is OFF.
- Updated `src/AtsBackgroundBuilder.DecisionTests/Program.cs`:
  - extended `TestBuildExecutionPlanQuarterVisibility()` with an ATS-enabled case to assert `ShowQuarterDefinitionLinework == true`.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (ATS On Should Keep 1/4 Lines But Hide L-QUATER When 1/4 Definitions Off, 2026-03-02)

- [x] Reproduce behavior split: ATS-on preserved desired 1/4 lines but also kept `L-QUATER` display when `1/4 Definitions` was off.
- [x] Split cleanup gating so `L-QUATER` follows `1/4 Definitions`/env only, while ATS can still preserve internal 1/4 helper lines.
- [x] Rebuild plugin and rerun decision tests.

## Review (ATS On Should Keep 1/4 Lines But Hide L-QUATER When 1/4 Definitions Off, 2026-03-02)

- Updated `src/AtsBackgroundBuilder/Diagnostics/Plugin.Diagnostics.CleanupDiagnostics.cs`:
  - Introduced `showQuarterDefinitionByToggleOrEnv = input.AllowMultiQuarterDispositions || EnableQuarterViewByEnvironment`.
  - `L-QUATER` cleanup now keys only off that value:
    - if `showQuarterDefinitionByToggleOrEnv` is false, erase `LayerQuarterView` entities in section scope.
  - `L-QSEC` helper-line cleanup remains ATS-aware:
    - erase only when ATS fabric is off and quarter-definition display is off.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (Quarter Visibility Policy Refactor, 2026-03-02)

- [x] Extract shared quarter visibility decision policy for toggle/env/ATS combinations.
- [x] Use shared policy in `BuildExecutionPlan` and cleanup path to remove duplicated condition logic.
- [x] Add decision tests for full policy matrix and update build-plan visibility expectation.
- [x] Rebuild plugin and rerun decision tests.

## Review (Quarter Visibility Policy Refactor, 2026-03-02)

- Added `src/AtsBackgroundBuilder/Core/QuarterVisibilityPolicy.cs`:
  - `ShowQuarterDefinitionView` (controls `L-QUATER` visibility)
  - `KeepQuarterHelperLinework` (controls `L-QSEC` helper cleanup retention)
  - `Create(includeAtsFabric, allowMultiQuarterDispositions, enableQuarterViewByEnvironment)` for single-source decision logic.
- Updated `src/AtsBackgroundBuilder/Core/BuildExecutionPlan.cs`:
  - now uses `QuarterVisibilityPolicy` for `ShowQuarterDefinitionLinework` instead of ad-hoc condition logic.
- Updated `src/AtsBackgroundBuilder/Diagnostics/Plugin.Diagnostics.CleanupDiagnostics.cs`:
  - now uses `QuarterVisibilityPolicy` for both:
    - `L-QUATER` cleanup gate
    - `L-QSEC` helper-line cleanup gate
- Updated decision tests:
  - `src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj` now links `QuarterVisibilityPolicy.cs`.
  - `src/AtsBackgroundBuilder.DecisionTests/Program.cs`:
    - added `TestQuarterVisibilityPolicyMatrix()` covering OFF/OFF, toggle-only, ATS-only, env-only.
    - updated `TestBuildExecutionPlanQuarterVisibility()` ATS-only expectation to `ShowQuarterDefinitionLinework == false` (matches `L-QUATER` display semantics).
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (Cleanup Plan Refactor, 2026-03-02)

- [x] Extract cleanup branching decisions into a pure `CleanupPlan` helper.
- [x] Wire `CleanupAfterBuild(...)` to execute plan outputs rather than inline toggle branching.
- [x] Add decision tests for cleanup matrix scenarios (ATS off/on, 1/4 off/on, disposition linework on/off).
- [x] Rebuild plugin and rerun decision tests.

## Review (Cleanup Plan Refactor, 2026-03-02)

- Added `src/AtsBackgroundBuilder/Core/CleanupPlan.cs`:
  - Encapsulates cleanup decisions as pure booleans:
    - `EraseQuarterDefinitionQuarterView`
    - `EraseQuarterDefinitionHelperLines`
    - `EraseQuarterBoxes`
    - `EraseQuarterHelpers`
    - `EraseSectionOutlines`
    - `EraseContextSectionPieces`
    - `EraseSectionLabels`
    - `EraseDispositionLinework`
  - `CleanupPlan.Create(...)` derives behavior from `AtsBuildInput` + `QuarterVisibilityPolicy`.
- Updated `src/AtsBackgroundBuilder/Diagnostics/Plugin.Diagnostics.CleanupDiagnostics.cs`:
  - computes `quarterVisibility` + `cleanupPlan` up front.
  - applies cleanup operations via plan flags.
  - avoids duplicate `QuarterHelperEntityIds` erase when ATS-off already erases full helper set.
- Updated decision tests wiring:
  - `src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj` links `Core/CleanupPlan.cs`.
  - `src/AtsBackgroundBuilder.DecisionTests/Program.cs` adds `TestCleanupPlanMatrix()` and runs it in `RunAll()`.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (Native Disposition Import Crash Guard for Large SHP/DBF, 2026-03-02)

- [x] Inspect latest ATSBUILD log to identify crash stage for `ATS Fabric + Dispositions + Labels` run.
- [x] Add pre-import guard to skip known high-risk large native shapefile imports instead of calling `Importer.Import()`.
- [x] Keep behavior overridable via env for explicit force-run.
- [x] Rebuild plugin and rerun decision tests.

## Review (Native Disposition Import Crash Guard for Large SHP/DBF, 2026-03-02)

- Root-cause signal from log:
  - run terminated after `Importer.Import begin.` with no `Importer.Import completed.` / no ATSBUILD exit marker.
  - indicates host-level native Map importer crash boundary.
- Updated `src/AtsBackgroundBuilder/Dispositions/ShapefileImporter.cs`:
  - added guarded native-size thresholds:
    - `MaxNativeImporterDbfBytesWithoutOverride = 512 MB`
    - `MaxNativeImporterShpBytesWithoutOverride = 256 MB`
  - added per-shapefile guard before native import:
    - `ShouldSkipLargeNativeDispositionImport(...)`
    - if exceeded, logs explicit skip reason and increments import failure instead of invoking `Importer.Import()`.
  - added env override for deliberate force-run:
    - `ATSBUILD_ALLOW_LARGE_DISPOSITION_IMPORT=1`
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (ATS-Fabric-ON Crash on Generated Subset Import, 2026-03-02)

- [x] Inspect newest `/debug-config` ATS Fabric ON crash log boundary.
- [x] Replace default large-file single-subset import path with chunked source import (stable path).
- [x] Keep subset-file import available only through explicit env opt-in.
- [x] Rebuild plugin and rerun decision tests.

## Review (ATS-Fabric-ON Crash on Generated Subset Import, 2026-03-02)

- Root-cause boundary from latest local log:
  - ATS Fabric ON run stopped at:
    - `Spatial subset import enabled for 'DAB_APPL.shp': kept 112/415688 record(s).`
    - `Shapefile import for 'DAB_APPL.shp' is using prefiltered subset input; location window disabled.`
    - `Importer.Import begin.`
  - no `Importer.Import completed.` / no `ATSBUILD exit stage`, indicating host termination while importing generated subset input.
- Updated `src/AtsBackgroundBuilder/Dispositions/ShapefileImporter.cs`:
  - Added env-gated behavior:
    - new opt-in env var: `ATSBUILD_ENABLE_SINGLE_SUBSET_IMPORT=1`.
    - default (`unset`): skip generated single-subset import files for large shapefiles.
  - Default large-file mode now:
    - use chunked source import (`TryCreateChunkedSubsetShapefiles(...)`) with existing section-window filtering and post-filter logic.
    - log explicit safety-mode message with env override instructions.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (LSD 20.12 vs 30.18 Boundary Regression, 2026-03-02)

- [x] Trace regression path for LSD endpoints terminating on 30.18-class boundaries.
- [x] Patch west-boundary selector to promote 20.12-class candidates over `L-USEC-0` when available.
- [x] Patch south-boundary selector to promote 20.12-class candidates over `L-USEC-0` when available.
- [x] Rebuild plugin and rerun decision tests.

## Review (LSD 20.12 vs 30.18 Boundary Regression, 2026-03-02)

- Updated `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - Added preferred west-boundary promotion pass when initial source resolves to `L-USEC-0`.
  - Added preferred south-boundary promotion pass when initial source resolves to `L-USEC-0` (non-blind south path).
  - Preferred candidate set: `L-USEC2012`, `L-USEC-2012`, `L-USEC`, `L-SEC`, `L-SEC-2012`.
  - For both passes, selector now tries:
    1) existing expected inset behavior, then
    2) a `0.0` offset fallback to handle normalized-edge cases where inset is already consumed.
  - South promotion is now deterministic once a preferred candidate resolves (instead of requiring an error-delta threshold).
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (Large DAB_APPL Safe Spatial-Subset Import, 2026-03-02)

- [x] Replace crash-prone large-file native import path with temporary spatial subset generation for disposition shapefiles above safe size threshold.
- [x] Keep OD table naming tied to original shapefile name while importing from subset path.
- [x] Add cleanup for temporary subset artifacts and explicit diagnostics (`kept/total` records).
- [x] Rebuild plugin and rerun decision tests.

## Review (Large DAB_APPL Safe Spatial-Subset Import, 2026-03-02)

- Updated `src/AtsBackgroundBuilder/Dispositions/ShapefileImporter.cs`:
  - Added large-file route `ShouldUseSpatialSubsetImport(...)` (uses existing 256 MB SHP / 512 MB DBF thresholds).
  - Added subset generation pipeline:
    - reads SHX record index,
    - spatial-filters records against requested section extents,
    - writes temporary filtered `.shp/.shx/.dbf` under `%TEMP%\AtsBackgroundBuilder\shape-subsets\...`,
    - copies optional `.prj/.cpg` sidecars,
    - logs `kept/total` counts.
  - Updated native import call sites to:
    - import from subset path when applicable,
    - preserve logical OD table naming and shapefile identity using original source path,
    - delete temporary subset folder in `finally`.
  - Safety behavior for large files:
    - if subset prep fails, skip source native import instead of falling back to crash-prone `Importer.Import()` on the full source file.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (Chunked Safe Import Fallback When Raw Spatial Filter Misses, 2026-03-02)

- [x] Investigate `/debug-config` log showing `Spatial subset import skipped ... no records intersect requested scope`.
- [x] Add fallback path so large-file import does not skip when raw-coordinate filtering misses.
- [x] Keep crash-safe behavior by avoiding direct full-source native import.
- [x] Rebuild plugin and rerun decision tests.

## Review (Chunked Safe Import Fallback When Raw Spatial Filter Misses, 2026-03-02)

- Updated `src/AtsBackgroundBuilder/Dispositions/ShapefileImporter.cs`:
  - If large-file raw spatial subset has zero matches, importer now falls back to chunked safe import instead of skipping `DAB_APPL`.
  - Added `TryCreateChunkedSubsetShapefiles(...)`:
    - builds sequential temporary SHP/SHX/DBF chunks from source records,
    - imports each chunk with existing location-window logic,
    - keeps OD table naming bound to original source shapefile.
  - Added chunk-size control env var:
    - `ATSBUILD_LARGE_IMPORT_CHUNK_RECORDS` (default `50000`, clamped to safe min/max).
  - Added chunk-progress logging and temporary folder cleanup for all generated chunk sets.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (ATS Fabric OFF Crash During Chunked Large Import, 2026-03-02)

- [x] Inspect latest `/debug-config` crash tail for ATS-off path.
- [x] Reduce per-chunk large-import default size to lower peak MPOLYGON/memory pressure.
- [x] Rebuild and rerun decision tests.

## Review (ATS Fabric OFF Crash During Chunked Large Import, 2026-03-02)

- Root-cause boundary from latest log:
  - ATS-off run reached chunked large import and stopped immediately after:
    - `Chunked safe import enabled for 'DAB_APPL.shp': 9 chunk(s).`
    - `Importer.Import completed.` (chunk 1)
  - no later summary/exit marker, indicating post-import chunk processing instability under large per-chunk volume.
- Updated `src/AtsBackgroundBuilder/Dispositions/ShapefileImporter.cs`:
  - reduced default `ATSBUILD_LARGE_IMPORT_CHUNK_RECORDS` from `50000` to `10000`.
  - lowered min clamp from `2000` to `1000`.
  - intent: reduce per-chunk imported MPOLYGON counts and memory pressure in ATS-off path.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (Disposition Import Scope Buffer +100m, 2026-03-02)

- [x] Confirm current disposition import scope buffer behavior.
- [x] Update ATS disposition import call to use `sections + 100m` buffer.
- [x] Rebuild plugin and rerun decision tests.

## Review (Disposition Import Scope Buffer +100m, 2026-03-02)

- Updated `src/AtsBackgroundBuilder/Core/Plugin.cs`:
  - main `ShapefileImporter.ImportShapefiles(...)` call now passes `scopeBufferMeters: 100.0`.
  - this applies to the primary ATS disposition import flow (independent of ATS Fabric on/off).
- Expected runtime log change:
  - `Section extents loaded: <n> (buffer 100).`
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (CRS-Aware Spatial Subset for Geographic Disposition SHP, 2026-03-02)

- [x] Identify why large-file spatial subset keeps missing valid in-scope dispositions.
- [x] Add coordinate-aware subset logic to transform section extents when source SHP is geographic and drawing extents are projected.
- [x] Wire zone hint from ATS UI/input into primary shapefile import path.
- [x] Wire zone hint for compass-mapping import path.
- [x] Rebuild plugin and rerun decision tests.

## Review (CRS-Aware Spatial Subset for Geographic Disposition SHP, 2026-03-02)

- Root cause:
  - `DAB_APPL.shp` header/prj is geographic (`lon/lat`, approx `X=-120..-110`, `Y=49..60`), while ATS section extents are projected UTM coordinates.
  - Raw record-bounds subset filtering in drawing coordinates returned zero matches, forcing expensive chunked fallback (`42` chunks at 10k records/chunk).
- Updated `src/AtsBackgroundBuilder/Dispositions/ShapefileImporter.cs`:
  - `ImportShapefiles(...)` now accepts optional `utmZoneHint`.
  - During spatial subset prep, detects source geographic bounds and projected section extents.
  - Converts section extents from UTM -> geographic (zone-aware) before record-bounds intersection.
  - Logs CRS transform application/fallback explicitly.
- Updated call sites:
  - `src/AtsBackgroundBuilder/Core/Plugin.cs`: passes `utmZoneHint: input.Zone` for main disposition import.
  - `src/AtsBackgroundBuilder/Core/Plugin.Core.ImportWindowing.cs`: passes `utmZoneHint: zone` for compass mapping import.
- Expected runtime behavior:
  - For `DAB_APPL`, logs should show CRS transform applied and `Spatial subset import enabled ... kept X/Y` instead of immediate chunk fallback.
  - ATS Fabric ON runs should be materially faster because import volume is reduced before native Map import/conversion.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (ATS-Fabric-OFF Stability Gate for CRS Subset Path, 2026-03-02)

- [x] Investigate `/debug-config` crash report occurring with ATS Fabric OFF after CRS-subset optimization.
- [x] Gate CRS-subset zone hint to ATS Fabric ON runs only.
- [x] Keep ATS Fabric OFF on the prior chunked-safe fallback behavior.
- [x] Rebuild plugin and rerun decision tests.

## Review (ATS-Fabric-OFF Stability Gate for CRS Subset Path, 2026-03-02)

- Root-cause boundary from latest local log:
  - ATS Fabric OFF run stopped at:
    - `Spatial subset CRS transform applied for 'DAB_APPL.shp'...`
    - `Spatial subset import enabled ... kept 112/...`
    - `Importer.Import begin.`
  - no completion/exit marker afterward, indicating a host-level native importer crash boundary on that path.
- Updated `src/AtsBackgroundBuilder/Core/Plugin.cs`:
  - main disposition import now passes:
    - `utmZoneHint: executionPlan.IncludeAtsFabric ? input.Zone : (int?)null`
  - effect:
    - ATS Fabric ON: keeps CRS-aware subset acceleration.
    - ATS Fabric OFF: disables CRS transform hint and reverts to previous chunked-safe path behavior.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (LSD Endpoint 30.18 Escape Hardening, 2026-03-02)

- [x] Re-check LSD endpoint fallback chain for `L-USEC-3018` survivors.
- [x] Add relaxed 30.18 escape fallback while preserving zero/20 side preference.
- [x] Rebuild plugin and rerun decision tests.

## Review (LSD Endpoint 30.18 Escape Hardening, 2026-03-02)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.EndpointEnforcement.cs`:
  - Added stronger 30.18 escape tuning constants in LSD hard-boundary enforcement:
    - `thirtyEscapeLateralTol = 90.0`
    - `thirtyEscapeMaxMove = 140.0`
  - Extended helper signatures with optional overrides for 30.18 handling:
    - `TryFindPreferredHardBoundaryMidpoint(..., maxMoveOverride)`
    - `TryFindPreferredHardBoundaryMidpointRelaxed(..., maxMoveOverride)`
    - `TryFindNearestHardBoundaryPoint(..., lateralTolOverride, maxMoveOverride, allowBacktrack)`
  - In both `p0OnThirty` and `p1OnThirty` branches:
    - widened preferred-midpoint search window,
    - added relaxed nearest hard-boundary fallback,
    - and added `TryFindSnapTarget(...)` fallback when preferred paths fail.
  - In final LSD invariant pass:
    - widened `nearThirtyTol` from `1.25` to `3.00`,
    - applied the same widened 30.18 escape chain for both horizontal and vertical endpoints,
    - added final relaxed nearest hard-boundary fallback with `preferZero: null` to prevent 30.18 persistence when preferred side candidates are absent.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (LSD Boundary Segment Collection Robustness, 2026-03-02)

- [x] Investigate why LSD 30.18 endpoint fix showed no visible behavior change.
- [x] Harden LSD boundary collection to include non-collinear polyline segments (and closed polylines) for layer scans.
- [x] Rebuild plugin and rerun decision tests.

## Review (LSD Boundary Segment Collection Robustness, 2026-03-02)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.EndpointEnforcement.cs` in `EnforceLsdLineEndpointsOnHardSectionBoundaries(...)`:
  - replaced single-segment-only boundary collection with per-segment entity extraction:
    - `Line`: one segment
    - `Polyline`: every adjacent segment; if closed, also closing segment
  - retained strict `TryReadOpenSegment` use for identifying movable LSD source lines (`L-SECTION-LSD`) so adjustment scope remains deterministic.
  - applied scope filtering per extracted segment, then fed layer-classified segment sets (`hardBoundarySegments`, `thirtyBoundarySegments`, QSEC targets) from those scoped segments.
- Expected effect:
  - `L-USEC-3018` boundaries represented as segmented/closed polylines are now visible to LSD endpoint rules and final 30.18 invariant snap.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).
# Follow-up (Nullable Warning Cleanup + Build Simulation, 2026-03-02)

- [x] Add targeted null-safety guards for reported CS8602 warnings.
- [x] Rebuild solution in Release and confirm warning status.
- [x] Document review notes and reproducible build-simulation commands.

## Review (Nullable Warning Cleanup + Build Simulation, 2026-03-02)

- Updated `src/AtsBackgroundBuilder/Core/Plugin.cs`:
  - added null-safe logger writes at warning sites in `DrawSectionsFromRequests(...)`.
- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.PlsrReviewDialog.cs`:
  - guarded `BindingContext` before indexing in the Apply handler (`bindingContext != null && bindingContext[rows] is CurrencyManager`).
- Verification:
  - `C:\Program Files\dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore`
  - result: `Build succeeded`, `0 Warning(s)`, `0 Error(s)`.
# Follow-up (ATS-Wide Section Invariant Validator Scaffolding, 2026-03-02)

- [x] Add explicit section-building invariant spec for ATS-wide pre-AutoCAD validation.
- [x] Implement headless JSONL-driven batch validator CLI for township/all-township runs.
- [x] Document validator usage and pass/fail gating workflow.
- [x] Run smoke checks and record verification results.

## Review (ATS-Wide Section Invariant Validator Scaffolding, 2026-03-02)

- Added spec `docs/specs/sections/ATS_WIDE_VALIDATION.md`:
  - defines tiered validation strategy (`Tier 0` to `Tier 3`),
  - enumerates stable invariant IDs (`INV-T0-*`, `INV-T1-*`),
  - sets default gating thresholds for ATS-wide batch checks.
- Added runner `ats_viewer/validator.py`:
  - supports `--township` and `--all-townships`,
  - evaluates township invariants from JSONL section data,
  - writes `validation_summary.json`, `validation_summary.md`, and `validation_failures.csv`,
  - returns non-zero exit code when any township fails.
- Updated docs:
  - `ats_viewer/README.md` includes validator commands and exit-code behavior.
  - `docs/README.md` indexes the new ATS-wide validation spec.
- Verification:
  - `python -m ats_viewer.validator --help` succeeded.
  - `python -m py_compile ats_viewer/validator.py ats_viewer/cli.py ats_viewer/streamlit_app.py` succeeded.
  - `python -m ats_viewer.validator --township "TWP 1 RGE 1 W5" --zone 11 --out out-validate-smoke` failed as expected in this environment due missing runtime dependency (`shapely`/`pyproj`).

# Follow-up (One-Command Ops Gate Script, 2026-03-02)

- [x] Add a single PowerShell script that runs calibrated ops validation gate.
- [x] Document gate command and default thresholds in root README.
- [x] Verify script execution on existing summary outputs.

## Review (One-Command Ops Gate Script, 2026-03-02)

- Added `scripts/run-ops-gate.ps1`:
  - runs zone validator sweeps (`z11`, `z12`) unless skipped,
  - applies ops filter (`TWP >= 50` and `sections >= 30`),
  - fails when any township violates:
    - unmatched ratio threshold,
    - zero accepted pairs,
    - sections-with-gt2-unmatched threshold.
  - writes `out-validate\ops-gate-failures.csv`.
- Updated `README.md` with one-command usage and default gate behavior.
- Verification:
  - `powershell -ExecutionPolicy Bypass -File .\scripts\run-ops-gate.ps1 -SkipZ11 -SkipZ12`
  - output: `Ops checked: 4832`, `Ops failed: 0`.

# Follow-up (Integrate WLS Program as Standalone Module, 2026-03-03)

- [x] Create a dedicated `wls_program/` module in this repository and import WLS source/docs/assets.
- [x] Exclude nested-repo/tool/build artifacts from the imported module (`.git`, `.vs`, `bin`, `obj`, local dotnet cache folders).
- [x] Update root docs so WLS module location/build workflow is explicit.
- [x] Verify solution-level build entry points for both modules still resolve from the new layout.

## Review (Integrate WLS Program as Standalone Module, 2026-03-03)

- Added new standalone module root:
  - `wls_program/`
  - includes WLS source, docs, lookup assets, and legacy workflow archive source snapshot.
- Import hygiene:
  - excluded nested repository/tooling/build folders during import:
    - `.git`
    - `.vs`
    - `.dotnet-codex-test`
    - `bin`
    - `obj`
  - removed copied workspace-clutter bundle under:
    - `wls_program/archive/2026-03-03-workspace-clutter`
- Documentation updates:
  - updated root `README.md` with WLS module location + build command.
  - updated `wls_program/README.md` with:
    - integration context,
    - recommended reuse patterns for pulling shared code from `src/AtsBackgroundBuilder`,
    - linked-file include example for quick function reuse.
- Verification:
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:n` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; dotnet restore .\wls_program\src\WildlifeSweeps\WildlifeSweeps.sln /m:1 -v:n` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.sln -c Release /m:1 -v:n` succeeded.

# Follow-up (WLS Table LOCATION from ATS Quarter Resolution, 2026-03-03)

- [x] Add ATS-style section index resolver for point-to-quarter matching in WLS (no ATS linework drawing).
- [x] Populate WLS table `LOCATION` cells from resolved quarter/location token per finding.
- [x] Log match summary (resolved/unresolved) for generated findings.
- [x] Build WLS solution and confirm compile success.

## Review (WLS Table LOCATION from ATS Quarter Resolution, 2026-03-03)

- Reused ATS index-reading code directly in WLS:
  - linked `src/AtsBackgroundBuilder/Sections/SectionIndexReader.cs` into WLS project.
  - added compatibility logger shim `wls_program/src/WildlifeSweeps/AtsLoggerShim.cs` (`AtsBackgroundBuilder.Logger`).
- Added WLS resolver `wls_program/src/WildlifeSweeps/AtsQuarterLocationResolver.cs`:
  - loads ATS section outlines for active UTM zone from section index folders,
  - performs point-in-section lookup,
  - resolves quarter (`NW/NE/SW/SE`) using section-local orientation,
  - outputs table location token format: `<QTR> <SEC>-<TWP>-<RGE>-W<MER>`.
- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`:
  - initializes resolver once per run using selected UTM zone,
  - assigns resolved location token when creating each `PhotoPointRecord`,
  - fills table `LOCATION` column from `record.Location`,
  - logs summary: `ATS location match: X/Y finding(s) resolved to quarter/section.`
- Updated WLS data model:
  - `PhotoPointRecord` now includes `Location`.
- Verification:
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.sln -c Release /m:1 -v:n` succeeded.

# Follow-up (WLS Optional L-QUARTER Validation Linework Toggle, 2026-03-03)

- [x] Add a persisted WLS setting for optional quarter validation linework output.
- [x] Add a Complete From Photos UI checkbox to toggle L-QUARTER validation output.
- [x] Extend ATS quarter resolver output to return matched quarter polygon geometry.
- [x] Draw unique matched quarter polygons on `L-QUARTER` during Complete From Photos when enabled.
- [x] Build WLS solution and confirm compile success.

## Review (WLS Optional L-QUARTER Validation Linework Toggle, 2026-03-03)

- Updated `wls_program/src/WildlifeSweeps/PluginSettings.cs`:
  - added `CompleteFromPhotosIncludeQuarterLinework` (`false` default).
- Updated `wls_program/src/WildlifeSweeps/Ui/PaletteControl.cs`:
  - added checkbox `Include L-QUARTER linework`,
  - wired setting load/save in `TryUpdateSettings(...)` and `ApplyCompleteFromPhotosBufferMode(...)`,
  - added tooltip describing visual quarter-assignment validation intent.
- Updated `wls_program/src/WildlifeSweeps/AtsQuarterLocationResolver.cs`:
  - added `TryResolveQuarterMatch(...)` returning both:
    - location token (`NW/NE/SW/SE SEC-TWP-RGE-WMER`),
    - quarter polygon vertices based on section-local orientation.
- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`:
  - collects unique quarter polygons by resolved location token while inserting findings,
  - when toggle is enabled, draws matched quarter polygons as closed polylines on `L-QUARTER`,
  - ensures `L-QUARTER` exists (ACI 30),
  - writes result messages:
    - section index unavailable,
    - no quarter matches,
    - or count of drawn validation polygons.
- Verification:
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.sln -c Release --no-restore /m:1 -v:n` succeeded.

# Follow-up (WLS SORT 100m buffer Photos Button + Copy Workflow, 2026-03-03)

- [x] Add a new WLS UI button for sorting/copying in-buffer photos.
- [x] Implement service workflow: pick closed buffer, pick photo folder, create `within 100m`, copy matching GPS photos.
- [x] Reuse WLS UTM conversion flow (zone prompt + lat/lon -> UTM projection) for buffer inclusion test.
- [x] Build WLS solution and confirm compile success.

## Review (WLS SORT 100m buffer Photos Button + Copy Workflow, 2026-03-03)

- Added new service `wls_program/src/WildlifeSweeps/SortBufferPhotosService.cs`:
  - prompts for closed polyline buffer,
  - prompts for photo folder selection,
  - prompts for UTM zone (`11/12`) using existing WLS pattern,
  - reads JPG/JPEG EXIF GPS metadata,
  - projects photo coordinates to UTM,
  - copies in-buffer photos to `<selected folder>\within 100m`,
  - logs summary counts (copied/outside/skipped/copy failures/overwritten).
- Updated `wls_program/src/WildlifeSweeps/Ui/PaletteControl.cs`:
  - added button `SORT 100m buffer Photos`,
  - wired click to `RunSortBufferPhotos()`,
  - added tooltip explaining the workflow.
- Verification:
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.sln -c Release --no-restore /m:1 -v:n` succeeded.

# Follow-up (WLS BUFFERS: PROPOSED / 100m Dual-Buffer + Dual-Block Flow, 2026-03-03)

- [x] Update `BUFFERS: PROPOSED / 100m` mode to prompt for two boundaries (`PROPOSED` and `100m`).
- [x] Update same mode to prompt for two sample blocks (one per boundary zone).
- [x] Ensure nested matches do not duplicate inserts (PROPOSED takes precedence when point is in both).
- [x] Build WLS solution and confirm compile success.

## Review (WLS BUFFERS: PROPOSED / 100m Dual-Buffer + Dual-Block Flow, 2026-03-03)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`:
  - for `IncludeBufferExcludeOutside` mode:
    - prompts for `PROPOSED` boundary,
    - prompts for `100m` boundary,
    - prompts for `PROPOSED` block and `100m-only` block.
  - added zone classifier (`Proposed`, `HundredMeter`, `Outside`) with precedence:
    - if inside both boundaries, classify as `Proposed` to avoid duplicate placements.
  - insert phase now picks block by zone for this mode:
    - `Proposed -> proposed block`
    - `HundredMeter -> 100m-only block`
    - `Outside -> skipped`
  - existing `PROPOSED / 100m / OUTSIDE` mode behavior remains unchanged.
- Updated tooltip text in `wls_program/src/WildlifeSweeps/Ui/PaletteControl.cs` for `BUFFERS: PROPOSED / 100m` to describe dual-buffer/dual-block behavior and duplicate prevention.
- Verification:
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.sln -c Release --no-restore /m:1 -v:n` succeeded.

# Follow-up (WLS Quarter Assignment Must Use L-QUATER Boundaries + Dotted Tokens, 2026-03-03)

- [x] Prioritize `L-QUATER`/`L-QUARTER` polygon containment for WLS finding location assignment.
- [x] Keep ATS section-index fallback when no quarter-layer polygon match exists.
- [x] Format table quarter token as `N.W.`, `N.E.`, `S.W.`, `S.E.` instead of `NW/NE/SW/SE`.
- [x] Rebuild WLS to verify compile (using alternate output path when runtime DLL is locked).

## Review (WLS Quarter Assignment Must Use L-QUATER Boundaries + Dotted Tokens, 2026-03-03)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`:
  - loads closed quarter polygons from both `L-QUATER` and `L-QUARTER`,
  - resolves each finding by quarter-layer polygon containment first,
  - falls back to ATS section-index quarter resolution only when no layer polygon contains the finding.
- Updated `wls_program/src/WildlifeSweeps/AtsQuarterLocationResolver.cs`:
  - quarter token display is now dotted in table location strings:
    - `NW -> N.W.`
    - `NE -> N.E.`
    - `SW -> S.W.`
    - `SE -> S.E.`
- Verification:
  - direct release build remains blocked by runtime file lock:
    - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.sln -c Release --no-restore /m:1 -v:n`
    - failed with `MSB3027/MSB3021` (`WildlifeSweeps.dll` locked in `bin\Release\net8.0-windows`).
  - compile verified to alternate output path:
    - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath="C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.artifacts\wls_alt\"`
    - succeeded.

# Follow-up (WLS LSD Location + Photo Caption + Proposed/100m Table Grouping, 2026-03-03)

- [x] Change WLS location output from quarter token to LSD-style legal location format.
- [x] Add second caption line under photo label with the same finding text used in table rows.
- [x] Force `PHOTO #` caption label text green (without forcing number text color to green).
- [x] For `BUFFERS: PROPOSED / 100m`, order findings `PROPOSED` first then `100m`, and insert one blank table row between groups.
- [x] Build WLS project to verify compile success.

## Review (WLS LSD Location + Photo Caption + Proposed/100m Table Grouping, 2026-03-03)

- Updated `wls_program/src/WildlifeSweeps/AtsQuarterLocationResolver.cs`:
  - added LSD resolver output (`TryResolveLsdMatch`) using ATS section-local 4x4 LSD row/col mapping with serpentine numbering:
    - even south-rows count right-to-left,
    - odd south-rows count left-to-right.
  - location strings now support LSD format:
    - `<LSD>-<SEC>-<TWP>-<RGE>-W<MER>`.
- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`:
  - switched finding location assignment to LSD resolution,
  - kept quarter-layer containment (`L-QUATER`/`L-QUARTER`) as first containment path,
  - changed summary wording to `resolved to LSD/section`,
  - added mode-aware ordering helper so `IncludeBufferExcludeOutside` (`PROPOSED / 100m`) is always:
    - all proposed findings first (direction-sorted),
    - then all 100m-only findings (direction-sorted),
  - extended table generation to inject one blank spacer row at proposed->100m boundary.
- Updated `wls_program/src/WildlifeSweeps/PhotoLayoutHelper.cs`:
  - extended `PhotoLayoutRecord` with caption text,
  - caption now renders:
    - line 1: `PHOTO #<n>` with only `PHOTO #` forced green via MText inline color,
    - line 2: finding description (matching table text),
  - escapes MText control characters in caption content.
- Verification:
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath="C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.artifacts\wls_alt\"`
  - succeeded.

# Follow-up (WLS Quarter Source Priority + Photo Label Style Offset, 2026-03-03)

- [x] Make WLS quarter-boundary matching prefer ATS `L-QUATER` polygons over validation `L-QUARTER`.
- [x] Keep `L-QUARTER` as fallback only when `L-QUATER` is unavailable.
- [x] Stop adding synthetic resolver quarter polygons to validation linework output.
- [x] Make photo captions all-caps and fully green.
- [x] Move photo caption label down an additional 24m.
- [x] Rebuild WLS solution and confirm compile success.

## Review (WLS Quarter Source Priority + Photo Label Style Offset, 2026-03-03)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`:
  - `LoadQuarterLayerMatches(...)` now scans both layers but uses this precedence:
    - `L-QUATER` first (ATS road-allowance quarter geometry),
    - `L-QUARTER` only if no `L-QUATER` polygons exist.
  - runtime message now reports the actual source layer in use.
  - validation linework capture now stores only polygons coming from matched quarter-layer geometry (no synthetic fallback polygons from section-index quarter split).
- Updated `wls_program/src/WildlifeSweeps/PhotoLayoutHelper.cs`:
  - label insertion Y offset changed from `-24.0` to `-48.0` (additional 24m down),
  - caption text now uppercases via `ToUpperInvariant()`,
  - entire caption (`PHOTO #` line + finding line) now uses green text color.
- Verification:
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.sln -c Release --no-restore /m:1 -v:n`
  - succeeded.

# Follow-up (WLS Proposed/100m Spacer Row Border + Height, 2026-03-03)

- [x] Update the proposed->100m spacer row to remove side/vertical borders.
- [x] Keep only top and bottom borders on that spacer row.
- [x] Set proposed->100m spacer row height to `125`.
- [x] Rebuild WLS project to verify compile success.

## Review (WLS Proposed/100m Spacer Row Border + Height, 2026-03-03)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`:
  - `ConfigureGroupSpacerRow(...)` now:
    - sets row height to `125.0`,
    - clears text,
    - hides `Left`, `Right`, and `Vertical` borders,
    - keeps `Top` and `Bottom` borders visible.
- Verification:
  - standard build attempted but blocked by DLL lock in `bin\Release\net8.0-windows\WildlifeSweeps.dll` (`MSB3027/MSB3021`).
  - alternate output build succeeded:
    - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath="C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.artifacts\wls_alt\"`

# Follow-up (WLS ATS Quarter Source Selection + L-QUARTER Boundary Fidelity, 2026-03-03)

- [x] Load both ATS quarter polygon layers (`L-QUATER` and `L-QUARTER`) instead of locking source choice up-front.
- [x] Select the quarter source that matches the most active findings (tie-break by polygon count, then ATS `L-QUATER`).
- [x] Harden quarter-layer LSD resolution with per-finding ATS metadata fallback and near-boundary matching tolerance.
- [x] Build WLS project to verify compile success.

## Review (WLS ATS Quarter Source Selection + L-QUARTER Boundary Fidelity, 2026-03-03)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`:
  - replaced single-source loader with `LoadQuarterLayerSources(...)` to ingest both `L-QUATER` and `L-QUARTER` polygon sets.
  - added `TrySelectQuarterLayerSource(...)` to score each source against actual active findings and auto-pick the best-matching layer for the current run.
  - improved quarter match robustness:
    - `TryResolveFromQuarterLayer(...)` now applies a bounded near-miss fallback (`2.0m`) when strict containment misses due tiny slivers/gaps.
  - improved quarter-layer LSD output path:
    - `TryResolveLsdLocationFromQuarterLayer(...)` now accepts resolver context and fills missing quarter/ATS tokens from per-finding `TryResolveQuarterMatch(...)`.
  - updated run-time messaging to show discovered quarter sources and the selected source hit count.
  - kept validation linework capture tied to matched quarter polygons and removed unnecessary ATS-index gating for the capture dictionary.
- Verification:
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath="C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.artifacts\wls_alt\"`
  - succeeded (`0 Warning(s)`, `0 Error(s)`).

# Follow-up (WLS L-QUARTER Toggle Drew Nothing - Diagnostic Fallback, 2026-03-03)

- [x] Add fallback quarter-source selection when scoring returns no direct containment hits.
- [x] Add nearest-quarter diagnostic capture for validation linework when strict containment misses.
- [x] Improve validation status messages to distinguish no-source vs no-associated-polygons cases.
- [x] Rebuild WLS project in a fresh alternate output path to verify compile success.

## Review (WLS L-QUARTER Toggle Drew Nothing - Diagnostic Fallback, 2026-03-03)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`:
  - added `TryGetPreferredQuarterLayerSource(...)` fallback to keep quarter source selection deterministic (`L-QUATER` preferred).
  - added `TryResolveNearestQuarterLayerForDiagnostics(...)` and per-finding capture so validation drawing still gets candidate quarter polygons when strict containment misses.
  - added tracking/reporting for:
    - direct quarter containment matches,
    - nearest diagnostic adds.
  - updated linework messaging to report no-source vs no-associated diagnostics.
- Verification:
  - first build attempt to previous alt output path failed on locked output DLL (`MSB3027/MSB3021`).
  - rebuilt to a fresh path and succeeded:
    - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath="C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.artifacts\wls_alt_20260303_1600\"`
  - succeeded (`0 Warning(s)`, `0 Error(s)`).

# Follow-up (WLS Enforce ATS L-QUATER-Only Quarter Source, 2026-03-03)

- [x] Restrict WLS quarter source loading to ATS quarter-view layer `L-QUATER` only.
- [x] Remove fallback quarter-source loading from `L-QUARTER` for location/section determination.
- [x] Keep validation draw output on `L-QUARTER` (orange) while sourcing geometry strictly from `L-QUATER` matches.
- [x] Rebuild WLS project in fresh alt output path and verify success.

## Review (WLS Enforce ATS L-QUATER-Only Quarter Source, 2026-03-03)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`:
  - `LoadQuarterLayerSources(...)` now loads only closed polylines from `L-QUATER` (ATS quarter-view source).
  - removed `L-QUARTER` from source-selection input for location matching.
  - updated no-source message to explicitly report missing ATS `L-QUATER` polygons.
  - retained optional validation drawing to `L-QUARTER` orange layer using matched ATS-source quarter polygons.
- Verification:
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath="C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.artifacts\wls_alt_20260303_1610\"`
  - succeeded (`0 Warning(s)`, `0 Error(s)`).

# Follow-up (WLS Use L-QUARTER Source for Validation, 2026-03-03)

- [x] Re-enable quarter source loading from `L-QUARTER` for Complete From Photos matching/validation.
- [x] Prioritize `L-QUARTER` over `L-QUATER` during source selection.
- [x] Update no-source message text to reference `L-QUARTER` expectation.
- [x] Rebuild WLS project to fresh output path and verify success.

## Review (WLS Use L-QUARTER Source for Validation, 2026-03-03)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`:
  - `LoadQuarterLayerSources(...)` now loads both layers again and captures `L-QUARTER` matches.
  - source ordering now places `L-QUARTER` first.
  - tie-break priority in source scoring/preferred fallback now favors `L-QUARTER`.
  - no-source linework message updated to `no L-QUARTER source polygons were found`.
- Verification:
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath="C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.artifacts\wls_alt_20260303_1620\"`
  - succeeded (`0 Warning(s)`, `0 Error(s)`).

# Follow-up (WLS Canonical Quarter Layer Name Only, 2026-03-03)

- [x] Remove `L-QUATER` legacy alias from WLS quarter source-loading logic.
- [x] Keep only `L-QUARTER` as the source layer for quarter matching and validation selection.
- [x] Rebuild WLS project to a fresh output path and verify success.

## Review (WLS Canonical Quarter Layer Name Only, 2026-03-03)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`:
  - removed `QuarterLayerLegacyName` constant and all `L-QUATER` branching.
  - `LoadQuarterLayerSources(...)` now scans only closed polylines on `L-QUARTER`.
  - quarter source list now contains only canonical `L-QUARTER` source entries.
- Verification:
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath="C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.artifacts\wls_alt_20260303_1630\"`
  - succeeded (`0 Warning(s)`, `0 Error(s)`).

# Follow-up (WLS L-QUARTER Draw Fallback from ATS Resolver, 2026-03-03)

- [x] Add resolver-based quarter polygon fallback when no quarter-layer source polygons are available.
- [x] Keep quarter validation linework output on `L-QUARTER` (orange) while sourcing fallback geometry from ATS quarter resolver.
- [x] Rebuild WLS project in Release and verify successful compile.

## Review (WLS L-QUARTER Draw Fallback from ATS Resolver, 2026-03-03)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`:
  - when a finding does not resolve to any loaded quarter-layer polygon, `TryResolveLsdMatch(...)` now also contributes `QuarterVertices` into the validation-draw polygon set.
  - added `resolver fallback adds` metric in linework status output to confirm fallback path usage.
  - retained existing direct-match and nearest-diagnostic counters.
- Verification:
  - `dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release`
  - succeeded (`0 Warning(s)`, `0 Error(s)`).

# Follow-up (WLS ATS Section-Build Quarter Generation Fallback, 2026-03-03)

- [x] Add fallback that triggers ATS section build pipeline when quarter source polygons are missing.
- [x] Reuse ATS `DrawSectionsFromRequests` + ATS cleanup to generate L-QUATER definitions and remove temporary sectioning linework.
- [x] Reload quarter polygon sources after ATS generation and continue normal WLS location/linework flow.
- [x] Rebuild WLS Release DLL and verify compile success.

## Review (WLS ATS Section-Build Quarter Generation Fallback, 2026-03-03)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`:
  - Added `TryGenerateQuarterSourcesViaAtsBuild(...)` reflection path to invoke ATS internal section build (`DrawSectionsFromRequests`) when no `L-QUARTER/L-QUATER` source polygons exist.
  - Added ATS cleanup invocation (`CleanupAfterBuild`) configured to remove temporary ATS build linework while preserving quarter definitions (`L-QUATER`).
  - Added source-summary helper reuse and post-generation quarter-source reload.
- Verification:
  - `dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release`
  - succeeded (`0 Warning(s)`, `0 Error(s)`).

# Follow-up (NE.NE Quarter Corner Crossing RA in 9-65-3-W6, 2026-03-04)

- [x] Reproduce baseline for `SEC 9 TWP 65 RGE 3 W6` and capture debug/simulation artifacts.
- [x] Trace `N.E. N.E.` corner authority path in section drawing and identify the crossing condition.
- [x] Implement a targeted fix that prevents RA-crossing NE corner placement without broad tolerance regressions.
- [x] Rebuild ATS and verify with Python viewer/simulation output before/after comparison.

## Review (NE.NE Quarter Corner Crossing RA in 9-65-3-W6, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - tightened non-correction `N.E. N.E.` fallback behavior in the `isBlindNonCorrectionSouth` branch.
  - removed blind "highest east endpoint" fallback when endpoint-node resolution fails.
  - added gated endpoint scoring that only accepts an east endpoint if it has north/horizontal node evidence (`HasHorizontalEndpointNode(...)`) or near-north-boundary touch.
  - if no valid endpoint node evidence exists, keeps prior NE candidate and logs `VERIFY-QTR-NE-NE-ENDPT-FALLBACK-SKIP`.
- Verification:
  - `dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release -nologo`
  - succeeded (`0 Warning(s)`, `0 Error(s)`).
  - Python viewer baseline + post-change export run for section `9-65-3-W6`:
    - `py -m ats_viewer --sections "9-65-3-W6" --zone auto --debug --road-width-targets "20.11,30.17" --out ".\out-debug\sec-9-65-3-w6-before"`
    - `py -m ats_viewer --sections "9-65-3-W6" --zone auto --debug --road-width-targets "20.11,30.17" --out ".\out-debug\sec-9-65-3-w6-after"`
  - Note: `ats_viewer` validates index/edge-pair inference and does not execute ATS AutoCAD quarter-corner heuristics; before/after viewer artifacts are unchanged and serve as secondary scope sanity only.
- Iteration after user retest reported "no changes":
  - Added stricter non-correction NE handling so `N.E. N.E.` uses strict east????????????north segment intersection (`VERIFY-QTR-NE-NE-STRICT`) instead of apparent infinite-line intersection.
  - Enabled quarter verify logging for section 9 to expose the exact NE authority path during user retest.
  - Rebuilt and redeployed `AtsBackgroundBuilder.dll` + `.pdb` to `C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows`.

# Follow-up (NE Corner Refactor Cleanup, 2026-03-04)

- [x] Extract non-correction NE endpoint fallback scoring into dedicated helpers.
- [x] Replace inline/local logic in `SectionDrawingLsd` with helper calls while preserving behavior.
- [x] Rebuild ATS and confirm compile stability after refactor.

## Review (NE Corner Refactor Cleanup, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - replaced deeply nested non-correction NE fallback block with `TryResolveNonCorrectionNorthEastFromEastEndpoints(...)`.
  - extracted scoring and endpoint-node checks to focused helpers:
    - `TryScoreNonCorrectionNorthEastEndpointCandidate(...)`
    - `HasQuarterViewHorizontalEndpointNode(...)`
    - `IsQuarterViewHorizontalSegment(...)`
  - preserved existing verification log strings and decision behavior.
- Verification:
  - `dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release -nologo`
  - succeeded (`0 Warning(s)`, `0 Error(s)`).

# Follow-up (Quarter Ownership Full 20.12 Baseline, 2026-03-04)

- [x] Switch quarter ownership inset target from `RoadAllowanceUsecWidthMeters - RoadAllowanceSecWidthMeters` to full `RoadAllowanceUsecWidthMeters`.
- [x] Update east-boundary selector to prefer inward west-side candidates using expected inset targeting, with legacy near-edge fallback.
- [x] Rebuild ATS and redeploy DLL/PDB to COMPASS plugin folder.
- [ ] User retest on baseline section(s) (`6-65-3-W6`, then `12-65-3-W6`) and confirm south/west/east quarter boundaries.

- Iteration after baseline report (`south 10.05 below RA`, `west using east boundary`):
  - Added west/south boundary fallback resolution ladder: full-width inset -> SEC-width inset -> near-edge fallback, preventing synthetic raw-offset fallback when real candidates exist.
  - Kept east-side inward selection and full-width targeting, with near-edge fallback retained.
  - Rebuilt and redeployed ATS DLL/PDB for immediate user retest.
- Iteration after user retest (`south good`, `west still missing RA where south surveyed + west unsurveyed`):
  - Gated west inset downgrade paths (`20.12 -> 10.05 -> 0`) to blind-south sections only.
  - Prevented preferred-west promotion from downgrading to zero-offset fallback when south is surveyed.
  - Rebuilt and redeployed ATS DLL/PDB for user validation.
- Iteration after next retest (`west fixed`, `south no longer includes RA`):
  - Removed south downgrade ladder that could demote surveyed south from full-width ownership (`20.12`) to SEC/near-edge candidates.
  - Restored direct south boundary resolution against the active ownership target only.
  - Rebuilt and redeployed ATS DLL/PDB for immediate retest.
- Iteration after next retest (`south ~10.06 too far south`):
  - Corrected quarter ownership inset class from `RoadAllowanceUsecWidthMeters` (`30.16`) to `RoadAllowanceSecWidthMeters` (`20.11`) for quarter boundary targeting.
  - Updated both frame-level quarter inset targeting and NE hard-corner inset scoring to use SEC-width ownership.
  - Rebuilt and redeployed ATS DLL/PDB for immediate retest.
- Iteration after next retest (`west now ~10.06 east of target`):
  - Split ownership offsets by side: kept south/east on SEC-width (`20.11`) while restoring west expected inset to USEC-width (`30.16`).
  - Updated west fallback source tag for clarity (`fallback-30.16`).
  - Rebuilt and redeployed ATS DLL/PDB for immediate retest.
- Iteration after next retest (`6-64-3-W6 south 30.18 treated as 20.12`, `32-64-3-W6 west treated as 20.12`):
  - Switched west/south ownership resolution to layer-guided candidate pools (`L-SEC`, `L-SEC-2012`, `L-USEC-0`) before broad fallbacks.
  - Added side-specific USEC-zero detection to choose 30.16 vs 20.11 ownership target per side instead of one global inset.
  - Prevented forced zero-layer demotion on sides already classified as USEC-width ownership.
  - Rebuilt and redeployed ATS DLL/PDB for immediate retest.

# Follow-up (ATS Quarter Ownership Policy Extraction, 2026-03-04)

- [x] Extract west/south quarter ownership target computation into a pure policy helper.
- [x] Wire `SectionDrawingLsd` quarter-view boundary setup to consume the policy output.
- [x] Add decision tests for blind/surveyed ownership combinations and `L-USEC-0` candidate signals.
- [x] Rebuild ATS and run decision tests.

## Review (ATS Quarter Ownership Policy Extraction, 2026-03-04)

- Added `src/AtsBackgroundBuilder/Core/QuarterBoundaryOwnershipPolicy.cs`:
  - pure policy helper for:
    - west expected ownership offset (`SEC` vs `USEC`),
    - south fallback offset (including blind-south forced zero behavior),
    - west inset downgrade gating,
    - fallback source labels (`fallback-20.12`, `fallback-30.16`, `fallback-blind`).
- Updated `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - replaced inline west/south ownership-offset computation with `QuarterBoundaryOwnershipPolicy.Create(...)`.
  - preserved existing downstream boundary resolution flow and fallback retries.
- Updated decision tests:
  - linked new helper in `src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj`.
  - added tests in `src/AtsBackgroundBuilder.DecisionTests/Program.cs`:
    - `TestQuarterBoundaryOwnershipPolicySurveyedSecFallback`
    - `TestQuarterBoundaryOwnershipPolicySurveyedUsecOwnership`
    - `TestQuarterBoundaryOwnershipPolicyBlindSouthForcesZeroSouthOffset`
- Verification:
  - `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n`
  - `dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release`
  - build and tests succeeded.

# Follow-up (ATS Preferred Boundary Helper Extraction, 2026-03-04)

- [x] Extract preferred west-boundary resolution local function into a dedicated section helper.
- [x] Extract preferred south-boundary resolution local function into a dedicated section helper.
- [x] Wire `SectionDrawingLsd` to the extracted helpers without behavior changes.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS Preferred Boundary Helper Extraction, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - extracted local preferred-west resolver into:
    - `TryResolvePreferredQuarterViewWestBoundary(...)`
  - extracted local preferred-south resolver into:
    - `TryResolvePreferredQuarterViewSouthBoundary(...)`
  - replaced in-method local-function calls with calls to the new helper methods.
  - preserved all existing fallback behavior:
    - first pass at expected inset,
    - zero-offset regression fallback when applicable.
- Verification:
  - `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n`
  - `dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release`
  - build and tests succeeded.

# Follow-up (ATS East Boundary Helper Extraction, 2026-03-04)

- [x] Extract preferred east-boundary resolution local function into a dedicated section helper.
- [x] Wire `SectionDrawingLsd` to the extracted east helper without behavior changes.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS East Boundary Helper Extraction, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - extracted local preferred-east resolver into:
    - `TryResolvePreferredQuarterViewEastBoundarySegment(...)`
  - replaced in-method local-function call with helper invocation.
  - preserved existing fallback behavior:
    - preferred layer pass at expected inset,
    - near-edge fallback pass at `0.0` inset.
- Verification:
  - `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n`
  - `dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release`
  - build and tests succeeded.

# Follow-up (Sync main + Re-apply WLS Quarter Visibility Refactor, 2026-03-04)

- [x] Pull latest `origin/main` into local `main`.
- [x] Re-apply WLS `L-QUARTER` visibility-off cleanup and ATS assembly diagnostics on top of pulled code.
- [x] Build `WildlifeSweeps` in Release to verify compile after rebase.
- [x] Build `AtsBackgroundBuilder` in Release to verify latest ATS fixes are compiled locally.
- [ ] User retest in AutoCAD: run with `1/4 Definitions` off and confirm quarter linework is hidden after completion.

## Review (Sync main + Re-apply WLS Quarter Visibility Refactor, 2026-03-04)

- Pulled latest `origin/main` via `git pull --rebase origin main` (fast-forward to `089c2fc`).
- Re-applied in `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`:
  - post-run `finally` enforcement for quarter linework visibility when `1/4 Definitions` is off.
  - layer visibility helpers for `L-QUARTER`/legacy validation layer handling.
  - ATS fallback assembly diagnostic line that reports resolved assembly name/version/source path.
- Verification:
  - `dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath="C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.artifacts\wls_alt_20260304_rebased\"`
  - `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n`
  - both succeeded (`0 Warning(s)`, `0 Error(s)`).

# Follow-up (ATS Blind-South East Refinement Helper Extraction, 2026-03-04)

- [x] Extract blind-south east-boundary north/south refinement block into a dedicated helper.
- [x] Preserve east mid-U projection fallback behavior inside the extracted helper.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS Blind-South East Refinement Helper Extraction, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - added `TryResolveQuarterViewBlindEastBoundaryFromNorthSouth(...)`.
  - moved blind-south east-boundary refinement + mid-U projection/fallback logic into the helper.
  - callsite now consumes helper output `resolvedEastMidU` and keeps existing verify logging/counters unchanged.
- Verification:
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n`
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release`
  - build and tests succeeded.

# Follow-up (ATS West Boundary Fallback Ladder Extraction, 2026-03-04)

- [x] Extract west-boundary offset fallback ladder into a dedicated helper.
- [x] Rewire section drawing callsite to use helper output only.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS West Boundary Fallback Ladder Extraction, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - added `TryResolveQuarterViewWestBoundaryWithInsetFallbacks(...)`.
  - moved west boundary fallback sequence into helper:
    - expected ownership inset attempt,
    - SEC-width retry when USEC-width candidate path misses,
    - near-edge `0.0` fallback when downgrade is allowed.
  - simplified callsite to assign resolved west boundary fields from helper output.
- Verification:
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n`
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release`
  - build and tests succeeded.

# Follow-up (ATS East Mid-U Helper Extraction, 2026-03-04)

- [x] Extract preferred east-boundary selection + mid-U derivation into a dedicated helper.
- [x] Keep existing mid-U fallback order (segment midpoint, then projection at frame mid-V).
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS East Mid-U Helper Extraction, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - added `TryResolvePreferredQuarterViewEastBoundary(...)`.
  - moved the callsite????????????s inline east mid-U calculation into the helper while reusing existing `TryResolvePreferredQuarterViewEastBoundarySegment(...)` behavior.
  - preserved assignment/logging behavior at the callsite (now consumes `preferredEastMidU`).
- Verification:
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n`
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release`
  - build and tests succeeded.

# Follow-up (ATS Preferred Boundary Promotion Helper Extraction, 2026-03-04)

- [x] Extract west `L-USEC-0` promotion-to-preferred-layers block into a helper.
- [x] Extract surveyed-south `L-USEC-0` promotion-to-SEC block into a helper.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS Preferred Boundary Promotion Helper Extraction, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - added `TryResolveQuarterViewPreferredWestBoundaryFromSections(...)`.
  - added `TryResolveQuarterViewPreferredSouthBoundaryFromSections(...)`.
  - replaced inline west/south promotion condition + candidate filtering + resolve calls with helper invocations.
  - preserved promotion gates and layer preferences:
    - west: only when current source is `L-USEC-0`.
    - south: only when non-blind section, `L-USEC-0` source, and SEC-width ownership class.
- Verification:
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n`
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release`
  - build and tests succeeded.

# Follow-up (ATS Quarter Verify Diagnostics Helper Extraction, 2026-03-04)

- [x] Extract quarter boundary selection verify logging block into a dedicated helper.
- [x] Keep log wording, metrics, and conditions unchanged.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS Quarter Verify Diagnostics Helper Extraction, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - added `WriteQuarterBoundarySelectionDiagnostics(...)`.
  - moved inline `emitQuarterVerify` diagnostics for south/north/west boundary selection into helper.
  - preserved existing log lines/fields:
    - `VERIFY-QTR-SOUTH-SELECT`
    - `VERIFY-QTR-NORTH-SELECT`
    - `VERIFY-QTR-WEST-SELECT`
- Verification:
  - initial standard Release build failed due locked output DLL (`MSB3027/MSB3021`) from active process.
  - rebuilt to fresh alternate output path and succeeded:
    - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath="C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.artifacts\ats_alt_20260304_114100\"`
  - decision tests passed:
    - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release`

# Follow-up (ATS Quarter Resolver Helper Continuation, 2026-03-04)

- [ ] Fix stale local resolver call after quarter resolver helper extraction.
- [ ] Rebuild ATS in Release to fresh alternate output path and rerun decision tests.
- [ ] Extract one more low-risk quarter helper from `SectionDrawingLsd` (ATS-only, behavior-preserving).
- [ ] Rebuild ATS in Release to fresh alternate output path and rerun decision tests.
- [ ] Append review notes and verification commands/results.

## Review (ATS Quarter Resolver Helper Continuation, 2026-03-04)

- [x] Fix stale local resolver call after quarter resolver helper extraction.
- [x] Rebuild ATS in Release to fresh alternate output path and rerun decision tests.
- [x] Extract one more low-risk quarter helper from `SectionDrawingLsd` (ATS-only, behavior-preserving).
- [x] Rebuild ATS in Release to fresh alternate output path and rerun decision tests.
- [x] Append review notes and verification commands/results.
- Updated `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - fixed remaining stale call: `ResolveEastBoundaryUAtV(...)` -> `ResolveQuarterEastBoundaryUAtV(...)`.
  - extracted `ResolveQuarterEastMidAtCenter(...)` from inline east-midpoint-at-center logic.
  - extracted `IsQuarterNorthEastCorrectionAdjoining(...)` and replaced repeated inline north-source correction checks.
- Verification:
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath=".artifacts\\ats_alt_20260304_114910\\"`
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath=".artifacts\\ats_alt_20260304_115031\\"`
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and tests succeeded in both passes.

## Review Addendum (ATS Quarter Resolver Helper Continuation, 2026-03-04)

- Added `TryResolveQuarterApparentEastCornerIntersection(...)` and rewired both apparent east-corner callsites:
  - south-east apparent lock path (`VERIFY-QTR-SE-SE-APP`)
  - correction-adjoining north-east apparent lock path (`VERIFY-QTR-NE-NE-APP`)
- Preserved the same offset acceptance bounds (`east: [-90, +90]`, side offset: `[-6, +90]`) while removing duplicated inline gating logic.
- Verification:
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath=".artifacts\\ats_alt_20260304_115250\\"`
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and tests succeeded.

# Follow-up (ATS SectionDrawingLsd Local Helper Exhaustion, 2026-03-04)

- [x] Re-verify current ATS baseline build and decision tests before additional refactor.
- [x] Extract remaining low-risk local helpers from `SectionDrawingLsd` to class-level methods.
- [x] Remove remaining local resolver helpers in quarter-view draw loop by promoting them to class-level methods with unchanged signatures.
- [x] Rebuild ATS and rerun decision tests after extraction batch.

## Review (ATS SectionDrawingLsd Local Helper Exhaustion, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs`:
  - promoted quarter-window/corner helper locals to class-level helpers:
    - `SegmentIntersectsAnyQuarterWindow(...)`
    - `ExtentsIntersectAnyQuarterWindow(...)`
    - `TryFindQuarterVertexSnapTarget(...)`
    - `AddBoundaryEndpointToCornerClusters(...)`
    - `IsPointInAnyQuarterWindow(...)`
    - `TryFindApparentCornerIntersection(...)`
  - promoted deferred-LSD scoped ownership/axis locals to class-level helpers:
    - `TryReadOpenSegmentForDeferredLsd(...)`
    - `IsPointInAnyScopedQuarter(...)`
    - `IsSegmentOwnedByScopedQuarters(...)`
    - `TryGetSectionAxes(...)`
  - promoted remaining quarter resolver locals to class-level helpers:
    - `TryResolveSouthDividerCornerFromHardBoundaries(...)`
    - `TryResolveWestBandCornerFromHardBoundaries(...)`
    - `TryResolveEastBandCornerFromHardBoundaries(...)`
    - `TryResolveNorthEastCornerFromEastHardNode(...)`
    - `TryResolveNorthEastCornerFromEndpointCornerClusters(...)`
  - removed local `WithinExpandedBounds(...)` in `TryIntersectBoundarySegmentsLocal(...)` and reused `IsPointWithinExpandedSegmentBounds(...)`.
  - callsites were rewired to the extracted helpers without algorithm changes.
- Verification:
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath=".artifacts\\ats_alt_20260304_124500\\"`
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath=".artifacts\\ats_alt_20260304_121200\\"`
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath=".artifacts\\ats_alt_20260304_121920\\"`
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - all builds/tests succeeded.

# Follow-up (ATS LsdPostProcessing Helper Consolidation, 2026-03-04)

- [x] Extract duplicated local helper functions in `LsdPostProcessing` to class-level reusable helpers.
- [x] Rewire all three LSD post-processing methods to use extracted helpers with no behavior changes.
- [x] Rebuild ATS and run decision tests.

## Review (ATS LsdPostProcessing Helper Consolidation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Sections/Plugin.Sections.LsdPostProcessing.cs`:
  - removed method-local helper functions and introduced class-level helpers:
    - `IsPointInLsdClipWindows(...)`
    - `DoesSegmentIntersectLsdClipWindows(...)`
    - `TryReadOpenTwoVertexSegmentForLsd(...)`
    - `TryReadOpenCollinearSegmentForLsd(...)`
    - `TryGetEntityCenterForLsd(...)`
    - `IsLsdHorizontalLike(...)`
    - `IsLsdVerticalLike(...)`
    - `TryIntersectLsdSegments(...)`
    - `TryIntersectLsdInfiniteLines(...)`
    - `IsPointInExpandedExtentsForLsd(...)`
  - rewired callsites in:
    - `RebuildLsdLabelsAtFinalIntersections(...)`
    - `SnapQuarterLsdLinesToSectionBoundaries(...)`
    - `RecenterExplodedLsdLabelsToFinalLinework(...)`
  - intent: reduce duplication and keep behavior equivalent for no-AutoCAD-safe refactor.
- Verification:
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath=".artifacts\\ats_alt_20260304_123610\\"`
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded.

# Follow-up (ATS SectionTypeInference Neighbor Helper Extraction, 2026-03-04)

- [x] Extract quarter-neighbor evidence and grid lookup local helpers from `SectionTypeInference`.
- [x] Rewire `InferQuarterSectionTypes(...)` to call extracted class-level helpers.
- [x] Resolve duplicate grid-position helper conflict by reusing existing shared implementation.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS SectionTypeInference Neighbor Helper Extraction, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionTypeInference.cs`:
  - extracted helper methods:
    - `AddQuarterGapSample(...)`
    - `MeasureQuarterGapFromBaseSample(...)`
    - `TryAddQuarterEvidenceFromPair(...)`
    - `BuildSectionGridLookupKey(...)`
    - `TryGetNeighborGeometryIndex(...)`
    - `IsSectionNeighborPairEligible(...)`
  - rewired `InferQuarterSectionTypes(...)` callsites to use extracted helpers.
  - removed duplicate `TryGetAtsSectionGridPosition(...)` from this file and reused existing shared implementation to resolve `CS0111` duplicate member error.
- Verification:
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath=".artifacts\ats_alt_20260304_124950\"`
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded.

# Follow-up (ATS Cross-File Helper Consolidation, 2026-03-04)

- [x] Extract duplicated local helper lambdas/functions from ATS files that do not require AutoCAD behavior validation.
- [x] Rewire callsites to shared class-level helpers without algorithm changes.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS Cross-File Helper Consolidation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Core/Plugin.cs`:
  - extracted `ArePointsNearWithTolerance(...)` and removed duplicated local `Near(...)` lambdas from:
    - `DoSegmentsShareEndpoint(...)`
    - `AreSegmentsDuplicateOrCollinearOverlap(...)`
- Updated `src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs`:
  - extracted `IsPointInRectBounds(...)` and removed local `PointInRect(...)` from `RectangleIntersectsPolyline(...)`.
- Updated `src/AtsBackgroundBuilder/Geometry/Plugin.Geometry.QuarterUtilities.cs`:
  - extracted `CreatePointFromAxesEN(...)` and removed local `FromEN(...)` in quarter fallback corner reconstruction.
- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.EndpointEnforcement.cs`:
  - extracted shared logging format helpers:
    - `FormatLsdEndpointTracePoint(...)`
    - `FormatLsdEndpointTraceId(...)`
  - removed duplicated local formatter functions and rewired callsites.
- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.QuarterExtensionsConnectivity.cs`:
  - extracted axis projection helpers:
    - `ProjectPointToSectionU(...)`
    - `ProjectPointToSectionV(...)`
  - removed duplicated local `ToU(...)`/`ToV(...)` functions and rewired callsites.
- Updated `src/AtsBackgroundBuilder/SurfaceImpact/Plugin.SurfaceImpact.cs`:
  - extracted local helpers in duplicate-land filtering:
    - `NormalizeSurfaceImpactLandLocation(...)`
    - `GetSurfaceImpactReportDate(...)`
    - `HasRealSurfaceImpactLandLocation(...)`
- Verification:
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath=".artifacts\ats_alt_20260304_133900\"`
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded.

# Follow-up (ATS RoadAllowance Cleanup Shared Segment Helpers, 2026-03-04)

- [x] Consolidate repeated `TryReadOpenSegment`/`TryWriteOpenSegment`/orientation local helpers in `RoadAllowance.Cleanup`.
- [x] Rewire all callsites in `RoadAllowance.Cleanup` to shared helper methods.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS RoadAllowance Cleanup Shared Segment Helpers, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.Cleanup.cs`:
  - extracted file-level shared helpers:
    - `TryReadOpenSegmentForCleanup(...)`
    - `TryWriteOpenSegmentForCleanup(...)`
    - `IsHorizontalLikeForCleanup(...)`
    - `IsVerticalLikeForCleanup(...)`
  - replaced repeated local helper definitions across cleanup routines with shared helper calls.
- Verification:
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath=".artifacts\ats_alt_20260304_141000\"`
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded.

# Follow-up (ATS LabelPlacer Intersection/Candidate Helper Extraction, 2026-03-04)

- [x] Remove local segment-intersection math helpers in `LabelPlacer` and promote to class-level helpers.
- [x] Remove local candidate-dedupe helper in leader target selection and promote to class-level helper.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS LabelPlacer Intersection/Candidate Helper Extraction, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs`:
  - `SegmentsIntersect(...)` now uses extracted helpers:
    - `CrossForSegmentIntersection(...)`
    - `IsPointOnSegmentForIntersection(...)`
  - `ChooseLeaderTargetAvoidingOtherDispositions(...)` now uses:
    - `AddUniqueCandidatePoint(...)`
  - removed corresponding local helper functions.
- Verification:
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath=".artifacts\ats_alt_20260304_141620\"`
  - `$env:DOTNET_CLI_HOME='C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded.

# Follow-up (ATS Core Plugin Window/Segment Helper Consolidation, 2026-03-04)

- [x] Extract repeated local window/segment predicates in `Core/Plugin.cs` to class-level helpers.
- [x] Keep method-local semantics where needed via wrapper flags (no-window match, 2-vertex-only vs collinear open polyline).
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS Core Plugin Window/Segment Helper Consolidation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Core/Plugin.cs`:
  - added shared helpers:
    - `IsPointInAnyWindowForPlugin(...)`
    - `IsPointOnAnyWindowBoundaryForPlugin(...)`
    - `DoesSegmentIntersectAnyWindowForPlugin(...)`
    - `TryReadOpenSegmentForPlugin(...)`
    - `TryMoveEndpointForPlugin(...)`
    - `IsZeroTwentyLayerForPlugin(...)`
  - rewired repeated method-local helpers across core cleanup/connect flows to call these shared helpers.
  - preserved existing behavior by passing explicit flags per callsite:
    - empty-window match behavior where required,
    - two-vertex-only vs collinear-open polyline read behavior,
    - two-vertex-only vs open-endpoint move behavior.
- Verification:
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath=".artifacts\ats_alt_20260304_174050\"`
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).

# Follow-up (ATS CorrectionLinePostProcessing Helper Consolidation, 2026-03-04)

- [x] Extract duplicated local window/orientation/endpoint-move helpers from correction post-processing to class-level helpers.
- [x] Rewire both correction post-processing methods to shared helpers with no algorithm changes.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS CorrectionLinePostProcessing Helper Consolidation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.CorrectionLinePostProcessing.cs`:
  - added shared helpers:
    - `IsPointInAnyWindowForCorrectionLinePost(...)`
    - `DoesSegmentIntersectAnyWindowForCorrectionLinePost(...)`
    - `IsHorizontalLikeForCorrectionLinePost(...)`
    - `IsVerticalLikeForCorrectionLinePost(...)`
    - `TryMoveEndpointForCorrectionLinePost(...)`
  - rewired duplicated local helper bodies in:
    - `EnforceFinalCorrectionOuterLayerConsistency(...)`
    - `ConnectCorrectionInnerEndpointsToVerticalUsecBoundaries(...)`
- Verification:
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath=".artifacts\ats_alt_20260304_174640\"`
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).

# Follow-up (ATS QuarterExtensionsConnectivity Window/Segment Helper Consolidation, 2026-03-04)

- [x] Extract repeated local window/segment/read/move helper bodies in quarter extensions connectivity to class-level helpers.
- [x] Preserve mixed semantics for read/move helpers via explicit flags (collinear-open support, two-vertex-only endpoint moves).
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS QuarterExtensionsConnectivity Window/Segment Helper Consolidation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.QuarterExtensionsConnectivity.cs`:
  - added shared helpers:
    - `IsPointInAnyWindowForQuarterExtensionsConnectivity(...)`
    - `DoesSegmentIntersectAnyWindowForQuarterExtensionsConnectivity(...)`
    - `TryReadOpenSegmentForQuarterExtensionsConnectivity(...)`
    - `TryMoveEndpointForQuarterExtensionsConnectivity(...)`
  - rewired repeated local helper definitions to shared helper wrappers across all quarter-extension connectivity passes.
  - retained behavior-specific modes using explicit flags:
    - `allowCollinearOpenPolyline: true/false`
    - `requireTwoVertexPolyline: true/false`
- Verification:
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /m:1 -v:n /p:BaseOutputPath=".artifacts\ats_alt_20260304_180120\"`
  - `$env:DOTNET_CLI_HOME = Join-Path (Get-Location) '.dotnet-home'; $env:HOME = $env:DOTNET_CLI_HOME; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).
# Follow-up (ATS Cleanup Move/Projection Helper Completion, 2026-03-04)

- [x] Add missing shared helper methods referenced by `Cleanup` wrapper refactors.
- [x] Preserve previous move-endpoint behavior variants with explicit mode flags.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS Cleanup Move/Projection Helper Completion, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.Cleanup.cs`:
  - added missing helpers:
    - `TryMoveEndpointByIndexForCleanup(...)`
    - `ClosestPointOnSegmentForCleanup(...)`
  - `TryMoveEndpointByIndexForCleanup(...)` preserves both prior local behaviors via `requireTwoVertexPolyline`:
    - `true`: only open 2-vertex polylines
    - `false`: open polylines with 2+ vertices (first/last endpoint move)
- Verification:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore -o .artifacts\\build-verify\\AtsBackgroundBuilder`
  - `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).

# Follow-up (ATS EndpointEnforcement Window/Boundary/Move Helper Consolidation, 2026-03-04)

- [x] Consolidate repeated window-point, boundary, and segment-window local helpers in endpoint enforcement.
- [x] Consolidate repeated endpoint-move local helper in endpoint enforcement.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS EndpointEnforcement Window/Boundary/Move Helper Consolidation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.EndpointEnforcement.cs`:
  - added shared helpers:
    - `IsPointInAnyWindowForEndpointEnforcement(...)`
    - `IsPointOnAnyWindowBoundaryForEndpointEnforcement(...)`
    - `DoesSegmentIntersectAnyWindowForEndpointEnforcement(...)`
    - `TryMoveEndpointForEndpointEnforcement(...)`
  - rewired repeated method-local helpers to wrappers using shared helpers in endpoint-enforcement flows.
  - removed redundant local wrappers that became unused to keep the build warning-free.
- Verification:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore -o .artifacts\\build-verify\\AtsBackgroundBuilder`
  - `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).
# Follow-up (ATS Cleanup Clip-Window Predicate Consolidation, 2026-03-04)

- [x] Remove repeated local `IsPointInAnyWindow(...)` definitions in `Cleanup`.
- [x] Rewire repeated local `DoesSegmentIntersectAnyWindow(...)` definitions to shared clip-window helper.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS Cleanup Clip-Window Predicate Consolidation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.Cleanup.cs`:
  - removed duplicated method-local `IsPointInAnyWindow(...)` helpers.
  - standardized all local `DoesSegmentIntersectAnyWindow(...)` helpers to:
    - `DoesSegmentIntersectAnyClipWindow(clipWindows, a, b)`
  - intent: behavior-neutral dedupe using existing shared clip-window predicates already present in file.
- Verification:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore -o .artifacts\\build-verify\\AtsBackgroundBuilder`
  - `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).
# Follow-up (ATS EndpointEnforcement/QuarterExtensions Local Geometry Helper Consolidation, 2026-03-04)

- [x] Consolidate duplicated local closest-point segment math in `EndpointEnforcement`.
- [x] Consolidate duplicated local window-intersection helpers in `QuarterExtensionsConnectivity`.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS EndpointEnforcement/QuarterExtensions Local Geometry Helper Consolidation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.EndpointEnforcement.cs`:
  - replaced two duplicated local `ClosestPointOnSegment(...)` bodies with wrapper calls.
  - added shared helper:
    - `ClosestPointOnSegmentForEndpointEnforcement(...)`
- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.QuarterExtensionsConnectivity.cs`:
  - replaced duplicated local window-intersection bodies with shared helper wrappers.
  - added shared helpers:
    - `IsPointInWindowForQuarterExtensionsConnectivity(...)`
    - `DoesSegmentIntersectWindowForQuarterExtensionsConnectivity(...)`
  - preserved existing method-local call structure with thin wrappers where direct `IsPointInWindow(...)` calls are still used.
- Verification:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore -o .artifacts\\build-verify\\AtsBackgroundBuilder`
  - `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).
# Follow-up (ATS QuarterExtensions Infinite-Line Helper Consolidation, 2026-03-04)

- [x] Consolidate repeated local infinite-line intersection helper bodies in `QuarterExtensionsConnectivity`.
- [x] Rewire all local callsites to a single shared helper.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS QuarterExtensions Infinite-Line Helper Consolidation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.QuarterExtensionsConnectivity.cs`:
  - replaced four duplicated local `TryIntersectInfiniteLines(...)` bodies with wrappers.
  - added shared helper:
    - `TryIntersectInfiniteLinesForQuarterExtensionsConnectivity(...)`
  - no algorithm changes; only helper-body dedupe.
- Verification:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore -o .artifacts\\build-verify\\AtsBackgroundBuilder`
  - `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).
# Follow-up (ATS Cleanup Window-Point Helper Finalization, 2026-03-04)

- [x] Consolidate remaining local window-point helper body in `Cleanup`.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS Cleanup Window-Point Helper Finalization, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.Cleanup.cs`:
  - replaced remaining local `IsPointInWindow(...)` body with a wrapper call.
  - added shared helper:
    - `IsPointInWindowForCleanup(...)`
- Verification:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore -o .artifacts\\build-verify\\AtsBackgroundBuilder`
  - `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).
# Follow-up (ATS Cleanup Blind-South Boundary Helper Consolidation, 2026-03-04)

- [x] Consolidate duplicated blind-south boundary helper bodies in `Cleanup`.
- [x] Rewire all local callsites to shared helper wrappers.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS Cleanup Blind-South Boundary Helper Consolidation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.Cleanup.cs`:
  - replaced three duplicated local definitions with wrappers:
    - `IsBlindSouthBoundarySection(...)`
    - `IsSegmentOnBlindSouthBoundary(...)`
  - added shared helpers:
    - `IsBlindSouthBoundarySectionForCleanup(...)`
    - `IsSegmentOnBlindSouthBoundaryForCleanup(...)`
  - helper logic is unchanged; refactor is body dedupe only.
- Verification:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore -o .artifacts\\build-verify\\AtsBackgroundBuilder`
  - `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).
# Follow-up (ATS Normalization Seam/Overlap/RangeEdge Helper Consolidation, 2026-03-04)

- [x] Consolidate duplicated `IsSeamLayer` local helpers in `Normalization` using a shared helper with a base-layer mode flag.
- [x] Consolidate duplicated `HorizontalOverlaps` local helper bodies in `Normalization`.
- [x] Consolidate duplicated `IsRangeEdgeCandidate` helper bodies in `Normalization`.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS Normalization Seam/Overlap/RangeEdge Helper Consolidation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.Normalization.cs`:
  - replaced duplicated local helper bodies with wrappers:
    - `IsSeamLayer(...)` now routes to `IsSeamLayerForNormalization(..., includeUsecBaseLayer: true/false)`
    - `HorizontalOverlaps(...)` now routes to `HorizontalOverlapsForNormalization(...)`
    - `IsRangeEdgeCandidate(...)` variants now route to `IsRangeEdgeCandidateForNormalization(...)`
  - added shared helpers:
    - `IsSeamLayerForNormalization(...)`
    - `HorizontalOverlapsForNormalization(...)`
    - `IsRangeEdgeCandidateForNormalization(...)`
  - behavior preserved by explicit mode flag for the second seam-layer pass that includes `LayerUsecBase`.
- Verification:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore -o .artifacts\\build-verify\\AtsBackgroundBuilder`
  - `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).
# Follow-up (ATS EndpointEnforcement Hard-Boundary Layer Helper Consolidation, 2026-03-04)

- [x] Consolidate duplicated hard-boundary layer predicate bodies in `EndpointEnforcement`.
- [x] Preserve per-callsite alias behavior with an explicit mode flag.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS EndpointEnforcement Hard-Boundary Layer Helper Consolidation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.EndpointEnforcement.cs`:
  - replaced duplicated local `IsHardBoundaryLayer(...)` bodies with wrappers.
  - added shared helper:
    - `IsHardBoundaryLayerForEndpointEnforcement(string layer, bool includeSecAliases)`
  - behavior preserved by callsite mode:
    - first pass: `includeSecAliases: false`
    - blind-line pass: `includeSecAliases: true`
- Verification:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore -o .artifacts\\build-verify\\AtsBackgroundBuilder`
  - `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).
# Follow-up (ATS Cross-File Infinite-Line Intersection Helper Consolidation, 2026-03-04)

- [x] Consolidate duplicated local infinite-line intersection helper bodies across ATS files.
- [x] Route all affected local callsites to one shared class-level helper.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS Cross-File Infinite-Line Intersection Helper Consolidation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Core/Plugin.cs`:
  - replaced local `TryIntersectInfiniteLines(...)` body with wrapper call.
  - added shared helper:
    - `TryIntersectInfiniteLinesForPluginGeometry(...)`
- Updated callsites in:
  - `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.Cleanup.cs`
  - `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.EndpointEnforcement.cs`
  - `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.CorrectionLinePostProcessing.cs`
  - each now uses local wrapper delegating to `TryIntersectInfiniteLinesForPluginGeometry(...)`.
- Verification:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore -o .artifacts\\build-verify\\AtsBackgroundBuilder`
  - `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).
# Follow-up (ATS EndpointEnforcement Thirty-Layer/Correction-Proximity Helper Consolidation, 2026-03-04)

- [x] Consolidate duplicated `IsThirtyEighteenLayer` helper bodies in endpoint enforcement.
- [x] Consolidate duplicated correction-boundary proximity helper bodies in endpoint enforcement.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS EndpointEnforcement Thirty-Layer/Correction-Proximity Helper Consolidation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.EndpointEnforcement.cs`:
  - replaced duplicated local `IsThirtyEighteenLayer(...)` bodies with wrappers.
  - added shared helper:
    - `IsThirtyEighteenLayerForEndpointEnforcement(...)`
  - replaced duplicated local `IsEndpointNearCorrectionBoundary(...)` bodies with wrappers.
  - added shared helper:
    - `IsEndpointNearBoundarySegmentsForEndpointEnforcement(...)`
- Verification:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore -o .artifacts\\build-verify\\AtsBackgroundBuilder`
  - `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).
# Follow-up (ATS EndpointEnforcement Hard-Boundary Endpoint Check Consolidation, 2026-03-04)

- [x] Consolidate duplicated hard-boundary endpoint check helper bodies in endpoint enforcement.
- [x] Preserve all tuple-shape variants used by different passes.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS EndpointEnforcement Hard-Boundary Endpoint Check Consolidation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.EndpointEnforcement.cs`:
  - replaced three duplicated local `IsEndpointOnHardBoundary(...)` bodies with wrapper calls.
  - added shared helpers:
    - `IsEndpointOnBoundarySegmentsForEndpointEnforcement(Point2d, IReadOnlyList<(Point2d A, Point2d B)>, double)`
    - `IsEndpointOnBoundarySegmentsForEndpointEnforcement(Point2d, IReadOnlyList<(Point2d A, Point2d B, bool IsZero)>, double)`
    - `IsEndpointOnBoundarySegmentsForEndpointEnforcement(Point2d, ObjectId, IReadOnlyList<(ObjectId Id, Point2d A, Point2d B)>, double)`
  - behavior preserved including source-segment exclusion and tuple-shape-specific passes.
- Verification:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore -o .artifacts\\build-verify\\AtsBackgroundBuilder`
  - `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).

# Follow-up (ATS QuarterExtensions LSD Midpoint Helper Consolidation, 2026-03-04)

- [x] Consolidate duplicated `TryBestMidpoint(...)` local helper bodies in `QuarterExtensionsConnectivity`.
- [x] Route both callsites to one shared helper while preserving threshold behavior per pass.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS QuarterExtensions LSD Midpoint Helper Consolidation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.QuarterExtensionsConnectivity.cs`:
  - replaced two duplicated local `TryBestMidpoint(...)` bodies with wrappers.
  - added shared helper:
    - `TrySelectBestLsdMidpointForQuarterExtensionsConnectivity(...)`
  - preserved prior tie-break order:
    - segment distance first, then old-midpoint distance, then move distance.
  - preserved per-pass thresholds by parameterizing:
    - `lsdOldSegmentTol`, `lsdOldMidpointTol`, `endpointMoveTol`, `lsdMaxMove`.
- Verification:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore -o .artifacts\\build-verify\\AtsBackgroundBuilder`
  - `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).

# Follow-up (ATS QuarterExtensions Owning-Section Index Helper Consolidation, 2026-03-04)

- [x] Consolidate duplicated `TryGetOwningSectionIndex(...)` local helper bodies in `QuarterExtensionsConnectivity`.
- [x] Preserve both ownership modes (with V-range constraints and U-only constraints) via typed shared overloads.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS QuarterExtensions Owning-Section Index Helper Consolidation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.QuarterExtensionsConnectivity.cs`:
  - replaced two local `TryGetOwningSectionIndex(...)` bodies with wrappers.
  - added shared overloads:
    - `TryGetOwningSectionIndexForQuarterExtensionsConnectivity(...)` for `(Width, Height, Window)` targets with V-range gating.
    - `TryGetOwningSectionIndexForQuarterExtensionsConnectivity(...)` for `(Window, EastEdgeU, OriginalSeCorner, HasOriginalSeCorner)` targets with U-only gating.
  - kept ownership ranking identical:
    - nearest `SwCorner` distance among valid candidates.
- Verification:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore -o .artifacts\\build-verify\\AtsBackgroundBuilder`
  - `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).

# Follow-up (ATS QuarterExtensions Local Wrapper Removal Cleanup, 2026-03-04)

- [x] Remove now-redundant local `TryBestMidpoint(...)` wrappers and call the shared midpoint helper directly.
- [x] Remove now-redundant local `TryGetOwningSectionIndex(...)` wrappers and call shared owning-section helpers directly.
- [x] Rebuild ATS and rerun decision tests.

## Review (ATS QuarterExtensions Local Wrapper Removal Cleanup, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.QuarterExtensionsConnectivity.cs`:
  - removed local `TryBestMidpoint(...)` wrappers in both SW/NW extension passes.
  - rewired endpoint midpoint selection calls directly to:
    - `TrySelectBestLsdMidpointForQuarterExtensionsConnectivity(...)`
  - removed local `TryGetOwningSectionIndex(...)` wrappers in both ownership passes.
  - rewired ownership checks directly to:
    - `TryGetOwningSectionIndexForQuarterExtensionsConnectivity(...)` overloads.
  - no decision logic changes; this is callsite simplification only.
- Verification:
  - `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore -o .artifacts\\build-verify\\AtsBackgroundBuilder`
  - `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
  - build and decision tests succeeded (0 warnings, 0 errors).

# Follow-up (WLS Shared EXIF GPS Reader Consolidation, 2026-03-04)

- [x] Extract shared JPG EXIF GPS loading/parsing helper for WLS services.
- [x] Rewire `CompleteFromPhotosService`, `PhotoToTextCheckService`, and `SortBufferPhotosService` to use shared helper.
- [x] Build WildlifeSweeps to verify no compile regressions.

## Review (WLS Shared EXIF GPS Reader Consolidation, 2026-03-04)

- Added shared helper:
  - `wls_program/src/WildlifeSweeps/PhotoGpsMetadataReader.cs`
  - centralizes:
    - JPG/JPEG file enumeration
    - EXIF GPS property parsing (`GPSLatitudeRef/GPSLatitude/GPSLongitudeRef/GPSLongitude`)
    - optional coordinate de-duplication (rounded 6 decimals)
    - consistent skipped/duplicate logging and per-file read-failure logging
- Updated callsites:
  - `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`
    - `LoadGpsPhotos(...)` now maps from shared helper output with dedupe enabled.
    - removed duplicated EXIF constants and helper methods.
  - `wls_program/src/WildlifeSweeps/PhotoToTextCheckService.cs`
    - `LoadGpsPhotos(...)` now maps from shared helper output with dedupe enabled.
    - removed duplicated EXIF constants and helper methods.
  - `wls_program/src/WildlifeSweeps/SortBufferPhotosService.cs`
    - `LoadGpsPhotos(...)` now maps from shared helper output with dedupe disabled (preserves per-photo behavior).
    - removed duplicated EXIF constants and helper methods.
- Verification:
  - `dotnet build wls_program\\src\\WildlifeSweeps\\WildlifeSweeps.csproj -c Release --no-restore -o .artifacts\\build-verify\\WildlifeSweeps`
  - build succeeded (0 warnings, 0 errors).

# Follow-up (WLS Prompt/Boundary Sampler Helper Consolidation, 2026-03-04)

- [x] Consolidate repeated UTM/JPG prompt helpers across WLS services.
- [x] Consolidate duplicated boundary polyline sampling helpers across CompleteFromPhotos and SortBufferPhotos.
- [x] Build WildlifeSweeps to verify compile stability.

## Review (WLS Prompt/Boundary Sampler Helper Consolidation, 2026-03-04)

- Added prompt helper:
  - `wls_program/src/WildlifeSweeps/WildlifePromptHelper.cs`
  - centralized:
    - UTM 11/12 keyword prompt and normalization
    - JPG picker dialog wrapper
- Added boundary helpers:
  - `wls_program/src/WildlifeSweeps/BoundarySamplingHelper.cs`
    - shared polyline vertex sampling with configurable step/merge tolerances
  - `wls_program/src/WildlifeSweeps/BoundaryContainmentHelper.cs`
    - shared point-in-polygon and boundary-distance math with configurable degenerate-segment distance mode
- Updated callsites:
  - `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`
    - uses `WildlifePromptHelper` for UTM + JPG prompts
    - uses `BoundarySamplingHelper` for boundary vertex generation
    - `BufferBoundary` now delegates containment/distance logic to `BoundaryContainmentHelper`
  - `wls_program/src/WildlifeSweeps/PhotoToTextCheckService.cs`
    - uses `WildlifePromptHelper` for UTM + JPG prompts
  - `wls_program/src/WildlifeSweeps/SortBufferPhotosService.cs`
    - uses `WildlifePromptHelper` for UTM prompt
    - uses `BoundarySamplingHelper` for boundary vertex generation
    - `BufferBoundary` now delegates containment logic to `BoundaryContainmentHelper`
- Verification:
  - `dotnet build wls_program\\src\\WildlifeSweeps\\WildlifeSweeps.csproj -c Release --no-restore -o .artifacts\\build-verify\\WildlifeSweeps`
  - build succeeded (0 warnings, 0 errors).

# Follow-up (WLS Other-Value/LSD Helper Consolidation, 2026-03-04)

- [x] Consolidate duplicated `Other`-value normalization logic used by findings standardization flows.
- [x] Consolidate duplicated LSD row/column-to-number logic used by ATS resolver + Complete From Photos.
- [x] Build WildlifeSweeps to verify compile stability.

## Review (WLS Other-Value/LSD Helper Consolidation, 2026-03-04)

- Added `wls_program/src/WildlifeSweeps/FindingOtherValueHelper.cs`:
  - centralizes:
    - `IsOtherValue(value, otherValue)`
    - `NormalizeOtherValue(value, otherValue)`
- Updated:
  - `wls_program/src/WildlifeSweeps/FindingsDescriptionStandardizer.cs`
  - `wls_program/src/WildlifeSweeps/FindingsStandardizationHelper.cs`
  - callsites now use the shared helper directly and redundant local methods were removed.
- Added `wls_program/src/WildlifeSweeps/LsdNumberingHelper.cs`:
  - centralizes LSD numbering from (rowFromSouth, colFromWest).
- Updated:
  - `wls_program/src/WildlifeSweeps/AtsQuarterLocationResolver.cs`
  - `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`
  - now call `LsdNumberingHelper.GetLsdNumber(...)` and removed duplicated local implementations.
- Verification:
  - `dotnet build wls_program\\src\\WildlifeSweeps\\WildlifeSweeps.csproj -c Release --no-restore -o .artifacts\\build-verify\\WildlifeSweeps`
  - build succeeded (0 warnings, 0 errors).

# Follow-up (PLSR DISP Leading-Zero Preservation, 2026-03-04)

- [x] Keep PLSR matching canonical while preserving leading-zero DISP display text in review/issues/log output.
- [x] Ensure missing-label creation paths (template/XML fallback) write DISP text with preserved leading zeros.
- [x] Rebuild/compile ATS and verify no regressions.
- [x] Capture review notes and residual risk.

## Review (PLSR DISP Leading-Zero Preservation, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - in PLSR scan issue creation, DISP display now uses `ResolveIssueDisplayDispNum(...)`:
    - keeps review-grid/log/summary DISP values aligned to XML/label text values with leading zeros preserved.
    - matching/indexing still uses normalized canonical keys.
  - missing-label summary/example text now reports preserved display DISP values.
  - template missing-label creation now also rewrites the disposition token in template contents using preserved display DISP (`ReplaceLabelDispNumInContents(...)`).
  - XML fallback label creation now uses preserved display DISP directly.
- Added helpers:
  - `ResolveIssueDisplayDispNum(...)`
  - `ReplaceLabelDispNumInContents(...)`
- Verification:
  - `dotnet build .\\src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore`
  - compile succeeded and produced `src/AtsBackgroundBuilder/bin/Release/net8.0-windows/AtsBackgroundBuilder.dll`.
  - expected copy-to-`build/net8.0-windows` failed because DLL is locked by active AutoCAD process.
  - staged fresh build to `build/net8.0-windows-next/AtsBackgroundBuilder.dll` (`2026-03-04 5:35:43 PM`).
- Residual risk:
  - if source OD DISP values are inherently zero-stripped upstream, source-geometry-created labels can still carry stripped DISP text in some paths; PLSR issue/review display and template/XML fallback creation are now zero-preserving.

# Follow-up (ATS Auto Shape Update Prompting, 2026-03-04)

- [x] Remove the manual "check/update shapes always" UI toggle from ATS build forms.
- [x] Force shape update checks to run automatically on every build submission.
- [x] Keep auto-update checks scoped to requested shape sets only (Dispositions, Compass Mapping, CLRs).
- [x] Add pre-copy confirmation prompts when newer shape content is detected.
- [x] Show user-facing warnings when requested shape updates cannot run because source drives/folders are unavailable.
- [x] Rebuild ATS + decision tests to verify compile/runtime safety.

## Review (ATS Auto Shape Update Prompting, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Core/AtsBuildWindow.cs`:
  - removed the `CHECK/UPDATE SHAPES ALWAYS` toggle from the action row.
  - build input capture now always sets `AutoCheckUpdateShapefilesAlways = true`.
- Updated `src/AtsBackgroundBuilder/Core/AtsBuildForm.cs` for parity:
  - removed the same manual toggle from the WinForms action row.
  - default/input capture now forces `AutoCheckUpdateShapefilesAlways = true`.
- Updated `src/AtsBackgroundBuilder/Core/Plugin.Core.ImportWindowing.cs`:
  - auto-update flow now prompts before each requested update when newer source content is detected:
    - `There are newer Shapes for <type> Located at: ... Would you like to update them?`
  - if update roots/folders cannot be reached for requested sets, ATSBUILD now shows a warning dialog instead of logging-only.
  - requested-only gating remains intact (`Dispositions` only when linework/labels/PLSR needs it; `Compass Mapping`/`CLRs` only when selected).
- Verification:
  - `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release -v minimal` (succeeded)
  - `dotnet build .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release -v minimal` (succeeded)
  - `dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release` ("Decision tests passed.")
- Notes:
  - NuGet vulnerability index warnings (`NU1900`) were emitted due source reachability to `https://api.nuget.org/v3/index.json`; no compile errors.

# Follow-up (ATS Shape-Update Pre-Prompt Performance Pass, 2026-03-04)

- [x] Identify startup delay source before shape-update prompt.
- [x] Remove per-base repeated recursive tree scans in shape source resolution.
- [x] Switch to single recursive `.shp` anchor pass for requested base names.
- [x] Compile-check patched code path.

## Review (ATS Shape-Update Pre-Prompt Performance Pass, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Core/Plugin.Core.ImportWindowing.cs`:
  - `ResolveSelectedShapeSourceFiles(...)` now:
    - builds a requested base-name set once,
    - runs one top-level `*.shp` pass,
    - then one recursive `*.shp` pass (only if needed),
    - chooses newest valid anchor per base name,
    - resolves sidecar files from anchor folder.
  - removed the previous per-base recursive `Directory.GetFiles(..., SearchOption.AllDirectories)` pattern that caused repeated network-tree traversals.
- Verification:
  - `dotnet msbuild .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -verbosity=minimal` succeeded.
  - full `dotnet build` currently blocked from mirror copy step when `build/net8.0-windows/AtsBackgroundBuilder.dll` is locked by active AutoCAD process; compile output still produced updated assembly in `obj/Release/net8.0-windows`.
- Build artifact status:
  - updated compiled DLL: `src/AtsBackgroundBuilder/obj/Release/net8.0-windows/AtsBackgroundBuilder.dll` (`2026-03-04 7:44:29 PM`).

# Follow-up (ATS Remove Manual Shape Update Controls, 2026-03-04)

- [x] Remove manual shape update combo/button from WPF ATS build action row.
- [x] Remove manual shape update combo/button from WinForms ATS build action row.
- [x] Remove now-unused manual shape update handlers/control fields.
- [x] Compile-check ATS after UI cleanup.

## Review (ATS Remove Manual Shape Update Controls, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Core/AtsBuildWindow.cs`:
  - removed shape-type dropdown + "Update Shape" button from action area.
  - removed `OnUpdateShape()` handler and related control fields.
  - build input continues to force auto shape update checks (`AutoCheckUpdateShapefilesAlways = true`).
- Updated `src/AtsBackgroundBuilder/Core/AtsBuildForm.cs` for parity:
  - removed shape-type dropdown + "Update Shape" button from action area.
  - removed `OnUpdateShape()` handler and related control fields.
  - build input/default keeps auto shape update checks on (`AutoCheckUpdateShapefilesAlways = true`).
- Verification:
  - `dotnet msbuild .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -verbosity:minimal` succeeded.
  - full `dotnet build` compiles but currently fails final mirror copy to `build\net8.0-windows\AtsBackgroundBuilder.dll` because file is locked by another process.

# Follow-up (ATS Deferred PLSR Shape Prompt + Refresh Compare Tightening, 2026-03-04)

- [x] Stop triggering Disposition update prompts during pre-build auto-check for PLSR-only runs.
- [x] Trigger Disposition update prompt only at the actual supplemental import step when PLSR decides import is needed.
- [x] Tighten shape refresh comparison to avoid false prompts from generated/extra sidecars and non-source-newer timestamp drift.
- [x] Build/compile and deploy updated ATS DLL.

## Review (ATS Deferred PLSR Shape Prompt + Refresh Compare Tightening, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Core/Plugin.Core.ImportWindowing.cs`:
  - pre-build disposition auto-update gate now runs only for linework/label requests (`IncludeDispositionLinework || IncludeDispositionLabels`).
  - added `AutoUpdateDispositionShapesIfNeeded(...)` helper and reused it for targeted invocation.
  - `DirectoryContentsDifferForBaseNames(...)` now checks refresh need per selected tracked files (`.shp/.shx/.dbf/.prj/.cpg`) using:
    - missing destination file,
    - size mismatch,
    - source file newer-than destination.
  - ignores destination-only/generated extras (e.g. index sidecars) to reduce repeated false prompts.
- Updated `src/AtsBackgroundBuilder/Core/Plugin.cs`:
  - for PLSR-only supplemental import path, disposition update check now runs immediately before `ShapefileImporter.ImportShapefiles(...)` and only when import is still required.
- Verification:
  - `dotnet msbuild .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -verbosity:minimal` succeeded.
  - `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release` succeeded.
  - Deployed to `C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows\AtsBackgroundBuilder.dll`.

# Follow-up (PLSR XML Owner Enforcement, 2026-03-04)

- [x] Enforce XML owner as authoritative during PLSR apply (owner mismatch actions always applied).
- [x] Keep non-owner actions (expired/missing-label create) under review acceptance flow.
- [x] Verify compile and deploy updated DLL.

## Review (PLSR XML Owner Enforcement, 2026-03-04)

- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - `ApplyAcceptedPlsrActions(...)` now force-adds all actionable `UpdateOwner` issues to the accepted issue set before decision routing.
  - logs owner enforcement count for transparency.
- Behavior:
  - With PLSR XML selected, label owner updates follow XML truth regardless of review checkbox state.
  - With no XML selected, PLSR check exits early and no XML owner enforcement runs (disposition-only behavior remains).
- Verification:
  - `dotnet msbuild .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -verbosity:minimal` succeeded.
  - `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release` succeeded.
  - deployed to `C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows\AtsBackgroundBuilder.dll`.

# Follow-up (PLSR Ver. Date OD/XML Compare Column, 2026-03-04)

- [x] Parse OD VER_DATE from disposition object data into the disposition info model.
- [x] Parse PLSR XML VersionDate values from activity nodes.
- [x] Add per-issue Ver. Date status calculation (MATCH / NON-MATCH / N/A) using OD sources that exist in drawing (or fallback source candidates used by insert decisions).
- [x] Add Ver. Date. column to the PLSR review grid and bind status values.
- [x] Rebuild ATS and rerun decision tests.

## Review (PLSR Ver. Date OD/XML Compare Column, 2026-03-04)

- Updated src/AtsBackgroundBuilder/Core/Plugin.cs:
  - read OD VER_DATE in disposition ingestion,
  - persist raw value to DispositionInfo.OdVerDateRaw for both width and non-width disposition entries.
- Updated src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs:
  - added DispositionInfo.OdVerDateRaw.
- Updated src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs:
  - parse XML <VersionDate> into PlsrActivity.VersionDate.
  - added source-resolution helper for version-date compare (quarter overlap match first, fallback indexed disposition candidate second).
  - added date-status resolver that compares OD yyyyMMdd vs XML date (date-only) and emits MATCH, NON-MATCH, or N/A.
  - wired VersionDateStatus onto missing-label, owner-mismatch, and expired issue rows.
- Updated src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.PlsrReviewDialog.cs:
  - added Ver. Date. review column,
  - bound row value from issue.VersionDateStatus.
- Verification:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` (with `DOTNET_CLI_HOME`, `DOTNET_SKIP_FIRST_TIME_EXPERIENCE`, `DOTNET_CLI_TELEMETRY_OPTOUT` set; succeeded; warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` (`Decision tests passed.`).

# Follow-up (PLSR Ver. Date Mismatch Listing + Parse Hardening, 2026-03-04)

- [x] Add standalone Version date mismatch findings when OD/XML dates differ and no other PLSR trigger exists.
- [x] Keep N/A version-date status informational only (no standalone row).
- [x] Harden OD VER_DATE parsing for numeric/text variants.
- [x] Harden XML version-date parsing to accept descendant VersionDate or ActivityDate values.
- [x] Rebuild ATS and rerun decision tests.

## Review (PLSR Ver. Date Mismatch Listing + Parse Hardening, 2026-03-04)

- Updated src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs:
  - added standalone Version date mismatch issue generation only when VersionDateStatus == NON-MATCH and no owner/expiry issue exists for that activity.
  - added display helpers for OD/XML version dates (yyyy-MM-dd output).
  - expanded OD date parsing to handle yyyyMMdd, digit-extracted values, and numeric/scientific-style values.
  - expanded XML date extraction to search descendant nodes by local-name (VersionDate or ActivityDate) and parse date-only safely.
- Verification:
  - dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore succeeded (warnings only).
  - dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore passed (Decision tests passed.).

# Follow-up (PLSR XML Date Source: ActivityDate Only, 2026-03-04)

- [x] Remove XML version-date fallback logic and use ActivityDate only for PLSR version-date comparison.
- [x] Rebuild ATS and deploy runtime DLL/PDB.

## Review (PLSR XML Date Source: ActivityDate Only, 2026-03-04)

- Updated src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs:
  - `TryParsePlsrXml(...)` now reads XML date source from `activity.Element(ns + "ActivityDate")` only.
  - removed descendant fallback chain (VersionDate / ActivityDate) for this compare source.
- Verification:
  - dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore succeeded.
  - runtime deploy copied to C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows.
# Follow-up (PLSR Ver. Date Source Scoping for NON-MATCH, 2026-03-04)

- [x] Restrict standalone PLSR version-date mismatch detection to same-quarter disposition matches only.
- [x] Keep missing-label version status tied to the actual source used for insertion decisions.
- [x] Rebuild ATS and deploy runtime DLL/PDB.

## Review (PLSR Ver. Date Source Scoping for NON-MATCH, 2026-03-04)

- Updated src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs:
  - removed cross-quarter fallback source resolution from standalone version-date mismatch checks.
  - standalone `Version date mismatch` rows now require a quarter-resolved source disposition (otherwise status remains `N/A`, no standalone row).
  - missing-label rows now calculate `VersionDateStatus` from the actual selected source disposition when one is used for insertion.
  - mismatch detail text now references XML `ActivityDate`.
- Verification:
  - .\\.local_dotnet\\dotnet.exe build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore succeeded (warnings only).
  - deployed to C:\\AUTOCAD-SETUP CG\\CG_LISP\\COMPASS\\net8.0-windows.
# Follow-up (PLSR Ver. Date Compare: VersionDate First + ActivityDate Fallback, 2026-03-04)

- [x] Parse both XML `VersionDate` and XML `ActivityDate` for each PLSR activity.
- [x] Mark VER_DATE compare as MATCH when OD date matches either XML date.
- [x] In mismatch rows, display XML VersionDate as expected date (fallback to ActivityDate when VersionDate is missing).
- [x] Rebuild ATS and deploy runtime DLL/PDB.

## Review (PLSR Ver. Date Compare: VersionDate First + ActivityDate Fallback, 2026-03-04)

- Updated src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs:
  - `PlsrActivity` now stores both `VersionDate` and `ActivityDate`.
  - XML parse now reads both fields independently.
  - `ResolvePlsrVersionDateStatus(...)` now returns MATCH when OD `VER_DATE` equals either XML `VersionDate` or XML `ActivityDate`.
  - mismatch expected-date display now prefers XML `VersionDate`; falls back to XML `ActivityDate`.
  - mismatch detail now explicitly states VersionDate mismatch context.
- Verification:
  - .\\.local_dotnet\\dotnet.exe build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore succeeded (warnings only).
  - runtime deploy copied to C:\\AUTOCAD-SETUP CG\\CG_LISP\\COMPASS\\net8.0-windows.
# Follow-up (PLSR XML VersionDate Nested Parse Fix, 2026-03-04)

- [x] Parse XML VersionDate from descendant nodes (e.g., Plans/Plan/VersionDate), not only direct Activity child.
- [x] Compare OD VER_DATE against any parsed XML VersionDate (plus ActivityDate fallback).
- [x] Rebuild ATS and deploy runtime DLL/PDB.

## Review (PLSR XML VersionDate Nested Parse Fix, 2026-03-04)

- Updated src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs:
  - `TryParsePlsrXml(...)` now collects descendant `VersionDate` values under each activity.
  - per-activity compare now uses all parsed XML version dates (date-only), then ActivityDate fallback.
  - mismatch expected display still prioritizes VersionDate.
- Verification:
  - .\\.local_dotnet\\dotnet.exe build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore succeeded (warnings only).
  - runtime deploy copied to C:\\AUTOCAD-SETUP CG\\CG_LISP\\COMPASS\\net8.0-windows.
# Follow-up (PLSR OD Date Compare: Add EFFDATE, 2026-03-05)

- [x] Extend disposition model to carry OD `EFFDATE` alongside `VER_DATE`.
- [x] Read OD `EFFDATE` during disposition ingestion and persist into `DispositionInfo`.
- [x] Update PLSR date status to match if any OD date (`VER_DATE`, `EFFDATE`) equals any XML date (`VersionDate`, `ActivityDate`).
- [x] Rebuild ATS and deploy runtime DLL/PDB.

## Review (PLSR OD Date Compare: Add EFFDATE, 2026-03-05)

- Updated src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs:
  - added `DispositionInfo.OdEffDateRaw`.
- Updated src/AtsBackgroundBuilder/Core/Plugin.cs:
  - reads OD field `EFFDATE` and stores it in both width and non-width disposition info entries.
- Updated src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs:
  - `ResolvePlsrVersionDateStatus(...)` now compares OD candidate dates from both `VER_DATE` and `EFFDATE`.
  - status returns MATCH when any OD date overlaps any XML compare date (`VersionDate` descendants + `ActivityDate`).
  - mismatch current-value display now shows available OD date fields (`VER_DATE=...; EFFDATE=...`).
  - mismatch detail text now references `VER_DATE/EFFDATE`.
- Verification:
  - .\\.local_dotnet\\dotnet.exe build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore succeeded (warnings only).
  - runtime deploy copied to C:\\AUTOCAD-SETUP CG\\CG_LISP\\COMPASS\\net8.0-windows.
# Follow-up (PLSR Date Compare: XML VersionDate + OD VER_DATE Only, 2026-03-05)

- [x] Restrict PLSR version-date compare inputs to XML `VersionDate` and OD `VER_DATE` only.
- [x] Return `N/A` when either side is missing and avoid standalone mismatch flags for that case.
- [x] Remove `EFFDATE` / `ActivityDate` fallback messaging from mismatch details and display values.
- [x] Compile-check ATS and rerun decision tests.

## Review (PLSR Date Compare: XML VersionDate + OD VER_DATE Only, 2026-03-05)

- Updated `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs`:
  - `ResolvePlsrVersionDateStatus(...)` now compares only OD `VER_DATE` against XML `VersionDate` values.
  - missing/invalid OD `VER_DATE` or missing XML `VersionDate` now returns `N/A`.
  - `FormatPlsrExpectedVersionDateForDisplay(...)` no longer falls back to XML `ActivityDate`.
  - `ResolvePlsrVersionDateMismatchDetail(...)` now references only `VER_DATE` vs `VersionDate`.
  - `FormatDispositionDateFieldsForDisplay(...)` now displays only `VER_DATE` (or `N/A`).
- Verification:
  - `dotnet msbuild src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -t:Compile -v minimal` succeeded (warning `NU1900` only).
  - `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj --no-restore -v minimal` passed (`Decision tests passed.`).
  - full `dotnet build` currently fails copy-to-`build\\net8.0-windows\\AtsBackgroundBuilder.dll` because the destination DLL is locked by another process (`MSB3021/MSB3027`).
# Follow-up (Mixed WELLSITE + ACCESS ROAD Split, 2026-03-05)

- [x] Detect mixed-purpose dispositions where purpose indicates both wellsite and access road.
- [x] Classify each mixed polygon as road-like vs pad-like using width + shape heuristics.
- [x] Apply label-mode split: access-road polygons use width labels; pad polygons use wellsite labels.
- [x] Make label reuse/dedupe variant-aware so both labels can coexist for a single DISP number.
- [x] Compile-check ATS and rerun decision tests.

## Review (Mixed WELLSITE + ACCESS ROAD Split, 2026-03-05)

- Updated `src/AtsBackgroundBuilder/Core/Plugin.cs`:
  - added mixed-purpose detection (`IsWellSiteAndAccessRoadPurpose(...)`).
  - added geometry classification (`TryClassifyMixedWellsiteAccessRoadAsAccessRoad(...)`) using:
    - acceptable-width snap proximity,
    - compactness (`4pA/P???`) and extents aspect ratio,
    - representative-width guard (`<= 40m`).
  - for mixed purposes:
    - road-like polygons are labeled as `ACCESS ROAD` and forced to width-label path,
    - pad-like polygons are labeled as `WELLSITE` and forced to non-width wellsite path.
  - added variant reuse keys on prepared dispositions (`MIXED_ACCESS_ROAD`, `MIXED_WELLSITE_PAD`).
  - existing-label indexing now adds optional variant reuse keys inferred from existing label text.
- Updated `src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs`:
  - reuse skip logic now supports variant keys for mixed dispositions.
  - placement identity now includes variant key so mixed variants on the same ObjectId are not collapsed.
  - added `DispositionInfo.ReuseVariantKey` and mixed-variant constants.
- Verification:
  - `dotnet msbuild src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -t:Compile -v minimal` succeeded (warning `NU1900` only in sandbox).
  - `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj --no-restore -v minimal` passed (`Decision tests passed.`).

# Follow-up (Mixed Wellsite/Access Road Variant Labels, 2026-03-05)

- [x] Route `PURPCD = WELLSITE AND ACCESS ROAD` through purpose lookup outputs instead of hardcoded strings.
- [x] Emit mixed variants as separate label candidates (A/R width label + wellsite surface label when pad signature is detected).
- [x] Keep wellsite LSD-prefix behavior for mixed wellsite variant.
- [x] Rebuild plugin and rerun decision tests.

## Review (Mixed Wellsite/Access Road Variant Labels, 2026-03-05)

- Updated `src/AtsBackgroundBuilder/Core/Plugin.cs`:
  - mixed-purpose mapping now uses lookup-driven resolver (`ResolveMixedPurposeMappedValue`) so access-road text follows lookup (e.g. `A/R`) and wellsite follows lookup (e.g. `8-2 (Surface)`).
  - mixed-purpose classification now returns both `isAccessRoad` and `hasWellsitePadSignature`.
  - mixed-purpose emission now supports two variants for a single mixed geometry when pad signature exists:
    - width/leader access-road variant (`ReuseVariantMixedAccessRoad`)
    - non-width wellsite variant with LSD-prefixed surface line (`ReuseVariantMixedWellsitePad`).
  - existing-label variant detection now recognizes `A/R` and `(Surface)` content for mixed reuse keys.
- Verification:
  - `dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj --no-restore -v minimal` (with `DOTNET_CLI_HOME=.dotnet-home`) succeeded.
  - `dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj --no-restore -v minimal` (with `DOTNET_CLI_HOME=.dotnet-home`) passed.

# Follow-up (Mixed Variant Layer + Wellsite Placement, 2026-03-05)

- [x] Ensure mixed access-road label variant uses purpose-lookup layer suffix (`AR`) for text layer mapping (`C/F-*-T`).
- [x] Bias mixed wellsite variant placement target to safe interior point when available in-quarter to avoid road-corridor-only search failures.
- [x] Loosen mixed pad-signature heuristic so road+pad shapes still emit wellsite variant when width spread is moderate.
- [x] Rebuild plugin and rerun decision tests.

## Review (Mixed Variant Layer + Wellsite Placement, 2026-03-05)

- Updated `src/AtsBackgroundBuilder/Core/Plugin.cs`:
  - mixed variants now compute layer names from mixed-purpose lookup entries (`TryBuildMixedPurposeLayerNames(...)`), so access-road text can land on `F-AR-T` for non-client records.
  - mixed access-road and wellsite `DispositionInfo` instances now use variant-specific layer names.
  - mixed classifier pad-signature thresholds relaxed (`spread >= 1.25` or pad-like shape check) to reduce false negatives for connected pad geometry.
- Updated `src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs`:
  - for `ReuseVariantMixedWellsitePad`, placement target now prefers `SafePoint` when it lies inside the current quarter+disposition intersection context.
- Verification:
  - `dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj --no-restore -v minimal` (with `DOTNET_CLI_HOME=.dotnet-home`) succeeded.
  - `dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj --no-restore -v minimal` (with `DOTNET_CLI_HOME=.dotnet-home`) passed.

# Follow-up (Mixed Wellsite Pad Placement + WS Linework Layer, 2026-03-05)

- [x] Force mixed-disposition source linework layer to wellsite layer variant (`C/F-WS`) when mixed wellsite variant is present.
- [x] Improve mixed wellsite label anchor selection to prefer widest local corridor sample center (`MaxCenter`) before safe-point fallback.
- [x] Recompile and run decision tests.

## Review (Mixed Wellsite Pad Placement + WS Linework Layer, 2026-03-05)

- Updated `src/AtsBackgroundBuilder/Core/Plugin.cs`:
  - line entity relayer now uses mixed-variant-aware `lineLayerForEntity`, preferring mixed wellsite line layer (`WS`) for mixed records.
- Updated `src/AtsBackgroundBuilder/Geometry/GeometryUtils.cs`:
  - `WidthMeasurement` now includes `MaxCenter` (widest sampled center).
- Updated `src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs`:
  - mixed wellsite variant now anchors label search to `MeasureCorridorWidth(...).MaxCenter` (pad-biased) with safe-point fallback.
- Verification:
  - `dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -v minimal` succeeded (NU1900 warning only).
  - `dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj --no-restore -v minimal` passed.

# Follow-up (Mixed Surface Label Interior Placement, 2026-03-05)

- [x] Replace mixed wellsite anchor heuristic with high-clearance interior target search inside quarter+disposition overlap.
- [x] Keep mixed wellsite text anchored to selected pad-center target before candidate spiral placement.
- [x] Rebuild and rerun decision tests.

## Review (Mixed Surface Label Interior Placement, 2026-03-05)

- Updated `src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs`:
  - added `TryFindMixedWellsitePadTarget(...)` with interior-clearance scoring and overlap-grid sampling.
  - mixed wellsite variant now uses that target for `searchTarget`/`leaderTarget`, improving pad interior placement.
- Verification:
  - `dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -v minimal` succeeded.
  - `dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj --no-restore -v minimal` passed.
  - `dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj --no-restore -v minimal` succeeded.

# Follow-up (Full Build Crash Investigation, 2026-03-05)

- [x] Inspect the latest ATS runtime log and identify the last completed stage before termination.
- [x] Correlate the failing stage with the current code path and recent crash-prone import/PLSR guards.
- [x] Implement the smallest safe fix and rebuild.
- [x] Verify the result with a compile/test path and document findings in this file.

## Review (Full Build Crash Investigation, 2026-03-05)

- Latest crash boundary from `build\net8.0-windows\AtsBackgroundBuilder.log`:
  - run started `2026-03-05 6:46:14 PM`
  - reached `ATSBUILD stage: disposition_import`
  - entered large-file `DAB_APPL.shp` safe-import path
  - completed chunks 1-18
  - stopped at `Starting shapefile import chunk 19/42: DAB_APPL-chunk-0019.shp` -> `Importer.Import begin.` with no later `Importer.Import completed.` and no `ATSBUILD exit stage`
  - this indicates another native Map importer host termination, not a managed exception path.
- Confirmed source data pressure:
  - `C:\AUTOCAD-SETUP CG\SHAPE FILES\DISPOS\DAB_APPL.dbf` ~= `743889870` bytes
  - `C:\AUTOCAD-SETUP CG\SHAPE FILES\DISPOS\DAB_APPL.shp` ~= `237342868` bytes
- Root cause in source:
  - default large-file chunked mode was chunking the full source shapefile sequentially, not chunking a prefiltered in-scope subset.
  - for this 25-quarter build that meant driving native `Importer.Import()` through 42 generated chunks even though only a tiny in-scope subset was relevant.
- Updated `src/AtsBackgroundBuilder/Dispositions/ShapefileImporter.cs`:
  - added `TryCreateSpatiallyFilteredChunkedSubsetShapefiles(...)`
  - large-file default path now:
    - builds a spatial subset first using section extents (+ CRS-aware zone hint when available)
    - chunks that subset instead of chunking the full source file
    - logs `Chunked safe import scope filter ... kept X/Y record(s) before chunking.`
  - keeps location-window clipping active for generated subset/chunk imports instead of disabling it for prefiltered inputs.
  - preserves prior full-source chunk fallback only if the spatially filtered chunk-prep path cannot produce records.
- Verification:
  - `dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -v minimal` succeeded.
  - `dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -v minimal` passed (`Decision tests passed.`).
# Follow-up (PLSR Should Audit After Shape-Based Label Placement, 2026-03-05)

- [x] Inspect current disposition-label and PLSR execution order in source and runtime logs.
- [x] Identify why Shapes+PLSR runs now flag every disposition as missing.
- [x] Restore the intended order/behavior with the smallest safe fix.
- [x] Rebuild, run decision tests, and document the result here.
## Review (PLSR Should Audit After Shape-Based Label Placement, 2026-03-05)

- Root cause from current source/runtime:
  - `BuildExecutionPlan.ShouldPlaceLabelsBeforePlsr` required `IncludeDispositionLabels && !ShouldRunPlsrCheck`.
  - when both `Disposition labels` and `Check PLSR` were ON, normal shape-based label placement was disabled entirely.
  - runtime evidence in `build\net8.0-windows\AtsBackgroundBuilder.log` showed repeated runs with:
    - `ATSBUILD stage: processing_dispositions`
    - immediately followed by `ATSBUILD stage: plsr_check`
    - summary `Labels placed: 0`
    - latest successful full run also showed `Disposition label reuse: indexed 0 existing label(s) across 0 quarter(s)` before PLSR, which explains why all dispositions were reported missing.
- Updated source:
  - `src/AtsBackgroundBuilder/Core/BuildExecutionPlan.cs`
    - `ShouldPlaceLabelsBeforePlsr` now follows `IncludeDispositionLabels` directly.
    - effect: when labels are enabled, shape-based disposition labels are placed before PLSR audits drawing state.
    - `PLSR only` behavior is unchanged because `IncludeDispositionLabels` remains OFF there.
  - `src/AtsBackgroundBuilder.DecisionTests/Program.cs`
    - updated `TestBuildExecutionPlanLabelPlacementOrdering()` to assert that `labels + PLSR` still places labels before PLSR.
- Verification:
  - `dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -verbosity=minimal` succeeded.
  - `dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -v minimal` passed (`Decision tests passed.`).
  - full `dotnet build` hit a locked runtime copy target (`build\net8.0-windows\AtsBackgroundBuilder.dll` in use), so compile/test verification is the clean proof for this change.
# Follow-up (PLSR Owner Must Win For First-Time Labels, 2026-03-05)

- [x] Trace current owner sourcing for new-label creation and owner-mismatch issue generation.
- [x] Make PLSR authoritative when creating a label for the first time.
- [x] Suppress owner-mismatch reporting that is invalid under the new PLSR-first rule.
- [x] Rebuild, run decision tests, and document the result here.

## Review (PLSR Owner Must Win For First-Time Labels, 2026-03-05)

- Root cause from current source/runtime behavior:
  - ProcessDispositionPolylines(...) was always building new-label owner text from disposition OD/company mapping before PLSR review ran.
  - when Disposition labels + Check PLSR created a label for the first time, that fresh label could immediately disagree with XML owner and show up as a false Owner mismatch on the same run.
  - the same bad owner would also flow into PLSR missing-label creation from disposition geometry because those labels reuse DispositionInfo source data.
- Updated source:
  - src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs
    - added BuildPlsrOwnerOverridesByDispNum(...) to parse current PLSR XML inputs once up front into a DISP->owner override map using the same compare-name normalization as the audit.
    - latest report date wins per DISP when multiple XML files disagree; same-date conflicts are logged and left stable.
  - src/AtsBackgroundBuilder/Core/Plugin.cs
    - ProcessDispositionPolylines(...) now checks that override map before building label text / DispositionInfo.MappedCompany.
    - new labels now use PLSR owner by default when XML contains that DISP.
    - layer/client classification still follows the original disposition mapping so this fix stays scoped to owner text rather than relayering geometry.
    - runtime log now reports how many disposition label sources received a PLSR owner override.
- Result:
  - first-time labels created before PLSR audit now start with XML owner, so the audit no longer fabricates owner mismatches caused by the build itself.
  - existing labels with wrong owners are still eligible for real Owner mismatch review rows.
- Verification:
  - dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -v minimal succeeded.
  - dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -v minimal passed (Decision tests passed.).
  - only remaining build warning was NU1900 because the restricted environment could not fetch NuGet vulnerability metadata from https://api.nuget.org/v3/index.json.
# Follow-up (PLSR Expired Marker + Version-Date Aggregation, 2026-03-05)

- [x] Inspect expired-marker and version-date mismatch generation paths in the current PLSR flow.
- [x] Make first-time PLSR-backed labels inherit `(Expired)` before review/audit.
- [x] Aggregate repeated standalone version-date mismatch rows by DISP with quarter `MULTIPLE`.
- [x] Rebuild, run focused verification, and document the result here.

## Review (PLSR Expired Marker + Version-Date Aggregation, 2026-03-05)

- Root cause from current PLSR flow:
  - first-time labels created before PLSR review were inheriting the new XML owner override, but not the XML expired state, so the same run could still fabricate Expired in PLSR rows for labels it had just created.
  - standalone version-date mismatch rows were emitted once per quarter, which repeated the same DISP across multiple quarters even when the issue was really one repeated DISP-level mismatch.
- Updated source:
  - src/AtsBackgroundBuilder/Core/Plugin.cs
    - replaced the owner-only PLSR override map with a label-override map that carries both authoritative owner and whether (Expired) must be appended.
    - ProcessDispositionPolylines(...) now propagates that expired flag into every new DispositionInfo label source and logs how many label sources were affected.
  - src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs
    - added AppendExpiredMarkerIfMissing(...) and applies it during label placement whenever a disposition source is marked expired by PLSR.
  - src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs
    - missing-label review rows now carry ShouldAddExpiredMarker so accepted template/XML fallback creates also include (Expired) immediately.
    - added post-scan consolidation for repeated standalone Version date mismatch rows: duplicate DISP rows are collapsed into one row with quarter MULTIPLE and the contributing quarter keys listed in Detail.
- Result:
  - fresh shape-based labels should no longer self-trigger Expired in PLSR when XML already says they are expired.
  - accepted missing-label creates from template/XML will also include (Expired) on first creation when required.
  - repeated standalone version-date mismatches now show one review row per DISP with quarter MULTIPLE instead of one row per quarter.
- Verification:
  - dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -v minimal succeeded.
  - dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -v minimal passed (Decision tests passed.).
  - only remaining warning was NU1900 because the restricted environment could not fetch NuGet vulnerability metadata from https://api.nuget.org/v3/index.json.
# Follow-up (Crash After PLSR Expired Marker + Version-Date Aggregation, 2026-03-05)

- [x] Inspect the newest ATS runtime log and isolate the crash boundary.
- [x] Trace the failing code path introduced by the latest PLSR label/review changes.
- [x] Implement the smallest safe fix, rebuild, and verify.
- [x] Document the result here.

## Review (Crash After PLSR Expired Marker + Version-Date Aggregation, 2026-03-05)

- Crash boundary remained native `Importer.Import()` during `ATSBUILD stage: disposition_import` for `DAB_APPL.shp`; the newest failed run logged `CheckPLSR=OFF`, so the recent PLSR expired-marker/version-date changes were not on the executed path.
- Earlier successful runs in the same log imported the same scoped `DAB_APPL` subset (`112/415348` records kept), which points to nondeterministic Map importer instability on the generated filtered chunk file rather than a deterministic bad PLSR record.
- Hardened `ShapefileImporter` so large scoped imports now use the first filtered subset directly when it already fits within one safe import file; chunking is still used only when the filtered subset exceeds the chunk threshold or scoped subset prep fails.
- `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -v minimal` succeeded.
- `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -v minimal` passed.
# Follow-up (Disposition Label Placement Collision + Aligned Dimension Positioning, 2026-03-07)

- [x] Inspect current placement flow, aligned-dimension text positioning, and PLSR label-anchor assumptions.
- [x] Add a shared label collision index seeded from existing label text footprints in model space.
- [x] Make candidate scoring overlap-aware for MText/MLeader and finalize expired text before scoring.
- [x] Rework aligned-dimension creation to preserve along-span motion, search normal lanes, and fall back cleanly when no legal lane exists.
- [x] Rebuild the project, verify the changed PLSR quarter-anchor behavior remains anchor-based, and document results here.

## Review (Disposition Label Placement Collision + Aligned Dimension Positioning, 2026-03-07)

- Updated source:
  - `src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs`
    - `PlaceLabels(...)` now builds placement requests, seeds one shared label collision index from existing model-space label text footprints, sorts constrained requests first, and reuses that same collision state for newly placed labels.
    - `CreateAlignedDimensionLabel(...)` now preserves both along-span and normal components of `labelPoint`, searches multiple normal lanes, sets `TextPosition` explicitly, and returns `null` when no non-forced lane works so caller fallback can engage.
    - `CreateLabelEntity(...)` now uses try/fallback behavior: aligned dimension -> leader (when leaders are enabled) -> plain `MText`.
    - `GetCandidateLabelPoints(...)` now accepts final label text plus the shared collision index and ranks `MText` / `MLeader` candidates by overlap-aware text boxes instead of only proximity/clearance.
    - collision tests now use estimated text-content boxes for `MText`, `MLeader`, and aligned-dimension text, not whole-entity extents.
- PLSR safeguard:
  - quarter assignment logic remains in `Plugin.Dispositions.LabelingPlsr.cs` and still anchors aligned dimensions by extension-line midpoint via `GetDimensionAnchorPoint(...)`, with text/extents only used as supplemental touch evidence. No quarter-assignment logic was changed.
- Verification:
  - `dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v:minimal` succeeded.
  - `dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -v minimal` passed (`Decision tests passed.`).
  - `dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -v minimal` still failed only in the post-build copy target because `build\net8.0-windows\AtsBackgroundBuilder.dll` is locked by another process.
  - NuGet vulnerability metadata remained unavailable in this environment (`NU1900` against `https://api.nuget.org/v3/index.json`).

# Follow-up (Refresh Disposition Label Placement Reference Map, 2026-03-07)

- [x] Re-read the updated disposition label placement and PLSR anchor code paths.
- [x] Rewrite the reference file so it reflects current code only, including the new collision-aware placement and aligned-dimension fallback flow.
- [x] Remove stale pre-refactor notes/excerpts and verify the refreshed reference file matches the live source.

## Review (Refresh Disposition Label Placement Reference Map, 2026-03-07)

- Updated file:
  - `docs/reference/DISPOSITION_LABEL_PLACEMENT_SOURCE_MAP.txt`
    - replaced the older mixed source map with a current-only reference tied to the live post-refactor `LabelPlacer.cs` code
    - refreshed the entry-point list, current rules, cleanup note, and PLSR safeguard note
    - replaced stale excerpts with current blocks for `PlaceLabels(...)`, `CreateLabelEntity(...)`, `CreateAlignedDimensionLabel(...)`, `CreateLeader(...)`, `CreateLabel(...)`, collision seeding helpers, text-box helpers, candidate ranking helpers, and the current PLSR aligned-dimension anchor logic
- Verification:
  - checked the rewritten file head/tail to confirm the new sections and code fences rendered cleanly
  - verified the file now references the current live entry points from `LabelPlacer.cs`, `Plugin.cs`, and `Plugin.Dispositions.LabelingPlsr.cs`
  - verified the old pre-refactor map content is no longer present as the file was rewritten wholesale rather than patched in place

# Follow-up (Width Label Search Shape + Dim Text Decoupling, 2026-03-07)

- [x] Add a dedicated aligned-dimension text candidate generator instead of reusing the generic spiral search.
- [x] Route width/aligned labels through that generator in PlaceLabels(...) and widen the search radius / outside-disposition allowance.
- [x] Decouple aligned-dimension TextPosition from dimLinePoint so width text can live in nearby whitespace.
- [x] Rebuild, run decision tests, and document the result here.

## Review (Width Label Search Shape + Dim Text Decoupling, 2026-03-07)

- Updated source:
  - `src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs`
    - added `DimensionTextCandidate` plus `GetCandidateDimensionTextPoints(...)` so width labels now search dedicated quarter-contained whitespace lanes around the measured span instead of reusing the generic spiral.
    - updated `PlaceLabels(...)` so aligned-width requests use the new dimension candidate set, allow text outside the disposition body, widen the search radius, and score/reject candidates with aligned-dimension text boxes plus disposition-linework overlap checks.
    - updated `CreateAlignedDimensionLabel(...)` so the aligned dimension still measures the corridor span, but `TextPosition` now stays at the chosen whitespace candidate while `dimLinePoint` is pushed just outside the corridor edge on the same side.
    - kept the existing fallback chain in `CreateLabelEntity(...)`: aligned dimension -> leader -> plain `MText`.
- PLSR safeguard:
  - no quarter-assignment logic changed; `Plugin.Dispositions.LabelingPlsr.cs` still treats the dimension span midpoint as the primary aligned-dimension anchor and uses text/extents only as supplemental touch evidence.
- Verification:
  - `dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v:minimal` succeeded.
  - `dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -v minimal` passed (`Decision tests passed.`).
  - NuGet vulnerability metadata was still unavailable in this environment (`NU1900` against `https://api.nuget.org/v3/index.json`).
# Follow-up (Width Label Self-Clearance From Measured Line, 2026-03-07)

- [x] Tighten width-label candidate generation so the text block clears the measured corridor line by the full text-box height, not just center-point gap.
- [x] Reject aligned-dimension text points that still sit too close to the measured line, even on fallback.
- [x] Rebuild, rerun decision tests, and document the result here.

## Review (Width Label Self-Clearance From Measured Line, 2026-03-07)

- Updated source:
  - `src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs`
    - added measured-line clearance helpers so aligned width-label spacing now uses the estimated dimension-text block height, not just a center-point edge gap.
    - updated `GetCandidateDimensionTextPoints(...)` so outside lanes start far enough away for the full text box to clear the measured corridor, and candidate points that still sit too close to the measured line are rejected before scoring.
    - updated `CreateAlignedDimensionLabel(...)` so a fallback candidate is still rejected if its text block would sit too close to the measured corridor line.
- Effect:
  - width labels should no longer settle with the top text line hugging the corridor edge the way your screenshot showed; if no aligned-dimension text point clears the measured line, the aligned-dimension creation now fails cleanly instead of drawing a too-tight label.
- Verification:
  - `dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v:minimal` succeeded.
  - `dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -v minimal` passed (`Decision tests passed.`).
  - `dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -v minimal` built the project but still failed in the post-build copy target because `build\net8.0-windows\AtsBackgroundBuilder.dll` is locked by another process.
  - NuGet vulnerability metadata remained unavailable in this environment (`NU1900` against `https://api.nuget.org/v3/index.json`).
# Follow-up (Width Measurement Anchor Split From Text Search, 2026-03-07)

- [x] Add a dedicated width measurement target to placement requests instead of reusing the text-search target.
- [x] Use that measurement target for aligned-dimension span geometry while keeping text-search seeding independent.
- [x] Rebuild, rerun decision tests, and document the result here.

## Review (Width Measurement Anchor Split From Text Search, 2026-03-07)

- Updated source:
  - `src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs`
    - added `MeasurementTarget` to `PlacementRequest` so width labels now carry a dedicated in-corridor cross-section anchor separate from text-search and leader-avoidance targets.
    - width-request building now preserves the refined corridor midpoint as `measurementTarget` before running `ChooseLeaderTargetAvoidingOtherDispositions(...)` for text placement bias.
    - aligned-dimension candidate generation and final A-DIM creation now use `request.MeasurementTarget` for span geometry, while text search still uses `request.SearchTarget` for side/whitespace preference.
    - width fallback creation now also anchors leaders back to the preserved measurement target rather than the displaced text-search anchor.
- Effect:
  - bent-corridor width labels should keep their aligned-dimension extension lines on the intended corridor cross-section even when the text-search target moves past a bend to find whitespace.
- Verification:
  - `dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v:minimal` succeeded.
  - `dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -v minimal` passed (`Decision tests passed.`).
  - `dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -v minimal` succeeded.
  - current build outputs are synced at `2026-03-07 19:28:37` in both `src\AtsBackgroundBuilder\bin\Release\net8.0-windows` and `build\net8.0-windows`.
  - NuGet vulnerability metadata remained unavailable in this environment (`NU1900` against `https://api.nuget.org/v3/index.json`).
# Follow-up (Local Width Target Resolution On Bent Corridors, 2026-03-07)

- [x] Resolve width measurement targets from local cross-sections near the quarter/intersection area instead of relying on a single global median sample.
- [x] Keep aligned-dimension span geometry anchored to that local width target while preserving separate text-search behavior.
- [x] Rebuild, rerun decision tests, and document the result here.

## Review (Local Width Target Resolution On Bent Corridors, 2026-03-07)

- Updated source:
  - `src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs`
    - width-label target selection no longer starts from `measurement.MedianCenter` alone.
    - added `TryResolveLocalWidthMeasurementTarget(...)`, which samples many local corridor cross-sections, evaluates their midpoint proximity to the quarter/intersection target, and uses the best local midpoint as the aligned-dimension measurement anchor.
    - width-request setup now uses that locally resolved midpoint as `measurementTarget`, then separately runs leader/text-placement avoidance from that anchor.
- Effect:
  - bent-corridor width labels should stop jumping to another leg simply because a global median width sample happened to land past the bend.
- Verification:
  - `dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v:minimal` succeeded.
  - `dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -v minimal` passed (`Decision tests passed.`).
  - `dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -v minimal` still failed only in the post-build copy target because `build\net8.0-windows\AtsBackgroundBuilder.dll` was locked by another process.
  - NuGet vulnerability metadata remained unavailable in this environment (`NU1900` against `https://api.nuget.org/v3/index.json`).
# Follow-up (Bend-Proximity Penalty For Width Targets, 2026-03-07)

- [x] Penalize local width-target candidates that sit too close to corridor vertices/bends.
- [x] Rebuild, rerun decision tests, and verify the bend-avoidance pass is in the current output DLL.
- [x] Document the result here.

## Review (Bend-Proximity Penalty For Width Targets, 2026-03-07)

- Updated source:
  - `src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs`
    - added `GetNearestVertexDistance(...)` and updated `TryResolveLocalWidthMeasurementTarget(...)` so local width-target scoring now penalizes bend-adjacent cross-sections instead of treating proximity to the overlap target as the only dominant factor.
    - the local width-target resolver still prefers nearby, width-consistent samples, but now strongly prefers ones that are farther from corridor vertices, which should move A-DIM anchors off transition nodes and onto straighter legs.
- Effect:
  - bent-corridor width labels should be less willing to measure right at the kink when a nearby straight-section cross-section is available in the same local area.
- Verification:
  - `dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v:minimal` succeeded.
  - `dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -v minimal` passed (`Decision tests passed.`).
  - `dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -v minimal` succeeded.
  - current synced outputs:
    - `src\AtsBackgroundBuilder\bin\Release\net8.0-windows\AtsBackgroundBuilder.dll`
    - `build\net8.0-windows\AtsBackgroundBuilder.dll`
    - both at `2026-03-07 22:21:10`, size `1065984`, SHA-256 `056C4D239111CDB2305A3E523681F8307C2EB6A0C949E776DAA7653AE4F2A12A`.
  - NuGet vulnerability metadata remained unavailable in this environment (`NU1900` against `https://api.nuget.org/v3/index.json`).
# Follow-up (Sticky Width Span Anchor, 2026-03-07)

- [x] Preserve the original measured width span anchor when it is valid instead of relocating it to satisfy text-placement heuristics.
- [x] Rebuild and redeploy the matching DLL/PDB so AutoCAD loads the sticky-anchor behavior.
- [x] Document the result here.

## Review (Sticky Width Span Anchor, 2026-03-07)

- Updated source:
  - src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs
    - width-label setup now keeps measurement.MedianCenter as the primary measurement anchor when it is valid inside the quarter/corridor.
    - local bent-corridor width-target resolution is now fallback-only; it no longer replaces a valid original measured span just because text-placement search prefers another area.
- Effect:
  - if the original aligned-dimension measured points were already correct, the code should now keep those points fixed and only solve text placement separately.
- Verification:
  - dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v:minimal succeeded.
  - dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -v minimal passed (Decision tests passed.).
  - forced rebuild via dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Rebuild -p:Configuration=Release -p:NuGetAudit=false -v:minimal succeeded.
  - current synced outputs:
    - src\AtsBackgroundBuilder\bin\Release\net8.0-windows\AtsBackgroundBuilder.dll
    - uild\net8.0-windows\AtsBackgroundBuilder.dll
    - deployed C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows\AtsBackgroundBuilder.dll
    - all three at 2026-03-07 22:33:12, size 1065984, SHA-256 1EA1A1CD1122A7741396DABB98CAB7CE20A74F8D7AEA9C12C8C45180962A9097.
  - NuGet vulnerability metadata remained unavailable in this environment (NU1900 against https://api.nuget.org/v3/index.json).

# Follow-up (Keep A-DIM Text And Hinge On Measured Span, 2026-03-07)

- [x] Clamp aligned-dimension text along-span placement so the label body stays over the measured span.
- [x] Move the aligned-dimension hinge/dim-line point to the same along-span position as the text instead of leaving it fixed at midpoint.
- [x] Rebuild, rerun decision tests, deploy the matching DLL/PDB, and document the result here.

## Review (Keep A-DIM Text And Hinge On Measured Span, 2026-03-07)

- Updated source:
  - src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs
    - added EstimateDimensionTextAlongHalfExtent(...) and ClampDimensionTextAlongOffset(...) so moved aligned-dimension text is clamped by the actual measured span length and estimated text width instead of only by generic candidate distance.
    - updated GetCandidateDimensionTextPoints(...) so width-label candidate text stations are pre-clamped to legal along-span positions before scoring.
    - updated CreateAlignedDimensionLabel(...) so the final TextPosition and the dimension-line hinge (dimLinePoint) use the same clamped along-span projection, keeping both on the measured segment while preserving independent normal offset for whitespace placement.
- Effect:
  - moved aligned-dimension text should no longer push the hinge/jog off the measured segment, which was causing missing or misleading return lines in bend-adjacent width labels.
- Verification:
  - dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v:minimal succeeded.
  - dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -v minimal passed (Decision tests passed.).
  - dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -v minimal compiled the project but still failed only in the post-build copy step because uild\net8.0-windows\AtsBackgroundBuilder.dll was locked by another process.
  - deployed src\AtsBackgroundBuilder\bin\Release\net8.0-windows\AtsBackgroundBuilder.dll and matching .pdb directly to C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows.
  - current source/deploy DLLs are synced at 2026-03-07 22:43:38, size 1067008, SHA-256 9DA4F08198D745A14AAF0B790CFD80E2474CE1F69D6A7CC9F8A50A4EC973AF3A.
  - NuGet vulnerability metadata remained unavailable in this environment (NU1900 against https://api.nuget.org/v3/index.json).
# Follow-up (Preserve A-DIM Placement During Post-Placement Text Updates, 2026-03-08)

- [x] Review all post-placement aligned-dimension mutation paths and confirm which ones can trigger AutoCAD recompute.
- [x] Back out the generic post-creation re-clamp that regressed many aligned-dimension labels.
- [x] Preserve existing aligned-dimension placement during PLSR owner/expired text rewrites instead of recomputing geometry.
- [x] Build, test, deploy the corrected DLL/PDB, and document the verified result here.

## Review (Preserve A-DIM Placement During Post-Placement Text Updates, 2026-03-08)

- Findings:
  - CleanupAfterBuild(...) only erases temporary/helper geometry and imported disposition linework. It does not move label entities.
  - The regression came from the generic aligned-dimension re-clamp path added in the previous pass; that normalization step was moving many width labels off their intended lines.
  - The legitimate post-placement mutator is the PLSR owner/expired text-update flow in Plugin.Dispositions.LabelingPlsr.cs.
- Updated source:
  - src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs
    - removed the post-creation call that re-normalized aligned-dimension placement after creation, so width labels now keep the geometry chosen by CreateAlignedDimensionLabel(...).
  - src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs
    - added aligned-dimension placement snapshot/restore helpers that capture TextPosition, DimLinePoint, and UsingDefaultTextPosition before a PLSR text rewrite.
    - after owner or expired-marker updates, aligned dimensions now restore that captured placement instead of recomputing a new one.
    - generic non-aligned Dimension fallback behavior remains unchanged.
- Effect:
  - aligned dimensions that were already on the correct measured line should stay there through PLSR text updates instead of being re-solved onto a worse line or hinge position.
- Verification:
  - dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v:minimal succeeded.
  - dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -v minimal passed (Decision tests passed.).
  - dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -v minimal compiled the project but still failed only in the post-build copy step because uild\net8.0-windows\AtsBackgroundBuilder.dll was locked by another process.
  - deployed src\AtsBackgroundBuilder\bin\Release\net8.0-windows\AtsBackgroundBuilder.dll and matching .pdb directly to C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows.
  - current source/deploy DLLs are synced at 2026-03-08 10:33:08, size 1070592, SHA-256 78EE314EAE305441D00C1F9A0C8487FF4973F767D02FDFE92DC8C434ED88D5F4.
  - NuGet vulnerability metadata remained unavailable in this environment (NU1900 against https://api.nuget.org/v3/index.json).
# Follow-up (Rollback Recent A-DIM Line-Preservation Experiments, 2026-03-08)

- [x] Revert the recent A-DIM along-span clamp changes while keeping the earlier overlap search improvements.
- [x] Revert the new PLSR aligned-dimension placement snapshot/restore path.
- [x] Rebuild, test, deploy, and verify the rollback output.

## Review (Rollback Recent A-DIM Line-Preservation Experiments, 2026-03-08)

- Updated source:
  - src/AtsBackgroundBuilder/Dispositions/LabelPlacer.cs
    - restored the earlier width-label creation behavior: CreateAlignedDimensionLabel(...) again uses the chosen labelPoint directly for text position and puts dimLinePoint back on the dimension line through the span midpoint instead of the recent along-span clamp.
    - restored the earlier width-label candidate lanes by removing the recent along-span clamping from GetCandidateDimensionTextPoints(...).
  - src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs
    - removed the new aligned-dimension placement-preservation calls from the owner/expired text-update path so PLSR is back to the simpler pre-experiment behavior.
- Effect:
  - this rolls back the last few experimental line-preservation changes that made many labels worse, while keeping the earlier collision/whitespace improvements in place.
- Verification:
  - dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v:minimal succeeded.
  - dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -v minimal passed (Decision tests passed.).
  - fresh rebuild via dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release -p:NuGetAudit=false -v minimal succeeded with no warnings.
  - deployed src\AtsBackgroundBuilder\bin\Release\net8.0-windows\AtsBackgroundBuilder.dll and matching .pdb directly to C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows.
  - current source/deploy DLLs are synced at 2026-03-08 11:04:42, size 1070080, SHA-256 3D52F323428FA3CF685272B67936070B6A505FADFBDA28E76985EB4596C75865.
# ATSBUILD_XLS (2026-03-08)

- [x] Trace the existing ATSBUILD command/UI build path and identify the reusable execution entry point.
- [x] Reuse existing Excel reading support or add a minimal workbook reader for Blank_Template / ATSBUILD_Input.
- [x] Implement ATSBUILD_XLS to prompt for a workbook, parse client/zone/legal rows, apply the fixed preset options, and run the same build workflow.
- [x] Emit command-line progress messages during long-running build stages.
- [x] Build and verify the new command path.

## Review (ATSBUILD_XLS, 2026-03-08)

- Updated source:
  - src/AtsBackgroundBuilder/Core/Plugin.cs
    - extracted the post-input ATSBUILD execution block into a shared `ExecuteAtsBuildFromInput(...)` path so the UI command and the Excel-driven command run the same build workflow.
  - src/AtsBackgroundBuilder/Core/Plugin.Core.AtsBuildXls.cs
    - added the new `ATSBUILD_XLS` AutoCAD command.
    - prompts for a workbook path from the command line and falls back to a file picker when the user presses Enter.
    - writes stage/progress messages to the AutoCAD command line for workbook prompt/loading and each existing ATSBUILD build stage.
  - src/AtsBackgroundBuilder/Core/AtsBuildExcelInputLoader.cs
    - reads `ATSBUILD_Input` or `Blank_Template` from Excel via OleDb.
    - reads client from `B1`, zone from `E1`, finds the legal-description header row, and converts rows into `SectionRequest`s using the existing parser.
    - applies the fixed preset options requested for ATSBUILD_XLS.
- Workbook verification:
  - inspected the example workbook at `src/AtsBackgroundBuilder/REFERENCE ONLY/ATSBUILD_Simple_Input.xlsx`.
  - confirmed the workbook contains both `ATSBUILD_Input` and `Blank_Template` sheets and that the legal-description header row is on row 8 with `1/4`, `Sec.`, `Twp.`, `Rge.`, `M`.
- Verification:
  - `dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false /v:minimal` succeeded.
  - `dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release -p:NuGetAudit=false -v minimal` compiled the plugin and produced a fresh `bin\Release` DLL, but failed in the post-build copy step because `build\net8.0-windows\AtsBackgroundBuilder.dll` was locked by AutoCAD.
  - current compiled output: `src\AtsBackgroundBuilder\bin\Release\net8.0-windows\AtsBackgroundBuilder.dll` at 2026-03-08 19:41:02, size 1083392.
  - runtime AutoCAD execution of `ATSBUILD_XLS` was not verifiable in this shell; the validation here is workbook-shape inspection plus compile success.

# Follow-up (WLS Proposed/100m/Outside Prompt Parity, 2026-03-09)

- [x] Inspect the existing WLS `PROPOSED / 100m` and `PROPOSED / 100m / OUTSIDE` flows to identify the selection/classification mismatch.
- [x] Update the WLS Complete From Photos flow so `PROPOSED / 100m / OUTSIDE` prompts for `PROPOSED` and `100m` boundaries plus three sample blocks (`PROPOSED`, `100m`, `OUTSIDE`).
- [x] Verify numbering/sorting/table grouping still behave like `PROPOSED / 100m`, with `OUTSIDE` appended as the third group.
- [x] Build `WildlifeSweeps` and record the verification result here.

## Review (WLS Proposed/100m/Outside Prompt Parity, 2026-03-09)

- Findings:
  - `PROPOSED / 100m / OUTSIDE` had drifted from the `PROPOSED / 100m` flow: it only prompted for one `100m` boundary, used one generic sample block for all in-buffer findings, and hardcoded outside findings to block `proposed`.
- Updated source:
  - `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`
    - introduced shared area-specific prompt handling so both buffer modes now prompt for `PROPOSED` and `100m` boundaries.
    - `PROPOSED / 100m / OUTSIDE` now also prompts for a third `OUTSIDE` sample block instead of forcing block `proposed`.
    - insertion/classification logic now uses `ClassifyBufferArea(...)` for both area-specific modes, so block selection, duplicate-number checks, and record ordering stay aligned across `PROPOSED`, `100m`, and `OUTSIDE`.
    - table creation now keeps the `PROPOSED -> 100m` spacer behavior for the three-area mode while still appending `OUTSIDE` last.
  - `wls_program/src/WildlifeSweeps/Ui/PaletteControl.cs`
    - updated the tooltip so the UI description matches the actual prompt flow.
- Verification:
  - `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors.
  - Runtime AutoCAD prompt behavior was not directly verifiable in this shell; validation here is compile success plus the code-path diff showing prompt/classification parity.

# ATSPLOT_AUTO (2026-03-09)

- [x] Inspect the ATSBUILD flow for a safe â€œlast build extentsâ€ source and confirm there is no existing plotting helper to reuse.
- [x] Persist the last ATSBUILD window so plotting can run later as a separate command instead of piggybacking on build.
- [x] Add `ATSPLOT_AUTO` to plot model space from the last ATSBUILD window at map scale `1:5000`.
- [x] Build, deploy, and verify the updated plugin DLL.

## Review (ATSPLOT_AUTO, 2026-03-09)

- Updated source:
  - `src/AtsBackgroundBuilder/Core/Plugin.cs`
    - persists the ATSBUILD plot window just before cleanup so the saved extents reflect the build area even when helper geometry is later erased.
  - `src/AtsBackgroundBuilder/Core/Plugin.Core.Plotting.cs`
    - added `ATSPLOT_AUTO`.
    - stores the last ATSBUILD window in the drawing Named Objects Dictionary under an `ATSBUILD / LAST_BUILD_WINDOW` Xrecord.
    - reads that saved window later and plots model space to PDF using `DWG To PDF.pc3`, `acad.ctb`, and a custom print scale equivalent to map scale `1:5000` for meter-based ATSBUILD geometry (`1 mm : 5 m`).
    - chooses a large available media size, centers the saved build window, and warns if the saved build extents may not fit on the selected paper at `1:5000`.
    - writes the PDF beside the drawing as `<drawing>_ATSPLOT_AUTO.pdf` and appends a timestamp if that file already exists.
- Assumptions:
  - ATSBUILD geometry is drawn in meters, so a map scale of `1:5000` is represented in AutoCAD plot custom scale as `1 mm : 5 drawing units`.
  - `acad.ctb` and `DWG To PDF.pc3` are available on this workstation, which was confirmed from the AutoCAD user plotter/plot-style directories.
- Verification:
  - `dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false /v:minimal` succeeded.
  - `dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors.
  - deployed plugin DLL/PDB were recopied after the build completed; the current `build` DLL and deployed AutoCAD DLL match exactly at SHA-256 `741D372B24AF070E7370888A5B045420A30669B7C70C3C9F2E9B3A8B5A982294`.
  - runtime AutoCAD plotting behavior was not directly verifiable in this shell; the validation here is compile success, local availability of the target PC3/CTB assets, and the deployed DLL hash match.

## Follow-up (ATSPLOT_AUTO Runtime Hardening, 2026-03-09)

- Hardened plot setup to start from the current model layout configuration instead of assuming AutoCAD will accept a forced device/media reset.
- Added step-specific runtime diagnostics so plot setup failures now report which stage failed (for example plot window, scale, style sheet, or rotation) instead of surfacing only a generic `eInvalidInput`.
- Rebuilt and redeployed the plugin; current deployed DLL hash matches build hash `7C2C18DCA14C48FFF2E62C13D1902F40C9125F4E9C2C19758BD25CC6EA97F83F`.

# Follow-up (ATSPLOT_AUTO Native Plot Fallback, 2026-03-09)

- [x] Replace the managed plot-engine execution path with a native AutoCAD `-PLOT` command script that reuses the current model page setup.
- [x] Keep the saved ATSBUILD extents and output-path logic, but feed the extents into the native plot window/scale prompts instead of `PlotInfoValidator`.
- [x] Rebuild, verify the DLL output, deploy it if AutoCAD is closed, and capture the runtime result.

## Review (ATSPLOT_AUTO Native Plot Fallback, 2026-03-09)

- Updated source:
  - `src/AtsBackgroundBuilder/Core/Plugin.Core.Plotting.cs`
    - removed the managed `PlotInfoValidator` / `PlotFactory` execution path that kept failing on this workstation's model-space media configuration.
    - `ATSPLOT_AUTO` now queues a native AutoCAD `-PLOT` command through `SendStringToExecute`, using the saved ATSBUILD extents as the plot window and the current model layout as the plot baseline.
    - preserves the current model plot config, media, and CTB when they are already suitable; otherwise falls back to `DWG To PDF.pc3` and `Plot Color.ctb`.
    - keeps the `1:5000` map-scale intent by feeding `1=5` for metric layouts and `1=127` for inch-based layouts into the native plot prompt.
- Effect:
  - plotting no longer depends on `PlotInfoValidator`, which was rejecting this workstation's current PDF/media setup with `eInvalidInput` / `eNoMatchingMedia` even though the same setup was valid in the AutoCAD UI.
- Verification:
  - `dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v:minimal` succeeded.
  - `dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -v minimal` passed (`Decision tests passed.`).
  - `dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors.
  - deployed `build\net8.0-windows\AtsBackgroundBuilder.dll` and matching `.pdb` to `C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\net8.0-windows` after clearing the AutoCAD file lock.
  - current build/deploy DLL SHA-256 now matches at `B69EBCEC3C71B8DDCEBB961AC9F1079712B02B0372805A6326CA83C0A157B2A2`.

# Follow-up (ATSPLOT_AUTO Adobe PDF Device Guard, 2026-03-09)

- [x] Reproduce/trace the Adobe PDF PostScript-font popup path in native `-PLOT`.
- [x] Restrict device reuse so only AutoCAD `DWG To PDF` devices are kept; force `DWG To PDF.pc3` for Adobe PDF.
- [x] Add runtime diagnostics when an unsupported model device is overridden.
- [x] Build plugin and run decision tests.

## Review (ATSPLOT_AUTO Adobe PDF Device Guard, 2026-03-09)

- Updated source:
  - `src/AtsBackgroundBuilder/Core/Plugin.Core.Plotting.cs`
    - changed native plot device reuse logic to whitelist only current devices containing `DWG To PDF`.
    - explicitly rejects `Adobe PDF` reuse and forces `DWG To PDF.pc3` in the scripted `-PLOT` call.
    - logs a command-line message when overriding a non-supported model plot device so runtime behavior is visible.
- Effect:
  - `ATSPLOT_AUTO` no longer inherits `Adobe PDF` from model layout configs, preventing the PostScript font popup that stops unattended plotting.
- Verification:
  - `dotnet build -c Release src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj` succeeded (`0` errors; `NU1900` warning only from blocked NuGet vulnerability feed).
  - `dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` passed (`Decision tests passed.`).
  - verified compiled outputs match: `src\AtsBackgroundBuilder\bin\Release\net8.0-windows\AtsBackgroundBuilder.dll` and `build\net8.0-windows\AtsBackgroundBuilder.dll` both hash to `52747DCECF85BE6C1193F21F1A63A613EDFC689D7C3428C84322968D22EEE2F1`.
  - Runtime AutoCAD execution could not be verified in this shell; this pass verifies compile/test correctness and the guarded device-selection path.

# Follow-up (ATSPLOT_AUTO Prompt Alignment + Full-Window Fallback, 2026-03-09)

- [x] Fix native `-PLOT` scripted prompt ordering for `DWG To PDF.pc3` so file-name/save/proceed prompts are answered correctly.
- [x] When 1:5000 cannot fit the selected paper, auto-fallback to `Fit` scale so the entire saved ATSBUILD window plots.
- [x] Keep the existing warning and add an explicit command-line message when the scale fallback is applied.
- [x] Build plugin, run decision tests, and verify `bin`/`build` DLL hash parity.

## Review (ATSPLOT_AUTO Prompt Alignment + Full-Window Fallback, 2026-03-09)

- Updated source:
  - `src/AtsBackgroundBuilder/Core/Plugin.Core.Plotting.cs`
    - corrected `BuildNativePlotLispScript(...)` token order by removing the extra `_Yes` before file-name input; this aligns scripted responses with `DWG To PDF` prompt flow.
    - changed `WarnIfPlotMayNotFit(...)` to return fit status and kept the same warning output when 1:5000 cannot fit.
    - added `shouldUseFitScale` flow in `QueueLastAtsBuildWindowNativePlot(...)`; when fit fails at 1:5000, the script now uses `Fit` scale and logs that fallback.
- Effect:
  - `ATSPLOT_AUTO` no longer falls into interactive `Yes or No` prompt loops caused by misaligned scripted responses.
  - Oversized ATS windows now plot as full-window PDFs on the current paper instead of clipping to a near-empty page at forced 1:5000.
- Verification:
  - `dotnet build -c Release src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj` succeeded (`0` errors; `NU1900` warning only from blocked NuGet vulnerability feed).
  - `dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` passed (`Decision tests passed.`).
  - verified compiled outputs match: `src\AtsBackgroundBuilder\bin\Release\net8.0-windows\AtsBackgroundBuilder.dll` and `build\net8.0-windows\AtsBackgroundBuilder.dll` both hash to `6C775A755C4B2CEB366AC8524B7BD180DAE4A5F128B9D4052440B4A758C681EF`.
  - Runtime AutoCAD execution could not be verified in this shell; this pass validates compile/test correctness plus script/scale fallback logic.
# Follow-up (Workspace Build, 2026-03-10)

- [x] Build `src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj` in Release.
- [x] Build `wls_program/src/WildlifeSweeps/WildlifeSweeps.csproj` in Release.
- [x] Record the build results and any blocking warnings/errors.

## Review (Workspace Build, 2026-03-10)

- Verification:
  - `dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors after an escalated restore/build, because sandboxed restore could not reach `api.nuget.org` for `System.Data.OleDb`.
  - `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors.
- Notes:
  - Initial sandboxed `dotnet` runs also tried to create first-run state under `C:\Users\CodexSandboxOffline\.dotnet`; using a workspace-local `DOTNET_CLI_HOME` resolved that for in-sandbox builds.
  - No source files were changed during the build; only `tasks/todo.md` was updated to record the result.

# Follow-up (Points.csv + Unmatched Resolver, 2026-03-10)

- [x] Confirm remaining CSV column/layout ambiguities with user.
- [x] Replace the current 15-column CSV export with the requested wide `Points.csv` layout.
- [x] Fix DMS degree-symbol output so exported coordinates do not contain `Â°`.
- [x] Simplify the unmatched finding dialog to show the found text, accept a replacement value, keep `Ignore`, and persist the mapping into the lookup workbook.
- [x] Build `wls_program/src/WildlifeSweeps/WildlifeSweeps.csproj` and record verification.

## Review (Points.csv + Unmatched Resolver, 2026-03-10)

- Updated source:
  - `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`
    - replaced the old 15-column export with a fixed-position `Points.csv` writer that emits one row per finding, no header row, global earliest photo date in column `B`, per-row photo date in `GQ`, fixed literals in `A/S/U/V/X/GR`, and ATS location pieces in `AD` through `AJ`.
    - changed the suggested export filename to `Points.csv`.
    - carries quarter token, section, township, range, and meridian alongside the full ATS LSD location so the CSV can populate `AE` through `AJ` directly.
  - `wls_program/src/WildlifeSweeps/FindingsStandardizationHelper.cs`
    - simplified the unmapped prompt flow to a single replacement-text input with `OK`, `Skip`, and `Ignore`.
    - removed species/finding-type validation from the manual prompt path and simplified the non-automatic log columns.
  - `wls_program/src/WildlifeSweeps/Ui/UnmappedFindingDialog.cs`
    - replaced the species/finding-type/description picker UI with a read-only found-text box and one editable replacement-text field.
  - `wls_program/src/WildlifeSweeps/FindingsDescriptionStandardizer.cs`
    - allows description-only keyword rules.
    - persists manual unmapped resolutions back into the lookup workbook `RecognitionKeywords` sheet instead of relying on the prior JSON-only save path.
    - keeps the mapping live for the current run immediately after the user enters it.
  - `wls_program/src/WildlifeSweeps/DmsFormatter.cs`
    - corrected the bad degree symbol literal.
- Verification:
  - `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors.
- Residual risk:
  - Workbook writeback was verified by compile only in this shell; runtime confirmation still depends on exercising the unmapped dialog against a writable lookup workbook from AutoCAD.

# Follow-up (Points.csv Meridian Text Preservation, 2026-03-10)

- [x] Stop Excel from auto-converting column `AJ` meridian values like `5 -5` into dates on CSV open.
- [x] Verify the code compiles after the meridian export change.
- [x] Document the CSV formatting limitation for hidden/shrunk blank columns.

## Review (Points.csv Meridian Text Preservation, 2026-03-10)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs` so column `AJ` is emitted in an Excel text-preserving form instead of raw `5 -5`, which Excel was interpreting as `05-May` when opening the CSV directly.
- Verification:
  - `dotnet msbuild wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v minimal` succeeded.
  - A full `dotnet build` retry after this change hit only a locked `bin\Release\net8.0-windows\WildlifeSweeps.dll`, not a compile error.
- Limitation:
  - CSV cannot store hidden columns or column widths. If you want the blank columns hidden/shrunk automatically, the export needs to be `.xlsx`, not `.csv`.
# Follow-up (Points.xlsx Export, 2026-03-10)

- [x] Switch the findings export from `Points.csv` to `Points.xlsx`.
- [x] Keep the same fixed data mapping while preserving meridian as plain text in column `AJ`.
- [x] Hide worksheet columns that are blank across the exported dataset.
- [x] Compile `WildlifeSweeps` after the workbook export change.

## Review (Points.xlsx Export, 2026-03-10)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs` so the export prompt now saves `Points.xlsx` and writes a minimal OpenXML workbook instead of CSV.
- The workbook writer outputs the same point rows as before, but stores values as worksheet text cells, so Excel no longer auto-converts meridian values like `5 -5` into dates.
- Blank columns are now hidden automatically when every exported row leaves that column empty.
- Verification:
  - `dotnet msbuild wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v minimal` succeeded.
- Note:
  - I used compile-only verification here because the full `bin\Release` DLL can be locked by another process on this machine.

# Follow-up (Snowshoe Hare Resolver Alias, 2026-03-10)

- [x] Find the rabbit/hare matcher paths that still emit `Rabbit` outputs.
- [x] Canonicalize resolver output and prompt/custom mappings to `Snowshoe Hare`.
- [x] Align the lookup workbook and sample matcher docs with the `Snowshoe Hare` wording.
- [x] Build `wls_program/src/WildlifeSweeps/WildlifeSweeps.csproj` in Release and record verification.

## Review (Snowshoe Hare Resolver Alias, 2026-03-10)

- Updated `wls_program/src/WildlifeSweeps/FindingsDescriptionStandardizer.cs` so regex rules, keyword rules, workbook-derived species options, custom mappings, and prompt-entered descriptions all canonicalize `Rabbit` / `Rabbit / Hare` outputs to `Snowshoe Hare` before the result is used or persisted.
- Updated `wls_program/wildlife_parsing_codex_lookup.xlsx` so the matcher workbook now stores `Snowshoe Hare` species and `Snowshoe Hare ...` standard descriptions in `SpeciesFindingTypes`, `RecognitionKeywords`, and `RecognitionRegex`, while leaving raw rabbit/hare keywords and regex patterns intact for matching.
- Updated `wls_program/docs/findings-rules.sample.json` to use `Snowshoe Hare Scat` in the sample description.
- Verification:
  - `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors using a workspace-local `DOTNET_CLI_HOME`.
  - A workbook XML check confirmed the relevant lookup sheets now store `Snowshoe Hare` / `Snowshoe Hare ...` labels instead of the prior rabbit output labels.

# Follow-up (Points.xlsx Numeric Cell Types + Meridian Column, 2026-03-10)

- [x] Inspect the current Points.xlsx writer and confirm which columns should be numeric.
- [x] Update the export so lat/long, AF-AI, and GP are written as numeric cells, and move meridian from AJ to AI.
- [x] Build `wls_program/src/WildlifeSweeps/WildlifeSweeps.csproj` in Release and record verification.

## Review (Points.xlsx Numeric Cell Types + Meridian Column, 2026-03-10)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs` so the Points workbook now writes latitude/longitude (`Y`/`Z`), ATS numeric fields (`AF`/`AG`/`AH`/`AI`), and finding number (`GP`) as numeric worksheet cells instead of inline strings.
- Moved the meridian export from column `AJ` to column `AI`; `AI` now receives the raw meridian value and `AJ` is left blank.
- Verification:
  - `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors using a workspace-local `DOTNET_CLI_HOME`.


# Follow-up (Points.xlsx Cell Style, 2026-03-10)

- [x] Inspect the current workbook writer and locate the style/relationship points needed for XLSX cell styling.
- [x] Add a shared cell style so exported Points.xlsx cells use Calibri with top vertical alignment and left horizontal alignment.
- [x] Build `wls_program/src/WildlifeSweeps/WildlifeSweeps.csproj` in Release and record verification.

## Review (Points.xlsx Cell Style, 2026-03-10)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs` so the generated Points workbook now includes an `xl/styles.xml` part, a workbook styles relationship, and one shared styled cell format.
- All emitted Points.xlsx value cells now use that style, which sets Calibri font and applies left horizontal alignment plus top vertical alignment for both text and numeric cells.
- Verification:
  - `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors using a workspace-local `DOTNET_CLI_HOME`.


# Follow-up (Points.xlsx Latest Photo Date Column, 2026-03-10)

- [x] Confirm the current Points row builder leaves column `C` blank.
- [x] Populate column `C` with the latest photo date across the export while keeping column `B` as the earliest photo date.
- [x] Build `wls_program/src/WildlifeSweeps/WildlifeSweeps.csproj` in Release and record verification.

## Review (Points.xlsx Latest Photo Date Column, 2026-03-10)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs` so the Points workbook now computes both the earliest and latest photo date across the export and writes the latest date into column `C` for every row.
- Column `B` remains the earliest photo date. When there is only one photo date in the export, columns `B` and `C` will naturally be the same value.
- Verification:
  - `dotnet msbuild wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v minimal` succeeded.
  - A full `dotnet build` retry was blocked only because `bin\Release\net8.0-windows\WildlifeSweeps.dll` was locked by another process.


# Follow-up (Points.xlsx Site Label + Meridian Display, 2026-03-10)

- [x] Confirm the current Points row builder still uses `Site-SITE` and raw meridian in `AI`.
- [x] Update column `S` to `Site -SITE` and emit column `AI` as `5 -5` style text.
- [x] Verify `WildlifeSweeps` still compiles and record the correction.

## Review (Points.xlsx Site Label + Meridian Display, 2026-03-10)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs` so column `S` now exports `Site -SITE` with the missing space.
- Updated column `AI` to use the `5 -5` display format again via `FormatMeridianCsvValue(...)`. Because Excel cannot treat `5 -5` as a true numeric value and still display it exactly that way, `AI` is no longer included in the numeric-cell column set.
- Verification:
  - `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors using a workspace-local `DOTNET_CLI_HOME`.


# Follow-up (Points.xlsx GS Leading Number Cleanup, 2026-03-10)

- [x] Confirm column `GS` is using the cleaned finding text and identify why leading numbers can still survive there.
- [x] Sanitize the exported `GS` value so leading list/count numbers are removed while leaving `GT` unchanged.
- [x] Verify `WildlifeSweeps` builds and record the correction.

## Review (Points.xlsx GS Leading Number Cleanup, 2026-03-10)

- Confirmed `GS` was being written from the cleaned finding text (`row.FindingNormalized`) in `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`.
- Updated the Points export to pass `GS` through a small sanitizer that strips leading list/count tokens like `1`, `#2`, or `3)` before writing the workbook cell. `GT` still writes the original finding text unchanged.
- Verification:
  - `dotnet msbuild wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v minimal` succeeded.
  - A full `dotnet build` retry was blocked only because `bin\Release\net8.0-windows\WildlifeSweeps.dll` was locked by another process.


# Follow-up (Snowshoe Hare Scat Description, 2026-03-10)

- [x] Confirm the runtime canonicalization and lookup workbook still resolve Snowshoe hare scat to a `...Droppings` description.
- [x] Update the canonical description and workbook lookup rows so Snowshoe hare scat exports as `Snowshoe Hare Scat`.
- [x] Verify `WildlifeSweeps` builds and record the correction.

## Review (Snowshoe Hare Scat Description, 2026-03-10)

- Updated `wls_program/src/WildlifeSweeps/FindingsDescriptionStandardizer.cs` so both legacy rabbit-source descriptions and the old `Snowshoe Hare Scat / Droppings` description canonicalize forward to `Snowshoe Hare Scat`.
- Updated `wls_program/wildlife_parsing_codex_lookup.xlsx` so the Snowshoe hare scat keyword/regex rows now store `Snowshoe Hare Scat` instead of `Snowshoe Hare Scat / Droppings`.
- Verification:
  - `dotnet msbuild wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v minimal` succeeded.
  - A full `dotnet build` retry was blocked only because `bin\Release\net8.0-windows\WildlifeSweeps.dll` was locked by another process.


# Follow-up (Points.xlsx Table Alignment + Woodpecker Rule, 2026-03-10)

- [x] Confirm workbook `GS` is still sourced from cleaned finding text instead of the table-standardized value.
- [x] Update the Points export so `GS` uses the same finding text as the inserted table.
- [x] Remove the unexpected `Pileated Woodpecker Feeding Cavity` remap path and preserve that phrase as-is.
- [x] Verify `WildlifeSweeps` compiles and record the correction.

## Review (Points.xlsx Table Alignment + Woodpecker Rule, 2026-03-10)

- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs` so Points workbook column `GS` now writes the same `record.WildlifeFinding` value used in the inserted table instead of the separate cleaned-finding export path.
- Removed the old now-dead `GS` normalization/sanitizer path from `CompleteFromPhotosService.cs` so the workbook and table no longer diverge by source.
- Updated `wls_program/wildlife_parsing_codex_lookup.xlsx` so the generic woodpecker cavity regex now requires `cavity tree` semantics, and added an exact preserve regex for `Pileated Woodpecker Feeding Cavity` so that phrase stays literal instead of collapsing to `Woodpecker Cavity Tree`.
- Verification:
  - `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors.

# Follow-up (Exact Finding Preserve Rules, 2026-03-10)

- [x] Confirm the active generic lookup rules are remapping user-approved exact phrases into broader wildlife buckets.
- [x] Add workbook preserve rules so the listed phrases stay as cleaned original output, including numbered-prefix variants like `2_...`.
- [x] Verify `WildlifeSweeps` still builds and record the correction.

## Review (Exact Finding Preserve Rules, 2026-03-10)

- Updated `wls_program/src/WildlifeSweeps/FindingsDescriptionStandardizer.cs` so a workbook rule can now emit `[Keep Original]`, which resolves to the cleaned original finding text at runtime instead of forcing a generic standardized label.
- Updated `wls_program/wildlife_parsing_codex_lookup.xlsx` with high-priority preserve regex rules for `Pileated Woodpecker Feeding Cavity`, `Moose Browsing Activity`, `Moose Hair`, `Inactive/Nactive Small Mammal Den`, and `Red Squirrel Midden`, including optional leading number prefixes like `2_...`.
- This preserves exact accepted phrases while leaving the broader woodpecker/moose/squirrel/den rules available for other findings.
- Verification:
  - `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors.

# Follow-up (Additional Exact Finding Overrides, 2026-03-10)

- [x] Confirm the newly reported phrases are still being remapped by broader lookup rules.
- [x] Add targeted exact overrides so accepted phrases stay unchanged and `Field mouse hole (mouse visual)` maps to `Small Rodent Sighting`.
- [x] Verify `WildlifeSweeps` still builds and record the correction.

## Review (Additional Exact Finding Overrides, 2026-03-10)

- Confirmed the stock workbook was still remapping the reported phrases through broader coyote/den/squirrel/mouse rules, while saved custom mappings were not the source for these cases.
- Updated `wls_program/wildlife_parsing_codex_lookup.xlsx` with priority-`1` regex overrides so `Coyote Tracks`, `Inactive Mammal Den`, and `Red Squirrel Burrow` now preserve the cleaned original phrase, including optional leading-number variants.
- Added a priority-`1` exact regex override so `Field mouse hole (mouse visual)` resolves to `Small Rodent Sighting` with the `Small Mammal (Unidentified)` / `Sighting` pair.
- Verification:
  - `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors.

# Follow-up (Leading Number Prefix Cleanup, 2026-03-10)

- [x] Confirm why leading prefixes like `2_` are surviving into cleaned-original finding text.
- [x] Update shared finding preprocessing so leading numbered prefixes like `2_` and `#2_` are removed before cleaned titles are built.
- [x] Verify `WildlifeSweeps` still builds and record the correction.

## Review (Leading Number Prefix Cleanup, 2026-03-10)

- Updated `wls_program/src/WildlifeSweeps/FindingsDescriptionStandardizer.cs` so the shared leading-prefix regex now strips underscore-attached and hash-prefixed number tags like `2_`, `#2_`, and `2 ` before cleaned-original titles are built.
- This fix applies across the standardization pipeline, so exact preserve outputs no longer keep those prefixes in the final title.
- Verification:
  - A regex sanity check confirmed `2_Pileated Woodpecker Feeding Cavity`, `#2_Pileated Woodpecker Feeding Cavity`, and `2 Pileated Woodpecker Feeding Cavity` all reduce to `Pileated Woodpecker Feeding Cavity` before further cleaning.
  - `dotnet msbuild wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v minimal` succeeded.
  - A full `dotnet build` retry was blocked only because `bin\Release\net8.0-windows\WildlifeSweeps.dll` was locked by another process.

# Follow-up (Default Findings Lookup Workbook, 2026-03-13)

- [x] Remove the configurable findings lookup workbook field from the WLS palette.
- [x] Stop storing/passing a user-configured lookup path so WLS always uses the bundled default workbook.
- [x] Update lookup warnings to match the new always-default behavior.
- [x] Verify `WildlifeSweeps` still builds and record the correction.

## Review (Default Findings Lookup Workbook, 2026-03-13)

- Updated `wls_program/src/WildlifeSweeps/Ui/PaletteControl.cs` to remove the `Findings lookup workbook` textbox, its settings writeback, tooltip, and the now-empty settings group from the palette.
- Updated `wls_program/src/WildlifeSweeps/PluginSettings.cs` to remove the unused `FindingsLookupPath` property so future settings saves stop carrying that field.
- Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs` to construct the findings standardizer with `null`, which forces the built-in workbook resolution path every time.
- Updated `wls_program/src/WildlifeSweeps/FindingsDescriptionStandardizer.cs` to replace the outdated ï¿½provide a valid path in settingsï¿½ warning with a default-workbook message that matches the new behavior.
- Verification:
  - `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors.

# Follow-up (Dual Findings Lookup Workbook Copies, 2026-03-13)

- [x] Inspect how the findings lookup workbook is stored, copied into build output, and updated at runtime.
- [x] Implement a two-copy workbook strategy and make workbook updates write to both copies together.
- [x] Verify the build and record the change in tasks files and lessons.

## Review (Dual Findings Lookup Workbook Copies, 2026-03-13)

- Added a second repo copy at `wls_program/wildlife_parsing_codex_lookup_backup.xlsx` so the project now carries both a primary and backup findings lookup workbook.
- Updated `wls_program/src/WildlifeSweeps/WildlifeSweeps.csproj` so both workbook files are copied into the plugin output on build.
- Updated `wls_program/src/WildlifeSweeps/FindingsDescriptionStandardizer.cs` so default lookup resolution can load either copy, creates the missing mirror if one is absent, and syncs keyword writebacks across both copies instead of updating only one file.
- Verification:
  - `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors.
  - Verified both `wildlife_parsing_codex_lookup.xlsx` and `wildlife_parsing_codex_lookup_backup.xlsx` exist in `wls_program\src\WildlifeSweeps\bin\Release\net8.0-windows` after the build.
# Follow-up (ATS Builder Session Runner Refactor, 2026-03-13)

- [x] Review the current `ATSBUILD` / `ATSBUILD_XLS` execution path and isolate a low-risk refactor seam around the shared runner.
- [x] Extract a staged ATS build session runner/context so the shared execution path is easier to follow without changing drawing behavior.
- [x] Build ATS Builder, run the decision tests, and record retest instructions for in-drawing validation.

## Review (ATS Builder Session Runner Refactor, 2026-03-13)

- Extracted the shared `ExecuteAtsBuildFromInput` flow into a small staged runner in `src/AtsBackgroundBuilder/Core/Plugin.Core.AtsBuildSession.cs`, with an `AtsBuildSessionContext` that carries command/session state through the existing ATS phases.
- Kept the underlying ATS drawing, disposition processing, and post-quarter pipeline logic intact; the refactor only reorganizes orchestration, validation exits, and summary logging around those existing calls.
- Simplified `src/AtsBackgroundBuilder/Core/Plugin.Core.AtsBuildXls.cs` so the shared runner now handles session logging, staged execution, error reporting, and logger disposal in one place.
- Verification:
  - `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors.
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release` reported `Decision tests passed.`
  - Manual AutoCAD retest remains recommended for `ATSBUILD` and `ATSBUILD_XLS` because this refactor touches command orchestration around drawing-time flows.

# Follow-up (ATS Import Sysvar Warning Cleanup, 2026-03-13)

- [x] Trace the recurring `MAPUSEMPOLYGON` / `POLYDISPLAY` warnings to the shapefile import helper.
- [x] Downgrade known harmless `eInvalidInput` writes for those two optimization sysvars so they no longer log as import failures every run.
- [x] Verify ATS Builder still compiles and decision tests still pass, then note the runtime retest expectation.

## Review (ATS Import Sysvar Warning Cleanup, 2026-03-13)

- Updated `src/AtsBackgroundBuilder/Dispositions/ShapefileImporter.cs` so `MAPUSEMPOLYGON` and `POLYDISPLAY` write failures with AutoCAD `eInvalidInput` are treated as unsupported optimization writes, not import failures.
- Added a once-per-variable log gate so the importer writes one explanatory message instead of repeating the same warning on every shapefile import pass.
- Verification:
  - `dotnet msbuild src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -p:NuGetAudit=false -v minimal` succeeded.
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` reported `Decision tests passed.`
  - A full `dotnet build` was blocked only because `src/AtsBackgroundBuilder/bin/Release/net8.0-windows/AtsBackgroundBuilder.dll` is currently locked by AutoCAD.

# Follow-up (ATS Road Allowance Cleanup Pipeline Refactor, 2026-03-13)

- [x] Review the current road-allowance cleanup orchestration inside `DrawSectionsFromRequests` and identify a safe extraction seam.
- [x] Extract the cleanup pass order into a structured pipeline/context without changing geometry behavior.
- [x] Build ATS Builder, run decision tests, and document the runtime retest path.

## Review (ATS Road Allowance Cleanup Pipeline Refactor, 2026-03-13)

- Extracted the long road-allowance cleanup/orchestration block from `src/AtsBackgroundBuilder/Core/Plugin.cs` into a dedicated cleanup pipeline partial at `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.CleanupPipeline.cs`.
- Added `RoadAllowanceCleanupContext` plus named phase helpers for generation, canonical cleanup, trim/re-layer, endpoint cleanup, and final post-restore cleanup, while preserving the existing geometry pass order and underlying cleanup/enforcement calls.
- Left `DrawSectionsFromRequests` responsible for setup and final result assembly, but moved the high-risk cleanup sequencing out of the middle of the method so the build path is easier to review and extend.
- Verification:
  - `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors.
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` reported `Decision tests passed.`
  - A manual AutoCAD retest is still recommended because this refactor touches the ordered road-allowance cleanup path inside ATS section generation.
# Follow-up (ATS Correction Seam Spike Guard, 2026-03-13)

- [x] Trace the `63-12-6` correction-line regression against the extracted cleanup pipeline and runtime log.
- [x] Tighten correction post-processing so C-0-to-vertical seam snaps stay local instead of creating long extension spikes.
- [x] Build ATS Builder, rerun decision tests, and record the focused retest path.

## Review (ATS Correction Seam Spike Guard, 2026-03-13)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.CorrectionLinePostProcessing.cs` so `ConnectCorrectionInnerEndpointsToVerticalUsecBoundaries(...)` no longer allows 500m+ seam-facing pulls when selecting or extending vertical 0/20 targets for correction inner (`L-USEC-C-0`) lines.
- Replaced the broad `520m` vertical target gap / endpoint move caps with a local correction-span cap (`CorrectionLinePostExpectedUsecWidthMeters * 6.0`), which is intended to stop the long south spikes reported below sections 1 and 2 in `63-12-6` while preserving nearby seam cleanup.
- Verification:
  - `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -p:BaseOutputPath=.artifacts/ats_regression_20260313_1/ -v minimal` succeeded with `0` warnings and `0` errors.
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` reported `Decision tests passed.`# Follow-up (ATS SE Correction South-Band Fallback, 2026-03-13)

- [x] Trace why the SE correction connector reported `withH=0` on `63-12-6` even though correction seam geometry existed in the build.
- [x] Update the SE south-band connector so it prefers the normal `20.11` band and only falls back to the `10.06` correction-pair band when the primary band is absent.
- [x] Build ATS Builder, rerun decision tests, and record the focused AutoCAD retest path.

## Review (ATS SE Correction South-Band Fallback, 2026-03-13)

- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.QuarterExtensionsConnectivity.cs` so `ConnectUsecSeSouthTwentyTwelveLinesToEastOriginalBoundary(...)` no longer looks only at the `10.06` south band (`CorrectionLinePairGapMeters`) when resolving SE correction connectors.
- The SE pass now mirrors the safer SW pattern: try the primary `20.11` south band first and fall back to `10.06` only if that band is absent for the local section window.
- This is intended to restore missing section-5/6 correction east extensions and prevent the later generic 0/20 cleanup from inventing long `L-USEC2012` south spikes when the dedicated SE connector never claimed the correct target.
- Verification:
  - `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -v minimal` succeeded with `0` warnings and `0` errors.
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` reported `Decision tests passed.`

## ATS SE Bounded Extension Bridge, 2026-03-13
- [x] Check the newest 63-12-6 ATSBUILD log and confirm the active failure path.
- [x] Run the Python DXF classifier before the next delivery to anchor the fix to the known correction-line mismatch classes.
- [x] Patch the SE south 20.11 bridge so bounded apparent joins are allowed when the horizontal stops short of the selected east boundary by a local correction-width amount.
- [x] Rebuild ATS and rerun decision tests.

### Review
- The latest log still showed Cleanup: SE L-USEC south 20.11 connect found no candidates (sections=32, withBoundary=24, withH=0, candidates=0) on the newest 63-12-6 run, so the previous offset-band tweak was not enough.
- Updated src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.QuarterExtensionsConnectivity.cs so ConnectUsecSeSouthTwentyTwelveLinesToEastOriginalBoundary(...) no longer rejects bounded apparent joins just because the target point is off the current horizontal segment by about one 20.11m road-allowance width.
- Verification:
  - dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -v minimal
  - dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build
  - python "src/AtsBackgroundBuilder/REFERENCE ONLY/dxf_layer_diff.py" "src/AtsBackgroundBuilder/REFERENCE ONLY/actual_bad.dxf" "src/AtsBackgroundBuilder/REFERENCE ONLY/expected_clean.dxf"
  - inline Python sanity check confirmed the old strict path rejects a 20.11m shortfall while the new bounded-extension rule accepts it.

## ATS SE Cleanup Protection, 2026-03-13
- [x] Re-check the newest `63-12-6` ATSBUILD log and confirm whether the SE bridge is now succeeding before later cleanup runs.
- [x] Carry the SE-corrected boundary IDs through the road-allowance cleanup context and skip those IDs in downstream generic `0/20` cleanup passes.
- [x] Rebuild ATS, rerun decision tests, rerun the Python verification, and capture the retest signal to look for in AutoCAD/logs.

### Review
- Re-checked the newest `63-12-6` run in `src/AtsBackgroundBuilder/bin/Release/net8.0-windows/AtsBackgroundBuilder.log`: the SE bridge is now active (`connected 5 SE L-USEC south 20.11...`), but the later generic `0/20 dangling endpoint connect` passes still moved large endpoint counts immediately afterward.
- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.QuarterExtensionsConnectivity.cs` so `ConnectUsecSeSouthTwentyTwelveLinesToEastOriginalBoundary(...)` returns the vertical boundary IDs it actually moved.
- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.CleanupPipeline.cs` so those SE-connected IDs are carried through the cleanup context, logged once, and skipped by downstream generic `0/20` cleanup passes (`ConnectDangling...`, overlap cleanup, no-crossing enforcement, pass-through trim, and endpoint-intersection overlap snap).
- Verification:
  - `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -p:BaseOutputPath=.artifacts/ats_se_cleanup_protection/ -v minimal`
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -p:NuGetAudit=false -p:BaseOutputPath=.artifacts/ats_se_cleanup_protection_tests/`
  - `python "src/AtsBackgroundBuilder/REFERENCE ONLY/dxf_layer_diff.py" "src/AtsBackgroundBuilder/REFERENCE ONLY/actual_bad.dxf" "src/AtsBackgroundBuilder/REFERENCE ONLY/expected_clean.dxf"`
  - inline Python source verification confirmed the SE-protection wiring is present in the updated ATS source.

## ATS Correction Seam Final Guard, 2026-03-13
- [x] Re-check the latest 63-12-6 regression against the current cleanup order and confirm whether the remaining failures are late-stage seam classification / post-correction trim gaps.
- [x] Broaden the final correction outer consistency pass so surveyed seam horizontals can still be promoted to correction where they overlap resolved correction outers.
- [x] Rerun the 100m trim after correction-line post-processing so late endpoint moves do not leave long buffer spikes ahead of the final LSD rebuild.
- [x] Rebuild ATS, rerun decision tests, and rerun a Python verification before delivery.

### Review
- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.CorrectionLinePostProcessing.cs` so the late correction outer promotion accepts overlapping surveyed seam horizontals (`L-SEC`, `L-SEC-0`, `L-SEC-2012`) in addition to USEC 0/20/30 bands.
- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.CleanupPipeline.cs` so ATS reruns the 100m trim after `ApplyCorrectionLinePostBuildRules(...)` and before the final quarter/LSD output passes, which should clip correction-line spikes that are introduced after the earlier trim stage.
- Verification:
  - `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -p:BaseOutputPath=.artifacts/ats_corr_regression_fix/ -v minimal`
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -p:NuGetAudit=false -p:BaseOutputPath=.artifacts/ats_corr_regression_fix_tests/`
  - `python "src/AtsBackgroundBuilder/REFERENCE ONLY/dxf_layer_diff.py" "src/AtsBackgroundBuilder/REFERENCE ONLY/actual_bad.dxf" "src/AtsBackgroundBuilder/REFERENCE ONLY/expected_clean.dxf"`
  - inline Python source verification confirmed the late retrim and surveyed-seam correction promotion are present in the ATS source.
- Follow-up: the latest runtime log proved the late correction pass was executing, but the wrong north seam was still coming through as plain `L-USEC`, so the final correction promotion now includes base `L-USEC` as well as surveyed/usec offset variants.
- Follow-up: the late post-correction retrim originally skipped every generated RA line that touched the requested core (`protectedSkip=126`), which meant it could not clip the visible south spikes at all; the late retrim now runs on generated RA without the core-skip guard.
- Verification:
  - `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -v minimal`
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -p:NuGetAudit=false -p:BaseOutputPath=.artifacts/ats_corr_regression_fix_tests2/`
  - inline Python source verification confirmed the late correction promotion now includes `L-USEC`/`LayerUsecBase` and that the post-correction generated retrim is unprotected.

## ATS Correction Companion Consistency, 2026-03-13
- [x] Re-check the latest 63-12-6 log and confirm whether the remaining correction seam failure is a missing companion-band classification problem rather than another 100m trim regression.
- [x] Update the final correction consistency pass so surviving ordinary seam horizontals can be promoted from either direct correction-band overlap or the expected 5.02m companion offset from an existing correction band.
- [x] Rebuild ATS, rerun decision tests, and rerun a Python sanity check before delivery.

### Review
- Re-checked the newest 63-12-6 quarter diagnostics in src/AtsBackgroundBuilder/bin/Release/net8.0-windows/AtsBackgroundBuilder.log: southSource=L-USEC-C-0 was already present on the bad seam, while the opposite boundary was still coming through as ordinary L-USEC / L-USEC-0, which pointed at a missing companion-band relayer rather than another missing trim.
- Updated src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.CorrectionLinePostProcessing.cs so EnforceFinalCorrectionOuterLayerConsistency(...) now seeds from both L-USEC-C and L-USEC-C-0, promotes ordinary horizontals that sit directly on either correction band, and also promotes the missing companion band when an ordinary seam horizontal sits at the expected 5.02m offset from an existing correction band.
- Added an unsuppressed cleanup summary log for that final correction consistency pass so future retests show how many outer/inner seam segments were promoted.
- Verification:
  - dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -v minimal
  - dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -p:NuGetAudit=false -p:BaseOutputPath=.artifacts/ats_correction_seam_tests/
  - inline Python source verification confirmed the final correction pass now includes both direct correction-band matching and 5.02m companion-band promotion.
  - inline Python geometry sanity check confirmed the companion-band rule accepts an ordinary horizontal whose span overlaps an inner correction band at the expected 5.02m offset.

## ATS Correction South Owner-Band Suppression, 2026-03-14
- [x] Re-check the newest runtime log and confirm whether the final correction consistency pass fired but left the visible seam unchanged.
- [x] Add a targeted cleanup pass to suppress ordinary south L-USEC-0 / L-USEC2012 owner bands that still survive one standard RA width below resolved correction seams.
- [x] Rebuild ATS, rerun decision tests, and rerun Python sanity checks before delivery.

### Review
- Re-checked the newest runtime log and confirmed the prior final correction pass did execute (Cleanup: final correction layer consistency outerAnchors=20, innerAnchors=27, outerConverted=4, innerConverted=0.), but the same correction seam still rendered wrong immediately afterward.
- That shifted the root cause from “late seam-band relayer did not run” to “ordinary south owner bands still survive below the correction seam,” which lines up with the earlier ~30m screenshot measurements and the persisted southSource=L-USEC-C-0 diagnostics.
- Updated src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.CorrectionLinePostProcessing.cs so ApplyCorrectionLinePostBuildRules(...) now calls SuppressStandardSouthOwnerBandsBelowCorrectionSeams(...) after the C-0 endpoint cleanup, removing surviving horizontal L-USEC-0 / L-USEC2012 owner bands that overlap a correction seam span and sit at the normal south-owner offsets below that seam.
- Verification:
  - dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -v minimal
  - dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -p:NuGetAudit=false -p:BaseOutputPath=.artifacts/ats_correction_seam_tests2/
  - inline Python source verification confirmed the new south-band suppression helper and call are present.
## ATS Correction Seam Survivor Cleanup, 2026-03-14
- [ ] Compare current correction seam behavior against reference-only bad/good screenshots and DXF root-cause reports.
- [ ] Broaden correction-seam cleanup so ordinary seam-adjacent horizontal survivors do not remain on L-USEC / L-USEC-0 around resolved correction seams.
- [ ] Verify with ATS release build, decision tests, and Python-side sanity/diff checks before reporting back.
## ATS Correction Seam Survivor Cleanup, 2026-03-14 Review
- [x] Compared current correction seam behavior against reference-only bad/good screenshots and DXF root-cause reports.
- [x] Broadened correction-seam cleanup to suppress ordinary seam-adjacent horizontal survivors using the actual north fallback / south owner offset map.
- [x] Verified with ATS release build, decision tests, and Python-side seam sanity checks.
- [x] Added a seam-driven correction relayer so seam-overlap horizontals are classified to L-USEC-C / L-USEC-C-0 from resolved correction seam positions before survivor cleanup runs.
- [x] Rebuilt ATS, reran decision tests, and reran Python direct-classification sanity checks.

## ATS Correction Seam Exact Geometry, 2026-03-14
- [x] Replace correction seam band fallback selection with exact seam-derived outer/inner generation.
- [x] Remove late seam relayer fallback calls from correction post-processing.
- [x] Remove ordinary horizontal survivors inside the correction seam band after exact correction generation.
- [x] Verify with Release build, decision tests, and Python source sanity.

## ATS Correction Seam Exact Geometry, 2026-03-14 Review
- Root cause: the correction-line post pass was relayering nearby ordinary seam-band geometry when strict candidates were missing instead of generating the exact correction outer/inner bands from the fitted seam itself.
- Result: correction seam geometry is now created from seam targets directly, with no one-sided band fallback in the main path.

## ATS Correction Width Selection Reset, 2026-03-14
- [x] Inspect the ATS correction-line path and confirm whether correction relayering is driven by measured road-allowance width or by seam/layer heuristics.
- [x] Replace the broad seam-band selector with a single measured correction-axis bucket per seam side so we stop relayering every near-band horizontal.
- [x] Normalize any `L-USEC-C-0` segment that still sits directly on the resolved outer correction axis back to `L-USEC-C`.
- [x] Verify with ATS Release build, decision tests, and a Python source sanity check before handing it back.

### Review
- Root-cause finding: ATS does measure perpendicular road-allowance gaps in section inference and some cleanup/connectivity passes, but the correction post-build selector was still mostly choosing correction bands from seam/evidence proximity rather than a single measured seam-axis decision.
- Updated `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.CorrectionLinePostProcessing.cs` so `SelectCorrectionHorizontalBand(...)` now chooses one closest axis bucket per seam side instead of relayering every candidate within the seam band.
- Added `NormalizeCorrectionZeroSegmentsOnOuterAxis(...)` so any `L-USEC-C-0` segment that still lands directly on the resolved outer correction axis is relayered back to `L-USEC-C`; this targets the exact failure mode where quarter view was selecting a seam-boundary line as the correction inner.
- Verification:
  - `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -p:BaseOutputPath=.artifacts/ats_width_root_fix_build/ -v minimal`
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -p:NuGetAudit=false -p:BaseOutputPath=.artifacts/ats_width_root_fix_tests/`
  - inline Python source sanity check for the bucketed selector and misaligned-`C-0` normalization helper

## ATS Correction Chain Reset, 2026-03-14
- [x] Roll `Plugin.RoadAllowance.CorrectionLinePostProcessing.cs` back to the simpler pre-experiment correction-line baseline instead of continuing the exact-geometry / seam-axis edits.
- [x] Keep only the narrow local cap that prevents distant vertical target pulls from recreating the long south spikes.
- [x] Broaden the final correction-chain promotion so plain `L-USEC` / `L-SEC*` seam segments can be promoted to correction when they are collinear/connected with the resolved correction outer chain.
- [x] Verify with ATS Release build, decision tests, and a Python source sanity check.

### Review
- Reset `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.CorrectionLinePostProcessing.cs` back to the simpler baseline from `HEAD`, preserving only the local `maxVerticalTargetGap` / `maxVerticalEndpointMove` cap that was keeping the old south spikes local.
- Root-cause correction focus after reset: the baseline final correction-chain pass was only promoting `0/20/30`-style seam layers, so a bad seam segment left on plain `L-USEC` would stay regular even when it was collinear/connected with the correction chain.
- Updated `EnforceFinalCorrectionOuterLayerConsistency(...)` so that same connected-chain promotion now also accepts plain `L-USEC` and `L-SEC*` seam candidates.
- Verification:
  - `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release --no-restore -p:NuGetAudit=false -p:BaseOutputPath=.artifacts/ats_correction_chain_reset_build/ -v minimal`
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -p:NuGetAudit=false -p:BaseOutputPath=.artifacts/ats_correction_chain_reset_tests/`
  - inline Python source sanity confirming the rollback and the broadened final correction-chain promotion

## Repo Build Verification, 2026-03-14
- [x] Confirm the repo build entry points and choose the build commands.
- [x] Build the ATS solution in Release.
- [x] Build the WLS solution in Release.
- [x] Record the build results and any follow-up blockers.

### Review
- Confirmed the repo has two solution entry points to build from the root: `src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln` and `wls_program/src/WildlifeSweeps/WildlifeSweeps.sln`.
- Built ATS in Release with the local SDK: `.\.local_dotnet\dotnet.exe build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal`.
- ATS build succeeded and produced `src/AtsBackgroundBuilder/bin/x64/Release/net8.0-windows/AtsBackgroundBuilder.dll`; the build reported 16 nullable warnings (`CS8602`) and 0 errors.
- Built WLS in Release with the local SDK: `.\.local_dotnet\dotnet.exe build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.sln -c Release -p:NuGetAudit=false -v minimal`.
- WLS build succeeded and produced `wls_program/src/WildlifeSweeps/bin/Release/net8.0-windows/WildlifeSweeps.dll` with 0 warnings and 0 errors.
- Follow-up blocker: none for the build itself; the only notable issue is the existing ATS nullable-warning set.

## ATS Correction Classification Perpendicular Gap Fix, 2026-03-14
- [ ] Confirm the current correction-line root cause and capture the exact geometry path to change.
- [ ] Replace section-type gap inference with perpendicular measurement between facing section edges.
- [ ] Add a regression test for angled road-allowance gap measurement.
- [ ] Build ATS and run decision tests.
- [ ] Record the review and any remaining follow-up.
## ATS Correction Classification Perpendicular Gap Fix, 2026-03-14 Review
- [x] Confirmed the likely root cause: full-section AUTO type inference was still measuring east/south road-allowance gaps from projected section centers and half-widths, not the actual perpendicular gap between facing section edges.
- [x] Updated `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionTypeInference.cs` to measure facing east/west and north/south section-edge gaps with a perpendicular sample path before falling back to the older box-gap logic.
- [x] Extended `SectionSpatialInfo` / section spatial analysis to retain the actual section corner points needed for facing-edge measurement.
- [x] Added a pure helper `src/AtsBackgroundBuilder/Core/PerpendicularGapMeasurement.cs` plus regression coverage in `src/AtsBackgroundBuilder.DecisionTests/Program.cs` for angled parallel edges and reversed edge direction.
- [x] Verification:
  - `dotnet run --project src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release -p:NuGetAudit=false` -> `Decision tests passed.`
  - `dotnet msbuild src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj /t:Compile /p:Configuration=Release /p:NuGetAudit=false /v:minimal` -> compile succeeded; existing nullable warnings remain.
  - Full `dotnet build` still cannot complete the final copy step while AutoCAD is holding `build/net8.0-windows/AtsBackgroundBuilder.dll` open.
- [x] Follow-up: retest `63-12-6` after reloading the freshly compiled DLL; if the exact segment still misclassifies, the next place to instrument is the correction post seam-band selector, but the section-width inference path is now using perpendicular geometry instead of projected box widths.

## ATS Correction Classification Path Trace, 2026-03-14
- [ ] Confirm which runtime classifier actually owns the misclassified `63-12-6` segment after the fresh DLL reload.
- [ ] Inspect the latest ATS runtime log / diagnostics for the seam containing `325614.237,6032915.344 -> 324823.466,6032945.842`.
- [ ] Patch the actual classification path with targeted diagnostics or logic changes.
- [ ] Rebuild and verify the result.

## ATS Correction Classification Perpendicular Seam Distance Fix, 2026-03-14
- [x] Trace the reported `63-12-6` correction-seam issue to the classifier that actually assigns correction layers.
- [x] Replace world-Y seam-band / inset checks in correction post-processing with perpendicular signed-distance checks against the fitted seam lines.
- [x] Point the targeted layer-trace hook at the reported bad segment `325614.237,6032915.344 -> 324823.466,6032945.842` for the next runtime log.
- [x] Add a pure regression test for angled signed line distance and rebuild ATS.

### Review
- Root cause: `Plugin.RoadAllowance.CorrectionLinePostProcessing.cs` was still classifying seam-band correction candidates by comparing `midY` to `seam.GetSouthYAt(midX)` / `GetNorthYAt(midX)`, which is a vertical offset in world coordinates, not a perpendicular distance to the fitted correction seam.
- Fix: correction seam band selection, surveyed-evidence checks, late post-relayer promotion, and inner-companion pairing/creation now use perpendicular signed distance to the north/south/center seam fits via `PerpendicularLineDistanceMeasurement`.
- Diagnostics: the existing `LAYER-TARGET` trace is now aimed at the user-reported bad segment so the next ATS runtime log will show exactly which cleanup passes touch it if anything still survives.
- Verification:
  - `./.local_dotnet/dotnet.exe run --project ./src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release -p:NuGetAudit=false`
  - `./.local_dotnet/dotnet.exe build ./src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal`
  - Result: decision tests passed; ATS Release build succeeded with the same 16 existing nullable warnings and 0 errors.

## ATS Correction Classification Exact Segment Trace, 2026-03-14
- [ ] Inspect the latest runtime log for the exact user-reported segment after the unchanged rerun.
- [ ] Prove which pass last touches or skips that segment.
- [ ] Patch the owning classifier/survivor path only.
- [ ] Rebuild ATS and record the result.

## ATS Correction Classification Perpendicular Seam Strip Fix, 2026-03-14
- [x] Inspect the exact-segment runtime trace and confirm whether the bad line survives as ordinary `L-USEC-0`.
- [x] Patch upstream correction seam candidate gates so angled segments are admitted by perpendicular strip intersection instead of world-axis `Y` windows / midpoint checks.
- [x] Fix the late-post seam claim ordering so a segment is only marked seen after it actually qualifies for a seam.
- [x] Add focused pure geometry regression coverage and rebuild ATS.

### Review
- Runtime trace confirmed the user-reported segment `325614.237,6032915.344 -> 324823.466,6032945.842` was surviving the cleanup pipeline as ordinary `L-USEC-0`, so the previous seam-band relayer fix was not enough on its own.
- Root cause refinement: `Plugin.RoadAllowance.CorrectionLinePostProcessing.cs` still had upstream world-axis / midpoint-only gates (`IntersectsAnyCorrectionSeamWindow`, seam-band candidate filters, and late-post band admission) that could exclude or skip long angled seam segments before the perpendicular relayer logic ever ran.
- Fix: correction seam admission now uses endpoint-based perpendicular strip intersection through `PerpendicularLineBandMeasurement`, the target-segment trace now logs signed-distance ranges across the whole segment, and the late post-relayer only marks a segment as seen after it has actually qualified for the current seam.
- Added decision-test coverage for angled strip intersection and outside-strip rejection alongside the earlier perpendicular gap / signed-distance tests.
- Verification:
  - `./.local_dotnet/dotnet.exe run --project ./src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release -p:NuGetAudit=false` -> `Decision tests passed.`
  - `./.local_dotnet/dotnet.exe build ./src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` -> compilation succeeded and produced `src/AtsBackgroundBuilder/bin/x64/Release/net8.0-windows/AtsBackgroundBuilder.dll`; final copy into `build/net8.0-windows` still failed only because AutoCAD is locking the destination DLL.
- Follow-up: reload the freshly compiled DLL and rerun `63-12-6`; the new `LAYER-TARGET corr` lines now include seam distance ranges, so if the exact line still survives we will know whether it never intersects the seam strip, intersects but is not considered a fallback layer, or qualifies and still fails to relayer.

## ATS Correction Seam LSD / 100m Buffer Follow-up, 2026-03-14
- [x] Trace the post-fix regression affecting LSD endpoints and east-end 100m buffer cleanup around the correction seam.
- [x] Tighten LSD `CORRZERO` endpoint targeting so correction-zero handoff stays on the quarter-interior side of the seam.
- [x] Rerun the final 100m trim after late correction-outer consistency promotions when that pass changes geometry.
- [x] Rebuild ATS and rerun the decision tests.

### Review
- Root cause split: the earlier seam-layer fix worked, but two downstream passes could still produce the new regression.
- LSD root cause: `Plugin.RoadAllowance.EndpointEnforcement.cs` treated nearby correction-zero horizontals as interchangeable whenever an outer vertical LSD endpoint was within the generic `CORRZERO` override radius, so adjacent LSDs could snap to different correction-zero owners across an angled seam.
- Cleanup root cause: `EnforceFinalCorrectionOuterLayerConsistency(...)` runs after the normal final 100 m trim, so any ordinary segments promoted to correction outer at that stage could survive without being re-trimmed on the east end.
- Fix: added a quarter-aware `TryFindQuarterInteriorCorrectionZeroTarget(...)` path that prefers correction-zero targets on the interior side of the top/bottom quarter boundary before falling back to the generic midpoint selector, and rerun the final 100 m trim whenever the late correction-outer consistency pass actually converts segments.
- Verification:
  - `./.local_dotnet/dotnet.exe run --project ./src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release -p:NuGetAudit=false` -> `Decision tests passed.`
  - `./.local_dotnet/dotnet.exe build ./src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` -> build succeeded and produced `src/AtsBackgroundBuilder/bin/x64/Release/net8.0-windows/AtsBackgroundBuilder.dll` with the same 16 existing nullable warnings and 0 errors.
- Follow-up: reload the freshly built DLL and rerun `63-12-6`; the LSD endpoint trace will now report `source=corrzero-interior` when the quarter-aware correction-zero target wins, which should make the remaining handoff behavior easy to confirm from the log if anything is still off.

## ATS Correction Seam Station-Target LSD / Late Correction Trim Fix, 2026-03-14
- [x] Prove whether the reported bad LSD endpoint is still being moved by the final hard-boundary pass.
- [x] Replace midpoint-based boundary endpoint targeting with a station-preserving point on the chosen boundary segment.
- [x] Include late correction-layer segments in the final 100m trim after correction post-processing / final correction consistency.
- [x] Re-run decision tests and ATS Release build.

### Review
- Runtime trace proved the user-reported LSD endpoint `326835.758,6032873.575` was still being moved in the final `LSD-ENDPT` rule-matrix pass with `source=midpoint-kind`, so the last mover was targeting the midpoint of a long `CORRZERO` segment rather than the point on that boundary at the LSD station.
- Root cause 1: `Plugin.RoadAllowance.EndpointEnforcement.cs` used segment midpoints in both the generic boundary-kind selector and the quarter-interior `CORRZERO` override, so long angled or over-extended correction-zero horizontals could drag vertical LSD endpoints south even when the correct local boundary line existed.
- Root cause 2: late correction entities were still outside the normal final trim sets; post-built / post-promoted `L-USEC-C` and `L-USEC-C-0` segments could survive beyond the east-end 100m window and make the midpoint-target bug worse.
- Fix: added `SegmentStationProjection` and changed LSD boundary targeting to resolve the point on the candidate boundary segment at the endpoint station (with a closest-point fallback only when the projected station is still within tolerance), renamed the runtime trace source to `station-kind`, and added a late correction-layer id collection so final 100m trim also clips correction-layer segments after correction post-processing and after late correction-outer consistency changes.
- Verification:
  - `./.local_dotnet/dotnet.exe run --project ./src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release -p:NuGetAudit=false` -> `Decision tests passed.`
  - `./.local_dotnet/dotnet.exe build ./src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` -> build succeeded and produced `src/AtsBackgroundBuilder/bin/x64/Release/net8.0-windows/AtsBackgroundBuilder.dll` with the same 16 existing nullable warnings and 0 errors.
- Follow-up: reload the fresh DLL and rerun `63-12-6`; the bad LSD should now target a local correction-zero point instead of the long-segment midpoint, and the east-end extra correction/buffer survivor should also be retrimmed if it is correction-layer geometry.

## ATS Correction Zero Outer-Axis Normalization, 2026-03-14
- [x] Trace the latest rerun and prove whether the bad LSD still follows a real correction-zero owner after the station-target fix.
- [x] Normalize any correction-zero horizontal that still sits on the seam outer axis back to correction outer before quarter/LSD selection.
- [x] Add pure regression coverage for inner-vs-outer correction-zero axis classification.
- [x] Re-run decision tests and ATS Release build.

### Review
- Fresh runtime evidence showed the bad sec 1 LSD no longer used a midpoint guess; it moved by `source=station-kind` onto a real `L-USEC-C-0` segment, and `VERIFY-QTR-SOUTHMID` still resolved the south boundary from `L-USEC-C-0`, so the remaining bug had shifted from endpoint targeting to correction-zero band semantics.
- Root cause: correction post-processing could leave overlapping outer-band horizontals on `L-USEC-C-0` after the main and late companion passes. Because quarter/LSD south-boundary resolution only reads `L-USEC-C-0` for correction south boundaries, those outer-axis survivors were treated as the valid south correction boundary and pulled LSD endpoints onto the south side of the road allowance.
- Fix: added `CorrectionBandAxisClassifier` and a new correction-post normalization pass that relayers any seam-overlapping horizontal `L-USEC-C-0` segment whose center distance is closer to the outer correction axis than the inner correction axis back to `L-USEC-C` before quarter/LSD selection runs.
- Verification:
  - `./.local_dotnet/dotnet.exe run --project ./src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release -p:NuGetAudit=false` -> `Decision tests passed.`
  - `./.local_dotnet/dotnet.exe build ./src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` -> build succeeded and produced `src/AtsBackgroundBuilder/bin/x64/Release/net8.0-windows/AtsBackgroundBuilder.dll` with the same 16 existing nullable warnings and 0 errors.
- Follow-up: rerun `63-12-6` and check for the new `CorrectionLine: normalized ... outer-axis correction-zero` log line; if the east-end survivor is still visible after this, the next trace will need the specific surviving entity/layer near the user’s east-end coordinates rather than another LSD endpoint guess.

## ATS Correction Zero Live-Snapshot Fix, 2026-03-14
- [x] Prove whether the outer-axis correction-zero normalization was inspecting stale pre-relayer segment state.
- [x] Change correction-zero normalization to rescan live model-space `L-USEC-C-0` horizontals immediately before normalization.
- [x] Re-run decision tests and ATS Release build.

### Review
- Fresh log evidence after the previous patch still showed `VERIFY-QTR-SOUTHMID` resolving sec 1 from `L-USEC-C-0` and the bad LSD moving by `source=station-kind`, but there was no `outer-axis correction-zero` normalization activity in the rerun. That meant the semantic fix existed in code but was not seeing the live survivors that quarter/LSD selection actually used.
- Root cause: the outer-axis normalization loop was iterating the earlier `segments` snapshot captured before later relayer / companion passes. By the time normalization ran, many real correction-zero survivors had been created or relayered after that snapshot, so the pass could silently miss the exact `L-USEC-C-0` entities that still owned the bad south boundary.
- Fix: changed `Plugin.RoadAllowance.CorrectionLinePostProcessing.cs` to rescan live model-space entities for horizontal `L-USEC-C-0` segments immediately before the normalization pass and added a runtime summary line reporting how many live C-0 horizontals were scanned, how many intersected seam candidates, and how many were relayered back to `L-USEC-C`.
- Verification:
  - `./.local_dotnet/dotnet.exe run --project ./src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release -p:NuGetAudit=false` -> `Decision tests passed.`
  - `./.local_dotnet/dotnet.exe build ./src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` -> build succeeded and produced `src/AtsBackgroundBuilder/bin/x64/Release/net8.0-windows/AtsBackgroundBuilder.dll` with the same 16 existing nullable warnings and 0 errors.
- Follow-up: rerun `63-12-6` and inspect the new `CorrectionLine: outer-axis correction-zero normalization scanned ...` line together with `VERIFY-QTR-SOUTHMID sec=1` and `LSD-ENDPT line=1264817`; that will confirm whether the live survivors are now being normalized before quarter/LSD ownership resolves.

## ATS Correction South-Crossing LSD Follow-Up, 2026-03-14
- [ ] Inspect the newest ATS runtime log for the exact sec 1 south-boundary owner, bad LSD endpoint move, and live correction-zero normalization counts.
- [ ] Patch the remaining owner-selection / cleanup path that still lets LSDs cross onto the south side of the road allowance.
- [ ] Re-run decision tests and ATS Release build, then capture the result and lesson.

## ATS Quarter Correction South-Boundary Preference Fix, 2026-03-14
- [x] Inspect the newest ATS runtime log for the exact sec 1 south-boundary owner, bad LSD endpoint move, and live correction-zero normalization counts.
- [x] Patch quarter south-boundary ownership so correction-adjacent LSDs stop on the near correction boundary instead of the far south correction-zero band when both survive.
- [x] Re-run decision tests and ATS Release build, then capture the result and lesson.

### Review
- Fresh runtime evidence showed the bad endpoint was still moving by `source=station-kind`, but the real owner was earlier: `VERIFY-QTR-SOUTHMID sec=1` still resolved the quarter south boundary from `L-USEC-C-0`, and the local correction logs showed the selected south band sitting about 25-26m below the section frame, which matches the userâ€™s â€œcrossing the road allowance to the south sideâ€ symptom.
- Root cause: `Plugin.Sections.SectionDrawingLsd.cs` was explicitly preferring the far `L-USEC-C-0` correction-zero south definition whenever both correction-zero bands existed. That quarter-owner decision happened before LSD endpoint enforcement, so later endpoint snapping faithfully followed an already-wrong south boundary.
- Fix: added `CorrectionSouthBoundaryPreference` and changed both correction south-boundary selectors to prefer the near correction inset boundary when a candidate is closer to `CorrectionLineInsetMeters` than to the far hard-boundary width, only falling back to the far correction boundary if no usable near candidate exists. I also enabled quarter verify output for sec 1 so the next rerun prints the exact selected south segment for the bad case.
- Verification:
  - `./.local_dotnet/dotnet.exe run --project ./src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release -p:NuGetAudit=false` -> `Decision tests passed.`
  - `./.local_dotnet/dotnet.exe build ./src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` -> build succeeded and updated both `src/AtsBackgroundBuilder/bin/x64/Release/net8.0-windows/AtsBackgroundBuilder.dll` and `build/net8.0-windows/AtsBackgroundBuilder.dll` at `2026-03-14 7:42:38 PM`, with the same 16 existing nullable warnings and 0 errors.
- Follow-up: rerun `63-12-6` and check `VERIFY-QTR-SOUTH-SELECT sec=1`, `VERIFY-QTR-SOUTHMID sec=1`, and `LSD-ENDPT line=1264817` in the log to confirm the chosen correction south segment has moved back to the near inset band.

## ATS Quarter Correction South-Boundary Follow-Up, 2026-03-14
- [ ] Inspect the newest ATS runtime log for sec 1-6 quarter south-boundary ownership and the affected correction-line LSD endpoints after the latest build.
- [ ] Narrow the correction south-boundary chooser so it does not globally push correction-adjacent LSDs onto the wrong side of the road allowance.
- [ ] Re-run decision tests and ATS Release build, then capture the result and lesson.

### Review
- The latest rerun showed the previous fix only corrected sec 1. `VERIFY-QTR-SOUTH-SELECT sec=1` was now using a near correction segment, but sec 6 still selected `L-USEC-C-0` with `dividerGap=79.138` and `dividerLinked=False`, which meant the correction-only south selector was still allowed to win with a far correction segment that did not reach the quarter divider.
- Root cause: `TryResolveQuarterViewSouthCorrectionBoundaryV` was not applying the same divider-gap penalty / divider-link preference that the normal south boundary selector uses, and `ApplyCorrectionSouthOverridesPreClamp` could then re-pick a different correction segment again through `TryResolveQuarterViewSouthMostCorrectionBoundarySegment`, undoing the initial south-boundary choice.
- Fix: the correction south selector now uses divider-gap scoring plus divider-linked preference before choosing a correction band, and the correction south corner override now reuses the already-selected correction south segment instead of independently reselecting another correction band. Quarter verify tracing was also widened to sec 1-6 so the next rerun shows the full correction seam row.
- Verification:
  - `./.local_dotnet/dotnet.exe run --project ./src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release -p:NuGetAudit=false` -> `Decision tests passed.`
  - `./.local_dotnet/dotnet.exe build ./src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` -> build succeeded and updated both `src/AtsBackgroundBuilder/bin/x64/Release/net8.0-windows/AtsBackgroundBuilder.dll` and `build/net8.0-windows/AtsBackgroundBuilder.dll` at `2026-03-14 7:51:08 PM`, with the same 16 existing nullable warnings and 0 errors.
- Follow-up: rerun `63-12-6` and inspect `VERIFY-QTR-SOUTH-SELECT sec=1..6`, `VERIFY-QTR-SOUTHMID sec=1..6`, and the affected `LSD-ENDPT` lines to confirm the correction seam row now uses divider-linked correction south candidates consistently.

## ATS Quarter Correction South-Boundary Re-Trace, 2026-03-14
- [ ] Inspect the newest ATS runtime log for sec 1-6 correction south ownership and the affected correction-line LSD endpoints after the latest build.
- [ ] Patch the actual remaining owner-selection or post-selection path based on the fresh trace.
- [ ] Re-run decision tests and ATS Release build, then capture the result and lesson.

### Review
- Fresh tracing showed the latest quarter-selection fixes were working in a narrower sense: sec 1-5 quarter south ownership was already on the near correction band (`VERIFY-QTR-SW-SW-APP` / `SE-SE-APP` south offsets about 5m), but the vertical LSD endpoints were still being moved later by the rule-matrix `CORRZERO/SEC` override to a second parallel correction-zero band about 20m farther south.
- Root cause: `TryFindQuarterInteriorCorrectionZeroTarget` in `Plugin.RoadAllowance.EndpointEnforcement.cs` ranked correction-zero candidates by boundary-gap first, so when two parallel `L-USEC-C-0` bands survived the seam it could jump from the already-near valid band to the farther south band purely because that farther band scored closer to the quarter boundary anchor. That bypassed the quarter owner the drawing had already selected.
- Fix: added `CorrectionZeroTargetPreference` and changed the correction-zero endpoint override to prefer the nearest valid `CORRZERO` band first, using boundary-gap only as a tie-breaker. I also widened `VERIFY-LSD-OUTER` tracing to sections 1-6 so the next rerun will show every affected correction-seam vertical endpoint in that row.
- Verification:
  - `./.local_dotnet/dotnet.exe run --project ./src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release -p:NuGetAudit=false` -> `Decision tests passed.`
  - `./.local_dotnet/dotnet.exe build ./src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` -> build succeeded and updated both `src/AtsBackgroundBuilder/bin/x64/Release/net8.0-windows/AtsBackgroundBuilder.dll` and `build/net8.0-windows/AtsBackgroundBuilder.dll` at `2026-03-14 8:03:56 PM`, with the same 16 existing nullable warnings and 0 errors.
- Follow-up: rerun `63-12-6` and inspect `VERIFY-LSD-OUTER sec=1..6`, the affected `LSD-ENDPT` lines with `kinds=CORRZERO/SEC`, and `VERIFY-QTR-SOUTHMID sec=1..6` to confirm the vertical LSD endpoints now stay on the already-near correction band instead of hopping to the farther south parallel band.

## ATS Correction-Line Missing LSD Re-Trace, 2026-03-14
- [ ] Inspect the newest ATS runtime log for sec 1-6 quarter ownership, correction-zero endpoint moves, and any missing correction-line LSD rebuild evidence after the latest build.
- [ ] Patch the actual remaining correction-line endpoint or rebuild path based on the fresh trace.
- [ ] Re-run decision tests and ATS Release build, then capture the result and lesson.

## ATS Correction-Zero Station-Kind Re-Trace, 2026-03-14
- [x] Inspect the newest ATS runtime log for the repeated sec 1-5 `CORRZERO/SEC` endpoint moves and confirm which endpoint-selection path still owns them.
- [x] Patch the specific station-target preservation rule so repeated rule-matrix passes stop walking already-snapped correction LSDs onto a farther parallel south band.
- [x] Re-run decision tests and ATS Release build, then capture the result and lesson.

### Review
- Fresh runtime evidence showed the remaining bad sec 1-5 vertical moves were not coming from `corrzero-interior` at all. The log kept reporting `outer source=station-kind` for the affected `CORRZERO/SEC` endpoints, and the same rule-matrix pass was running twice: first it snapped the endpoints onto a near correction-zero band, then the second pass moved those already-snapped endpoints another ~20 m farther south onto a second parallel band.
- Root cause: `TryFindBoundaryStationTarget` already had a primary-boundary preservation scan, but it intentionally kept searching after finding that the endpoint already sat on the primary preferred boundary. That behavior was useful for stale midpoint rows in some generic cases, but it was wrong for `CORRZERO`: a second correction-zero candidate could still win later in scoring, so repeated cleanup passes kept marching the same LSD endpoints outward across the road allowance.
- Fix: added `CorrectionZeroTargetPreference.ShouldPreserveExistingPrimaryBoundary` and changed `Plugin.RoadAllowance.EndpointEnforcement.cs` so once a vertical LSD endpoint already lies on the primary `CORRZERO` boundary, the station-target selector returns that existing endpoint immediately instead of continuing to rank farther parallel correction-zero rows.
- Verification:
  - `./.local_dotnet/dotnet.exe ./src/AtsBackgroundBuilder.DecisionTests/bin/Release/net8.0/AtsBackgroundBuilder.DecisionTests.dll` -> `Decision tests passed.`
  - `./.local_dotnet/dotnet.exe build ./src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` -> build succeeded and updated both `src/AtsBackgroundBuilder/bin/x64/Release/net8.0-windows/AtsBackgroundBuilder.dll` and `build/net8.0-windows/AtsBackgroundBuilder.dll` at `2026-03-14 8:18:36 PM`, with the same 16 existing nullable warnings and 0 errors.
- Follow-up: rerun `63-12-6` and inspect whether the second rule-matrix block no longer emits `VERIFY-LSD-OUTER sec=1..5 ... kinds=CORRZERO/SEC` moves after the endpoints have already landed on the near correction band.

## ATS SE-6 Quarter Correction Fallback, 2026-03-14
- [x] Inspect the newest sec 6 SouthEast quarter logs for the remaining bad LSD endpoint and crossing quarter line.
- [x] Patch the correction south-boundary chooser so an orphaned non-divider-linked correction segment cannot own that quarter.
- [x] Re-run decision tests and ATS Release build, then capture the result and lesson.

### Review
- The final bad `SE 6` rerun showed both remaining symptoms shared the same owner: `VERIFY-QTR-SOUTH-SELECT sec=6` was still picking `L-USEC-C-0` with `dividerGap=79.138` and `dividerLinked=False`, and the same quarter then drove the remaining bad `CORRZERO/SEC` LSD endpoint. That meant the last failure had shifted fully upstream into quarter south ownership for that one quarter.
- Root cause: `TryResolveQuarterViewSouthCorrectionBoundaryV` would still return a non-divider-linked correction candidate through its `foundAny` fallback when no divider-linked correction segment survived. In `SE 6`, that let an orphaned east-half correction segment own the quarter south edge even though it never reached the quarter divider, so both the quarter line and the LSD endpoint crossed the road allowance together.
- Fix: added `CorrectionSouthBoundaryPreference.IsUnlinkedDividerGapAcceptable` and changed `Plugin.Sections.SectionDrawingLsd.cs` so the correction south chooser rejects orphaned non-divider-linked correction candidates once their divider gap exceeds a tolerant threshold (`RoadAllowanceSecWidthMeters * 0.6`), allowing that quarter to fall back to the normal south owner instead.
- Verification:
  - `./.local_dotnet/dotnet.exe build ./src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` -> build succeeded and updated both `src/AtsBackgroundBuilder/bin/x64/Release/net8.0-windows/AtsBackgroundBuilder.dll` and `build/net8.0-windows/AtsBackgroundBuilder.dll` at `2026-03-14 8:28:50 PM`, with the same 16 existing nullable warnings and 0 errors.
  - `./.local_dotnet/dotnet.exe ./src/AtsBackgroundBuilder.DecisionTests/bin/Release/net8.0/AtsBackgroundBuilder.DecisionTests.dll` -> `Decision tests passed.`
- Follow-up: rerun `63-12-6` and check that `VERIFY-QTR-SOUTH-SELECT sec=6` no longer reports the `dividerGap=79.138 dividerLinked=False source=L-USEC-C-0` owner, and that the remaining `SE 6` quarter line plus LSD now stop on the same normal south boundary.

## ATS SE-6 Missing South Owner Fallback, 2026-03-14
- [x] Inspect the newest SE 6 quarter/LSD runtime traces after the previous patch to determine why the remaining LSD and quarter line became missing.
- [x] Patch south-boundary resolution so SE 6 can recover a real ordinary south segment after the orphaned correction owner is rejected.
- [x] Re-run decision tests and ATS Release build, then capture the result and lesson.

### Review
- The newest rerun showed a new state change in `SE 6`: the bad correction south owner was gone, but `VERIFY-QTR-SOUTHMID sec=6` now reported `southSource=fallback-20.12`, and there was no longer any `VERIFY-QTR-SOUTH-SELECT sec=6` line in that latest block. That meant the previous patch successfully rejected the orphaned correction owner, but the quarter then failed to recover any real ordinary south boundary segment and dropped to a synthetic fallback edge instead.
- Root cause: `Plugin.Sections.SectionDrawingLsd.cs` still built `southResolutionSegments` from only `L-USEC-0` and `L-SEC*`. In the remaining `SE 6` case, once the orphaned `L-USEC-C-0` was rejected, the real surviving ordinary south owner was on the standard `L-USEC` / `L-USEC2012` family, so the resolver never saw it and the quarter linework fell back to `20.12` rather than selecting a real south segment.
- Fix: added `QuarterSouthBoundaryLayerFilter` and changed south-boundary resolution to include ordinary non-correction `L-USEC`, `L-USEC2012`, `L-USEC-2012`, `L-USEC-0`, and `L-SEC*` layers when recovering the south owner after correction filtering. That keeps correction layers excluded, but lets `SE 6` recover a real ordinary south boundary instead of staying synthetic.
- Verification:
  - `./.local_dotnet/dotnet.exe build ./src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` -> build succeeded and updated both `src/AtsBackgroundBuilder/bin/x64/Release/net8.0-windows/AtsBackgroundBuilder.dll` and `build/net8.0-windows/AtsBackgroundBuilder.dll` at `2026-03-14 9:06:48 PM`, with the same 16 existing nullable warnings and 0 errors.
  - `./.local_dotnet/dotnet.exe ./src/AtsBackgroundBuilder.DecisionTests/bin/Release/net8.0/AtsBackgroundBuilder.DecisionTests.dll` -> `Decision tests passed.`
- Follow-up: rerun `63-12-6` and confirm that the latest `SE 6` block now prints a real `VERIFY-QTR-SOUTH-SELECT sec=6 ... source=L-USEC...` owner instead of only `southSource=fallback-20.12`, and that the remaining LSD plus quarter line reappear on that same recovered south edge.
## ATS SE 6 Local East Correction Tie, 2026-03-15
- [x] Re-read the latest SE 6 quarter and LSD log path after the widened south fallback still missed.
- [x] Patch quarter southeast construction so a section with no selected south owner can still tie its east corner to a local correction segment.
- [x] Rebuild ATS and rerun the decision tests.

Review:
- Latest SE 6 logs showed the bad full-width correction owner was gone, but quarter construction had dropped to southSource=fallback-20.12 while the LSD endpoint pass still snapped the same quarter to CORRZERO/SEC.
- Added a local southeast correction fallback in SectionDrawingLsd so the east corner can still resolve against an east-side correction segment when the full south owner is intentionally rejected.
- Verification: dotnet build passed with the same 16 existing nullable warnings; decision tests passed.
## ATS SE 6 Local West-East Correction Trend, 2026-03-15
- [x] Inspect the newest sec 6 logs after the user reported SW 6 ending on L-USEC-C and SE 6 still crossing.
- [x] Replace the east-only no-owner correction fallback with a west/east local correction trend rebuild and a rebuilt south-mid point.
- [x] Rebuild ATS and rerun the decision tests.

Review:
- Latest logs showed VERIFY-QTR-SE-SE-CORR-FALLBACK was firing, but VERIFY-QTR-SOUTHMID sec=6 was still on southSource=fallback-20.12, so the southeast quarter was being stretched between a correction east corner and a synthetic south midpoint.
- Added symmetric local correction fallbacks for west and east corners, scored from live L-USEC-C/L-USEC-C-0 geometry, plus a rebuilt south-mid intersection on the west-east correction trend when no full south owner exists.
- Verification: dotnet build passed with the same 16 existing nullable warnings; decision tests passed.
## ATS SE 6 Missing Inner Correction Companion, 2026-03-15
- [x] Re-check the newest sec 6 logs after the west/east local fallback still left southSource=fallback-20.12.
- [x] Patch correction post-processing so outer-axis L-USEC-C-0 normalization also recreates the missing inner companion before quarter/LSD selection runs.
- [x] Rebuild ATS and rerun the decision tests.

Review:
- Latest sec 6 logs showed the east fallback was firing, but there was still no real south correction owner or west/mid correction trend, which pointed back upstream to missing inner correction geometry rather than another quarter-only scoring bug.
- Outer-axis correction-zero normalization now relayers the misbucketed row back to L-USEC-C and immediately tries to recreate the matching inner L-USEC-C-0 companion in the same pass.
- Verification: dotnet build passed with the same 16 existing nullable warnings; decision tests passed.
## ATS SE 6 South Drift Regression, 2026-03-15
- [ ] Inspect the newest sec 6 logs after the latest companion-creation change added an extra offset line and pulled both SW/SE south.
- [ ] Patch the earliest bad correction companion / quarter ownership branch so sec 6 stops generating the extra south linework and both quarters stay north of the road allowance.
- [ ] Rebuild ATS and rerun the decision tests.
## ATS Sec 6 Partial Hard Correction South Owner, 2026-03-15
- [x] Inspect latest sec 6 quarter/LSD logs and compare against sec 5 healthy correction ownership.
- [x] Confirm the live sec 6 `L-USEC-C-0` owner is behaving like a partial hard-boundary row (`southOffset ~18-22m`) instead of the intended inset companion (`~5m`).
- [x] Patch quarter south correction ownership to reject partial hard-boundary correction rows from owning the full section south boundary.
- [x] Apply the same full-span gate to south-most correction promotion so fallback logic does not re-promote the same partial hard row.
- [x] Add decision coverage for the new hard-boundary coverage rule and verify build/tests.

### Review
- Root cause: sec 6 had a live `L-USEC-C-0` row that only covered about half the section width, but the quarter south resolver still allowed hard-boundary correction rows to own the entire south edge. That forced both SW/SE quarter geometry south across the road allowance.
- Fix: added `CorrectionSouthBoundaryPreference.IsHardBoundaryCoverageAcceptable(...)` and used it in the two full-boundary south correction selectors so only near-full-span hard correction rows can own/promote the whole quarter south boundary.
- Verification: `dotnet build .\\src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` passed with the same 16 existing nullable warnings and 0 errors; `dotnet .\\src\\AtsBackgroundBuilder.DecisionTests\\bin\\Release\\net8.0\\AtsBackgroundBuilder.DecisionTests.dll` passed.
- Remaining runtime check: rerun `63-12-6` in AutoCAD and confirm sec 6 falls back to the local correction tie path instead of selecting the partial hard `L-USEC-C-0` owner.
## ATS Sec 6 Local Correction Fallback And LSD Override, 2026-03-15
- [x] Inspect the newest sec 6 log after the last rerun and confirm the full south-owner gate now falls back, but local correction corner fallback and LSD station snapping still reuse the bad row.
- [x] Patch quarter local correction corner fallback to infer an inset companion from hard-like correction rows instead of intersecting the hard row directly.
- [x] Patch LSD rule-matrix so a failed correction-specific interior lookup downgrades back to the normal fallback kinds instead of generic `CORRZERO/SEC` station snapping.
- [x] Rebuild and rerun decision tests.

### Review
- Root cause: the earlier gate successfully rejected sec 6 as a full correction south owner, but two later paths were still reusing the same bad partial correction row: the local quarter correction-corner fallback extrapolated that row directly, and the LSD rule-matrix still fell back from `correctionOverride` into generic `CORRZERO/SEC` station snapping.
- Fix: local correction corner fallback now infers the missing inset companion from hard-like correction rows before scoring the SW/SE corner tie, and LSD endpoint enforcement now downgrades back to the quarter’s normal fallback target kinds when the correction-specific interior target cannot actually be resolved.
- Verification: `dotnet build .\\src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` passed with the same 16 existing nullable warnings and 0 errors; `dotnet .\\src\\AtsBackgroundBuilder.DecisionTests\\bin\\Release\\net8.0\\AtsBackgroundBuilder.DecisionTests.dll` passed.
- Remaining runtime check: rerun `63-12-6` and inspect the new `VERIFY-QTR-SW-SW-CORR-FALLBACK`, `VERIFY-QTR-SE-SE-CORR-FALLBACK`, `VERIFY-QTR-SOUTHMID-CORR-FALLBACK`, and `LSD-ENDPT ... correction-override-downgraded` lines.
## ATS Correction Seam Regression Rebalance, 2026-03-15
- [x] Re-check the current correction-seam runtime behavior after the downgrade patch started sending all affected LSD endpoints back to `L-USEC-C` / `TWENTY`.
- [x] Patch quarter local-correction fallback so raw `L-USEC-C` corner intersections that sit too close to the section south edge are not accepted as inset owners; infer the inset companion before scoring instead.
- [x] Patch LSD endpoint enforcement so failed direct `CORRZERO` lookup falls back to an inferred correction-zero target from `L-USEC-C` before downgrading to ordinary boundary kinds.
- [x] Patch correction normalization so it reuses an existing inner companion before creating a new `L-USEC-C-0`, preventing duplicate correction-zero artifacts.
- [x] Rebuild ATS and rerun decision tests.

### Review
- Root cause: the last downgrade patch was too broad. Once `TryFindQuarterInteriorCorrectionZeroTarget(...)` missed, the rule-matrix dropped all affected vertical LSDs back to ordinary `TWENTY/SEC`, which is why they started terminating on `L-USEC-C`. At the same time, the sec 6 local quarter fallback was still treating raw `L-USEC-C` corner hits with south offsets around 3-8m as if they were valid inset boundaries, so the quarter line could still cross the road allowance. Separately, the outer-axis normalization companion pass could create an extra `L-USEC-C-0` even when a usable inner companion already existed.
- Fix: local quarter correction corner fallback now rejects implausibly small inset offsets and infers the inset corner from the outer correction row using a perpendicular shift before scoring. LSD endpoint enforcement now tries a correction-specific inferred `C-0` target from horizontal `L-USEC-C` geometry before downgrading to ordinary fallback kinds. Outer-axis correction-zero normalization now reuses an existing inner companion before creating a new one, which prevents duplicate `L-USEC-C-0` artifacts.
- Verification: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` compiled ATS and decision tests successfully but still failed the final DLL copy because AutoCAD is locking `build\net8.0-windows\AtsBackgroundBuilder.dll`; `dotnet msbuild .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln /t:Compile /p:Configuration=Release /p:NuGetAudit=false /v:minimal` passed; `dotnet .\src\AtsBackgroundBuilder.DecisionTests\bin\Release\net8.0\AtsBackgroundBuilder.DecisionTests.dll` passed.
- Runtime check pending: reload the fresh DLL from `src\AtsBackgroundBuilder\bin\x64\Release\net8.0-windows\AtsBackgroundBuilder.dll` and rerun `63-12-6` to confirm the affected LSD endpoints return to `L-USEC-C-0`, the extra `L-USEC-C-0` survivor is gone, and the sec 6 quarter line stays north of the road allowance.
## ATS Deterministic Correction Companion Recovery, 2026-03-15
- [x] Inspect the newest runtime trace after the last build showed LSD endpoints landing short off linework.
- [x] Confirm the off-line stops were coming from synthetic `corrzero-inferred` endpoint targets, while sec 6 still lacked a deterministic full correction-zero south owner.
- [x] Remove the synthetic inferred correction-zero endpoint path so LSDs no longer snap to points that are not on real geometry.
- [x] Tighten correction companion matching so partial `L-USEC-C-0` overlap cannot suppress creation of the real full companion row.
- [x] Rebuild ATS and rerun decision tests.

### Review
- Root cause: the previous patch tried to recover missing `L-USEC-C-0` geometry inside endpoint enforcement by inventing `corrzero-inferred` target points from `L-USEC-C`. That made some LSD endpoints stop short in empty space because those inferred points were not guaranteed to lie on actual CAD entities. Upstream, the correction companion matcher was still treating a partial inner `L-USEC-C-0` overlap as a valid companion, which prevented creation of the real full-span companion row and kept sec 6 on `southSource=fallback-20.12`.
- Fix: removed the synthetic `corrzero-inferred` endpoint path from `EndpointEnforcement`, returning endpoint snapping to actual geometry only. Tightened `TryFindCorrectionInnerCompanion(...)` so a companion now needs substantial coverage of the source correction row before it can block companion creation; partial overlap no longer counts as “good enough.” This moves the fix back upstream into deterministic correction-row generation instead of section-local endpoint recovery.
- Verification: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` passed with 16 existing nullable warnings and 0 errors; both deployed and source DLLs updated at `2026-03-15 2:08:53 PM`; `dotnet .\src\AtsBackgroundBuilder.DecisionTests\bin\Release\net8.0\AtsBackgroundBuilder.DecisionTests.dll` passed.
- Runtime check pending: rerun `63-12-6` and confirm there are no `corrzero-inferred` traces, LSD endpoints stop on real `L-USEC-C-0` geometry, and sec 6 stops falling back to synthetic south ownership.
## ATS Sec 6 Deterministic Correction Owner Recovery, 2026-03-15
- [ ] Inspect the newest sec 6 runtime log and current correction-row generation/selection code to confirm which real correction rows exist, which one sec 6 is selecting, and whether the extra south offset line is the same survivor row.
- [ ] Patch upstream correction-row generation/selection so sec 6 gets one consistent real `L-USEC-C-0` companion and the extra south-side correction row is removed instead of reused.
- [ ] Rebuild ATS, rerun decision tests, and document the deterministic fix plus verification.
## ATS Sec 6 Same-Side Correction Companion Matching, 2026-03-15
- [x] Re-inspect the sec 6 correction behavior and identify whether the wrong `L-USEC-C-0` ownership comes from deterministic row generation or later quarter/LSD fallback code.
- [x] Patch correction companion matching so an outer correction row can only pair with an inset companion on the same side of the fitted seam, preventing cross-road `C-0` ownership.
- [x] Rebuild ATS, rerun decision tests, and document the verified upstream fix.

### Review
- Root cause: `TryFindCorrectionInnerCompanion(...)` was matching by absolute distance from the fitted seam center only. In sec 6 that allowed a north-side correction outer row to treat a south-side row across the road allowance as its valid `L-USEC-C-0` companion when the absolute offsets happened to differ by about the 5m inset. That split the correction model before quarter/LSD drawing, which is why the sec 6 LSD endpoints, the quarter line, and the extra south-side offset line all drifted together.
- Fix: companion matching is now side-aware. A correction companion must stay on the same signed side of the seam center and be inward by the expected inset before it can block creation or relayering of the real companion. This keeps correction row generation deterministic and upstream instead of relying on sec-level fallback behavior.
- Verification: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` passed with the same 16 existing nullable warnings and 0 errors; `dotnet .\src\AtsBackgroundBuilder.DecisionTests\bin\Release\net8.0\AtsBackgroundBuilder.DecisionTests.dll` passed; both `src\AtsBackgroundBuilder\bin\x64\Release\net8.0-windows\AtsBackgroundBuilder.dll` and `build\net8.0-windows\AtsBackgroundBuilder.dll` updated at `2026-03-15 2:23:24 PM`.
- Runtime check pending: rerun `63-12-6` and confirm sec 6 stops cross-pairing north/south correction rows, the extra south-side offset line disappears, and the sec 6 LSD / quarter south boundary stay on the north-side `L-USEC-C-0` row.
## ATS Sec 6 Unified Correction Row Recovery, 2026-03-15
- [ ] Inspect the newest sec 6 runtime log and identify which correction rows still survive through correction post-processing, quarter south ownership, and LSD endpoint enforcement.
- [ ] Patch the upstream correction-row ownership/generation logic so sec 6 produces one consistent correction row set for correction linework, 1/4 lines, and LSD endpoints without section-local fallback behavior.
- [ ] Rebuild ATS, rerun decision tests, and document the verified outcome plus the remaining runtime check.
- [x] Inspect the newest sec 6 runtime log and identify which correction rows still survive through correction post-processing, quarter south ownership, and LSD endpoint enforcement.
- [x] Patch the upstream correction-row ownership/generation logic so sec 6 produces one consistent correction row set for correction linework, 1/4 lines, and LSD endpoints without section-local fallback behavior.
- [x] Rebuild ATS, rerun decision tests, and document the verified outcome plus the remaining runtime check.

### Review
- Root cause: sec 6 was still letting different consumers interpret the correction bands differently. `SectionDrawingLsd` could infer an inset owner from any hard-like `L-USEC-C-0` survivor, even though that layer should already mean the real inset row, while endpoint enforcement still ranked `CORRZERO` candidates mostly by smallest move. That let quarter south ownership, LSD endpoint targets, and live correction geometry drift onto different parallel rows. Separately, correction cleanup was still splitting only `L-USEC-C-0`, so a surviving `L-USEC-C` row could visually cross a north/south road allowance even when the inset row broke correctly.
- Fix: the correction south selectors now preserve layer semantics and only infer inset companions from `L-USEC-C` outer rows, not from hard-like `L-USEC-C-0` survivors. `TryFindQuarterInteriorCorrectionZeroTarget(...)` now prefers the correction-zero row whose offset best matches the real inset distance before using move distance as a tie-breaker, so LSD endpoints stop freezing on the wrong parallel `C-0` just because they already touch it. Correction post-processing now also splits live horizontal `L-USEC-C` rows at vertical road-allowance targets, which keeps the correction linework itself on the same local row model as the quarter and LSD consumers.
- Verification: `dotnet msbuild .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj /t:Compile /p:Configuration=Release /p:Platform=x64 /p:NuGetAudit=false` passed with the same 16 existing nullable warnings; `dotnet build .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release -p:NuGetAudit=false -p:BuildProjectReferences=false -v minimal` passed; `dotnet .\src\AtsBackgroundBuilder.DecisionTests\bin\Release\net8.0\AtsBackgroundBuilder.DecisionTests.dll` passed.
- Runtime check pending: reload the fresh DLL from `src\AtsBackgroundBuilder\bin\x64\Release\net8.0-windows\AtsBackgroundBuilder.dll` and rerun `63-12-6` to confirm sec 6 stops falling to `southSource=fallback-20.12`, both south-quarter LSDs terminate on the north-side `L-USEC-C-0`, the 1/4 line stays north of the road allowance, and the crossing horizontal `L-USEC-C` row is broken at the north/south road allowance instead of running through it.
## ATS Correction Generator Includes Plain USEC Seam Rows, 2026-03-15
- [x] Inspect the newest runtime trace and compare the bad sec 6 / east-end coordinates to the live rows the plugin is actually selecting.
- [x] Patch upstream correction-row generation so plain `L-USEC` seam rows can become correction outers and bridge continuations before quarter/LSD selection runs.
- [x] Rebuild ATS, rerun decision tests, and document the verified result plus runtime check.

### Review
- Root cause: the remaining sec 6 failures were upstream of quarter/LSD consumption. The bad endpoints the user gave (`317450.525,6033260.968`, `318248.606,6033230.114`) are on plain `L-USEC` rows, which means the real correction outer/inset pair never fully existed there. The late seam-band relayer and bridge pass in `CorrectionLinePostProcessing` were still only treating `L-USEC2012` / `L-USEC3018` and south-side `L-USEC-0` as deterministic correction-outer fallback layers, so ordinary seam rows on `L-USEC` could survive untouched right where sec 6 needed them to become `L-USEC-C` and spawn the true `L-USEC-C-0` inset companion.
- Fix: introduced `CorrectionOuterFallbackLayerClassifier` and widened both the late correction-outer fallback relayer and the bridge relayer to include plain `L-USEC` seam rows. That keeps seam-band ordinary usec geometry eligible to become correction outer geometry before companion generation, which is the deterministic upstream path needed for sec 6 and the east-end seam instead of section-local fallback ownership.
- Verification: `dotnet msbuild .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj /t:Compile /p:Configuration=Release /p:Platform=x64 /p:NuGetAudit=false` passed with the same 16 existing nullable warnings; `dotnet build .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release -p:NuGetAudit=false -p:BuildProjectReferences=false -v minimal` passed; `dotnet .\src\AtsBackgroundBuilder.DecisionTests\bin\Release\net8.0\AtsBackgroundBuilder.DecisionTests.dll` passed; `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` passed and updated the deployed DLL.
- Runtime check pending: rerun `63-12-6` and confirm the sec 6 LSDs stop landing on the plain `L-USEC` row, the sec 6 quarter line hits the inset row near `318248.413,6033225.098`, the extra south correction row disappears, the tiny extra `L-USEC-C-0` stub at the east end is gone, and the east-end `L-USEC-0` line returns to `327231.911,6032878.418`.
## ATS Correction Seam Fit Uses Real Boundary Trend, 2026-03-15
- [x] Checked the newest uild/net8.0-windows/AtsBackgroundBuilder.log and confirmed sec 6 still had southSource=fallback-20.12 while LSD endpoints were targeting plain L-USEC axes at 317849.566,6033245.541 and 318652.669,6033214.493.
- [x] Found that ApplyCorrectionLinePostBuildRules could silently no-op when zero candidate segments intersected the resolved seam windows, which explained the total absence of CorrectionLine: runtime lines in the newest rerun.
- [x] Moved correction seam fitting upstream to sample the real section boundary trend from Top/Bottom/Left/Right anchors across each section x-span instead of using only Bottom.Y / Top.Y plus extents midpoints.
- [x] Added an explicit CorrectionLine: no candidate segments intersect resolved seam windows ... log so future seam-pass no-ops are visible immediately.
- [x] Added decision coverage for the new correction boundary trend sampler and rebuilt ATS successfully.
- Review:
  - The deployed DLL and source DLL both updated at 2026-03-15 3:43:18 PM.
  - Decision tests passed.
  - ATS solution build passed with the same 16 existing nullable warnings and 0 errors.
## ATS Correction Bridge Propagation Requires Two-Sided Touch, 2026-03-15
- [x] Checked the newest AtsBackgroundBuilder.log after the seam-fit change and confirmed the regression shape matched correction-row propagation: sec 6 quarter corners improved, but an extra correction outer now extended westward and the east-end 100m context extension was clipped away.
- [x] Tightened the correction bridge relayer so an ordinary collinear segment is only promoted when both endpoints touch correction-owned outer geometry, instead of allowing one-ended propagation to walk a whole L-USEC chain.
- [x] Added decision coverage for the new two-sided bridge requirement and rebuilt ATS successfully.
- Review:
  - Decision tests passed.
  - ATS solution build passed with the same 16 existing nullable warnings and 0 errors.
## ATS Sec 6 Correction Zero Endpoint Reuse, 2026-03-15
- [ ] Inspect the newest sec 6 log block and endpoint-enforcement correction-zero target code to confirm why LSD endpoints downgrade off the real L-USEC-C-0 owner.
- [ ] Patch deterministic correction-zero endpoint selection so correction-touching LSDs reuse the live inset owner and do not prefer or leave behind the extra L-USEC-C row.
- [ ] Rebuild ATS, run decision tests, and document the verified outcome.
## ATS Sec 6 Correction Zero Endpoint Reuse Review, 2026-03-15
- [x] Inspected the newest sec 6 log block and confirmed quarter south ownership had moved to L-USEC-C-0 while the live inset row still survived as L-USEC-C, leaving the LSD endpoint pass without a real correction-zero target.
- [x] Added deterministic inset-axis normalization so live seam-overlapping L-USEC-C rows with a valid same-side outer companion are relayered to L-USEC-C-0.
- [x] Verified with AtsBackgroundBuilder.DecisionTests and dotnet build that the new normalization compiles and the deployed DLL updated successfully.
## ATS Correction LSD Endpoint Uses Quarter South Owner, 2026-03-15
- [x] Confirmed the newest 4:22 PM log still had correct sec 6 quarter south ownership (L-USEC-C-0) while the LSD endpoint pass independently downgraded back to fallback anchors.
- [x] Changed rule-matrix endpoint enforcement to reuse the resolved quarter south correction boundary model before falling back to raw live corrZeroHorizontal station matching.
- [x] Rebuilt ATS, ran decision tests, and copied the fresh DLL into uild\net8.0-windows for runtime verification.
## ATS Sec 6 Extra Correction Row Cleanup, 2026-03-15
- [ ] Inspect the newest sec 6 runtime log and current correction post-processing passes to identify which step leaves the extra segment 318248.219,6033220.081 -> 319056.344,6033188.840 alive.
- [ ] Patch deterministic correction cleanup so sec 6 keeps only the intended correction row(s) and the stray parallel segment is removed or relayered consistently.
- [ ] Rebuild ATS, run decision tests, and document the verified outcome.
- [x] Tightened final correction outer consistency so ordinary spans with a full correction-zero inset companion only promote to L-USEC-C when anchored into the correction chain at both ends; this targets the remaining sec 6 extra parallel row at 318248.219,6033220.081 -> 319056.344,6033188.840.
- [x] Verification: decision tests passed and ATS solution build succeeded at 2026-03-15 4:49:09 PM; deployed DLL updated in build\net8.0-windows.
- [x] Added a final live correction-zero snap after resolved/interior LSD correction targeting so correction-touching LSD endpoints land on the actual drawn L-USEC-C-0 segment and do not stop short with a visible gap.
- [x] Verification: decision tests passed and ATS solution build succeeded at 2026-03-15 4:54:05 PM; deployed DLL updated in build\net8.0-windows.
## ATS Sec 6 Final Correction Row + LSD Live Snap, 2026-03-15
- [ ] Inspect the newest runtime log for sec 6 correction rows and both vertical/horizontal LSD endpoint decisions to identify the still-active live pass.
- [ ] Patch the real owning pass so sec 6 keeps only the intended correction pair and LSD endpoints snap to live geometry in both orientations.
- [ ] Rebuild ATS, run decision tests, and confirm the deployed DLL timestamp for the fixed build.
- [ ] Document the verified outcome and capture any new lesson from this regression.
- [x] Inspect newest sec 6 / LSD runtime behavior and confirm the live-QSEC midpoint path was not actually compiled into the solution build.
- [x] Replace rule-matrix LSD inner endpoint targeting with deterministic live L-QSEC intersection targeting for both north-south and east-west LSDs.
- [x] Fix correction seam pruning scope so the sec 6 redundant correction row pass receives the resolved seam set outside the transaction.
- [x] Rebuild ATS, run decision tests, and verify the deployed DLL updates.
Review 2026-03-15 5:18 PM
- Root cause: the earlier live-QSEC midpoint change referenced helper locals from a different nested scope, so AutoCAD was still running the old anchor-based inner endpoint path even though the source diff looked correct.
- Final implementation: rule-matrix inner targets now resolve directly from live L-QSEC geometry already collected in the same method, and redundant correction-band pruning now receives the resolved seam list outside the seam-build transaction.
- Verification: `Decision tests passed.` and `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` succeeded with the existing 16 nullable warnings and 0 errors.
## ATS Correction Tie-In Cleanup + Final LSD Ordering, 2026-03-15
- [ ] Inspect the newest correction tie-in overhangs and confirm which non-final LSD passes are still running.
- [ ] Remove pre-final LSD endpoint enforcement so LSD draw/snap stays in the final stage only.
- [ ] Add a deterministic ordinary L-USEC-0 / L-USEC-2012 trim-to-vertical-correction-boundary pass for the tie-in overhangs.
- [ ] Build ATS, run decision tests, and verify the deployed DLL timestamp.
- [ ] Record the root cause and lesson.
- [x] Inspect the newest correction tie-in overhangs and confirm which non-final LSD passes are still running.
- [x] Remove pre-final LSD endpoint enforcement so LSD draw/snap stays in the final stage only.
- [x] Add a deterministic ordinary L-USEC-0 / L-USEC-2012 trim-to-vertical-correction-boundary pass for the tie-in overhangs.
- [x] Build ATS, run decision tests, and verify the deployed DLL timestamp.
- [x] Record the root cause and lesson.
Review 2026-03-15 5:45 PM
- Root cause: LSD geometry was still being drawn/snapped in multiple pre-final stages, and ordinary L-USEC-0 / L-USEC-2012 rows had no dedicated final tie-in trim against the matching vertical hard targets created around correction lines.
- Final implementation: deferred all LSD draw/enforcement to the final stage only, removed the extra correction-post LSD enforcement, and added an ordinary USEC tie-in overhang trim pass inside correction post-processing.
- Verification: `Decision tests passed.` and `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` succeeded with the existing 16 nullable warnings and 0 errors.
## ATS Remaining Correction Tie-In Owner Trace, 2026-03-15
- [ ] Inspect the newest log and confirm whether the latest LSD-ordering and ordinary tie-in trim changes actually ran.
- [ ] Identify the true owning pass for the remaining overhangs and missing LSDs, then patch that pass deterministically.
- [ ] Build ATS, run decision tests, and verify the deployed DLL timestamp.
- [ ] Record the root cause and lesson.
## ATS Correction Tie-In + Final LSD Anchor Snap Review, 2026-03-15
- [x] Confirmed latest build was loading from log (`Cleanup: section geometry finalized ... LSD draw/enforcement remains deferred to the final stage.`).
- [x] Traced remaining no-op to two causes:
  - ordinary `L-USEC-0` / `L-USEC-2012` tie-in trim only recognized same-band ordinary vertical targets, so tie-ins that should stop on correction/section hard boundaries were skipped.
  - final LSD rule-matrix inner snap was resolving from the already-drifted drawn endpoint instead of the resolved quarter anchor, so east-west / north-south LSDs could preserve midpoint drift even in the final pass.
- [x] Patched tie-in trim to admit correction/section vertical hard boundaries as supported anchors/trim targets while still preferring same-band ordinary targets first.
- [x] Patched final LSD live-QSEC inner target resolution to reference the resolved quarter anchor before falling back.

### Review
- `dotnet build .\\src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` succeeded with the same 16 existing nullable warnings and 0 errors.
- `dotnet build .\\src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release -p:NuGetAudit=false -p:BuildProjectReferences=false -v minimal` succeeded.
- `dotnet .\\src\\AtsBackgroundBuilder.DecisionTests\\bin\\Release\\net8.0\\AtsBackgroundBuilder.DecisionTests.dll` reported `Decision tests passed.`
## ATS Exact Sec 6 Coordinate Trace, 2026-03-15
- [ ] Trace these exact failing endpoints in the latest log and code path before changing logic:
  - `L-USEC-2012` ending at `317440.073,6033251.323` should end at `317440.273,6033256.340`
  - LSD ending at `317849.383,6033240.948` should end at `317844.343,6033240.719`
  - `L-USEC-0` ending at `319056.344,6033188.840` should end at `319056.533,6033193.856`
- [ ] Patch the owning sec 6 geometry pass deterministically.
- [ ] Build ATS and run decision tests.
- [ ] Document the verified root cause and prevention note.
## ATS Sec 6 Exact Coordinate Follow-up, 2026-03-15
- [x] Traced the latest sec 6 LSD miss to a stationing regression, not the correction row owner.
- [x] Verified from the log geometry that using the resolved inner LSD station against the logged sec 6 correction south trend projects to `317844.410,6033240.717`, which matches the user-expected target `317844.343,6033240.719` within rounding/log drift.
- [x] Patched correction-zero outer targeting to preserve the resolved inner station in both the resolved-boundary path and the live correction-zero projection path.
- [x] Tightened the final ordinary `L-USEC-0` / `L-USEC-2012` tie-in trim so ordinary-only contacts no longer block trimming to harder correction/section vertical stops, and hard vertical targets outrank ordinary same-band targets in that pass.

### Review
- `dotnet build .\\src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` succeeded with the same 16 existing nullable warnings and 0 errors.
- `dotnet .\\src\\AtsBackgroundBuilder.DecisionTests\\bin\\Release\\net8.0\\AtsBackgroundBuilder.DecisionTests.dll` reported `Decision tests passed.`
## ATS Sec 6 Vertical Tie-In Root Cause, 2026-03-15
- [x] Confirmed the remaining sec 6 section-line endpoints were vertical ordinary `L-USEC-0` / `L-USEC-2012` tie-ins, while the previous final trim only handled horizontal ordinary sources.
- [x] Extended the final ordinary tie-in trim to process both:
  - horizontal ordinary sources to vertical hard targets
  - vertical ordinary sources to horizontal hard targets
- [x] Tie-in target ranking now prefers correction-zero first, then correction outer, then SEC, then ordinary same-band targets in the final trim.

### Review
- `dotnet build .\\src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` succeeded with the same 16 existing nullable warnings and 0 errors.
- `dotnet .\\src\\AtsBackgroundBuilder.DecisionTests\\bin\\Release\\net8.0\\AtsBackgroundBuilder.DecisionTests.dll` reported `Decision tests passed.`

## ATS Python Verification Loop, 2026-03-15
- [x] Capture the latest sec 6 failing coordinates from the current log for the section-line and LSD cases.
- [x] Reproduce the current sec 6 ordinary section-line targets in Python from live logged geometry.
- [ ] Identify the exact pass still leaving the sec 6 ordinary tie-in endpoints short of the resolved correction-zero row.
- [ ] Identify the exact corrzero LSD stationing step that still leaves a small endpoint gap.
- [ ] Patch only the verified owners, then rebuild and rerun decision tests.
- [ ] Re-run Python checks against the live geometry before treating the fix as correct.
- [x] Identify the exact pass still leaving the sec 6 ordinary tie-in endpoints short of the resolved correction-zero row.
- [x] Identify the exact corrzero LSD stationing step that still leaves a small endpoint gap.
- [x] Patch only the verified owners, then rebuild and rerun decision tests.
- [x] Re-run Python checks against the live geometry before treating the fix as correct.
Review:
- Verified in Python that the current sec 6 south correction row resolves the requested ordinary endpoints to within 0.012 m of the expected targets.
- Verified in Python that using the inner quarter station for the live corrzero snap reduces the sec 6 LSD target delta to about 0.083 m from the expected point.
- Build succeeded and decision tests passed.
Regression follow-up:
- Confirmed from the 2026-03-15 6:23 PM log that the newly added ordinary `L-USEC-0/2012` endpoint-on-hard pass moved 20 endpoints after the final trim and caused the broad north-south overextensions.
- Removed that pass entirely rather than tuning a rule that was too broad for the final stage.
- Rebuilt and reran decision tests; runtime verification still needs the next AutoCAD rerun.

## ATS Sec 6 Resolved-Span Ownership, 2026-03-15
- [x] Verified from the latest sec 6 log in Python that the current resolved south correction row `318248.413,6033225.098 -> 319056.533,6033193.856` only spans the east half of the section, while the SouthWest LSD station sits about `403.422 m` west of that span.
- [x] Confirmed from the historical log that the generic `midpoint-kind` boundary-station path already produced the exact expected SouthWest LSD target `317844.343,6033240.719` before later correction-zero special-case overrides replaced it.
- [x] Patched the correction-zero special path so it only runs when the resolved quarter south correction row actually covers the LSD station, otherwise it falls back to the generic station-ranked boundary path.
- [x] Patched the ordinary `L-USEC-0` / `L-USEC-2012` tie-in trim to treat plain `L-USEC` north/south rows as anchor geometry only, so sec 6 vertical tie-ins can qualify for south trimming without turning `L-USEC` into a trim target.

### Review
- `dotnet build .\\src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` succeeded with 0 warnings and 0 errors.
- `dotnet build .\\src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with 0 warnings and 0 errors.
- `dotnet .\\src\\AtsBackgroundBuilder.DecisionTests\\bin\\Release\\net8.0\\AtsBackgroundBuilder.DecisionTests.dll` reported `Decision tests passed.`
- Python verification confirmed the sec 6 resolved-row compatibility gate should reject the current `403.422 m` west extrapolation, and that the current special-path targets are still `0.423 m` and `5.045 m` away from the exact expected LSD point.

## ATS Sec 6 LSD Priority And Tie-In Trim Gate, 2026-03-15
- [x] Re-check latest sec 6 log ownership for quarter south, LSD outer targets, and plain USEC tie-in rows.
- [x] Patch LSD endpoint selection so the final generic CORRZERO/SEC station path runs before any correction-specific downgrade.
- [x] Patch ordinary USEC tie-in trimming so it can run off live correction geometry even when seam fitting itself reports zero seams.
- [x] Rebuild ATS + decision tests and verify the sec 6 target geometry with Python before sending back.

### Review
- Root cause 1: the LSD rule-matrix was still downgrading sec 6 SouthWest straight to fallback kinds before the generic final CORRZERO/SEC station pass could run, even though that generic pass historically produced the exact midpoint target.
- Root cause 2: the ordinary L-USEC-0 / L-USEC-2012 tie-in trim was trapped inside `if (seamCount > 0)`, so in the current no-seam sec 6 runs it never executed at all despite live `L-USEC-C-0` geometry being present in the model.
- Fix: run the generic boundary-station targeting first, only attempt correction-specific resolved/interior targeting after that fails, and only downgrade to fallback kinds if both higher-fidelity paths fail. Separately, run the ordinary tie-in trim regardless of seamCount, but only when live correction targets are actually present in the buffered windows.
- Verification: `dotnet build` on `src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln` succeeded with 17 existing nullable warnings and 0 errors; `dotnet build` on `src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj` succeeded with 0 warnings and 0 errors; `Decision tests passed.`
- Python verification from the current sec 6 correction row geometry:
  - `L-USEC-2012` west tie-in target resolves to `317440.273000,6033256.340773`, within `0.001 m` of the requested `317440.273,6033256.340`.
  - `L-USEC-0` east tie-in target resolves to `319056.533000,6033193.856000`, exactly matching the requested `319056.533,6033193.856`.
  - Historical good sec 6 LSD midpoint targets remain `317844.343,6033240.719` (SW) and `318652.475,6033209.477` (SE), and the patch restores priority back toward that generic final LSD path.

## ATS Sec 6 Final Tie-In Trim Placement, 2026-03-15
- [x] Confirm the newest sec 6 run still leaves the same four three-band section rows unchanged and has no `CorrectionLine:` diagnostics.
- [x] Verify the ordinary USEC tie-in trim was still hidden behind the correction cadence/seam gate.
- [x] Move the ordinary tie-in trim into the unconditional final cleanup stage so it runs whenever live correction targets exist.
- [x] Rebuild, rerun decision tests, and verify the sec 6 target stations with Python.

### Review
- Root cause: the sec 6 overhang trim was still implemented inside `ApplyCorrectionLinePostBuildRules`, but that whole function returns early whenever there are no cadence-aligned seam sections in scope. In the current sec 6 runs there are live correction rows, but no cadence-aligned seam build for that pass, so the trim never executed.
- Fix: remove the ordinary `L-USEC-0` / `L-USEC-2012` tie-in trim call from `Plugin.RoadAllowance.CorrectionLinePostProcessing.cs` and call it directly from `FinalizeRoadAllowanceCleanup` after correction post-processing. That makes the trim depend on live final geometry instead of correction seam discovery.
- Verification: `dotnet build` on `src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln` succeeded with 17 existing nullable warnings and 0 errors; `dotnet build` on `src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj` succeeded with 0 warnings and 0 errors; `Decision tests passed.`
- Python verification from the resolved sec 6 correction south row:
  - west tie-in station target resolves to `317440.273000,6033256.340773`, within `0.001 m` of the requested `317440.273,6033256.340`.
  - east tie-in station target resolves to `319056.533000,6033193.856000`, exactly matching the requested `319056.533,6033193.856`.


## ATS Sec 6 Duplicate 5.02m Correction-Band Survivor, 2026-03-15
- [x] Confirm the latest sec 6 remaining overhang is a plain USEC chain exactly one correction inset below the live `L-USEC-C-0` row.
- [x] Extend the final correction consistency pass to erase duplicate plain USEC rows that seed from strong overlap with `L-USEC-C-0` and propagate through collinear endpoint-touching neighbors.
- [x] Rebuild, run decision tests, and verify the survivor geometry numerically with Python.

### Review
- Root cause: the remaining sec 6 overhang was no longer a tie-in-trim problem. It was a plain USEC duplicate chain (`12643A1 -> 12643A0`) sitting exactly one inset (`5.02 m`) south of the live correction-zero row. The existing final consistency pass only promoted seam-overlap USEC rows to `L-USEC-C`; it never pruned the inverse duplicate case.
- Fix: `EnforceFinalCorrectionOuterLayerConsistency` now seeds duplicate suppression from plain USEC rows that strongly overlap a live `L-USEC-C-0` row at `5.02 m`, then propagates across collinear endpoint-touching neighbors on the same inset side and erases that duplicate chain before ordinary promotion runs.
- Verification: decision tests passed. Source build compiled successfully, but the deployed copy step failed because AutoCAD locked `build/net8.0-windows/AtsBackgroundBuilder.dll`.
- Python verification from the latest sec 6 log geometry:
  - seed duplicate row midpoint offset from live `L-USEC-C-0`: `5.020327 m`
  - west extension row midpoint offset from live `L-USEC-C-0`: `5.019766 m`
  - seed row projected overlap with live `L-USEC-C-0`: `808.724 m`
  - extension row touches the seed chain at endpoint distance `0.0 m`
## 2026-03-15 - Sec 6 ordinary 0/20.12 extension chain cleanup
- [x] Re-trace sec 6 against the live log and verify the remaining ordinary rows are still being bucketed as L-USEC2012 / L-USEC3018 around the correction line.
- [x] Replace single-row ghost detection with deterministic seed-plus-continuation detection for ordinary rows offset one correction inset (5.02 m) from live L-USEC-C-0.
- [x] Apply that same ghost-chain suppression in both three-band normalization and ordinary USEC tie-in overhang trimming so section lines and later endpoint cleanup see the same geometry.
- [x] Rebuild, run decision tests, and verify the exact sec 6 chain numerically with Python before handoff.
### Review
- Root cause: the remaining sec 6 failure still matched the user's clue about L-USEC-0 / L-USEC-2012 extension. The code only recognized the direct ghost row that strongly overlapped L-USEC-C-0; it missed the attached ordinary continuation, so the bad chain survived and kept influencing later section-line and LSD cleanup.
- Fix: CorrectionInsetGhostRowClassifier now finds a ghost chain deterministically: direct seed rows must strongly overlap live L-USEC-C-0 at the correction inset, and endpoint-connected continuations are removed only when they stay on that same inset side/band. Three-band normalization and ordinary USEC tie-in trim now both use that same chain detection.
- Verification: Python on the current sec 6 coordinates selected only 12643A1 as the seed and only 12643A0 as its continuation; neighboring ordinary rows 12646F5, 12646F6, and 12646B0 stayed out. Decision tests passed. dotnet build for the ATS solution succeeded with 17 existing nullable warnings and 0 errors, and both DLL copies updated.

## 2026-03-15 - Correction-adjoining ordinary 0/20 endpoint projection
- [x] Trace the wrong L-USEC-2012 sec 6 endpoint at 317440.073,6033251.323 against the live correction-zero trend and confirm it sits one correction inset (5.02 m) below the intended target.
- [x] Add a deterministic correction-zero companion projection for the generic  /20 dangling-endpoint connector so correction-adjoining ordinary endpoints prefer the nearby L-USEC-C-0 trend instead of the ordinary parallel row.
- [x] Cover the projection rule in decision tests and verify the exact sec 6 point numerically with Python before rebuilding ATS.

### Review
- Root cause: the remaining sec 6 L-USEC-2012 / L-USEC-0 misses were still being committed in the generic ConnectDanglingUsecZeroTwentyEndpoints pass. That pass only knew about ordinary  /20 targets, so it would happily stop on a same-band ordinary row even when that row sat exactly one correction inset south of the real L-USEC-C-0 trend.
- Fix: the generic  /20 pass now collects live L-USEC-C-0 segments and, after it picks an ordinary target, projects that target onto a nearby correction-zero companion trend when the chosen ordinary point is about 5.02 m off the correction line. That preference is deterministic and local; it only nudges already-selected ordinary targets onto the correction-zero companion and then skips the ordinary terminator cap for that endpoint.
- Verification: Python projection on the exact sec 6 case moved 317440.073,6033251.323 to 317440.275167,6033256.340689, within  .003 m of the expected 317440.273,6033256.340. Decision tests passed. ATS solution build succeeded with 17 existing nullable warnings and 0 errors, and both DLL copies updated.

## 2026-03-15 - Section-6 correction companion snap for ordinary 0/20 endpoints
- [x] Re-check the latest log and confirm sec 6 quarter south ownership already resolves to L-USEC-C-0, so the remaining ordinary endpoint miss is a later section-line pass.
- [x] Add a narrow final cleanup pass that snaps L-USEC-0 / L-USEC-2012 endpoints directly onto live L-USEC-C-0 when they sit one correction inset off that row.
- [x] Run Python, decision tests, and a full ATS build before handoff.

### Review
- Root cause: the sec 6 misses were no longer an LSD or quarter-owner problem. The live quarter geometry already resolved to the correct L-USEC-C-0 row, but the remaining ordinary L-USEC-0 / L-USEC-2012 endpoints never had a final correction-specific snap, so they could survive one inset (~5.02 m) south on the ordinary parallel row.
- Fix: EnforceZeroTwentyEndpointsOnCorrectionZeroBoundaries now runs in final cleanup after the generic 0/20 passes and before duplicate pruning. It only targets ordinary  /20 endpoints inside the buffered window that are not already on L-USEC-C-0 and whose live endpoint projects one correction inset onto a correction-zero trend.
- Verification: Python still projects the reported west sec 6 miss from 317440.073,6033251.323 to 317440.272705,6033256.340785 (5.021757 m move). Decision tests passed, including the new current-endpoint companion-projection coverage. dotnet build for the ATS solution succeeded with 17 existing nullable warnings and 0 errors, and the deployed DLL updated.
## 2026-03-15 - Move correction companion snap to final geometry stage
- [x] Re-check the latest sec 6 log and confirm the early endpoint cleanup was not the final owner because correction-line post-processing still rebuilt the ordinary  /20 geometry afterward.
- [x] Move the deterministic L-USEC-0 / L-USEC-2012 correction-zero companion snap into FinalizeRoadAllowanceCleanup so it runs on the real final section geometry before quarter/LSD drawing.
- [x] Rebuild and confirm the deployed DLL updated for the next sec 6 rerun.

### Review
- Root cause: the earlier correction-zero companion snap lived in RunRoadAllowanceEndpointCleanup, but sec 6's correction-adjoining ordinary rows are rebuilt later by ApplyCorrectionLinePostBuildRules inside final cleanup. That meant the snap could run on pre-final geometry and then be silently undone before the user ever saw the result.
- Fix: the same deterministic snap now runs after correction-line post-processing and final correction consistency, immediately before quarter/LSD generation. I also made its log emit every run so the next rerun will show whether it actually scanned and moved sec 6 endpoints.
- Verification: decision-test build succeeded with 0 warnings / 0 errors, ATS solution build succeeded with the same 17 existing nullable warnings / 0 errors, and the deployed DLL updated to 2026-03-15 11:18:59 PM.
## 2026-03-16 - Instrument sec 6 owner trace instead of inferring from final symptoms
- [x] Add targeted endpoint-trace tags for the two remaining sec 6 ordinary  /20 misses.
- [x] Snapshot those targets after each endpoint-cleanup and final correction cleanup stage.
- [x] Rebuild so the next rerun exposes the first owning pass directly in the log.

### Review
- Root cause: I still did not have the actual move owner for the sec 6 L-USEC-0 / L-USEC-2012 misses. The existing log only proved late quarter geometry, not which cleanup stage first moved or rebuilt those ordinary endpoints.
- Fix: the trace helper now recognizes the two sec 6 wrong/expected endpoint targets, and TraceTargetLayerSegmentState runs after each endpoint-cleanup stage plus the final correction post-processing stages. The next log should show exactly when those endpoints first appear on the wrong row.
- Verification: decision-test build succeeded with 0 warnings / 0 errors, ATS solution build succeeded with the same 17 existing nullable warnings / 0 errors.
## 2026-03-16 - Added correction post-build subpass tracing for sec 6 vertical ordinary endpoints
- [x] Confirm wrong sec 6 ordinary endpoints first appear after correction post-build.
- [x] Add stage snapshots around inner endpoint snap, redundant-band prune, quarter hard, and blind hard inside correction post-build.
- [x] Add direct TRACE-SEC6 logging when correction post-build moves an ordinary vertical target endpoint.
- [ ] Rerun 63-12-6 and capture the first subpass that creates sec6-0-wrong / sec6-2012-wrong.


## 2026-03-16 - Fixed sec 6 double-adjust in correction post C-0 target snap
- [x] Trace confirmed ordinary sec 6 verticals were moved twice in corr-post-c0-target-adjust.
- [x] First move landed on the expected point; second move was ~5.02m farther and created the wrong sec 6 endpoint.
- [x] Keep only the closest seam-facing snap per vertical target endpoint within the correction post-build pass.
- [x] Verified with Python against the live sec 6 trace and added decision coverage.

## 2026-03-16 - Survey L-SEC LSD midpoint regression in 59-10-5
- [x] Trace the fresh 59-10-5 run in 	race-build and confirm the survey miss is coming from the LSD rule-matrix outer target, not the later hard-boundary pass.
- [x] Patch rule-matrix boundary collection so L-SEC polyline segments populate the SEC pool the same way later endpoint passes already do.
- [x] Add a narrow surveyed-SEC preference so no-offset survey cases choose the closer SEC target before TWENTY/ZERO, without changing correction L-USEC overrides.
- [x] Rebuild ATS + decision tests and verify the survey-kind selection with Python before handoff.

### Review
- Root cause: the LSD rule-matrix collector was still reading only one open segment per entity, so survey L-SEC polyline road-allowance segments never populated the SEC pool. That left sec 29 falling straight to TWENTY/SEC, and Python confirmed those survey misses were all the same full 10.06 m outer jump.
- Fix: mirror the later endpoint pass and collect per-segment Line/Polyline geometry inside the rule-matrix collector, then apply a narrow survey tie-break that only prefers a local SEC target when it is materially closer than the chosen TWENTY/ZERO target. Correction overrides still keep the L-USEC path first.
- Verification: dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release -p:NuGetAudit=false -v minimal succeeded with 0 warnings / 0 errors. dotnet build src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release -p:NuGetAudit=false -v minimal succeeded with 0 warnings / 0 errors. Decision tests passed.
- Python verification from the fresh 	race-build log showed the live sec 29 TWENTY/SEC survey misses were a consistent 10.06 m full-offset move (1264BC6, 1264BC9, 1264BCA, 1264BCB, 1266515, 1266516, 1266517), matching the no-offset survey symptom this fix now targets.## 2026-03-16 - Survey L-SEC road-allowance midpoint pairing correction
- [x] Re-trace the latest survey-only sec 29 run after the first SEC-pool fix and confirm the remaining miss is full boundary-to-boundary 20.11 m movement or raw SEC preservation, not an L-USEC correction override.
- [x] Replace raw survey-sec preservation with a survey-only midpoint target that pairs adjacent L-SEC boundaries about one RoadAllowanceSecWidthMeters apart and uses their midpoint.
- [x] Rebuild ATS, rerun decision tests, and verify the exact sec 29 half-width midpoint numbers in Python before handoff.

### Review
- Root cause: the first survey patch fixed SEC collection but still treated survey rows like ownership rows. In a pure surveyed run the rule-matrix either preserved the endpoint on a raw L-SEC boundary or moved it the full 20.11 m to the opposite SEC boundary. The user-visible requirement was the midpoint between those paired L-SEC rows.
- Fix: add a survey-only midpoint target inside the rule-matrix. When the active boundary pool contains only SEC, it now projects the local station onto adjacent SEC rows, finds the paired boundaries about one RoadAllowanceSecWidthMeters apart, and targets the midpoint between them before any generic TWENTY/ZERO station fallback.
- Verification: dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release -p:NuGetAudit=false -v minimal succeeded with 0 warnings / 0 errors. dotnet build src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release -p:NuGetAudit=false -v minimal succeeded with 0 warnings / 0 errors. Decision tests passed.
- Python verification from the latest sec 29 survey log:
  - 126A395 wrong full move 20.110 m -> midpoint target 599542.157,5999519.151 (10.055 m half move)
  - 126A398 wrong full move 20.109 m -> midpoint target 599982.211,5998310.723 (10.055 m half move)
  - 126A399 wrong full move 20.109 m -> midpoint target 599558.936,5998713.703 (10.054 m half move)
  - 126A39A wrong full move 20.109 m -> midpoint target 600791.055,5998329.798 (10.055 m half move)## 2026-03-16 - Survey L-SEC midpoint gate must be station-local
- [x] Re-trace the latest sec 29 survey run and confirm the live failures still fall through as `station-kind` rather than `survey-sec-midpoint`.
- [x] Replace the section-wide "pure SEC pool" gate with a station-local projected-candidate gate so survey midpoint logic can still run when unrelated non-SEC rows exist elsewhere in the buffered window.
- [x] Rebuild ATS, rerun decision tests, and verify the latest sec 29 wrong targets still represent full-width 20.11 m moves that should collapse to 10.05 m midpoints.

### Review
- Root cause: the first survey midpoint gate was checking the whole section window for any ZERO/TWENTY/BLIND/CORRZERO pools. That was too broad. Sec 29 still had unrelated non-SEC rows elsewhere in scope, so the survey midpoint branch never ran even though the actual road-allowance station had only paired L-SEC boundaries.
- Fix: add `SurveySecRoadAllowanceMidpointPolicy` and make the gate station-local. The rule-matrix now only suppresses the survey midpoint when a projected ZERO/TWENTY/CORRZERO candidate exists at that same endpoint station; otherwise a `ZERO/SEC` or `TWENTY/SEC` survey case is allowed to midpoint between the paired L-SEC rows.
- Verification: `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with 0 warnings / 0 errors. `dotnet build src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release -p:NuGetAudit=false -v minimal` succeeded with 0 warnings / 0 errors. `Decision tests passed.` Full solution build succeeded with 0 warnings / 0 errors.
- Python verification from the latest sec 29 log confirmed the still-wrong survey cases are all full-width moves that should midpoint instead:
  - `126AE5A`: full move `20.109 m` -> expected midpoint `602260.000,5950991.927`
  - `126AE5C`: full move `20.109 m` -> expected midpoint `603883.022,5951031.794`
  - `126AE5D`: full move `20.109 m` -> expected midpoint `602697.701,5949785.111`
  - `126AE5E`: full move `20.110 m` -> expected midpoint `602277.738,5950187.476`
  - `126AE5F`: full move `20.109 m` -> expected midpoint `603499.289,5949804.487`
  - `126AE60`: full move `20.109 m` -> expected midpoint `603900.933,5950226.898`
## 2026-03-16 - Survey L-SEC owner correction: use the single-line anchor midpoint, not a paired-line midpoint
- [x] Re-check the survey sec 29 rule after the user clarified the target is the midpoint anchor on the actual L-SEC segment, not the midpoint between two L-SEC road-allowance sides.
- [x] Remove the paired-line midpoint branch and route pure survey `ZERO/SEC` and `TWENTY/SEC` cases onto the existing quarter outer-anchor path instead.
- [x] Rebuild ATS, rerun decision tests, and verify from Python that the affected sec 29 cases are exactly the `ZERO/SEC` and `TWENTY/SEC` station-kind crossings while `BLIND/SEC` cases stay on the anchor path.

### Review
- Root cause: I mis-modeled the surveyed road-allowance owner. The correct survey target is the midpoint anchor on the single L-SEC boundary segment selected by the quarter, not the midpoint across two parallel L-SEC rows.
- Fix: replace the survey paired-line midpoint gate with `SurveySecRoadAllowanceAnchorPolicy`. For survey `ZERO/SEC` and `TWENTY/SEC` cases with no projected local ZERO/TWENTY/CORRZERO owner at the endpoint station, the rule-matrix now skips station-kind targeting and falls through to the existing quarter outer-anchor target. That preserves `BLIND/SEC` behavior and leaves L-USEC correction logic alone.
- Verification: `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` succeeded with 0 warnings / 0 errors. `Decision tests passed.` Both `src\AtsBackgroundBuilder\bin\x64\Release\net8.0-windows\AtsBackgroundBuilder.dll` and `build\net8.0-windows\AtsBackgroundBuilder.dll` updated at the same timestamp.
- Python verification from the latest sec 29 log confirmed the classification split this fix is targeting: `12649A5`, `12649A7`, `12649A8`, `12649A9`, `12649AA`, and `12649AB` are still `ZERO/SEC` or `TWENTY/SEC` station-kind crossings that should flip to the survey anchor path, while `12649A4` and `12649A6` are `BLIND/SEC` and already stay on the anchor path.
## 2026-03-16 - Survey L-SEC sec 29 fix: preserve or snap to SEC at station, do not promote to survey anchor
- [x] Trace the exact sec 29 example `600360.453,5999537.442 -> 601188.609,5999555.943` and confirm the rule-matrix was moving it off the already-correct `L-SEC` point.
- [x] Replace the survey-anchor override with a survey `SEC`-target path that preserves or projects onto the live `SEC` segment at the current station.
- [x] Rebuild ATS, rerun decision tests, and verify the reported sec 29 overmove is the full `20.11064 m` drift from the correct point.

### Review
- Root cause: the survey-anchor interpretation was still wrong. In the latest sec 29 log, line `126A5B8` already started with outer endpoint `601168.503,5999555.511`, which matches the user's expected point exactly, and the rule-matrix then moved it to `601188.609,5999555.943` under `survey-sec-anchor`.
- Fix: replace that override with `SurveySecRoadAllowanceSecTargetPolicy`. For survey `ZERO/SEC` and `TWENTY/SEC` cases with no projected local ZERO/TWENTY/CORRZERO owner, the rule-matrix now uses `SEC` as the station target instead of forcing a survey anchor. That preserves endpoints already on the correct `L-SEC` line and still allows a snap onto the live `SEC` segment when needed.
- Verification: `dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal` succeeded with 0 warnings / 0 errors. `Decision tests passed.` Both deployed and source DLLs updated at the same timestamp.
- Python verification: the reported wrong target `601188.609,5999555.943` sits `20.11064 m` away from the expected `601168.503,5999555.511`. The logged pre-move outer endpoint for `126A5B8` already matched the expected point exactly before the bad override ran.

# Follow-up (WLS Bunny Tracks + Photo Label Green, 2026-03-16)

- [x] Verify why bunny-related findings fall back to unidentified tracks instead of Snowshoe Hare Tracks.
- [x] Force photo labels to use AutoCAD GREEN instead of an RGB green variant.
- [x] Rebuild the WLS plugin to verify the changes compile cleanly.

## Review (WLS Bunny Tracks + Photo Label Green, 2026-03-16)

- Root cause: the findings lookup workbook recognizes abbit|hare tracks, but not unny, so Bunny Tracks was falling through to the generic unidentified-track fallback.
- Fix: updated FindingsDescriptionStandardizer so preprocessing and canonicalization normalize unny / unnies through the existing rabbit-hare rule path, which resolves to Snowshoe Hare Tracks.
- Fix: updated PhotoLayoutHelper so photo labels use AutoCAD ACI 3 (GREEN) instead of a raw RGB green value.
- Verification: dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release --no-restore -p:OutDir=C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\artifacts\verify\WildlifeSweeps\ succeeded with 0 warnings / 0 errors. A normal Release build was blocked only because the live in\Release\net8.0-windows\WildlifeSweeps.dll is currently locked by another process.
- Verification: targeted checks confirmed Bunny Tracks now normalizes to abbit tracks, and the lookup workbook already contains the abbit tracks -> Rabbit Tracks rule that canonicalizes to Snowshoe Hare Tracks.


## 2026-03-16 - Make blind BLIND/SEC LSD ownership explicit
- [x] Trace the latest BLIND/SEC fallback-anchor path and confirm it is acting as an implicit quarter-anchor owner rather than a true rescue move.
- [x] Add a deterministic blind-owner policy so the LSD rule-matrix can choose the intended blind anchor path before the generic fallback branch.
- [x] Keep existing station-kind behavior for non-blind and survey/correction cases while removing the normal-operation dependence on generic fallback for blind sections.
- [x] Add decision coverage for the new blind-owner policy and its gating.
- [x] Run decision tests and record the review notes with any residual risks.

### Review
- Root cause: the frequent BLIND/SEC fallback hits were not behaving like true rescue moves. In the latest log they were overwhelmingly source=fallback-anchor moved=False, which means the generic rule-matrix was failing to resolve a station target and then dropping into the anchor fallback just to preserve the intended blind quarter endpoint.
- Fix: added BlindSecRoadAllowanceAnchorPolicy and routed the LSD rule-matrix through an explicit blind-owner path before the generic fallback branch. The rule-matrix now records that path separately as lind-sec-anchor, so blind sections no longer depend on the generic fallback branch during normal operation.
- Verification: dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore -p:NuGetAudit=false passed. dotnet msbuild src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -t:Compile -p:Configuration=Release -p:Platform=x64 -p:NuGetAudit=false -verbosity:minimal passed. A full dotnet build of the ATS solution is currently blocked only because AutoCAD is locking uild\net8.0-windows\AtsBackgroundBuilder.dll during the post-build copy step.
- Residual risk: this change intentionally preserves blind-anchor behavior rather than replacing it with a new blind station solver. It removes dependence on the generic fallback branch and makes the owner path explicit, but if you want blind sections to resolve from live blind geometry instead of anchors, that would be the next upstream refactor.## 2026-03-16 - Restore unsurveyed BLIND/SEC LSD midpoint targeting
- [x] Re-trace the user-reported unsurveyed blind miss and confirm the wrong landing coordinate is the old allback-anchor target, not a corrected station midpoint.
- [x] Replace the blind anchor shortcut with a station-local BLIND/SEC midpoint target that resolves halfway between the projected 30.18 blind row and 20.12 sec row.
- [x] Keep generic anchor fallback available only when the paired blind/sec midpoint cannot be resolved.
- [ ] Rebuild ATS, rerun decision tests, and record the verification plus any remaining runtime checks for the user.

### Review
- Root cause: I treated the frequent BLIND/SEC anchor fallback as the intended blind owner. The user's exact coordinates showed that anchor target was still wrong for unsurveyed blind sections; those LSDs should land on the half-width midpoint between the projected blind (30.18) and sec (20.12) rows at the active station.
- Fix: replace BlindSecRoadAllowanceAnchorPolicy with BlindSecRoadAllowanceMidpointPolicy, add a paired blind/sec midpoint resolver inside the rule-matrix, and log the successful path as lind-sec-midpoint. The generic quarter-anchor path remains only as the final fallback.
- Verification: pending.### Verification Update
- dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal succeeded with 0 warnings / 0 errors.
- dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build -p:NuGetAudit=false passed.
- Next runtime check: rerun the affected unsurveyed blind section build and confirm source=blind-sec-midpoint appears in 	race-build\net8.0-windows\AtsBackgroundBuilder.log where the old run showed source=fallback-anchor on the wrong coordinate.### Follow-up Review Update
- New evidence from uild\net8.0-windows\AtsBackgroundBuilder.log at 2026-03-16 7:54:24 PM: the patched run did execute, but lindSecMidpointTargets=0 and the exact sec 30 miss still matched the blind anchor (318172.054,6041341.703).
- Follow-up fix: replace the failed projected-line midpoint attempt with a deterministic midpoint between the blind outer anchor and the matching section-side anchor on the same quarter face.
- Verification: dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal succeeded with 0 warnings / 0 errors. dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build -p:NuGetAudit=false passed.
- Next runtime check: rerun the sec 30 blind case and confirm the newer uild\net8.0-windows\AtsBackgroundBuilder.log shows lindSecMidpointTargets > 0 and your example no longer stays on 318172.054,6041341.703.### Follow-up Review Update
- Replaced the bad BLIND/SEC anchor-to-section midpoint with a guarded station-local midpoint between the current blind endpoint and a projected TWENTY companion target when the gap matches the expected 30.16 - 20.11 spacing.
- Verified dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal and dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build -p:NuGetAudit=false both pass after the change.
- Source and deployed AtsBackgroundBuilder.dll hashes match, so the next AutoCAD rerun will exercise this fix.
- [x] Investigated sec 30/31 BLIND/SEC miss and confirmed final rule-matrix was falling back to quarter anchors because same-station BLIND targeting cannot produce the required along-boundary midpoint.
- [x] Replaced the bogus BLIND->TWENTY midpoint attempt with a live short-blind-segment midpoint resolver in the rule-matrix so BLIND/SEC vertical LSD outers read post-cleanup blind geometry.
- [x] Verified with `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal`, `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build -p:NuGetAudit=false`, and matching DLL hashes for src/build outputs.
- [x] Review: The root bug was not just pass order; the final rule-matrix was using stale quarter-anchor fallback after a same-station search that can only rediscover the current blind endpoint. The fix now midpoint-snaps from the live short blind segment when BLIND/SEC applies.- [x] Re-traced the fresh 8:36 PM build log after the failed short-blind midpoint attempt and confirmed `blindSecMidpointTargets=0`; sec 30/31 were still preserving the outer blind anchor.
- [x] Replaced the BLIND/SEC midpoint rule with an inward-boundary intersection midpoint: for vertical BLIND/SEC LSD outers, midpoint between the current blind-row anchor and the first inward vertical hard-boundary intersection on that blind row.
- [x] Re-verified with `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal`, `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build -p:NuGetAudit=false`, and matching DLL hashes for src/build outputs.- [x] Simplified BLIND/SEC handling by removing the dead blind midpoint rule-matrix experiments and adding a deterministic post-rule-matrix blind-boundary midpoint pass for vertical LSD outers.
- [x] Verified with `dotnet build src\\AtsBackgroundBuilder\\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal`, `dotnet run --project src\\AtsBackgroundBuilder.DecisionTests\\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build -p:NuGetAudit=false`, and matching DLL hashes for src/build outputs.
## 2026-03-16 - Add AutoCAD ATS harness
- [x] Confirm the ATS Excel command can run non-interactively from AutoCAD Core Console with an env-provided workbook path.
- [x] Add a batch ATS command that exports DXF after a successful build so console automation can produce reviewable geometry artifacts.
- [x] Add helper scripts to generate ATS workbooks from JSON specs, review DXF geometry, and orchestrate an end-to-end console run.
- [ ] Verify with solution build/tests and one dry-run harness execution against a local DWG/workbook.

### Review
- Goal: make ATS runs reproducible outside the interactive UI so we can generate a workbook, run AutoCAD on a known DWG, export DXF, and assert geometry from a script.
- Implementation: added ATSBUILD_XLS_BATCH in the plugin, kept workbook generation/review in Python stdlib scripts, and added scripts\atsbuild_harness.ps1 to drive accoreconsole.exe, capture logs, export DXF, and optionally run DXF checks.
- Verification: pending runtime pass.### Verification Update
- dotnet build src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release -p:NuGetAudit=false -v minimal compiled fresh source outputs but still failed its final copy into uild\net8.0-windows because AtsBackgroundBuilder.dll is locked by AutoCAD process 29564.
- dotnet run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build -p:NuGetAudit=false passed.
- Smoke harness run succeeded with scripts\atsbuild_harness.ps1 -DwgPath C:\Users\jesse\OneDrive\Desktop\00666-16-PAD-R10.dwg -WorkbookPath src\AtsBackgroundBuilder\REFERENCE ONLY\ATSBUILD_Simple_Input.xlsx -OutputDir data\atsbuild-harness-smoke2; it produced rtifacts\output.dxf, captured plugin/console logs, and confirmed ATSBUILD_XLS_BATCH exit stage: completed (ok) even though AutoCAD returned a non-zero console exit code.
- Review harness run also executed end to end with -DefaultBlindMidpoints and correctly failed the case with a JSON report at data\atsbuild-harness-smoke3\artifacts\review-report.json, proving the DXF assertion step can gate the run.
- [x] Verify with solution build/tests and one dry-run harness execution against a local DWG/workbook.### Batch Verification Update
- Used the reference drawing at src\AtsBackgroundBuilder\REFERENCE ONLY\test drawing.dwg and confirmed the correct ATS zone is 11; a probe zone 12 run failed because the section index files Master_Sections.index_Z12.jsonl/csv do not contain these sections.
- Generated one workbook per range via scripts\atsbuild_generate_workbook.py from JSON specs for Twp 63 / Rge 1..12 / W6M, with a single row quarter=ALL, section blank so ATS expanded each workbook into 36 section requests.
- Ran scripts\atsbuild_harness.ps1 sequentially for ranges 1 through 12 with -DefaultBlindMidpoints against the reference drawing and captured artifacts under data\atsbuild-twp63-r1-12-testdrawing.
- Result: all 12 runs completed, exported DXF, and passed the blind-midpoint review (checkedBlindLines=72, lindFailures=0, lindAmbiguous=0 in every run). Summary saved to data\atsbuild-twp63-r1-12-testdrawing\summary.json.
- Shared warning profile: each successful run still logged Import failures: 2 plus three Map 3D Importer not available warnings, but these did not block ATS completion or the geometry review.### W5 Batch Verification Update
- Reused src\AtsBackgroundBuilder\REFERENCE ONLY\test drawing.dwg for Twp 63 / Rge 1..12 / W5M and confirmed the correct ATS zone is again 11; the zone 12 probe did not complete on this drawing.
- Generated one workbook per range from JSON specs and ran scripts\atsbuild_harness.ps1 sequentially with -DefaultBlindMidpoints, writing artifacts under data\atsbuild-twp63-r1-12-w5-testdrawing.
- Result: all 12 W5 runs completed, exported DXF, and passed the blind-midpoint review. Review coverage varied by range (checkedBlindLines =  , 54, 60, or 72 depending on the geometry present), but lindFailures=0 and lindAmbiguous=0 in every run. Summary saved to data\atsbuild-twp63-r1-12-w5-testdrawing\summary.json.
- Shared warning profile: each W5 run also logged Import failures: 2 plus three Map 3D Importer not available warnings, matching the W6 run behavior and not blocking ATS completion or review success.- 2026-03-17: Full AutoCAD aligned-dimension pass for N.W. 8-57-18-5
  - Added leader-based rendered aligned-dimension text alignment in LabelPlacer and a final ligned_dimension_text_finalize pass in ATSBUILD.
  - Verified with full AutoCAD/Map 3D batch on C:\AtsHarness\manual1: imported 17 dispositions, placed 13 labels, exported DXF successfully.
  - Runtime log confirmed ATSBUILD_XLS_BATCH stage: aligned_dimension_text_finalize and Aligned dimension rendered-text pass: inspected=9, adjusted=0 on the fresh rebuilt DLL (2026-03-17 7:26:48 PM).
  - Residual note: raw DXF anonymous-block parsing does not mirror AutoCAD's runtime rotation state one-to-one for aligned dimensions, so runtime verification is the trusted source for this case.
- 2026-03-17: Added FullAutoCAD mode to scripts/atsbuild_harness.ps1.
  - Uses a no-space launcher workspace under C:\AtsHarness, copies the DWG/workbook there, and launches cad.exe /b against the ATS batch command.
  - Auto-detects ATSBUILD_XLS_BATCH completion from the appended plugin log, copies the DXF back to the artifact folder, and force-closes the GUI worker only after a successful batch or timeout.
  - Verified with N.W. 8-57-18-5 at data\atsbuild-harness-fullacad-smoke-nw8-57-18-5-rerun: batch completed, DXF exported, launcher workspace cleaned up, ligned_dimension_text_finalize ran, Labels placed: 13, Imported dispositions: 17.

## 2026-03-18 - Fix WLS LSD table location east/west flip
- [x] Trace the WLS table ATS-location path and confirm whether the reported `4-6-63-17-5 -> 1-6-63-17-5` miss comes from LSD numbering or a misbuilt local section/quarter frame.
- [x] Replace any unstable polygon-orientation heuristic in the WLS ATS location resolver/table path with a deterministic east/west-first frame builder.
- [x] Rebuild WLS and verify the corrected frame logic against the reported east/west flip scenario.

### Review
- Root cause: WLS was deriving local ATS frames from whichever polygon edge happened to win the `longest edge` tie. On square/near-square section or quarter polylines that start on a vertical edge, that rotates the local frame, which can flip the resolved quarter/LSD used in the summary table.
- Fix: added `wls_program/src/WildlifeSweeps/AtsPolygonFrameBuilder.cs` and routed both `AtsQuarterLocationResolver` and `CompleteFromPhotosService` through the shared builder so east/west is chosen from the most east-west edge instead of an arbitrary longest-edge tie.
- Verification: `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release --no-restore -p:OutDir="C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\artifacts\verify\WildlifeSweeps\"` passed with 0 warnings / 0 errors. A normal Release build is currently blocked only because `wls_program\src\WildlifeSweeps\bin\Release\net8.0-windows\WildlifeSweeps.dll` is locked by AutoCAD process 18912.
- Verification: a focused synthetic check reproduced the old flaw on a square whose first edge is vertical (`OLD quarter=NW lsd=13`) and confirmed the new frame builder preserves the correct ATS orientation (`NEW quarter=SW lsd=4`).
- Next runtime check: rerun the affected WLS table export and confirm finding `#16` now resolves to `4-6-63-17-5` instead of `1-6-63-17-5`.

## 2026-03-18 - Fix WLS lookup mappings for Squirrel Tracks and Beaver Den
- [x] Trace the active WLS findings lookup path and confirm whether the phrases are missing from the workbook or being overridden by code-side canonicalization.
- [x] Update the authoritative lookup workbook copies so `Squirrel Tracks` resolves specifically and `Beaver Den` is accepted as-is.
- [x] Verify the first-match priority behavior after the workbook update and record the result.

### Review
- Root cause: the workbook had no squirrel-track-specific regex, so `Squirrel Tracks` was falling through to the generic track fallback regex. `Beaver Den` was also missing a specific beaver-den rule, so the generic `den` regex was converting it into the inactive/possible den bucket.
- Fix: added higher-priority `RecognitionRegex` rows for squirrel tracks and beaver den, and added the missing `Squirrel / Tracks` row to `SpeciesFindingTypes`.
- Updated workbook copies: `wls_program\wildlife_parsing_codex_lookup.xlsx`, `wls_program\wildlife_parsing_codex_lookup_backup.xlsx`, `wls_program\src\WildlifeSweeps\wildlife_parsing_codex_lookup.xlsx`, and the live `bin\Release\net8.0-windows` primary/backup workbook copies.
- Verification: programmatic workbook check now resolves `squirrel tracks => [(12, Squirrel Tracks, Squirrel, Tracks)]` and `beaver den => [(12, Beaver Den, Beaver, Lodge / Structure)]` at the first matching regex priority tier, and confirms the `Squirrel / Tracks` pair exists in `SpeciesFindingTypes`.

## 2026-03-18 - Add WLS lookup mappings for Hare Burrow and Rabbit Feeding
- [x] Inspect the active WLS lookup workbook and confirm Snowshoe Hare burrow/feeding mappings and valid pairs are missing.
- [x] Update the authoritative, backup, source-mirror, and live release workbook copies with the requested Snowshoe Hare burrow/feeding mappings.
- [x] Verify first-match rule behavior for hare/rabbit burrow and feeding phrases against the same priority logic the standardizer uses.

### Review
- Root cause: the workbook had no Snowshoe Hare `Burrow` or `Feeding` valid pairs and no hare/rabbit burrow or feeding recognition rules, so those phrases could not standardize directly.
- Fix: added priority-10 hare/rabbit burrow and feeding regex rules, exact keyword rows for the requested phrases, and new `Snowshoe Hare / Burrow` and `Snowshoe Hare / Feeding` rows in `SpeciesFindingTypes` across all workbook copies that WLS uses.
- Verification: programmatic workbook checks now resolve `hare burrow -> Snowshoe Hare burrow`, `rabbit feeding -> Snowshoe Hare Feeding`, `rabbit burrows -> Snowshoe Hare burrow`, and `hare feeding sign -> Snowshoe Hare Feeding` in both the authoritative workbook and the live `bin\\Release` workbook copy.
- Parked note: the user found a separate ATS builder regression where quarter definitions on correction lines are broken after recent correction-line fixes; that investigation is deferred until tomorrow.

## 2026-03-18 - Add WLS lookup mapping for Hares_Tracks
- [x] Trace why `Hares_Tracks` misses the Snowshoe Hare bucket in the current workbook.
- [x] Update the authoritative, backup, source-mirror, and live release workbook copies with a plural-hare track mapping.
- [x] Verify the normalized `Hares_Tracks` phrase resolves before the generic track fallback.

### Review
- Root cause: `Hares_Tracks` preprocesses to `hares tracks`, but the Snowshoe Hare track regex only matched singular `hare`, so the phrase fell through to the generic regex track fallback.
- Fix: added a priority-10 regex and exact keyword for `hares tracks`, both mapping to the existing canonical `Snowshoe Hare Tracks` description across all workbook copies.
- Verification: programmatic workbook checks now resolve `Hares_Tracks -> Snowshoe Hare Tracks` and `hares tracks -> Snowshoe Hare Tracks` in both the authoritative workbook and the live `bin\\Release` workbook copy.
- Assumption: treated `Snowshow Hare Tracks` in the request as a typo and kept the canonical workbook spelling `Snowshoe Hare Tracks` for consistency with the rest of the Snowshoe Hare rules.

## 2026-03-19 - WLS fixed block names, point removal, table export, and ISO dates
- [x] Trace the WLS complete-from-photos flow for fixed block-name assignment, table generation, existing-point maintenance, and workbook export reuse.
- [x] Update WLS findings insertion to use the fixed blocks `WLS_INPROPOSEd`, `WLS_100m`, and `wl_100out`, and remove the manual summary-table header row.
- [x] Add a WLS remove-point workflow in the palette/command surface that accepts comma-separated point numbers, removes matching point blocks, renumbers the remaining blocks, and rebuilds a selected table when provided.
- [x] Add a WLS table-to-workbook export workflow in the palette/command surface that reads the current table rows and writes the same `.xlsx` shape as the initial export without relying on original-text columns.
- [x] Update WLS workbook date formatting to `yyyy-MM-dd`, build the WLS project, and record the verification/review results.

### Review
- Root cause: the WLS flow still depended on user-picked sample blocks for findings, manually inserted a visible header row into the summary table, and only knew how to generate the workbook during the initial in-memory run. There was no post-run maintenance path for removing numbered point blocks or rebuilding/exporting from an existing AutoCAD table.
- Fix: updated `wls_program\src\WildlifeSweeps\CompleteFromPhotosService.cs` so buffer findings now use the fixed block names `WLS_INPROPOSEd`, `WLS_100m`, and `wl_100out`, validate those block definitions up front, emit headerless summary tables, and expose two new maintenance actions: remove points by comma-separated number with optional table rebuild, and export a workbook directly from an existing summary table. Added palette buttons in `wls_program\src\WildlifeSweeps\Ui\PaletteControl.cs` and matching commands in `wls_program\src\WildlifeSweeps\Commands.cs`.
- Date/export note: both workbook paths now use ISO `yyyy-MM-dd` formatting for any populated dates. The new table-driven export reuses the existing workbook writer, derives ATS quarter/section fields from the table `Location` value, and intentionally leaves the original-text workbook column blank because that source text is not stored in the table.
- Verification: `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release --no-restore -p:OutDir="C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\artifacts\verify\WildlifeSweeps\"` passed with `0` warnings and `0` errors.

## 2026-03-19 - WLS remove-point photo layout reflow
- [x] Inspect how WLS photo sheets persist `PHOTO #...` labels and raster images in the drawing.
- [x] Extend the remove-point workflow so it also removes the matching photo label/image pair and reflows the remaining photo sheet back into 4-up groups.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: the first remove-point pass only updated point blocks and the optional summary table. The photo sheet is persisted separately as `RasterImage` entities on the photo layer plus `PHOTO #...` `MText` labels on `DETAIL-T`, with no table linkage, so photo pages would have been left stale after removals.
- Fix: updated `wls_program\src\WildlifeSweeps\PhotoLayoutHelper.cs` to read existing photo layouts by pairing `PHOTO #...` labels with nearby raster images, erase the matched entities, and rebuild the surviving photo records back into the original 4-up stack starting from the first photo anchor. Wired that helper into `wls_program\src\WildlifeSweeps\CompleteFromPhotosService.cs` so `Remove Point` now updates blocks, the selected table, and the photo layout in one pass.
- Verification: `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release --no-restore -p:OutDir="C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\artifacts\verify\WildlifeSweeps\"` passed with `0` warnings and `0` errors.

## 2026-03-19 - WLS remove-point photo sheet cleanup and reflow
- [x] Trace how WLS photo sheets persist raster images and `PHOTO #...` labels after the initial run, and identify a safe matching strategy for existing layouts.
- [x] Extend the WLS remove-point workflow so removing point numbers also removes the matching photo label and photo image entities.
- [x] Rebuild the surviving photo layout into the same 4-up stacking pattern after removals, preserving current photo ordering by number.
- [x] Build WLS, verify the new flow compiles cleanly, and record the result.

### Review
- Root cause: the first remove-point pass only knew about numbered finding blocks and the summary table. WLS photo sheets are persisted separately as raster images on the photo layer plus `MText` labels on `DETAIL-T` that begin with `PHOTO #...`, so removing a point left the old photo image/label stack behind. The first photo cleanup fix then overcorrected by deleting and recreating the photo sheets, which would throw away any manual rotations or nudges the user had already applied.
- Fix: updated `wls_program\src\WildlifeSweeps\PhotoLayoutHelper.cs` to scan existing `PHOTO #...` labels, pair them back to nearby raster images on the configured photo layer, and compact the stack by translating the surviving image/label entities from their old slot to their new slot instead of recreating them. `wls_program\src\WildlifeSweeps\CompleteFromPhotosService.cs` now uses that in-place photo reflow after block/table renumbering, so the photo graphics keep their current rotation and local adjustments while the numbering and stack order update.
- Safety note: unmatched raster images on the photo layer are left untouched and reported back to the command line if they cannot be paired to a `PHOTO #...` label, so the cleanup does not guess on unrelated photo-layer imagery.
- Verification: `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release --no-restore` passed with `0` warnings and `0` errors.

## 2026-03-19 - WLS safe ATS fallback and buffer-edge classification
- [x] Trace the WLS quarter fallback path to confirm whether ATS generation is touching live section linework.
- [x] Move ATS-backed quarter generation off the live drawing so WLS only copies back generated quarter definitions and never runs ATS section cleanup/stash logic against existing section linework.
- [x] Harden WLS buffer classification so near-edge findings use a real-boundary grace check before being treated as outside the 100m buffer.
- [x] Rebuild WLS, verify the updated flow compiles cleanly, and record the result.

### Review
- Root cause: when WLS found no usable quarter polygons, it reflected into ATS `DrawSectionsFromRequests(...)` against the live drawing. That ATS path can stash and erase nearby `L-SEC`/`L-QSEC`/correction geometry before restoring it, which made Complete From Photos unsafe around existing section linework. Separately, the 100m-buffer logic only classified against a sampled copy of the selected boundary with a very small edge tolerance, so near-edge findings could be shown as outside even when they were visually inside the selected buffer.
- Fix: updated `wls_program\src\WildlifeSweeps\CompleteFromPhotosService.cs` so WLS now uses the section-index resolver's own quarter geometry as the authoritative source for ATS location resolution and quarter-outline generation on every run. Existing `L-QUARTER/L-QUATER` entities are no longer used to drive 1/4 logic; they can remain visible in the drawing, but WLS builds and uses its own internal quarter definitions instead. The same file also retains a clone of the selected buffer polyline and gives points an exact-boundary grace check before outside classification, while also relaxing the sampled-edge tolerance used for sampled containment.
- Safety note: if neither live quarter polygons nor the section-index resolver are available, WLS still fails safe and asks for the missing quarter source/index setup rather than guessing or touching existing linework.
- Follow-up correction: buffer classification now uses exact selected-polyline intersection math as the primary in/out test, with only a 1 cm on-boundary tolerance. The prior meter-scale grace check was removed because the user needs a strict in/out result, not "inside if close enough."
- Follow-up correction: quarter-definition authority now comes from ATS road-allowance-aware quarter polygons generated in a scratch drawing, not from the simple section-index quarter boxes. Those same polygons are used for optional orange proof linework when the UI toggle is on, while existing live linework remains untouched.
- Follow-up correction: the ATS scratch quarter build now seeds from a full `db.Wblock()` clone of the active drawing instead of an empty scratch database. That keeps the build non-destructive while preserving the drawing resources ATS needs, which avoids the runtime `eKeyNotFound` failure the empty scratch DB triggered.
- Follow-up correction: WLS now uses existing live `L-QUARTER/L-QUATER` polygons as the authoritative 1/4 source whenever they are present, regardless of the UI proof-line toggle. ATS scratch generation remains only as a fallback for drawings that do not already have quarter polygons.

## 2026-03-19 - Investigate ATS scratch `eKeyNotFound` during WLS quarter builds
- [x] Review the ATS `DrawSectionsFromRequests` call path and the road-allowance cleanup pipeline for named resource assumptions.
- [x] Search ATS source for explicit dependencies on working-database state, named-object dictionaries, block definitions, linetypes, text styles, and layer table records.
- [x] Rank the most likely causes of `ErrorStatus.eKeyNotFound` when ATS is invoked on a scratch database clone.
- [x] Recommend the safest non-destructive workaround without editing ATS or WLS code.

### Review
- Source review result: the quarter-build path itself is heavily layer-dependent and only lightly dependent on block/style resources. `DrawSectionFromIndex` ensures the immediate section/quarter layers (`L-QSEC`, `L-QSEC-BOX`, `L-QUARTER`, and inferred `L-SEC`/`L-USEC`), but the downstream road-allowance cleanup path repeatedly reassigns entities onto `L-USEC`, `L-USEC-0`, `L-USEC2012`, `L-USEC3018`, `L-USEC-C`, `L-USEC-C-0`, and `L-SEC`, with several later passes assigning `writable.Layer = targetLayer` without a local ensure immediately before the assignment. That makes incomplete layer/resource state in a scratch clone a credible source of `eKeyNotFound`.
- Negative findings: I did not find explicit `HostApplicationServices.WorkingDatabase` usage inside ATS source, and I did not find strong evidence that linetypes, text styles, or named-object dictionaries are first-order dependencies in the `DrawSectionsFromRequests` quarter path. The main explicit named-object dictionary usage is in ATS plotting persistence, not in the section/quarter builder. LSD label blocks are a real dependency in ATS, but WLS calls the builder with `drawLsds=false`, so block definitions are a weaker suspect for this specific failure.
- Recommendation: the safest workaround is not more pre-seeding on an in-memory scratch clone. Use a temp DWG copy opened by AutoCAD as a real document/database, run ATS there, harvest the resulting quarter polygons, then close and delete the temp file. That preserves full file-backed database state and command/document lifecycle without touching the user drawing. A temporary working-database swap is the best quick diagnostic if someone wants a smaller experiment first, but it is less defensible as the durable fix because the ATS source does not isolate one narrow missing key that pre-seeding or working-db swapping clearly resolves.

## 2026-03-19 - WLS ATS-only internal quarter definitions
- [x] Revisit the WLS quarter source-selection flow and confirm whether live `L-QUARTER/L-QUATER` entities are still influencing 1/4 logic.
- [x] Switch Complete From Photos back to full ATS road-allowance-aware quarter generation in a scratch clone for every run, keeping the linework internal unless proof drawing is requested.
- [x] Harden the ATS scratch invocation so it runs against the scratch clone as the active working database during reflection.
- [x] Rebuild WLS and record the result.

### Review
- Root cause: the latest fallback tweak had reintroduced live `L-QUARTER/L-QUATER` polygons as an authority source for Complete From Photos. That avoided some scratch-build failures, but it violated the intended design: WLS should always use the full ATS road-allowance-aware quarter logic internally and only draw orange `L-QUARTER` proof linework when the UI toggle is on.
- Fix: updated `wls_program\src\WildlifeSweeps\CompleteFromPhotosService.cs` so the main flow always calls `TryBuildQuarterSourcesViaAtsScratch(...)` and no longer promotes existing live `L-QUARTER/L-QUATER` entities into the quarter-resolution authority path. The same scratch invoke now temporarily switches `HostApplicationServices.WorkingDatabase` to the cloned scratch database before reflecting into ATS `DrawSectionsFromRequests(...)`, then restores it immediately afterward.
- Behavior note: WLS still uses the internal ATS quarter polygons for every run whether or not proof linework is requested. The UI toggle only controls whether those polygons are drawn back out as orange `L-QUARTER` proof geometry.
- Verification: `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release --no-restore` passed with `0` warnings and `0` errors.
- Follow-up correction: WLS proof output and UI wording now align with ATS's actual quarter-view layer name `L-QUATER`. Internal ATS quarter generation is unchanged; only the visible proof layer name/visibility behavior was corrected so WLS no longer creates or prefers `L-QUARTER` output.
- Follow-up correction: WLS had still been harvesting `SectionDrawResult.LabelQuarterPolylineIds`, which ATS uses for the temporary `L-QSEC-BOX` request quarter boxes. The scratch-build path now snapshots preexisting quarter-layer entities in the cloned drawing, runs ATS, and then loads only the newly generated `L-QUATER`/quarter-layer polygons from that scratch DB for WLS logic and proof output. That keeps the original live drawing untouched while using the final road-allowance-shaped ATS quarter geometry instead of box quarters.
- Follow-up correction: ATS restores stashed nearby section-building geometry before redrawing final `L-QUATER`, so cloning the user's current drawing into scratch was still letting old `L-SEC`/`L-QSEC`/`L-USEC*`/`L-QUATER` helper geometry skew the quarter-view result. WLS now erases those ATS helper layers from the scratch clone before invoking ATS, which makes the internal quarter build behave like a clean standalone ATS build while still leaving the live drawing untouched.

## 2026-03-19 - WLS binary buffer classification diagnostics
- [x] Trace the exact selected-polyline containment path used for 100m buffer decisions and confirm where an inside point can still fall through to outside.
- [x] Add a per-point buffer debug report for Complete From Photos so each numbered finding records the exact-boundary result, sampled fallback result, and final area decision.
- [x] Tighten the WLS buffer decision so the selected closed polyline remains the authority source whenever exact containment can classify the point, and only use sampled geometry as a true fallback if exact evaluation cannot run.
- [x] Build WLS, rerun verification, and record the findings and follow-up lesson.

### Review
- Root cause: WLS was still mixing two different containment models for the same selected buffer polyline. The exact check used one ray/intersection parity test with a tiny Y offset, then silently fell back to a separate 2 m sampled polygon when that exact check returned false. That made it hard to tell whether a point like 30 or 31 was being rejected by the real selected polyline, by a brittle exact ray cast, or by the sampled fallback geometry.
- Fix: updated `wls_program\src\WildlifeSweeps\BoundaryContainmentHelper.cs` to expose structured exact and sampled containment evaluations, and made the exact selected polyline authoritative whenever it can classify the point cleanly. The exact path now tries multiple small ray offsets and marks conflicting ray results as ambiguous instead of pretending they are a clean outside result. `wls_program\src\WildlifeSweeps\CompleteFromPhotosService.cs` now writes a per-point `*_buffer_debug.txt` report that includes the final point number, final area, proposed/100m exact result, sampled fallback result, boundary distances, hit counts, and whether sampled fallback was used.
- Behavior note: this does not add any grace-zone or exception buffer. The selected closed polyline remains the binary in/out authority; sampled containment is now only a backup when the exact evaluation cannot classify the point cleanly.
- Verification: `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release --no-restore` passed with `0` warnings and `0` errors, producing the live Release DLL. A second alternate-output verification attempt hit project intermediate-output issues after the successful live build, so the main compile is the verified build for this change.
- Follow-up correction: the live `01147-24-WLS-R0P1_buffer_debug.txt` report showed points 30 and 31 were not fallback mistakes at all; both exact and sampled containment agreed they were outside the actual selected 100m boundary by `58.305 m` and `120.612 m`. To catch stacked/hidden `Defpoints` polylines being selected by mistake, `wls_program\src\WildlifeSweeps\CompleteFromPhotosService.cs` and `wls_program\src\WildlifeSweeps\SortBufferPhotosService.cs` now require an interior confirmation click for `Defpoints` buffer boundaries and reject the selected boundary if that point is not actually inside it.

## 2026-03-20 - WLS recurring ATS scratch `eKeyNotFound`
- [x] Trace the active WLS quarter-build path and confirm why `eKeyNotFound` is still reachable.
- [x] Replace the fragile in-memory ATS scratch quarter build with a safer file-backed scratch flow.
- [x] Build WLS, verify the updated quarter-build path compiles cleanly, and record the review result.

### Review
- Root cause: the live WLS quarter-generation path in `wls_program\src\WildlifeSweeps\CompleteFromPhotosService.cs` was still invoking ATS against `db.Wblock()` with a working-database swap. That was exactly the brittle in-memory scratch pattern previously called out as not faithful enough for ATS road-allowance cleanup, so `ErrorStatus.eKeyNotFound` could still recur even after the earlier mitigations.
- Fix: replaced the `db.Wblock()` scratch creation in `TryBuildQuarterSourcesViaAtsScratch(...)` with a file-backed side database opened from the saved DWG via `Database.ReadDwgFile(...)` and `CloseInput(true)`. WLS still strips ATS helper layers from that isolated scratch DB, runs the reflected ATS `DrawSectionsFromRequests(...)` build there, and harvests only the newly generated `L-QUATER` quarter polygons back into WLS logic. The live drawing remains untouched.
- Behavior note: this path now requires the current drawing to be saved to disk. If the drawing has no file path yet, WLS fails safely with an explicit message instead of silently dropping back to the fragile in-memory quarter build.
- Verification: `dotnet build wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Release --no-restore` passed with `0` warnings and `0` errors.

## 2026-03-23 - WLS title block controls
- [x] Add a WLS `TITLE BLOCK CONTROLs` sub-UI with the requested input controls, ATS location-fill action, and apply/cancel flow.
- [x] Implement title-block placeholder replacement on `Layout 1...` while preserving the existing text entities' formatting.
- [x] Build the WLS project and record the verification result.

### Review
- Root cause: WLS had no title-block workflow in the palette, no reusable ATS boundary-to-location formatter on the WLS side, and no paper-space placeholder replacement flow for the `1.` through `7.` title-block tokens.
- Fix: added a collapsible `TITLE BLOCK CONTROLs` section to the WLS palette with the requested sweep type, purpose, location, survey dates, sub-region, existing-land, and spacing inputs plus `Apply` and `Cancel`. The location field now supports `ADD SECTIONS FROM BDY` and formats grouped ATS quarter text from selected closed boundaries. Added `wls_program/src/WildlifeSweeps/TitleBlockControlService.cs` to collect ATS-style location text and replace placeholders on the first `Layout 1*` paper-space layout by updating existing `MText`, `DBText`, and block attributes in place so the original text formatting is preserved. Increased the palette minimum size to fit the new controls cleanly.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-24 - WLS Environmental Conditions detail rows and mixed casing follow-up
- [ ] Add the four manual-entry Environmental Conditions detail rows under each survey-date row without reintroducing merged-cell behavior.
- [ ] Restore uppercase text for green title-block replacements while keeping blue purpose text in display case.
- [ ] Rebuild WLS, verify the result, and document the follow-up review and lesson.
- Follow-up correction: live first-sheet tabs can be named like `1 of 5`, not just `Layout 1`. `TitleBlockControlService` now matches first-sheet names like `1 of _`, still accepts `Layout 1`, and only errors when no first-sheet paper-space tab can be identified.

## 2026-03-23 - WLS footprint-based natural sub-region lookup
- [x] Replace the title-block sub-region picker with a footprint-driven lookup workflow and button in the WLS palette.
- [x] Import the Alberta natural sub-region shapefile through a location-window-limited Map import, read `NSRNAME` / `NRNAME`, and format the title-block text.
- [x] Keep matching region linework only when multiple natural-region polygons overlap the selected footprint; otherwise clean up the temporary import.
- [x] Build WLS and record the verification result.

### Review
- Root cause: the title-block sub-region field was still using a manual hardcoded list, which did not reflect the user’s actual proposed footprint and could not surface the rare multi-region case visually.
- Fix: changed the WLS title-block sub-region UI to a freeform text box with `GET SUB-REGION FROM FOOTPRINT`, added `wls_program/src/WildlifeSweeps/NaturalRegionLookupService.cs`, and wired it to select closed footprint boundaries, import the Alberta natural-subregion shapefile through a bounded location window, read `NSRNAME` / `NRNAME` from imported Object Data, and format text like `Lower Foothills Sub-region of the Foothills Region`. When exactly one unique match is found, the temporary imported linework is erased; when multiple unique matches are found, only the matching imported linework is left visible for review.
- Safety note: if the Map importer cannot apply a location window, WLS now refuses the import instead of risking a full-dataset load from the provincial shapefile.
- Follow-up correction: AutoCAD could crash right after a successful single-match lookup because the first implementation deleted the temporary Object Data table immediately after reflection-based OD reads and entity cleanup. The lookup now keeps the temporary OD table definition in place, runs the OD read phase separately from the erase phase, opens imported entities `ForRead` during evaluation, and explicitly disposes reflection-based table/record/enumerator wrappers to match the safer ATS lifetime pattern.

## 2026-03-23 - WLS Environmental Conditions tables per survey date
- [x] Trace the current survey-date output path and choose the least invasive layout anchor for Environmental Conditions tables.
- [x] Add per-survey-date Environmental Conditions table generation with the requested heading, text sizing, colors, backgrounds, and borders.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: title-block survey dates were only being injected into placeholder `4.` text in `TitleBlockControlService`, so there was no existing path to create or refresh any per-date Environmental Conditions tables on the layout.
- Fix: updated `wls_program/src/WildlifeSweeps/TitleBlockControlService.cs` so `Apply(...)` now rebuilds Environmental Conditions tables whenever survey dates are present. The first table includes a merged `ENVIRONMENTAL CONDITIONS` heading row with text size `16` and ACI `14`, and every survey date gets its own three-column row with the formatted survey date, `START OF SWEEP`, and `END OF SWEEP` using text size `10`, red text, ACI `254` background, and full borders. Re-runs first remove earlier Environmental Conditions tables, then recreate them. The insertion anchor is resolved from the first existing Environmental Conditions table if present, otherwise from the survey-date placeholder entity on the target layout so the behavior survives subsequent applies.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.
- Follow-up correction: Environmental Conditions tables now ignore the survey-date placeholder anchor and always start at fixed paper-space coordinates `(67.256, 753.705)` on the target layout, then stack downward from there on each apply.

## 2026-03-23 - WLS safe title-block color gating
- [x] Audit the title-block replacement path and define a fail-closed gate so only intended placeholder text can be updated.
- [x] Restrict WLS title-block replacements to blue or green `DBText`, block attributes, and inline-formatted `MText` placeholders.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: the title-block replacement flow was broad enough to risk updating placeholder-looking text anywhere on the first layout, even when the visible text was not part of the blue/green editable title-block fields the user intended to target.
- Fix: tightened `wls_program/src/WildlifeSweeps/TitleBlockControlService.cs` so `DBText` and block attributes are only updated when their effective visible color resolves to blue or green through direct color, `ByLayer`, or `ByBlock` inheritance, and `MText` now checks inline `\C...;` / `\c...;` color overrides before falling back to the entity color. The allowed colors are fail-closed to exact blue/green values so white body text in the same layout is left untouched.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-23 - WLS Environmental Conditions formatting polish
- [x] Update Environmental Conditions table alignment, date casing, and non-bold text styling.
- [x] Normalize methodology spacing output to remove the gap before `m`.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: the first Environmental Conditions pass left the heading and date cell left-aligned, reused mixed-case survey-date formatting from the narrative text path, and still emitted methodology spacing like `30 m` instead of the tighter `30m` format the user wanted.
- Fix: updated `wls_program/src/WildlifeSweeps/TitleBlockControlService.cs` so Environmental Conditions heading and value cells are centered, the table-only survey date formatter now uppercases the date text, and table cells force a non-bold clone of their active text style when the underlying style is bold. Methodology spacing output now removes the space before the terminal `m`.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-23 - WLS natural-region wording correction
- [x] Locate the natural-region formatter that emits the title-block sub-region text.
- [x] Update the region suffix wording from `Region` to `Natural Region` in the lookup formatter.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: the footprint-driven sub-region formatter in `wls_program/src/WildlifeSweeps/NaturalRegionLookupService.cs` normalized `NRNAME` values to plain `Region`, which produced output like `Lower Foothills Sub-region of the Foothills Region` instead of the desired `Foothills Natural Region`.
- Fix: updated `FormatNaturalRegionText(...)` so region names already ending in `Natural Region` are preserved, names ending in plain `Region` are rewritten to `Natural Region`, and bare names append `Natural Region`.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-23 - WLS Match Table to Photos
- [x] Add a WLS action/command that prompts for an existing summary table and syncs photo labels from its numbered finding rows.
- [x] Update only matching `PHOTO #...` labels in the same space, using all-caps captions derived from the table wording.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: WLS already had table export/rebuild and photo-label generation paths, but there was no maintenance action to push updated wording from an edited summary table back into the existing `PHOTO #...` labels.
- Fix: added `wls_program/src/WildlifeSweeps/MatchTableToPhotosService.cs`, which prompts for a summary table, reads numbered finding rows from column 0/1, and updates matching `PHOTO #...` `MText` labels in the same drawing space as the table. The new service reuses `PhotoLayoutHelper`'s photo-label parser/formatter so captions stay escaped and all caps. Wired it into the main WLS command surface with `WLS_MATCH_TABLE_TO_PHOTOS` in `wls_program/src/WildlifeSweeps/Commands.cs` and added a `Match Table to Photos` palette button in `wls_program/src/WildlifeSweeps/Ui/PaletteControl.cs`.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-24 - WLS Environmental Conditions and purpose casing follow-up
- [x] Fix later Environmental Conditions tables so they render as three cells instead of a single merged row.
- [x] Keep Environmental Conditions month text uppercase while leaving ordinal suffixes lowercase.
- [x] Keep title-block token `1.` uppercase while formatting token `2.` in mixed case.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: the one-row Environmental Conditions tables were still inheriting AutoCAD table style title/header behavior, which could collapse the second table into a single merged-looking row, and the earlier all-caps date formatter uppercased the ordinal suffix as well. Separately, title-block token `2.` was still using the raw uppercase combo-box text even though the blue purpose field should read in display case.
- Fix: updated `wls_program/src/WildlifeSweeps/TitleBlockControlService.cs` so generated Environmental Conditions tables explicitly suppress style title/header behavior before sizing rows, the table-specific survey date formatter now emits an uppercase month with lowercase ordinal suffixes, and token `2.` now uses a dedicated purpose formatter that maps the known uppercase choices into mixed-case display text while token `1.` remains unchanged.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-24 - WLS Environmental Conditions detail rows and color-specific purpose casing
- [x] Add the four manual-entry Environmental Conditions rows under each survey-date row.
- [x] Keep purpose text uppercase on green title fields while preserving mixed-case purpose text on blue narrative fields.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: the Environmental Conditions table builder only emitted the date/start/end row, so there was nowhere for the user to manually enter temperature, wind speed, precipitation, and cloud cover values. Separately, the previous purpose-casing change assumed placeholder casing was token-specific, but the same purpose token can appear in both green title text and blue narrative text with different casing expectations.
- Fix: updated `wls_program/src/WildlifeSweeps/TitleBlockControlService.cs` so every Environmental Conditions table now adds four bordered detail rows for `TEMPERATURE (C°)`, `WIND SPEED (km/h)`, `PRECIPITATION (mm)`, and `CLOUD COVER (%)`, leaving the start/end value cells blank for manual entry. The same file now resolves replacement text per occurrence, using the target text color to keep purpose replacements uppercase on green text and mixed case on blue text.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-24 - WLS Environmental Conditions inter-table gap removal
- [x] Remove the visible gap between stacked Environmental Conditions tables.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: `wls_program/src/WildlifeSweeps/TitleBlockControlService.cs` was still applying a fixed `EnvironmentalConditionsVerticalGap` when stacking one generated Environmental Conditions table under the next, so each later survey-date table was intentionally offset downward.
- Fix: changed `EnvironmentalConditionsVerticalGap` to `0.0` so the generated tables stack flush while leaving row heights, borders, and the initial anchor position unchanged.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.
## 2026-03-24 - WLS photo layout template sheets
- [x] Inspect the current WLS photo generation flow and identify the safest place to clone `4 of 5` as a template layout.
- [x] Implement per-4-photo layout copying and viewport centering on the generated photo group extents.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: WLS already chunked photos into 4-up model-space groups, but it had no paper-space follow-up. There was no layout cloning, no viewport selection logic, and no viewport-centering pass tied to the same 4-slot geometry used to place photos.
- Fix: updated `wls_program/src/WildlifeSweeps/PhotoLayoutHelper.cs` so photo placement now also syncs paper-space photo sheets from template layout `4 of 5`. The helper removes old generated photo-sheet copies, reuses `4 of 5` for the first photo group, copies it for later groups, finds the main floating viewport on each sheet, and recenters that viewport on the canonical 4-slot model-space frame for each group while preserving the template scale. The same helper also refreshes those photo sheets after photo reflow so remove/renumber flows do not leave stale viewport positions behind. Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs` to print photo-layout report messages before returning on failure.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-24 - WLS photo sheet viewport target correction
- [x] Fix the photo-sheet viewport centering so copied `4 of 5` layouts actually pan to the photo group after placement.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: the first photo-sheet pass tried to pan copied layout viewports by writing `Viewport.ViewCenter` from transformed model coordinates. For these template viewports that left the copied sheets on their original template framing instead of moving the actual view to the photo group.
- Fix: updated `wls_program/src/WildlifeSweeps/PhotoLayoutHelper.cs` so sheet centering now moves the viewport by setting `Viewport.ViewTarget` to the canonical group center and resetting `Viewport.ViewCenter` to `Point2d.Origin`, while still preserving the template scale and lock state.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-24 - WLS photo sheet viewport template detection fallback
- [x] Fix the photo-sheet viewport selector so the `4 of 5` template viewport is recognized even when it is not numbered as a typical floating viewport.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: the first viewport selector only accepted viewports with `Viewport.Number > 1`, which assumed the template sheet used AutoCAD's usual floating-viewport numbering. Your `4 of 5` template did contain a usable viewport, but it was being rejected by that narrow rule so sheet centering never ran.
- Fix: updated `wls_program/src/WildlifeSweeps/PhotoLayoutHelper.cs` so viewport selection now ranks candidates instead of hard-rejecting them. It still prefers numbered floating viewports first, but now falls back to locked/on viewports and then the largest usable viewport on the layout.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-24 - WLS photo sheet model-space + template safety correction
- [x] Force photo placement and reflow to operate on model space instead of the active current space.
- [x] Treat `4 of 5` as a pure template and copy it for every generated photo sheet, including the first group.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: the first photo-sheet pass still inserted photos and read existing photo layouts from `db.CurrentSpaceId`, so running the command while sitting on layout `4 of 5` could write photo entities into paper space instead of model space. On top of that, the paper-space sync path reused the real `4 of 5` layout as sheet 1, so the template itself could be modified instead of being used purely as a copy source.
- Fix: updated `wls_program/src/WildlifeSweeps/PhotoLayoutHelper.cs` so the insertion-point prompt temporarily switches to `Model` for the pick, actual photo placement/reflow always reads and writes the model-space block table record, and photo-sheet generation now copies `4 of 5` for every group into generated layouts like `4 of 5 - PHOTO 1`, leaving the original template untouched.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-24 - WLS generated photo sheet blank viewport follow-up
- [x] Inspect the copied photo-sheet viewport pan logic and confirm why generated layouts are blank.
- [x] Fix viewport centering so copied layouts preserve the template viewport framing while targeting the correct photo group.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: the copied photo-sheet layouts were still vulnerable to selecting the overall paper-space viewport instead of the actual model viewport. On copied layouts where `Viewport.Number` was not a reliable discriminator, the fallback logic could rank the full-sheet paper-space viewport as the "largest usable" candidate, and panning that viewport makes the entire generated layout appear blank.
- Fix: updated `wls_program/src/WildlifeSweeps/PhotoLayoutHelper.cs` so `TryGetPrimaryPhotoViewport(...)` now rejects viewports that match the paper-space page size and page-center signature of AutoCAD's overall layout viewport. The centering path still preserves the copied template viewport's existing `ViewCenter`, so generated sheets now pan only the real photo viewport toward the canonical 4-photo group.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-24 - WLS photo sheet blank viewport follow-up
- [x] Reinspect the copied photo-sheet viewport centering logic against the live `4 of 5` template behavior.
- [x] Apply the smallest safe viewport fix so generated photo sheets show the model-space photo groups without mutating the template layout.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: the previous viewport-target patch moved copied photo-sheet viewports to the correct model-space `ViewTarget`, but then it also forced `Viewport.ViewCenter = Point2d.Origin`. That discarded the template viewport's existing DCS framing, so copied sheets could pan off into blank space even though the template layout itself stayed protected.
- Fix: updated `wls_program/src/WildlifeSweeps/PhotoLayoutHelper.cs` so copied photo-sheet viewports now preserve their original `ViewCenter` and only retarget the model-space `ViewTarget` to the canonical 4-photo group center. This keeps the template scale and hand-tuned viewport framing intact while still panning each generated sheet to its group.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-24 - WLS photo sheet blank viewport investigation
- [x] Inspect the current photo-sheet viewport-centering code in `wls_program/src/WildlifeSweeps/PhotoLayoutHelper.cs`.
- [x] Compare the viewport target/center writes against the copied-template behavior and identify the likeliest blank-view failure mode.
- [x] Record the reasoning and recommended narrow fix.

### Review
- Root cause under investigation: in `TryCenterPhotoSheetViewports(...)`, the copied template viewport is updated with a new `ViewTarget`, but the code also forcibly resets `Viewport.ViewCenter` to `Point2d.Origin`. On a hand-tuned template layout, `ViewCenter` is part of the preserved DCS framing. Zeroing it discards the template's existing pan/centering and can shift the view away from the intended model-space target, which matches the observed blank generated sheets after the model-space/template safety fixes.
- Recommended narrow fix: preserve the copied viewport's existing `ViewCenter` and other template view parameters, and update only `ViewTarget` (plus `On`/`Locked` handling). If a follow-up adjustment is still needed after that, inspect the copied viewport's original `ViewCenter`/`CustomScale`/`ViewHeight` at runtime instead of zeroing DCS state.

## 2026-03-24 - WLS photo sheet diagnostics
- [x] Add structured photo-sheet logging to the existing WLS report/output path.
- [x] Capture template detection, generated layouts, viewport candidates, selected viewport state, and target center details.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: although the photo-sheet helper already had a basic `report` list, it did not yet log enough context to explain the next failure quickly. We still had to infer whether a bad run came from the prompt context, generated layout cleanup/copy, group base-point math, or the viewport state chosen on each sheet.
- Fix: expanded `wls_program/src/WildlifeSweeps/PhotoLayoutHelper.cs` to log the photo-sheet prompt context, chosen insertion point, model-space group placement anchors, template-layout confirmation, generated-layout cleanup/copy actions, and richer viewport details including `CustomScale`, `TwistAngle`, `ViewTarget`, and `ViewCenter`. The existing report plumbing in `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs` already prints these entries to the AutoCAD command line on failure and in the final report on success, so no second logging channel was needed.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-24 - WLS photo sheet layout-tab regression
- [x] Inspect the current copied-layout viewport mutation flow for anything that can leave layout tabs unusable.
- [x] Apply the narrowest safe fix to avoid corrupting layout/view state while still supporting photo-sheet centering.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: the photo-sheet diagnostics showed two concrete failures. First, the helper was still selecting the oversized dormant viewport whose `ViewCenter` mirrored its paper-space center instead of the real photo viewport whose `ViewCenter` already carried a model-space location. Second, the actual viewport edit then failed with `eNotInPaperspace`, which means WLS was trying to retarget the copied sheet viewport without activating that layout in paper space first.
- Fix: updated `wls_program/src/WildlifeSweeps/PhotoLayoutHelper.cs` so each generated photo sheet is activated before viewport edits, including an explicit `SwitchToPaperSpace()` call and restoration of the user's original layout afterward. The viewport selector now strongly prefers viewports whose stored `ViewCenter` is meaningfully distinct from their paper-space center, which biases selection toward the real model viewport and away from the oversized dormant sheet viewport seen in the log.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug -o C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\build\verify-photo-layout-fix` passed with `0` warnings and `0` errors while the normal debug DLL remained locked by AutoCAD.

## 2026-03-24 - WLS photo sheet diagnostics logging
- [x] Inspect the current photo-sheet report/output path and choose the highest-signal runtime diagnostics to print.
- [x] Add targeted logging for layout cleanup/copy, viewport selection, and viewport centering in the photo-sheet helper flow.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: the photo-sheet path already carried a `report` list from `PhotoLayoutHelper` back to `CompleteFromPhotosService`, but it only emitted sparse failure messages, so recent viewport/layout regressions still required guesswork. On top of that, successful photo-layout runs printed the same report twice: once immediately after placement and again in the final `--- Report ---` block.
- Fix: updated `wls_program/src/WildlifeSweeps/PhotoLayoutHelper.cs` so the photo-sheet helper now logs sync start parameters, template discovery, generated-layout cleanup/copy actions, per-layout viewport scans, skipped viewport reasons, selected viewport details, and the before/after targeting values used during centering. Updated `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs` so failed photo-layout runs still print the diagnostics immediately before returning, while successful runs defer to the existing final report block and avoid duplicate output.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-24 - WLS photo sheet layout-tab safety follow-up
- [x] Reinspect the generated-layout viewport-edit path for anything that can leave layout tabs unstable after the photo command.
- [x] Reduce layout/viewport write scope to the minimum safe transaction pattern and preserve diagnostics.
- [x] Rebuild WLS and record the verification result.

### Review
- Root cause: the photo-sheet centering path was still scanning viewport candidates `ForWrite` and then modifying every generated layout viewport inside one long transaction. That is broader than necessary for AutoCAD layout objects, and it increases the risk of leaving copied layout tabs in a bad UI state if one viewport/layout interaction goes sideways.
- Fix: updated `wls_program/src/WildlifeSweeps/PhotoLayoutHelper.cs` so viewport candidate scans now run `ForRead`, the helper records only the selected viewport `ObjectId`, and each generated layout is reopened and centered in its own short transaction. Only the chosen viewport for that one layout is upgraded `ForWrite`, while the existing photo-sheet diagnostics remain intact.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-24 - WLS photo sheet pan-property correction
- [x] Reconcile the latest photo-sheet report with the current centering write path.
- [x] Pan copied photo sheets through the template viewport property that actually stores model-space center.
- [x] Rebuild the live DLL and record the verification result.

### Review
- Root cause: the latest photo-sheet report showed the correct viewport was finally selected and the edit succeeded, but the visible sheet did not move. The selected template viewport already stored a real model-space center in `ViewCenter` while `ViewTarget` remained `(0, 0, 0)`, so WLS was still updating the wrong pan property for this template.
- Fix: updated `wls_program/src/WildlifeSweeps/PhotoLayoutHelper.cs` so centering now chooses a pan mode per selected viewport. When the template viewport clearly carries a model-space `ViewCenter`, WLS keeps `ViewTarget` unchanged and moves `ViewCenter` to the canonical photo-group center. The old `ViewTarget` path remains as the fallback for templates that do not encode model-space pan in `ViewCenter`. The diagnostics now log `panMode=ViewCenter` or `panMode=ViewTarget` plus old/new center values.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-24 - WLS photo sheet visual-centering polish
- [x] Inspect the remaining vertical centering bias after the viewport starts panning correctly.
- [x] Include the photo-caption footprint in the canonical 4-photo group center calculation.
- [x] Rebuild the live DLL and record the verification result.

### Review
- Root cause: even after the correct viewport and pan property were fixed, the canonical photo-group center in `wls_program/src/WildlifeSweeps/PhotoLayoutHelper.cs` still measured only the four image rectangles. The real visual footprint extends farther south because each photo has a two-line caption centered `48` units below the image, so the computed center stayed slightly too far north and made the viewport framing look a bit south-heavy.
- Fix: updated `BuildCanonicalPhotoGroupCenter(...)` to include the caption footprint when calculating the overall 4-up extents. The helper now uses shared label geometry constants and expands the vertical extent downward by the caption offset plus an approximate two-line text half-height. I also replaced the duplicated hardcoded caption offset/text-height values in the photo-label helper paths with those shared constants so the centering math and actual label placement stay in sync.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors.

## 2026-03-24 - WLS simplified finding-name mapping workbook
- [x] Inspect the current finding-name lookup workbook and code source of truth to identify the review-friendly mapping fields.
- [x] Generate a simplified XLSX that shows the current finding-name input-to-output mappings in a compact format for manual review.
- [x] Verify the workbook contents/location and record the result.

### Review
- Source of truth confirmed: the WLS finding-name standardizer loads rules from `wls_program/src/WildlifeSweeps/wildlife_parsing_codex_lookup.xlsx`, specifically the `RecognitionKeywords`, `RecognitionRegex`, and `Skips` sheets. No saved custom prompt mappings were present under `%APPDATA%\\WildlifeSweeps\\custom_mappings.json`, so the export reflects the current workbook-driven behavior only.
- Deliverable: created `output/spreadsheet/wls_finding_name_mapping_simplified.xlsx` with a `Summary` sheet plus two review sheets: `LookupSimple` (source order) and `ByOutput` (sorted by final standardized description). Each row shows the rule type, trigger text/pattern, resulting standardized description, species, finding type, notes, and source sheet/row.
- Verification: opened the generated workbook with `openpyxl` and confirmed it exists, contains the expected three sheets, and includes 400 mapped rows total (281 keyword rules, 96 regex rules, and 23 skip rows).

## 2026-03-24 - WLS direct-mapping finding lookup simplification
- [x] Remove species/finding-type dependencies from the WLS finding standardizer and keep only direct input-to-output mapping behavior.
- [x] Rewrite the source lookup workbook(s) to direct-mapping sheets and regenerate the simplified review export to match.
- [x] Build WLS, verify the new workbook structure/load path, and record the lesson from this correction.

### Review
- Root cause: even after producing a simplified review export, the active WLS standardizer and source lookup workbook still carried the older species/finding-type model internally. That left the code, source workbook, and review workbook out of sync with the user’s actual requirement, which is a direct “this becomes this” mapping model.
- Fix: simplified `wls_program/src/WildlifeSweeps/FindingsDescriptionStandardizer.cs` to treat keyword/regex/custom matches as direct standardized-description mappings only, removed the species/finding-type pair validation/reverse-lookup path, shrank `PromptResult`, `RecognitionRule`, `RecognitionMatch`, `CustomMapping`, and `StandardizedFinding` to description-focused data, and removed the now-dead `wls_program/src/WildlifeSweeps/FindingOtherValueHelper.cs`. Updated `wls_program/src/WildlifeSweeps/FindingsStandardizationHelper.cs` to the smaller prompt contract. Rewrote the lookup workbooks at `wls_program/wildlife_parsing_codex_lookup.xlsx`, `wls_program/wildlife_parsing_codex_lookup_backup.xlsx`, `wls_program/src/WildlifeSweeps/wildlife_parsing_codex_lookup.xlsx`, and `wls_program/docs/wildlife_parsing_codex_lookup.xlsx` to use only `RecognitionKeywords`, `RecognitionRegex`, and `Skips`, with direct-mapping columns (`Priority`, trigger text/pattern, `StandardDescription`, `Notes`). Regenerated `output/spreadsheet/wls_finding_name_mapping_simplified.xlsx` to remove species/finding-type columns as well.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors. Verified the source, backup, debug-output, and simplified export workbooks with `openpyxl`; each now has the expected direct-mapping sheet structure and no `SpeciesFindingTypes` sheet.

## 2026-03-24 - WLS findings statement updater
- [x] Inspect the title-block UI/update path, summary-table structure, and test harness approach for a deterministic findings statement feature.
- [x] Implement the statement builder plus a new `Upd. Finding Statement` title-block submenu button that reads a selected table and updates the page-1 placeholder text in blue.
- [x] Add and run at least 3 sample tests for the statement builder, verify the WLS build, and record the review.

### Review
- Root cause: WLS had no deterministic findings-paragraph builder and no page-1 updater for the yellow placeholder text. The existing title-block service only replaced numbered `1.` through `7.` tokens in blue/green text, while the findings summary table flattened the usable inputs down to one finding-description column plus row ordering/background cues.
- Fix: added `wls_program/src/WildlifeSweeps/WildlifeFindingsStatementBuilder.cs` as a pure deterministic narrative engine with explicit `FindingRow`, `WildlifeFeatureFlags`, grouping helpers, narrative-label helpers, and Oxford-comma joining. Extended `wls_program/src/WildlifeSweeps/TitleBlockControlService.cs` with a new selected-table workflow that prompts for the findings table, asks the four key-feature yes/no flags, derives `FindingRow` objects from the table, resolves the findings page number from the table layout, and replaces the exact yellow page-1 placeholder text `This Statement Must be written based on findings` in blue. Updated `wls_program/src/WildlifeSweeps/Ui/PaletteControl.cs` to add the new `Upd. Finding Statement` button inside the title-block submenu and wire it to the new service path with status/tooltip updates.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors. Added `wls_program/src/WildlifeSweeps.DecisionTests/WildlifeSweeps.DecisionTests.csproj` plus three deterministic sample tests, and `dotnet run --project C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps.DecisionTests\WildlifeSweeps.DecisionTests.csproj -c Debug` passed.

## 2026-03-24 - WLS lookup alias additions
- [x] Inspect the current WLS lookup workbook structure and locate the source workbook plus mirror copies.
- [x] Add the requested direct mappings to the recognition sheet(s) and sync the workbook copies.
- [x] Verify the workbook entries are present and record the result.

### Review
- Scope: the WLS direct-mapping lookup workbook still uses `RecognitionKeywords` as the primary alias table, mirrored across the source workbook, backup workbook, plugin copy, and docs copy.
- Fix: added two exact keyword aliases to `RecognitionKeywords` in `wls_program/wildlife_parsing_codex_lookup.xlsx` and mirrored the same rows into `wls_program/wildlife_parsing_codex_lookup_backup.xlsx`, `wls_program/src/WildlifeSweeps/wildlife_parsing_codex_lookup.xlsx`, and `wls_program/docs/wildlife_parsing_codex_lookup.xlsx`: `live rabbit -> Rabbit Sighting` and `wood pecker feeding cavity -> Pileated Woodpecker Feeding Cavity`. Both were added at priority `1` so they stay ahead of broader generic rules.
- Verification: reopened each workbook with `openpyxl` and confirmed the new rows are present at rows `283` and `284` in `RecognitionKeywords` with the expected descriptions and notes.

## 2026-03-24 - WLS moose alias additions
- [x] Check the current WLS lookup workbook for existing Moose alias rows that could conflict or need updating.
- [x] Add or update the requested direct mappings across the source workbook and mirrored copies.
- [x] Verify the new workbook rows and record the result in the task log.

### Review
- Scope: the WLS lookup already had several Moose-specific scat/track/browse aliases plus broader regex rules, but it did not contain an exact `moose poop` alias or a bare `moose` alias.
- Fix: added two exact keyword aliases to `RecognitionKeywords` in `wls_program/wildlife_parsing_codex_lookup.xlsx` and mirrored the same rows into `wls_program/wildlife_parsing_codex_lookup_backup.xlsx`, `wls_program/src/WildlifeSweeps/wildlife_parsing_codex_lookup.xlsx`, and `wls_program/docs/wildlife_parsing_codex_lookup.xlsx`: `moose poop -> Moose Scat` and `moose -> Moose Sighting`. Both were added at priority `1` so they override the broader Moose regex rules cleanly.
- Verification: reopened each workbook with `openpyxl` and confirmed the new rows are present at rows `285` and `286` in `RecognitionKeywords` with the expected descriptions and notes.

## 2026-03-24 - WLS normalized species lookup fallback
- [x] Inspect the existing findings lookup workbook/code path and decide the narrowest schema/API for normalized-value species metadata with prompt fallback.
- [x] Implement workbook-backed species resolution in the `Upd. Finding Statement` flow, including prompt-on-miss and automatic workbook update.
- [x] Verify build behavior, update task/docs/lessons, and record what remains blocked on exact page-1 table text.

### Review
- Root cause: the new findings-statement flow was still deriving species only from a local suffix heuristic, which does not satisfy the user’s requirement that species assignment be keyed by normalized finding values and learned automatically when a value is missing from the workbook.
- Fix: extended `wls_program/src/WildlifeSweeps/FindingsDescriptionStandardizer.cs` with workbook-backed normalized-description species metadata loading, lookup, and mirror-sync upsert support through a new `FindingSpecies` sheet. Updated `wls_program/src/WildlifeSweeps/TitleBlockControlService.cs` so `Upd. Finding Statement` now resolves each row’s species from workbook metadata first, prompts with a species selection dialog only when a normalized finding description is not mapped yet, and writes the chosen species back into the lookup workbook for future runs. Added `wls_program/src/WildlifeSweeps/Ui/SpeciesSelectionDialog.cs` for the one-time species picker. Seeded the new `FindingSpecies` sheet into `wls_program/wildlife_parsing_codex_lookup.xlsx`, `wls_program/wildlife_parsing_codex_lookup_backup.xlsx`, `wls_program/src/WildlifeSweeps/wildlife_parsing_codex_lookup.xlsx`, and `wls_program/docs/wildlife_parsing_codex_lookup.xlsx`, while leaving generic or ambiguous labels blank so they still prompt.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors. `dotnet run --project C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps.DecisionTests\WildlifeSweeps.DecisionTests.csproj -c Debug` passed. Reopened the source workbook and confirmed the new `FindingSpecies` sheet exists with seeded rows such as `Moose Scat -> Moose`, `Moose Sighting -> Moose`, `Pileated Woodpecker Feeding Cavity -> Pileated Woodpecker`, while ambiguous rows like `Rabbit Sighting`, `Woodpecker Cavity Tree`, and `Animal Remains (Species Unconfirmed)` are intentionally left blank to trigger the prompt.
- Remaining blocker: the two new page-1 findings tables are still waiting on the exact header/row text from the screenshots so they can be generated accurately under the same button.

## 2026-03-24 - WLS wildlife group normalization
- [x] Inspect the current workbook-backed species metadata flow and identify every place that still assumes specific species values.
- [x] Change the prompt, stored lookup values, and seeded workbook data so findings resolve only to `AMPHIBIANS`, `BIRDS`, `MAMMALS`, or `REPTILES`.
- [x] Rebuild WLS, verify the workbook/group values, and record the correction.

### Review
- Root cause: the first pass of the lookup-backed prompt flow still treated findings metadata as specific species values, so the runtime picker offered entries like `Moose`, `Snowshoe Hare`, and `Pileated Woodpecker` instead of the four wildlife groups the user actually needs for the page-1 findings table.
- Fix: updated `wls_program/src/WildlifeSweeps/FindingsDescriptionStandardizer.cs` so the workbook metadata layer now resolves and persists normalized finding descriptions to wildlife groups only, using the allowed values `AMPHIBIANS`, `BIRDS`, `MAMMALS`, and `REPTILES`. Updated `wls_program/src/WildlifeSweeps/TitleBlockControlService.cs` so `Upd. Finding Statement` prompts for a wildlife group instead of a species when a normalized finding is missing from the workbook, while still keeping the narrative statement builder on the original parsed finding wording. Updated `wls_program/src/WildlifeSweeps/Ui/SpeciesSelectionDialog.cs` into a group-only picker UI. Normalized the `FindingSpecies` workbook sheet across all mirrored copies so column B is now `WildlifeGroup` and existing seeded rows were converted to group values, with ambiguous rows intentionally left blank to trigger the prompt.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors. `dotnet run --project C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps.DecisionTests\WildlifeSweeps.DecisionTests.csproj -c Debug` passed. Reopened the source workbook and confirmed `FindingSpecies` now has header `StandardDescription / WildlifeGroup / Notes`, with rows such as `Rabbit Sighting -> MAMMALS`, `Moose Scat -> MAMMALS`, `Pileated Woodpecker Feeding Cavity -> BIRDS`, and ambiguous rows like `Animal Remains (Species Unconfirmed)` still blank.

## 2026-03-24 - WLS findings statement partial replacement and group-status follow-up
- [x] Inspect the current page-1 findings placeholder replacement, narrative phrasing, and wildlife-group table status path.
- [x] Replace only the yellow placeholder segment, improve mixed sign/sighting wording, and output wildlife-group `SIGHTING`/`SIGN` with `SIGHTING` priority.
- [x] Build WLS, run the deterministic statement tests, and record the verification plus lesson from the correction.

### Review
- Root cause: the first `Upd. Finding Statement` pass reused the same whole-entity replacement pattern as the simpler numbered title-block tokens. That was too blunt for the page-1 findings prompt because the editable yellow sentence can live inside a larger `MText` object, so `mtext.Contents = statement` wiped surrounding text. At the same time, the deterministic builder still collapsed mixed sign/sighting rows for one species into `species sign and observations`, which read awkwardly in narrative form, and the page-1 wildlife-group status table had not been generated yet.
- Fix: updated `wls_program/src/WildlifeSweeps/TitleBlockControlService.cs` so the page-1 findings updater now preserves surrounding `MText` content and replaces only the visible placeholder span with a scoped blue `MText` run. Added visible-text parsing for formatted `MText`, substring replacement for `DBText`/attributes, and a new page-1 wildlife-group table rebuild at `(-445.662, 752.205)` that outputs `N/A`, `SIGN`, or `SIGHTING` for `AMPHIBIANS`, `BIRDS`, `MAMMALS`, and `REPTILES`, with `SIGHTING` taking priority whenever any row in that group is a sighting/audible/visual observation. Extended the request model so the selected findings table already carries those aggregated group statuses through the same button flow.
- Wording follow-up: updated `wls_program/src/WildlifeSweeps/WildlifeFindingsStatementBuilder.cs` so mixed sign/incidental species now collapse to `species sign with incidental observations` instead of `species sign and observations`, and added a deterministic regression case in `wls_program/src/WildlifeSweeps.DecisionTests/Program.cs` for that exact narrative shape.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug` passed with `0` warnings and `0` errors. `dotnet run --project C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps.DecisionTests\WildlifeSweeps.DecisionTests.csproj -c Debug` passed.

## 2026-03-25 - WLS findings table merged first-row fix
- [x] Locate the WLS findings-table builder and confirm why the first data row is rendering as a merged/header-style row.
- [x] Patch the findings table setup so row 1 is a normal unmerged data row without regressing the rest of the table layout.
- [x] Build WLS, verify the fix, and record the correction plus lesson.

### Review
- Root cause: the numbered findings table is built in `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs`, not in the title-block service. Its shared table style already suppresses title/header rows, but the live `Table` instance itself never explicitly suppressed them, so AutoCAD still treated row `0` like a title row and merged the first finding across the table.
- Fix: updated `CreateTable(...)` in `wls_program/src/WildlifeSweeps/CompleteFromPhotosService.cs` to explicitly set `table.IsTitleSuppressed = true` and `table.IsHeaderSuppressed = true` on the created table before sizing/populating rows. That keeps the first numbered finding as a normal five-column data row while leaving the rest of the table layout unchanged.
- Verification: the normal debug output build was blocked because `acad.exe` was locking `wls_program/src/WildlifeSweeps/bin/Debug/net8.0-windows/WildlifeSweeps.dll`. Verified the fix with `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug -o C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\build\verify-findings-table-row-fix`, which passed with `0` warnings and `0` errors.

## 2026-03-25 - WLS wildlife-group table layer crash
- [x] Inspect the `Upd. Finding Statement` wildlife-group table path and confirm the cause of the `eKeyNotFound` crash.
- [x] Patch the table builder so the target layer always exists before assignment, without changing the table content flow.
- [x] Build WLS, verify the fix, and record the correction plus lesson.

### Review
- Root cause: the new page-1 wildlife-group table in `wls_program/src/WildlifeSweeps/TitleBlockControlService.cs` assigned `Layer = "WLS-WILDLIFE-GROUPS"` directly during table creation, but unlike the existing Environmental Conditions flow it never ensured that layer existed first. In drawings that did not already contain that layer, AutoCAD threw `eKeyNotFound` from `Entity.Layer`.
- Fix: updated `BuildWildlifeGroupStatusTable(...)` in `wls_program/src/WildlifeSweeps/TitleBlockControlService.cs` to call `PhotoLayoutHelper.EnsureLayer(db, WildlifeGroupStatusLayerName, tr)` before constructing the table, reusing the same layer-creation helper already used elsewhere in WLS.
- Verification: verified the fix with `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug -o C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\build\verify-wildlife-group-layer-fix`, which passed with `0` warnings and `0` errors.

## 2026-03-25 - WLS findings statement bold/style and wording polish
- [x] Inspect the inserted page-1 findings statement style path and the current narrative-collapse wording to confirm the narrowest safe fixes.
- [x] Make the inserted findings statement render non-bold and improve the collapsed species wording so it reads naturally.
- [x] Verify the affected build/test paths and record the correction plus lesson.

### Review
- Root cause: the blue page-1 findings replacement was already stripping underline/overline with an inline `MText` run, but it still inherited bold font styling from the original highlighted placeholder because the replacement did not override the font weight. Separately, the builder’s collapsed labels still used noun phrases like `moose sign` and `moose sign with incidental observations`, which read stiffly in a narrative sentence.
- Fix: updated `wls_program/src/WildlifeSweeps/TitleBlockControlService.cs` so the inserted `MText` run now emits a non-bold font override based on the current text style, and DBText/attribute replacements also swap to the non-bold derived text style when they are used. Updated `wls_program/src/WildlifeSweeps/WildlifeFindingsStatementBuilder.cs` so collapsed sign labels now read as `signs of {species}`, incidental-only collapse reads `incidental observations of {species}`, and mixed sign/incidental collapse reads `signs and incidental observations of {species}`.
- Verification: `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug -o C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\build\verify-finding-text-style-wording-fix` passed with `0` warnings and `0` errors. `dotnet run --project C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps.DecisionTests\WildlifeSweeps.DecisionTests.csproj -c Debug` passed.

## 2026-03-25 - WLS wildlife-group table header color correction
- [x] Inspect the wildlife-group summary table header styling against the established title-block heading convention.
- [x] Update the generated wildlife-group table so its header uses the same ACI 14 heading color instead of the red value color.
- [x] Verify the WLS build result and record the current debug-DLL lock state.

### Review
- Root cause: the generated page-1 wildlife-group status table in `wls_program/src/WildlifeSweeps/TitleBlockControlService.cs` reused the red emphasis color from the Environmental Conditions date/value row, not the established title-block heading color convention.
- Fix: updated `WildlifeGroupStatusHeaderColor` in `wls_program/src/WildlifeSweeps/TitleBlockControlService.cs` to reuse `EnvironmentalConditionsHeadingColor`, so the wildlife-group table header now renders in the same ACI `14` heading color as the other title-block headings.
- Verification: the normal debug output build was blocked because `acad.exe` was still locking `wls_program/src/WildlifeSweeps/bin/Debug/net8.0-windows/WildlifeSweeps.dll`. Verified the code fix with `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug -o C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\build\verify-wildlife-group-header-color-fix`, which passed with `0` warnings and `0` errors.

## 2026-03-25 - WLS missing key-wildlife-features table
- [x] Inspect the `Upd. Finding Statement` flow to confirm whether the key-wildlife-features page-1 table is generated anywhere.
- [x] Add the missing page-1 key-wildlife-features table rebuild using the existing occupied-nest/dens/hibernacula/mineral-lick yes/no prompts.
- [x] Build WLS and record the verification result.

### Review
- Root cause: `Upd. Finding Statement` in `wls_program/src/WildlifeSweeps/TitleBlockControlService.cs` already prompted for `WildlifeFeatureFlags`, but after updating the narrative paragraph it only rebuilt the wildlife-group status table. The separate page-1 `KEY WILDLIFE FEATURES IDENTIFIED` table was never created from those flags, so the prompt answers had no table output.
- Fix: extended `TitleBlockControlService.cs` with a dedicated key-wildlife-features table builder/remover path at anchor `(-447.107, 593.275)`, using a merged ACI `14` heading row, four data rows (`OCCUPIED NEST`, `OCCUPIED DENS`, `HIBERNACULA`, `MINERAL LICKS`), and `YES`/`NO` values driven directly by `WildlifeFeatureFlags`. The same `Upd. Finding Statement` flow now removes prior generated copies and rebuilds this table alongside the wildlife-group status table on each run.
- Verification: the normal debug output build was blocked because `acad.exe` was still locking `wls_program/src/WildlifeSweeps/bin/Debug/net8.0-windows/WildlifeSweeps.dll`. Verified the fix with `dotnet build C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\WildlifeSweeps.csproj -c Debug -o C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\build\verify-key-wildlife-features-table-fix`, which passed with `0` warnings and `0` errors.

## 2026-03-27 - Git sync
- [x] Review project lessons and current git worktree state before syncing.
- [x] Confirm the tracked branch and remote configuration.
- [x] Fetch all remotes and fast-forward the checked-out branch.
- [x] Verify final git status and record the result.

### Review
- Fetched all remotes from origin and discovered main was behind origin/main by 13 commits.
- Fast-forwarded main from 494b38f to 40dba71 (Enhance WLS findings statement workflows).
- Upstream also changed tasks/todo.md, so I temporarily stashed the local planning note to avoid a pull conflict before the fast-forward.
- Current git status matches origin/main on the tracked branch; only the task-note update above and the pre-existing local untracked artifact directories/files remain in the worktree.

## 2026-03-27 - Repo release build
- [x] Confirm the ATS and WLS build entrypoints.
- [x] Build src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln in Release.
- [x] Build wls_program/src/WildlifeSweeps/WildlifeSweeps.sln in Release.
- [x] Record the build results.

### Review
- ATS release build succeeded for src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln. Output: src/AtsBackgroundBuilder/bin/x64/Release/net8.0-windows/AtsBackgroundBuilder.dll.
- ATS reported two CS0219 warnings for unused axisTol variables in RoadAllowance/Plugin.RoadAllowance.CorrectionLinePostProcessing.cs:1750 and RoadAllowance/Plugin.RoadAllowance.EndpointEnforcement.cs:685.
- WLS release build succeeded for wls_program/src/WildlifeSweeps/WildlifeSweeps.sln with 0 warnings and 0 errors. Output: wls_program/src/WildlifeSweeps/bin/Release/net8.0-windows/WildlifeSweeps.dll.

## 2026-03-27 - ATS 59-12-5 and 54-12-5 geometry corrections
- [x] Review the new geometry bug report, current lessons, and repo state.
- [ ] Locate the exact AutoCAD harness inputs/results for 59-12-5 and 54-12-5 and capture the current failure evidence.
- [ ] Trace the road-allowance classification, LSD endpoint, and quarter-definition landing logic that owns these coordinates.
- [ ] Implement a general logic fix instead of township-specific fallback behavior.
- [ ] Run ATS build/tests and rerun the AutoCAD harness for the affected cases.
- [ ] Record the verified results and any follow-up risk.
- [x] reviewed bug report/current lessons/repo state
- [x] locate harness inputs/results for 59-12-5 and 54-12-5 repros
- [x] trace RA / LSD / quarter geometry ownership logic
- [x] implement general ATS fixes without township-specific fallback
- [x] run ATS build, decision tests, and FullAutoCAD repro harnesses
- [x] record verification results

Review:
- ATS fix 1: preserve surveyed SEC endpoints when SEC-only station fallback already lands on the authoritative boundary; this fixed the 54-12-5 sec 33 LSD endpoint drifting to the next parallel surveyed row.
- ATS fix 2: treat shared-endpoint skew seams as continuous rows during road-allowance band consistency, and let collinear mixed SEC/USEC rows normalize together; this fixed the 59-12-5 top seam segment that remained on L-USEC3018.
- Build: dotnet build src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln -c Release succeeded (same 2 pre-existing unused-variable warnings).
- Decision tests: dotnet test src/AtsBackgroundBuilder.DecisionTests/AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build exited 0.
- FullAutoCAD 59-12-5 review passed in data/twp59-12-5-top-ra-run-rerun3/artifacts/review-report.json; segment 579094.379,6001152.867 -> 579900.263,6001167.606 is now L-SEC.
- FullAutoCAD 54-12-5 review passed in data/twp54-12-5-geometry-run-rerun2/artifacts/review-report.json; verified 585549.901,5952661.480 on L-SECTION-LSD, 588394.243,5952713.003 present, and 591006.561,5944677.112 present.## 2026-03-27 - ATS follow-up north seam and quarter-definition corrections
- [x] record follow-up repro details from user
- [ ] inspect remaining north seam misclassifications near 579930.420,6001168.144
- [ ] inspect remaining 1/4-definition misses near 590862.289,5951150.462
- [ ] implement shared logic fix
- [ ] rebuild and rerun FullAutoCAD harness
- [ ] record verification- [x] Reproduced remaining north seam misclassification at 579930.420,6001168.144 and confirmed the right-hand row was split across a 30.16 m section-boundary gap.
- [x] Widened collinear mixed-layer grouping to bridge standard 30.16 m section-break seams when both facing endpoints are anchored by classified opposite-orientation road-allowance connectors.
- [x] Preserved SEC endpoints already anchored on classified road-allowance bridge connectors so the SEC hard-boundary pass does not re-snap valid seam endpoints onto the wrong vertical.
- [x] Rebuilt ATS Release and reran FullAutoCAD verification for twp59-12-5 and twp54-12-5.

Review 2026-03-27:
- ATS build passed: C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\bin\x64\Release\net8.0-windows\AtsBackgroundBuilder.dll
- twp59-12-5 review passed in C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\data\twp59-12-5-top-ra-run-rerun6\artifacts\review-report.json
  Verified L-SEC segments at 579094.379,6001152.867 -> 579900.263,6001167.606; 579930.420,6001168.144 -> 580726.156,6001182.357; and 580726.156,6001182.357 -> 581531.905,6001196.703.
- twp54-12-5 review passed in C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\data\twp54-12-5-geometry-run-rerun3\artifacts\review-report.json
  Verified final DXF point matches at 585549.901,5952661.480; 588394.243,5952713.003; 591006.561,5944677.112; and 590862.289,5951150.462.
- Remaining build warnings are unchanged unused axisTol locals in Plugin.RoadAllowance.CorrectionLinePostProcessing.cs and Plugin.RoadAllowance.EndpointEnforcement.cs.## 2026-03-27 - ATS follow-up slanted L-SEC quarter-definition landing
- [ ] Reproduce the slanted L-SEC 1/4-definition miss around 585999.114,5951069.661 / 586036.287,5951070.218 versus 586028.068,5951069.999.
- [ ] Trace the quarter-definition target logic for slanted section lines and identify why the landing stays on the wrong sloped SEC edge.
- [ ] Implement a general slanted-section fix without township-specific fallback.
- [ ] Rebuild ATS and rerun full AutoCAD verification with an explicit guard for the corrected 1/4-definition landing.
- [x] Inspected the current twp54-12-5 slanted section geometry: the verified harness output already contains the shared L-SEC point 586028.068,5951069.999, so the exact screenshot coordinates did not reproduce from the local stored runs.
- [x] Added a general quarter-line junction snap so L-QSEC endpoints that already touch a slanted section boundary can still promote to a nearby shared boundary junction when that is the better landing.
- [x] Added an explicit slanted-point review guard at 586028.068,5951069.999 and reran full AutoCAD verification.

Review 2026-03-27 (slanted L-SEC follow-up):
- ATS build passed: C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\bin\x64\Release\net8.0-windows\AtsBackgroundBuilder.dll
- twp54-12-5 review passed in C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\data\twp54-12-5-geometry-run-rerun4\artifacts\review-report.json
  Verified the new guard point at 586028.068,5951069.999 plus the prior geometry guards.
- twp59-12-5 regression review passed in C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\data\twp59-12-5-top-ra-run-rerun7\artifacts\review-report.json
- Local gap: the exact screenshot coordinates 585999.114,5951069.661 and 586036.287,5951070.218 do not appear in the stored harness outputs, so the new junction-snap rule is preventative here and still needs the specific user DWG/run to prove against the reported miss.- [ ] Clarified repro: NE.NE of 28-54-12-5 and SE.SE of 33-54-12-5 should both land at 586028.068,5951069.999.- [x] Map the named legal corners (NE.NE sec 28 / SE.SE sec 33) back to the quarter-view east-corner generator and add a shared endpoint-node snap for slanted east-side quarter corners.
- [x] Rebuild ATS, rerun 54-12-5 Full AutoCAD with an explicit DAB_APPL guard at 586028.068,5951069.999, and rerun 59-12-5 as regression.

Review 2026-03-27 (slanted east-corner endpoint snap):
- Root cause: quarter-view east-side corners were not symmetric with the stronger west/blind paths. Ordinary NE corners and SE corners could keep a strict/apparent slanted intersection even when a real hard endpoint node already existed at the shared SEC junction.
- Fix: added a general east-corner endpoint-cluster snap in src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs and applied it to non-correction NE corners plus non-correction SE corners, so slanted east-side quarter corners prefer the shared endpoint node when horizontal and vertical endpoint evidence exists.
- Build: dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal passed with the same 2 pre-existing unused xisTol warnings.
- Decision tests: dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build passed.
- FullAutoCAD 54-12-5 review passed in C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\data\twp54-12-5-geometry-run-rerun5\artifacts\review-report.json, including the shared point 586028.068,5951069.999 on both L-SEC and DAB_APPL.
- FullAutoCAD 59-12-5 regression review passed in C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\data\twp59-12-5-top-ra-run-rerun8\artifacts\review-report.json.## 2026-03-27 - ATS follow-up surveyed RA quarter-definition regression and 59-12-5 northern seam
- [ ] Reproduce the new surveyed-section quarter-definition misses from the latest Full AutoCAD output, including 589359.772,5946278.332 vs 589360.122,5946258.226, 587593.733,5952698.833 vs 587573.623,5952698.477, and 588413.423,5952713.884 vs 588394.243,5952713.003.
- [ ] Reproduce the 59-12-5 northern seam miss where L-SEC lands at 579930.420,6001168.144 instead of tying into the existing L-USEC-2012 end near 579920.370,6001167.954.
- [ ] Fix the root causes without township-specific fallback and keep running Full AutoCAD verification until the exact coordinates pass again.
- [ ] Confirm whether disposition import is already disabled in the harness path and keep the verification path as lean as possible.

## 2026-03-27 - ATS surveyed RA helper-line endpoint reconciliation
- [x] Reproduce the surveyed 54-12-5 quarter-definition misses with exact review guards, including the stricter segment check at 588394.243,5952713.003 -> 587573.623,5952698.477.
- [x] Reproduce the 59-12-5 north road-allowance seam miss where the visible L-SEC helper stayed at 579930.420,6001168.144 instead of tying into the live L-USEC-2012 seam endpoint at 579920.370,6001167.954.
- [x] Fix the shared late-stage helper-line root cause without township-specific fallback.
- [x] Rebuild ATS, rerun FullAutoCAD until both exact geometry reviews pass, and rerun decision tests.
- [x] Record whether disposition import is already out of the harness path.

Review 2026-03-27 (late L-SEC helper endpoint reconciliation):
- Root cause: the quarter-view solver was already resolving the correct surveyed/slanted corners, but a later-visible L-SEC helper path still preserved raw section-box endpoints. In 54-12-5 that left the stale segment 588394.243,5952713.003 -> 587593.733,5952698.833 in the DXF; in 59-12-5 the northern seam helper stayed at 579930.420,6001168.144 instead of snapping onto the live L-USEC-2012 seam endpoint.
- Fix 1: expanded the final L-SEC endpoint-on-hard pass so authoritative nearby fabric endpoints can override a raw bridge-anchor preserve when the better live target is already present.
- Fix 2: added a final L-SEC helper snap alongside quarter-view rebuild, driven only by protected quarter-derived corners plus live hard road-allowance endpoints (`L-USEC-0`, `L-USEC-20`, `L-USEC-2012`, correction-zero), so stale L-SEC raw corner clusters cannot self-certify.
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed with the same two pre-existing unused `axisTol` warnings.
- Decision tests: `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- FullAutoCAD 54-12-5 review passed in `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\data\twp54-12-5-geometry-run-rerun15\artifacts\review-report.json`, including the exact L-SEC segment `588394.243,5952713.003 -> 587573.623,5952698.477` and the earlier guarded quarter-definition points.
- FullAutoCAD 59-12-5 review passed in `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\data\twp59-12-5-top-ra-run-rerun10\artifacts\review-report.json`, including the corrected northern seam segment `579920.370,6001167.954 -> 580726.156,6001182.357`.
- Harness note: disposition import was already effectively out of this verification path; the ATS workbook-driven FullAutoCAD repros ran with no imported XML disposition files, so no extra disablement was required.

## 2026-03-27 - ATS correction after L-SEC movement regression report
- [x] Re-open the just-verified change set after the user reported that broad L-SEC movement made the fabric worse and caused crossings over road allowances.
- [x] Remove the broad late L-SEC helper-line mover and restore the default behavior that visible L-SEC fabric stays where it already belongs.
- [x] Keep only the narrow exception that allows L-SEC to extend to a live `L-USEC-2012` seam hit when needed.
- [x] Rebuild ATS, rerun the affected FullAutoCAD cases, and rerun decision tests.

Review 2026-03-27 (user-corrected L-SEC constraint):
- User clarification: visible `L-SEC` lines generally should not move. The only acceptable exception here is the rare case where an `L-SEC` endpoint needs to extend to hit a live `L-USEC-2012` seam.
- Fix: removed the broad quarter-view-stage `L-SEC` helper snap and rolled the endpoint behavior back to the earlier hard-boundary pass, then narrowed the only added exception so a window-edge `L-SEC` can overrun the normal inset guard only when the target seam is specifically `L-USEC-2012`.
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed with the same two pre-existing unused `axisTol` warnings.
- Decision tests: `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- FullAutoCAD 59-12-5 review passed in `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\data\twp59-12-5-top-ra-run-rerun11\artifacts\review-report.json`, preserving the intended narrow `L-USEC-2012` seam extension at `579920.370,6001167.954 -> 580726.156,6001182.357`.
- FullAutoCAD 54-12-5 review passed in `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\data\twp54-12-5-geometry-run-rerun17\artifacts\review-report.json` after replacing the bad visible-`L-SEC` proxy with the actual shared endpoint guard at `587573.623,5952698.477`.

## 2026-03-27 - Repo release build rerun
- [x] Review project lessons and current build entrypoints.
- [x] Build `src/AtsBackgroundBuilder/AtsBackgroundBuilder.sln` in Release.
- [x] Build `wls_program/src/WildlifeSweeps/WildlifeSweeps.sln` in Release.
- [x] Record the build results.

Review 2026-03-27 (repo release build rerun):
- ATS release build passed: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release /m:1 -v:minimal`.
- ATS output: `C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\src\AtsBackgroundBuilder\bin\x64\Release\net8.0-windows\AtsBackgroundBuilder.dll`.
- ATS decision-test project also built as part of the solution: `C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\src\AtsBackgroundBuilder.DecisionTests\bin\Release\net8.0\AtsBackgroundBuilder.DecisionTests.dll`.
- ATS reported the same two pre-existing CS0219 warnings for unused `axisTol` locals in `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.CorrectionLinePostProcessing.cs:1750` and `src/AtsBackgroundBuilder/RoadAllowance/Plugin.RoadAllowance.EndpointEnforcement.cs:733`.
- WLS release build passed: `dotnet build .\wls_program\src\WildlifeSweeps\WildlifeSweeps.sln -c Release /m:1 -v:minimal`.
- WLS output: `C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\wls_program\src\WildlifeSweeps\bin\Release\net8.0-windows\WildlifeSweeps.dll`.
- WLS finished with `0` warnings and `0` errors.

## 2026-03-27 - ATS 54-12-5 L-QUATER endpoint follow-up
- [x] Review the recent 54-12-5 quarter-definition lessons and prior verification notes.
- [x] Reproduce the exact bad `L-QUATER` endpoints landing at `588413.423,5952713.884` instead of `588394.243,5952713.003`.
- [x] Trace the shared quarter-view target-selection path that is still allowing the stale endpoint cluster to win.
- [x] Implement a general ATS fix without section-specific fallback behavior.
- [x] Rerun focused AutoCAD verification for the exact 54-12-5 drawing once the local DWG/harness inputs are available in this workspace.

Review 2026-03-27 (ATS 54-12-5 L-QUATER endpoint follow-up):
- Root cause: the bad point was not coming from late `L-SEC` helper movement this time. In the sec 35 quarter-view path inside `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs`, the shared north-mid `L-QUATER` corner only snapped to the divider when a north boundary segment had been resolved. In the reproduced runtime log, sec 35 was on `northSource=fallback-north`, so `northAtMidU` stayed on the synthetic `centerU` fallback even though the live `L-QSEC` divider already had the correct north endpoint at `588394.243,5952713.003`. Because that north-mid point was also not part of the protected quarter-corner set, the two quarter boxes sharing it could finish on the stale shifted point the user reported.
- Fix: when quarter view has a live `L-QSEC` divider but no resolved north boundary segment, the code now prefers the divider's north endpoint as the shared north-mid authority when it is the better local fit. The final `L-QUATER` box post-snap also now protects north-mid shared corners the same way it already protects south-mid and east/west boundary corners, so the two quarter boxes cannot drift together onto a stale endpoint cluster afterward.
- Review guard: updated `data/twp54-12-5-geometry-review.json` so future focused harness runs must match the two `L-QUATER` north-edge segments `587573.623,5952698.477 -> 588394.243,5952713.003` and `588394.243,5952713.003 -> 589216.464,5952731.171`, not just the old `L-SEC`/`L-QSEC` point check.
- Verification completed locally:
- `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed with the same two pre-existing CS0219 `axisTol` warnings.
- `dotnet .\src\AtsBackgroundBuilder.DecisionTests\bin\Release\net8.0\AtsBackgroundBuilder.DecisionTests.dll` passed.
- `Get-Content data\twp54-12-5-geometry-review.json | ConvertFrom-Json` passed, confirming the tightened review config is valid JSON.
- AutoCAD proof update 2026-03-28:
- FullAutoCAD rerun now works locally from `C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0` using `scripts\atsbuild_harness.ps1` plus the local shell DWG `data\quarter-correctionline-run\accore-isolate\Local\Template\Generic 24in x 36in Title Block.dwg`.
- The first real FullAutoCAD proof in `data\twp54-12-5-geometry-run-proof-autocad\artifacts\review-report.json` reproduced the issue cleanly: with default XLS batch cleanup, `L-QUATER` was erased before DXF review and the tightened segment guards only found the stale `L-SEC` segments.
- Root-cause follow-up: the north-mid fix was already working, but the sec 35 NE `L-QUATER` corner was still being moved in the final snap pass because east-side quarter corners were only protected when an explicit north boundary segment existed. That let a fallback-north corner drift onto a nearby stale hard endpoint.
- Final fix: keep the computed east-side NE quarter corner in the protected-corner set whenever an east boundary segment has been resolved, matching the existing west-side protection behavior and preventing the late snap pass from overriding the intended corner.
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed again with the same two pre-existing CS0219 `axisTol` warnings.
- Decision tests: `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- FullAutoCAD proof with quarter view preserved (`ATSBUILD_QUATERVIEW=1`) now matches the exact reported `L-QUATER` north-edge segments in `data\twp54-12-5-geometry-run-proof-autocad-qv-rerun2\artifacts\review-report.json`.
- Focused proof review for this bug passes in `data\twp54-12-5-geometry-run-proof-autocad-qv-rerun2\artifacts\lquater-proof-review.json` using `data\twp54-12-5-lquater-proof-review.json`.
- Remaining unrelated gap: the broader `data\twp54-12-5-geometry-review.json` still contains a legacy `DAB_APPL` endpoint guard at `586028.068,5951069.999` that fails in this harness template even though the quarter-line proof now passes.

## 2026-03-28 - ATS 54-12-5 follow-up shared quarter-corner corrections
- [x] Reproduce the remaining user-reported quarter-corner misses for `SW.SW 34-54-12-5` and `NE.NE 35-54-12-5` from the latest FullAutoCAD proof artifacts.
- [x] Trace the shared quarter-corner resolution path and identify why those corners still choose the wrong hard node.
- [x] Implement a general fix without section-specific fallback behavior.
- [x] Rebuild ATS and rerun focused FullAutoCAD proof with explicit guards for the corrected `SW.SW 34` and `NE.NE 35` endpoints.

Review 2026-03-28 (shared quarter-corner corrections):
- User correction: the right legal targets were `SW.SW 34 -> 586028.068,5951069.999` and `NE.NE 35 -> 589194.148,5952728.097`; my earlier focused proof had the `NE.NE 35` target wrong.
- Root cause 1: `SW.SW 34` was still resolving from the raw west/south section-box geometry even though the adjacent section had already produced the correct inset east-side quarter corner on the shared boundary. The old hard-boundary-only west-band snap could not see that prior quarter-derived owner.
- Root cause 2: `NE.NE 35` was in the `northSource=fallback-north` path, and the east-band hard-node scorer still preferred the outer east endpoint when it scored against the raw east boundary location instead of the legal quarter inset target.
- Fix 1: added a general west-corner handoff that can reuse a previously resolved neighboring east quarter corner on the same shared section boundary when that yields the correct quarter inset.
- Fix 2: changed the endpoint-evidence east/west band snap scoring to prefer the legal quarter inset target instead of the raw outer boundary position, added a fallback north-band east snap for no-north-segment cases, and only protect the east NE quarter corner after that stronger resolution actually wins.
- Fix 3: corrected `scripts/atsbuild_review.py` so `segment_match` filters by the expected layer before choosing the best candidate; overlapping `L-SEC`/`L-QUATER` segments were causing false review failures.
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed with the same two pre-existing CS0219 `axisTol` warnings.
- Decision tests: `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- FullAutoCAD proof with quarter view preserved passed in `data\twp54-12-5-geometry-run-proof-autocad-qv-rerun5\artifacts\lquater-proof-review.json`, verifying `SW.SW 34`, the shared north-mid at `588394.243,5952713.003`, `NW.NW 35 -> 587573.623,5952698.477`, and `NE.NE 35 -> 589194.148,5952728.097`.
- The updated broad review in `data\twp54-12-5-geometry-run-proof-autocad-qv-rerun5\artifacts\geometry-review-after-followup.json` now only fails the legacy `DAB_APPL` endpoint guard; the quarter-corner geometry checks all pass.

## 2026-03-28 - ATS follow-up southwest-corner regression after shared-corner fix
- [x] Reproduce the new reported southwest-corner regression around `581240.123,5946127.244` vs `581220.404,5946106.821` in the latest AutoCAD proof output.
- [x] Identify why the new shared southwest-corner handoff is catching a corner that should stay on its original owner.
- [x] Tighten the general southwest-corner rule without undoing the verified `SW.SW 34` / `NE.NE 35` fixes.
- [x] Rebuild ATS and rerun focused AutoCAD proof so the original corrected corners and this new southwest example all pass together.

Review 2026-03-28 (southwest-corner regression after shared-corner fix):
- Root cause: the first shared-corner generalization only searched `protectedEastBoundaryCorners`, which is enough when the corrected `SW.SW` corner is owned by the west-adjoining section on the same row, but not when the corrected owner is the section directly south. The bad `L-QUATER` west edge `581204.567,5946928.582 -> 581240.123,5946127.244` needed to inherit the previously resolved west-side corner at `581220.404,5946106.821` from the lower adjoining section, not an east-side protected corner.
- Fix: generalized the shared southwest-corner reuse path in `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs` to try protected east-side owners first and then protected west-side owners, preserving the verified `SW.SW 34` behavior while allowing north/south shared-boundary handoff for cases like this one. Quarter verify logs now record whether the shared corner came from an `east` or `west` protected owner.
- Proof guard: tightened `data/twp54-12-5-lquater-proof-review.json` and `data/twp54-12-5-geometry-review.json` with the unique `L-QUATER` west-edge segment `581204.567,5946928.582 -> 581220.404,5946106.821`. Using the shared boundary edge alone would have been a false pass because the neighboring quarter box already emitted that segment.
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed with the same two pre-existing CS0219 `axisTol` warnings.
- Decision tests: `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- FullAutoCAD proof with quarter view preserved passed in `data\twp54-12-5-geometry-run-proof-autocad-qv-rerun7\artifacts\lquater-proof-review.json`, verifying the new west-edge segment plus the earlier `SW.SW 34`, shared north-mid, `NW.NW 35`, and `NE.NE 35` guards together.
- The broader `data\twp54-12-5-geometry-run-proof-autocad-qv-rerun7\artifacts\review-report.json` still only fails the pre-existing legacy `DAB_APPL` point guard at `586028.068,5951069.999`; the quarter-corner geometry checks now all pass.

## 2026-03-29 - ATS sec 6 southwest quarter-corner follow-up
- [x] Reproduce the new `581302.492,5942891.067` vs `581282.777,5942870.514` southwest-corner miss from the latest quarter-view AutoCAD proof.
- [x] Trace which southwest-corner snap path is still dragging that corner onto the wrong hard node.
- [x] Tighten the general southwest-corner logic without undoing the previously verified shared-corner fixes.
- [x] Rebuild ATS and rerun the quarter-view AutoCAD proof with an explicit guard for this sec 6 corner and the earlier quarter-corner fixes.

Review 2026-03-29 (sec 6 southwest quarter-corner follow-up):
- Root cause: the south-band inset scorer in the quarter-corner fallback helpers was still using the wrong sign for south-side distance (`v - SouthEdgeV`) while the construction logic and diagnostics treat south inset as outward distance from the south edge (`SouthEdgeV - v`). That made the legal sec 6 southwest inset corner look worse than a near-edge hard endpoint, so late fallback snaps could still pull `SW.SW` onto `581302.492,5942891.067` or `581282.389,5942890.619`.
- Fix: corrected the south-band inset sign in the west hard-boundary fallback, the shared protected-corner scorer, and the matching east-side south-band scorer inside `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs`. This keeps fallback scoring aligned with the same quarter-inset target the primary geometry logic uses, instead of favoring stale endpoint nodes near the section edge.
- Proof guard: tightened `data/twp54-12-5-lquater-proof-review.json` and `data/twp54-12-5-geometry-review.json` with the unique sec 6 `L-QUATER` west-edge segment `581266.901,5943694.223 -> 581282.777,5942870.514`, so the proof checks the actual quarter box edge rather than a shared point that could already exist on another entity.
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed with the same two pre-existing CS0219 `axisTol` warnings.
- Decision tests: `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- FullAutoCAD proof with quarter view preserved passed in `data\twp54-12-5-geometry-run-proof-autocad-qv-rerun10\artifacts\lquater-proof-review.json`, verifying the new sec 6 west-edge segment plus the earlier `SW.SW 34`, shared north-mid, `NW.NW 35`, and `NE.NE 35` guards together.
- The broader `data\twp54-12-5-geometry-run-proof-autocad-qv-rerun10\artifacts\review-report.json` still only fails the pre-existing legacy `DAB_APPL` point guard at `586028.068,5951069.999`; the quarter-corner geometry checks now all pass, including the `L-QUATER` sec 6 edge landing at `581282.777,5942870.514`.

## 2026-03-29 - ATS 54-11-6 southwest quarter-corner follow-up
- [x] Reproduce the reported `SW.SW 1-54-11-6` miss from `334744.867,5945107.646` to `334744.160,5945087.548` in a focused proof run.
- [x] Trace which southwest-corner snap or shared-corner path is still pulling that corner off the correct shared owner.
- [x] Tighten the general southwest-corner logic without adding a township- or section-specific exception.
- [x] Rebuild ATS and rerun focused AutoCAD proof for `54-11-6` until the unique `L-QUATER` west-edge guard passes.

Review 2026-03-29 (ATS 54-11-6 southwest quarter-corner follow-up):
- Root cause: `SW.SW 1` was another shared-owner order problem. The sec 1 west/south corner was being drawn and hard-snapped before sec 2 later produced the correct shared `SE.SE` corner at `334744.160,5945087.548`, so the existing "reuse a prior protected corner" logic never had a chance to repair sec 1 in the same frame pass. The trace showed sec 1 locking to `334744.867,5945107.646` while sec 2 later emitted the right owner on the same shared boundary.
- Fix: added a post-draw west-boundary shared-corner reconciliation pass in `src/AtsBackgroundBuilder/Sections/Plugin.Sections.SectionDrawingLsd.cs`. After all quarter boxes are drawn and the full protected-corner sets exist, the pass revisits west-side `SW`/`NW` quarter-box corners and reapplies the existing protected-corner scoring with full neighboring context. This keeps the fix general and removes the remaining section-order blind spot without adding a township-specific rule.
- Proof guard: added `data/twp54-11-6-geometry-spec.json` and the focused proof config `data/twp54-11-6-lquater-proof-review.json`, using the unique `L-QUATER` west-edge segment `334773.095,5945911.548 -> 334744.160,5945087.548` so the proof checks the actual sec 1 quarter box edge instead of the shared point alone.
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed with the same two pre-existing CS0219 `axisTol` warnings.
- Decision tests: `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- FullAutoCAD proof with quarter view preserved passed in `data\twp54-11-6-geometry-run-proof-autocad-qv-rerun2\artifacts\lquater-proof-review.json`, verifying the sec 1 `L-QUATER` west edge now lands at `334744.160,5945087.548`.
- Regression check: reran the earlier `54-12-5` focused quarter-corner proof in `data\twp54-12-5-geometry-run-proof-autocad-qv-rerun11\artifacts\lquater-proof-review.json`; all previously guarded `L-QUATER` corners still pass with the new post-draw reconciliation enabled.

## 2026-03-29 - ATS 54-11-6 southwest quarter-corner sec 6 follow-up
- [x] Reproduce the reported `SW.SW 6-54-11-6` miss from `326622.533,5945398.767` to `326621.794,5945378.670` in the focused AutoCAD proof.
- [x] Trace why the southwest hard-boundary snap is replacing the correct 30 m south inset with a shallower endpoint candidate.
- [x] Tighten the general quarter-corner inset scoring so hard-boundary snaps use the resolved ownership offsets instead of a fixed SEC-width target.
- [x] Rebuild ATS and rerun the `54-11-6` proof plus the prior `54-12-5` regression proof until all guarded `L-QUATER` edges pass together.

Review 2026-03-29 (ATS 54-11-6 southwest quarter-corner sec 6 follow-up):
- Root cause: the west hard-boundary snap helper was still scoring `SW.SW` west-band candidates against a fixed SEC-width inset target, even after quarter-view ownership had already resolved both the west and south boundaries to USEC-width for sec 6. That made the correct apparent corner at roughly `30 m / 30 m` inset score worse than a nearby hard endpoint at roughly `30 m / 10 m`, so the late snap pulled `SW.SW 6` north from `326621.794,5945378.670` to `326622.533,5945398.767`.
- Fix: threaded the resolved ownership offsets into `TryResolveWestBandCornerFromHardBoundaries` and compared both the current corner and candidate corners against those live west/south inset targets instead of hard-coding `RoadAllowanceSecWidthMeters`. This keeps the fix general for surveyed quarter corners that legitimately resolve to USEC-width on the west and south sides.
- Proof guard: extended `data/twp54-11-6-lquater-proof-review.json` with the unique sec 6 `L-QUATER` west-edge segment `326652.145,5946202.652 -> 326621.794,5945378.670` while retaining the earlier sec 1 guard.
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed with the same two pre-existing CS0219 `axisTol` warnings.
- Decision tests: `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- FullAutoCAD proof with quarter view preserved passed in `data\twp54-11-6-geometry-run-proof-autocad-qv-rerun3\artifacts\review-report.json`, verifying both the earlier sec 1 west-edge guard and the new sec 6 west-edge guard together.
- Regression check: reran the earlier `54-12-5` focused quarter-corner proof in `data\twp54-12-5-geometry-run-proof-autocad-qv-rerun12\artifacts\review-report.json`; all previously guarded `L-QUATER` corners still pass.
## 2026-03-31 - Repo release build
- [x] Review current repo state before building.
- [x] Build ATS release solution.
- [x] Build WLS release solution.
- [x] Record outputs and warnings.

Review 2026-03-31:
- ATS release build passed: `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\bin\x64\Release\net8.0-windows\AtsBackgroundBuilder.dll`
- ATS warnings are unchanged: `Plugin.RoadAllowance.CorrectionLinePostProcessing.cs:1750` and `Plugin.RoadAllowance.EndpointEnforcement.cs:733` both report unused `axisTol` locals (`CS0219`).
- WLS release build passed: `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\wls_program\src\WildlifeSweeps\bin\Release\net8.0-windows\WildlifeSweeps.dll`
- WLS finished with `0` warnings and `0` errors.

## 2026-03-31 - ATS final L-USEC output relayer
- [x] Inspect the ATS post-build pipeline and find the true last drawing-mutation stage.
- [x] Add a final output-only relayer so `L-USEC-0`, `L-USEC-2012` / `L-USEC2012`, and `L-USEC-3018` / `L-USEC3018` linework becomes `L-USEC` after all build logic is complete.
- [x] Move any final diagnostic export that should reflect the completed drawing so it runs after the relayer.
- [x] Rebuild ATS and record the verified result plus the new standing rule.

Review 2026-03-31 (final L-USEC output relayer):
- Hook point: added the relayer in the tail of `ExecutePostQuarterPipeline` after cleanup, optional surface-impact work, and aligned-dimension text finalization, so it is the last drawing-mutation step before completion.
- Behavior: model-space curve entities on `L-USEC-0`, `L-USEC-2012` / `L-USEC2012`, and `L-USEC-3018` / `L-USEC3018` now relayer to `L-USEC` in one final output-only pass. This does not change build-time logic; the subtype layers stay available until all geometry decisions are done.
- Output/export ordering: moved optional CAD GeoJSON export to run after the final relayer so diagnostics reflect the completed drawing state.
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed. Output: `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\bin\x64\Release\net8.0-windows\AtsBackgroundBuilder.dll`
- Decision tests: `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- Remaining warnings are unchanged `CS0219` unused `axisTol` locals in `RoadAllowance\Plugin.RoadAllowance.CorrectionLinePostProcessing.cs:1750` and `RoadAllowance\Plugin.RoadAllowance.EndpointEnforcement.cs:733`.

## 2026-03-31 - ATS P3 hydro import outside requested work area
- [x] Inspect the P3 shapefile import/location-window path and identify why hydro entities can survive outside the requested quarter work area.
- [x] Implement a minimal fix so P3 output is clipped/filtered to the real requested work area instead of a broader section envelope.
- [x] Rebuild ATS and record the verification result plus the new lesson.

Review 2026-03-31 (P3 hydro work-area scope):
- Root cause: the P3, Compass Mapping, and Crown Reservation imports were all being windowed against `sectionDrawResult.SectionPolylineIds`, so quarter-only builds still gave those shape imports a full-section work area.
- Fix: resolved the requested build scope once up front using the existing quarter-aware scope helper and reused that same scope for P3, Compass Mapping, Crown Reservations, and disposition imports. P3’s import helper was also renamed internally from section extents to requested work area extents so the location-window and post-import filter stay aligned with the real scope.
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed. Output: `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\bin\x64\Release\net8.0-windows\AtsBackgroundBuilder.dll`
- Decision tests: `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- Remaining warnings are unchanged `CS0219` unused `axisTol` locals in `RoadAllowance\Plugin.RoadAllowance.CorrectionLinePostProcessing.cs:1750` and `RoadAllowance\Plugin.RoadAllowance.EndpointEnforcement.cs:733`.
- Verification note: I did not run a fresh AutoCAD screenshot repro here, so this closes the obvious scope bug in code and compile/test verification, but the next real P3 build is the proof for your exact hydro feature.

Follow-up 2026-03-31 (corrected P3 diagnosis after user report):
- User correction: the stray hydro feature is not merely a scope-id issue; the surviving linework is tens of kilometres long, which means a feature is brushing the build area and then remaining whole.
- Corrected fix: added a true post-import clip/replace path for P3 geometry. Closed imported boundaries are intersected against the requested work-area windows, and open imported lines/polylines are clipped segment-by-segment to those windows and rebuilt from the clipped runs. That prevents a long hydro feature from surviving intact just because one small portion overlaps the requested area.
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed again. Output remains `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\bin\x64\Release\net8.0-windows\AtsBackgroundBuilder.dll`
- Decision tests: `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed again.
- Remaining proof gap: I still have not run a fresh AutoCAD repro of your screenshot in this turn, so the next P3-enabled build is the proof for the exact hydro line you showed.

Follow-up 2026-03-31 (P3 clipping performance correction):
- User correction: the full clip/replace pass made P3 builds run too long, so that tradeoff is not acceptable.
- Performance fix: added a fast scope-overlap classifier and now only run the expensive clip/replace path for partial-overlap P3 entities. Features fully outside the requested work area are erased immediately, and features fully inside it stay on the cheap keep/relabel path. Open hydro lines also bypass the polygon-clipping path and go straight to line clipping only when they truly overrun the work area.
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed again. Output remains `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\bin\x64\Release\net8.0-windows\AtsBackgroundBuilder.dll`
- Decision tests: `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed again.
- Remaining proof gap: I still have not run a fresh AutoCAD P3 repro of the slow case in this turn, so the next real P3 build is the proof for both runtime and the exact stray hydro line.

## 2026-03-31 - ATS live P3 slowdown investigation
- [x] Inspect the current live ATS log and determine the last confirmed stage reached by the hanging/slow run.
- [x] Add finer-grained P3 progress logging so the next live run shows whether time is being spent in importer initialization, location-window setup, raw import, or post-import filtering.
- [x] Tighten the P3 post-import path so obviously out-of-scope hydro does not pay polygon-conversion cost before rejection.
- [x] Rebuild ATS and record the updated diagnosis and verification notes.

Review 2026-03-31 (live P3 slowdown investigation):
- Live log diagnosis: the fresh build log at `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\build\net8.0-windows\AtsBackgroundBuilder.log` stops at `ATSBUILD stage: p3_import`. The logger does flush stage markers immediately, so the last confirmed stage reached by the slow run was P3 import.
- Session evidence: fresh Map 3D session logs show `C:\Users\Jesse 2025\Desktop\01467-24-PLA-R0.dwg` opened at about `11:10 AM`, but there was no matching fatal AutoCAD error detail in the Map error logs and the DWG itself was not saved later. That means the old log could not distinguish whether time was being spent inside `importer.Init` / `SetLocationWindowAndOptions` / `importer.Import` or our own post-import filtering.
- Logging fix: added per-file P3 progress markers around model-space pre-scan, importer init, location-window apply, input-layer enablement, raw import, post-import scan, and post-process completion in `src/AtsBackgroundBuilder\Core\Plugin.Core.ImportWindowing.cs`, so the next live run will identify the exact substep and timing.
- Performance fix: moved scope-overlap classification ahead of polygon conversion so entities that are obviously outside the requested work area are erased before any polygon explode/convert cost is paid. Partial-overlap clipping still runs when needed, but we no longer convert clearly out-of-scope hydro just to throw it away afterward.
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed. Output: `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\bin\x64\Release\net8.0-windows\AtsBackgroundBuilder.dll`
- Decision tests: `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- Remaining proof gap: I have not rerun the live AutoCAD P3 build in this turn, so the next actual run is still needed to prove both the improved runtime and the exact substep timing in the updated log.

Follow-up 2026-03-31 (user-corrected P3 rollback toward baseline):
- User correction: even the reduced partial-overlap clipping path is still too slow in practice, so the extra geometry surgery is not worth it for this workflow.
- Simplification: removed the expensive P3 clip/replace path entirely and rolled the hot path back toward the old keep-or-erase behavior, while preserving the better requested-quarter scope selection and the cheap early out-of-scope rejection before polygon conversion. In other words, P3 now keeps the quarter-aware scope and the fast reject, but no longer rebuilds imported geometry to trim partial overlaps.
- Logging: the new `P3 import ...` and `P3 importer ...` diagnostics now flush immediately so future slow runs will actually show which P3 substep is consuming time instead of stopping visibly at the coarse `p3_import` stage.
- Verification: the new source DLL compiled successfully to `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\src\AtsBackgroundBuilder\bin\x64\Release\net8.0-windows\AtsBackgroundBuilder.dll` at `2026-03-31 11:36:55 AM`, but the normal build copy into `build\net8.0-windows` failed because the currently running AutoCAD session (`acad.exe` PID `45824`) is still locking `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\build\net8.0-windows\AtsBackgroundBuilder.dll`.
- Current state: the live AutoCAD run is still using the older slower DLL. The lightweight rollback is ready in the source output, but it cannot be deployed to the standard build folder until that AutoCAD session exits and releases the file lock.

Follow-up 2026-03-31 (rebuild after AutoCAD closed):
- After the user closed AutoCAD, `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` completed successfully and the shared build output was updated.
- `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- Active deployed ATS DLL: `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\build\net8.0-windows\AtsBackgroundBuilder.dll`

Follow-up 2026-03-31 (fresh P3 log proved bbox false positives):
- Fresh log proof: the requested P3 location window is correct and tightly scoped to the quarter work area (`X[527599.761...,529434.455...] Y[5976152.128...,5977969.508...]`), so the bad hydro is not coming from the wrong quarter scope.
- Fresh log proof: Map 3D still imported `88599` polygon entities and `328942` arc entities inside that location-window request, but the cheap extents filter only kept `2` polygons and `7` arcs. The screenshot false positives are therefore surviving because their bounding boxes overlap the requested window even though the actual geometry does not touch it.
- Fix: keep the fast quarter-aware scope and cheap extents filter, then add one narrow actual-touch confirmation only for `Partial` survivors. Open paths now have to clip at least one segment to the requested window, and closed boundaries now have to intersect or contain the requested window before they are kept. This removes the far-away bbox survivors without reintroducing the slow geometry rebuild path.
- Verification: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed with the same two pre-existing `axisTol` warnings. `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.

Follow-up 2026-03-31 (live run proved closed-boundary touch check is still too expensive):
- Live log proof: the current run reaches `P3 importer import complete` for `BF_Hydro_Polygon.shp` in about `66459 ms`, reaches `P3 import post-scan complete` with `newIds=88732`, and then stalls inside post-process before the `post-process complete` line. That means the new slowdown is in polygon post-filtering, not in raw Map import.
- Root cause: the earlier actual-touch confirmation was being applied to closed hydro polygons as well as open hydro arcs. For `88732` polygon candidates that is too expensive, and it defeats the goal of staying close to baseline runtime.
- Narrow rollback: reserve actual-touch validation only for open hydro paths (`Line` and non-closed `Polyline`), which are the class that produced the far-away river survivors. Closed boundaries fall back to the fast extents-based keep/erase path again.
- Verification note: while the live AutoCAD session is still running, I verified the source change with `dotnet msbuild .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj /t:Compile /p:Configuration=Release /p:Platform=x64 /v:minimal`. The source output compiled with the same two pre-existing `axisTol` warnings, but the standard deployed `build\net8.0-windows` DLL cannot be updated until AutoCAD releases its lock.

Follow-up 2026-03-31 (deployed open-path-only rollback):
- After AutoCAD closed, `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` succeeded and updated the shared deployed DLL.
- `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- Active deployed ATS DLL: `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\build\net8.0-windows\AtsBackgroundBuilder.dll` (`2026-03-31 11:55:50 AM`)

Follow-up 2026-03-31 (trim surviving open hydro instead of keeping whole line):
- User report: after restoring performance, the large far-away hydro shapes still survived because the long river path genuinely touched the requested window somewhere and was therefore kept whole.
- Final narrow fix: for `Partial` open-path hydro survivors only, keep the fast touch validation but then replace the original entity with clipped-in-window polyline pieces. Closed polygons still stay on the fast path; only the surviving open river lines get trimmed.
- Verification: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed with the same two pre-existing `axisTol` warnings, and `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- Active deployed ATS DLL: `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\build\net8.0-windows\AtsBackgroundBuilder.dll` (`2026-03-31 12:05:56 PM`)

## 2026-03-31 - ATS P3 section 23-57-18-5 outside-linework verification
- [x] Create a dedicated AutoCAD harness repro for section `23-57-18-5` using `C:\Users\Jesse 2025\Desktop\01467-24-PLA-R0.dwg`.
- [x] Add a repeatable DXF review for stray new `T-WATER-P3` linework outside the requested 100m work-area buffer while preserving legitimate existing P3 entities.
- [x] Inspect the failing AutoCAD output to identify why far-away `BF_SLNET_arc.shp` geometry still survives.
- [x] Implement the minimal P3 import/filter fix, rebuild, rerun the AutoCAD harness, and prove the stray outside linework is gone.
- [x] Record the verified result and any new user-correction lesson.

Review 2026-03-31 (section 23-57-18-5 P3 outside-linework verification):
- Repro harness: added `data\sec23-57-18-5-p3-spec.json` and `data\sec23-57-18-5-p3-review.json`, then ran the Full AutoCAD harness against `C:\Users\Jesse 2025\Desktop\01467-24-PLA-R0.dwg`.
- Root cause 1: the bad far-away hydro was the `BF_Hydro_Polygon.shp` survivors, not the small `BF_SLNET_arc.shp` lines. Two huge converted closed hydro polylines were surviving because partial-overlap classification happened before polygon conversion, so the later closed-boundary clip/filter never saw the converted geometry’s true extents.
- Root cause 2: preserving existing `T-WATER-P3` means the build cannot clear the whole layer or review all layer content. The correct boundary is “new ATS-imported P3” versus pre-existing drawing content.
- Fix: recompute overlap kind after polygon conversion, clip/filter partial closed hydro boundaries the same way as other path entities, and tag ATS-imported P3 entities with invisible `ATSBUILD_P3` XData so reruns clear only prior ATS-imported P3 in the requested scope.
- Fix: the DXF review script now has a `path_window_guard` check and the section-23 review config scopes that check to ATS-tagged `T-WATER-P3` only, so preserved legacy P3 outside the build area does not count as a failure.
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed with the same two pre-existing unused `axisTol` warnings.
- Decision tests: `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- FullAutoCAD verification: `data\sec23-57-18-5-p3-run-rerun2\artifacts\review-report.json` passed (`checked=11`, `failures=0`). The final DXF contains `85` total `T-WATER-P3` polylines, but only `11` are ATS-tagged new imports and all `11` satisfy the 100m-buffer rule.
- P3 log proof: `BF_Hydro_Polygon.shp` now reports `kept=0, filtered=88599, converted=2`; `BF_SLNET_arc.shp` reports `kept=11, filtered=328935`; and only `10` prior tagged in-scope `T-WATER-P3` entities were cleared before reimport instead of wiping all existing P3 layer content.

## 2026-03-31 - ATS P3 section 23 north-half rerun gap
- [x] Create a repeatable south-half then north-half AutoCAD repro for section `23-57-18-5` starting from `C:\Users\Jesse 2025\Desktop\01467-24-PLA-R0.dwg`.
- [x] Confirm whether the second north-half run erases part of an existing ATS-imported south-half P3 path when it overlaps the cleanup scope.
- [x] Implement the minimal cleanup/import fix so adjacent half-section reruns preserve continuity without reviving far-away P3 survivors.
- [ ] Rebuild ATS, rerun the sequential AutoCAD verification, and prove the section-23 hydro stays continuous across the half boundary.
- [x] Record the current result and the new P3 cleanup lesson.

Review 2026-03-31 (section 23 north-half rerun gap):
- Repro setup: added `data\sec23-57-18-5-p3-southhalf-spec.json` and `data\sec23-57-18-5-p3-northhalf-spec.json`, reran the south-half FullAutoCAD harness, then generated a chained saved intermediate from the south-half `output.dxf` by converting it to `C:\AtsHarness\convert-20260331-23\converted.dwg`.
- Root cause: scoped cleanup of prior ATS-tagged `T-WATER-P3` erased any partial-overlap open path wholesale. On section `23-57-18-5`, the north-half import window overlaps the top of two south-half ATS river paths, so the second rerun could delete the whole south entity and leave a visible gap just below the north-half boundary.
- Fix: `ClearLayerEntities` in `src\AtsBackgroundBuilder\Core\Plugin.Core.ImportWindowing.cs` now preserves outside-of-scope pieces for partial ATS-tagged open paths instead of erasing the entire entity. The new helper path is `TryReplaceOpenPathEntityWithOutsideScopePieces`, backed by `ClipOpenPathOutsideScopeWindows`.
- Harness improvement: `scripts\atsbuild_harness.ps1` now preserves the source drawing extension in the launcher workspace, which is needed for chained verification using DXF intermediates.
- Local proof on real geometry: running the section-23 south-half tagged `T-WATER-P3` paths through the new outside-scope clipping logic shows the two overlapping south-half seam paths now survive as clipped outside pieces ending exactly on the north-half window boundary at `Y=5976956.299536862`, instead of being deleted wholesale.
- Example preserved seam pieces from the real south-half output:
- `528632.028980109,5976848.202725275 -> 528541.4002204019,5976956.299536862`
- `529134.9652333478,5976923.632821075 -> 529094.1622980316,5976956.299536862`
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed with the same two pre-existing `axisTol` warnings.
- Decision tests: `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- Full chained AutoCAD proof gap: the south-half rerun completed successfully, but repeated north-half reruns on the chained intermediate were interrupted by machine instability. Before the reboot, `managedmapapi.dll` threw an unhandled exception immediately after `sections_built`; after the reboot, the PC crashed again during the same chained verification attempt. Because of that host instability, I do not yet have a completed final north-after-south DXF to mark the end-to-end AutoCAD proof as finished.

Follow-up 2026-03-31 (user-directed P3 no-trim simplification):
- User correction: the desired behavior is no longer “trim P3 to the 100m buffer.” Instead, P3 objects should be kept whole whenever they touch the 100m buffer, and only objects that do not touch the buffer should be filtered out.
- Simplification: removed the partial-overlap clip/replace path for new P3 imports and removed the partial outside-scope preservation path for scoped ATS-tagged cleanup. The current behavior is now:
- partial new open-path P3 survives whole if it actually touches the requested buffer, otherwise it is filtered out;
- partial existing ATS-tagged P3 that overlaps the current rerun scope is erased whole so the touching object can be reimported whole by the current pass;
- no P3 object is clipped to the 100m buffer anymore.
- Files updated: `src\AtsBackgroundBuilder\Core\Plugin.Core.ImportWindowing.cs`
- Verification: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed with the same two pre-existing `axisTol` warnings, and `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- Remaining proof gap: I did not rerun the chained AutoCAD north-after-south case after this simplification because the machine had just crashed repeatedly during the previous verification attempts. The next stable AutoCAD run is still needed for end-to-end proof of the revised no-trim behavior.

Follow-up 2026-03-31 (user-corrected restore of P3 partial trimming):
- User correction: the no-trim simplification reintroduced the original failure mode where huge hydro features survive far outside the area of interest, because the import window is not a true geometry clip and touching whole-object keep is too permissive for long P3 paths.
- Corrected fix: restored the post-import partial path clip/replace behavior for new P3 imports and restored the scoped cleanup behavior that preserves outside-of-scope pieces for partial ATS-tagged reruns. This returns to the earlier seam-safe + outside-linework-safe behavior.
- Files updated: `src\AtsBackgroundBuilder\Core\Plugin.Core.ImportWindowing.cs`
- Verification: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed with the same two pre-existing `axisTol` warnings, and `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.

Follow-up 2026-03-31 (user-corrected no-trim endpoint-anchor rule):
- User correction: clipping is still the wrong contract. P3 should stay whole, but the new import should not keep a full object outside the work area unless it has a real endpoint inside the requested 100 m buffer.
- Root cause: the earlier no-trim whole-object rule was still too permissive. A long hydro path could survive whole if any part of it touched the import window, even when both endpoints were far outside the requested area.
- Fix: `src\AtsBackgroundBuilder\Core\Plugin.Core.ImportWindowing.cs` now uses `ShouldKeepWholePartialP3Entity(...)` for partial P3 imports and the same endpoint-anchor rule during scoped cleanup of ATS-tagged `T-WATER-P3`.
- Final rule:
- partial open P3 path: keep whole only if at least one endpoint is inside the requested 100 m window;
- partial closed P3 boundary: do not keep whole;
- fully-inside entities still stay on the cheap keep path;
- no clipping/rebuild is used for this behavior.
- Verification:
- `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed. Remaining warnings are only the two pre-existing unused `axisTol` locals.
- `dotnet test .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed.
- FullAutoCAD repro passed with `scripts\atsbuild_harness.ps1 -Runner FullAutoCAD -DwgPath C:\Users\Jesse 2025\Desktop\01467-24-PLA-R0.dwg -SpecPath data\sec23-57-18-5-p3-spec.json -ReviewConfigPath data\sec23-57-18-5-p3-review.json -OutputDir data\sec23-57-18-5-p3-run-rerun3`
- `data\sec23-57-18-5-p3-run-rerun3\artifacts\review-report.json` passed with `checked=6`, `failures=0`, `passed=true`.
- P3 log proof from `data\sec23-57-18-5-p3-run-rerun3\artifacts\AtsBackgroundBuilder.run.log`:
- `BF_Hydro_Polygon.shp`: `kept=0`, `filtered=88599`
- `BF_SLNET_arc.shp`: `kept=6`, `filtered=328936`
- The batch completed with `ATSBUILD_XLS_BATCH exit stage: completed (ok)`.

## 2026-03-31 - Width aligned dimensions keep measured span attached
- [x] Review the current aligned-dimension placement flow in `LabelPlacer.cs`, including candidate selection, creation, and finalize behavior.
- [x] Update width-required aligned dimensions so moved text can sit outside the arrow span while the arrowheads and dimension line stay on the measured cross-section.
- [x] Preserve `DimensionTextCandidate.DimLineOffset` through selection and creation so text placement and measured-span placement are controlled independently.
- [x] Add a focused regression test for a narrow `10.00 m` multiline width label with text outside the arrow span and the dimension line still on the measured segment.
- [x] Build/run the relevant tests and record the review outcome here.

Review 2026-03-31 (width aligned dimensions keep measured span attached):
- Root cause: the width-label aligned-dimension workflow was still treating moved text as the authority for the dimension geometry. `CreateAlignedDimensionLabel(...)` derived `dimLineOffset` from the text lane (`maxS + edgeGap` / `minS - edgeGap`), then projected `DimLinePoint` along the span under the text. The finalize pass reinforced that by forcing text movement mode `0` and calling `TryProjectAlignedDimensionLinePointUnderText(...)`, which could drag the measured geometry off the sampled cross-section whenever text moved outside the arrows.
- Fix: `src\AtsBackgroundBuilder\Dispositions\LabelPlacer.cs` now keeps width-label `DimLinePoint` on the measured span instead of solving collisions by moving the aligned-dimension geometry. Width candidates preserve a separate `DimLineOffset`, creation/fallback now pass that value through explicitly, width labels default that offset to `0.0`, and creation/finalize both use text-movement mode `1` so outside text gets a native jog/leader while the measured cross-section stays attached. Forced-overlap creation also no longer rejects the aligned dimension just because the text must overlap the measured linework.
- Test coverage: added `src\AtsBackgroundBuilder\Core\WidthAlignedDimensionPlacementPolicy.cs` plus the decision test `TestWidthAlignedDimensionPlacementKeepsMeasuredSpanAttachedWhenTextSitsOutside()` in `src\AtsBackgroundBuilder.DecisionTests\Program.cs`. The test models a narrow `10.00 m` span with long multiline text positioned outside the arrows and asserts that the text stays outside while the dimension line point remains on the measured segment midpoint with zero offset.
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed. Warnings were unchanged except for one existing nullable warning in `Plugin.Core.ImportWindowing.cs` that predates this change.
- Decision tests: `dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed (`Decision tests passed.`).
- Remaining proof gap: I did not run a live AutoCAD visual harness in this turn, so the automated proof here is the new geometry regression test plus successful compile/test coverage rather than a fresh rendered DXF screenshot.

Follow-up 2026-03-31 (user-corrected same-line aligned dimensions):
- User correction: the desired width-label behavior is not leader/jog style. Text may sit outside the arrows, but it must stay on the same aligned dimension line with `DIMTMOVE/TextMovement = 0`, while the arrowheads remain on the measured cross-section and no leader/jog/vertical extension is created.
- Corrected fix: `src\AtsBackgroundBuilder\Dispositions\LabelPlacer.cs` now searches width-label candidates along the span axis only (`normalOffsets = { 0.0 }`), strongly penalizes any between-arrow overflow instead of moving text off the dim line, projects the chosen `labelPoint` back onto the span axis before assigning `TextPosition`, keeps `dimLineOffset = 0.0` on the measured cross-section, restores movement mode `0`, skips the leader-style `TryProjectAlignedDimensionLinePointUnderText(...)` path, and removes `MLeader` fallback for width-required aligned dimensions.
- Corrected regression coverage: `src\AtsBackgroundBuilder\Core\WidthAlignedDimensionPlacementPolicy.cs` now models same-line side placement, and `TestWidthAlignedDimensionPlacementKeepsMeasuredSpanAttachedWhenTextSitsOutside()` in `src\AtsBackgroundBuilder.DecisionTests\Program.cs` now verifies that a narrow `10.00 m` multiline width label chooses an outside-along-span position, projects the text back onto the dim line, keeps the dimension line point on the measured cross-section midpoint, and produces no normal-offset text geometry.
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed after a one-line unrelated compile unblock in `src\AtsBackgroundBuilder\Core\Plugin.Core.ImportWindowing.cs` (`scopeExtents ?? Array.Empty<Extents2d>()` -> `scopeExtents`), with remaining warnings limited to the pre-existing nullable warning in that file and the two pre-existing `axisTol` warnings.
- Decision tests: `dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed (`Decision tests passed.`).
- Remaining proof gap: I still did not run a fresh live AutoCAD visual harness in this turn, so the automated proof is the corrected same-line regression test plus successful compile/test coverage rather than a rendered DXF screenshot.

## 2026-03-31 - Width aligned dimensions keep outside placement matched to measured span
- [x] Inspect the remaining same-line width-label edge case in the outside candidate ranking and finalize pass.
- [x] Refine placement only so outside same-line text prefers the measured-span-matched continuation distance without changing the label type or movement mode.
- [x] Extend the focused regression test to lock the outside continuation to the measured span width.
- [x] Rebuild and rerun the decision tests, then record the outcome here.

Review 2026-03-31 (width aligned dimensions keep outside placement matched to measured span):
- User correction: the latest same-line fix looked much better, but some narrow width labels still looked visually detached because the continuation from the arrowheads to the outside text did not consistently track the measured segment width.
- Root cause: the outside candidate generator still seeded “just clear the arrows” positions before the width-matched continuation distance, and the finalize pass `TryTightenOverlongAlignedDimensionJog(...)` could pull outside same-line text back toward the arrows after creation. That late tightening was changing placement even though the aligned-dimension object type and movement mode were already correct.
- Fix: `src\AtsBackgroundBuilder\Core\WidthAlignedDimensionPlacementPolicy.cs` now exposes the preferred outside-along offset where the arrowhead-to-text continuation matches the measured span width and seeds that position first during same-line candidate generation. `src\AtsBackgroundBuilder\Dispositions\LabelPlacer.cs` now scores outside candidates much more strongly around that preferred width-matched position, and the finalize pass now normalizes width aligned dimensions back onto the same dimension line instead of tightening them inward.
- Regression coverage: `TestWidthAlignedDimensionPlacementKeepsMeasuredSpanAttachedWhenTextSitsOutside()` in `src\AtsBackgroundBuilder.DecisionTests\Program.cs` now also asserts that the primary outside candidate lands at the preferred width-matched offset and that the gap from the nearest arrowhead to the text equals the `10.00 m` measured span.
- Build: `dotnet build .\src\AtsBackgroundBuilder\AtsBackgroundBuilder.sln -c Release --no-restore /m:1 -v:minimal` passed with the same pre-existing nullable warning in `Plugin.Core.ImportWindowing.cs` and the same two pre-existing `axisTol` warnings.
- Decision tests: `dotnet run --project .\src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-build` passed (`Decision tests passed.`).
- Remaining proof gap: I still did not run a fresh live AutoCAD visual harness in this turn, so the proof here is build + regression coverage rather than a new rendered drawing screenshot.
