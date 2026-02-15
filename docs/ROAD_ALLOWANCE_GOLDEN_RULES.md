# Road Allowance Golden Rules

Date added: 2026-02-12

These rules are authoritative for ATS background generation. If any code path contradicts them, stop and revisit the implementation.

See also `docs/SECTION_RULES.md` for the township/section canonical ruleset (including 20.11/30.16 defaults and 20.12/30.18 scaled values).

1. West road allowance of a section belongs to that section.
2. South road allowance of a section belongs to that section.
3. JSONL represents interior section geometry only; road-allowance gaps are inferred and built from that interior.
4. A 30.16 (+/- tolerance) gap is theoretical road allowance (`L-USEC`).
5. A 20.11 (+/- tolerance) gap is surveyed road allowance (`L-SEC`).
6. LSD lines always end on the midpoint of a 20.11 boundary (`L-SEC` or `L-USEC`, whichever is the 20.11 boundary).
7. In 30.16 corridors, the 20.11 line is offset from the most western line.
8. Road-allowance bearing/angle must not be changed by nearest-point snapping.
9. If a blind line connects to an N-S `L-SEC` road allowance, that blind linework is `L-SEC` for that quarter.
10. When a N-S and E-W road allowance meet, the two 20.11 lines must terminate at their apparent intersection.
11. LSD lines must never enter or pass through a 20.11 road allowance (`L-SEC` or `L-USEC`).
12. LSD lines must never terminate on a 30.16 boundary when a valid 20.11 boundary exists.
13. No section/LSD blind line should pass through or inside a 20.11 (`L-SEC`) road allowance.
14. Only LSD endpoints should be midpoint-adjusted; `L-SEC` / `L-USEC` linework should remain bearing-stable and only be extended/trimmed via valid intersections.

