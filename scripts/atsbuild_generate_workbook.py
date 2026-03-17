#!/usr/bin/env python3
import argparse
import json
import os
import zipfile
from xml.sax.saxutils import escape


HEADERS = ["1/4", "Sec", "Twp", "Rge", "M"]


def cell_ref(col_index, row_index):
    col_name = ""
    value = col_index
    while value:
        value, remainder = divmod(value - 1, 26)
        col_name = chr(ord("A") + remainder) + col_name
    return f"{col_name}{row_index}"


def parse_spec(path):
    with open(path, "r", encoding="utf-8-sig") as handle:
        data = json.load(handle)

    build = data.get("build", data)
    rows = build.get("rows", build.get("requests", []))
    if not rows:
        raise SystemExit("Spec must contain at least one build row under 'rows' or 'requests'.")

    client = str(build.get("client", "")).strip()
    zone = str(build.get("zone", "")).strip()
    if not client:
        raise SystemExit("Spec is missing 'client'.")
    if not zone:
        raise SystemExit("Spec is missing 'zone'.")

    normalized_rows = []
    for row in rows:
        normalized_rows.append(
            [
                str(row.get("quarter", row.get("q", ""))).strip(),
                str(row.get("section", row.get("sec", ""))).strip(),
                str(row.get("township", row.get("twp", ""))).strip(),
                str(row.get("range", row.get("rge", ""))).strip(),
                str(row.get("meridian", row.get("m", ""))).strip(),
            ]
        )

    return {
        "client": client,
        "zone": zone,
        "rows": normalized_rows,
    }


def build_shared_strings(spec):
    values = [spec["client"], spec["zone"], *HEADERS]
    for row in spec["rows"]:
        values.extend(row)

    unique = []
    index_by_value = {}
    for value in values:
        if value not in index_by_value:
            index_by_value[value] = len(unique)
            unique.append(value)

    return unique, index_by_value


def shared_string_xml(values):
    items = "".join(f"<si><t>{escape(value)}</t></si>" for value in values)
    return (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        '<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" '
        f'count="{len(values)}" uniqueCount="{len(values)}">{items}</sst>'
    )


def worksheet_xml(spec, shared_index):
    rows = []

    def add_string_cell(cells, col_index, row_index, value):
        ref = cell_ref(col_index, row_index)
        string_index = shared_index[value]
        cells.append(f'<c r="{ref}" t="s"><v>{string_index}</v></c>')

    row1 = []
    add_string_cell(row1, 2, 1, spec["client"])
    add_string_cell(row1, 5, 1, spec["zone"])
    rows.append(f'<row r="1">{"".join(row1)}</row>')

    header_cells = []
    for offset, value in enumerate(HEADERS, start=1):
        add_string_cell(header_cells, offset, 3, value)
    rows.append(f'<row r="3">{"".join(header_cells)}</row>')

    for row_number, values in enumerate(spec["rows"], start=4):
        cells = []
        for col_index, value in enumerate(values, start=1):
            add_string_cell(cells, col_index, row_number, value)
        rows.append(f'<row r="{row_number}">{"".join(cells)}</row>')

    sheet_data = "".join(rows)
    return (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" '
        'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
        f"<sheetData>{sheet_data}</sheetData></worksheet>"
    )


def write_workbook(output_path, spec):
    shared_values, shared_index = build_shared_strings(spec)
    output_dir = os.path.dirname(output_path)
    if output_dir:
        os.makedirs(output_dir, exist_ok=True)
    with zipfile.ZipFile(output_path, "w", compression=zipfile.ZIP_DEFLATED) as workbook:
        workbook.writestr(
            "[Content_Types].xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
  <Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>
</Types>""",
        )
        workbook.writestr(
            "_rels/.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
</Relationships>""",
        )
        workbook.writestr(
            "xl/workbook.xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
  <sheets>
    <sheet name="ATSBUILD_Input" sheetId="1" r:id="rId1"/>
  </sheets>
</workbook>""",
        )
        workbook.writestr(
            "xl/_rels/workbook.xml.rels",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
  <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/>
</Relationships>""",
        )
        workbook.writestr("xl/worksheets/sheet1.xml", worksheet_xml(spec, shared_index))
        workbook.writestr(
            "xl/styles.xml",
            """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
  <fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>
  <fills count="1"><fill><patternFill patternType="none"/></fill></fills>
  <borders count="1"><border/></borders>
  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
  <cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs>
</styleSheet>""",
        )
        workbook.writestr("xl/sharedStrings.xml", shared_string_xml(shared_values))


def main():
    parser = argparse.ArgumentParser(description="Generate an ATSBUILD_Input workbook from JSON.")
    parser.add_argument("--spec", required=True, help="Path to JSON spec.")
    parser.add_argument("--output", required=True, help="Path to output .xlsx workbook.")
    args = parser.parse_args()

    spec = parse_spec(args.spec)
    output = os.path.abspath(args.output)
    write_workbook(output, spec)
    print(output)


if __name__ == "__main__":
    main()
