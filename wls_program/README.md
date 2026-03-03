# WILDLIFE SWEEPS

AutoCAD 2015/2025-ready .NET plug-in that reproduces legacy LISP workflows for photo placement and point numbering.

## Contents
- **PHOTOJPG4**: Place JPGs in groups of four based on a Photo_Location block, nearest `PIC ##` text, and `_Page_XX` filenames.
- **PHOTODDMMSS**: Place JPGs in groups of four by matching photo GPS (DDMMSS) to Photo_Location block coordinates.
- **NUMPTS**: Number points/texts and export a CSV of coordinates (including DDMMSS).
- **WLS_PALETTE**: Palette UI for the above commands.

## Solution Layout
```
src/WildlifeSweeps/WildlifeSweeps.sln
src/WildlifeSweeps/WildlifeSweeps.csproj
```

## Repo Integration Context

This module is integrated into `COMPLETE DRAFT` as a standalone project under `wls_program/`.
It can be built and loaded independently of `AtsBackgroundBuilder`.

## Reusing COMPLETE DRAFT Code

For selective function reuse in WLS, use one of these patterns:

1. Preferred: move shared logic into a dedicated shared library project and add a `ProjectReference`.
2. Fast path: link specific source files directly into `WildlifeSweeps.csproj`.

Example linked-file include (relative to `wls_program/src/WildlifeSweeps`):

```xml
<ItemGroup>
  <Compile Include="..\..\..\src\AtsBackgroundBuilder\Core\YourSharedFile.cs" Link="Shared\YourSharedFile.cs" />
</ItemGroup>
```

## Build (Visual Studio)
1. Install AutoCAD 2015/2025 and locate the install directory that contains `AcCoreMgd.dll`, `AcDbMgd.dll`, and `AcMgd.dll`.
2. Open `src/WildlifeSweeps/WildlifeSweeps.sln` in Visual Studio.
3. Set the MSBuild property `ACAD_DIR` to the folder containing the AutoCAD managed DLLs.
   - Example: `C:\Program Files\Autodesk\AutoCAD 2025`
4. Build the solution.

## Load in AutoCAD
Use `NETLOAD` and select `WildlifeSweeps.dll` from the build output folder.

## Commands
- `WLS_PALETTE` - Open the palette UI.
- `PHOTOJPG4` - Run the image placement workflow from the command line.
- `PHOTODDMMSS` - Match photos by GPS DDMMSS and place them in 4-up layouts.
- `NUMPTS` - Run the numbering workflow from the command line.

## Notes
- PHOTOJPG4 searches the current space for `TEXT`/`MTEXT` containing `PIC <digits>` and pairs that with the nearest Photo_Location block attribute `#`.
- Image filenames must include `_Page_XX` for lookup.
- NUMPTS can use Map 3D/Civil 3D for coordinate conversion, with a built-in NAD83 UTM fallback for Lat/Long output.

See [docs/WILDLIFE_SWEEPS.md](docs/WILDLIFE_SWEEPS.md) for workflow details.
