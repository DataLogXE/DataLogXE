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
  <aside v-show="!collapsed" ref="panelRef" class="events-filters">
    <div
      class="events-filters-resize"
      :class="{ dragging: resizing }"
      role="separator"
      aria-orientation="vertical"
      aria-label="Width of the filters"
      :aria-valuemin="FILTERS_WIDTH_MIN"
      :aria-valuemax="FILTERS_WIDTH_MAX"
      :aria-valuenow="width"
      tabindex="0"
      data-hint="Drag to change the width (double click: reset)"
      @pointerdown="onResizeDown"
      @pointermove="onResizeMove"
      @pointerup="onResizeEnd"
      @pointercancel="onResizeEnd"
      @dblclick="setWidth(FILTERS_WIDTH_DEFAULT)"
      @keydown="onResizeKey"
    />
    <div class="events-filters-body">
      <div class="events-filters-head">
        <h2 class="events-filters-title">Filters</h2>
        <button
          type="button"
          class="panel-close"
          data-hint="Close"
          aria-label="Close filters"
          @click="emit('close')"
        >
          <svg viewBox="0 0 24 24" aria-hidden="true">
            <path d="M7 7l10 10M17 7L7 17" />
          </svg>
        </button>
      </div>
      <slot />
    </div>
  </aside>
</template>

<script setup lang="ts">
import { ref } from "vue";
import {
  clampFiltersWidth,
  FILTERS_WIDTH_DEFAULT,
  FILTERS_WIDTH_MAX,
  FILTERS_WIDTH_MIN
} from "../alarmCommon";

// The filters panel at the right edge of an alarm page. Its width is kept in the browser under the key of the page;
// the page holds the value (v-model:width), as the layout around the panel takes its width from it.
const props = defineProps<{
  /** The key in the browser that keeps the width of this panel; every page has its own. */
  storageKey: string;
  collapsed: boolean;
  width: number;
}>();

const emit = defineEmits<{
  close: [];
  "update:width": [width: number];
}>();

const FILTERS_WIDTH_STEP = 16;
const panelRef = ref<HTMLElement | null>(null);
const resizing = ref(false);

function setWidth(value: number) {
  const width = clampFiltersWidth(value);
  emit("update:width", width);
  try {
    window.localStorage.setItem(props.storageKey, String(width));
  } catch {
    // Storage can be unavailable in private mode.
  }
}

// The filters end at the right edge of the layout, so the pointer gives the width
function onResizeDown(event: PointerEvent) {
  if (event.button !== 0) {
    return;
  }
  (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
  resizing.value = true;
  event.preventDefault();
}

function onResizeMove(event: PointerEvent) {
  const layout = panelRef.value?.parentElement;
  if (!resizing.value || !layout) {
    return;
  }
  emit("update:width", clampFiltersWidth(layout.getBoundingClientRect().right - event.clientX));
}

function onResizeEnd() {
  if (resizing.value) {
    resizing.value = false;
    setWidth(props.width);
  }
}

function onResizeKey(event: KeyboardEvent) {
  const step = event.key === "ArrowLeft" ? FILTERS_WIDTH_STEP : event.key === "ArrowRight" ? -FILTERS_WIDTH_STEP : 0;
  if (step !== 0) {
    event.preventDefault();
    setWidth(props.width + step);
  }
}
</script>
