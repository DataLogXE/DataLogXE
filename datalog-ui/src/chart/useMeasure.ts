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

import { ref } from "vue";
import type uPlot from "uplot";
import { readMeasureTimes, writeMeasureTimes } from "./chartStorage";
import { createMeasureState, measureHitTest, MOUSE_HIT_PX, TOUCH_HIT_PX } from "./measureCursors";
import type { RangeController } from "./rangeController";
import { isChartShortcut } from "./shortcutKeys";

const ARROW_KEYS: Record<string, { shift?: -1 | 1; zoom?: -1 | 1 }> = {
  ArrowLeft: { shift: -1 },
  ArrowRight: { shift: 1 },
  ArrowUp: { zoom: -1 },
  ArrowDown: { zoom: 1 }
};

/**
 * The measuring cursors X1 and X2 of a chart: switching them on and off with the toolbar buttons and the keys 1, 2 and X,
 * where they come back, and their share in the dragging of the plots; the arrow keys move and zoom the time range. The times are kept in the browser, so every chart
 * shows the cursors where the last one left them. `redraw` draws the plots again.
 * `inModal` is for a chart that is a dialog itself, where the keys must work although a dialog is open.
 */
export function useMeasure(range: RangeController, redraw: () => void, options: { inModal?: boolean } = {}) {
  const measure = createMeasureState();
  const saved = readMeasureTimes();
  measure.x = [saved[0], saved[1]];
  const measureTimes = ref<[number | null, number | null]>([saved[0], saved[1]]);
  // Where each cursor stood when it was switched off, so it comes back to the same place
  const lastCursorTime: [number | null, number | null] = [null, null];

  function onMeasureMove() {
    measureTimes.value = [measure.x[0], measure.x[1]];
    writeMeasureTimes(measureTimes.value);
    redraw();
  }

  function removeCursor(index: 0 | 1) {
    if (measure.x[index] != null) {
      lastCursorTime[index] = measure.x[index];
      measure.x[index] = null;
    }
  }

  // X1 in the middle of the left third and X2 in the middle of the right third of the view; when the other cursor is
  // in view, the new one comes in the middle between it and the end of the chart on its side
  function defaultCursorTime(index: 0 | 1): number {
    const { min, max } = range;
    const other = measure.x[1 - index];
    if (other != null && other >= min && other <= max) {
      return index === 0 ? (min + other) / 2 : (other + max) / 2;
    }
    return index === 0 ? min + (max - min) / 6 : min + ((max - min) * 5) / 6;
  }

  // The old place when it is still in view, even where X2 lies left of X1 by now; else the default one, which keeps
  // X1 left of X2
  function cursorTimeToSet(index: 0 | 1): number {
    const remembered = lastCursorTime[index];
    const inView = remembered != null && remembered >= range.min && remembered <= range.max;
    return inView ? remembered : defaultCursorTime(index);
  }

  // A cursor that is switched on comes at `time` when it is given, else back to its old place or to the default one
  function toggleCursor(index: 0 | 1, time?: number) {
    if (measure.x[index] != null) {
      removeCursor(index);
    } else {
      measure.x[index] = time ?? cursorTimeToSet(index);
    }
    onMeasureMove();
  }

  // From the context menu of a plot, at the time of the right click: a cursor that is off comes there, one that is on moves
  // there, and one that already stands there is switched off
  function placeCursor(index: 0 | 1, time?: number) {
    const current = measure.x[index];
    if (time == null || current == null || current === time) {
      toggleCursor(index, time);
      return;
    }
    measure.x[index] = time;
    onMeasureMove();
  }

  // Removes both cursors, by the key X or the button beside the cursor buttons
  function clearCursors() {
    removeCursor(0);
    removeCursor(1);
    onMeasureMove();
  }

  function canStartDrag(plot: uPlot, clientX: number, pointer: "mouse" | "touch"): boolean {
    return measureHitTest(measure, plot, clientX, pointer === "mouse" ? MOUSE_HIT_PX : TOUCH_HIT_PX) == null;
  }

  // 1 and 2 over a plot set a cursor at the mouse, move it there, or remove it when pressed on its line; X removes both,
  // also away from the plots, and the buttons bring them back to their old places
  function onKey(event: KeyboardEvent) {
    // While the crosshair is in a plot, the arrow keys move and zoom the time range as the buttons of the toolbar:
    // left and right go back and forward, up zooms in, down zooms out
    const arrow = ARROW_KEYS[event.key];
    if (arrow && isChartShortcut(event, options.inModal)) {
      const left = measure.hover?.cursor.left;
      if (left != null && left >= 0) {
        event.preventDefault();
        if (arrow.shift) {
          range.shift(arrow.shift);
        } else if (arrow.zoom) {
          range.zoom(arrow.zoom);
        }
      }
      return;
    }
    if (event.key.toLowerCase() === "x" && isChartShortcut(event, options.inModal)) {
      if (measure.x[0] != null || measure.x[1] != null) {
        event.preventDefault();
        clearCursors();
      }
      return;
    }
    if ((event.key !== "1" && event.key !== "2") || !isChartShortcut(event, options.inModal)) {
      return;
    }
    const plot = measure.hover;
    const left = plot?.cursor.left;
    if (!plot || left == null || left < 0) {
      return;
    }
    const index = event.key === "1" ? 0 : 1;
    const current = measure.x[index];
    const onLine = current != null && Math.abs(plot.valToPos(current, "x") - left) <= MOUSE_HIT_PX;
    if (onLine) {
      removeCursor(index);
    } else {
      measure.x[index] = plot.posToVal(left, "x");
    }
    onMeasureMove();
  }

  // S over a plot asks for the statistics between the cursors when both are on and not at the same time, and over the
  // visible range without both (as the entry of the context menu does); `open` gets the plot under the mouse, the
  // times of X1 and X2 or the range, and whether it is the visible range
  function onStatisticsKey(event: KeyboardEvent, open: (plot: uPlot, x1: number, x2: number, viewport: boolean) => void) {
    if (event.key.toLowerCase() !== "s" || !isChartShortcut(event, options.inModal)) {
      return;
    }
    const plot = measure.hover;
    const [x1, x2] = measure.x;
    if (!plot || (x1 != null && x2 != null && x1 === x2)) {
      return;
    }
    event.preventDefault();
    if (x1 != null && x2 != null) {
      open(plot, x1, x2, false);
    } else {
      open(plot, range.min, range.max, true);
    }
  }

  return { measure, measureTimes, toggleCursor, placeCursor, clearCursors, onMeasureMove, canStartDrag, onKey, onStatisticsKey };
}
