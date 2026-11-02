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
import type { BoundsHandler } from "./dragAndZoom";

const DECIDE_PX = 8;
const TAP_MS = 300;
const MIN_PINCH_PX = 20;

export interface TouchGestureOptions {
  /** False when the touch hits something else, for example a measuring cursor. */
  canStart?: (plot: uPlot, clientX: number) => boolean;
  /** A short tap; by default it shows the cursor there, also in the synced plots. */
  onTap?: (plot: uPlot, left: number, top: number) => void;
}

type Pointer = { x: number; y: number };

/**
 * Touch gestures on the plot: one finger drags the range sideways, two fingers zoom around their
 * center, a tap shows the cursor. A finger that moves up or down first scrolls the page instead
 * (touch-action: pan-y). The mouse keeps using the drag and wheel plugin.
 */
export function touchGesturesPlugin(onBounds: BoundsHandler, options: TouchGestureOptions = {}): uPlot.Plugin {
  let cleanup: (() => void) | null = null;

  return {
    hooks: {
      ready: (plot) => {
        const over = plot.over;
        over.style.touchAction = "pan-y";

        const pointers = new Map<number, Pointer>();
        let mode: "none" | "pending" | "pan" | "pinch" | "ignored" = "none";
        let startX = 0;
        let startY = 0;
        let startAt = 0;
        let min0 = 0;
        let max0 = 0;
        let unitsPerPx = 0;
        let pinchD0 = 0;
        let pinchT0 = 0;
        let pinchR0 = 0;
        let raf = 0;
        let pending: [number, number, number | undefined] | null = null;

        const flush = () => {
          raf = 0;
          if (pending) {
            const next = pending;
            pending = null;
            onBounds(next[0], next[1], next[2]);
          }
        };

        const schedule = (min: number, max: number, focus?: number) => {
          pending = [min, max, focus];
          if (!raf) {
            raf = window.requestAnimationFrame(flush);
          }
        };

        const startPan = (pointer: Pointer) => {
          const rect = over.getBoundingClientRect();
          min0 = plot.scales.x.min ?? 0;
          max0 = plot.scales.x.max ?? 0;
          unitsPerPx = rect.width > 0 ? (max0 - min0) / rect.width : 0;
          startX = pointer.x;
          startY = pointer.y;
        };

        const startPinch = () => {
          const [a, b] = [...pointers.values()];
          const rect = over.getBoundingClientRect();
          pinchD0 = Math.max(Math.abs(a.x - b.x), MIN_PINCH_PX);
          const center = (a.x + b.x) / 2 - rect.left;
          pinchT0 = plot.posToVal(center, "x");
          pinchR0 = (plot.scales.x.max ?? 0) - (plot.scales.x.min ?? 0);
          mode = "pinch";
        };

        const tap = (pointer: Pointer) => {
          const rect = over.getBoundingClientRect();
          const left = pointer.x - rect.left;
          const top = pointer.y - rect.top;
          if (options.onTap) {
            options.onTap(plot, left, top);
            return;
          }
          // The third argument publishes the cursor to the synced plots; the typings do not know it.
          (plot.setCursor as (opts: { left: number; top: number }, fire?: boolean, pub?: boolean) => void)(
            { left, top },
            true,
            true
          );
        };

        const onDown = (event: PointerEvent) => {
          if (event.pointerType !== "touch") {
            return;
          }
          // No emulated mouse events, so the mouse drag does not start as well
          event.preventDefault();
          pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
          if (pointers.size === 1) {
            if (options.canStart && !options.canStart(plot, event.clientX)) {
              mode = "ignored";
              return;
            }
            mode = "pending";
            startAt = performance.now();
            startPan({ x: event.clientX, y: event.clientY });
            return;
          }
          if (pointers.size === 2 && mode !== "ignored") {
            for (const id of pointers.keys()) {
              try {
                over.setPointerCapture(id);
              } catch {
                // The pointer may already be gone
              }
            }
            startPinch();
          }
        };

        const onMove = (event: PointerEvent) => {
          const pointer = pointers.get(event.pointerId);
          if (!pointer) {
            return;
          }
          pointer.x = event.clientX;
          pointer.y = event.clientY;

          if (mode === "pending") {
            const dx = pointer.x - startX;
            const dy = pointer.y - startY;
            if (Math.hypot(dx, dy) < DECIDE_PX) {
              return;
            }
            if (Math.abs(dx) > Math.abs(dy)) {
              mode = "pan";
              try {
                over.setPointerCapture(event.pointerId);
              } catch {
                // The pointer may already be gone
              }
            } else {
              // The browser scrolls the page and cancels the pointer
              mode = "ignored";
              return;
            }
          }

          if (mode === "pan") {
            const dx = (pointer.x - startX) * unitsPerPx;
            schedule(min0 - dx, max0 - dx);
            return;
          }

          if (mode === "pinch" && pointers.size >= 2) {
            const [a, b] = [...pointers.values()];
            const rect = over.getBoundingClientRect();
            if (rect.width <= 0) {
              return;
            }
            const distance = Math.max(Math.abs(a.x - b.x), MIN_PINCH_PX);
            const center = (a.x + b.x) / 2 - rect.left;
            const range = (pinchR0 * pinchD0) / distance;
            const min = pinchT0 - (center / rect.width) * range;
            schedule(min, min + range, pinchT0);
          }
        };

        const onUp = (event: PointerEvent) => {
          const pointer = pointers.get(event.pointerId);
          if (!pointer) {
            return;
          }
          pointers.delete(event.pointerId);

          if (
            mode === "pending" &&
            event.type === "pointerup" &&
            performance.now() - startAt < TAP_MS
          ) {
            tap(pointer);
          }

          if (pointers.size === 1 && mode === "pinch") {
            // The remaining finger drags on from where it is
            const rest = [...pointers.values()][0];
            mode = "pan";
            startPan(rest);
            return;
          }

          if (pointers.size === 0) {
            mode = "none";
            if (raf) {
              window.cancelAnimationFrame(raf);
              flush();
            }
          }
        };

        over.addEventListener("pointerdown", onDown);
        over.addEventListener("pointermove", onMove);
        over.addEventListener("pointerup", onUp);
        over.addEventListener("pointercancel", onUp);

        cleanup = () => {
          if (raf) {
            window.cancelAnimationFrame(raf);
            raf = 0;
          }
          over.removeEventListener("pointerdown", onDown);
          over.removeEventListener("pointermove", onMove);
          over.removeEventListener("pointerup", onUp);
          over.removeEventListener("pointercancel", onUp);
        };
      },
      destroy: () => {
        cleanup?.();
        cleanup = null;
      }
    }
  };
}
