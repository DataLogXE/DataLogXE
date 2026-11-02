<!--
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
-->

<template>
  <div class="overlay chart-dialog-overlay" @click.self="!progress && emit('close')">
    <form
      class="overlay-card chart-dialog chart-export-dialog"
      :class="{ 'is-busy': progress }"
      :aria-busy="progress != null"
      role="dialog"
      aria-modal="true"
      aria-labelledby="chart-export-title"
      @submit.prevent="submit"
      @keydown.esc.prevent="emit('close')"
    >
      <!-- The range in one line below the title; from the cursors, the cursor at each end stands before it in its colour -->
      <header class="chart-export-head">
        <strong id="chart-export-title">Export</strong>
        <p class="chart-export-range">
          <span v-if="startCursor" class="chart-export-cursor" :class="`is-x${startCursor}`">X{{ startCursor }}</span>
          {{ formatDateTimeMs(new Date(min * 1000)) }}
          <span class="chart-export-dash">–</span>
          <span v-if="startCursor" class="chart-export-cursor" :class="`is-x${3 - startCursor}`">X{{ 3 - startCursor }}</span>
          {{ formatDateTimeMs(new Date(max * 1000)) }}
        </p>
        <p class="hint">
          <SpanValue :seconds="max - min" /> · {{ fromCursors ? "between X1 and X2" : "the visible range" }}
        </p>
      </header>

      <!-- Format and data each as a switch of two, the option of the format and the meaning of the data below -->
      <div class="chart-export-choices" :inert="progress != null">
        <div class="chart-export-choice">
          <span id="chart-export-format" class="chart-export-label">Format</span>
          <div class="segmented" role="radiogroup" aria-labelledby="chart-export-format">
            <label class="segmented-option">
              <input v-model="kind" class="sr-only" type="radio" value="csv" />
              CSV
            </label>
            <label class="segmented-option">
              <input v-model="kind" class="sr-only" type="radio" value="xlsx" />
              Excel
            </label>
          </div>
          <label v-if="plots.length > 1" class="chart-export-option" :class="{ 'is-hidden': kind !== 'xlsx' }">
            <input v-model="sheetPerPlot" type="checkbox" :disabled="kind !== 'xlsx'" />
            One sheet per plot
          </label>
        </div>
        <div class="chart-export-choice">
          <span id="chart-export-data" class="chart-export-label">Data</span>
          <div class="segmented" role="radiogroup" aria-labelledby="chart-export-data">
            <label class="segmented-option">
              <input v-model="source" class="sr-only" type="radio" value="chart" />
              Chart points
            </label>
            <label class="segmented-option">
              <input v-model="source" class="sr-only" type="radio" value="raw" />
              Raw data
            </label>
          </div>
          <p class="hint">
            {{ source === "raw" ? "Every raw value in the range." : "The drawn chart points." }}
          </p>
        </div>
      </div>

      <!-- The plots as a table: all of them in the head, then one row per plot with its rows -->
      <div class="chart-export-table" role="group" aria-labelledby="chart-export-plots">
        <label class="chart-export-tr chart-export-th">
          <input
            type="checkbox"
            :disabled="progress != null"
            :checked="selected.size === plots.length"
            :indeterminate="selected.size > 0 && selected.size < plots.length"
            @change="toggleAll"
          />
          <span id="chart-export-plots">{{ selected.size }} of {{ plots.length }} plots</span>
          <span class="chart-export-count">Rows</span>
        </label>
        <div class="chart-export-plots">
          <label
            v-for="plot in plots"
            :key="plot.key"
            class="chart-export-tr"
            :class="{ 'is-cut': cut.has(plot.key), 'is-off': !selected.has(plot.key) }"
          >
            <input type="checkbox" :disabled="progress != null" :checked="selected.has(plot.key)" @change="toggle(plot.key)" />
            <span class="chart-export-plot">
              <span v-if="plot.sourceName" class="chart-export-plot-source">{{ plot.sourceName }} ·</span>
              {{ plot.tagName }}
            </span>
            <span v-if="progress && selected.has(plot.key)" class="chart-export-count" :class="`is-${progress.plots[plot.key]?.state}`">
              <span v-if="progress.plots[plot.key]?.state === 'loading'" class="spinner spinner-sm" aria-hidden="true"></span>
              <svg v-else-if="progress.plots[plot.key]?.state === 'done'" viewBox="0 0 16 16" aria-label="loaded">
                <path d="M3 8.5l3 3 7-7" />
              </svg>
              {{ progressText(plot.key) }}
            </span>
            <span v-else class="chart-export-count">
              <svg v-if="cut.has(plot.key)" viewBox="0 0 16 16" aria-label="cut">
                <circle cx="4" cy="4" r="2" />
                <circle cx="4" cy="12" r="2" />
                <path d="M5.6 5.2L13 12M5.6 10.8L13 4" />
              </svg>
              {{ countText(plot.key) }}
            </span>
          </label>
        </div>
      </div>

      <!-- While the file is prepared: how far it got, the time so far and the time left until the browser saves it -->
      <div v-if="progress" class="chart-export-progress" role="status">
        <div class="chart-export-progress-line">
          <span>{{ progressPhase }}</span>
          <span class="hint">{{ progressTime }}</span>
        </div>
        <div
          class="chart-export-bar"
          :class="{ 'is-indeterminate': progressShare == null }"
          role="progressbar"
          :aria-valuenow="progressShare == null ? undefined : Math.round(progressShare * 100)"
          aria-valuemin="0"
          aria-valuemax="100"
        >
          <span :style="progressShare == null ? undefined : { width: `${progressShare * 100}%` }"></span>
        </div>
      </div>

      <div v-else-if="cut.size > 0" class="chart-export-callout" role="status">
        <svg viewBox="0 0 16 16" aria-hidden="true">
          <path d="M8 1.8L15 14H1z M8 6.5v3.5 M8 12v.01" />
        </svg>
        <span>
          {{ cut.size }} {{ cut.size === 1 ? "plot does" : "plots do" }} not fit completely into an Excel sheet of
          {{ formatInteger(MAX_SHEET_ROWS) }} rows.
        </span>
        <button class="btn secondary btn-sm" type="button" @click="kind = 'csv'">Use CSV</button>
      </div>

      <div class="actions chart-dialog-actions">
        <p class="hint chart-export-estimate">
          <template v-if="estimate.counting">
            <span class="spinner spinner-sm" aria-hidden="true"></span>
            Counting the raw values …
          </template>
          <template v-else-if="estimate.rows == null">Rows unknown</template>
          <template v-else>
            {{ estimate.approx ? "≈ " : "" }}{{ formatInteger(estimate.rows) }} rows · ≈ {{ formatSize(estimate.bytes) }}
          </template>
        </p>
        <button class="btn secondary" type="button" @click="emit('close')">Cancel</button>
        <button ref="submitRef" class="btn chart-export-submit" type="submit" :disabled="selected.size === 0 || progress != null">
          <span v-if="progress" class="spinner spinner-sm" aria-hidden="true"></span>
          <svg v-else viewBox="0 0 16 16" aria-hidden="true">
            <path d="M8 2v8M4.5 6.5L8 10l3.5-3.5M3 13h10" />
          </svg>
          {{ progress ? "Preparing" : "Export" }} {{ kind === "csv" ? "CSV" : "Excel" }}
        </button>
      </div>
    </form>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import SpanValue from "./SpanValue.vue";
import { formatDateTimeMs, formatInteger } from "../format";
import { getRawValueCount, isAbortError } from "../api";
import { MAX_SHEET_ROWS } from "../chart/xlsxExport";

/** CSV, or an Excel workbook with all plots on one sheet or with one sheet per plot. */
export type ExportFormat = "csv" | "xlsx" | "xlsx-sheets";
/** The points the chart draws, or every raw value of the archive. */
export type ExportSource = "chart" | "raw";

/**
 * How far the export got while it prepares the file: starting the download of the raw data CSV, loading the plots,
 * or writing the file in the browser. rows are the rows read of a plot so far, all of them once it is done.
 */
export interface ExportProgress {
  phase: "starting" | "loading" | "writing";
  startedAt: number;
  plots: Record<string, { state: "waiting" | "loading" | "done" | "failed"; rows: number }>;
}

const FORMAT_KEY = "chartExportFormat";
const SOURCE_KEY = "chartExportSource";

const props = defineProps<{
  /** chartRows: the drawn points of the plot in the range, null when they are not loaded. */
  plots: {
    key: string;
    label: string;
    sourceName: string;
    tagName: string;
    dataSourceId: number;
    tagId: number;
    chartRows: number | null;
  }[];
  /** The range of the export in seconds: between X1 and X2 when both are set, else the visible range. */
  min: number;
  max: number;
  fromCursors: boolean;
  /** The cursor at the start of a range of the cursors (the other one is at its end), else null. */
  startCursor: 1 | 2 | null;
  /** The width of the plots in pixels: the chart points of a range are at most 4 per pixel. */
  width: number;
  /** Set while the file is prepared after Export; Cancel and Esc then cancel the export. */
  progress?: ExportProgress | null;
}>();

const emit = defineEmits<{
  export: [format: ExportFormat, keys: string[], source: ExportSource];
  close: [];
}>();

function readFormat(): ExportFormat {
  try {
    const stored = window.localStorage.getItem(FORMAT_KEY);
    return stored === "xlsx" || stored === "xlsx-sheets" ? stored : "csv";
  } catch {
    return "csv";
  }
}

function readSource(): ExportSource {
  try {
    return window.localStorage.getItem(SOURCE_KEY) === "raw" ? "raw" : "chart";
  } catch {
    return "chart";
  }
}

// The format as the switch of CSV and Excel and the option of one sheet per plot
const stored = readFormat();
const kind = ref<"csv" | "xlsx">(stored === "csv" ? "csv" : "xlsx");
const sheetPerPlot = ref(stored === "xlsx-sheets");
const chosenFormat = computed<ExportFormat>(() => (kind.value === "csv" ? "csv" : sheetPerPlot.value ? "xlsx-sheets" : "xlsx"));
// A single plot has no choice of sheets (the option is not shown); the choice stays stored for charts with more plots
const format = computed<ExportFormat>(() => (props.plots.length > 1 || chosenFormat.value === "csv" ? chosenFormat.value : "xlsx"));
const source = ref<ExportSource>(readSource());
const selected = ref(new Set(props.plots.map((plot) => plot.key)));
const submitRef = ref<HTMLButtonElement | null>(null);

function toggle(key: string) {
  const next = new Set(selected.value);
  if (!next.delete(key)) {
    next.add(key);
  }
  selected.value = next;
}

function toggleAll() {
  selected.value = selected.value.size === props.plots.length ? new Set() : new Set(props.plots.map((plot) => plot.key));
}

// The raw values are counted per plot by the counters of the archive when they are needed, and again every few
// seconds while the range reaches into the future and new values still come in
const rawCounts = ref(new Map<string, number | null>());
const live = computed(() => props.max > Date.now() / 1000);
let countController: AbortController | null = null;
let countTimer: number | undefined;

function countRawValues() {
  if (countController) {
    return;
  }
  window.clearTimeout(countTimer);
  countController = new AbortController();
  const signal = countController.signal;
  void Promise.all(props.plots.map((plot) =>
    getRawValueCount(plot.dataSourceId, plot.tagId, props.min, props.max, signal)
      .catch((error) => (isAbortError(error) ? undefined : null))
      .then((count) => {
        if (count !== undefined) {
          rawCounts.value = new Map(rawCounts.value).set(plot.key, count);
        }
      })
  )).then(() => {
    if (signal.aborted) {
      return;
    }
    countController = null;
    if (live.value) {
      countTimer = window.setTimeout(countRawValues, 5000);
    }
  });
}

// The chart points of the cursor range load at export; until then the raw values tell how many there will be
const needsRawCounts = computed(() => source.value === "raw" || props.plots.some((plot) => plot.chartRows == null));
watch(needsRawCounts, (value) => value && countRawValues(), { immediate: true });

// The rows of a plot: its drawn points, the counted raw values, or for the chart points of a range that is not
// drawn the raw values up to 4 per pixel as the server reduces them. undefined while counting, null when unknown;
// approx when the number is an estimate
function rowsOf(plot: (typeof props.plots)[number]): { count: number | null | undefined; approx: boolean } {
  const raw = source.value === "raw";
  if (!raw && plot.chartRows != null) {
    return { count: plot.chartRows, approx: live.value };
  }
  if (!rawCounts.value.has(plot.key)) {
    return { count: undefined, approx: true };
  }
  const counted = rawCounts.value.get(plot.key) ?? null;
  return raw ? { count: counted, approx: live.value } : { count: counted == null ? null : Math.min(counted, 4 * props.width), approx: true };
}

const plotRows = computed(() => new Map(props.plots.map((plot) => [plot.key, rowsOf(plot)])));

function countText(key: string): string {
  const count = plotRows.value.get(key)?.count;
  return count === undefined ? "…" : count === null ? "–" : formatInteger(count);
}

// Only an Excel sheet has a row limit; the CSV of the raw data comes streamed from the server and the chart points are
// at most a few per pixel. The plots the limit cuts: with one sheet per plot each one above it, else those that no
// longer fit into the sheet after the plots before them
const maxRows = computed(() => (format.value === "csv" ? Infinity : MAX_SHEET_ROWS));
const perSheet = computed(() => format.value === "xlsx-sheets");
const cut = computed(() => {
  const result = new Set<string>();
  let before = 0;
  for (const plot of props.plots) {
    const count = plotRows.value.get(plot.key)?.count;
    if (!selected.value.has(plot.key) || count == null) {
      continue;
    }
    if (perSheet.value ? count > maxRows.value : before + count > maxRows.value) {
      result.add(plot.key);
    }
    before += count;
  }
  return result;
});

const estimate = computed(() => {
  const chosen = props.plots.filter((plot) => selected.value.has(plot.key)).map((plot) => plotRows.value.get(plot.key)!);
  if (chosen.some((rows) => rows.count === undefined)) {
    return { counting: true, rows: null, bytes: 0, approx: true };
  }
  if (chosen.some((rows) => rows.count === null)) {
    return { counting: false, rows: null, bytes: 0, approx: true };
  }
  return {
    counting: false,
    rows: chosen.reduce((sum, rows) => sum + (rows.count ?? 0), 0),
    bytes: fileBytes(),
    approx: chosen.some((rows) => rows.approx)
  };
});

// The rows of each chosen plot that the file keeps: all of them in a CSV, in Excel those that fit into its sheet;
// null while its raw values are not counted or the archive does not know them
const keptRows = computed(() => {
  const result = new Map<string, number | null>();
  let left = maxRows.value;
  for (const plot of props.plots) {
    const count = selected.value.has(plot.key) ? plotRows.value.get(plot.key)?.count : 0;
    if (count == null) {
      result.set(plot.key, null);
      continue;
    }
    const kept = perSheet.value ? Math.min(count, maxRows.value) : Math.min(count, left);
    left -= kept;
    result.set(plot.key, kept);
  }
  return result;
});

function fileBytes(): number {
  return props.plots.reduce((bytes, plot) => bytes + (keptRows.value.get(plot.key) ?? 0) * rowBytes(plot), 0);
}

// The bytes of a row in the file, measured on exports of the archive: the names, both times and the value with their
// separators in the CSV, the same as inline strings and cells of the XML in an Excel sheet, which is not compressed
function rowBytes(plot: (typeof props.plots)[number]): number {
  const names = plot.sourceName.length + plot.tagName.length;
  return names + (format.value === "csv" ? 62 : 290);
}

function formatSize(bytes: number): string {
  const units = ["B", "KB", "MB", "GB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1000 && unit < units.length - 1) {
    value /= 1000;
    unit += 1;
  }
  return `${formatInteger(Math.round(value))} ${units[unit]}`;
}

// The progress of the export: the share of the rows loaded so far, by the raw values counted before, and from the
// pace up to now the time left. The chart points count by plots, since they load side by side and quickly
const now = ref(Date.now());
let clockTimer: number | undefined;
watch(() => props.progress != null, (busy) => {
  window.clearInterval(clockTimer);
  now.value = Date.now();
  clockTimer = busy ? window.setInterval(() => (now.value = Date.now()), 500) : undefined;
});

const progressPlots = computed(() => {
  const progress = props.progress;
  return progress ? props.plots.filter((plot) => plot.key in progress.plots) : [];
});

const progressShare = computed<number | null>(() => {
  const progress = props.progress;
  if (!progress || progress.phase !== "loading") {
    return null;
  }
  const plots = progressPlots.value;
  const finished = plots.filter((plot) => isFinished(progress.plots[plot.key].state)).length;
  // By plots when the rows of one are not known, else by the rows; never full before the last plot is loaded
  const share = (() => {
    if (source.value === "chart" || plots.some((plot) => keptRows.value.get(plot.key) == null)) {
      return finished / Math.max(plots.length, 1);
    }
    let total = 0;
    let loaded = 0;
    for (const plot of plots) {
      const kept = keptRows.value.get(plot.key)!;
      const state = progress.plots[plot.key];
      total += kept;
      loaded += isFinished(state.state) ? kept : Math.min(state.rows, kept);
    }
    return total > 0 ? loaded / total : finished / Math.max(plots.length, 1);
  })();
  return finished < plots.length ? Math.min(share, 0.99) : share;
});

function isFinished(state: ExportProgress["plots"][string]["state"]): boolean {
  return state === "done" || state === "failed";
}

const progressPhase = computed(() => {
  const progress = props.progress;
  if (!progress) {
    return "";
  }
  if (progress.phase === "starting") {
    return "Starting the download …";
  }
  if (progress.phase === "writing") {
    return `Writing the ${kind.value === "csv" ? "CSV" : "Excel"} file …`;
  }
  const plots = progressPlots.value;
  const current = plots.findIndex((plot) => !isFinished(progress.plots[plot.key].state));
  const what = source.value === "raw" ? "raw values" : "chart points";
  return source.value === "raw" && current >= 0
    ? `Loading the ${what} of plot ${current + 1} of ${plots.length} …`
    : `Loading the ${what} …`;
});

function formatClock(ms: number): string {
  const seconds = Math.max(0, Math.round(ms / 1000));
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
}

// The time left shows once the pace is known, after a few seconds and some of the rows
const progressTime = computed(() => {
  const progress = props.progress;
  if (!progress) {
    return "";
  }
  const elapsed = now.value - progress.startedAt;
  const share = progressShare.value;
  if (share != null && share > 0.02 && share < 1 && elapsed > 2000) {
    return `${formatClock(elapsed)} · about ${formatClock((elapsed * (1 - share)) / share)} left`;
  }
  return formatClock(elapsed);
});

function progressText(key: string): string {
  const state = props.progress?.plots[key];
  const kept = keptRows.value.get(key);
  if (!state || state.state === "failed") {
    return state ? "failed" : "";
  }
  if (state.state === "done") {
    return formatInteger(source.value === "raw" ? Math.min(state.rows, kept ?? state.rows) : state.rows);
  }
  // While loading the rows read so far, of the rows expected when they are known
  if (state.state === "loading" && source.value === "raw") {
    return kept ? `${formatInteger(Math.min(state.rows, kept))} of ${formatInteger(kept)}` : formatInteger(state.rows);
  }
  return state.state === "waiting" && kept != null ? formatInteger(kept) : "…";
}

function submit() {
  if (selected.value.size === 0 || props.progress) {
    return;
  }
  try {
    window.localStorage.setItem(FORMAT_KEY, chosenFormat.value);
    window.localStorage.setItem(SOURCE_KEY, source.value);
  } catch {
    // Storage can be unavailable in private mode.
  }
  emit("export", format.value, props.plots.map((plot) => plot.key).filter((key) => selected.value.has(key)), source.value);
}

onMounted(() => submitRef.value?.focus());
onBeforeUnmount(() => {
  window.clearInterval(clockTimer);
  countController?.abort();
  window.clearTimeout(countTimer);
});
</script>
