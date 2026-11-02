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

// The offsets of the live cursor label, which the labels of the measuring cursors follow
export const LABEL_GAP_X_PX = 16;
export const LABEL_GAP_Y_PX = 8;
// Free space kept between a label and what it must not cover
export const LABEL_MARGIN_PX = 3;
const LABEL_SCAN_STEPS = 8;

/** Fired on the .u-over when the labels of the measuring cursors have moved, so the live cursor label gives way. */
export const LABELS_MOVED_EVENT = "chart-labels-moved";

/** A box in pixels of the plot area. */
export interface Rect {
  left: number;
  top: number;
  right: number;
  bottom: number;
}

export function overlaps(a: Rect, b: Rect): boolean {
  return a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top;
}

export function grown(rect: Rect, by: number): Rect {
  return { left: rect.left - by, top: rect.top - by, right: rect.right + by, bottom: rect.bottom + by };
}

/** The tops just above and just below each box, nearest to `desired` first: where a label of this height fits tight against them. */
function adjacentTops(blocked: Rect[], height: number, desired: number): number[] {
  const tops: number[] = [];
  for (const rect of blocked) {
    tops.push(rect.bottom, rect.top - height);
  }
  return tops.sort((a, b) => Math.abs(a - desired) - Math.abs(b - desired));
}

function overlapArea(a: Rect, b: Rect): number {
  return Math.max(0, Math.min(a.right, b.right) - Math.max(a.left, b.left)) * Math.max(0, Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top));
}

/** The first candidate that covers nothing; when there is none, the one that covers the least. */
function firstFree(candidates: Rect[], blocked: Rect[]): Rect {
  let best = candidates[0];
  let bestArea = Infinity;
  for (const rect of candidates) {
    const area = blocked.reduce((sum, other) => sum + overlapArea(rect, other), 0);
    if (area === 0) {
      return rect;
    }
    if (area < bestArea) {
      best = rect;
      bestArea = area;
    }
  }
  return best;
}

const TRANSLATE = /translate\(\s*(-?[\d.]+)px,\s*(-?[\d.]+)px\s*\)/;

/**
 * Where a label that slides to its place (a transition on its transform) will rest, since its box on the
 * screen is somewhere in between until the transition ends.
 */
function restingRect(label: HTMLElement): Rect | null {
  const match = TRANSLATE.exec(label.style.transform);
  if (!match || label.style.display !== "block") {
    return null;
  }
  const left = Number(match[1]);
  const top = Number(match[2]);
  return { left, top, right: left + label.offsetWidth, bottom: top + label.offsetHeight };
}

/** The arrow between the cursors of a plot (drawn on the canvas), set by measureCursors.ts; labels keep clear of it. */
export const spanArrowRects = new WeakMap<HTMLElement, Rect>();

/**
 * What a label must not cover: the name and the badge in the plot, the dY badge, the badges of the crosshair and the
 * arrow between the cursors (the badge dX sits on it), and with `valueLabels` the labels Y1 and Y2 of the measuring cursors.
 */
export function obstacleRects(plot: uPlot, valueLabels: boolean): Rect[] {
  const origin = plot.over.getBoundingClientRect();
  const fixed = plot.over.querySelectorAll<HTMLElement>(
    ".chart-stack-info-left > *, .chart-stack-info-right > *, .chart-measure-label.is-dy, .chart-measure-label.is-dx, .chart-axis-badge"
  );
  const rects: Rect[] = [];
  for (const element of fixed) {
    if (element.offsetWidth > 0) {
      const box = element.getBoundingClientRect();
      rects.push({
        left: box.left - origin.left,
        top: box.top - origin.top,
        right: box.right - origin.left,
        bottom: box.bottom - origin.top
      });
    }
  }
  const arrow = spanArrowRects.get(plot.over);
  if (arrow) {
    rects.push(arrow);
  }
  if (valueLabels) {
    for (const element of plot.over.querySelectorAll<HTMLElement>(".chart-measure-label.is-y1, .chart-measure-label.is-y2")) {
      const rect = restingRect(element);
      if (rect) {
        rects.push(rect);
      }
    }
  }
  return rects.map((rect) => grown(rect, LABEL_MARGIN_PX));
}

/**
 * Where a label of the given size goes beside the point (x, y): to the right (with `preferLeft` to the left)
 * as long as it fits within the plot, and below it in the upper half of the plot, above it otherwise. When
 * that covers one of `blocked`, the other sides come next, then steps along the line; the label stays inside
 * the plot area.
 */
export function placeLabelRect(
  plot: uPlot,
  width: number,
  height: number,
  x: number,
  y: number,
  blocked: Rect[],
  gapX = LABEL_GAP_X_PX,
  preferLeft = false
): Rect {
  const plotWidth = plot.bbox.width / devicePixelRatio;
  const plotHeight = plot.bbox.height / devicePixelRatio;
  const fit = (left: number, top: number): Rect => {
    const l = Math.min(Math.max(0, left), Math.max(0, plotWidth - width));
    const t = Math.min(Math.max(0, top), Math.max(0, plotHeight - height));
    return { left: l, top: t, right: l + width, bottom: t + height };
  };
  const lefts =
    (preferLeft ? x - gapX - width < 0 : x + gapX + width <= plotWidth)
      ? [x + gapX, x - gapX - width]
      : [x - gapX - width, x + gapX];
  const tops =
    y < plotHeight / 2 ? [y + LABEL_GAP_Y_PX, y - height - LABEL_GAP_Y_PX] : [y - height - LABEL_GAP_Y_PX, y + LABEL_GAP_Y_PX];

  const candidates: Rect[] = [];
  for (const top of tops) {
    for (const left of lefts) {
      candidates.push(fit(left, top));
    }
  }
  // Tight against the badges and labels in the way, where a gap between them is big enough
  for (const top of adjacentTops(blocked, height, tops[0])) {
    for (const left of lefts) {
      candidates.push(fit(left, top));
    }
  }
  const step = height + LABEL_MARGIN_PX;
  for (let k = 1; k <= LABEL_SCAN_STEPS; k++) {
    for (const sign of [1, -1]) {
      for (const left of lefts) {
        candidates.push(fit(left, tops[0] + sign * k * step));
      }
    }
  }
  return firstFree(candidates, blocked);
}

// Space between a label pinned to an edge and the edge of the plot area
const EDGE_GAP_PX = 2;

/**
 * Where a label of the given size goes when it is pinned to the left or right edge of the plot area, at the
 * height of y; it moves along the edge when that covers one of `blocked`.
 */
export function placeLabelAtEdge(
  plot: uPlot,
  width: number,
  height: number,
  side: "left" | "right",
  y: number,
  blocked: Rect[]
): Rect {
  const plotWidth = plot.bbox.width / devicePixelRatio;
  const plotHeight = plot.bbox.height / devicePixelRatio;
  const left = side === "left" ? EDGE_GAP_PX : Math.max(0, plotWidth - width - EDGE_GAP_PX);
  const fit = (top: number): Rect => {
    const t = Math.min(Math.max(0, top), Math.max(0, plotHeight - height));
    return { left, top: t, right: left + width, bottom: t + height };
  };
  const candidates = [fit(y - height / 2)];
  // Tight against the badges and labels in the way, where a gap between them is big enough
  for (const top of adjacentTops(blocked, height, y - height / 2)) {
    candidates.push(fit(top));
  }
  const step = height + LABEL_MARGIN_PX;
  for (let k = 1; k <= LABEL_SCAN_STEPS; k++) {
    candidates.push(fit(y - height / 2 + k * step), fit(y - height / 2 - k * step));
  }
  return firstFree(candidates, blocked);
}
