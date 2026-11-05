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

import uPlot from "uplot";
import { formatWeekdayDay, formatWeekdayLines } from "../format";
import { chartColors, type ChartColors } from "../theme";
import { timed } from "./drawTiming";

/*
 * The vertical grid of the time axis, tied to the calendar instead of the step of the time labels, so every zoom
 * shows the same kind of line for the same unit and the user sees at a glance whether days or hours are in view:
 * - every other day lies on a light band, Saturday and Sunday on a band of their own,
 * - midnight is a strong line, a full hour a thin one, finer units dashed and dotted lines that fade in as they get
 *   far enough apart and fade out as they crowd; the hour lines and day names lie above the series, the rest under it,
 * - the weekday and date stand at the top of each day (optional, for the plots with time labels).
 * Zoomed out, when the days get narrow, the midnight lines fade out and the day names get quieter, so the bands
 * alone show the days and nothing pulls the eye from the line. Today keeps its band up to midnight; beyond that
 * and right of now the plot stays plain.
 * All in local time, so the days with a clock change have 23 or 25 hours.
 */

const DAY_SEC = 86400;

type Level = { sec: number; color: (c: ChartColors) => string; width: number; dash: number[] };

// From the coarsest to the finest; midnight is drawn on its own, after these
const LEVELS: Level[] = [
  { sec: 3600, color: (c) => c.hourLine, width: 1, dash: [] },
  { sec: 600, color: (c) => c.minuteLine, width: 1, dash: [6, 4] },
  { sec: 60, color: (c) => c.minuteLine, width: 1, dash: [3, 3] },
  { sec: 10, color: (c) => c.minuteLine, width: 1, dash: [1, 3] },
  { sec: 1, color: (c) => c.minuteLine, width: 1, dash: [1, 5] }
];

// A level fades in between these distances of its lines (CSS pixels)
const FADE_FROM_PX = 6;
const FADE_FULL_PX = 24;
// The width of a day (CSS pixels) from which midnight is a full line; narrower days fade it out down to FROM
const DAY_LINE_FROM_PX = 40;
const DAY_LINE_FULL_PX = 160;
const DAY_LINE_WIDTH = 1.5;
// The hour lines get this width once the dashed 10-minute lines show between them, so the hours stand out
const HOUR_LINE_WIDE = 1.5;
// The day names are bold and full strength from this width of a day on, quieter below it
const DAY_TEXT_FULL_PX = 160;
const DAY_TEXT_QUIET_ALPHA = 0.55;
const DAY_TEXT_FONT_PX = 11;
const DAY_TEXT_PAD_PX = 5;

function localMidnight(sec: number): number {
  const date = new Date(sec * 1000);
  date.setHours(0, 0, 0, 0);
  return date.getTime() / 1000;
}

function nextMidnight(midnight: number): number {
  const date = new Date(midnight * 1000);
  date.setDate(date.getDate() + 1);
  date.setHours(0, 0, 0, 0);
  return date.getTime() / 1000;
}

/** The local midnights from the one at or before min to the first one after max. */
export function dayStarts(min: number, max: number): number[] {
  const starts: number[] = [];
  for (let midnight = localMidnight(min); midnight <= max; midnight = nextMidnight(midnight)) {
    starts.push(midnight);
  }
  starts.push(nextMidnight(starts[starts.length - 1]));
  return starts;
}

// Seconds that local time is ahead of UTC at a time
function localOffset(sec: number): number {
  return -new Date(sec * 1000).getTimezoneOffset() * 60;
}

/** The times in [min, max] that are a whole multiple of step in local time (step up to one hour). */
export function localSteps(min: number, max: number, step: number): number[] {
  const times = new Set<number>();
  // A clock change in the range moves the grid; the offsets at both ends cover both sides of it
  for (const offset of new Set([localOffset(min), localOffset(max)])) {
    for (let t = Math.ceil((min + offset) / step) * step - offset; t <= max; t += step) {
      const local = t + localOffset(t);
      if (Math.abs(local - Math.round(local / step) * step) < 1e-6) {
        times.add(t);
      }
    }
  }
  return [...times].sort((a, b) => a - b);
}

function fade(px: number, from = FADE_FROM_PX, full = FADE_FULL_PX): number {
  return Math.min(1, Math.max(0, (px - from) / (full - from)));
}

// Days counted in local dates, so the bands alternate across a clock change
function dayNumber(midnight: number): number {
  const date = new Date(midnight * 1000);
  return Math.round(Date.UTC(date.getFullYear(), date.getMonth(), date.getDate()) / (DAY_SEC * 1000));
}

function isWeekend(midnight: number): boolean {
  const day = new Date(midnight * 1000).getDay();
  return day === 0 || day === 6;
}

type GridRange = {
  min: number;
  /** The lines end at now (or the end of the plot). */
  lineEnd: number;
  /** The bands and day names end at the end of today. */
  bandEnd: number;
};

// The part of the x range that gets a grid, or null when it lies wholly after today or the plot has no range
function gridRange(plot: uPlot): GridRange | null {
  const min = plot.scales.x.min;
  const max = plot.scales.x.max;
  if (min == null || max == null || !(max > min)) {
    return null;
  }
  const now = Date.now() / 1000;
  const bandEnd = Math.min(max, nextMidnight(localMidnight(now)));
  return bandEnd > min ? { min, lineEnd: Math.min(max, now), bandEnd } : null;
}

// The hour lines lie above the series, so the hours stay readable where the line is dense; the bands, the finer
// lines and midnight lie under the y grid and the series
type Layer = "under" | "over";

function drawGrid(plot: uPlot, { min, lineEnd, bandEnd }: GridRange, layer: Layer) {
  const colors = chartColors();
  const ctx = plot.ctx;
  const pr = uPlot.pxRatio;
  const { left, top, width, height } = plot.bbox;
  const right = Math.min(left + width, plot.valToPos(bandEnd, "x", true));
  const bottom = top + height;
  const pxPerSec = width / pr / ((plot.scales.x.max ?? bandEnd) - min);
  const x = (sec: number) => plot.valToPos(sec, "x", true);
  const days = dayStarts(min, bandEnd);
  const end = lineEnd;

  ctx.save();
  ctx.beginPath();
  ctx.rect(left, top, right - left, height);
  ctx.clip();

  for (let i = 0; layer === "under" && i + 1 < days.length; i++) {
    const fill = isWeekend(days[i]) ? colors.weekendBand : dayNumber(days[i]) % 2 === 0 ? colors.dayBand : null;
    if (fill) {
      const x0 = Math.max(left, x(days[i]));
      const x1 = Math.min(right, x(days[i + 1]));
      ctx.fillStyle = fill;
      ctx.fillRect(x0, top, x1 - x0, height);
    }
  }

  const line = (sec: number, lineWidth: number) => {
    const pos = Math.round(x(sec)) + (Math.round(lineWidth) % 2 === 1 ? 0.5 : 0);
    ctx.lineWidth = lineWidth;
    ctx.beginPath();
    ctx.moveTo(pos, top);
    ctx.lineTo(pos, bottom);
    ctx.stroke();
  };

  // Hours and finer, each level only where it is not also a line of a coarser one
  const hourWidth = fade(600 * pxPerSec) > 0 ? HOUR_LINE_WIDE : 1;
  LEVELS.forEach((level, index) => {
    const alpha = fade(level.sec * pxPerSec);
    const lineWidth = (level.sec === 3600 ? hourWidth : level.width) * pr;
    if (alpha <= 0 || (level.sec === 3600) !== (layer === "over")) {
      return;
    }
    const coarser = index === 0 ? DAY_SEC : LEVELS[index - 1].sec;
    ctx.globalAlpha = alpha;
    ctx.strokeStyle = level.color(colors);
    ctx.setLineDash(level.dash.map((v) => v * pr));
    for (const sec of localSteps(min, end, level.sec)) {
      if ((sec + localOffset(sec)) % coarser !== 0) {
        line(sec, lineWidth);
      }
    }
  });
  ctx.setLineDash([]);

  const dayAlpha = fade(DAY_SEC * pxPerSec, DAY_LINE_FROM_PX, DAY_LINE_FULL_PX);
  if (dayAlpha > 0 && layer === "under") {
    ctx.globalAlpha = dayAlpha;
    ctx.strokeStyle = colors.dayLine;
    for (const midnight of days) {
      if (midnight > min && midnight < end) {
        line(midnight, DAY_LINE_WIDTH * pr);
      }
    }
  }
  ctx.restore();
}

// The weekday and date at the top of each day up to today, short or left out when the day is narrow, quieter when
// zoomed out; above the series, so the line does not cover them
function drawDayNames(plot: uPlot, { min, bandEnd: end }: GridRange) {
  const ctx = plot.ctx;
  const pr = uPlot.pxRatio;
  const { left, top, width } = plot.bbox;
  const right = Math.min(left + width, plot.valToPos(end, "x", true));
  const pad = DAY_TEXT_PAD_PX * pr;
  const days = dayStarts(min, end);
  const dayPx = (DAY_SEC * width) / pr / ((plot.scales.x.max ?? end) - min);
  const full = dayPx >= DAY_TEXT_FULL_PX;

  ctx.save();
  ctx.font = `${full ? 500 : 400} ${Math.round(DAY_TEXT_FONT_PX * pr)}px "Segoe UI", system-ui, sans-serif`;
  ctx.fillStyle = chartColors().dayText;
  ctx.globalAlpha = full ? 1 : DAY_TEXT_QUIET_ALPHA;
  ctx.textBaseline = "top";
  ctx.textAlign = "left";
  // One format for all days, the long one when the widest name fits a whole day, so the names do not change their
  // form from day to day; none when not even the short ones fit
  const dayRoom = dayPx * pr - 2 * pad;
  // No short weekday is narrower than one em, so narrower days skip the names without formatting and measuring
  // every day (zoomed out to years that would be thousands on each redraw)
  if (dayRoom < Math.round(DAY_TEXT_FONT_PX * pr)) {
    ctx.restore();
    return;
  }
  const names = days.slice(0, -1).map((day) => {
    const date = new Date(day * 1000);
    return { long: formatWeekdayDay(date), short: formatWeekdayLines(date)[0] };
  });
  const widest = (key: "long" | "short") => Math.max(...names.map((name) => ctx.measureText(name[key]).width));
  const key = widest("long") <= dayRoom ? "long" : widest("short") <= dayRoom ? "short" : null;
  for (let i = 0; key && i < names.length; i++) {
    const x0 = Math.max(left, plot.valToPos(days[i], "x", true));
    const x1 = Math.min(right, plot.valToPos(days[i + 1], "x", true));
    // A day cut off at the left edge or at now keeps its name only while it still fits
    const text = names[i][key];
    if (ctx.measureText(text).width <= x1 - x0 - 2 * pad) {
      ctx.fillText(text, x0 + pad, top + pad);
    }
  }
  ctx.restore();
}

export function timeGridPlugin(options: { dayNames: boolean }): uPlot.Plugin {
  return {
    hooks: {
      drawClear: (plot) => {
        const range = gridRange(plot);
        if (range) {
          timed("time grid under", () => drawGrid(plot, range, "under"));
        }
      },
      draw: (plot) => {
        const range = gridRange(plot);
        if (range) {
          timed("time grid over", () => drawGrid(plot, range, "over"));
          if (options.dayNames) {
            timed("day names", () => drawDayNames(plot, range));
          }
        }
      }
    }
  };
}
