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

import { chartTimeTickSample } from "../format";

/** The font of the axis labels: the font of the page in the small size of the charts (--chart-font-small) */
export const AXIS_FONT = '11px "Segoe UI", system-ui, sans-serif';
// The least space between two ticks, and the least free space between two labels
const MIN_TICK_SPACE_PX = 60;
const LABEL_GAP_PX = 24;

/** The steps of the time axis in seconds: down to hundredths of a second, so a zoomed plot gets ticks that fit. */
export const TIME_INCRS = [
  0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30,
  60, 120, 300, 600, 900, 1800,
  3600, 7200, 3600 * 3, 3600 * 6, 3600 * 12, 3600 * 24,
  3600 * 24 * 7, 3600 * 24 * 14
];

let measureContext: CanvasRenderingContext2D | null | undefined;
const widths = new Map<string, number>();

// The width of the widest label of a step, measured with the font of the axis; the formats change with the step
function labelWidth(incrSec: number): number {
  const sample = chartTimeTickSample(incrSec);
  let width = widths.get(sample);
  if (width === undefined) {
    if (measureContext === undefined) {
      measureContext = document.createElement("canvas").getContext("2d");
      if (measureContext) {
        measureContext.font = AXIS_FONT;
      }
    }
    // Without a canvas the length of the text is a good guess
    width = measureContext ? measureContext.measureText(sample).width : sample.length * 7;
    widths.set(sample, width);
  }
  return width;
}

/**
 * The least space between two ticks of the time axis, so that their labels do not run into each other: the first step
 * whose ticks are far enough apart for its labels (the ones with milliseconds are wide) gets just that space, which
 * makes uPlot take that step. Plots of one chart have the same range and width, so their grids are alike.
 */
export function timeAxisSpace(min: number, max: number, plotWidthPx: number): number {
  const pxPerSecond = plotWidthPx / (max - min);
  if (!(pxPerSecond > 0) || !Number.isFinite(pxPerSecond)) {
    return MIN_TICK_SPACE_PX;
  }
  for (const incr of TIME_INCRS) {
    const px = incr * pxPerSecond;
    if (px >= Math.max(MIN_TICK_SPACE_PX, labelWidth(incr) + LABEL_GAP_PX)) {
      // A little less than the distance of this step, so floating point does not pass it by
      return px - 0.5;
    }
  }
  return MIN_TICK_SPACE_PX;
}
