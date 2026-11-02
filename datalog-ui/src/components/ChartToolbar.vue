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
  <div class="chart-toolbar">
    <slot name="leading" />
    <input
      :value="header.startDate"
      :min="header.minDate"
      class="chart-date"
      type="date"
      aria-label="Start date"
      @change="onStartDate"
    />
    <select
      class="chart-timespan"
      :value="header.timespanExact ? String(header.timespanSeconds) : 'custom'"
      aria-label="Time span"
      @change="onTimespan"
    >
      <option v-for="span in spans" :key="span.seconds" :value="String(span.seconds)">
        {{ span.label }}
      </option>
      <option v-if="!header.timespanExact" value="custom" disabled>{{ header.timespanLabel }}</option>
    </select>
    <!-- The navigation and the tools (the cursor buttons) wrap together as one group -->
    <div class="chart-toolbar-group">
      <div class="chart-nav-btns">
        <button class="icon-btn" type="button" data-hint="Back" aria-label="Back" @click="emit('shift', -1)">
          <svg viewBox="0 0 24 24" aria-hidden="true">
            <path d="M15 6l-6 6 6 6" />
          </svg>
        </button>
        <button class="icon-btn" type="button" data-hint="Zoom out" aria-label="Zoom out" @click="emit('zoom', 1)">
          <svg viewBox="0 0 24 24" aria-hidden="true">
            <path d="M5 12h14" />
          </svg>
        </button>
        <button class="icon-btn" type="button" data-hint="Zoom in" aria-label="Zoom in" @click="emit('zoom', -1)">
          <svg viewBox="0 0 24 24" aria-hidden="true">
            <path d="M5 12h14M12 5v14" />
          </svg>
        </button>
        <button class="icon-btn" type="button" data-hint="Forward" aria-label="Forward" @click="emit('shift', 1)">
          <svg viewBox="0 0 24 24" aria-hidden="true">
            <path d="M9 6l6 6-6 6" />
          </svg>
        </button>
      </div>
      <slot name="tools" />
    </div>
    <slot />
  </div>
</template>

<script setup lang="ts">
import { TIME_SPANS } from "../chart/bounds";
import type { RangeHeader } from "../chart/rangeController";

defineProps<{
  header: RangeHeader;
}>();

const emit = defineEmits<{
  "start-date": [value: string];
  timespan: [seconds: number];
  shift: [direction: -1 | 1];
  zoom: [direction: -1 | 1];
}>();

const spans = TIME_SPANS;

function onStartDate(event: Event) {
  emit("start-date", (event.target as HTMLInputElement).value);
}

function onTimespan(event: Event) {
  const select = event.target as HTMLSelectElement;
  const value = Number(select.value);
  // The list gives the focus back after a choice, else it keeps it and takes the keys of the chart (1 picks a span)
  select.blur();
  if (Number.isFinite(value) && value > 0) {
    emit("timespan", value);
  }
}
</script>
