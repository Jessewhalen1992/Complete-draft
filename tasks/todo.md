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
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
  - decision tests passed:
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release`

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
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
  - decision tests passed:
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release`

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
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
  - decision tests passed:
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release`

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
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
  - decision tests passed:
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release`

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
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
  - decision tests passed:
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release`

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
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
  - decision tests passed:
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release`

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
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
  - decision tests passed:
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release`

# Follow-up (/debug-config lingering command prompt screenshot, 2026-03-02)

- [x] Confirm screenshot state against current source behavior.
- [x] Rebuild Release plugin and re-run decision tests.
- [x] Sync latest DLL/PDB to AutoCAD runtime path and verify source/runtime parity.

## Review (/debug-config lingering command prompt screenshot, 2026-03-02)

- Finding:
  - screenshot (`src/AtsBackgroundBuilder/REFERENCE ONLY/Screenshot 2026-03-01 194742.png`) shows the older modeless PLSR review variant (`Drawing changed during review. Apply is disabled; rerun PLSR Check.`).
  - current source in `src/AtsBackgroundBuilder/Dispositions/Plugin.Dispositions.LabelingPlsr.cs` is modal `ShowDialog()` flow and does not contain that modeless guard text.
- Build/test verification:
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` succeeded (`Decision tests passed.`).
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
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
  - decision tests passed:
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
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
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
  - decision tests passed:
    - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore`
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
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` succeeded (`Decision tests passed.`).
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
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` succeeded (`Decision tests passed.`).
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
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` succeeded (`Decision tests passed.`).
  - standard release output build was blocked by file lock on `bin\Release\net8.0-windows\AtsBackgroundBuilder.dll`.
  - compiled equivalent artifact via alternate output path:
    - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /p:OutputPath=bin\Release\net8.0-windows-plsr-no-final-popup\`
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
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` succeeded (`Decision tests passed.`).
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
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` succeeded (`Decision tests passed.`).
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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (warnings only).
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` succeeded (`Decision tests passed.`).
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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (warnings only).
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` succeeded (`Decision tests passed.`).
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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (warnings only).
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` succeeded (`Decision tests passed.`).

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
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` succeeded (`Decision tests passed.`).

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
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` succeeded (`Decision tests passed.`).

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
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (warnings only).
  - `.\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` succeeded (`Decision tests passed.`).

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
    - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /p:OutputPath=bin\Release\net8.0-windows-plsr-cleanup-fix\`

# Follow-up (1/4 Definitions UI Default Off, 2026-03-01)

- [x] Remove config-driven default for `1/4 Definitions` in shared ATSBUILD option catalog.
- [x] Rebuild release and sync runtime artifacts.

## Review (1/4 Definitions UI Default Off, 2026-03-01)

- Updated `Core/AtsBuildOptionCatalog.cs`:
  - removed `config => config.AllowMultiQuarterDispositions` default resolver from `AllowMultiQuarterDispositions`
  - `1/4 Definitions` now defaults unchecked in UI unless explicitly set by seeded state during recovery flows
- Build and artifact sync:
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (warnings only, no errors)
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
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (warnings only, no errors)
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
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded (warnings only, no errors)
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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
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
  - `.\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore /p:OutputPath=bin\Release\net8.0-windows-surfaceimpact\` (success)
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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home\.nuget\packages'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore`
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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` passed (`Decision tests passed.`).

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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` passed (`Decision tests passed.`).

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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` passed (`Decision tests passed.`).

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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` passed (`Decision tests passed.`).

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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` passed (`Decision tests passed.`).

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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.nuget_packages'; $env:HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.nuget_packages'; $env:HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` passed (`Decision tests passed.`).

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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.nuget_packages'; $env:HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.cli_home'; $env:NUGET_PACKAGES='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.nuget_packages'; $env:HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` passed (`Decision tests passed.`).

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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` passed (`Decision tests passed.`).

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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` passed (`Decision tests passed.`).

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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` passed (`Decision tests passed.`).

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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` passed (`Decision tests passed.`).

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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` passed (`Decision tests passed.`).

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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` passed (`Decision tests passed.`).

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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` passed (`Decision tests passed.`).

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
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; .\.local_dotnet\dotnet.exe build src\AtsBackgroundBuilder\AtsBackgroundBuilder.csproj -c Release --no-restore` succeeded.
  - `$env:DOTNET_CLI_HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; $env:HOME='C:\Users\jesse\OneDrive\Desktop\COMPLETE DRAFT\.dotnet-home'; .\.local_dotnet\dotnet.exe run --project src\AtsBackgroundBuilder.DecisionTests\AtsBackgroundBuilder.DecisionTests.csproj -c Release --no-restore` passed (`Decision tests passed.`).
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
  - Added stricter non-correction NE handling so `N.E. N.E.` uses strict east×north segment intersection (`VERIFY-QTR-NE-NE-STRICT`) instead of apparent infinite-line intersection.
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
  - moved the callsite�s inline east mid-U calculation into the helper while reusing existing `TryResolvePreferredQuarterViewEastBoundarySegment(...)` behavior.
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
