# Deployment State

- Active startup script:
  - `C:\AUTOCAD-SETUP CG\CG_LISP\load jgw programs.lsp`
- Current `NETLOAD` target:
  - `C:\AUTOCAD-SETUP CG\CG_LISP\Compass_20260421_231718.dll`
- Matching local build:
  - `C:\Users\Jesse 2025\Desktop\COMPLETE DRAFT\compass_program\src\Compass\bin\x64\Release\net8.0-windows\Compass.dll`
- Shared SHA256:
  - `8510C598E44FBE2E67E3AD2470DF122AD8BBD6CCAD799D808E48683F19FBF7AE`

This package now reflects the working WELL CORNERS cell-based implementation:

- true table-cell `ID` bubbles again, not overlay inserts
- ActiveX table-cell population modeled on the user-provided LISP
- `RegenerateTableSuppressed` while configuring block cells
- cell block setup through `SetCellType`, `SetBlockTableRecordId`, `SetAutoScale`, `SetBlockScale`, `SetCellAlignment`, `SetBlockRotation`, and `SetBlockAttributeValue`
- only the intended header cells use explicit background fill `254`
- title and data cells now use no-fill instead of explicit black background color
