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
  <!-- Moves into the .u-over of the plot as soon as it exists (infoPlugin) -->
  <div class="chart-stack-info" :style="style">
    <div class="chart-stack-info-left">
      <span class="chart-stack-name">{{ name }}</span>
      <span v-if="deleted" class="chart-tag-flag is-deleted" data-hint="The data tag was deleted; its history is still shown.">
        Deleted
      </span>
      <span v-else-if="inactive" class="chart-tag-flag" data-hint="The data tag is inactive and records no new values.">
        Inactive
      </span>
    </div>
    <div class="chart-stack-info-right">
      <span v-if="state.error" class="chart-stack-error error-text" :data-hint="state.error">{{ state.error }}</span>
      <span
        v-if="state.fetching"
        class="badge connected chart-stack-loading"
        :data-hint="previewTitle"
        aria-live="polite"
      >
        <span class="spinner spinner-sm" aria-hidden="true"></span>
        {{ loadingText }}
      </span>
      <span v-else class="chart-value-badge" :class="plotBadgeClass(state)" :data-hint="previewTitle">
        {{ plotBadgeText(state) }}
      </span>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from "vue";
import { plotBadgeClass, plotBadgeText } from "../chart/badge";
import { HIDDEN_X_AXIS_HEIGHT, X_AXIS_HEIGHT, Y_AXIS_WIDTH } from "../chart/bounds";
import { formatDurationMs } from "../format";
import type { PlotState } from "../chart/plotController";

/** The name of a plot at the bottom left and its badge at the bottom right, in the plot area. */
const props = defineProps<{
  name: string;
  deleted?: boolean;
  inactive?: boolean;
  state: PlotState;
  showXAxis: boolean;
}>();

const loadingText = computed(() => {
  const phase = props.state.loadingPhase ? `${props.state.loadingPhase} …` : "Loading …";
  return props.state.previewSeconds > 0 ? `Coarse preview · ${phase}` : phase;
});

const previewTitle = computed(() => {
  const seconds = props.state.previewSeconds;
  if (seconds <= 0) {
    return undefined;
  }
  const level = formatDurationMs(seconds * 1000);
  return props.state.fetching
    ? `Coarse points of the ${level} compression level; the exact points are loading.`
    : `Coarse points of the ${level} compression level; the exact points could not be loaded.`;
});

// Before the plot exists the info lies over where its plot area will be
const style = computed(() => ({
  "--info-left": `${Y_AXIS_WIDTH}px`,
  "--info-bottom": `${props.showXAxis ? X_AXIS_HEIGHT : HIDDEN_X_AXIS_HEIGHT}px`
}));
</script>
