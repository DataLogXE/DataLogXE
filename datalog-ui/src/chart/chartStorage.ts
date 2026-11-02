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

import { initialBounds, isNowInRange, limitBounds, ARCHIVE_START_FALLBACK } from "./bounds";

const LAST_ID_KEY = "chartLastId";
const RANGE_KEY = "chartRange";
const TREE_VISIBLE_KEY = "chartTreeVisible";
const TREE_EXPANDED_KEY = "chartTreeExpanded";
const LIVE_LABELS_KEY = "chartLiveLabels";
const AXIS_BADGES_KEY = "chartAxisBadges";
const MEASURE_TIMES_KEY = "chartMeasureTimes";

function read(storage: () => Storage, key: string): string | null {
  try {
    return storage().getItem(key);
  } catch {
    return null;
  }
}

function write(storage: () => Storage, key: string, value: string | null) {
  try {
    if (value == null) {
      storage().removeItem(key);
    } else {
      storage().setItem(key, value);
    }
  } catch {
    // Storage may be blocked; the page then starts from the defaults
  }
}

const session = () => window.sessionStorage;
const local = () => window.localStorage;

export function readLastChartId(): number | null {
  const value = Number(read(session, LAST_ID_KEY));
  return Number.isInteger(value) && value > 0 ? value : null;
}

export function writeLastChartId(id: number | null) {
  write(session, LAST_ID_KEY, id == null ? null : String(id));
}

/**
 * The range of the last chart, so switching charts or coming back keeps it. A range that showed
 * the current time moves on by the time that passed, so a live view stays live.
 */
export function readChartRange(now = Date.now() / 1000): [number, number] {
  try {
    const saved = JSON.parse(read(session, RANGE_KEY) ?? "null") as { min?: number; max?: number; savedAt?: number } | null;
    if (saved && Number.isFinite(saved.min) && Number.isFinite(saved.max) && Number.isFinite(saved.savedAt)) {
      let min = saved.min!;
      let max = saved.max!;
      if (isNowInRange(min, max, saved.savedAt!)) {
        min += now - saved.savedAt!;
        max += now - saved.savedAt!;
      }
      if (max > min) {
        return limitBounds(min, max, ARCHIVE_START_FALLBACK, now);
      }
    }
  } catch {
    // An unreadable range falls back to the default
  }
  return initialBounds(now);
}

export function writeChartRange(min: number, max: number) {
  write(session, RANGE_KEY, JSON.stringify({ min, max, savedAt: Date.now() / 1000 }));
}

export function readTreeVisible(): boolean {
  return read(local, TREE_VISIBLE_KEY) !== "0";
}

export function writeTreeVisible(visible: boolean) {
  write(local, TREE_VISIBLE_KEY, visible ? "1" : "0");
}

export function readExpandedFolders(): Set<number> {
  try {
    const ids = JSON.parse(read(local, TREE_EXPANDED_KEY) ?? "[]") as unknown;
    return new Set(Array.isArray(ids) ? ids.filter((id): id is number => Number.isInteger(id)) : []);
  } catch {
    return new Set();
  }
}

export function writeExpandedFolders(ids: ReadonlySet<number>) {
  write(local, TREE_EXPANDED_KEY, JSON.stringify([...ids]));
}

/** Whether the live labels at the crosshair show; on until the user switches them off. */
export function readLiveLabels(): boolean {
  return read(local, LIVE_LABELS_KEY) !== "0";
}

export function writeLiveLabels(shown: boolean) {
  write(local, LIVE_LABELS_KEY, shown ? "1" : "0");
}

/** Whether the badges of the crosshair on the time and the value axis show; on until the user switches them off. */
export function readAxisBadges(): boolean {
  return read(local, AXIS_BADGES_KEY) !== "0";
}

export function writeAxisBadges(shown: boolean) {
  write(local, AXIS_BADGES_KEY, shown ? "1" : "0");
}

/**
 * The times of the measuring cursors X1 and X2 in unix seconds, null for a cursor that is off. They are the same for
 * every chart of the browser: the charts page and the chart of a data tag.
 */
export function readMeasureTimes(): [number | null, number | null] {
  try {
    const saved = JSON.parse(read(local, MEASURE_TIMES_KEY) ?? "null") as unknown;
    if (Array.isArray(saved) && saved.length === 2) {
      const time = (value: unknown) => (typeof value === "number" && Number.isFinite(value) ? value : null);
      return [time(saved[0]), time(saved[1])];
    }
  } catch {
    // Unreadable times mean that both cursors are off
  }
  return [null, null];
}

export function writeMeasureTimes(times: readonly [number | null, number | null]) {
  write(local, MEASURE_TIMES_KEY, times[0] == null && times[1] == null ? null : JSON.stringify(times));
}
