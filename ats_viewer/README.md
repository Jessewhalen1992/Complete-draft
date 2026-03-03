# Alberta ATS UTM83 Sections Viewer

Run from repo root:

```bash
python -m ats_viewer --sections "11-1-1-W5,14-1-1-W5" --out out/
python -m ats_viewer --township "TWP 1 RGE 1 W5" --out out/
python -m ats_viewer --zone 11 --sections "11-1-1-W5" --out out/
```

Debug mode (for missing connections):

```bash
python -m ats_viewer --sections "11-1-1-W5,14-1-1-W5" --debug --out out-debug/
python -m ats_viewer --township "TWP 1 RGE 1 W5" --zone 11 --debug --out out-debug/merged --road-width-targets "20.11,30.17" --gap-tolerance 1.0
```

ATS-wide invariant validation (pre-AutoCAD):

```bash
python -m ats_viewer.validator --township "TWP 57 RGE 18 W5" --zone 11 --out out-validate/twp-57-18-w5
python -m ats_viewer.validator --all-townships --zone 11 --out out-validate/z11
python -m ats_viewer.validator --all-townships --zone auto --out out-validate/all-zones
```

Validator outputs:
- `validation_summary.json`
- `validation_summary.md`
- `validation_failures.csv`

Exit code:
- `0`: all checked townships passed configured invariants.
- `1`: one or more townships failed.

Note:
- `--road-width-targets` must be provided as one quoted value: `--road-width-targets "20.11,30.17"`.

Viewer guidance for 20.11:
- If all 20.11 segments are shown as low-confidence/inferred, that means they have `best_gap` too close to 0 to be a confirmed paired allowance.
- The UI shows **Section edges 20.11 confirmed** and **Section edges 20.11 inferred** separately so CAD-like confirmed lines are easier to isolate.

Outputs (all EPSG:4326 GeoJSON):
- `sections.geojson` section polygons
- `centrelines.geojson` generated centerlines
- `labels.geojson` section labels
- `section_edges.geojson` polygon edge lines for selected sections
- `unmatched_edges.geojson` sections edges that were not paired
- `edge_pairs_debug.geojson` candidate edge pairs with score/metrics
- `debug_summary.txt` match stats
- `preview.png` optional static render (disabled with `--no-preview`)

Interactive map (Streamlit):

```bash
python -m pip install streamlit pydeck
streamlit run ats_viewer/streamlit_app.py -- --data-dir out
```

The `out` directory is where the GeoJSON files above were written by the CLI.
Use `--data-dir` to point at another folder (for example `out_debug/step2b`).

Viewer workflow for missing-connection diagnosis:
- Open the app and set **Output folder** to your out/debug folder.
- Use **Quick section** to jump directly to a section key (e.g. `28-57-18-W5`).
- Or use **Section filter** with comma-separated keys, then keep **Enable section filter** checked.
- When enabled, the map view zooms to matched sections and hides all other features.
- Use **Section edges (20.11)** and **Section edges (30.17)** toggles to separate road-boundary widths for diagnostics.
- If a width is missing, check the sidebar **Section edges (all/matched/unmatched)** counts and the status in `debug_summary.txt`.
- Centrelines are internal centerline outputs and are separate from the section boundary widths.
- Use **Layer mode -> Boundary-only (no centrelines)** to mimic CAD-style edge/candidate debugging.

AutoCAD parity workflow (exact plugin linework):
- In PowerShell before launching AutoCAD:
  - `$env:ATSBUILD_EXPORT_GEOJSON = "1"`
  - Optional explicit output path:
  - `$env:ATSBUILD_EXPORT_GEOJSON_PATH = "C:\Users\Work Test 2\Desktop\COMPLETE DRAFT 2.0\out_debug_twp_3017\cad_lines.geojson"`
- Run `ATSBUILD` in AutoCAD as normal.
- The plugin exports final CAD layers to `cad_lines.geojson` (UTM coordinates with zone metadata).
- Start viewer on that folder:
  - `streamlit run ats_viewer/streamlit_app.py -- --data-dir ".\out_debug_twp_3017"`
- In sidebar choose **View mode = AutoCAD parity** and toggle:
  - `CAD L-SEC`
  - `CAD L-USEC`
  - `CAD L-QSEC`
  - `CAD L-SECTION-LSD`
  - optional `CAD L-QSEC-BOX`
  - optional quarter highlights:
  - `Highlight N.W. 1/4`
  - `Highlight N.E. 1/4`
  - `Highlight S.W. 1/4`
  - `Highlight S.E. 1/4`
  - Quarter highlights apply ATS ownership convention (west allowance belongs to west half; south allowance belongs to south half).

Diagnostic layers now include:
- `section_edges (unmatched)` and `section_edges (20.11/30.17)` to make boundary behavior visible like CAD debug output.

Current data exports do not include raw 1/4-LSD lines. If you need those exactly as drawn in AutoCAD, export them from the plugin/engine side and load as an additional layer.

Defaults and parsing:
- Zone is auto-selected across Z11/Z12 unless `--zone` is set.
- Ambiguous single-section matches across zones are rejected and ask for explicit zone.
- Section forms accepted:
  - `SEC-TWP-RGE-W5`
  - `SEC TWP RGE W5`
  - comma list `11-1-1-W5,14-1-1-W5`
  - township `TWP 1 RGE 1 W5`
- Road allowance target/gap controls:
  - `--road-width-target` (default `20.11`)
  - `--road-width-targets` (comma-separated list, e.g. `20.11,30.17`)
  - `--gap-tolerance` (default `0.5`)
  - `--angle-tolerance-deg` (default `12`)
  - `--min-overlap-ratio` (default `0.20`)
- `--max-debug-pairs` limits `edge_pairs_debug.geojson` (default `2000`)

Notes:
- Index discovery order:
  1. `data/`
  2. `src/AtsBackgroundBuilder/REFERENCE ONLY/`
- Requires `shapely` and `pyproj`.

Install:

```bash
python -m pip install -r requirements.txt
```

