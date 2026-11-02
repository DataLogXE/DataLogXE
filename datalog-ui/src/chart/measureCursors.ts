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
import { formatChartCursorTime, formatLabelValue, formatSpan, formatSpanLines } from "../format";
import type { PlotPoints } from "../types";
import { PLOT_PAD_TOP } from "./bounds";
import { BOOL_Y_RANGE } from "./createPlot";
import {
  grown,
  LABEL_MARGIN_PX,
  LABELS_MOVED_EVENT,
  obstacleRects,
  placeLabelAtEdge,
  placeLabelRect,
  spanArrowRects,
  type Rect
} from "./labelPlacement";
import { valueAtTime } from "./plotData";
import { chartColors } from "../theme";

export const MOUSE_HIT_PX = 3;
export const TOUCH_HIT_PX = 12;
// The labels of the cursors sit closer to their line than the live cursor label to its point
const MEASURE_LABEL_GAP_X_PX = 5;
// The arrow between the cursors lies this far below the upper edge of the plot area, its heads are this long
const SPAN_ARROW_TOP_PX = 12;
const SPAN_ARROW_HEAD_PX = 6.5;
// In a bool plot the arrow lies this far above the line at 1, but with its heads inside of the space above the plot area
const SPAN_BOOL_LIFT_PX = 6;
// The badge stays between the cursors when they are at least 1.2 times its width apart, else it moves beside them
const SPAN_BADGE_ROOM = 1.2;
const SPAN_BADGE_GAP_PX = 6;
const VALUE_READ_DELAY_MS = 150;
// An answer for a time this close to now may still change with the values that are stored later (a source that stops
// delivering, a lost signal that is marked when it returns), so it is read again after a few seconds
const NEAR_NOW_SEC = 120;
const NEAR_NOW_REFRESH_MS = 5000;
// The tooltip of the badge dX opens this long after the mouse came to it and closes this long after the mouse left it
export const TIP_OPEN_DELAY_MS = 1000;
export const TIP_CLOSE_DELAY_MS = 1000;
// A click on the badge dX shows both cursors with this share of the range free on each side
const SPAN_FIT_MARGIN = 0.05;
// Ridgway: Eugenia red (15) and Vanderpoel's blue (182), see people.csail.mit.edu/jaffer/Color/R.htm
// Width of the lines of the cursors and of the arrow between them
const LINE_WIDTH_PX = 2;
// The arrow of dX has the color of its badge (--measure-dx), Ridgway: Cossack green (125); the colors come with the
// theme (chartColors)
const CURSOR_VARS = ["var(--measure-x1)", "var(--measure-x2)"];
// The dot where a cursor meets the line, and the least distance of its label from which a line leads to it
const SVG_NAMESPACE = "http://www.w3.org/2000/svg";
const CONNECTOR_DOT_RADIUS_PX = 3.5;
const CONNECTOR_LINE_WIDTH_PX = 1.5;
const CONNECTOR_MIN_PX = 4;

/**
 * How far below the upper edge of the plot area the arrow with the badge dX lies. A number plot has it at a fixed
 * place; a bool plot has the line at 1 close to the upper edge, so the arrow goes a little above that line instead of
 * onto it.
 */
export function spanArrowTop(isBool: boolean, areaHeightPx: number): number {
  if (!isBool) {
    return SPAN_ARROW_TOP_PX;
  }
  const [low, high] = BOOL_Y_RANGE;
  const lineAtOne = (areaHeightPx * (high - 1)) / (high - low);
  return Math.max(lineAtOne - SPAN_BOOL_LIFT_PX, SPAN_ARROW_HEAD_PX - PLOT_PAD_TOP);
}

/** The measuring cursors X1 and X2, shared by all plots of a chart. */
export interface MeasureState {
  /** Times of X1 and X2 in unix seconds. */
  x: [number | null, number | null];
  /** The plot under the mouse, for the keys 1 and 2. */
  hover: uPlot | null;
  /** The cursor being dragged. */
  drag: 0 | 1 | null;
}

// Set on the body while a cursor is dragged; the style sheet hides the live cursor labels then
const DRAGGING_CLASS = "is-measure-dragging";

export function createMeasureState(): MeasureState {
  return { x: [null, null], hover: null, drag: null };
}

/** The cursor within the tolerance of the pointer, the nearer one if both are. */
export function measureHitTest(state: MeasureState, plot: uPlot, clientX: number, tolerancePx: number): 0 | 1 | null {
  const rect = plot.over.getBoundingClientRect();
  const left = clientX - rect.left;
  let hit: 0 | 1 | null = null;
  let best = tolerancePx;
  for (const index of [0, 1] as const) {
    const time = state.x[index];
    if (time == null) {
      continue;
    }
    const distance = Math.abs(plot.valToPos(time, "x") - left);
    if (distance <= best) {
      best = distance;
      hit = index;
    }
  }
  return hit;
}

/** The value of a cursor for its label: "…" while it is read, "–" without a value, else the value. */
export function formatMeasureValue(value: number | null, isBool: boolean, pending: boolean): string {
  if (pending) {
    return "…";
  }
  if (value == null) {
    return "–";
  }
  return formatLabelValue(value, isBool);
}

/**
 * Draws the cursors X1 and X2 as red lines with the value of the plot at each (Y1, Y2) and their
 * difference (dY). Lines are dragged with the mouse or a finger.
 */
export function measureCursorsPlugin(
  state: MeasureState,
  options: {
    isBool: boolean;
    /** Whether this plot draws the arrow with dX between the cursors when both are in view. */
    showSpan: () => boolean;
    /** The points of the plot: where the label of a cursor stands at the height of the line there. */
    getData: () => PlotPoints | null;
    /** The raw value of the data tag at the time, read from the database; null when there is none (no row, a lost signal). */
    getValueAt: (time: number, signal: AbortSignal) => Promise<number | null>;
    /** A cursor moved: redraw every plot and update the toolbar. */
    onMove: () => void;
    /** The label of a cursor outside of the view was clicked: bring the time into view. */
    onReveal: (time: number) => void;
    /** The badge dX was clicked: show this range, with both cursors in it. */
    onFitSpan: (min: number, max: number) => void;
  }
): uPlot.Plugin {
  const labels: HTMLDivElement[] = [];
  let cleanup: (() => void) | null = null;

  const setDrag = (value: 0 | 1 | null) => {
    state.drag = value;
    document.body.classList.toggle(DRAGGING_CLASS, value != null);
  };

  // Where the value labels belong in the plot area, set by every draw
  // `side` is set when the cursor lies outside of the view; its label then sits at that edge; `onLine` is set when the
  // cursor meets the drawn line, at (x, y)
  const anchors: (
    | { x: number; y: number; value: string; time: string; side: "left" | "right" | null; onLine: boolean }
    | null
  )[] = [null, null];
  // The value above the time in the labels Y1 and Y2, like the live cursor label
  const valueTexts: HTMLSpanElement[] = [];
  const timeTexts: HTMLSpanElement[] = [];
  // The raw value of each cursor at its time, read from the database and not taken from the points of the plot
  // `local` is the answer for a time in the future, which is not asked for: nothing is known about it; `at` is when it was
  // asked for
  const fetched: ({ time: number; done: boolean; value: number | null; at: number; local: boolean } | null)[] = [null, null];
  const requests: (AbortController | null)[] = [null, null];
  const requestTimers = [0, 0];
  // The height of each cursor in the plot (value of the drawn line), kept while the points do not cover its time
  const levels: ({ time: number; level: number | null } | null)[] = [null, null];
  let layoutRaf = 0;

  const requestValue = (index: 0 | 1, time: number, plot: uPlot) => {
    const known = fetched[index];
    const nowSec = Date.now() / 1000;
    if (time > nowSec) {
      // Nothing is known about the future: no value, and no read. Once the time has come, the value is read
      if (!(known?.time === time && known.local)) {
        requests[index]?.abort();
        window.clearTimeout(requestTimers[index]);
        fetched[index] = { time, done: true, value: null, at: Date.now(), local: true };
      }
      return;
    }
    const current = known?.time === time && !known.local ? known : null;
    if (current && !(current.done && time > nowSec - NEAR_NOW_SEC && Date.now() - current.at >= NEAR_NOW_REFRESH_MS)) {
      return;
    }
    // A value that is read again stays shown until the new answer is there
    fetched[index] = current ? { ...current, at: Date.now() } : { time, done: false, value: null, at: Date.now(), local: false };
    requests[index]?.abort();
    window.clearTimeout(requestTimers[index]);
    const controller = new AbortController();
    requests[index] = controller;
    // A cursor that is dragged changes its time all the time, so the read waits until it rests
    requestTimers[index] = window.setTimeout(async () => {
      let value: number | null = null;
      try {
        value = await options.getValueAt(time, controller.signal);
      } catch {
        // No value is shown when the read fails
      }
      if (controller.signal.aborted) {
        return;
      }
      fetched[index] = { time, done: true, value, at: Date.now(), local: false };
      plot.redraw(false, false);
    }, VALUE_READ_DELAY_MS);
  };

  // A dot where a cursor meets the line of the plot, and a thin line from it to the label of the cursor. The label is a
  // rounded rectangle; the line ends at its nearest point, and is left out when the label is at the dot.
  const connectors: { group: SVGGElement; line: SVGLineElement; dot: SVGCircleElement }[] = [];
  let labelRadius = 0;

  const placeConnector = (index: 0 | 1, anchor: (typeof anchors)[number], rect: Rect | null, label: HTMLElement) => {
    const connector = connectors[index];
    if (!connector) {
      return;
    }
    if (!anchor?.onLine) {
      connector.group.style.display = "none";
      return;
    }
    connector.group.style.display = "";
    connector.dot.setAttribute("cx", String(anchor.x));
    connector.dot.setAttribute("cy", String(anchor.y));
    let target: { x: number; y: number } | null = null;
    if (rect) {
      labelRadius ||= parseFloat(getComputedStyle(label).borderTopLeftRadius) || 0;
      const radius = Math.min(labelRadius, (rect.right - rect.left) / 2, (rect.bottom - rect.top) / 2);
      const innerX = Math.min(Math.max(anchor.x, rect.left + radius), rect.right - radius);
      const innerY = Math.min(Math.max(anchor.y, rect.top + radius), rect.bottom - radius);
      const dx = anchor.x - innerX;
      const dy = anchor.y - innerY;
      const distance = Math.hypot(dx, dy);
      if (distance - radius >= CONNECTOR_MIN_PX) {
        target = { x: innerX + (dx / distance) * radius, y: innerY + (dy / distance) * radius };
      }
    }
    connector.line.style.display = target ? "" : "none";
    if (target) {
      connector.line.setAttribute("x1", String(anchor.x));
      connector.line.setAttribute("y1", String(anchor.y));
      connector.line.setAttribute("x2", String(target.x));
      connector.line.setAttribute("y2", String(target.y));
    }
  };

  // Where a label is on the screen right now, in pixels of the plot area; a label that slides to its place is on the way
  const renderedRect = (plot: uPlot, label: HTMLElement): Rect => {
    const box = label.getBoundingClientRect();
    const origin = plot.over.getBoundingClientRect();
    return { left: box.left - origin.left, top: box.top - origin.top, right: box.right - origin.left, bottom: box.bottom - origin.top };
  };

  // The lines to the labels follow them while they slide: every frame until no label moves anymore. A smooth pan moves
  // the labels all the time, so the line keeps up with the label and does not run to a place the label has not reached
  let followRaf = 0;
  const followLabels = (plot: uPlot) => {
    if (followRaf || !labels.some((label, index) => index < 2 && label.getAnimations().length > 0)) {
      return;
    }
    const step = () => {
      followRaf = 0;
      let sliding = false;
      for (const index of [0, 1] as const) {
        const label = labels[index];
        if (label && anchors[index] && label.style.display === "block") {
          placeConnector(index, anchors[index], renderedRect(plot, label), label);
          sliding ||= label.getAnimations().length > 0;
        }
      }
      if (sliding) {
        followRaf = window.requestAnimationFrame(step);
      }
    };
    followRaf = window.requestAnimationFrame(step);
  };

  const layout = (plot: uPlot) => {
    const blocked = obstacleRects(plot, false);
    for (const index of [0, 1] as const) {
      const label = labels[index];
      const anchor = anchors[index];
      if (!label) {
        continue;
      }
      if (!anchor) {
        label.style.display = "none";
        placeConnector(index, null, null, label);
        continue;
      }
      const hidden = label.style.display !== "block";
      valueTexts[index].textContent = anchor.value;
      timeTexts[index].textContent = anchor.time;
      // Measured while shown, without sliding in from the corner
      if (hidden) {
        label.classList.add("is-instant");
      }
      label.style.display = "block";
      label.classList.toggle("is-offscreen-left", anchor.side === "left");
      label.classList.toggle("is-offscreen-right", anchor.side === "right");
      if (anchor.side) {
        label.dataset.hint = `Go to X${index + 1}`;
      } else {
        delete label.dataset.hint;
      }
      const rect = anchor.side
        ? placeLabelAtEdge(plot, label.offsetWidth, label.offsetHeight, anchor.side, anchor.y, blocked)
        : placeLabelRect(plot, label.offsetWidth, label.offsetHeight, anchor.x, anchor.y, blocked, MEASURE_LABEL_GAP_X_PX, index === 0);
      label.style.transform = `translate(${rect.left}px, ${rect.top}px)`;
      blocked.push(grown(rect, LABEL_MARGIN_PX));
      if (hidden) {
        void label.offsetWidth;
        label.classList.remove("is-instant");
      }
      // The line ends where the label is just now, which is not its place yet while it slides there
      placeConnector(index, anchor, renderedRect(plot, label), label);
    }
    plot.over.dispatchEvent(new Event(LABELS_MOVED_EVENT));
    followLabels(plot);
  };

  // The area between X1 and X2 is tinted a little, and a little more while the mouse is over it in this plot. The
  // mouse position is in pixels of the plot area (null outside of it), the band is given in the same pixels (null
  // without one).
  let band: HTMLDivElement | null = null;
  let mouseLeft: number | null = null;
  let bandPx: [number, number] | null = null;

  const updateBandHover = () => {
    band?.classList.toggle("is-hover", mouseLeft != null && bandPx != null && mouseLeft >= bandPx[0] && mouseLeft <= bandPx[1]);
  };

  // The band runs to the edge for a cursor outside of the view and is not shown when both are outside on one side
  const drawBand = (plot: uPlot) => {
    const min = plot.scales.x.min;
    const max = plot.scales.x.max;
    const [first, second] = state.x;
    bandPx = null;
    if (first != null && second != null && min != null && max != null) {
      const low = Math.min(first, second);
      const high = Math.max(first, second);
      if (low < high && high >= min && low <= max) {
        bandPx = [low < min ? 0 : plot.valToPos(low, "x"), high > max ? plot.bbox.width / devicePixelRatio : plot.valToPos(high, "x")];
      }
    }
    if (band) {
      band.style.display = bandPx ? "block" : "none";
      if (bandPx) {
        band.style.left = `${bandPx[0]}px`;
        band.style.width = `${bandPx[1] - bandPx[0]}px`;
      }
    }
    updateBandHover();
  };

  // The badge dX holds its text and a tooltip, which stays open while the mouse is over it and can be selected
  const spanText = document.createElement("span");
  const spanTip = document.createElement("div");
  spanTip.className = "chart-measure-tip";
  let tipTimer = 0;
  let openTimer = 0;

  // Whether the selection reaches into the tooltip: asked by the ranges themselves, since anchor and focus of a
  // selection are moved out of the tooltip as soon as it is hidden
  const isTipSelected = () => {
    const selection = window.getSelection();
    if (!selection) {
      return false;
    }
    for (let i = 0; i < selection.rangeCount; i++) {
      if (selection.getRangeAt(i).intersectsNode(spanTip)) {
        return true;
      }
    }
    return false;
  };

  // Closes the tooltip and ends a selection in it, however the selection was made
  const closeTip = () => {
    window.clearTimeout(tipTimer);
    window.clearTimeout(openTimer);
    // Asked before the tooltip is hidden, which changes what the selection reports
    const selected = isTipSelected();
    const badge = spanTip.parentElement;
    if (!selected && !badge?.classList.contains("is-open")) {
      return;
    }
    badge?.classList.remove("is-open");
    if (selected) {
      window.getSelection()?.removeAllRanges();
    }
  };

  // The arrow between X1 and X2 at the upper edge of the plot with the badge dX, which is also drawn when a cursor
  // is outside of the view: the arrow then runs to the edge and has no head on that end. Both cursors outside on
  // the same side leave the badge only, at that edge; on different sides the arrow spans the plot without heads.
  const drawSpan = (plot: uPlot) => {
    const badge = labels[3];
    const min = plot.scales.x.min;
    const max = plot.scales.x.max;
    const [first, second] = state.x;
    if (!badge || !options.showSpan() || first == null || second == null || min == null || max == null) {
      spanArrowRects.delete(plot.over);
      if (badge) {
        badge.style.display = "none";
        // A hidden badge takes its tooltip with it, which does not report the mouse leaving
        closeTip();
      }
      return;
    }

    const low = Math.min(first, second);
    const high = Math.max(first, second);
    const lowSide = low < min ? "left" : low > max ? "right" : null;
    const highSide = high < min ? "left" : high > max ? "right" : null;
    const sameSide = lowSide != null && lowSide === highSide;
    const plotWidth = plot.bbox.width / devicePixelRatio;
    // Pixels of the plot area; a cursor outside of the view lies at the edge on its side
    const edgeOrPos = (time: number, side: "left" | "right" | null) =>
      side === "left" ? 0 : side === "right" ? plotWidth : plot.valToPos(time, "x");
    const leftPx = edgeOrPos(low, lowSide);
    const rightPx = edgeOrPos(high, highSide);
    const arrowTop = spanArrowTop(options.isBool, plot.bbox.height / devicePixelRatio);

    if (sameSide) {
      spanArrowRects.delete(plot.over);
    } else {
      // The other labels keep clear of the arrow
      spanArrowRects.set(plot.over, {
        left: leftPx,
        right: rightPx,
        top: arrowTop - SPAN_ARROW_HEAD_PX,
        bottom: arrowTop + SPAN_ARROW_HEAD_PX
      });
      const y = plot.bbox.top + arrowTop * devicePixelRatio;
      const head = SPAN_ARROW_HEAD_PX * devicePixelRatio;
      const from = plot.bbox.left + leftPx * devicePixelRatio;
      const to = plot.bbox.left + rightPx * devicePixelRatio;
      const ctx = plot.ctx;
      ctx.save();
      ctx.strokeStyle = chartColors().span;
      ctx.fillStyle = chartColors().span;
      ctx.lineWidth = LINE_WIDTH_PX * devicePixelRatio;
      ctx.beginPath();
      ctx.moveTo(from, y);
      ctx.lineTo(to, y);
      ctx.stroke();
      // The heads need room, so cursors that are very close to each other get the line only; a cursor outside of
      // the view gets no head on its end
      if (to - from > 3 * head) {
        ctx.beginPath();
        if (lowSide == null) {
          ctx.moveTo(from, y);
          ctx.lineTo(from + head * 1.6, y - head);
          ctx.lineTo(from + head * 1.6, y + head);
          ctx.closePath();
        }
        if (highSide == null) {
          ctx.moveTo(to, y);
          ctx.lineTo(to - head * 1.6, y - head);
          ctx.lineTo(to - head * 1.6, y + head);
          ctx.closePath();
        }
        ctx.fill();
      }
      ctx.restore();
    }

    const span = second - first;
    // Texts are only replaced when they change, or a selection in the tooltip would be lost with every redraw
    const text = `ΔT: ${formatSpan(span)}`;
    if (spanText.textContent !== text) {
      spanText.textContent = text;
    }
    // The tooltip shows the span in seconds, minutes, hours and days, one below the other
    const tip = formatSpanLines(span).join("\n");
    if (spanTip.textContent !== tip) {
      spanTip.textContent = tip;
    }
    badge.style.display = "block";
    const width = badge.offsetWidth;
    let x = (leftPx + rightPx) / 2 - width / 2;
    if (sameSide) {
      x = lowSide === "left" ? SPAN_BADGE_GAP_PX : plotWidth - width - SPAN_BADGE_GAP_PX;
    } else if (rightPx - leftPx < width * SPAN_BADGE_ROOM) {
      // Too narrow for the badge between the cursors: beside them, on the side with room for it
      x = rightPx + SPAN_BADGE_GAP_PX + width <= plotWidth ? rightPx + SPAN_BADGE_GAP_PX : leftPx - SPAN_BADGE_GAP_PX - width;
    }
    x = Math.min(Math.max(0, x), Math.max(0, plotWidth - width));
    badge.style.transform = `translate(${x}px, ${arrowTop - badge.offsetHeight / 2}px)`;
  };

  // The badges of the plot change their text after a draw, so place once more when the page has updated
  const scheduleLayout = (plot: uPlot) => {
    if (layoutRaf) {
      return;
    }
    layoutRaf = window.requestAnimationFrame(() => {
      layoutRaf = 0;
      layout(plot);
    });
  };

  return {
    hooks: {
      init: (plot) => {
        // Before the labels, which lie above it
        band = document.createElement("div");
        band.className = "chart-measure-band";
        band.style.display = "none";
        plot.over.appendChild(band);
        const svg = document.createElementNS(SVG_NAMESPACE, "svg");
        svg.setAttribute("class", "chart-measure-connectors");
        for (const index of [0, 1] as const) {
          const group = document.createElementNS(SVG_NAMESPACE, "g");
          group.style.display = "none";
          group.style.stroke = CURSOR_VARS[index];
          const line = document.createElementNS(SVG_NAMESPACE, "line");
          line.setAttribute("stroke-width", String(CONNECTOR_LINE_WIDTH_PX));
          const dot = document.createElementNS(SVG_NAMESPACE, "circle");
          dot.setAttribute("r", String(CONNECTOR_DOT_RADIUS_PX));
          dot.style.fill = CURSOR_VARS[index];
          // A ring in the color of the background keeps the dot visible on the line of a plot with many points
          dot.style.stroke = "var(--surface)";
          dot.setAttribute("stroke-width", "1");
          group.append(line, dot);
          svg.appendChild(group);
          connectors[index] = { group, line, dot };
        }
        plot.over.appendChild(svg);
        for (const kind of ["y1", "y2", "dy", "dx"]) {
          const label = document.createElement("div");
          label.className = `chart-measure-label is-${kind}`;
          if (kind === "y1" || kind === "y2") {
            const valueText = document.createElement("span");
            valueText.className = "chart-measure-label-value";
            const timeText = document.createElement("span");
            label.append(valueText, document.createTextNode("\n"), timeText);
            valueTexts.push(valueText);
            timeTexts.push(timeText);
            // At the edge the label brings its cursor into view; the plot below must not take it for a drag
            const index = valueTexts.length - 1;
            for (const type of ["mousedown", "pointerdown", "touchstart", "dblclick"]) {
              label.addEventListener(type, (event) => event.stopPropagation());
            }
            label.addEventListener("click", () => {
              const time = state.x[index];
              if (time != null && (label.classList.contains("is-offscreen-left") || label.classList.contains("is-offscreen-right"))) {
                options.onReveal(time);
              }
            });
          }
          if (kind === "dx") {
            label.append(spanText, spanTip);
            // The text is selected with the mouse: the plot below must not take it for a drag
            for (const type of ["mousedown", "pointerdown", "touchstart", "dblclick"]) {
              label.addEventListener(type, (event) => event.stopPropagation());
            }
            // The tooltip opens with the mouse over the badge and closes 1 s after the mouse left the badge, so
            // the mouse can move to the tooltip; it stays open while the mouse is over it and closes at once
            // when the mouse leaves it, together with its selection
            label.addEventListener("mouseenter", () => {
              window.clearTimeout(tipTimer);
              if (!label.classList.contains("is-open")) {
                window.clearTimeout(openTimer);
                openTimer = window.setTimeout(() => label.classList.add("is-open"), TIP_OPEN_DELAY_MS);
              }
            });
            label.addEventListener("mouseleave", () => {
              window.clearTimeout(tipTimer);
              window.clearTimeout(openTimer);
              if (label.classList.contains("is-open")) {
                tipTimer = window.setTimeout(closeTip, TIP_CLOSE_DELAY_MS);
              }
            });
            // A click on the badge shows the span between the cursors, with 5 % of room on both sides
            spanText.addEventListener("click", () => {
              const [first, second] = state.x;
              if (first == null || second == null || first === second) {
                return;
              }
              const span = Math.abs(second - first);
              const room = (span / (1 - 2 * SPAN_FIT_MARGIN)) * SPAN_FIT_MARGIN;
              options.onFitSpan(Math.min(first, second) - room, Math.max(first, second) + room);
            });
            spanTip.addEventListener("mouseenter", () => window.clearTimeout(tipTimer));
            spanTip.addEventListener("mouseleave", (event) => {
              if ((event.buttons & 1) === 0) {
                closeTip();
                return;
              }
              // The text is being selected by dragging: the tooltip closes when the button is released
              const onUp = () => {
                document.removeEventListener("mouseup", onUp);
                if (!spanTip.matches(":hover")) {
                  closeTip();
                }
              };
              document.addEventListener("mouseup", onUp);
            });
          }
          plot.over.appendChild(label);
          labels.push(label);
        }
      },
      ready: (plot) => {
        const over = plot.over;

        const moveTo = (index: 0 | 1, clientX: number) => {
          const rect = over.getBoundingClientRect();
          const left = Math.min(rect.width, Math.max(0, clientX - rect.left));
          state.x[index] = plot.posToVal(left, "x");
          options.onMove();
        };

        const onEnter = () => {
          state.hover = plot;
        };

        const onLeave = () => {
          mouseLeft = null;
          updateBandHover();
          if (state.hover === plot) {
            state.hover = null;
          }
          if (state.drag == null) {
            over.style.cursor = "";
          }
        };

        const onHover = (event: MouseEvent) => {
          mouseLeft = event.clientX - over.getBoundingClientRect().left;
          updateBandHover();
          if (state.drag == null) {
            over.style.cursor = measureHitTest(state, plot, event.clientX, MOUSE_HIT_PX) != null ? "ew-resize" : "";
          }
        };

        const onMouseDown = (event: MouseEvent) => {
          if (event.button !== 0) {
            return;
          }
          const hit = measureHitTest(state, plot, event.clientX, MOUSE_HIT_PX);
          if (hit == null) {
            return;
          }
          event.preventDefault();
          setDrag(hit);
          let raf = 0;
          let clientX = event.clientX;
          const onMove = (moveEvent: MouseEvent) => {
            moveEvent.preventDefault();
            clientX = moveEvent.clientX;
            if (!raf) {
              raf = window.requestAnimationFrame(() => {
                raf = 0;
                moveTo(hit, clientX);
              });
            }
          };
          const onUp = () => {
            document.removeEventListener("mousemove", onMove);
            document.removeEventListener("mouseup", onUp);
            if (raf) {
              window.cancelAnimationFrame(raf);
              raf = 0;
              moveTo(hit, clientX);
            }
            setDrag(null);
          };
          document.addEventListener("mousemove", onMove);
          document.addEventListener("mouseup", onUp);
        };

        let touchId: number | null = null;
        let touchCursor: 0 | 1 | null = null;
        let touchRaf = 0;
        let touchX = 0;

        const onPointerDown = (event: PointerEvent) => {
          if (event.pointerType !== "touch" || touchId != null) {
            return;
          }
          const hit = measureHitTest(state, plot, event.clientX, TOUCH_HIT_PX);
          if (hit == null) {
            return;
          }
          touchId = event.pointerId;
          touchCursor = hit;
          touchX = event.clientX;
          setDrag(hit);
          try {
            over.setPointerCapture(event.pointerId);
          } catch {
            // The pointer may already be gone
          }
        };

        const onPointerMove = (event: PointerEvent) => {
          if (event.pointerId !== touchId || touchCursor == null) {
            return;
          }
          touchX = event.clientX;
          const cursor = touchCursor;
          if (!touchRaf) {
            touchRaf = window.requestAnimationFrame(() => {
              touchRaf = 0;
              moveTo(cursor, touchX);
            });
          }
        };

        const onPointerUp = (event: PointerEvent) => {
          if (event.pointerId !== touchId) {
            return;
          }
          touchId = null;
          touchCursor = null;
          setDrag(null);
        };

        over.addEventListener("mouseenter", onEnter);
        over.addEventListener("mouseleave", onLeave);
        over.addEventListener("mousemove", onHover);
        over.addEventListener("mousedown", onMouseDown);
        over.addEventListener("pointerdown", onPointerDown);
        over.addEventListener("pointermove", onPointerMove);
        over.addEventListener("pointerup", onPointerUp);
        over.addEventListener("pointercancel", onPointerUp);

        cleanup = () => {
          if (touchRaf) {
            window.cancelAnimationFrame(touchRaf);
          }
          if (state.hover === plot) {
            state.hover = null;
          }
          over.removeEventListener("mouseenter", onEnter);
          over.removeEventListener("mouseleave", onLeave);
          over.removeEventListener("mousemove", onHover);
          over.removeEventListener("mousedown", onMouseDown);
          over.removeEventListener("pointerdown", onPointerDown);
          over.removeEventListener("pointermove", onPointerMove);
          over.removeEventListener("pointerup", onPointerUp);
          over.removeEventListener("pointercancel", onPointerUp);
        };
      },
      draw: (plot) => {
        const min = plot.scales.x.min;
        const max = plot.scales.x.max;
        const values: (number | null)[] = [null, null];
        const pending = [false, false];
        const ctx = plot.ctx;
        drawBand(plot);

        for (const index of [0, 1] as const) {
          const time = state.x[index];
          if (time == null) {
            // A removed cursor needs no value: its read is cancelled, also in the database
            requests[index]?.abort();
            requests[index] = null;
            window.clearTimeout(requestTimers[index]);
            fetched[index] = null;
          }
          if (time == null || min == null || max == null) {
            anchors[index] = null;
            continue;
          }
          const inView = time >= min && time <= max;
          if (inView) {
            const xCanvas = plot.valToPos(time, "x", true);
            ctx.save();
            ctx.strokeStyle = chartColors().cursors[index];
            ctx.lineWidth = LINE_WIDTH_PX * devicePixelRatio;
            ctx.beginPath();
            ctx.moveTo(xCanvas, plot.bbox.top);
            ctx.lineTo(xCanvas, plot.bbox.top + plot.bbox.height);
            ctx.stroke();
            ctx.restore();
          }

          requestValue(index, time, plot);
          const read = fetched[index];
          pending[index] = !read?.done;
          values[index] = read?.done ? read.value : null;
          // The label text is the value of the database; its place is the height of the drawn line, and the
          // top without a line there
          const drawn = valueAtTime(options.getData(), time);
          if (drawn != null || inView) {
            levels[index] = { time, level: drawn };
          }
          let level = levels[index]?.time === time ? levels[index].level : null;
          // Where the database has no value (a lost signal, the future) the line is not there either: no dot, and the
          // label stands at the top
          if (read?.done && values[index] == null) {
            level = null;
          }
          const y = level == null ? 0 : plot.valToPos(level, "y");
          // A value above or below the range of the y axis has no dot and no line to its label: they would lie outside
          // of the plot area
          const inRange = y >= 0 && y <= plot.bbox.height / devicePixelRatio;
          anchors[index] = {
            x: plot.valToPos(time, "x"),
            y,
            value: `Y${index + 1}: ${formatMeasureValue(values[index], options.isBool, pending[index])}`,
            time: formatChartCursorTime(new Date(time * 1000), max - min),
            side: inView ? null : time < min ? "left" : "right",
            onLine: inView && level != null && inRange
          };
        }

        const deltaLabel = labels[2];
        if (deltaLabel) {
          const [first, second] = values;
          if (options.isBool || first == null || second == null) {
            deltaLabel.style.display = "none";
          } else {
            // The difference of the two values read from the database, not of points of the plot
            deltaLabel.textContent = `ΔY: ${formatLabelValue(second - first)}`;
            // The style sheet puts it at the bottom right, above the badge of the plot
            deltaLabel.style.display = "block";
          }
        }

        drawSpan(plot);
        layout(plot);
        scheduleLayout(plot);
      },
      destroy: () => {
        closeTip();
        for (const index of [0, 1]) {
          requests[index]?.abort();
          window.clearTimeout(requestTimers[index]);
        }
        if (layoutRaf) {
          window.cancelAnimationFrame(layoutRaf);
          layoutRaf = 0;
        }
        if (followRaf) {
          window.cancelAnimationFrame(followRaf);
          followRaf = 0;
        }
        cleanup?.();
        cleanup = null;
        if (state.drag == null) {
          document.body.classList.remove(DRAGGING_CLASS);
        }
      }
    }
  };
}
