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
  <Teleport to="body">
    <div class="chart-overlay" role="dialog" aria-modal="true" :aria-label="title" @click.self="emit('close')">
      <div class="chart-overlay-card is-plot" :class="{ 'is-live-labels-off': !liveLabels, 'is-axis-badges-off': !axisBadges, 'is-bool': plotState.isBool }">
        <div class="chart-overlay-top">
          <h2>{{ title }}</h2>
          <button class="panel-close" type="button" data-hint="Close" aria-label="Close" @click="emit('close')">
            <svg viewBox="0 0 24 24" aria-hidden="true">
              <path d="M7 7l10 10M17 7L7 17" />
            </svg>
          </button>
        </div>

        <ChartToolbar
          :header="header"
          @start-date="range.setStartDate"
          @timespan="range.setTimespan"
          @shift="range.shift"
          @zoom="range.zoom"
        >
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

        <div ref="hostRef" class="chart-plot-host" @wheel="onPlotWheel" @contextmenu="onContextMenu">
          <ChartPlotInfo
            ref="infoRef"
            :name="title"
            :state="plotState"
            :show-x-axis="true"
          />
          <div ref="wrapRef" class="chart-plot-wrap"></div>
        </div>
      </div>

      <ChartStatisticsDialog
        v-if="statistics"
        :data-source-id="dataSourceId"
        :tag-id="tagId"
        :name="title"
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
  </Teleport>

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
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, shallowRef } from "vue";
import "uplot/dist/uPlot.min.css";
import ChartExportButton from "./ChartExportButton.vue";
import ChartExportDialog from "./ChartExportDialog.vue";
import ChartMeasureButtons from "./ChartMeasureButtons.vue";
import ChartPlotInfo from "./ChartPlotInfo.vue";
import ChartPlotMenu from "./ChartPlotMenu.vue";
import ChartStatisticsDialog from "./ChartStatisticsDialog.vue";
import ChartToolbar from "./ChartToolbar.vue";
import { getRawValueAt } from "../api";
import { initialBounds, Y_AXIS_WIDTH } from "../chart/bounds";
import { infoPlugin } from "../chart/infoPlugin";
import { createLiveTimer } from "../chart/liveTimer";
import { measureCursorsPlugin } from "../chart/measureCursors";
import { plotAreaOfContextMenu, plotMenuPlace, type PlotMenuPlace } from "../chart/plotMenuPlace";
import {
  createPlotController,
  devicePixelWidth,
  plotAreaWidth,
  type PlotController,
  type PlotState
} from "../chart/plotController";
import { useChartExport } from "../chart/useChartExport";
import { useChartRange } from "../chart/useChartRange";
import { useLiveLabels } from "../chart/useLiveLabels";
import { useAxisBadges } from "../chart/useAxisBadges";
import { isExportKey } from "../chart/shortcutKeys";
import { useMeasure } from "../chart/useMeasure";
import { createWheelZoom } from "../chart/wheelZoom";

const props = defineProps<{
  dataSourceId: number;
  tagId: number;
  tagName: string;
  sourceName: string;
}>();

const emit = defineEmits<{
  close: [];
}>();

const LOADING_STATE: PlotState = {
  fetching: true,
  loadingPhase: "",
  error: "",
  isBool: null,
  loaded: false,
  compressionSeconds: 0,
  reductionSeconds: 0,
  viewCount: 0,
  previewSeconds: 0
};

const hostRef = ref<HTMLElement | null>(null);
const wrapRef = ref<HTMLElement | null>(null);
// The root element of the info component, which the plugin moves into the plot
const infoRef = ref<{ $el: HTMLElement } | null>(null);
const controller = shallowRef<PlotController | null>(null);
// The context menu of the plot and the statistics dialog it opens
const plotMenu = ref<PlotMenuPlace | null>(null);
const statistics = ref<{ x1: number; x2: number; viewport: boolean } | null>(null);
const plotState = computed(() => controller.value?.state ?? LOADING_STATE);

const title = computed(() => {
  return props.sourceName ? `${props.sourceName}.${props.tagName}` : props.tagName;
});

const { range, header } = useChartRange({
  initial: initialBounds(),
  onScale: (min, max) => controller.value?.setRange(min, max),
  onPending: () => controller.value?.markPending(),
  onIdle: () => void load(false)
});

const { measure, measureTimes, toggleCursor, placeCursor, clearCursors, onMeasureMove, canStartDrag, onKey: onMeasureKey, onStatisticsKey } = useMeasure(
  range,
  () => controller.value?.redraw(),
  { inModal: true }
);
const { liveLabels, toggleLiveLabels, onKey: onLiveLabelsKey } = useLiveLabels({ inModal: true });
const { axisBadges, toggleAxisBadges } = useAxisBadges();
const wheelZoom = createWheelZoom(range, () => hostRef.value?.querySelector<HTMLElement>(".u-over"));

const { exporting, exportDialog, exportPlots, exportProgress, openExport: openExportDialog, closeExport, runExport } = useChartExport({
  rows: () => [{ key: "plot", plot: { dataSourceId: props.dataSourceId, dataTagId: props.tagId, sourceName: props.sourceName, tagName: props.tagName } }],
  controllerOf: () => controller.value,
  range,
  measureTimes,
  pixelWidth: plotPixelWidth,
  fileName: () => title.value
});

function openExport() {
  plotMenu.value = null;
  openExportDialog();
}

let live: { stop: () => void } | null = null;
let resizeObs: ResizeObserver | null = null;

function plotPixelWidth(): number {
  return devicePixelWidth(wrapRef.value ? plotAreaWidth(wrapRef.value) : 1500 + Y_AXIS_WIDTH);
}

function load(force: boolean) {
  return controller.value?.fetch(range.min, range.max, plotPixelWidth(), force);
}

// A right click in the area of the plot opens its menu: the switches of the live labels and of the cursors and the
// statistics between the cursors. The menu remembers the time at the click for the cursors.
function onContextMenu(event: MouseEvent) {
  const plot = controller.value?.plot;
  if (plot && plotAreaOfContextMenu(event)) {
    event.preventDefault();
    plotMenu.value = plotMenuPlace(event, plot);
  }
}

// The statistics between X1 and X2, as they are when the entry is chosen, and over the visible range without both
function openStatistics() {
  const [x1, x2] = measureTimes.value;
  plotMenu.value = null;
  statistics.value = x1 != null && x2 != null
    ? { x1, x2, viewport: false }
    : { x1: range.min, x2: range.max, viewport: true };
}

function onKey(event: KeyboardEvent) {
  // The export dialog closes itself on Escape while it has the focus (and prevents the key, so the chart stays), else
  // here; the keys of the chart wait, so its range does not move while the dialog is open
  if (event.defaultPrevented) {
    return;
  }
  if (exportDialog.value) {
    if (event.key === "Escape") {
      closeExport();
    }
    return;
  }
  if (event.key === "Escape") {
    // The statistics dialog closes itself, the menu closes first, then the chart
    if (plotMenu.value) {
      plotMenu.value = null;
    } else if (!statistics.value) {
      emit("close");
    }
    return;
  }
  // The keys 1, 2, X and S act on the plots, which makes no sense while the menu covers the plot; the key ^
  // for the live labels does, the menu shows their switch. A dialog of statistics that is open stays as it is.
  // E opens the export like the entry of the menu, but not over a dialog of statistics
  if (isExportKey(event, true) && !statistics.value && !exporting.value) {
    event.preventDefault();
    openExport();
    return;
  }
  if (!plotMenu.value) {
    onMeasureKey(event);
    if (!statistics.value) {
      onStatisticsKey(event, (_plot, x1, x2, viewport) => {
        statistics.value = { x1, x2, viewport };
      });
    }
  }
  onLiveLabelsKey(event);
}

// The wheel zooms the time range anywhere in the chart, also over the axes and the margins; the plot handles it
// itself over its own area
function onPlotWheel(event: WheelEvent) {
  if (event.defaultPrevented || event.deltaY === 0) {
    return;
  }
  event.preventDefault();
  wheelZoom.add(event);
}

onMounted(() => {
  window.addEventListener("keydown", onKey);
  const wrap = wrapRef.value;
  if (!wrap) {
    return;
  }
  range.reset(initialBounds());
  controller.value = createPlotController({
    target: wrap,
    dataSourceId: props.dataSourceId,
    tagId: props.tagId,
    seriesName: props.tagName,
    label: title.value,
    width: () => plotAreaWidth(wrap),
    plotOptions: (isBool) => ({
      hoverLabel: false,
      canStartDrag,
      extraPlugins: [
        infoPlugin(() => infoRef.value?.$el ?? null, () => hostRef.value),
        measureCursorsPlugin(measure, {
          isBool,
          showSpan: () => true,
          getData: () => controller.value?.drawnData ?? null,
          getValueAt: async (time, signal) =>
            (await getRawValueAt(props.dataSourceId, props.tagId, time, signal)).value,
          onMove: onMeasureMove,
          // The view keeps its width and moves smoothly until the time lies in its middle
          onFitSpan: (min, max) => range.applySmooth(min, max),
          onReveal: (time) => {
            const half = (range.max - range.min) / 2;
            range.applySmooth(time - half, time + half);
          }
        })
      ]
    }),
    getRange: () => [range.min, range.max],
    onBounds: range.applyDirect,
    onArchiveStart: range.setArchiveStart
  });
  resizeObs = new ResizeObserver(() => {
    controller.value?.resize();
    // Reload the points for the new pixels once the size settled
    const key = controller.value?.loadedKey;
    if (key && !key.endsWith(`|${plotPixelWidth()}`)) {
      range.refresh();
    }
  });
  resizeObs.observe(wrap);
  void load(false);
  live = createLiveTimer(() => void load(true));
});

onUnmounted(() => {
  window.removeEventListener("keydown", onKey);
  live?.stop();
  wheelZoom.dispose();
  resizeObs?.disconnect();
  controller.value?.destroy();
});
</script>
