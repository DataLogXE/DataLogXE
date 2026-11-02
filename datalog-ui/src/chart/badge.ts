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

import { formatDurationMs, formatInteger } from "../format";

/** What the server did to the points of a plot and how many of them lie in the view. */
export interface PlotBadgeStats {
  /** False until the first points arrived. */
  loaded: boolean;
  compressionSeconds: number;
  reductionSeconds: number;
  viewCount: number;
  /** Window of the compression level of a coarse preview drawn instead of the exact points, else 0. */
  previewSeconds: number;
}

export function plotBadgeClass(stats: PlotBadgeStats): "pending" | "loaded" {
  return !stats.loaded || stats.previewSeconds > 0 ? "pending" : "loaded";
}

export function plotBadgeText(stats: PlotBadgeStats): string {
  // The exact points failed to load after the preview was drawn
  if (stats.previewSeconds > 0) {
    return `coarse preview · compressed ${formatDurationMs(stats.previewSeconds * 1000)}`;
  }
  if (!stats.loaded) {
    return "…";
  }
  const count = formatInteger(stats.viewCount);
  // Without "points" every drawn point is a raw value, otherwise the source of the points follows
  if (stats.compressionSeconds > 0) {
    return `${count} points · compressed ${formatDurationMs(stats.compressionSeconds * 1000)}`;
  }
  if (stats.reductionSeconds > 0) {
    // The server kept the first, min, max and last raw value of every pixel
    return `${count} points · raw values`;
  }
  return `${count} raw values`;
}
