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
  <div class="chart-measure">
    <button
      class="btn secondary btn-sm btn-icon chart-measure-btn is-live"
      :class="{ active: liveLabels }"
      type="button"
      :aria-pressed="liveLabels"
      aria-label="Live labels at the crosshair"
      data-hint="Live labels at the crosshair (key ^)"
      @click="emit('toggle-live')"
    >
      <!-- A crosshair with a label at its corner -->
      <svg viewBox="0 0 16 16" aria-hidden="true">
        <path d="M6.5 1.5v3.5M6.5 8.5v3.5M1 7h3.5M8.5 7h3.5" />
        <rect x="9.5" y="9.5" width="5.5" height="4.5" rx="1.2" />
      </svg>
    </button>
    <button
      class="btn secondary btn-sm btn-icon chart-measure-btn is-live"
      :class="{ active: axisBadges }"
      type="button"
      :aria-pressed="axisBadges"
      aria-label="Badges of the crosshair on the axes"
      data-hint="Badges of the crosshair on the axes"
      @click="emit('toggle-axis')"
    >
      <!-- A crosshair with a badge at the top and one at the left, where the axes are -->
      <svg viewBox="0 0 16 16" aria-hidden="true">
        <path d="M10 5.5v9M5.5 10h9" />
        <rect x="7.5" y="0.75" width="5" height="3.5" rx="1" />
        <rect x="0.75" y="7.5" width="3.5" height="5" rx="1" />
      </svg>
    </button>
    <button
      v-for="index in [0, 1] as const"
      :key="index"
      class="btn secondary btn-sm btn-icon chart-measure-btn"
      :class="[`is-x${index + 1}`, { active: times[index] != null }]"
      type="button"
      :aria-pressed="times[index] != null"
      :aria-label="`Measuring cursor X${index + 1}`"
      :data-hint="tooltipOf(index)"
      @click="emit('toggle', index)"
    >
      <!-- A vertical line in the middle, like the cursor in the plot, with an arrowhead at both ends that points inward -->
      <svg viewBox="0 0 16 16" aria-hidden="true">
        <path d="M8 4v8" />
        <path d="M5.5 1.5 8 4l2.5-2.5M5.5 14.5 8 12l2.5 2.5" />
      </svg>
      <span class="chart-measure-index" aria-hidden="true">{{ index + 1 }}</span>
    </button>
    <button
      class="btn secondary btn-sm btn-icon chart-measure-btn is-clear"
      type="button"
      aria-label="Remove both measuring cursors"
      data-hint="Remove both measuring cursors (key X)"
      :disabled="times[0] == null && times[1] == null"
      @click="emit('clear')"
    >
      <svg viewBox="0 0 16 16" aria-hidden="true">
        <path d="M4 4l8 8M12 4l-8 8" />
      </svg>
    </button>
  </div>
</template>

<script setup lang="ts">
const props = defineProps<{
  /** The times of X1 and X2, null for a cursor that is off. */
  times: [number | null, number | null];
  /** Whether the live labels at the crosshair show. */
  liveLabels: boolean;
  /** Whether the badges of the crosshair on the axes show. */
  axisBadges: boolean;
  /** The time of the right click when the buttons are in the context menu: a cursor then moves there. */
  at?: number;
}>();

const emit = defineEmits<{
  toggle: [index: 0 | 1];
  "toggle-live": [];
  "toggle-axis": [];
  clear: [];
}>();

function tooltipOf(index: 0 | 1): string {
  const name = `X${index + 1}`;
  const time = props.times[index];
  if (props.at == null) {
    return `Measuring cursor ${name} (key ${index + 1} over a plot, X removes both)`;
  }
  if (time == null) {
    return `Set ${name} here`;
  }
  return time === props.at ? `Switch ${name} off` : `Move ${name} here`;
}
</script>
