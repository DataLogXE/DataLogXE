/*
 * DataLogXE - A simple and high performance data logger.
 * Copyright (C) 2026 datalogxe@gmail.com
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

import { computed, onUnmounted, ref, watch, type Ref } from "vue";
import type { ExportFormat, ExportProgress, ExportSource } from "../components/ChartExportDialog.vue";
import { checkRawCsv, getPlotPoints, getRawValues, isAbortError, rawCsvUrl, type RawValues } from "../api";
import { formatInteger } from "../format";
import { notify } from "../notify";
import { buildCsv, decimalComma, downloadBlob, downloadUrl, exportFileName, timeZoneOffsets } from "./csvExport";
import type { PlotController } from "./plotController";
import { indexRange } from "./plotData";
import type { RangeController } from "./rangeController";
import { MAX_SHEET_ROWS, buildXlsx } from "./xlsxExport";

export interface ExportRow {
  key: string;
  plot: { dataSourceId: number; dataTagId: number; sourceName: string; tagName: string };
}

/**
 * The export of a chart as CSV or Excel, for the chart page and the quick chart of a tag: the state of the dialog,
 * the plots it offers with their drawn rows and the export itself. `rows` are the plots of the chart, `controllerOf`
 * gives the controller that draws a plot, `pixelWidth` the width its points load for and `fileName` the name the
 * file starts with.
 */
export function useChartExport(options: {
  rows: () => ExportRow[];
  controllerOf: (key: string) => PlotController | null | undefined;
  range: RangeController;
  measureTimes: Ref<[number | null, number | null]>;
  pixelWidth: () => number;
  fileName: () => string;
}) {
  const { range, measureTimes, pixelWidth } = options;
  const exporting = ref(false);

  // The export covers the range between X1 and X2 when both are set, else the visible range
  const exportDialog = ref<{ min: number; max: number; fromCursors: boolean; startCursor: 1 | 2 | null; width: number } | null>(null);

  // The rows the chart points of a plot give: its drawn values in the visible range, once they are loaded
  function drawnRows(row: ExportRow, min: number, max: number, width: number): number | null {
    const controller = options.controllerOf(row.key);
    const data = controller?.data;
    if (!controller || !data || !controller.isCurrent(min, max, width)) {
      return null;
    }
    // The same points as the export takes
    const [start, end] = indexRange(data.times, min, max);
    let rows = 0;
    for (let i = start; i < end; i++) {
      if (data.raw[i] != null) {
        rows += 1;
      }
    }
    return rows;
  }

  // While the dialog is open the drawn points of a live view grow, so their count is taken again now and then
  const exportTick = ref(0);
  let exportTimer: number | undefined;
  watch(exportDialog, (dialog) => {
    window.clearInterval(exportTimer);
    exportTimer = dialog ? window.setInterval(() => (exportTick.value += 1), 2000) : undefined;
  });
  onUnmounted(() => {
    window.clearInterval(exportTimer);
    exportAbort?.abort();
  });

  const exportPlots = computed(() => {
    void exportTick.value;
    const dialog = exportDialog.value;
    return options.rows().map((row) => ({
      key: row.key,
      label: row.plot.sourceName ? `${row.plot.sourceName}.${row.plot.tagName}` : row.plot.tagName,
      sourceName: row.plot.sourceName,
      tagName: row.plot.tagName,
      dataSourceId: row.plot.dataSourceId,
      tagId: row.plot.dataTagId,
      chartRows: dialog && !dialog.fromCursors ? drawnRows(row, dialog.min, dialog.max, dialog.width) : null
    }));
  });

  function openExport() {
    const [x1, x2] = measureTimes.value;
    const width = pixelWidth();
    exportDialog.value = x1 != null && x2 != null && x1 !== x2
      ? { min: Math.min(x1, x2), max: Math.max(x1, x2), fromCursors: true, startCursor: x1 < x2 ? 1 : 2, width }
      : { min: range.min, max: range.max, fromCursors: false, startCursor: null, width };
  }

  // The export keeps its dialog open while it prepares the file, which shows how far it got; closing it cancels
  const exportProgress = ref<ExportProgress | null>(null);
  let exportAbort: AbortController | null = null;

  function closeExport() {
    exportAbort?.abort();
    exportAbort = null;
    exportProgress.value = null;
    exportDialog.value = null;
  }

  function setPlotProgress(key: string, state: ExportProgress["plots"][string]["state"], rows?: number) {
    const plot = exportProgress.value?.plots[key];
    if (plot) {
      plot.state = state;
      plot.rows = rows ?? plot.rows;
    }
  }

  // Lets the browser draw the progress before work that holds the page, like writing the file
  function nextPaint(): Promise<void> {
    return new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));
  }

  async function runExport(format: ExportFormat, keys: string[], source: ExportSource) {
    const dialog = exportDialog.value;
    if (!dialog || exporting.value) {
      return;
    }
    exporting.value = true;
    const abort = new AbortController();
    exportAbort = abort;
    exportProgress.value = {
      phase: source === "raw" && format === "csv" ? "starting" : "loading",
      startedAt: Date.now(),
      plots: Object.fromEntries(keys.map((key) => [key, { state: "waiting", rows: 0 }]))
    };
    try {
      const { min, max, fromCursors } = dialog;
      const width = pixelWidth();
      const list = options.rows().filter((row) => keys.includes(row.key));
      if (source === "raw" && format === "csv") {
        await downloadRawCsv(list, min, max, abort.signal);
        return;
      }
      let rawTruncated = false;
      // The raw data for Excel: at most the rows of a sheet per plot, or of the one sheet for all of them. The plots
      // load one after the other, so a long range takes the memory of a small server for one plot at a time only
      const raw: (RawValues | null)[] = [];
      if (source === "raw") {
        let left = MAX_SHEET_ROWS;
        for (const row of list) {
          const limit = format === "xlsx-sheets" ? MAX_SHEET_ROWS : left;
          if (limit <= 0) {
            rawTruncated = true;
            raw.push({ dataTagId: row.plot.dataTagId, name: row.plot.tagName, isBool: false, truncated: true, times: [], raw: [] });
            setPlotProgress(row.key, "done", 0);
            continue;
          }
          setPlotProgress(row.key, "loading", 0);
          try {
            const values = await getRawValues(row.plot.dataSourceId, row.plot.dataTagId, min, max, limit, abort.signal, (progress) =>
              setPlotProgress(row.key, "loading", progress.count ?? 0)
            );
            rawTruncated ||= values.truncated;
            left -= values.raw.reduce((count: number, value) => (value == null ? count : count + 1), 0);
            raw.push(values);
            setPlotProgress(row.key, "done", values.raw.length);
          } catch (error) {
            if (abort.signal.aborted) {
              return;
            }
            raw.push(null);
            setPlotProgress(row.key, "failed");
          }
        }
      }
      // The visible range takes the drawn points, loading those of plots out of view without a preview; the range of
      // the cursors may lie outside of the view and loads apart from the plots, which keep what they draw
      const data = source === "raw" ? raw : await Promise.all(list.map(async (row) => {
        setPlotProgress(row.key, "loading");
        try {
          let points: { times: number[] } | null;
          if (fromCursors) {
            points = await getPlotPoints(row.plot.dataSourceId, row.plot.dataTagId, min, max, width, abort.signal, undefined, {
              label: "Loading points for the export"
            });
          } else {
            const controller = options.controllerOf(row.key);
            if (controller && !controller.isCurrent(min, max, width)) {
              await controller.fetch(min, max, width, false, { preview: false });
            }
            points = controller?.isCurrent(min, max, width) ? controller.data : null;
          }
          setPlotProgress(row.key, points ? "done" : "failed", points?.times.length);
          return points as Awaited<ReturnType<typeof getPlotPoints>> | null;
        } catch {
          setPlotProgress(row.key, "failed");
          return null;
        }
      }));
      if (abort.signal.aborted) {
        return;
      }
      if (source === "chart" && !fromCursors && (range.min !== min || range.max !== max)) {
        notify("warning", "Export cancelled", "The time range changed while the points were loading.");
        return;
      }
      const plots = list.map((row, index) => ({
        sourceName: row.plot.sourceName,
        tagName: row.plot.tagName,
        data: data[index] ?? null
      }));
      exportProgress.value!.phase = "writing";
      await nextPaint();
      if (abort.signal.aborted) {
        return;
      }
      const result = format === "csv" ? buildCsv(plots, min, max) : buildXlsx(plots, min, max, format === "xlsx-sheets");
      downloadBlob(result.blob, exportFileName(options.fileName(), min, format === "csv" ? "csv" : "xlsx"));
      const missing = data.filter((points) => points == null).length;
      if (missing > 0) {
        notify("warning", "Export incomplete", `${missing} data tags could not be loaded and are missing.`);
      } else if (result.truncated || rawTruncated) {
        notify(
          "warning",
          "Export truncated",
          `Cut at ${formatInteger(MAX_SHEET_ROWS)} rows, the most an Excel sheet holds. Choose CSV or a shorter range for all values.`
        );
      }
    } finally {
      exporting.value = false;
      // Closes the dialog when the file is saved or the export failed; a cancel closed it already
      if (exportAbort === abort) {
        exportAbort = null;
        exportProgress.value = null;
        exportDialog.value = null;
      }
    }
  }

  // The raw data as CSV comes as a download straight from the server, which writes it while it reads the archive, so
  // neither the server nor the browser holds the file and it has no row limit
  async function downloadRawCsv(list: ExportRow[], min: number, max: number, signal: AbortSignal) {
    const tags = list.map((row) => ({ dataSourceId: row.plot.dataSourceId, tagId: row.plot.dataTagId }));
    const comma = decimalComma();
    const offsets = timeZoneOffsets(min, max);
    const fileName = exportFileName(options.fileName(), min, "csv");
    try {
      await checkRawCsv(tags, min, max, comma, offsets, fileName, signal);
    } catch (error) {
      if (!isAbortError(error)) {
        notify("error", "Export failed", error instanceof Error ? error.message : String(error));
      }
      return;
    }
    if (signal.aborted) {
      return;
    }
    downloadUrl(rawCsvUrl(tags, min, max, comma, offsets, fileName), fileName);
    notify("info", "Download started", "The browser saves the raw data while the server reads it; its downloads show how far it got.");
  }

  return { exporting, exportDialog, exportPlots, exportProgress, openExport, closeExport, runExport };
}
