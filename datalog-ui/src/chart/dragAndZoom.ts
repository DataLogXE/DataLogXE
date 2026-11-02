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
import { ZOOM_SENSITIVITY } from "./bounds";

export type BoundsHandler = (min: number, max: number, focus?: number) => void;

const LINE_DELTA_PX = 16;
const PAGE_DELTA_PX = 16 * 16;
const MIN_SCALE = 0.5;
const MAX_SCALE = 2;

export function wheelDeltaPx(event: WheelEvent): number {
  let dy = event.deltaY;
  if (event.deltaMode === 1) {
    dy *= LINE_DELTA_PX;
  } else if (event.deltaMode === 2) {
    dy *= PAGE_DELTA_PX;
  }
  return dy;
}

/**
 * The range after a wheel turn of `dy` pixels: wider for a positive one, narrower for a negative one, with the
 * time `focus` staying where it is.
 */
export function zoomedRange(min: number, max: number, focus: number, dy: number, sensitivity = ZOOM_SENSITIVITY): [number, number] {
  const range = max - min;
  const scale = Math.min(MAX_SCALE, Math.max(MIN_SCALE, Math.exp(dy * sensitivity)));
  const newMin = focus - (focus - min) * scale;
  return [newMin, newMin + range * scale];
}

export function dragAndZoomPlugin(
  onBounds: BoundsHandler,
  sensitivity = ZOOM_SENSITIVITY,
  options: { canStart?: (plot: uPlot, clientX: number) => boolean } = {}
): uPlot.Plugin {
  let zoomRaf = 0;
  let dragRaf = 0;

  return {
    hooks: {
      ready: (plot) => {
        const over = plot.over;
        let pendingDy = 0;
        let lastClientX = 0;

        const flushZoom = () => {
          zoomRaf = 0;
          const dy = pendingDy;
          pendingDy = 0;
          if (dy === 0) {
            return;
          }

          const rect = over.getBoundingClientRect();
          if (rect.width <= 0) {
            return;
          }

          const left = Math.min(rect.width, Math.max(0, lastClientX - rect.left));
          const oxMin = plot.scales.x.min ?? 0;
          const oxMax = plot.scales.x.max ?? 0;
          const oxRange = oxMax - oxMin;
          if (oxRange <= 0) {
            return;
          }

          const xVal = plot.posToVal(left, "x");
          const [nxMin, nxMax] = zoomedRange(oxMin, oxMax, xVal, dy, sensitivity);
          onBounds(nxMin, nxMax, xVal);
        };

        const onMouseDown = (event: MouseEvent) => {
          if (event.button !== 0) {
            return;
          }
          if (options.canStart && !options.canStart(plot, event.clientX)) {
            return;
          }
          event.preventDefault();

          const left0 = event.clientX;
          const scXMin0 = plot.scales.x.min ?? 0;
          const scXMax0 = plot.scales.x.max ?? 0;
          const xUnitsPerPx = plot.posToVal(1, "x") - plot.posToVal(0, "x");
          let pendingClientX = left0;

          const flushDrag = () => {
            dragRaf = 0;
            const dx = xUnitsPerPx * (pendingClientX - left0);
            onBounds(scXMin0 - dx, scXMax0 - dx);
          };

          const onMove = (moveEvent: MouseEvent) => {
            moveEvent.preventDefault();
            pendingClientX = moveEvent.clientX;
            if (!dragRaf) {
              dragRaf = window.requestAnimationFrame(flushDrag);
            }
          };

          const onUp = () => {
            document.removeEventListener("mousemove", onMove);
            document.removeEventListener("mouseup", onUp);
            if (dragRaf) {
              window.cancelAnimationFrame(dragRaf);
              dragRaf = 0;
              flushDrag();
            }
          };

          document.addEventListener("mousemove", onMove);
          document.addEventListener("mouseup", onUp);
        };

        const onWheel = (event: WheelEvent) => {
          event.preventDefault();
          if (event.deltaY === 0) {
            return;
          }
          pendingDy += wheelDeltaPx(event);
          lastClientX = event.clientX;
          if (!zoomRaf) {
            zoomRaf = window.requestAnimationFrame(flushZoom);
          }
        };

        over.addEventListener("mousedown", onMouseDown);
        over.addEventListener("wheel", onWheel, { passive: false });
      },
      destroy: () => {
        if (zoomRaf) {
          window.cancelAnimationFrame(zoomRaf);
          zoomRaf = 0;
        }
        if (dragRaf) {
          window.cancelAnimationFrame(dragRaf);
          dragRaf = 0;
        }
      }
    }
  };
}
