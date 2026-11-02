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
  <div class="overlay chart-dialog-overlay" @click.self="close">
    <form
      class="overlay-card chart-dialog add-to-chart"
      role="dialog"
      aria-modal="true"
      aria-labelledby="add-to-chart-title"
      @submit.prevent="submit"
      @keydown.esc.prevent="close"
    >
      <strong id="add-to-chart-title">Add {{ tagCountLabel }} to a chart</strong>

      <template v-if="result">
        <p class="hint">{{ resultText }}</p>
        <div class="actions chart-dialog-actions">
          <button class="btn secondary" type="button" @click="emit('close')">Close</button>
          <button class="btn" type="button" @click="openChart">Open chart</button>
        </div>
      </template>

      <template v-else>
        <p v-if="loading" class="hint">Loading …</p>
        <template v-else-if="tree">
          <div class="add-to-chart-modes" role="radiogroup" aria-label="Target">
            <label class="checkbox">
              <input v-model="mode" type="radio" value="existing" :disabled="!charts.length" />
              Existing chart
            </label>
            <label class="checkbox">
              <input v-model="mode" type="radio" value="new" />
              New chart
            </label>
          </div>

          <label v-if="mode === 'existing'" class="field">
            <span class="hint">Chart</span>
            <select v-model.number="chartId">
              <option v-for="option in charts" :key="option.item.id" :value="option.item.id" :disabled="option.item.plotCount >= tree.maxPlots">
                {{ option.path }} ({{ option.item.plotCount }} / {{ tree.maxPlots }})
              </option>
            </select>
          </label>

          <template v-else>
            <label class="field">
              <span class="hint">Name</span>
              <input ref="nameRef" v-model="name" type="text" maxlength="100" autocomplete="off" spellcheck="false" />
            </label>
            <label class="field">
              <span class="hint">Folder</span>
              <select v-model.number="parentId">
                <option v-for="option in folders" :key="option.id" :value="option.id">
                  {{ "  ".repeat(option.depth) }}{{ option.label }}
                </option>
              </select>
            </label>
          </template>

          <p v-if="tagIds.length > tree.maxPlots" class="error-text">
            A chart can show at most {{ tree.maxPlots }} data tags. Select fewer data tags.
          </p>
        </template>
        <p v-if="error" class="error-text">{{ error }}</p>

        <div class="actions chart-dialog-actions">
          <button class="btn secondary" type="button" @click="close">Cancel</button>
          <button class="btn" type="submit" :disabled="!canSubmit">Add</button>
        </div>
      </template>
    </form>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from "vue";
import { useRouter } from "vue-router";
import { appendChartPlots, createChart, getCharts } from "../api";
import { buildTree, chartOptions, folderOptions } from "../chart/chartTree";
import type { ChartTree } from "../types";

const props = defineProps<{
  tagIds: number[];
}>();

const emit = defineEmits<{
  close: [];
}>();

const router = useRouter();
const tree = ref<ChartTree | null>(null);
const loading = ref(true);
const busy = ref(false);
const error = ref("");
const mode = ref<"existing" | "new">("existing");
const chartId = ref<number | null>(null);
const name = ref("New chart");
const parentId = ref<number | null>(null);
const nameRef = ref<HTMLInputElement | null>(null);
const result = ref<{ chartId: number; added: number; skipped: number; created: boolean } | null>(null);

const index = computed(() => buildTree(tree.value?.items ?? []));
const charts = computed(() => chartOptions(index.value));
const folders = computed(() => folderOptions(index.value));

const tagCountLabel = computed(() =>
  props.tagIds.length === 1 ? "1 data tag" : `${props.tagIds.length} data tags`
);

const canSubmit = computed(() => {
  if (busy.value || loading.value || !tree.value || props.tagIds.length === 0) {
    return false;
  }
  if (props.tagIds.length > tree.value.maxPlots) {
    return false;
  }
  return mode.value === "existing" ? chartId.value != null : !!name.value.trim() && parentId.value != null;
});

const resultText = computed(() => {
  const value = result.value;
  if (!value) {
    return "";
  }
  if (value.created) {
    return `The new chart shows ${value.added === 1 ? "1 data tag" : `${value.added} data tags`}.`;
  }
  const added = value.added === 1 ? "1 data tag was added" : `${value.added} data tags were added`;
  if (value.skipped === 0) {
    return `${added}.`;
  }
  return `${added}; ${value.skipped} ${value.skipped === 1 ? "was" : "were"} already in the chart or deleted.`;
});

function close() {
  if (!busy.value) {
    emit("close");
  }
}

function openChart() {
  if (result.value) {
    void router.push(`/charts/${result.value.chartId}`);
  }
}

async function submit() {
  if (!canSubmit.value) {
    return;
  }
  busy.value = true;
  error.value = "";
  try {
    if (mode.value === "existing" && chartId.value != null) {
      const response = await appendChartPlots(chartId.value, props.tagIds);
      result.value = { chartId: chartId.value, added: response.added, skipped: response.skipped, created: false };
    } else if (parentId.value != null) {
      const created = await createChart({
        name: name.value.trim(),
        type: "Chart",
        parentId: parentId.value,
        dataTagIds: props.tagIds
      });
      result.value = { chartId: created.id, added: created.plotCount, skipped: 0, created: true };
    }
  } catch (err) {
    error.value = err instanceof Error ? err.message : "The data tags could not be added.";
  } finally {
    busy.value = false;
  }
}

watch(mode, (value) => {
  if (value === "new") {
    void nextTick(() => {
      nameRef.value?.focus();
      nameRef.value?.select();
    });
  }
});

onMounted(async () => {
  try {
    tree.value = await getCharts();
    const max = tree.value.maxPlots;
    chartId.value = charts.value.find((option) => option.item.plotCount < max)?.item.id ?? null;
    parentId.value = folders.value[0]?.id ?? null;
    if (!charts.value.length) {
      mode.value = "new";
    }
  } catch (err) {
    error.value = err instanceof Error ? err.message : "The charts could not be loaded.";
  } finally {
    loading.value = false;
  }
});
</script>
