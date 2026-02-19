# Road Allowance Golden Rules

Status date: 2026-02-19

These rules are authoritative for ATS background generation. If a code path contradicts them, stop and revisit the implementation.

Primary canonical reference: `docs/specs/sections/SECTION_RULES.md` (Authoritative v2).

## Shared Definitions

1. Hard section boundaries: `L-SEC`, `L-USEC-0`, `L-USEC-2012` (alias `L-USEC2012`), and correction-zero (`L-USEC-C-0`).
2. 30.18 boundaries: `L-USEC-3018` (alias `L-USEC3018`).
3. Base road allowance offsets are 20.11 m (`L-SEC`) and 30.16 m (`L-USEC`).
4. Scaled working bands may be represented as 20.12 / 30.18.

## Road Allowance Rules

1. JSONL input represents interior section geometry only; road-allowance gaps are inferred and built from that interior.
2. Vertical road allowance ownership is to the quarter/section on the right (east) side.
3. Horizontal road allowance ownership is to the quarter/section above (north) side.
4. Equivalent section statement: the west road allowance belongs to that section, and the south road allowance belongs to that section.
5. A 30.16 (+/- tolerance) gap classifies as unsurveyed road allowance (`L-USEC`).
6. A 20.11 (+/- tolerance) gap classifies as surveyed road allowance (`L-SEC`).
7. In 30.16 corridors, 20.11 companion boundaries are the inward hard-target lines used for endpoint resolution.
8. When N-S and E-W road allowances meet, 20.11 companion boundaries terminate at their apparent intersection.
9. When surveyed and unsurveyed allowances meet, align by the 20.11 companion boundary; 30.16 does not continue through that resolved boundary.
10. Quarter (`L-QSEC`) endpoints must terminate on hard section boundaries.
11. Blind-line endpoints must terminate on hard section boundaries.
12. LSD (`L-SECTION-LSD`) endpoints must terminate on hard section boundaries and must not terminate on 30.18 boundaries when a valid hard section boundary target exists.
13. No section-building linework (section, quarter, blind, LSD) may remain inside a 20.12 corridor after endpoint enforcement and cleanup.
14. LSD linework must not enter or pass through 20.11 road-allowance interiors.
15. Only LSD endpoints are midpoint-adjusted; `L-SEC` / `L-USEC` linework remains bearing-stable and is only extended/trimmed through valid intersections.
16. Baseline seam priority is mandatory: 4-township cadence seam rows are forced to `L-SEC` ahead of conflicting relayer outcomes.

