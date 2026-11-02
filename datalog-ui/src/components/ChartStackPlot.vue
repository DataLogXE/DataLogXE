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
  <div ref="rootRef" class="chart-stack-item">
    <ChartPlotInfo
      ref="infoRef"
      :name="title"
      :deleted="plot.isDeleted"
      :inactive="!plot.isActive"
      :state="state"
      :show-x-axis="showXAxis"
    />
    <div ref="plotRef" class="chart-stack-plot" :style="{ height: `${height}px` }"></div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from "vue";
import ChartPlotInfo from "./ChartPlotInfo.vue";
import { getRawValueAt } from "../api";
import { infoPlugin } from "../chart/infoPlugin";
import { measureCursorsPlugin } from "../chart/measureCursors";
import { createPlotController, type PlotController, type PlotState } from "../chart/plotController";
import type { StackContext } from "../chart/stackContext";
import type { ChartPlot } from "../types";

const props = defineProps<{
  rowKey: string;
  plot: ChartPlot;
  /** Width with the y axis in CSS pixels. */
  width: number;
  /** Total height with the x axis in CSS pixels. */
  height: number;
  showXAxis: boolean;
  /** The first plot of the chart draws the arrow with dX between the cursors X1 and X2. */
  showSpan: boolean;
  ctx: StackContext;
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

const rootRef = ref<HTMLElement | null>(null);
const plotRef = ref<HTMLElement | null>(null);
// The root element of the info component, which the plugin moves into the plot
const infoRef = ref<{ $el: HTMLElement } | null>(null);
const controller = shallowRef<PlotController | null>(null);
const state = computed(() => controller.value?.state ?? LOADING_STATE);

const title = computed(() => {
  return props.plot.sourceName ? `${props.plot.sourceName}.${props.plot.tagName}` : props.plot.tagName;
});

onMounted(() => {
  const target = plotRef.value;
  const root = rootRef.value;
  if (!target || !root) {
    return;
  }
  const { ctx, plot } = props;
  const ctrl: PlotController = createPlotController({
    target,
    dataSourceId: plot.dataSourceId,
    tagId: plot.dataTagId,
    seriesName: plot.tagName,
    label: title.value,
    isBool: plot.isBool,
    width: () => props.width,
    height: () => props.height,
    plotOptions: () => ({
      showXAxis: props.showXAxis,
      syncKey: ctx.syncKey,
      hoverLabel: false,
      canStartDrag: ctx.canStartDrag,
      extraPlugins: [
        infoPlugin(() => infoRef.value?.$el ?? null, () => rootRef.value),
        measureCursorsPlugin(ctx.measure, {
          isBool: plot.isBool,
          showSpan: () => props.showSpan,
          getData: () => controller.value?.drawnData ?? null,
          getValueAt: async (time, signal) =>
            (await getRawValueAt(plot.dataSourceId, plot.dataTagId, time, signal)).value,
          onMove: ctx.onMeasureMove,
          // The view keeps its width and moves smoothly until the time lies in its middle
          onFitSpan: (min, max) => ctx.range.applySmooth(min, max),
          onReveal: (time) => {
            const half = (ctx.range.max - ctx.range.min) / 2;
            ctx.range.applySmooth(time - half, time + half);
          }
        })
      ]
    }),
    getRange: () => [ctx.range.min, ctx.range.max],
    onBounds: ctx.range.applyDirect,
    queue: ctx.queue,
    previewQueue: ctx.previewQueue,
    onArchiveStart: ctx.range.setArchiveStart,
    onData: (key, data, at) => ctx.cache.set(plot.dataTagId, { key, data, at }),
    initial: ctx.cache.get(plot.dataTagId)
  });
  controller.value = ctrl;
  ctx.register(props.rowKey, ctrl, root);
});

watch(
  () => [props.width, props.height],
  () => controller.value?.resize()
);

onBeforeUnmount(() => {
  props.ctx.unregister(props.rowKey);
  controller.value?.destroy();
});
</script>
