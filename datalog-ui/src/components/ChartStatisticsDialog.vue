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
  <div class="overlay chart-dialog-overlay" @click.self="emit('close')">
    <div
      class="overlay-card chart-dialog chart-stats-dialog"
      role="dialog"
      aria-modal="true"
      aria-labelledby="chart-stats-title"
    >
      <div class="chart-stats-head">
        <h2 id="chart-stats-title" class="chart-stats-title">
          Statistics
          <span class="hint chart-export-source">{{ viewport ? "of the visible range" : "between X1 and X2" }}</span>
        </h2>
        <button class="panel-close" type="button" data-hint="Close" aria-label="Close" @click="emit('close')">
          <svg viewBox="0 0 24 24" aria-hidden="true">
            <path d="M7 7l10 10M17 7L7 17" />
          </svg>
        </button>
        <p class="hint chart-stats-tag" :data-hint="name">{{ name }}</p>
      </div>

      <dl class="chart-stats-list">
        <template v-if="viewport">
          <dt>Start</dt>
          <dd>{{ formatTime(x1) }}</dd>
          <dt>End</dt>
          <dd>{{ formatTime(x2) }}</dd>
        </template>
        <template v-else>
          <dt class="is-x1">X1</dt>
          <dd>{{ formatTime(x1) }}</dd>
          <dt class="is-x2">X2</dt>
          <dd>{{ formatTime(x2) }}</dd>
        </template>
        <dt>Duration</dt>
        <dd><SpanValue :seconds="Math.abs(x2 - x1)" /></dd>
      </dl>

      <p v-if="loading" class="hint chart-stats-status">
        <span class="spinner spinner-sm" aria-hidden="true"></span>
        Calculating the statistics from the archive …
      </p>
      <p v-else-if="error" class="error-text">{{ error }}</p>
      <p v-else-if="stats && !hasValues" class="hint chart-stats-status">The archive has no values {{ viewport ? "in the visible range" : "between X1 and X2" }}.</p>
      <dl v-else-if="stats" class="chart-stats-list">
        <dt>Values</dt>
        <dd>{{ formatInteger(stats.count) }}</dd>
        <dt>Minimum</dt>
        <dd>{{ value(stats.min, stats.isBool) }}</dd>
        <dt>Maximum</dt>
        <dd>{{ value(stats.max, stats.isBool) }}</dd>
        <dt>Mean</dt>
        <dd>{{ value(stats.mean, false) }}</dd>
        <dt>Standard deviation</dt>
        <dd>{{ value(stats.stdDev, false) }}</dd>
        <template v-if="stats.isBool">
          <dt>Signal Changes</dt>
          <dd>{{ stats.signalChanges == null ? "–" : formatInteger(stats.signalChanges) }}</dd>
        </template>
        <template v-for="row in timeRows" :key="row.label">
          <dt>{{ row.label }}</dt>
          <dd class="has-share"><SpanValue v-if="row.seconds != null" :seconds="row.seconds" /><template v-else>–</template></dd>
          <dd class="share">{{ share(row.seconds) }}</dd>
        </template>
      </dl>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import { getTagStatistics, isAbortError, type TagStatistics } from "../api";
import { formatDateTimeMs, formatInteger, formatLabelValue, formatNumber } from "../format";
import SpanValue from "./SpanValue.vue";

const props = defineProps<{
  dataSourceId: number;
  tagId: number;
  /** The data tag with its source, as the plots show it. */
  name: string;
  /** The times of the cursors X1 and X2 in unix seconds, X1 may lie right of X2; for the visible range its start and end. */
  x1: number;
  x2: number;
  /** The statistics of the visible range, without cursors. */
  viewport?: boolean;
}>();

const emit = defineEmits<{
  close: [];
}>();

const stats = ref<TagStatistics | null>(null);
const loading = ref(true);
const error = ref("");
const abort = new AbortController();

function formatTime(seconds: number): string {
  return formatDateTimeMs(new Date(seconds * 1000));
}

// A value holds until the next one is stored, so a tag that does not change has times in a range without stored values
const hasValues = computed(() => {
  const result = stats.value;
  return !!result && (result.count > 0 || (result.highSeconds ?? 0) + (result.lowSeconds ?? 0) + (result.invalidSeconds ?? 0) > 0);
});

// The times of the range, each with its share of the duration in a second column
const timeRows = computed(() => {
  const result = stats.value;
  if (!result) {
    return [];
  }
  const rows: { label: string; seconds: number | null }[] = [];
  if (result.isBool) {
    rows.push(
      { label: "High Time", seconds: result.highSeconds },
      { label: "Low Time", seconds: result.lowSeconds },
      { label: "Longest High", seconds: result.longestHighSeconds },
      { label: "Shortest High", seconds: result.shortestHighSeconds },
      { label: "Longest Low", seconds: result.longestLowSeconds },
      { label: "Shortest Low", seconds: result.shortestLowSeconds }
    );
  }
  rows.push({ label: "No Signal Time", seconds: result.invalidSeconds });
  return rows;
});

function share(seconds: number | null): string {
  const duration = Math.abs(props.x2 - props.x1);
  return seconds == null || duration === 0
    ? ""
    : `${formatNumber((seconds / duration) * 100, { minimumFractionDigits: 1, maximumFractionDigits: 1 })} %`;
}

function value(number: number | null, isBool: boolean): string {
  return number == null ? "–" : formatLabelValue(number, isBool);
}

// Esc closes the dialog wherever the focus is, also after a click on its text
function onKeydown(event: KeyboardEvent) {
  if (event.key === "Escape") {
    event.preventDefault();
    emit("close");
  }
}

onMounted(async () => {
  window.addEventListener("keydown", onKeydown);
  try {
    stats.value = await getTagStatistics(
      props.dataSourceId,
      props.tagId,
      Math.min(props.x1, props.x2),
      Math.max(props.x1, props.x2),
      abort.signal
    );
  } catch (cause) {
    if (!isAbortError(cause)) {
      error.value = cause instanceof Error ? cause.message : "The statistics could not be calculated.";
    }
  } finally {
    loading.value = false;
  }
});

// A closed dialog stops the query on the server
onBeforeUnmount(() => {
  window.removeEventListener("keydown", onKeydown);
  abort.abort();
});
</script>
