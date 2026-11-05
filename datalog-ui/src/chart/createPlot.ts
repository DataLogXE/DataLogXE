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
import type { PlotPoints } from "../types";
import { formatAxisCursorTime, formatAxisCursorValue, formatChartCursorTime, formatChartTimeTicks, formatChartValue, formatLabelValue } from "../format";
import { HIDDEN_X_AXIS_HEIGHT, plotHeight, PLOT_PAD_TOP, X_AXIS_HEIGHT, Y_AXIS_WIDTH } from "./bounds";
import { AXIS_FONT, TIME_INCRS, timeAxisSpace } from "./timeAxis";
import { chartColors, THEME_CHANGED_EVENT } from "../theme";
import { dragAndZoomPlugin, type BoundsHandler } from "./dragAndZoom";
import { LABEL_GAP_Y_PX, LABELS_MOVED_EVENT, obstacleRects, placeLabelRect, type Rect } from "./labelPlacement";
import { touchGesturesPlugin } from "./touchGestures";
import { timeGridPlugin } from "./timeGrid";
import { drawTimingPlugins } from "./drawTiming";

type PlotState = { locked: boolean; last: [number, number] | null; stale: boolean };

const yLockByPlot = new WeakMap<uPlot, PlotState>();

export function setPlotYLock(plot: uPlot, locked: boolean): void {
  const state = yLockByPlot.get(plot);
  if (state) {
    state.locked = locked;
  }
}

export function setPlotStale(plot: uPlot, stale: boolean): void {
  const state = yLockByPlot.get(plot);
  if (state && state.stale !== stale) {
    state.stale = stale;
    plot.redraw(false, false);
  }
}

export function emptyAlignedData(xMin: number, xMax: number): uPlot.AlignedData {
  return [
    [xMin, xMax],
    [null, null]
  ];
}

export function toAlignedData(data: PlotPoints, xMin: number, xMax: number): uPlot.AlignedData {
  if (data.times.length === 0) {
    return emptyAlignedData(xMin, xMax);
  }
  return [data.times, data.raw];
}

/** The y range of a bool plot, so the lines at 0 and 1 do not lie on its edges. */
export const BOOL_Y_RANGE: readonly [number, number] = [-0.1, 1.1];

function computeYRange(
  isBool: boolean,
  dataMin: number | null,
  dataMax: number | null
): [number, number] {
  if (dataMin == null || dataMax == null) {
    return isBool ? [BOOL_Y_RANGE[0], BOOL_Y_RANGE[1]] : [0, 1];
  }
  if (isBool) {
    return [BOOL_Y_RANGE[0], BOOL_Y_RANGE[1]];
  }
  if (Math.abs(dataMin - dataMax) < 0.0001) {
    return [dataMin - 0.5, dataMax + 0.5];
  }
  return [dataMin, dataMax];
}

export interface TagPlotOptions {
  target: HTMLElement;
  seriesName: string;
  isBool: boolean;
  width: number;
  xMin: number;
  xMax: number;
  onBounds: BoundsHandler;
  /** Total height with the x axis; by default the height of a single plot. */
  height?: number;
  /** Time labels below the plot (default); without them only the grid lines stay. */
  showXAxis?: boolean;
  /** Plots with the same key share the cursor position on the time axis. */
  syncKey?: string;
  /** The name and value in the lower left corner (default). */
  hoverLabel?: boolean;
  /** False when a mouse press or touch belongs to something else, so it does not drag the range. */
  canStartDrag?: (plot: uPlot, clientX: number, pointer: "mouse" | "touch") => boolean;
  extraPlugins?: uPlot.Plugin[];
}

export function createTagPlot(options: TagPlotOptions): uPlot {
  const { target, seriesName, isBool, width, xMin, xMax, onBounds } = options;
  const height = options.height ?? plotHeight(isBool);
  const showXAxis = options.showXAxis ?? true;
  const syncKey = options.syncKey;
  const stepped = uPlot.paths.stepped!({ align: 1 });
  const yState: PlotState = { locked: false, last: null, stale: false };
  const timing = drawTimingPlugins();
  // The colors of the theme; while new points load the line is grayed, since the drawn ones no longer match the range
  const stroke = () => (yState.stale ? chartColors().staleLine : chartColors().line);
  const fill = isBool ? () => (yState.stale ? chartColors().staleFill : chartColors().boolFill) : undefined;

  const plot = new uPlot(
    {
      width: Math.max(width, 120),
      height,
      legend: { show: false },
      padding: [PLOT_PAD_TOP, 0, 0, 0],
      series: [
        {},
        {
          label: "RAW",
          stroke,
          width: 1.5,
          paths: stepped,
          points: { show: false },
          fill
        }
      ],
      axes: [
        {
          show: true,
          labelSize: 0,
          font: AXIS_FONT,
          size: showXAxis ? X_AXIS_HEIGHT : HIDDEN_X_AXIS_HEIGHT,
          stroke: () => chartColors().axisText,
          // The vertical lines follow the calendar instead of the labels (timeGridPlugin)
          grid: { show: false },
          ticks: { show: false },
          border: { show: true, stroke: () => chartColors().xBorder, width: 0 },
          space: (_self, _axisIdx, min, max, plotWidth) => timeAxisSpace(min, max, plotWidth),
          incrs: TIME_INCRS,
          values: (_self, splits, _axisIdx, _foundSpace, foundIncr) =>
            showXAxis ? formatChartTimeTicks(splits, foundIncr) : splits.map(() => "")
        },
        {
          font: AXIS_FONT,
          stroke: () => chartColors().axisText,
          grid: { stroke: () => chartColors().grid },
          ticks: { show: false },
          values: (_self, ticks) => ticks.map((rawValue) => {
            if (isBool) {
              if (rawValue === 0 || rawValue === 1) {
                return formatChartValue(rawValue, true);
              }
              return "";
            }
            return formatChartValue(rawValue, false);
          }),
          size: Y_AXIS_WIDTH,
          border: { show: true, stroke: () => chartColors().yBorder, width: 1 }
        }
      ],
      scales: {
        x: {
          time: true,
          auto: false,
          min: xMin,
          max: xMax
        },
        y: {
          range(_plot, dataMin, dataMax) {
            if (yState.locked && yState.last) {
              return yState.last;
            }
            const next = computeYRange(isBool, dataMin, dataMax);
            yState.last = next;
            return next;
          }
        }
      },
      select: { show: false, left: 0, top: 0, width: 0, height: 0 },
      cursor: {
        drag: { setScale: false },
        // uPlot would fit the x axis to the loaded points, which include the padding beyond the range
        bind: { dblclick: () => null },
        ...(syncKey
          ? {
              // Only the time is shared; the y axes of the plots have nothing in common, so the horizontal line shows
              // only in the plot under the mouse (axisBadgesPlugin)
              sync: { key: syncKey, setSeries: false, scales: ["x", null] as [string, null] }
            }
          : {})
      },
      plugins: [
        // First and last, so the timing (?perf) covers everything in between
        timing.start,
        // The day names stand in the plots with time labels, in a stack the first, every fourth and the last
        timeGridPlugin({ dayNames: showXAxis }),
        dragAndZoomPlugin(onBounds, undefined, {
          canStart: options.canStartDrag && ((self, x) => options.canStartDrag!(self, x, "mouse"))
        }),
        touchGesturesPlugin(onBounds, {
          canStart: options.canStartDrag && ((self, x) => options.canStartDrag!(self, x, "touch"))
        }),
        ...(options.hoverLabel === false ? [] : [hoverLabelPlugin(seriesName, isBool)]),
        cursorTimePlugin(isBool),
        axisBadgesPlugin(syncKey != null),
        nowLinePlugin(),
        themePlugin(),
        ...(options.extraPlugins ?? []),
        timing.end
      ]
    },
    emptyAlignedData(xMin, xMax),
    target
  );

  yLockByPlot.set(plot, yState);
  plot.setScale("x", { min: xMin, max: xMax });
  return plot;
}

function hoverLabelPlugin(seriesName: string, isBool: boolean): uPlot.Plugin {
  let label: HTMLDivElement | null = null;

  const setText = (plot: uPlot) => {
    if (!label) {
      return;
    }
    const { left, idx } = plot.cursor;
    if (left == null || left < 0 || idx == null) {
      label.style.display = "none";
      return;
    }

    const raw = plot.data[1]?.[idx];
    let text = seriesName;
    if (raw != null) {
      text += `  |  Value: ${formatLabelValue(raw, isBool)}`;
    }

    label.textContent = text;
    label.style.display = "block";
    const top = Math.max(4, plot.bbox.height / devicePixelRatio - label.clientHeight - 4);
    label.style.left = "0";
    label.style.top = `${top}px`;
  };

  return {
    hooks: {
      init: (plot) => {
        label = document.createElement("div");
        label.className = "chart-hover-label";
        plot.over.appendChild(label);
      },
      setCursor: setText,
      setSize: setText
    }
  };
}

function cursorPoint(plot: uPlot): { x: number; y: number } | null {
  const idx = plot.cursor.idx;
  const top = plot.cursor.top;
  const xs = plot.data[0];
  if (idx == null || top == null || top < 0 || xs == null) {
    return null;
  }
  const x = xs[idx];
  if (x == null) {
    return null;
  }

  const raw = plot.data[1]?.[idx];
  return raw != null ? { x, y: raw } : null;
}

// The height around the mouse that the live label keeps clear of: the tip of the pointer and its arrow below it
const POINTER_ABOVE_PX = 4;
const POINTER_BELOW_PX = 22;

function cursorTimePlugin(isBool: boolean): uPlot.Plugin {
  let label: HTMLDivElement | null = null;
  // The value in bold above the time
  const valueText = document.createElement("span");
  valueText.className = "chart-cursor-value";
  const timeText = document.createElement("span");

  const place = (plot: uPlot) => {
    if (!label) {
      return;
    }
    const point = cursorPoint(plot);
    const min = plot.scales.x.min;
    const max = plot.scales.x.max;
    if (!point || min == null || max == null) {
      label.style.display = "none";
      return;
    }

    const time = formatChartCursorTime(new Date(point.x * 1000), max - min);
    valueText.textContent = formatLabelValue(point.y, isBool);
    timeText.textContent = time;

    const xPx = plot.valToPos(point.x, "x");
    const yPx = plot.valToPos(point.y, "y");
    const hidden = label.style.display !== "block";
    if (hidden) {
      label.classList.add("is-instant");
    }
    // The label is measured while shown; it keeps clear of the badges and the labels of the measuring cursors
    label.style.display = "block";
    const width = label.offsetWidth;
    const height = label.offsetHeight;
    const blocked = obstacleRects(plot, true);
    let rect = placeLabelRect(plot, width, height, xPx, yPx, blocked);
    // When the mouse is at the height of the label, the label goes below the pointer (above it at the bottom), so the
    // line under the mouse stays visible; in a stack only in the plot under the mouse
    const mouseTop = plot.cursor.top;
    if (mouseTop != null && mouseTop >= 0 && !plot.root.matches(".is-synced:not(.is-hovered)")) {
      const band: Rect = {
        left: 0,
        top: mouseTop - POINTER_ABOVE_PX,
        right: plot.bbox.width / devicePixelRatio,
        bottom: mouseTop + POINTER_BELOW_PX
      };
      if (rect.top < band.bottom && rect.bottom > band.top) {
        rect = placeLabelRect(plot, width, height, xPx, band.bottom - LABEL_GAP_Y_PX, [...blocked, band]);
      }
    }
    label.style.transform = `translate(${rect.left}px, ${rect.top}px)`;
    if (hidden) {
      void label.offsetWidth;
      label.classList.remove("is-instant");
    }
  };

  return {
    hooks: {
      init: (plot) => {
        label = document.createElement("div");
        label.className = "chart-cursor-time";
        label.append(valueText, document.createTextNode("\n"), timeText);
        plot.over.appendChild(label);
        plot.over.addEventListener(LABELS_MOVED_EVENT, () => place(plot));
      },
      setCursor: place,
      setScale: place
    }
  };
}

// The size of the arrows of the badges on the axes (style.css)
const AXIS_BADGE_ARROW_PX = 4;
// The share of the plot height at the top and the bottom where the time badge moves to the other edge
const BADGE_FLIP_SHARE = 0.2;

/**
 * The values of the crosshair on the axes: the time above the plot area of the vertical line and the value left of the horizontal
 * one, inside the plot area at the y axis. In a stack, where the plots share the vertical line, only the plot under the mouse shows the
 * horizontal line and the badges.
 */
function axisBadgesPlugin(synced: boolean): uPlot.Plugin {
  const timeBadge = document.createElement("div");
  timeBadge.className = "chart-axis-badge is-x";
  // The time in front, larger than the date
  const timeText = document.createElement("span");
  timeText.className = "chart-axis-badge-time";
  const dateText = document.createElement("span");
  dateText.className = "chart-axis-badge-date";
  timeBadge.append(timeText, dateText);
  const valueBadge = document.createElement("div");
  valueBadge.className = "chart-axis-badge is-y";
  let hovered = !synced;
  // The time badge is down on the time axis
  let below = false;

  const hide = () => {
    timeBadge.style.display = "none";
    valueBadge.style.display = "none";
    below = false;
  };

  const place = (plot: uPlot) => {
    const { left, top } = plot.cursor;
    const xMin = plot.scales.x.min;
    const xMax = plot.scales.x.max;
    if (left == null || left < 0 || xMin == null || xMax == null) {
      hide();
      return;
    }
    const plotWidth = plot.bbox.width / devicePixelRatio;

    if (hovered) {
      const text = formatAxisCursorTime(new Date(plot.posToVal(left, "x") * 1000), xMax - xMin);
      timeText.textContent = text.time;
      dateText.textContent = text.date;
      timeBadge.style.display = "block";
      // Centered on the line, but within the plot area
      const width = timeBadge.offsetWidth;
      const x = Math.min(Math.max(0, left - width / 2), Math.max(0, plotWidth - width));
      // At the top of the plot area; when the mouse comes into the top fifth, it moves down onto the time axis with its
      // arrow pointing up, so the line around the mouse stays visible, and back up when the mouse comes into the
      // bottom fifth
      const height = timeBadge.offsetHeight;
      const plotHeight = plot.bbox.height / devicePixelRatio;
      if (top == null || top < 0) {
        below = false;
      } else if (top < plotHeight * BADGE_FLIP_SHARE) {
        below = true;
      } else if (top > plotHeight * (1 - BADGE_FLIP_SHARE)) {
        below = false;
      }
      timeBadge.classList.toggle("is-below", below);
      const y = below ? plotHeight - height : 0;
      timeBadge.style.transform = `translate(${Math.round(x)}px, ${Math.round(y)}px)`;
      // The arrow stays on the line when the badge is held at an edge
      timeBadge.style.setProperty("--arrow-x", `${Math.round(left - x)}px`);
    } else {
      timeBadge.style.display = "none";
    }

    const yMin = plot.scales.y.min;
    const yMax = plot.scales.y.max;
    if (!hovered || plot.cursor.y === false || top == null || top < 0 || yMin == null || yMax == null) {
      valueBadge.style.display = "none";
      return;
    }
    valueBadge.textContent = formatAxisCursorValue(plot.posToVal(top, "y"), yMin, yMax);
    valueBadge.style.display = "block";
    // Inside the plot area at the y axis, its arrow touching the axis; it stays within the plot area
    const height = valueBadge.offsetHeight;
    const y = Math.min(Math.max(0, top - height / 2), plot.bbox.height / devicePixelRatio - height);
    valueBadge.style.transform = `translate(${AXIS_BADGE_ARROW_PX}px, ${Math.round(y)}px)`;
  };

  return {
    hooks: {
      init: (plot) => {
        plot.over.append(timeBadge, valueBadge);
        if (synced) {
          plot.root.classList.add("is-synced");
          plot.over.addEventListener("pointerenter", () => {
            hovered = true;
            plot.root.classList.add("is-hovered");
          });
          plot.over.addEventListener("pointerleave", () => {
            hovered = false;
            plot.root.classList.remove("is-hovered");
            hide();
          });
        }
      },
      setCursor: place,
      setScale: place
    }
  };
}

function nowLinePlugin(): uPlot.Plugin {
  let timer = 0;
  return {
    hooks: {
      init: (plot) => {
        timer = window.setInterval(() => {
          // Only while the line is in view, plus the one redraw that removes it at the right edge
          const now = Date.now() / 1000;
          const min = plot.scales.x.min;
          const max = plot.scales.x.max;
          if (min != null && max != null && now >= min && now <= max + 1.5) {
            plot.redraw(false, false);
          }
        }, 1000);
      },
      draw: (plot) => {
        const xTime = Date.now() / 1000;
        const min = plot.scales.x.min;
        const max = plot.scales.x.max;
        if (min == null || max == null || xTime < min || xTime > max) {
          return;
        }
        const xPos = plot.valToPos(xTime, "x", true);
        plot.ctx.save();
        plot.ctx.strokeStyle = chartColors().now;
        plot.ctx.lineWidth = 2.5;
        plot.ctx.setLineDash([5, 5]);
        plot.ctx.beginPath();
        plot.ctx.moveTo(xPos, plot.bbox.top);
        plot.ctx.lineTo(xPos, plot.bbox.top + plot.bbox.height);
        plot.ctx.stroke();
        plot.ctx.restore();
      },
      destroy: () => {
        window.clearInterval(timer);
      }
    }
  };
}

/** Redraws the canvas in the colors of the theme when it changes. */
function themePlugin(): uPlot.Plugin {
  let redraw: (() => void) | null = null;
  return {
    hooks: {
      init: (plot) => {
        redraw = () => plot.redraw(false, false);
        window.addEventListener(THEME_CHANGED_EVENT, redraw);
      },
      destroy: () => {
        if (redraw) {
          window.removeEventListener(THEME_CHANGED_EVENT, redraw);
        }
      }
    }
  };
}
