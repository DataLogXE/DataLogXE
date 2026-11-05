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

import type uPlot from "uplot";

/*
 * Timing of the chart drawing, to compare browsers. Off by default; ?perf in the URL turns it on and keeps it on in
 * this browser, ?perf=0 turns it off again. While on:
 * - every 5 seconds with activity, a table in the browser console: count, average and longest time of each step,
 * - each step as a performance measure ("dlx ..."), so it shows in the marker track of the browser profilers,
 * - frames that took longer than 50 ms, also outside the chart code (layout, style, garbage collection, network).
 * The steps: a whole redraw of a plot (clear, axes, series and all plugins), its series (the line), its time grid
 * under and over the series, the day names, and a cursor move (all setCursor hooks of a plot).
 */

const STORAGE_KEY = "datalog.perf";
const REPORT_MS = 5000;
const LONG_FRAME_MS = 50;

function readEnabled(): boolean {
  try {
    const param = new URLSearchParams(window.location.search).get("perf");
    if (param != null) {
      const on = param !== "0";
      window.localStorage.setItem(STORAGE_KEY, on ? "1" : "0");
      return on;
    }
    return window.localStorage.getItem(STORAGE_KEY) === "1";
  } catch {
    return false;
  }
}

export const drawTimingEnabled: boolean = readEnabled();

type Stat = { count: number; total: number; max: number };

const stats = new Map<string, Stat>();
let reportTimer: number | null = null;

function record(name: string, start: number, end: number) {
  const ms = end - start;
  const stat = stats.get(name) ?? { count: 0, total: 0, max: 0 };
  stat.count++;
  stat.total += ms;
  stat.max = Math.max(stat.max, ms);
  stats.set(name, stat);
  try {
    performance.measure(`dlx ${name}`, { start, end });
  } catch {
    // Older browsers without measure options: the console table is enough
  }
  reportTimer ??= window.setTimeout(report, REPORT_MS);
}

function report() {
  reportTimer = null;
  const round = (ms: number) => Math.round(ms * 10) / 10;
  const rows: Record<string, { count: number; "avg ms": number; "max ms": number; "total ms": number }> = {};
  for (const [name, stat] of stats) {
    rows[name] = { count: stat.count, "avg ms": round(stat.total / stat.count), "max ms": round(stat.max), "total ms": round(stat.total) };
  }
  stats.clear();
  console.info(`DataLogXE draw timing, last ${REPORT_MS / 1000} s (${navigator.userAgent})`);
  console.table(rows);
}

/** Runs fn and records its time under name, when the timing is on. */
export function timed<T>(name: string, fn: () => T): T {
  if (!drawTimingEnabled) {
    return fn();
  }
  const start = performance.now();
  try {
    return fn();
  } finally {
    record(name, start, performance.now());
  }
}

// Frames longer than LONG_FRAME_MS, while the page is visible (a hidden page gets no frames)
if (drawTimingEnabled) {
  let last = 0;
  const frame = (now: number) => {
    if (last > 0 && document.visibilityState === "visible" && now - last > LONG_FRAME_MS) {
      record(`frame > ${LONG_FRAME_MS} ms`, last, now);
    }
    last = now;
    requestAnimationFrame(frame);
  };
  document.addEventListener("visibilitychange", () => {
    last = 0;
  });
  requestAnimationFrame(frame);
  console.info("DataLogXE draw timing is on (?perf=0 turns it off)");
}

const redrawStart = new WeakMap<uPlot, number>();
const cursorStart = new WeakMap<uPlot, number>();
const seriesStart = new WeakMap<uPlot, number>();

/**
 * The two ends of the timing of a plot: start as the first plugin, end as the last, so the time covers uPlot's own
 * drawing and the hooks of all plugins in between. Empty plugins when the timing is off.
 */
export function drawTimingPlugins(): { start: uPlot.Plugin; end: uPlot.Plugin } {
  if (!drawTimingEnabled) {
    return { start: { hooks: {} }, end: { hooks: {} } };
  }
  return {
    start: {
      hooks: {
        drawClear: (plot) => {
          redrawStart.set(plot, performance.now());
        },
        // After the axes, before the series
        drawAxes: (plot) => {
          seriesStart.set(plot, performance.now());
        },
        setCursor: (plot) => {
          cursorStart.set(plot, performance.now());
        }
      }
    },
    end: {
      hooks: {
        drawSeries: (plot) => {
          const start = seriesStart.get(plot);
          if (start != null) {
            record("series", start, performance.now());
            seriesStart.delete(plot);
          }
        },
        draw: (plot) => {
          const start = redrawStart.get(plot);
          if (start != null) {
            record("plot redraw", start, performance.now());
            redrawStart.delete(plot);
          }
        },
        setCursor: (plot) => {
          const start = cursorStart.get(plot);
          if (start != null) {
            record("cursor move", start, performance.now());
            cursorStart.delete(plot);
          }
        }
      }
    }
  };
}
