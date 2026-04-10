# AutoCAD 2025 References

Place the Autodesk AutoCAD 2025 managed assemblies required for compilation in this directory if you want a local fallback instead of using the default installation path.

Recommended files:

- `AcDbMgd.dll`
- `AcMgd.dll`
- `AdWindows.dll`
- `AcCoreMgd.dll`

Ensure the file versions match the target AutoCAD installation and set their `Copy Local` property to `false` in Visual Studio so they are resolved from the host AutoCAD installation at runtime.
