# Section Building Rules (Authoritative v2)

Status date: 2026-02-19

These rules are authoritative for ATS background generation. If a code path contradicts them, stop and revisit the implementation.

Primary companion reference: `docs/specs/road-allowance/ROAD_ALLOWANCE_GOLDEN_RULES.md`.

## Shared Definitions

1. Hard section boundaries: `L-SEC`, `L-USEC-0`, `L-USEC-2012` (alias `L-USEC2012`), and correction-zero (`L-USEC-C-0`).
2. 30.18 boundaries: `L-USEC-3018` (alias `L-USEC3018`).
3. Base road allowance offsets are 20.11 m (`L-SEC`) and 30.16 m (`L-USEC`).
4. Scaled working bands may be represented as 20.12 / 30.18.

## Section Building Rules

1. Township/range orientation: township increases north, range increases west.
2. Section numbering: use 1-36 serpentine numbering; section 1 is at the southeast corner.
3. Width classification rule for section/quarter inference: width >= 25.0 m is unsurveyed (`L-USEC`), otherwise surveyed (`L-SEC`).
4. Correction-line cadence is every 4 townships anchored at township 58 (58, 54, 50, ... and 62, 66, ...). Canonical inset is 5.03 m inward.
   Implementation note: correction post-build companion creation currently uses 5.02 m for compatibility tolerance.
5. Rule 0/20 intersection continuity: 0 and 20.12 section lines must form continuous cross intersections (no interior break-through behavior).
6. Road allowance construction alternates with blind-line contexts between sections as part of gap/offset generation.
7. L-shaped 30.16 behavior: extend through the intended two-section pattern and do not cross hard section boundaries; interactions are resolved against allowed quarter/blind targets.
8. When surveyed and unsurveyed allowances meet, align by the 20.11 companion boundary; 30.16 does not continue through that resolved boundary.
9. Partial townships are supported by clipping/trimming to requested/context windows and skipping invalid boundary construction.
10. Vertical road allowance ownership is to the quarter/section on the right (east) side.
11. Horizontal road allowance ownership is to the quarter/section above (north) side.
12. Quarter (`L-QSEC`) endpoints must terminate on hard section boundaries.
13. Blind-line endpoints must terminate on hard section boundaries.
14. LSD (`L-SECTION-LSD`) endpoints must terminate on hard section boundaries and must not terminate on 30.18 boundaries when a valid hard section boundary target exists.
15. No section-building linework (section, quarter, blind, LSD) may remain inside a 20.12 corridor after endpoint enforcement and cleanup.
16. Baseline seam priority is mandatory: 4-township cadence seam rows (for example 57/56, 53/52, 49/48) are forced to `L-SEC` ahead of conflicting relayer outcomes.
17. LSD linework must not enter or pass through 20.11 road-allowance interiors.
18. Only LSD endpoints are midpoint-adjusted; `L-SEC` / `L-USEC` linework remains bearing-stable and is only extended/trimmed through valid intersections.
19. JSONL input represents interior section geometry only; road-allowance gaps are inferred and built from that interior.

## Disposition Labeling Rules

Deferred intentionally. To be redefined in a separate pass.

## Program Order Of Operation (Requested/Checked Items)

A. Section order of operation:

1. Draw requested sections first with LSD explicitly deferred (`drawLsds: false`).
2. Draw adjoining/context sections.
3. Generate road allowance gap/offset geometry.
4. Run canonical RA cleanup/normalization and enforce section/quarter/blind endpoint rules before any LSD draw.
5. Draw LSD lines in the deferred stage.
6. Enforce LSD endpoints on hard section boundaries.
7. Run the final endpoint convergence pass again (section/quarter/blind and LSD).
8. Run correction-line post-build after convergence (seam detection/classification, correction relayer/inner-companion/connect passes).
9. If correction geometry changed, re-enforce quarter/blind/LSD endpoints; then rebuild LSD labels at final intersections.

B. Disposition order of operation:

Deferred intentionally (not yet defined in this ruleset).
