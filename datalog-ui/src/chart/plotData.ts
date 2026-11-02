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

import type { PlotPoints } from "../types";

/** First index with a time at or after min, by binary search over the sorted times. */
export function lowerIndex(times: number[], min: number): number {
  let low = 0;
  let high = times.length;
  while (low < high) {
    const mid = (low + high) >> 1;
    if (times[mid] < min) {
      low = mid + 1;
    } else {
      high = mid;
    }
  }
  return low;
}

/** First index with a time after max. */
export function upperIndex(times: number[], max: number, from = 0): number {
  let low = from;
  let high = times.length;
  while (low < high) {
    const mid = (low + high) >> 1;
    if (times[mid] <= max) {
      low = mid + 1;
    } else {
      high = mid;
    }
  }
  return low;
}

/** The index range [start, end) of the points between min and max. */
export function indexRange(times: number[], min: number, max: number): [number, number] {
  const start = lowerIndex(times, min);
  return [start, upperIndex(times, max, start)];
}

export function countInRange(times: number[], min: number, max: number): number {
  const [start, end] = indexRange(times, min, max);
  return end - start;
}

/**
 * The value a stepped line shows at a time: the last point at or before it. Null in a gap (invalid
 * values), before the first point and in the future, where no value was recorded yet.
 */
export function valueAtTime(
  data: PlotPoints | null,
  time: number,
  now = Date.now() / 1000
): number | null {
  if (!data || data.times.length === 0 || time > now) {
    return null;
  }
  const index = upperIndex(data.times, time) - 1;
  if (index < 0) {
    return null;
  }
  return data.raw[index] ?? null;
}

/**
 * The points a plot draws and the time span they stand for: the span that was loaded, not where the points lie,
 * so a tag without values in part of it is not loaded again. A preview filled the gaps of the span with coarse points.
 */
export interface DrawnPoints {
  points: PlotPoints;
  from: number;
  to: number;
  preview: boolean;
}

/** Seconds of the view up to now that the drawn span does not cover. */
export function uncoveredSeconds(
  drawn: { from: number; to: number } | null,
  min: number,
  max: number,
  now: number
): number {
  const end = Math.min(max, now);
  if (end <= min) {
    return 0;
  }
  if (!drawn) {
    return end - min;
  }
  const covered = Math.max(0, Math.min(end, drawn.to) - Math.max(min, drawn.from));
  return end - min - covered;
}

function slicePoints(points: PlotPoints, start: number, end: number) {
  return {
    times: points.times.slice(start, end),
    raw: points.raw.slice(start, end)
  };
}

/**
 * The drawn points with the gaps of the view [min, max] filled from a preview: the preview points before and after
 * the drawn span, the drawn points within it and the view. The joins get no null point, which would show a gap of
 * a whole window; the stepped line holds the last value up to the next point, as everywhere else.
 */
export function mergePreview(
  drawn: DrawnPoints | null,
  preview: PlotPoints,
  min: number,
  max: number,
  now: number
): DrawnPoints {
  // Nothing after now was asked for, so later values are still missing
  const to = Math.min(max, now);
  if (!drawn || drawn.to <= min || drawn.from >= max) {
    return { points: preview, from: min, to, preview: true };
  }
  const keepFrom = Math.max(drawn.from, min);
  const keepTo = Math.min(drawn.to, max);
  const [oldStart, oldEnd] = indexRange(drawn.points.times, keepFrom, keepTo);
  const parts = [
    slicePoints(preview, 0, lowerIndex(preview.times, keepFrom)),
    slicePoints(drawn.points, oldStart, oldEnd),
    slicePoints(preview, upperIndex(preview.times, keepTo), preview.times.length)
  ];
  return {
    points: {
      ...preview,
      times: parts.flatMap((part) => part.times),
      raw: parts.flatMap((part) => part.raw)
    },
    from: min,
    to: Math.max(to, keepTo),
    preview: true
  };
}
