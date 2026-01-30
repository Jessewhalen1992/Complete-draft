# ATS Background Builder

AutoCAD Map 3D .NET plugin for generating Alberta ATS-style background linework and disposition labels.

## Build output

The solution targets **net45** and **net8.0-windows**. Build outputs (plus dependencies) are copied to:

```
/build/net45/
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
