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
import type { PlotPoints } from "../types";
import type { FetchQueue } from "./fetchQueue";
import type { MeasureState } from "./measureCursors";
import type { PlotController } from "./plotController";
import type { RangeController } from "./rangeController";

/** What the plots of a chart share: the range, the load queue, the measuring cursors and the cursor sync. */
export interface StackContext {
  range: RangeController;
  queue: FetchQueue;
  /** The coarse previews, which do not wait behind the exact points. */
  previewQueue: FetchQueue;
  measure: MeasureState;
  syncKey: string;
  /** The last points of each data tag, so a reloaded chart draws them at once. */
  cache: Map<number, { key: string; data: PlotPoints; at: number }>;
  canStartDrag: (plot: uPlot, clientX: number, pointer: "mouse" | "touch") => boolean;
  onMeasureMove: () => void;
  register: (key: string, controller: PlotController, element: HTMLElement) => void;
  unregister: (key: string) => void;
}
