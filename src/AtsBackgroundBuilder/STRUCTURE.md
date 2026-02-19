# Source Structure

## Domain folders

1. `Core`: command entry, UI/input, config, lookup/load, and core plugin orchestration helpers.
2. `Geometry`: geometric utility logic and geometry-focused plugin partials.
3. `Sections`: section index/rules and section-focused plugin partials.
4. `RoadAllowance`: road allowance and endpoint enforcement plugin partials.
5. `Dispositions`: disposition import/OD/labeling logic and related plugin partials.
6. `Diagnostics`: cleanup/export diagnostics plugin partials.

## Namespace conventions

1. Non-`Plugin` classes use domain namespaces:
   `AtsBackgroundBuilder.Core`,
   `AtsBackgroundBuilder.Geometry`,
   `AtsBackgroundBuilder.Sections`,
   `AtsBackgroundBuilder.Dispositions`.
2. `Plugin` partial files remain in `AtsBackgroundBuilder` to preserve the single partial class surface.
3. Domain namespaces are made available project-wide via `Core/GlobalUsings.Domain.cs`.

## Plugin partial naming

1. Use `Plugin.<Domain>.<Feature>.cs` for new partial files.
2. Keep one feature family per file (for example, `Plugin.RoadAllowance.Cleanup.cs`).
3. Avoid creating new monolithic partial files; prefer focused files grouped by domain.
