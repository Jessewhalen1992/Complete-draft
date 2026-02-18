# Lessons

- Correction-line context cannot rely on plain ATS grid adjacency at township seams; force include shifted cross-range neighbors for seam-edge sections (for example `6 above -> 35 below+range`, and mirror the rule below the seam).
- When users validate one side of a correction seam as fixed, explicitly test/cover the opposite seam direction too; missing symmetric pairs (for example `32 below -> 5 above`) can still drop required RA context.
- Forced correction-context sections can be valid for RA generation but noisy for correction-line/LSD seam inference; keep those scopes decoupled to avoid unintended LSD endpoint behavior.
