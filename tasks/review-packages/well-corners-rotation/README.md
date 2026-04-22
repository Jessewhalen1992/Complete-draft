# WELL CORNERS Review Package

## Current State

The latest WELL CORNERS build is back on a true table-cell implementation for the `ID` bubbles and is no longer using the overlay block workaround.

The current source:

- builds the table through the managed API
- commits the table
- reopens the database-resident table
- populates the `ID` cells through one ActiveX table API path modeled on the user-provided LISP

## Latest Local Change

The newest refinement after the cell-based fix was table fill cleanup:

- only the shaded header cells keep explicit background fill `254`
- title and data cells now use the cell no-fill state instead of explicit black background color

## Deployment Note

- Active AutoCAD startup script: `C:\AUTOCAD-SETUP CG\CG_LISP\load jgw programs.lsp`
- Active DLL target: `C:\AUTOCAD-SETUP CG\CG_LISP\Compass_20260421_231718.dll`
- That DLL hash matches the current local Release build exactly

## Included Files

- `WellCornerTableService.cs`
  - Current WELL CORNERS implementation, including the ActiveX cell-population path.
- `CompassStartupDiagnostics.cs`
  - Startup/runtime logging helper.
- `DrillManagerViewModel.cs`
  - Command entry point that calls `CreateWellCornersTable()`.
- `DrillManagerControl.xaml`
  - Drill Manager UI binding for the command.
- `load jgw programs.lsp`
  - Active AutoCAD startup script now pointing at `Compass_20260421_231718.dll`.
- `Compass-netload-latest.txt`
  - Latest copied startup/runtime log available in the package.
- `DEPLOYMENT.md`
  - Exact load-path and hash details for the current deployed build.

## Review Focus

If this package needs external review, the most relevant code paths are:

- `CreateWellCornersTable(...)`
- `BuildTable(...)`
- `PopulateBubbleCellsViaActiveX(...)`
- `GetDisplayedAngleAsWorldRotation(...)`
