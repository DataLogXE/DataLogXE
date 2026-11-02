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

import { indexRange } from "./plotData";

const CHUNK_ROWS = 2000;

export interface CsvPlot {
  sourceName: string;
  tagName: string;
  /** The drawn points of the plot or the raw values of the archive. */
  data: { times: number[]; raw: (number | null)[] } | null;
}

export interface CsvResult {
  blob: Blob;
  rows: number;
  truncated: boolean;
}

function localeDecimal(): string {
  try {
    const parts = new Intl.NumberFormat(navigator.languages as string[]).formatToParts(1.5);
    return parts.find((part) => part.type === "decimal")?.value ?? ".";
  } catch {
    return ".";
  }
}

function pad(value: number, length = 2): string {
  return String(value).padStart(length, "0");
}

/** Local time as YYYY-MM-DD HH:mm:ss.SSS, which spreadsheets read as a date. */
export function localTime(date: Date): string {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} `
    + `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}.${pad(date.getMilliseconds(), 3)}`;
}

/** One drawn point of a data tag in an export. */
export interface ExportRow {
  sourceName: string;
  tagName: string;
  /** Seconds since 1970. */
  time: number;
  value: number;
}

/**
 * Hands the drawn points of the plots between min and max to `row`, at most maxRows of them; the
 * gaps of invalid values are left out. Shared by the CSV and the Excel export.
 */
export function forEachExportRow(
  plots: CsvPlot[],
  min: number,
  max: number,
  maxRows: number,
  row: (point: ExportRow) => void
): { rows: number; truncated: boolean } {
  let rows = 0;
  for (const plot of plots) {
    const data = plot.data;
    if (!data) {
      continue;
    }
    const [start, end] = indexRange(data.times, min, max);
    for (let i = start; i < end; i++) {
      const raw = data.raw[i];
      if (raw == null) {
        continue;
      }
      if (rows >= maxRows) {
        return { rows, truncated: true };
      }
      row({
        sourceName: plot.sourceName,
        tagName: plot.tagName,
        time: data.times[i],
        value: raw
      });
      rows += 1;
    }
  }
  return { rows, truncated: false };
}

/**
 * The drawn points of the plots between min and max as CSV, one row per point and data tag. The
 * separators follow the browser locale
 * like a spreadsheet expects them: ";" and "," where the decimal mark is a comma.
 */
export function buildCsv(plots: CsvPlot[], min: number, max: number, maxRows = Infinity): CsvResult {
  const decimal = localeDecimal();
  const separator = decimal === "," ? ";" : ",";
  const field = (text: string) =>
    /["\n\r]/.test(text) || text.includes(separator) ? `"${text.replace(/"/g, '""')}"` : text;
  const number = (value: number | null | undefined) => {
    if (value == null || !Number.isFinite(value)) {
      return "";
    }
    const text = String(value);
    return decimal === "," ? text.replace(".", ",") : text;
  };

  const chunks: string[] = ["﻿"];
  let lines: string[] = [
    ["Data source", "Data tag", "Time", "Time (UTC)", "Value"].join(separator)
  ];
  const flush = () => {
    if (lines.length > 0) {
      chunks.push(lines.join("\r\n") + "\r\n");
      lines = [];
    }
  };

  const { rows, truncated } = forEachExportRow(plots, min, max, maxRows, (point) => {
    const date = new Date(point.time * 1000);
    lines.push([
      field(point.sourceName),
      field(point.tagName),
      localTime(date),
      date.toISOString(),
      number(point.value)
    ].join(separator));
    if (lines.length >= CHUNK_ROWS) {
      flush();
    }
  });
  flush();

  return { blob: new Blob(chunks, { type: "text/csv;charset=utf-8" }), rows, truncated };
}

/** Whether the browser locale writes a comma as decimal mark, which the CSV follows with ";" between the fields. */
export function decimalComma(): boolean {
  return localeDecimal() === ",";
}

// Minutes east of UTC of the local time at the time in seconds
function offsetAt(seconds: number): number {
  return -new Date(seconds * 1000).getTimezoneOffset();
}

/**
 * The offsets of the local time from UTC between min and max as "seconds:minutes" from the time on which each holds,
 * for the CSV the server writes in the local time of the browser. A change is searched in steps of an hour and then
 * found to the second.
 */
export function timeZoneOffsets(min: number, max: number): string[] {
  const result = [`${Math.floor(min)}:${offsetAt(min)}`];
  let time = min;
  while (time < max) {
    const next = Math.min(time + 3600, max);
    if (offsetAt(next) !== offsetAt(time)) {
      let low = time;
      let high = next;
      while (high - low > 1) {
        const middle = Math.floor((low + high) / 2);
        if (offsetAt(middle) === offsetAt(low)) {
          low = middle;
        } else {
          high = middle;
        }
      }
      result.push(`${high}:${offsetAt(high)}`);
    }
    time = next;
  }
  return result;
}

/** A file name without characters that file systems refuse. */
export function exportFileName(name: string, min: number, extension: "csv" | "xlsx"): string {
  const date = new Date(min * 1000);
  const stamp = `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}${pad(date.getMinutes())}`;
  const base = name.replace(/[\\/:*?"<>|]+/g, "_").trim() || "chart";
  return `${base} ${stamp}.${extension}`;
}

export function downloadBlob(blob: Blob, fileName: string) {
  const url = URL.createObjectURL(blob);
  downloadUrl(url, fileName);
  window.setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/** Saves what the URL answers as a download of the browser, which writes it to disk as it arrives. */
export function downloadUrl(url: string, fileName: string) {
  const link = document.createElement("a");
  link.href = url;
  link.download = fileName;
  document.body.appendChild(link);
  link.click();
  link.remove();
}
