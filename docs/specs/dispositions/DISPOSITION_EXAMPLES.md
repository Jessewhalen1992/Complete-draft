# Disposition Examples (Implementation Snapshot)

Status date: 2026-02-19

These examples reflect current implemented behavior and are intended as test-ready reference cases.

## Example 1: Standard Non-Width Label

Inputs
```text
CurrentClient: ACME ENERGY
OD: DISP_NUM=LOC12345, COMPANY=ACME ENERGY, PURPCD=TEMP SITE, DIMENSION=
Lookups: COMPANY->ACME ENERGY, PURPOSE->Temporary Site, PURPOSE EXTRA=ROW
Flags: IncludeDispositionLabels=true, UseAlignedDimensions=false
```

Expected
```text
Line layer: C-ROW
Text layer: C-ROW-T
Label entity: MText
Label text:
ACME ENERGY\PTemporary Site\PLOC 12345
Text color index: 256 (ByLayer)
```

## Example 2: Foreign Company Layer Mapping

Inputs
```text
CurrentClient: ACME ENERGY
OD: DISP_NUM=PLA9001, COMPANY=OTHERCO, PURPCD=PIPELINE, DIMENSION=
Lookups: COMPANY->OTHERCO, PURPOSE->Pipeline, PURPOSE EXTRA=PIPE
```

Expected
```text
Line layer: F-PIPE
Text layer: F-PIPE-T
```

## Example 3: Width-Required Fixed Width Label

Inputs
```text
OD: DISP_NUM=DPL100, COMPANY=ACME ENERGY, PURPCD=PIPELINE, DIMENSION=
Measured median width: 15.22
Nearest acceptable width: 15.24
WidthSnapTolerance: 0.25
Flags: UseAlignedDimensions=false
```

Expected
```text
RequiresWidth: true
Label entity: MLeader
Label text:
ACME ENERGY\P15.24 Pipeline\PDPL 100
Text color index: 256 (within snap tolerance and no OD fallback)
```

## Example 4: Variable Width Label

Inputs
```text
OD: DISP_NUM=RME77, COMPANY=ACME ENERGY, PURPCD=ACCESS ROAD, DIMENSION=
Measured corridor: variable beyond absolute/relative tolerance
No successful OD dimension fallback
```

Expected
```text
Label entity: MLeader (or AlignedDimension if UseAlignedDimensions=true)
Label text:
ACME ENERGY\PVariable Width\PAccess Road\PRME 77
Text color index: 3 (green)
```

## Example 5: Variable Width Resolved By OD DIMENSION

Inputs
```text
OD: DISP_NUM=DPI450, COMPANY=ACME ENERGY, PURPCD=POWERLINE, DIMENSION=15 M X 2.4
Measured corridor: variable
OD dimension parsed width: 15.00
Nearest acceptable width: 15.00
```

Expected
```text
Label text uses fixed-width format:
ACME ENERGY\P15.00 Powerline\PDPI 450
Text color index: 3 (green, because OD fallback was used)
```

## Example 6: Wellsite Surface Prefixing

Inputs
```text
OD purpose normalized to WELL SITE (or mapped purpose contains "(Surface)")
Dominant section token successfully derived from section/LSD overlap logic
```

Expected
```text
Mapped purpose is prefixed with token before label creation, for example:
NW-16-58-20-W5M. Well Site (Surface)
```

## Example 7: Multi-Quarter Behavior

Inputs
```text
One disposition intersects NW and NE quarters of a requested section
```

Expected
```text
Labels are attempted in each intersected quarter (multi-quarter mode is currently forced on).
```

## Example 8: Labels Kept When Disposition Linework Is Removed

Inputs
```text
IncludeDispositionLabels=true
IncludeDispositionLinework=false
```

Expected
```text
Disposition label entities remain.
Imported disposition polylines are erased in cleanup.
```

## Example 9: PLSR Expired Marker

Inputs
```text
CheckPlsr=true
PLSR activity expiry date < report date for matched label
```

Expected
```text
"(Expired)" is appended to label contents.
PLSR summary includes incremented "Expired tags added".
```
