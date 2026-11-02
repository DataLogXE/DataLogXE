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

import {
  ARCHIVE_START_FALLBACK,
  TIME_SPANS,
  centerRange,
  dateInputValue,
  FETCH_DEBOUNCE_MS,
  formatTimespan,
  lerp,
  limitBounds,
  matchTimespanIndex,
  shiftRange,
  shiftStartDate,
  SMOOTH_AMT,
  zoomTimespanIndex
} from "./bounds";

/** The time range shown in the toolbar: start date and time span. */
export interface RangeHeader {
  startDate: string;
  /** The day of the archive start: the date field offers no day before it. */
  minDate: string;
  timespanExact: boolean;
  timespanSeconds: number;
  timespanLabel: string;
}

export interface RangeControllerOptions {
  initial: [number, number];
  /** The x range changed; the plots set their scale. */
  onScale: (min: number, max: number) => void;
  onHeader: (header: RangeHeader) => void;
  /** The range is moving; the drawn points no longer match it. */
  onPending: () => void;
  /** The range settled for the debounce time; the plots load their points. */
  onIdle: () => void;
}

export type RangeController = ReturnType<typeof createRangeController>;

/**
 * The x range of one or more plots. Mouse, wheel and touch set it directly; the toolbar moves it
 * smoothly. Points load once the range stood still for the debounce time.
 */
export function createRangeController(options: RangeControllerOptions) {
  let actualMin = options.initial[0];
  let actualMax = options.initial[1];
  let smoothMin = actualMin;
  let smoothMax = actualMax;
  let archiveStart = ARCHIVE_START_FALLBACK;
  let smoothRaf = 0;
  let debounceTimer: number | undefined;
  let disposed = false;

  function applyHeader() {
    const match = matchTimespanIndex(smoothMax - smoothMin);
    options.onHeader({
      startDate: dateInputValue(smoothMin),
      minDate: dateInputValue(archiveStart),
      timespanExact: match.exact,
      timespanSeconds: match.exact ? TIME_SPANS[match.index].seconds : Math.round(smoothMax - smoothMin),
      timespanLabel: formatTimespan(smoothMax - smoothMin)
    });
  }

  function scheduleIdleWork() {
    options.onPending();
    if (debounceTimer !== undefined) {
      window.clearTimeout(debounceTimer);
    }
    debounceTimer = window.setTimeout(() => {
      debounceTimer = undefined;
      if (disposed) {
        return;
      }
      applyHeader();
      options.onIdle();
    }, FETCH_DEBOUNCE_MS);
  }

  function applyDirect(min: number, max: number, focus?: number) {
    const limited = limitBounds(min, max, archiveStart, undefined, focus);
    actualMin = limited[0];
    actualMax = limited[1];
    smoothMin = limited[0];
    smoothMax = limited[1];
    if (smoothRaf) {
      window.cancelAnimationFrame(smoothRaf);
      smoothRaf = 0;
    }
    options.onScale(actualMin, actualMax);
    applyHeader();
    scheduleIdleWork();
  }

  function applySmooth(min: number, max: number) {
    const limited = limitBounds(min, max, archiveStart);
    smoothMin = limited[0];
    smoothMax = limited[1];
    applyHeader();
    if (!smoothRaf) {
      smoothRaf = window.requestAnimationFrame(tickSmooth);
    }
  }

  function tickSmooth() {
    if (disposed) {
      return;
    }
    const dMin = Math.abs(actualMin - smoothMin);
    const dMax = Math.abs(actualMax - smoothMax);
    if (dMin > 1e-3 || dMax > 1e-3) {
      actualMin = lerp(actualMin, smoothMin, SMOOTH_AMT);
      actualMax = lerp(actualMax, smoothMax, SMOOTH_AMT);
      options.onScale(actualMin, actualMax);
      scheduleIdleWork();
      smoothRaf = window.requestAnimationFrame(tickSmooth);
      return;
    }
    actualMin = smoothMin;
    actualMax = smoothMax;
    smoothRaf = 0;
    options.onScale(actualMin, actualMax);
    scheduleIdleWork();
  }

  return {
    get min() {
      return actualMin;
    },
    get max() {
      return actualMax;
    },
    /** The range is animating or waiting for the debounce, so a load would be out of date at once. */
    get moving() {
      return smoothRaf !== 0 || debounceTimer !== undefined;
    },
    get archiveStart() {
      return archiveStart;
    },
    setArchiveStart(value: number | null | undefined) {
      if (value && value !== archiveStart) {
        archiveStart = value;
        applyHeader();
      }
    },
    /** Sets the range without animation and without the debounce; the caller loads the points. */
    reset(bounds: [number, number]) {
      if (smoothRaf) {
        window.cancelAnimationFrame(smoothRaf);
        smoothRaf = 0;
      }
      if (debounceTimer !== undefined) {
        window.clearTimeout(debounceTimer);
        debounceTimer = undefined;
      }
      archiveStart = ARCHIVE_START_FALLBACK;
      actualMin = bounds[0];
      actualMax = bounds[1];
      smoothMin = bounds[0];
      smoothMax = bounds[1];
      applyHeader();
    },
    applyDirect,
    applySmooth,
    /** Marks the points as out of date and loads them after the debounce, for example after a resize. */
    refresh: scheduleIdleWork,
    setStartDate(value: string) {
      applySmooth(...shiftStartDate(actualMin, actualMax, value));
    },
    setTimespan(seconds: number) {
      if (!Number.isFinite(seconds) || seconds <= 0) {
        return;
      }
      applySmooth(...centerRange(smoothMin, smoothMax, seconds));
    },
    shift(direction: -1 | 1) {
      applySmooth(...shiftRange(smoothMin, smoothMax, direction));
    },
    zoom(direction: -1 | 1) {
      const idx = zoomTimespanIndex(matchTimespanIndex(smoothMax - smoothMin).index, direction);
      applySmooth(...centerRange(smoothMin, smoothMax, TIME_SPANS[idx].seconds));
    },
    dispose() {
      disposed = true;
      if (debounceTimer !== undefined) {
        window.clearTimeout(debounceTimer);
        debounceTimer = undefined;
      }
      if (smoothRaf) {
        window.cancelAnimationFrame(smoothRaf);
        smoothRaf = 0;
      }
    }
  };
}
