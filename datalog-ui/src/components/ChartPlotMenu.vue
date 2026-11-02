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
    <div class="tag-context-menu-backdrop" @click="emit('close')" @contextmenu.prevent="emit('close')">
      <div
        ref="menuRef"
        class="chart-plot-menu"
        role="menu"
        tabindex="-1"
        :style="{ left: `${left}px`, top: `${top}px` }"
        @click.stop
        @contextmenu.prevent.stop
        @mouseleave="emit('close')"
        @keydown.esc.prevent.stop="emit('close')"
      >
        <ChartMeasureButtons
          :times="times"
          :at="at"
          :live-labels="liveLabels"
          :axis-badges="axisBadges"
          @toggle="(index) => emit('toggle', index)"
          @toggle-live="emit('toggle-live')"
          @toggle-axis="emit('toggle-axis')"
          @clear="emit('clear')"
        />
        <button
          class="chart-plot-menu-item"
          type="button"
          role="menuitem"
          :disabled="statisticsDisabled"
          @click="emit('statistics')"
        >
          <span>Statistics</span>
          <span v-if="statisticsHint" class="chart-plot-menu-hint">{{ statisticsHint }}</span>
        </button>
        <button v-if="exportable" class="chart-plot-menu-item" type="button" role="menuitem" @click="emit('export')">
          <span>Export …</span>
        </button>
      </div>
    </div>
  </Teleport>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import ChartMeasureButtons from "./ChartMeasureButtons.vue";

const props = defineProps<{
  /** Where the menu opens, in viewport pixels; it moves inside the window when it would not fit. */
  x: number;
  y: number;
  /** The times of X1 and X2, null for a cursor that is off. */
  times: [number | null, number | null];
  /** The time of the right click in the plot, unix seconds. */
  at: number;
  liveLabels: boolean;
  axisBadges: boolean;
  /** Whether the menu offers the export of the chart. */
  exportable?: boolean;
}>();

const emit = defineEmits<{
  toggle: [index: 0 | 1];
  "toggle-live": [];
  "toggle-axis": [];
  clear: [];
  statistics: [];
  export: [];
  close: [];
}>();

const WINDOW_MARGIN = 4;
// The menu opens a little up and left of the mouse, so the mouse starts inside of it: the menu closes as soon as the
// mouse leaves it
const MOUSE_INSET = 6;

const menuRef = ref<HTMLElement | null>(null);
const left = ref(props.x - MOUSE_INSET);
const top = ref(props.y - MOUSE_INSET);

// The statistics are taken between the two cursors, which must not lie at the same time; without both they are taken
// over the visible range, which needs no hint
const bothCursors = computed(() => props.times[0] != null && props.times[1] != null);
const statisticsHint = computed(() => (bothCursors.value && props.times[0] === props.times[1] ? "X1 and X2 are equal" : ""));

// ... and only from the area between them that the plots tint, which needs no hint
const outsideOfArea = computed(() => {
  const [x1, x2] = props.times;
  return x1 != null && x2 != null && (props.at < Math.min(x1, x2) || props.at > Math.max(x1, x2));
});

const statisticsDisabled = computed(() =>
  bothCursors.value && (props.times[0] === props.times[1] || outsideOfArea.value));

onMounted(() => {
  const menu = menuRef.value;
  if (!menu) {
    return;
  }
  const { width, height } = menu.getBoundingClientRect();
  left.value = Math.max(WINDOW_MARGIN, Math.min(props.x - MOUSE_INSET, window.innerWidth - width - WINDOW_MARGIN));
  top.value = Math.max(WINDOW_MARGIN, Math.min(props.y - MOUSE_INSET, window.innerHeight - height - WINDOW_MARGIN));
  // The menu itself takes the focus, so Esc works and no tooltip of a button shows at once
  menu.focus();
});
</script>
