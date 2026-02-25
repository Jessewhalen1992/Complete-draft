# Lessons

- Correction-line context cannot rely on plain ATS grid adjacency at township seams; force include shifted cross-range neighbors for seam-edge sections (for example `6 above -> 35 below+range`, and mirror the rule below the seam).
- When users validate one side of a correction seam as fixed, explicitly test/cover the opposite seam direction too; missing symmetric pairs (for example `32 below -> 5 above`) can still drop required RA context.
- Forced correction-context sections can be valid for RA generation but noisy for correction-line/LSD seam inference; keep those scopes decoupled to avoid unintended LSD endpoint behavior.
- For midpoint snapping, prioritize exact regular boundary midpoints first when an LSD endpoint is already on `L-SEC`/`L-USEC`; quarter/component midpoint logic should be fallback, not primary.
- When repeated endpoint heuristics regress nearby correction logic, pivot to an explicit rule-matrix pass keyed by section/quarter instead of layering more fallbacks into the legacy path.
- Treat generic `L-USEC` in user LSD rules as blind-line layer (`L-USEC`) only, not `L-USEC-3018`; if the user says endpoints must never end on 3018, encode that as a hard matrix constraint.
- For Rule 1 (1/4 inner endpoints), derive targets from final adjusted `L-QSEC` components in-model; section-anchor-derived centers/endpoints can be stale after endpoint cleanup and produce exact half-gap misses (for example 5.03 m on west halves).
- When deriving post-adjustment `L-QSEC` component targets, scope candidate components to the owning section envelope; global clip-window clustering can bleed into adjacent sections and pull a 1/4 endpoint onto the neighbor section line.
- In deferred LSD redraw, never erase by "segment intersects requested window" alone; boundary-touching LSDs from adjoining previously-built sections can be deleted. Use quarter ownership (midpoint-in-requested-quarter) for erase scope.
- For horizontal LSD outer endpoints, honor surveyed `L-SEC` as first-choice target; if `L-USEC` is prioritized first, lines can cross the section line into the adjoining side.
- Even with `SEC` priority, midpoint target search must be section-scoped; otherwise global candidate pools can select neighboring-section `L-SEC` midpoints and drive E-W LSDs across the intended boundary.
- Correction seam classification must not rely on north-side evidence only; when north RA is out-of-buffer, use surveyed south-edge horizontal evidence before classifying seam as unsurveyed (`L-USEC-C`).
- For correction-line bugs in 100m-buffer/range-over builds, add explicit seam-input logging (north/south sample contributions, one-sided synthesized seam mode, and xOnly vs seam-band evidence counts) before changing thresholds.
- One-sided synthesized correction seams (missing north/south section samples in 100m-buffer runs) need relaxed surveyed-edge tolerance and x-only fallback checks; strict seam-band edge tolerances can miss valid surveyed south-only evidence and incorrectly classify seams as `L-USEC-C`.
