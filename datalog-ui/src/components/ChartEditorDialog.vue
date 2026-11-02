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
    <div
      class="chart-overlay"
      role="dialog"
      aria-modal="true"
      aria-labelledby="chart-editor-title"
      @click.self="close"
      @keydown.esc.prevent="close"
    >
      <form class="chart-overlay-card chart-editor" @submit.prevent="save">
        <div class="chart-overlay-top">
          <h2 id="chart-editor-title">{{ chartId == null ? "New chart" : "Edit chart" }}</h2>
        </div>

        <div class="chart-editor-body">
          <label class="field chart-editor-name">
            <span class="hint">Name</span>
            <input ref="nameRef" v-model="name" type="text" maxlength="100" autocomplete="off" spellcheck="false" />
          </label>

          <div class="chart-editor-columns">
            <section class="chart-editor-col" aria-labelledby="chart-editor-tags">
              <h3 id="chart-editor-tags" class="chart-editor-heading">Data tags</h3>
              <ChartTagPicker :used="usedIds" :full="plots.length >= maxPlots" @add="addPlot" />
            </section>

            <section class="chart-editor-col" aria-labelledby="chart-editor-plots">
              <h3 id="chart-editor-plots" class="chart-editor-heading">
                In the chart
                <span class="hint">{{ plots.length }} / {{ maxPlots }}</span>
              </h3>
              <ol class="chart-editor-plots" @dragover="onDragOverList" @drop="onDrop">
                <li
                  v-for="(plot, index) in plots"
                  :key="plot.dataTagId"
                  class="chart-editor-plot"
                  :class="{
                    'is-drop': dropSlot === index,
                    'is-drop-end': dropSlot === index + 1 && index === plots.length - 1,
                    'is-dragged': dragIndex === index
                  }"
                  draggable="true"
                  @dragstart="onDragStart($event, index)"
                  @dragend="onDragEnd"
                  @dragover="onDragOver($event, index)"
                  @drop="onDrop"
                  @dblclick="removePlot(index)"
                >
                  <span class="chart-editor-grip" aria-hidden="true">
                    <svg viewBox="0 0 16 16">
                      <circle cx="6" cy="4" r="1.1" />
                      <circle cx="10" cy="4" r="1.1" />
                      <circle cx="6" cy="8" r="1.1" />
                      <circle cx="10" cy="8" r="1.1" />
                      <circle cx="6" cy="12" r="1.1" />
                      <circle cx="10" cy="12" r="1.1" />
                    </svg>
                  </span>
                  <span class="chart-editor-plot-name" :data-hint="plotTitle(plot)">{{ plotLabel(plot) }}</span>
                  <span v-if="plot.isDeleted" class="chart-tag-flag is-deleted">Deleted</span>
                  <button
                    class="chart-icon-btn"
                    type="button"
                    data-hint="Remove from the chart"
                    :aria-label="`Remove ${plotLabel(plot)}`"
                    @click="removePlot(index)"
                  >
                    <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M4 4l8 8M12 4L4 12" /></svg>
                  </button>
                </li>
                <li v-if="!plots.length && !loading" class="chart-editor-empty hint">
                  Add data tags from the list on the left. A double-click adds or removes a tag.
                </li>
                <li v-if="loading" class="chart-editor-empty hint">Loading …</li>
              </ol>
            </section>
          </div>

          <p v-if="error" class="error-text">{{ error }}</p>
        </div>

        <div class="source-form-actions">
          <button class="btn secondary" type="button" @click="close">Cancel</button>
          <button class="btn" type="submit" :disabled="busy || loading || !name.trim()">Save</button>
        </div>
      </form>
    </div>
  </Teleport>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import ChartTagPicker, { type PickedTag } from "./ChartTagPicker.vue";
import { createChart, getChart, updateChart } from "../api";

type EditorPlot = {
  dataTagId: number;
  sourceName: string;
  tagName: string;
  displayName: string;
  isDeleted: boolean;
};

const props = defineProps<{
  /** Null for a new chart. */
  chartId: number | null;
  /** The folder of a new chart. */
  parentId: number;
  maxPlots: number;
}>();

const emit = defineEmits<{
  saved: [id: number];
  close: [];
}>();

const nameRef = ref<HTMLInputElement | null>(null);
const name = ref(props.chartId == null ? "New chart" : "");
const plots = ref<EditorPlot[]>([]);
const loading = ref(props.chartId != null);
const busy = ref(false);
const error = ref("");
const dragIndex = ref<number | null>(null);
// The place between the rows where the dragged one lands: 0 above the first row, plots.length below the last
const dropSlot = ref<number | null>(null);

const usedIds = computed(() => new Set(plots.value.map((plot) => plot.dataTagId)));

function plotLabel(plot: EditorPlot): string {
  return plot.sourceName ? `${plot.sourceName}.${plot.tagName}` : plot.tagName;
}

function plotTitle(plot: EditorPlot): string {
  const label = plotLabel(plot);
  return plot.displayName && plot.displayName !== plot.tagName ? `${label} (${plot.displayName})` : label;
}

function addPlot(tag: PickedTag) {
  if (usedIds.value.has(tag.dataTagId)) {
    return;
  }
  if (plots.value.length >= props.maxPlots) {
    error.value = `A chart can show at most ${props.maxPlots} data tags.`;
    return;
  }
  error.value = "";
  plots.value = [
    ...plots.value,
    {
      dataTagId: tag.dataTagId,
      sourceName: tag.sourceName,
      tagName: tag.tagName,
      displayName: tag.displayName,
      isDeleted: false
    }
  ];
}

function removePlot(index: number) {
  plots.value = plots.value.filter((_plot, i) => i !== index);
  error.value = "";
}

function movePlot(from: number, to: number) {
  if (to < 0 || to >= plots.value.length || from === to) {
    return;
  }
  const next = [...plots.value];
  const [item] = next.splice(from, 1);
  next.splice(to, 0, item);
  plots.value = next;
}

function onDragStart(event: DragEvent, index: number) {
  dragIndex.value = index;
  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = "move";
    event.dataTransfer.setData("text/plain", String(index));
  }
}

function onDragEnd() {
  dragIndex.value = null;
  dropSlot.value = null;
}

// The upper half of a row drops above it, the lower half below it
function onDragOver(event: DragEvent, index: number) {
  if (dragIndex.value == null) {
    return;
  }
  event.preventDefault();
  const row = event.currentTarget as HTMLElement;
  const box = row.getBoundingClientRect();
  dropSlot.value = event.clientY < box.top + box.height / 2 ? index : index + 1;
}

// Below the last row, in the free space of the list, the dragged row goes last
function onDragOverList(event: DragEvent) {
  if (dragIndex.value == null || event.target !== event.currentTarget) {
    return;
  }
  event.preventDefault();
  dropSlot.value = plots.value.length;
}

function onDrop(event: DragEvent) {
  if (dragIndex.value == null || dropSlot.value == null) {
    return;
  }
  event.preventDefault();
  const from = dragIndex.value;
  const slot = dropSlot.value;
  onDragEnd();
  movePlot(from, slot > from ? slot - 1 : slot);
}

function close() {
  if (!busy.value) {
    emit("close");
  }
}

async function save() {
  const trimmed = name.value.trim();
  if (!trimmed || busy.value || loading.value) {
    return;
  }
  busy.value = true;
  error.value = "";
  try {
    const dataTagIds = plots.value.map((plot) => plot.dataTagId);
    if (props.chartId == null) {
      const created = await createChart({ name: trimmed, type: "Chart", parentId: props.parentId, dataTagIds });
      emit("saved", created.id);
    } else {
      await updateChart(props.chartId, { name: trimmed, dataTagIds });
      emit("saved", props.chartId);
    }
  } catch (err) {
    error.value = err instanceof Error ? err.message : "The chart could not be saved.";
  } finally {
    busy.value = false;
  }
}

onMounted(async () => {
  if (props.chartId == null) {
    nameRef.value?.focus();
    nameRef.value?.select();
    return;
  }
  try {
    const chart = await getChart(props.chartId);
    name.value = chart.name;
    plots.value = chart.plots.map((plot) => ({
      dataTagId: plot.dataTagId,
      sourceName: plot.sourceName,
      tagName: plot.tagName,
      displayName: plot.displayName,
      isDeleted: plot.isDeleted
    }));
  } catch (err) {
    error.value = err instanceof Error ? err.message : "The chart could not be loaded.";
  } finally {
    loading.value = false;
  }
  nameRef.value?.focus();
});
</script>
