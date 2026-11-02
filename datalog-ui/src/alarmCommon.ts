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

import { formatClassName } from "./format";
import type { EventLogItem } from "./types";

/** The width of the filters panel can be changed within these limits. */
export const FILTERS_WIDTH_MIN = 200;
export const FILTERS_WIDTH_MAX = 440;
export const FILTERS_WIDTH_DEFAULT = 220;

export function clampFiltersWidth(value: number): number {
  return Math.round(Math.min(FILTERS_WIDTH_MAX, Math.max(FILTERS_WIDTH_MIN, value)));
}

/**
 * The width kept in the browser under `key`. A page that has none of its own yet takes the one of the time before
 * every page had its own (`legacyKey`).
 */
export function readFiltersWidth(key: string, legacyKey: string): number {
  try {
    const stored = Number(window.localStorage.getItem(key) ?? window.localStorage.getItem(legacyKey));
    return Number.isFinite(stored) && stored > 0 ? clampFiltersWidth(stored) : FILTERS_WIDTH_DEFAULT;
  } catch {
    return FILTERS_WIDTH_DEFAULT;
  }
}

export const DETAIL_HEIGHT_MIN = 130;

/** The height of the detail panel under `key` (or `legacyKey`, see readFiltersWidth); null fits the panel to its content. */
export function readDetailHeight(key: string, legacyKey: string): number | null {
  try {
    const raw = window.localStorage.getItem(key) ?? window.localStorage.getItem(legacyKey);
    const stored = raw === null ? Number.NaN : Number(raw);
    return Number.isFinite(stored) && stored >= DETAIL_HEIGHT_MIN ? Math.round(stored) : null;
  } catch {
    return null;
  }
}

export function queryText(value: unknown): string {
  return typeof value === "string" ? value : "";
}

export function parseSourceIds(value: string): number[] {
  const ids: number[] = [];
  for (const part of value.split(",")) {
    const id = Number(part);
    if (!Number.isInteger(id) || id <= 0 || ids.includes(id)) {
      continue;
    }

    ids.push(id);
  }

  return ids;
}

/** A time of a stored filter: a positive number of milliseconds, else none. */
export function storedTime(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) && value > 0 ? value : null;
}

export function formatClassDetail(item: EventLogItem): string {
  const symbol = item.alarmClassSymbol?.trim() ?? "";
  const name = formatClassName(item.alarmClassName);
  if (symbol && name && symbol !== name) {
    return `${symbol} (${name})`;
  }
  return symbol || name || "—";
}

export interface ProcessProps {
  condition: string;
  opcSource: string;
  branch: string;
  /** S7CommPlus alarm: the PLC location and the fields of the S7 alarm, in display order. */
  s7: boolean;
  location: string;
  s7Fields: { label: string; value: string }[];
}

// Props of an S7CommPlus alarm row and their labels.
const S7_PROP_LABELS: [string, string][] = [
  ["CpuAlarmId", "CPU alarm ID"],
  ["MessageType", "Message type"],
  ["Domain", "Alarm domain"],
  ["AlarmClassId", "Alarm class ID"],
  ["Priority", "Priority"],
  ["PlcTime", "PLC time"],
  ["InfoText", "Info text"],
  ["AssociatedValues", "Associated values"]
];

export function readProcessProps(raw: string | undefined): ProcessProps {
  const empty: ProcessProps = { condition: "", opcSource: "", branch: "", s7: false, location: "", s7Fields: [] };
  if (!raw) {
    return empty;
  }

  try {
    const parsed = JSON.parse(raw) as Record<string, string | undefined>;
    if (parsed.CpuAlarmId) {
      return {
        ...empty,
        s7: true,
        location: parsed.SourceName?.trim() || "",
        s7Fields: S7_PROP_LABELS.filter(([key]) => parsed[key]?.trim()).map(([key, label]) => ({
          label,
          value: String(parsed[key]).trim()
        }))
      };
    }
    return {
      ...empty,
      condition: parsed.ConditionName?.trim() || "",
      opcSource: parsed.SourceName?.trim() || "",
      branch: parsed.BranchId?.trim() || ""
    };
  } catch {
    return empty;
  }
}

function parseDsName(props: string): string | null {
  if (!props) {
    return null;
  }

  try {
    const parsed = JSON.parse(props) as { DsName?: string };
    return parsed.DsName?.trim() || null;
  } catch {
    return null;
  }
}

export function sourceLabelFor(row: EventLogItem, sourceNameById: Record<number, string>): string {
  if (row.sourceId > 0) {
    return sourceNameById[row.sourceId] ?? parseDsName(row.props) ?? `Source ${row.sourceId}`;
  }

  return parseDsName(row.props) ?? "—";
}
