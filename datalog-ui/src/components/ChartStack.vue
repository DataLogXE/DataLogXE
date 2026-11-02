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
  <div class="chart-stack-root" :class="{ 'is-live-labels-off': !liveLabels, 'is-axis-badges-off': !axisBadges }">
    <ChartToolbar
      :header="header"
      @start-date="range.setStartDate"
      @timespan="range.setTimespan"
      @shift="range.shift"
      @zoom="range.zoom"
    >
      <template #leading>
        <slot name="toolbar-leading" />
      </template>
      <template #tools>
        <ChartMeasureButtons
          :times="measureTimes"
          :live-labels="liveLabels"
          :axis-badges="axisBadges"
          @toggle="toggleCursor"
          @toggle-live="toggleLiveLabels"
          @toggle-axis="toggleAxisBadges"
          @clear="clearCursors"
        />
      </template>
      <ChartExportButton :busy="exporting" @click="openExport" />
    </ChartToolbar>

    <div
      ref="scrollRef"
      class="chart-stack"
      @dblclick="onDblClick"
      @contextmenu="onContextMenu"
      @scroll.passive="updateSpanRow"
      @wheel="onStackWheel"
    >
      <ChartStackPlot
        v-for="row in rows"
        :key="row.key"
        :row-key="row.key"
        :show-span="row.key === spanKey"
        :plot="row.plot"
        :width="plotWidth"
        :height="plotHeightOf(row)"
        :show-x-axis="row.showXAxis"
        :ctx="ctx"
      />
    </div>

    <ChartPlotMenu
      v-if="plotMenu"
      :x="plotMenu.x"
      :y="plotMenu.y"
      :at="plotMenu.time"
      :times="measureTimes"
      :live-labels="liveLabels"
      :axis-badges="axisBadges"
      @toggle="(index) => placeCursor(index, plotMenu?.time)"
      @toggle-live="toggleLiveLabels"
      @toggle-axis="toggleAxisBadges"
      @clear="clearCursors"
      @statistics="openStatistics"
      exportable
      @export="openExport"
      @close="plotMenu = null"
    />
    <ChartStatisticsDialog
      v-if="statistics"
      :data-source-id="statistics.plot.dataSourceId"
      :tag-id="statistics.plot.dataTagId"
      :name="plotTitle(statistics.plot)"
      :x1="statistics.x1"
      :x2="statistics.x2"
      :viewport="statistics.viewport"
      @close="statistics = null"
    />
    <ChartExportDialog
      v-if="exportDialog"
      :plots="exportPlots"
      :min="exportDialog.min"
      :max="exportDialog.max"
      :from-cursors="exportDialog.fromCursors"
      :start-cursor="exportDialog.startCursor"
      :width="exportDialog.width"
      :progress="exportProgress"
      @export="runExport"
      @close="closeExport"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, markRaw, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import type uPlot from "uplot";
import "uplot/dist/uPlot.min.css";
import ChartMeasureButtons from "./ChartMeasureButtons.vue";
import ChartPlotMenu from "./ChartPlotMenu.vue";
import ChartStackPlot from "./ChartStackPlot.vue";
import ChartStatisticsDialog from "./ChartStatisticsDialog.vue";
import ChartExportButton from "./ChartExportButton.vue";
import ChartExportDialog from "./ChartExportDialog.vue";
import ChartToolbar from "./ChartToolbar.vue";
import { HIDDEN_X_AXIS_HEIGHT, PLOT_PAD_TOP, X_AXIS_HEIGHT } from "../chart/bounds";
import { wheelDeltaPx } from "../chart/dragAndZoom";
import { readChartRange, writeChartRange } from "../chart/chartStorage";
import { createFetchQueue } from "../chart/fetchQueue";
import { spanArrowTop } from "../chart/measureCursors";
import { plotAreaOfContextMenu, plotMenuPlace, type PlotMenuPlace } from "../chart/plotMenuPlace";
import { createLiveTimer } from "../chart/liveTimer";
import { devicePixelWidth, type PlotController } from "../chart/plotController";
import type { StackContext } from "../chart/stackContext";
import { useChartExport } from "../chart/useChartExport";
import { useChartRange } from "../chart/useChartRange";
import { useLiveLabels } from "../chart/useLiveLabels";
import { useAxisBadges } from "../chart/useAxisBadges";
import { isExportKey } from "../chart/shortcutKeys";
import { useMeasure } from "../chart/useMeasure";
import { createWheelZoom } from "../chart/wheelZoom";
import type { ChartPlot, PlotPoints } from "../types";

const props = defineProps<{
  plots: ChartPlot[];
  chartName: string;
  /** Changes with every load of the chart, so the plots start fresh. */
  loadId: number;
}>();

const emit = defineEmits<{
  "toggle-tree": [];
}>();

// Two of the three query slots of the server, so other pages still get one
const MAX_PARALLEL_LOADS = 2;
// The fast lane of the server has five slots of its own for the bounded preview reads, so the previews of a
// stack of plots do not wait for each other
const MAX_PARALLEL_PREVIEWS = 5;
const SYNC_KEY = "chart-page";
const PLOT_GAP = 6;
// Where the badge dX starts below the upper edge of a number plot (see measureCursors.ts)
const SPAN_BADGE_OFFSET_PX = 12;
const BOOL_AREA = 64;
const MIN_NUMBER_AREA = 160;
const MAX_NUMBER_AREA = 380;

type Row = { key: string; plot: ChartPlot; showXAxis: boolean };

const scrollRef = ref<HTMLElement | null>(null);
const plotWidth = ref(800);
const availableHeight = ref(600);
// The plot with the arrow and the badge dX: the first one whose upper edge is in view
const spanKey = ref("");
// The context menu of a plot and the statistics dialog it opens
// `time` is where the right click was in the plot: a cursor that is switched on in the menu appears there
const plotMenu = ref<(PlotMenuPlace & { rowKey: string }) | null>(null);
const statistics = ref<{ plot: ChartPlot; x1: number; x2: number; viewport: boolean } | null>(null);

const controllers = new Map<string, PlotController>();
const elements = new Map<string, HTMLElement>();
const keyByElement = new WeakMap<Element, string>();
const visible = new Set<string>();
const cache = new Map<number, { key: string; data: PlotPoints; at: number }>();
const queue = createFetchQueue(MAX_PARALLEL_LOADS);
const previewQueue = createFetchQueue(MAX_PARALLEL_PREVIEWS);

let io: IntersectionObserver | null = null;
let resizeObs: ResizeObserver | null = null;
let live: { stop: () => void } | null = null;
let redrawRaf = 0;

const { range, header } = useChartRange({
  initial: readChartRange(),
  onScale: (min, max) => {
    for (const controller of controllers.values()) {
      controller.setRange(min, max);
    }
  },
  onPending: () => {
    for (const controller of controllers.values()) {
      controller.markPending();
    }
  },
  onIdle: () => {
    writeChartRange(range.min, range.max);
    loadVisible();
  }
});

const { measure, measureTimes, toggleCursor, placeCursor, clearCursors, onMeasureMove, canStartDrag, onKey, onStatisticsKey } = useMeasure(range, redrawAll);
const { liveLabels, toggleLiveLabels, onKey: onLiveLabelsKey } = useLiveLabels();
const { axisBadges, toggleAxisBadges } = useAxisBadges();
const wheelZoom = createWheelZoom(range, () => scrollRef.value?.querySelector<HTMLElement>(".u-over"));

// X axis labels on the first plot, every fourth and the last, as in DataLogXE2
function showXAxis(index: number, count: number): boolean {
  return index === 0 || (index + 1) % 4 === 0 || index === count - 1;
}

const rows = computed<Row[]>(() =>
  props.plots.map((plot, index) => ({
    key: `${props.loadId}:${index}:${plot.dataTagId}`,
    plot,
    showXAxis: showXAxis(index, props.plots.length)
  }))
);

function axisHeight(show: boolean): number {
  return show ? X_AXIS_HEIGHT : HIDDEN_X_AXIS_HEIGHT;
}

// Number plots share the height that bool plots, labels and axes leave, within limits
const numberArea = computed(() => {
  const list = rows.value;
  const numbers = list.filter((row) => !row.plot.isBool).length;
  if (numbers === 0) {
    return MIN_NUMBER_AREA;
  }
  let fixed = 0;
  for (const row of list) {
    fixed += PLOT_GAP + PLOT_PAD_TOP + axisHeight(row.showXAxis);
    if (row.plot.isBool) {
      fixed += BOOL_AREA;
    }
  }
  const area = Math.floor((availableHeight.value - fixed) / numbers);
  return Math.min(MAX_NUMBER_AREA, Math.max(MIN_NUMBER_AREA, area));
});

function plotHeightOf(row: Row): number {
  return (row.plot.isBool ? BOOL_AREA : numberArea.value) + axisHeight(row.showXAxis) + PLOT_PAD_TOP;
}

function pixelWidth(): number {
  return devicePixelWidth(plotWidth.value);
}

// A plot that came into view loads the range, unless its points are drawn or loading already
function loadInView(key: string) {
  const controller = controllers.get(key);
  if (controller && !range.moving) {
    void controller.fetch(range.min, range.max, pixelWidth(), false);
  }
}

// Top to bottom, so the plots in view come first through the queue
function loadVisible() {
  const width = pixelWidth();
  for (const row of rows.value) {
    if (visible.has(row.key)) {
      void controllers.get(row.key)?.fetch(range.min, range.max, width, false);
    }
  }
}

function liveTick() {
  if (range.moving) {
    return;
  }
  const width = pixelWidth();
  for (const row of rows.value) {
    if (visible.has(row.key)) {
      void controllers.get(row.key)?.fetch(range.min, range.max, width, true);
    }
  }
}

function redrawAll() {
  if (redrawRaf) {
    return;
  }
  redrawRaf = window.requestAnimationFrame(() => {
    redrawRaf = 0;
    for (const controller of controllers.values()) {
      controller.redraw();
    }
  });
}

// The badge dX stays in view when the stack is scrolled: the first plot of which the badge would still be seen
function updateSpanRow() {
  const container = scrollRef.value;
  const list = rows.value;
  if (!container || list.length === 0) {
    spanKey.value = list[0]?.key ?? "";
    return;
  }
  const top = container.getBoundingClientRect().top;
  let key = list[list.length - 1].key;
  for (const row of list) {
    const element = elements.get(row.key);
    // The arrow of a bool plot lies higher than that of a number plot, and its badge with it
    const shift = row.plot.isBool ? spanArrowTop(true, BOOL_AREA) - spanArrowTop(false, 0) : 0;
    if (!element || element.getBoundingClientRect().top + SPAN_BADGE_OFFSET_PX + shift >= top) {
      key = row.key;
      break;
    }
  }
  if (key !== spanKey.value) {
    spanKey.value = key;
    redrawAll();
  }
}

// The wheel zooms the time range anywhere in the stack, also over the axes and between the plots, around the
// time under the mouse (the mouse beside the plot area counts as its nearest edge). The plots handle it
// themselves over their own area; with Shift the wheel scrolls the stack.
function onStackWheel(event: WheelEvent) {
  if (event.defaultPrevented) {
    return;
  }
  // Over the scrollbar of the stack the wheel scrolls it, as the browser does by itself
  const stack = scrollRef.value;
  if (stack && event.clientX >= stack.getBoundingClientRect().left + stack.clientLeft + stack.clientWidth) {
    return;
  }
  if (event.shiftKey) {
    event.preventDefault();
    scrollRef.value?.scrollBy({ top: event.deltaY || event.deltaX });
    return;
  }
  if (event.deltaY === 0) {
    return;
  }
  event.preventDefault();
  wheelZoom.add(event);
}

// Beside the stack, over the page (its header, the toolbar and the margins), the wheel scrolls the stack; the
// chart tree, dialogs and the fields of the toolbar keep their own wheel
function onPageWheel(event: WheelEvent) {
  const stack = scrollRef.value;
  const target = event.target;
  if (
    !stack ||
    event.defaultPrevented ||
    !(target instanceof Element) ||
    stack.contains(target) ||
    target.closest(".chart-tree-pane, [aria-modal='true'], input, select, textarea")
  ) {
    return;
  }
  event.preventDefault();
  stack.scrollBy({ top: wheelDeltaPx(event) });
}

const ctx: StackContext = markRaw({
  range,
  queue,
  previewQueue,
  measure,
  syncKey: SYNC_KEY,
  cache,
  canStartDrag,
  onMeasureMove,
  register(key: string, controller: PlotController, element: HTMLElement) {
    controllers.set(key, controller);
    elements.set(key, element);
    keyByElement.set(element, key);
    io?.observe(element);
  },
  unregister(key: string) {
    controllers.delete(key);
    visible.delete(key);
    const element = elements.get(key);
    if (element) {
      io?.unobserve(element);
      elements.delete(key);
    }
  }
});

function plotTitle(plot: ChartPlot): string {
  return plot.sourceName ? `${plot.sourceName}.${plot.tagName}` : plot.tagName;
}

// A right click in the area of a plot opens its menu: the switches of the live labels and of the cursors and the
// statistics between the cursors. The menu remembers the time at the click for the cursors.
function onContextMenu(event: MouseEvent) {
  const item = plotAreaOfContextMenu(event)?.closest(".chart-stack-item");
  const rowKey = item ? keyByElement.get(item) : undefined;
  const plot = rowKey ? controllers.get(rowKey)?.plot : null;
  if (rowKey && plot) {
    event.preventDefault();
    plotMenu.value = { rowKey, ...plotMenuPlace(event, plot) };
  }
}

// The statistics of the plot under the menu between X1 and X2, as they are when the entry is chosen, and over the
// visible range without both cursors
function openStatistics() {
  const rowKey = plotMenu.value?.rowKey;
  const [x1, x2] = measureTimes.value;
  plotMenu.value = null;
  const plot = rows.value.find((row) => row.key === rowKey)?.plot;
  if (!plot) {
    return;
  }
  statistics.value = x1 != null && x2 != null
    ? { plot, x1, x2, viewport: false }
    : { plot, x1: range.min, x2: range.max, viewport: true };
}

// The key S opens the statistics of the plot under the mouse between X1 and X2, or over the visible range
function openStatisticsOfPlot(uplot: uPlot, x1: number, x2: number, viewport: boolean) {
  for (const [rowKey, controller] of controllers) {
    if (controller.plot === uplot) {
      const plot = rows.value.find((row) => row.key === rowKey)?.plot;
      if (plot) {
        statistics.value = { plot, x1, x2, viewport };
      }
      return;
    }
  }
}

// The keys 1, 2 and S act on the plot under the mouse, which makes no sense while the menu covers the plots (the menu
// has the entry for the statistics and the switches of the cursors); the key ^ for the live labels does, the menu shows
// their switch, and the key E opens the export like the entry of the menu
function onWindowKey(event: KeyboardEvent) {
  onLiveLabelsKey(event);
  if (isExportKey(event) && !exporting.value) {
    event.preventDefault();
    openExport();
    return;
  }
  if (!plotMenu.value) {
    onKey(event);
    onStatisticsKey(event, openStatisticsOfPlot);
  }
}

function onDblClick(event: MouseEvent) {
  if (event.target instanceof Element && event.target.closest(".u-over")) {
    emit("toggle-tree");
  }
}

const { exporting, exportDialog, exportPlots, exportProgress, openExport: openExportDialog, closeExport, runExport } = useChartExport({
  rows: () => rows.value,
  controllerOf: (key) => controllers.get(key),
  range,
  measureTimes,
  pixelWidth,
  fileName: () => props.chartName
});

function openExport() {
  plotMenu.value = null;
  openExportDialog();
}

function measureStack() {
  const element = scrollRef.value;
  if (!element) {
    return;
  }
  const style = getComputedStyle(element);
  const padX = parseFloat(style.paddingLeft) + parseFloat(style.paddingRight);
  const padY = parseFloat(style.paddingTop) + parseFloat(style.paddingBottom);
  plotWidth.value = Math.max(element.clientWidth - padX, 120);
  availableHeight.value = Math.max(element.clientHeight - padY, 0);
}

// A new chart: drop the waiting loads of the old plots and the points of tags no longer shown
watch(
  () => props.loadId,
  () => {
    queue.clear();
    previewQueue.clear();
    plotMenu.value = null;
    statistics.value = null;
    const tagIds = new Set(props.plots.map((plot) => plot.dataTagId));
    for (const tagId of cache.keys()) {
      if (!tagIds.has(tagId)) {
        cache.delete(tagId);
      }
    }
  }
);

// The plots change with the chart and with their sizes; the first one shows the badge until the stack is scrolled
watch(
  rows,
  () => void nextTick(updateSpanRow),
  { flush: "post" }
);

let pageElement: HTMLElement | null = null;

onMounted(() => {
  window.addEventListener("keydown", onWindowKey);
  pageElement = scrollRef.value?.closest("main.page") ?? null;
  pageElement?.addEventListener("wheel", onPageWheel, { passive: false });
  measureStack();
  updateSpanRow();
  range.reset(readChartRange());
  for (const controller of controllers.values()) {
    controller.setRange(range.min, range.max);
  }

  io = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        const key = keyByElement.get(entry.target);
        if (!key) {
          continue;
        }
        if (entry.isIntersecting) {
          visible.add(key);
          loadInView(key);
        } else {
          visible.delete(key);
        }
      }
    },
    { root: scrollRef.value, rootMargin: "200px 0px" }
  );
  for (const element of elements.values()) {
    io.observe(element);
  }

  resizeObs = new ResizeObserver(() => {
    measureStack();
    updateSpanRow();
    // Reload the points for the new pixels once the size settled
    const width = pixelWidth();
    for (const controller of controllers.values()) {
      const key = controller.loadedKey;
      if (key && !key.endsWith(`|${width}`)) {
        range.refresh();
        break;
      }
    }
  });
  if (scrollRef.value) {
    resizeObs.observe(scrollRef.value);
  }

  live = createLiveTimer(liveTick);
});

onUnmounted(() => {
  window.removeEventListener("keydown", onWindowKey);
  pageElement?.removeEventListener("wheel", onPageWheel);
  live?.stop();
  io?.disconnect();
  resizeObs?.disconnect();
  queue.clear();
  previewQueue.clear();
  if (redrawRaf) {
    window.cancelAnimationFrame(redrawRaf);
  }
  wheelZoom.dispose();
});
</script>
