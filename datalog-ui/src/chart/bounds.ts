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

export const MIN_RANGE_SEC = 0.5;
export const MAX_RANGE_SEC = 30 * 24 * 3600;
export const INITIAL_PAST_SEC = 20 * 60;
export const INITIAL_FUTURE_SEC = 2 * 60;
export const FETCH_DEBOUNCE_MS = 450;
// A coarse preview is loaded for a part of the view without points of at least this many device pixels,
// unless the exact points arrive within the delay
export const PREVIEW_MIN_GAP_PX = 16;
export const PREVIEW_DELAY_MS = 200;
export const LIVE_INTERVAL_MS = 10_000;
export const LIVE_MAX_RANGE_SEC = 12 * 3600;
export const LIVE_NOW_PAD_SEC = 5 * 60;
export const SMOOTH_AMT = 0.15;
export const ZOOM_SENSITIVITY = 0.001625;
export const X_AXIS_HEIGHT = 40;
// A plot of a stack without time labels keeps a thin axis, so its grid lines stay
export const HIDDEN_X_AXIS_HEIGHT = 4;
export const Y_AXIS_WIDTH = 80;
// Space above the plot area, so half of the topmost y label is not cut off
export const PLOT_PAD_TOP = 8;
export const ARCHIVE_START_FALLBACK = Date.UTC(2000, 0, 1) / 1000;

export const TIME_SPANS: { seconds: number; label: string }[] = [
  { seconds: 60, label: "1 min" },
  { seconds: 5 * 60, label: "5 min" },
  { seconds: 15 * 60, label: "15 min" },
  { seconds: 3600, label: "1 hour" },
  { seconds: 3 * 3600, label: "3 hours" },
  { seconds: 6 * 3600, label: "6 hours" },
  { seconds: 12 * 3600, label: "12 hours" },
  { seconds: 24 * 3600, label: "1 day" },
  { seconds: 3 * 24 * 3600, label: "3 days" },
  { seconds: 7 * 24 * 3600, label: "1 week" },
  { seconds: 14 * 24 * 3600, label: "2 weeks" },
  { seconds: 30 * 24 * 3600, label: "4 weeks" }
];

export function nowSec(): number {
  return Date.now() / 1000;
}

export function initialBounds(now = nowSec()): [number, number] {
  return [now - INITIAL_PAST_SEC, now + INITIAL_FUTURE_SEC];
}

export function plotHeight(isBool: boolean): number {
  return isBool ? 200 : 380;
}

export function lerp(start: number, end: number, amt: number): number {
  return start + (end - start) * amt;
}

export function limitBounds(
  xMin: number,
  xMax: number,
  archiveMin: number,
  now = nowSec(),
  focus?: number
): [number, number] {
  let min = xMin;
  let max = xMax;
  const reqRange = max - min;
  const anchor = focus ?? (min + max) / 2;
  const pct = reqRange > 0
    ? Math.min(1, Math.max(0, (anchor - min) / reqRange))
    : 0.5;

  let range = reqRange;
  if (range > MAX_RANGE_SEC) {
    range = MAX_RANGE_SEC;
  }
  if (range < MIN_RANGE_SEC) {
    range = MIN_RANGE_SEC;
  }

  min = anchor - pct * range;
  max = min + range;

  const maxTime = now + range * 0.5;
  if (max > maxTime) {
    max = maxTime;
    min = max - range;
  }

  const minTime = archiveMin - range * 0.5;
  if (min < minTime) {
    min = minTime;
    max = min + range;
  }

  return [min, max];
}

export function centerRange(xMin: number, xMax: number, newRange: number): [number, number] {
  const actRange = xMax - xMin;
  const newMin = xMin + actRange / 2 - newRange / 2;
  const newMax = xMax - actRange / 2 + newRange / 2;
  return [newMin, newMax];
}

// Back and Forward (buttons and arrow keys) move the range by this share of it, so the plot keeps half of what it
// showed; the loaded half span on both sides (paddedRange) already holds the new part
const SHIFT_SHARE = 0.5;

export function shiftRange(xMin: number, xMax: number, direction: -1 | 1): [number, number] {
  const step = (xMax - xMin) * SHIFT_SHARE;
  return [xMin + direction * step, xMax + direction * step];
}

/** The range the server loads for a requested one (DataTagAppService.GetPlotPoints): one more span on both sides. */
export function paddedRange(min: number, max: number): [number, number] {
  const span = max - min;
  const extra = span < 120 ? 60 : 0;
  return [min - extra - span / 2, max + extra + span / 2];
}

export function isNowInRange(xMin: number, xMax: number, now = nowSec()): boolean {
  return xMin - LIVE_NOW_PAD_SEC < now && xMax + LIVE_NOW_PAD_SEC > now;
}

export function matchTimespanIndex(rangeSec: number): { index: number; exact: boolean } {
  const range = Math.round(rangeSec);
  for (let i = 0; i < TIME_SPANS.length; i++) {
    if (range === TIME_SPANS[i].seconds) {
      return { index: i, exact: true };
    }
  }
  for (let i = 0; i < TIME_SPANS.length; i++) {
    if (range < TIME_SPANS[i].seconds) {
      return { index: Math.max(i - 0.5, 0), exact: false };
    }
  }
  return { index: TIME_SPANS.length - 1, exact: false };
}

export function zoomTimespanIndex(index: number, direction: -1 | 1): number {
  if (direction < 0) {
    let idx = Math.ceil(index);
    if (idx > 0) {
      idx -= 1;
    }
    return idx;
  }
  let idx = Math.floor(index);
  if (idx < TIME_SPANS.length - 1) {
    idx += 1;
  }
  return idx;
}

export function formatTimespan(rangeSec: number): string {
  const range = Math.round(rangeSec);
  if (range < 60) {
    return `${range} sec`;
  }
  if (range < 3600) {
    return `${Math.floor(range / 60)} min`;
  }
  if (range < 24 * 3600) {
    const hour = Math.round(range / 3600);
    return hour === 1 ? "1 hour" : `${hour} hours`;
  }
  if (range < 7 * 24 * 3600) {
    const days = Math.round(range / (24 * 3600));
    return days === 1 ? "1 day" : `${days} days`;
  }
  const days = Math.round(range / (24 * 3600));
  const weeks = Math.round(days / 7);
  return weeks === 1 ? "1 week" : `${weeks} weeks`;
}

export function dateInputValue(unix: number): string {
  const d = new Date(unix * 1000);
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${y}-${m}-${day}`;
}

export function shiftStartDate(xMin: number, xMax: number, dateValue: string): [number, number] {
  const range = xMax - xMin;
  const parts = dateValue.split("-").map(Number);
  if (parts.length !== 3 || parts.some((n) => !Number.isFinite(n))) {
    return [xMin, xMax];
  }
  const [year, month, day] = parts;
  const localMidnight = new Date(year, month - 1, day).getTime() / 1000;
  const current = new Date(xMin * 1000);
  const timeInDay =
    current.getHours() * 3600 +
    current.getMinutes() * 60 +
    current.getSeconds() +
    current.getMilliseconds() / 1000;
  const newMin = localMidnight + timeInDay;
  return [newMin, newMin + range];
}
