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

/** Where a right click in a plot was: where its menu opens and the time under the mouse. */
export interface PlotMenuPlace {
  x: number;
  y: number;
  /** Unix seconds; a cursor that is switched on in the menu appears there. */
  time: number;
}

/**
 * The plot area (uPlot's `.u-over`) that a right click hit, null elsewhere. The label with the tip of dX keeps the
 * menu of the browser, so its text can be copied.
 */
export function plotAreaOfContextMenu(event: MouseEvent): HTMLElement | null {
  const target = event.target;
  if (!(target instanceof Element) || target.closest(".chart-measure-tip")) {
    return null;
  }
  return target.closest<HTMLElement>(".u-over");
}

export function plotMenuPlace(event: MouseEvent, plot: uPlot): PlotMenuPlace {
  return {
    x: event.clientX,
    y: event.clientY,
    time: plot.posToVal(event.clientX - plot.over.getBoundingClientRect().left, "x")
  };
}
