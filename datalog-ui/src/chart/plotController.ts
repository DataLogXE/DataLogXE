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

import { reactive } from "vue";
import type uPlot from "uplot";
import { getPlotPoints, getPlotPreview, isAbortError } from "../api";
import type { PlotPoints } from "../types";
import type { PlotBadgeStats } from "./badge";
import {
  isNowInRange,
  LIVE_INTERVAL_MS,
  LIVE_MAX_RANGE_SEC,
  nowSec,
  paddedRange,
  plotHeight,
  PREVIEW_DELAY_MS,
  PREVIEW_MIN_GAP_PX,
  Y_AXIS_WIDTH
} from "./bounds";
import { createTagPlot, setPlotStale, setPlotYLock, toAlignedData, type TagPlotOptions } from "./createPlot";
import type { BoundsHandler } from "./dragAndZoom";
import type { FetchQueue } from "./fetchQueue";
import { countInRange, mergePreview, uncoveredSeconds, type DrawnPoints } from "./plotData";

/** What a plot shows next to its line: the badge, the loading phase and an error. */
export interface PlotState extends PlotBadgeStats {
  /** Points for the current range are loading; the drawn ones (old points or a coarse preview) are grayed. */
  fetching: boolean;
  loadingPhase: string;
  error: string;
  /** Whether the tag is a bool, null until the kind is known (given up front or told by the first points). */
  isBool: boolean | null;
}

export type PlotOptionsOverride = Pick<
  TagPlotOptions,
  "showXAxis" | "syncKey" | "hoverLabel" | "canStartDrag" | "extraPlugins"
>;

export interface PlotControllerOptions {
  target: HTMLElement;
  dataSourceId: number;
  tagId: number;
  seriesName: string;
  /** Shown in the activity panel while the points load for a while. */
  label?: string;
  /** Known up front on the chart page. Otherwise a number plot shows until the first points tell. */
  isBool?: boolean;
  /** Width of the plot with its y axis in CSS pixels. */
  width: () => number;
  height?: (isBool: boolean) => number;
  /** The extra options of a plot that is built, with the kind of the tag. */
  plotOptions?: (isBool: boolean) => PlotOptionsOverride;
  /** The range at the moment the points arrive; a newer range may have replaced the requested one. */
  getRange: () => [number, number];
  onBounds: BoundsHandler;
  /** Limits how many plots load at once. */
  queue?: FetchQueue;
  /** Limits how many coarse previews load at once; they do not wait behind the exact points. */
  previewQueue?: FetchQueue;
  onArchiveStart?: (value: number) => void;
  /** The exact points of a load, with the time they were loaded (unix seconds). */
  onData?: (key: string, data: PlotPoints, at: number) => void;
  /** Points loaded before for the same tag, drawn until the next load. */
  initial?: { key: string; data: PlotPoints; at: number };
}

export type PlotController = ReturnType<typeof createPlotController>;

/** Width of the plot area of a wrapper in CSS pixels, with the y axis. */
export function plotAreaWidth(wrap: HTMLElement): number {
  const style = getComputedStyle(wrap);
  const pad = parseFloat(style.paddingLeft) + parseFloat(style.paddingRight);
  return Math.max(wrap.clientWidth - pad, 120);
}

// Width of the drawing area in device pixels, computed like uPlot does. The server reduces the
// points to one M4 bucket per pixel, which is only exact with the real pixels: without the y axis
// and with the 2 or 3 device pixels per CSS pixel of high resolution screens.
export function devicePixelWidth(cssWidth: number): number {
  return Math.round(Math.max(cssWidth - Y_AXIS_WIDTH, 1) * (window.devicePixelRatio || 1));
}

export function fetchKey(min: number, max: number, width: number): string {
  return `${min.toFixed(3)}|${max.toFixed(3)}|${width}`;
}

/** The padded range of the points loaded for a fetch key; the server adds one span on both sides. */
export function loadedExtent(key: string): { from: number; to: number; span: number; width: number } | null {
  const [min, max, width] = key.split("|").map(Number);
  if (![min, max, width].every(Number.isFinite)) {
    return null;
  }
  const [from, to] = paddedRange(min, max);
  return { from, to, span: max - min, width };
}

/** A pan keeps the span and width of the loaded points and stays inside them, so no new points are needed. */
export function extentCovers(
  extent: { from: number; to: number; span: number; width: number } | null,
  min: number,
  max: number,
  width: number
): boolean {
  if (!extent || extent.width !== width) {
    return false;
  }
  const eps = extent.span * 1e-4;
  return Math.abs(max - min - extent.span) <= eps && min >= extent.from - eps && max <= extent.to + eps;
}

/**
 * The span the points of a load stand for: the padded range, but only up to the time of the load (or the last
 * point, should the clock of the server be ahead), since later values were not recorded yet.
 */
function loadedSpan(key: string, points: PlotPoints, at: number): { from: number; to: number } | null {
  const extent = loadedExtent(key);
  if (!extent) {
    return null;
  }
  const last = points.times.length > 0 ? points.times[points.times.length - 1] : -Infinity;
  return { from: extent.from, to: Math.min(extent.to, Math.max(at, last)) };
}

/**
 * One data tag in a uPlot: loads its points for a range and keeps the drawn line in step. Where the view has no
 * drawn points, a coarse preview from a compression level fills the gaps grayed until the exact points arrive.
 */
export function createPlotController(options: PlotControllerOptions) {
  const state = reactive<PlotState>({
    fetching: true,
    loadingPhase: "",
    error: "",
    isBool: options.isBool ?? null,
    loaded: false,
    compressionSeconds: 0,
    reductionSeconds: 0,
    viewCount: 0,
    previewSeconds: 0
  });
  const height = options.height ?? plotHeight;

  let plot: uPlot | null = null;
  let plotIsBool: boolean | null = null;
  let data: PlotPoints | null = null;
  let loadedTimes: number[] = [];
  let fetchGen = 0;
  let abort: AbortController | null = null;
  // A load for a new range runs; a live refresh must not cancel it.
  let userFetching = false;
  let lastFetchKey = "";
  // When the points of lastFetchKey were loaded (unix seconds)
  let loadedAt = 0;
  // The drawn points: those of lastFetchKey, or with the gaps of the view filled from a preview
  let drawn: DrawnPoints | null = null;
  // The preview that waits or loads. Its own token, since a newer exact load ends it while fetchGen stays.
  let preview: { timer: number; abort: AbortController } | null = null;
  // The range and width of the user load that waits or runs
  let requestedKey = "";
  let lastLiveAt = 0;
  let yUnlockRaf = 0;
  let stopped = false;

  // The loaded points already contain this range (a pan inside them)
  function covered(min: number, max: number, width: number): boolean {
    return extentCovers(loadedExtent(lastFetchKey), min, max, width);
  }

  // Gray the line while new points load, since the drawn ones no longer match the range, and while it is a preview
  function syncStale() {
    if (plot) {
      setPlotStale(plot, state.fetching || !!drawn?.preview);
    }
  }

  function setFetching(value: boolean) {
    state.fetching = value;
    syncStale();
  }

  function setDrawnFromLoad(points: PlotPoints, key: string, at: number) {
    const span = loadedSpan(key, points, at);
    drawn = span ? { points, ...span, preview: false } : null;
    state.previewSeconds = 0;
  }

  function cancelPreview() {
    if (!preview) {
      return;
    }
    window.clearTimeout(preview.timer);
    preview.abort.abort();
    preview = null;
  }

  // Waits a moment, so a fast exact load shows no gray preview, then loads it unless the exact points came first
  function schedulePreview(min: number, max: number, width: number) {
    const token = { timer: 0, abort: new AbortController() };
    preview = token;
    token.timer = window.setTimeout(() => void loadPreview(token, min, max, width), PREVIEW_DELAY_MS);
  }

  async function loadPreview(token: { abort: AbortController }, min: number, max: number, width: number) {
    const signal = token.abort.signal;
    const load = () => getPlotPreview(options.dataSourceId, options.tagId, min, max, width, signal);
    try {
      const points = options.previewQueue ? await options.previewQueue.run(load, signal) : await load();
      if (stopped || preview !== token) {
        return;
      }
      preview = null;
      if (points.archiveStart) {
        options.onArchiveStart?.(points.archiveStart);
      }
      if (points.times.length === 0) {
        return;
      }
      ensurePlot(points.isBool, points.name || options.seriesName);
      drawn = mergePreview(drawn, points, min, max, nowSec());
      state.previewSeconds = points.compressionSeconds;
      draw(drawn.points);
      syncStale();
    } catch {
      // The exact load reports its own errors; without a preview the plot only waits longer
      if (preview === token) {
        preview = null;
      }
    }
  }

  function refreshViewCount() {
    const [min, max] = options.getRange();
    state.viewCount = countInRange(loadedTimes, min, max);
  }

  function destroyPlot() {
    plot?.destroy();
    plot = null;
    plotIsBool = null;
    options.target.replaceChildren();
  }

  function resize() {
    if (!plot || plotIsBool == null) {
      return;
    }
    plot.setSize({ width: options.width(), height: height(plotIsBool) });
  }

  // `known: false` builds a number plot before the kind of the tag is known, so the empty plot shows at once
  // while the points load; points of a bool tag build it again
  function ensurePlot(isBool: boolean, seriesName: string, known = true) {
    if (known) {
      state.isBool = isBool;
    }
    if (stopped || (plot && plotIsBool === isBool)) {
      return;
    }
    destroyPlot();
    const [min, max] = options.getRange();
    plot = createTagPlot({
      target: options.target,
      seriesName,
      isBool,
      width: options.width(),
      height: height(isBool),
      xMin: min,
      xMax: max,
      onBounds: options.onBounds,
      ...options.plotOptions?.(isBool)
    });
    plotIsBool = isBool;
    syncStale();
    resize();
  }

  function draw(points: PlotPoints) {
    if (!plot) {
      return;
    }
    const [min, max] = options.getRange();
    // Keep the y range while the data swaps, so the axis does not jump for one frame
    setPlotYLock(plot, true);
    plot.batch(() => {
      plot?.setData(toAlignedData(points, min, max), false);
      plot?.setScale("x", { min, max });
    });
    if (yUnlockRaf) {
      window.cancelAnimationFrame(yUnlockRaf);
    }
    const gen = fetchGen;
    yUnlockRaf = window.requestAnimationFrame(() => {
      yUnlockRaf = 0;
      if (!plot || stopped || gen !== fetchGen) {
        return;
      }
      setPlotYLock(plot, false);
      // Only setting the x scale fits the y scale to the new points; a plain redraw keeps the locked range
      plot.redraw(true, false);
    });
  }

  function apply(points: PlotPoints) {
    data = points;
    loadedTimes = points.times;
    state.loaded = true;
    state.compressionSeconds = points.compressionSeconds ?? 0;
    state.reductionSeconds = points.reductionSeconds ?? 0;
    refreshViewCount();
  }

  async function fetch(
    min: number,
    max: number,
    width: number,
    force: boolean,
    { preview: withPreview = true }: { preview?: boolean } = {}
  ): Promise<void> {
    if (stopped) {
      return;
    }
    if (force) {
      if (!isNowInRange(min, max) || max - min >= LIVE_MAX_RANGE_SEC) {
        return;
      }
      if (userFetching || Date.now() - lastLiveAt < LIVE_INTERVAL_MS) {
        return;
      }
    }

    // A different width needs other points, since the server reduces them per pixel
    const key = fetchKey(min, max, width);
    if (!force && (key === lastFetchKey || covered(min, max, width))) {
      // Supersedes a load still running for a range that is no longer shown
      if (userFetching) {
        abort?.abort();
        fetchGen += 1;
        userFetching = false;
      }
      cancelPreview();
      // A preview drawn on the way lies over the loaded points, which are exact here
      if (drawn?.preview && data) {
        setDrawnFromLoad(data, lastFetchKey, loadedAt);
        draw(data);
      }
      // A failed load was for a range that is no longer shown
      state.error = "";
      state.loadingPhase = "";
      setFetching(false);
      refreshViewCount();
      return;
    }

    // The same range is loading already
    if (!force && userFetching && key === requestedKey) {
      return;
    }

    abort?.abort();
    cancelPreview();
    const controller = new AbortController();
    abort = controller;
    const gen = ++fetchGen;
    userFetching = !force;
    requestedKey = force ? "" : key;
    // A live refresh keeps the shown points valid, so it loads without the loading badge.
    if (!force) {
      setFetching(true);
      state.loadingPhase = options.queue ? "Queued" : "";
      // Part of the view has no drawn points: a coarse preview fills it first
      const gapPx = (uncoveredSeconds(drawn, min, max, nowSec()) / (max - min)) * width;
      if (withPreview && gapPx >= PREVIEW_MIN_GAP_PX) {
        schedulePreview(min, max, width);
      }
    }
    try {
      const load = () => getPlotPoints(
        options.dataSourceId,
        options.tagId,
        min,
        max,
        width,
        controller.signal,
        (progress) => {
          if (!stopped && gen === fetchGen && !force) {
            state.loadingPhase = progress.phase;
          }
        },
        { label: options.label, background: force }
      );
      const points = options.queue
        ? await options.queue.run(load, controller.signal, () => {
          if (gen === fetchGen && !force) {
            state.loadingPhase = "";
          }
        })
        : await load();
      if (stopped || gen !== fetchGen) {
        return;
      }
      cancelPreview();
      state.loadingPhase = "";
      if (points.archiveStart) {
        options.onArchiveStart?.(points.archiveStart);
      }
      const at = nowSec();
      lastFetchKey = key;
      loadedAt = at;
      lastLiveAt = Date.now();
      apply(points);
      setDrawnFromLoad(points, key, at);
      if (!force) {
        setFetching(false);
      } else {
        syncStale();
      }
      ensurePlot(points.isBool, points.name || options.seriesName);
      draw(points);
      state.error = "";
      options.onData?.(key, points, at);
    } catch (err) {
      if (stopped || gen !== fetchGen || isAbortError(err)) {
        return;
      }
      // A drawn preview stays grayed and the badge says so
      cancelPreview();
      state.loadingPhase = "";
      state.error = err instanceof Error ? err.message : "Unable to load chart data.";
      if (!force) {
        setFetching(false);
      }
      refreshViewCount();
    } finally {
      if (gen === fetchGen) {
        userFetching = false;
      }
    }
  }

  if (options.isBool != null) {
    ensurePlot(options.isBool, options.seriesName);
  } else if (!options.initial) {
    ensurePlot(false, options.seriesName, false);
  }
  if (options.initial) {
    lastFetchKey = options.initial.key;
    loadedAt = options.initial.at;
    apply(options.initial.data);
    setDrawnFromLoad(options.initial.data, options.initial.key, options.initial.at);
    ensurePlot(options.initial.data.isBool, options.initial.data.name || options.seriesName);
    draw(options.initial.data);
  }

  return {
    state,
    /** The exact points of the last load; never a preview. */
    get data() {
      return data;
    },
    /** The points the line shows, which may hold a coarse preview. */
    get drawnData() {
      return drawn?.points ?? data;
    },
    get plot() {
      return plot;
    },
    /** The range and width of the drawn points, empty before the first load. */
    get loadedKey() {
      return lastFetchKey;
    },
    /** A user load for a new range is waiting or running. */
    get loading() {
      return userFetching;
    },
    /** The points drawn belong to this range and width. */
    isCurrent(min: number, max: number, width: number) {
      return lastFetchKey === fetchKey(min, max, width) || covered(min, max, width);
    },
    /**
     * Loads the points of the range; it returns at once when they are drawn or loading already. A
     * forced load is the live refresh and runs only when due. `preview: false` loads without a coarse
     * preview, for a load nobody looks at.
     */
    fetch,
    setRange(min: number, max: number) {
      plot?.batch(() => {
        plot?.setScale("x", { min, max });
      });
      if (!state.fetching) {
        refreshViewCount();
      }
    },
    /** The range moves; the drawn points no longer match it. */
    markPending() {
      const [min, max] = options.getRange();
      // A pan inside the loaded points needs no new ones, so the line stays as it is
      if (!covered(min, max, devicePixelWidth(options.width()))) {
        setFetching(true);
      }
    },
    resize,
    redraw() {
      plot?.redraw(false, false);
    },
    destroy() {
      stopped = true;
      abort?.abort();
      cancelPreview();
      if (yUnlockRaf) {
        window.cancelAnimationFrame(yUnlockRaf);
        yUnlockRaf = 0;
      }
      destroyPlot();
    }
  };
}
