# Compass Compiled Programs

Compass is a consolidated AutoCAD palette set that surfaces multiple Compass tooling experiences behind a single `Compass` command. The initial implementation introduces the Drill Manager (Drill-Namer-2025) and the 3D Profile Manager (Profile-Xing-Gen) as dockable palettes that behave like standard AutoCAD property windows.

This module is integrated into `COMPLETE DRAFT` as a standalone sibling project under `compass_program/`.

## Solution Layout

```
Compass.sln            # Visual Studio solution targeting AutoCAD 2025+ on .NET 8
src/Compass/           # AutoCAD plug-in entry point and WPF UI
lib/AutoCAD2025/       # Optional local AutoCAD 2025 reference drop-in location
```

The `Compass` project is a class library that references the AutoCAD 2025 managed assemblies. The build output should be loaded into AutoCAD 2025 or newer via `NETLOAD`.

For the old-style single-file deployment workflow, use:

`build/compass/single-dll/Compass.dll`

The build now embeds the plug-in-side managed dependencies back into `Compass.dll` (for example `NLog`, `Newtonsoft.Json`, `EPPlus`, and the supporting Microsoft.Extensions assemblies). AutoCAD host assemblies are still provided by the AutoCAD installation and are not embedded.

## AutoCAD Dependencies

By default the project tries to locate the AutoCAD managed assemblies in the following locations, in order:

1. The value of the `ACAD_DIR` MSBuild property, if set.
2. The value of the `AUTOCAD_ROOT` environment variable, if set.
3. `%ProgramFiles%\Autodesk\AutoCAD 2025\`

If you need a local fallback, copy the dependencies into `lib/AutoCAD2025` and update `AutoCADReferencePath` in the project file accordingly.

Recommended files:

- `AcDbMgd.dll`
- `AcMgd.dll`
- `AcCoreMgd.dll`
- `AdWindows.dll`

Set their **Copy Local** property to `False` to ensure the plug-in binds to the host AutoCAD installation.

## NuGet Dependencies

`Compass.csproj` relies on a handful of third-party libraries, including EPPlus, Newtonsoft.Json, and NLog. NuGet restore is enabled through `.nuget/NuGet.Config`, which keeps the standard `nuget.org` feed enabled.

Restore from the repository root with:

```powershell
dotnet restore .\compass_program\Compass.sln
```

Build from the repository root with:

```powershell
dotnet build .\compass_program\Compass.sln -c Release -p:Platform=x64
```

Output notes:

- Standard build output: `compass_program/src/Compass/bin/x64/Release/net8.0-windows/`
- Clean deployment copy: `build/compass/net8.0-windows/`
- Single-DLL deployment copy: `build/compass/single-dll/Compass.dll`

## Commands

Once the assembly has been `NETLOAD`ed into AutoCAD, run the `Compass` command to display the main launcher palette. Each button opens its respective module in a dockable palette:

- **Drill Manager** - Hosts the Drill-Namer-2025 workflow with support for up to 20 drills.
- **3D Profile Manager** - Placeholder for the Profile-Xing-Gen workflow integration.

## Integration Notes

- The first integration pass keeps the existing Drill Manager `Complete CORDS` workflow behavior intact.
- Legacy AutoCAD 2015 / `net45` deployment plumbing has been removed from this in-repo copy so the module can standardize on AutoCAD 2025+.

## Extending Modules

Modules implement the `ICompassModule` interface found in `src/Compass/Modules/ICompassModule.cs`. Additional programs can be registered from `CompassApplication.EnsureModules` to expose future tooling through the launcher UI.
