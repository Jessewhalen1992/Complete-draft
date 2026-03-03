# WILDLIFE SWEEPS – Workflow Notes

## PHOTOJPG4 (AutoCAD)
1. Start command: `PHOTOJPG4` or use the palette.
2. Select one Photo_Location block (sample) to capture the block name.
3. Pick any JPG in the folder that contains files named with `_Page_XX`.
4. Confirm the JPEG page number for PIC 1 (default is 2).
5. Select blocks or press Enter for all matching blocks in the current space.
6. For each group of four photos, pick a base insertion point.

### Placement rules
- Images are inserted in four positions:
  - (0, 0)
  - (+Offset X, 0)
  - (0, -Offset Y)
  - (+Offset X, -Offset Y)
- Images are placed on the `AS-PHOTO` layer (configurable).
- PIC numbers are derived from the nearest TEXT/MTEXT containing `PIC <digits>` within the search radius.

## NUMPTS
1. Start command: `NUMPTS` or use the palette.
2. Select the point, block, or text objects to number.
3. Choose numbering order: LeftRight or SWNE.
4. Provide text height and starting number.
5. Pick a CSV output path.

### Output
- CSV columns: `OriginalText,Lat,Long,LatDDMMSS,LongDDMMSS,Northing,Easting,Number`.
- Lat/Long columns use Map 3D/Civil 3D conversion when available, with a built-in NAD83 UTM fallback.
- DDMMSS columns format coordinates like `54°25'57.04" N`.

## PHOTODDMMSS
1. Start command: `PHOTODDMMSS` or use the palette button `USE PHOTO DDMMSS`.
2. Select one Photo_Location block (sample) to capture the block name.
3. Pick any JPG in the folder (the command reads GPS metadata from all JPGs).
4. Select blocks or press Enter for all matching blocks in the current space.
5. For each group of four photos, pick a base insertion point.

### Matching rules
- Each Photo_Location block `#` is matched to the nearest photo GPS coordinate within 1 second of latitude/longitude.
- Images are placed in the same 4-up layout as PHOTOJPG4.

## Palette UI
Run `WLS_PALETTE` to access all controls in one palette.
