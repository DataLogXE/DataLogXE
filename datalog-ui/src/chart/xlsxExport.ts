/*
DataLogXE - A simple and high performance data logger.
Copyright (C) 2026 datalogxe@gmail.com

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/
import { forEachExportRow, type CsvPlot, type CsvResult } from "./csvExport";

/** The rows of an Excel sheet without its header, the most a sheet of the export holds. */
export const MAX_SHEET_ROWS = 1_048_575;

// An Excel workbook (.xlsx) of the exported points, with the same rows and columns as the CSV export, on one sheet or
// on one sheet per plot: a zip of a few XML files, written without compression so no library is needed. The time is
// a real date cell of the local time.

const CHUNK_ROWS = 2000;
const encoder = new TextEncoder();

function escapeXml(text: string): string {
  return text.replace(/[&<>"]/g, (char) => (char === "&" ? "&amp;" : char === "<" ? "&lt;" : char === ">" ? "&gt;" : "&quot;"))
    // Control characters other than tab, line feed and carriage return are not allowed in XML
    .replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/g, "");
}

function textCell(text: string, style = 0): string {
  return `<c t="inlineStr"${style ? ` s="${style}"` : ""}><is><t xml:space="preserve">${escapeXml(text)}</t></is></c>`;
}

function numberCell(value: number | null, style = 0): string {
  return value == null || !Number.isFinite(value) ? "<c/>" : `<c${style ? ` s="${style}"` : ""}><v>${value}</v></c>`;
}

/** The local time as an Excel date: days since 1899-12-30. */
function excelDate(seconds: number): number {
  const date = new Date(seconds * 1000);
  return (seconds * 1000 - date.getTimezoneOffset() * 60_000) / 86_400_000 + 25569;
}

const HEADER = ["Data source", "Data tag", "Time", "Time (UTC)", "Value"];
const COLUMN_WIDTHS = [22, 40, 24, 26, 14];

const SHEET_TYPE = "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";

function contentTypes(sheets: number): string {
  const overrides = Array.from({ length: sheets }, (_, index) =>
    `<Override PartName="/xl/worksheets/sheet${index + 1}.xml" ContentType="${SHEET_TYPE}"/>`).join("\n");
  return `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
<Default Extension="xml" ContentType="application/xml"/>
<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
${overrides}
<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
</Types>`;
}

const ROOT_RELS = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
</Relationships>`;

function workbook(names: string[]): string {
  const sheets = names.map((name, index) =>
    `<sheet name="${escapeXml(name)}" sheetId="${index + 1}" r:id="rId${index + 1}"/>`).join("");
  return `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
<sheets>${sheets}</sheets>
</workbook>`;
}

// The sheets first, the styles after them
function workbookRels(sheets: number): string {
  const rels = Array.from({ length: sheets }, (_, index) =>
    `<Relationship Id="rId${index + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet${index + 1}.xml"/>`);
  return `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
${rels.join("\n")}
<Relationship Id="rId${sheets + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
</Relationships>`;
}

/**
 * Sheet names as Excel takes them: at most 31 characters, none of : \ / ? * [ ], not blank, and unique without
 * regard to case (a number in brackets tells equal names apart).
 */
export function sheetNames(names: string[]): string[] {
  const used = new Set<string>();
  return names.map((name) => {
    const base = name.replace(/[:\\/?*[\]]/g, "_").replace(/^'+|'+$/g, "").trim() || "Data";
    let candidate = base.slice(0, 31);
    for (let n = 2; used.has(candidate.toLowerCase()); n++) {
      const suffix = ` (${n})`;
      candidate = base.slice(0, 31 - suffix.length) + suffix;
    }
    used.add(candidate.toLowerCase());
    return candidate;
  });
}

// Style 1 is the date of the column Time, style 2 the bold header
const STYLES = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
<numFmts count="1"><numFmt numFmtId="164" formatCode="yyyy-mm-dd hh:mm:ss.000"/></numFmts>
<fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
<fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
<borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
<cellXfs count="3">
<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
<xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>
<xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/>
</cellXfs>
<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>
</styleSheet>`;

/** One sheet of the workbook: its rows from the plots, at most maxRows of them. */
function buildSheet(plots: CsvPlot[], min: number, max: number, maxRows: number) {
  const parts: Uint8Array[] = [];
  let lines: string[] = [];
  let rowNumber = 1;
  const flush = () => {
    if (lines.length > 0) {
      parts.push(encoder.encode(lines.join("")));
      lines = [];
    }
  };

  const cols = COLUMN_WIDTHS.map((width, index) => `<col min="${index + 1}" max="${index + 1}" width="${width}" customWidth="1"/>`).join("");
  lines.push(
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
      + '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
      + '<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>'
      + `<cols>${cols}</cols><sheetData>`
      + `<row r="1">${HEADER.map((title) => textCell(title, 2)).join("")}</row>`
  );

  const { rows, truncated } = forEachExportRow(plots, min, max, maxRows, (point) => {
    rowNumber += 1;
    lines.push(
      `<row r="${rowNumber}">`
        + textCell(point.sourceName)
        + textCell(point.tagName)
        + numberCell(excelDate(point.time), 1)
        + textCell(new Date(point.time * 1000).toISOString())
        + numberCell(point.value)
        + "</row>"
    );
    if (lines.length >= CHUNK_ROWS) {
      flush();
    }
  });
  lines.push(`</sheetData><autoFilter ref="A1:E${rowNumber}"/></worksheet>`);
  flush();
  return { parts, rows, truncated };
}

/**
 * The workbook of the plots: all rows on one sheet "Data", or with sheetPerPlot one sheet per plot named after its
 * data tag. The row limit holds for each sheet.
 */
export function buildXlsx(
  plots: CsvPlot[],
  min: number,
  max: number,
  sheetPerPlot = false,
  maxRows = MAX_SHEET_ROWS
): CsvResult {
  const groups = sheetPerPlot && plots.length > 0 ? plots.map((plot) => [plot]) : [plots];
  const names = sheetPerPlot && plots.length > 0 ? sheetNames(plots.map((plot) => plot.tagName)) : ["Data"];
  const sheets: Uint8Array[][] = [];
  let rows = 0;
  let truncated = false;
  for (const group of groups) {
    const sheet = buildSheet(group, min, max, maxRows);
    sheets.push(sheet.parts);
    rows += sheet.rows;
    truncated ||= sheet.truncated;
  }

  const blob = zip([
    { name: "[Content_Types].xml", parts: [encoder.encode(contentTypes(sheets.length))] },
    { name: "_rels/.rels", parts: [encoder.encode(ROOT_RELS)] },
    { name: "xl/workbook.xml", parts: [encoder.encode(workbook(names))] },
    { name: "xl/_rels/workbook.xml.rels", parts: [encoder.encode(workbookRels(sheets.length))] },
    { name: "xl/styles.xml", parts: [encoder.encode(STYLES)] },
    ...sheets.map((parts, index) => ({ name: `xl/worksheets/sheet${index + 1}.xml`, parts }))
  ]);
  return { blob, rows, truncated };
}

const CRC_TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) {
      c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    }
    table[n] = c >>> 0;
  }
  return table;
})();

function crc32(parts: Uint8Array[]): number {
  let crc = 0xffffffff;
  for (const part of parts) {
    for (let i = 0; i < part.length; i++) {
      crc = CRC_TABLE[(crc ^ part[i]) & 0xff] ^ (crc >>> 8);
    }
  }
  return (crc ^ 0xffffffff) >>> 0;
}

/** A zip archive of the files, stored without compression. */
function zip(files: { name: string; parts: Uint8Array[] }[]): Blob {
  const out: BlobPart[] = [];
  const central: Uint8Array[] = [];
  let offset = 0;
  const now = new Date();
  const dosTime = (now.getHours() << 11) | (now.getMinutes() << 5) | (now.getSeconds() >> 1);
  const dosDate = ((now.getFullYear() - 1980) << 9) | ((now.getMonth() + 1) << 5) | now.getDate();

  for (const file of files) {
    const name = encoder.encode(file.name);
    const size = file.parts.reduce((total, part) => total + part.length, 0);
    const crc = crc32(file.parts);

    const local = new DataView(new ArrayBuffer(30));
    local.setUint32(0, 0x04034b50, true);
    local.setUint16(4, 20, true);
    local.setUint16(6, 0x0800, true);
    local.setUint16(8, 0, true);
    local.setUint16(10, dosTime, true);
    local.setUint16(12, dosDate, true);
    local.setUint32(14, crc, true);
    local.setUint32(18, size, true);
    local.setUint32(22, size, true);
    local.setUint16(26, name.length, true);
    local.setUint16(28, 0, true);
    out.push(local.buffer, name, ...file.parts);

    const entry = new DataView(new ArrayBuffer(46));
    entry.setUint32(0, 0x02014b50, true);
    entry.setUint16(4, 20, true);
    entry.setUint16(6, 20, true);
    entry.setUint16(8, 0x0800, true);
    entry.setUint16(10, 0, true);
    entry.setUint16(12, dosTime, true);
    entry.setUint16(14, dosDate, true);
    entry.setUint32(16, crc, true);
    entry.setUint32(20, size, true);
    entry.setUint32(24, size, true);
    entry.setUint16(28, name.length, true);
    entry.setUint32(42, offset, true);
    central.push(new Uint8Array(entry.buffer), name);

    offset += 30 + name.length + size;
  }

  const centralSize = central.reduce((total, part) => total + part.length, 0);
  const end = new DataView(new ArrayBuffer(22));
  end.setUint32(0, 0x06054b50, true);
  end.setUint16(8, files.length, true);
  end.setUint16(10, files.length, true);
  end.setUint32(12, centralSize, true);
  end.setUint32(16, offset, true);
  out.push(...central, end.buffer);

  return new Blob(out, { type: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" });
}
