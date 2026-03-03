# ATS Background Builder

AutoCAD Map 3D 2025 .NET plugin for generating Alberta ATS-style background linework and disposition labels.

## Build output

The solution targets **net8.0-windows** for AutoCAD Map 3D 2025. Build outputs (plus dependencies) are copied to:

```
/build/net8.0-windows/
```

## Install

1. Build the solution in Visual Studio.
2. Copy the `AtsBackgroundBuilder.dll` output (and your Excel lookup files) into a folder of your choice.
3. In AutoCAD Map 3D (or Civil 3D with Map 3D), run `NETLOAD` and load the DLL.

## Excel lookup files

Place the following Excel files beside the DLL to enable mapping:

- `CompanyLookup.xlsx` (columns: Code, Value, Extra)
- `PurposeLookup.xlsx` (columns: Code, Value, Extra)

If the Excel files are missing or cannot be read, the plugin falls back to raw OD values.

## Usage

Run the command:

```
ATSBUILD
```

## Pre-AutoCAD Ops Gate

Run the calibrated section-validation gate in one step:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run-ops-gate.ps1
```

Defaults in the script:
- runs zone 11 + zone 12 validator sweeps
- applies ops scope filter: `TWP >= 50` and `sections >= 30`
- fails gate when any township has:
  - `unmatched_ratio > 0.30`, or
  - `accepted_pairs == 0`, or
  - `sections_with_gt2_unmatched > 22`

Artifacts:
- `out-validate\z11-gate\validation_summary.json`
- `out-validate\z12-gate\validation_summary.json`
- `out-validate\ops-gate-failures.csv`

## CAD line export for Python diagnosis

To visualize the exact final AutoCAD linework in the Streamlit viewer (layer toggles by CAD layer):

```powershell
$env:ATSBUILD_EXPORT_GEOJSON = "1"
$env:ATSBUILD_EXPORT_GEOJSON_PATH = "C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\out_debug_twp_3017\cad_lines.geojson"
```

Then run `ATSBUILD` in AutoCAD. The plugin writes `cad_lines.geojson` containing final model-space line segments on:

- `L-SEC`
- `L-USEC`
- `L-QSEC`
- `L-SECTION-LSD`
- `L-QSEC-BOX`

Open with:

```powershell
streamlit run ats_viewer/streamlit_app.py -- --data-dir ".\out_debug_twp_3017"
```

In the sidebar choose **View mode = AutoCAD parity**.

You will be prompted to:

1. Select quarter-section polylines. Press Enter to select section polylines and auto-generate quarters.
2. Select disposition polylines. Press Enter to select all closed polylines on a specified layer.
3. Choose the current client (from lookup list if available).
4. Provide text height and max overlap attempts.

The command will:

- Read OD fields `DISP_NUM`, `COMPANY`, `PURPCD`.
- Map company/purpose values (if lookup files are present).
- Move linework to computed layers and place 3-line MText labels with `\P` newlines.
- Avoid overlaps within each quarter and log the run to `AtsBackgroundBuilder.log`.

## Notes / future enhancements

- **Shapefile import:** Hook in MAPIMPORT or FDO automation in `Plugin.AtsBuild`.
- **Polygon clipping:** Upgrade `GeometryUtils.TryIntersectRegions` for true multi-piece intersections.
- **Label packing:** Extend `LabelPlacer` to honor priorities and advanced packing.
- **Callouts:** Add leaders or callout tables for labels that cannot fit.
