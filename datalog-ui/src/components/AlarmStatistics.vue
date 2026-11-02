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

<!-- How often the alarm of the selected row entered its state: one bar per hour of 24 hours or per day of 7 days, the
     latest ones or earlier ones with the arrows -->
<template>
  <div class="alarm-stats">
    <p v-if="!countable" class="alarm-stats-note">This entry has no alarm to count.</p>
    <template v-else>
      <div class="alarm-stats-head">
        <p class="alarm-stats-title">
          <!-- The counts are of this one alarm (its condition), not of all alarms in this state -->
          <span>This alarm went <span class="alarm-stats-state">{{ stateLabel }}</span></span>
          <span v-if="total != null">
            <strong>{{ formatInteger(total) }}</strong> {{ total === 1 ? "time" : "times" }} {{ rangeText }}
          </span>
          <span v-else class="alarm-stats-muted">{{ rangeText }}</span>
        </p>
        <div class="alarm-stats-range" role="group" aria-label="Range of the statistics">
          <button
            type="button"
            class="btn secondary btn-sm btn-icon alarm-stats-step"
            :data-hint="`Back ${stepText}`"
            :aria-label="`Back ${stepText}`"
            :disabled="atOldest"
            @click="step(1)"
          >
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M15 6l-6 6 6 6" /></svg>
          </button>
          <button
            type="button"
            class="btn secondary btn-sm btn-icon alarm-stats-step"
            :data-hint="`Forward ${stepText}`"
            :aria-label="`Forward ${stepText}`"
            :disabled="offset === 0"
            @click="step(-1)"
          >
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M9 6l6 6-6 6" /></svg>
          </button>
          <button
            v-for="option in RANGES"
            :key="option.id"
            type="button"
            class="btn secondary btn-sm"
            :class="{ active: range === option.id }"
            :aria-pressed="range === option.id"
            @click="setRange(option.id)"
          >
            {{ option.label }}
          </button>
        </div>
      </div>

      <p v-if="error" class="alarm-stats-note is-error" role="alert">{{ error }}</p>
      <p v-else-if="incompleteText" class="alarm-stats-note" role="status">{{ incompleteText }}</p>

      <div class="alarm-stats-chart" :class="{ loading }" :aria-busy="loading">
        <div class="alarm-stats-plot">
          <div class="alarm-stats-grid" aria-hidden="true">
            <div v-for="tick in ticks" :key="tick" class="alarm-stats-gridline" :style="{ bottom: `${(tick / top) * 100}%` }">
              <span>{{ formatInteger(tick) }}</span>
            </div>
          </div>
          <div class="alarm-stats-bars">
            <div
              v-for="bar in bars"
              :key="bar.start"
              class="alarm-stats-col"
              tabindex="0"
              :data-hint="bar.hint"
              :aria-label="bar.hint"
            >
              <div
                v-if="bar.count > 0"
                class="alarm-stats-bar"
                :style="{ height: `max(2px, ${(bar.count / top) * 100}%)` }"
              />
            </div>
          </div>
          <p v-if="!loading && total === 0" class="alarm-stats-empty">This alarm was not {{ stateLabel }} {{ rangeText }}.</p>
          <p v-else-if="loading && bars.every((bar) => bar.count === 0)" class="alarm-stats-empty">Counting…</p>
        </div>
        <div class="alarm-stats-axis" aria-hidden="true">
          <span v-for="bar in bars" :key="bar.start" class="alarm-stats-tick" :class="{ minor: bar.minor }">
            {{ bar.label }}<small v-if="bar.sublabel">{{ bar.sublabel }}</small>
          </span>
        </div>
      </div>

      <table class="sr-only">
        <caption>This alarm went {{ stateLabel }} {{ rangeText }}</caption>
        <thead><tr><th scope="col">From</th><th scope="col">Count</th></tr></thead>
        <tbody>
          <tr v-for="bar in bars" :key="bar.start">
            <td>{{ formatDateTime(new Date(bar.start)) }}</td>
            <td>{{ bar.count }}</td>
          </tr>
        </tbody>
      </table>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import { getAlarmCounts, isCancelledError } from "../api";
import {
  formatAlarmState,
  formatDateTime,
  formatHourOfDay,
  formatInteger,
  formatWeekdayDay,
  formatWeekdayLines,
  limitStateFromProps
} from "../format";
import type { EventLogItem } from "../types";

const props = defineProps<{
  item: EventLogItem;
}>();

type RangeId = "day" | "week";

const RANGES: { id: RangeId; label: string }[] = [
  { id: "day", label: "24 hours" },
  { id: "week", label: "7 days" }
];
const RANGE_KEY = "alarmStatisticsRange";
const HOUR_MS = 3_600_000;

const range = ref<RangeId>(readRange());
// How many ranges back from the latest one: 0 shows the last 24 hours or 7 days
const offset = ref(0);
const edges = ref<number[]>([]);
const counts = ref<number[]>([]);
const completeFrom = ref<number | null>(null);
// The oldest stored alarm (from the counts); Back stops at the range that holds it
const oldest = ref<number | null>(null);
const loading = ref(false);
const error = ref("");
let abort: AbortController | undefined;

const countable = computed(() => Boolean(props.item.correlationId && props.item.alarmState));
const stateLabel = computed(() => formatAlarmState(props.item.alarmState, limitStateFromProps(props.item.props)));
const atOldest = computed(() => oldest.value != null && edges.value.length > 0 && edges.value[0] <= oldest.value);
const stepText = computed(() => (range.value === "day" ? "24 hours" : "7 days"));
const rangeText = computed(() => {
  if (offset.value === 0 || edges.value.length < 2) {
    return range.value === "day" ? "in the last 24 hours" : "in the last 7 days";
  }

  const first = new Date(edges.value[0]);
  const end = new Date(edges.value[edges.value.length - 1]);
  if (range.value === "day") {
    return `from ${formatWeekdayDay(first)} ${formatHourOfDay(first)} to ${formatWeekdayDay(end)} ${formatHourOfDay(end)}`;
  }
  // The last day is the one before the end edge
  return `from ${formatWeekdayDay(first)} to ${formatWeekdayDay(new Date(edges.value[edges.value.length - 2]))}`;
});
const total = computed(() => (counts.value.length > 0 ? counts.value.reduce((sum, value) => sum + value, 0) : null));

// The top of the scale and its ticks: about three steps of 1, 2 or 5 times a power of ten
const scale = computed(() => {
  const max = Math.max(0, ...counts.value);
  if (max === 0) {
    return { top: 1, ticks: [0] };
  }

  const raw = max / 3;
  const power = 10 ** Math.floor(Math.log10(raw));
  const norm = raw / power;
  const step = Math.max(1, (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10) * power);
  const top = Math.ceil(max / step) * step;
  const ticks: number[] = [];
  for (let tick = 0; tick <= top; tick += step) {
    ticks.push(tick);
  }
  return { top, ticks };
});
const top = computed(() => scale.value.top);
const ticks = computed(() => scale.value.ticks);

const bars = computed(() => {
  const list = edges.value.slice(0, -1).map((start, index) => {
    const end = edges.value[index + 1];
    const startDate = new Date(start);
    const count = counts.value[index] ?? 0;
    const current = end > Date.now();
    const when = range.value === "day"
      ? `${formatWeekdayDay(startDate)}, ${formatHourOfDay(startDate)}–${formatHourOfDay(new Date(end))}`
      : formatWeekdayDay(startDate);
    // A day is labelled on two lines, so seven of them fit beside each other
    const [label, sublabel] = range.value === "day"
      ? [startDate.getHours() % 3 === 0 ? formatHourOfDay(startDate) : "", ""]
      : formatWeekdayLines(startDate);
    return {
      start,
      count,
      label,
      sublabel,
      // Dropped first when the panel is narrow
      minor: range.value === "day" && startDate.getHours() % 6 !== 0,
      hint: `${when}${current ? " (so far)" : ""}: ${formatInteger(count)}`
    };
  });
  return list;
});

const incompleteText = computed(() => {
  if (completeFrom.value == null || edges.value.length === 0 || completeFrom.value <= edges.value[0]) {
    return "";
  }

  return completeFrom.value >= Date.now() - HOUR_MS
    ? "The alarms stored before the update are still being counted; the bars show only the newer ones."
    : `The alarms before ${formatDateTime(new Date(completeFrom.value))} are still being counted; their bars are not complete yet.`;
});

function readRange(): RangeId {
  try {
    return window.localStorage.getItem(RANGE_KEY) === "week" ? "week" : "day";
  } catch {
    return "day";
  }
}

function setRange(id: RangeId) {
  range.value = id;
  offset.value = 0;
  try {
    window.localStorage.setItem(RANGE_KEY, id);
  } catch {
    // Storage can be unavailable in private mode.
  }
}

function step(ranges: number) {
  offset.value = Math.max(0, offset.value + ranges);
}

/**
 * The bucket edges: the hours of the last 24 hours up to the end of the current one, or the local days of the last
 * 7 days up to the end of today. The counts are kept per hour, so a day starts on the full hour of its midnight
 * (the same in every time zone with whole hours). Each offset moves them one range back.
 */
function buildEdges(id: RangeId, now: number, back: number): number[] {
  const floorHour = (ms: number) => Math.floor(ms / HOUR_MS) * HOUR_MS;
  if (id === "day") {
    const last = floorHour(now) + HOUR_MS - back * 24 * HOUR_MS;
    return Array.from({ length: 25 }, (_, index) => last - (24 - index) * HOUR_MS);
  }

  const midnight = new Date(now);
  midnight.setHours(0, 0, 0, 0);
  midnight.setDate(midnight.getDate() - back * 7);
  return Array.from({ length: 8 }, (_, index) => {
    const day = new Date(midnight);
    day.setDate(midnight.getDate() - 6 + index);
    return floorHour(day.getTime());
  });
}

async function load() {
  abort?.abort();
  abort = undefined;
  error.value = "";
  if (!countable.value) {
    edges.value = [];
    counts.value = [];
    return;
  }

  const controller = new AbortController();
  abort = controller;
  const nextEdges = buildEdges(range.value, Date.now(), offset.value);
  // The bars of the range at once, filled when the counts come
  if (edges.value.length !== nextEdges.length) {
    counts.value = [];
  }
  edges.value = nextEdges;
  loading.value = true;
  try {
    const result = await getAlarmCounts({
      correlationId: props.item.correlationId,
      sourceId: props.item.sourceId > 0 ? props.item.sourceId : undefined,
      time: new Date(props.item.time).getTime(),
      state: props.item.alarmState,
      edges: nextEdges
    }, controller.signal);
    if (abort !== controller) {
      return;
    }

    counts.value = result.counts;
    completeFrom.value = result.completeFrom ? new Date(result.completeFrom).getTime() : null;
    oldest.value = result.oldest ? new Date(result.oldest).getTime() : null;
  } catch (err) {
    if (abort !== controller || isCancelledError(err)) {
      return;
    }

    counts.value = [];
    error.value = err instanceof Error ? err.message : "Failed to count the alarm.";
  } finally {
    if (abort === controller) {
      loading.value = false;
    }
  }
}

watch(
  () => [props.item.sourceId, props.item.correlationId, props.item.alarmState, props.item.time, range.value, offset.value],
  () => void load(),
  { immediate: true }
);

onBeforeUnmount(() => {
  abort?.abort();
});
</script>
