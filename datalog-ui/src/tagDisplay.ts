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

import { formatNumber } from "./format";
import type { DataTagActualValue, DataTagListItem } from "./types";

const ARCHIVE_VALUE_MIN_MS = 10000;
const TIMEOUT_FACTOR = 3;
const LIVE_SOURCE_STATES = new Set(["connected"]);

export interface TagDisplay {
  valueText: string;
  status: string;
  badge: string;
  title?: string;
}

/** Short badge text for a problem reported by the source; the full text is the tooltip. */
export function issueStatus(issue: string): string {
  if (issue.startsWith("Not resolved")) {
    return "Not resolved";
  }
  if (issue.includes("at most half")) {
    return "DataLogXE limit";
  }
  if (issue.includes("limit")) {
    return "PLC limit";
  }
  if (issue.includes("cannot be logged")) {
    return "Not loggable";
  }
  return "Error";
}

export function isDataSourceOffline(sourceState: string | undefined): boolean {
  if (!sourceState) {
    return false;
  }
  return !LIVE_SOURCE_STATES.has(sourceState.toLowerCase());
}

export function displayTag(
  tag: DataTagListItem | undefined,
  actual: DataTagActualValue | undefined,
  nowMs: number,
  sourceState?: string
): TagDisplay {
  if (!tag) {
    return { valueText: "", status: "", badge: "default" };
  }

  if (!tag.isActive) {
    return { valueText: "", status: "Inactive", badge: "inactive" };
  }

  if (isDataSourceOffline(sourceState)) {
    return { valueText: "***", status: "Offline", badge: "inactive" };
  }

  if (actual?.issue) {
    return { valueText: "***", status: issueStatus(actual.issue), badge: "error", title: actual.issue };
  }

  if (!actual) {
    return { valueText: "…", status: "Pending", badge: "connecting" };
  }

  const state = String(actual.state);
  if (state === "Invalid" || state === "0") {
    return { valueText: "***", status: "Invalid", badge: "error" };
  }

  const age = nowMs - Date.parse(actual.time);
  const maxAge = Math.max(tag.cycle, ARCHIVE_VALUE_MIN_MS) * TIMEOUT_FACTOR;
  if (!Number.isFinite(age) || age > maxAge) {
    return { valueText: "***", status: "Stale", badge: "error" };
  }

  const valueText = formatNumber(actual.value, { maximumFractionDigits: 6 });
  if (state === "Resend" || state === "2") {
    return { valueText, status: "Resend", badge: "connected" };
  }

  return { valueText, status: "Valid", badge: "connected" };
}
