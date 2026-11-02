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

import { wheelDeltaPx, zoomedRange } from "./dragAndZoom";
import type { RangeController } from "./rangeController";

/**
 * Zooms the time range with the mouse wheel anywhere over a chart, around the time under the mouse; a mouse beside
 * the plot area (over the axes, between plots) counts as the nearest edge of it. The wheel turns of one frame are
 * added up. `getOver` returns the .u-over element of a plot, which tells where the plot area lies.
 */
export function createWheelZoom(range: RangeController, getOver: () => HTMLElement | null | undefined) {
  let pendingDy = 0;
  let clientX = 0;
  let raf = 0;

  function flush() {
    raf = 0;
    const dy = pendingDy;
    pendingDy = 0;
    const over = getOver();
    if (!over || dy === 0) {
      return;
    }
    const rect = over.getBoundingClientRect();
    if (rect.width <= 0) {
      return;
    }
    const share = Math.min(1, Math.max(0, (clientX - rect.left) / rect.width));
    const focus = range.min + share * (range.max - range.min);
    const [min, max] = zoomedRange(range.min, range.max, focus, dy);
    range.applyDirect(min, max, focus);
  }

  return {
    /** Takes the wheel turn; the caller has stopped the page from scrolling. */
    add(event: WheelEvent) {
      pendingDy += wheelDeltaPx(event);
      clientX = event.clientX;
      if (!raf) {
        raf = window.requestAnimationFrame(flush);
      }
    },
    dispose() {
      if (raf) {
        window.cancelAnimationFrame(raf);
        raf = 0;
      }
    }
  };
}
